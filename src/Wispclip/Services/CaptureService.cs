using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Wispclip.Models;

namespace Wispclip.Services;

public enum CaptureState { Idle, ReplayBuffer, Recording }

/// <summary>
/// Live encode statistics parsed from ffmpeg's "-progress pipe:1" output. Speed is the
/// realtime factor (1.0 = keeping up exactly); dup/drop counts are cumulative for the run.
/// </summary>
public record CaptureStats(double Fps, double Speed, long DupFrames, long DropFrames);

/// <summary>
/// Owns the single ffmpeg capture process. Two modes, mutually exclusive so the desktop is
/// only ever duplicated once:
///
///   Replay buffer – encodes into a ring of small .ts segments; SaveReplayAsync losslessly
///                   concatenates the newest segments into an mp4 (no re-encode).
///   Recording     – encodes straight into an mp4, stopped gracefully so the index is written.
/// </summary>
public class CaptureService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<FfmpegPaths?> _ffmpeg;

    private Process? _proc;
    private readonly JobObject _job = new();
    private readonly List<AudioPipeSource> _audio = new();
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private System.Threading.Timer? _pruneTimer;
    private string? _bufferDir;
    private string? _recordingPath;
    private bool _stopping;
    private bool _resumeBufferAfterRecording;

    // Desktop-duplication access can be revoked at any time (UAC prompt, lock screen, a
    // game going exclusive-fullscreen, GPU driver reset) and ffmpeg dies when it happens.
    // The buffer is meant to always be protecting the last N seconds, so a crash there is
    // auto-recovered instead of silently leaving the user unprotected. Capped so a genuine,
    // persistent failure doesn't spin forever relaunching ffmpeg.
    private readonly List<DateTime> _recentBufferCrashes = new();
    private const int MaxAutoRestarts = 3;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(2);

    // ddagrab (DXGI Desktop Duplication) can keep losing access under sustained load even
    // outside exclusive-fullscreen games (driver stalls, mode changes, etc.). If it crashes
    // repeatedly with that specific signature, permanently switch to the Windows.Graphics.Capture
    // input instead of endlessly restarting a pipeline that keeps failing. One attempt per
    // process lifetime so a genuinely unsupported gfxcapture build doesn't get retested forever.
    private const int AccessLostFallbackThreshold = 2;
    private bool _gfxFallbackAttempted;

    public CaptureState State { get; private set; } = CaptureState.Idle;
    public DateTime? StartedAt { get; private set; }

    /// <summary>Wall-clock duration of the most recent successful SaveReplayAsync, for diagnostics.</summary>
    public long? LastSaveDurationMs { get; private set; }

    /// <summary>CPU time + working set of the running ffmpeg process, if any (diagnostics).</summary>
    public (TimeSpan Cpu, long WorkingSet)? EncoderProcessUsage
    {
        get
        {
            var p = _proc;
            try
            {
                return p != null && !p.HasExited ? (p.TotalProcessorTime, p.WorkingSet64) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public event Action<CaptureState>? StateChanged;
    public event Action<string>? ClipSaved;
    public event Action<string>? CaptureError;
    public event Action<CaptureStats>? StatsUpdated;

    public CaptureService(SettingsService settings, Func<FfmpegPaths?> ffmpegResolver)
    {
        _settings = settings;
        _ffmpeg = ffmpegResolver;
    }

    private AppSettings S => _settings.Current;

    // ---------------------------------------------------------------- replay buffer

    public void StartReplayBuffer()
    {
        if (State != CaptureState.Idle) return;
        var ff = RequireFfmpeg(); if (ff == null) return;
        var pipeline = RequirePipeline(); if (pipeline == null) return;

        // Each run gets its own subfolder instead of reusing a shared ".buffer" directory.
        // If a previous ffmpeg ever survives as an orphan (e.g. after a desktop-duplication
        // crash) and keeps a segment file locked, that lock now lives in an old, abandoned
        // subfolder instead of blocking every future launch from even starting — and fresh
        // segment numbering never risks colliding with leftovers from a dead session.
        var bufferRoot = Path.Combine(S.OutputDirectory, ".buffer");
        _bufferDir = Path.Combine(bufferRoot, Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(bufferRoot);
            new DirectoryInfo(bufferRoot).Attributes |= FileAttributes.Hidden;
            Directory.CreateDirectory(_bufferDir);
            RemoveStaleSessions(bufferRoot, _bufferDir);
        }
        catch (Exception ex)
        {
            Fail($"Could not prepare buffer directory: {ex.Message}");
            return;
        }

        int seg = Math.Clamp(S.Replay.SegmentSeconds, 1, 10);
        double timeDelta = 1.0 / (2 * Math.Clamp(S.Video.Fps, 10, 240));
        var outputArgs = new List<string>
        {
            "-f", "segment",
            "-segment_format", "mpegts",
            "-segment_time", seg.ToString(CultureInfo.InvariantCulture),
            "-segment_time_delta", timeDelta.ToString("0.########", CultureInfo.InvariantCulture),
            "-reset_timestamps", "1",
            Path.Combine(_bufferDir, "seg_%06d.ts"),
        };

        if (StartFfmpeg(pipeline, outputArgs, forMp4: false))
        {
            State = CaptureState.ReplayBuffer;
            StartedAt = DateTime.Now;
            _pruneTimer = new System.Threading.Timer(_ => PruneSegments(), null, 5000, 5000);
            StateChanged?.Invoke(State);
            Log.Write("capture", $"replay buffer started ({pipeline.Id}, {S.Replay.DurationSeconds}s window)");
        }
    }

    public void StopReplayBuffer()
    {
        if (State != CaptureState.ReplayBuffer) return;
        StopProcess();
        CleanupBufferDir();
        State = CaptureState.Idle;
        StartedAt = null;
        StateChanged?.Invoke(State);
        Log.Write("capture", "replay buffer stopped");
    }

    /// <summary>Losslessly stitch the newest segments into a clip. The buffer keeps running.</summary>
    public async Task<string?> SaveReplayAsync()
    {
        if (State != CaptureState.ReplayBuffer || _bufferDir == null)
        {
            CaptureError?.Invoke("Replay buffer is not running.");
            return null;
        }
        var ff = RequireFfmpeg(); if (ff == null) return null;
        string bufferDir = _bufferDir;

        int seg = Math.Clamp(S.Replay.SegmentSeconds, 1, 10);
        int wanted = Math.Max(5, S.Replay.DurationSeconds);
        string tempDir = Path.Combine(Path.GetTempPath(), $"wispclip_save_{Guid.NewGuid():N}");
        var saveTimer = Stopwatch.StartNew();

        try
        {
            return await Task.Run(async () =>
            {
                var segments = ListSegments(bufferDir);
                if (segments.Count == 0)
                    throw new InvalidOperationException("The replay buffer is warming up. Try again in a few seconds.");

                // Wait for the segment containing the hotkey press to close. A new
                // segment appearing means the prior file has a complete trailer.
                int activeIndex = ParseIndex(segments[^1]);
                var deadline = DateTime.UtcNow.AddSeconds(seg + 2);
                while (DateTime.UtcNow < deadline && State == CaptureState.ReplayBuffer)
                {
                    await Task.Delay(100);
                    segments = ListSegments(bufferDir);
                    if (segments.Count > 0 && ParseIndex(segments[^1]) > activeIndex)
                        break;
                }

                // The newest segment is open in FFmpeg. Copying it mid-write can
                // produce a valid-looking MP4 with a truncated tail that loops or
                // fails on reverse seeks, so only snapshot finalized segments.
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                if (segments.Count == 0)
                    throw new InvalidOperationException("The replay buffer is warming up. Try again in a few seconds.");

                int need = (int)Math.Ceiling(wanted / (double)seg);
                var take = segments.Skip(Math.Max(0, segments.Count - need)).ToList();

                // snapshot the segments so pruning/rollover can't touch what we're reading
                Directory.CreateDirectory(tempDir);
                var copies = new List<string>();
                foreach (var s in take)
                {
                    string dst = Path.Combine(tempDir, Path.GetFileName(s));
                    using (var src = new FileStream(s, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dstFs = File.Create(dst))
                        await src.CopyToAsync(dstFs);
                    copies.Add(dst);
                }

                string listFile = Path.Combine(tempDir, "list.txt");
                File.WriteAllLines(listFile, copies.Select(c => $"file '{c.Replace('\\', '/')}'"));

                string outPath = UniquePath(Path.Combine(S.OutputDirectory,
                    $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4"));

                var args = new List<string>
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-fflags", "+genpts",
                    "-f", "concat", "-safe", "0", "-i", listFile,
                    "-map", "0",
                    "-c", "copy",
                    "-avoid_negative_ts", "make_zero",
                };
                if (await IsHevcAsync(ff, copies[0]))
                    args.AddRange(new[] { "-tag:v", "hvc1" });
                args.AddRange(new[] { "-movflags", "+faststart", outPath });

                var res = await ProcessRunner.RunAsync(ff.Ffmpeg, args, 60000);
                if (!res.Success || !File.Exists(outPath))
                    throw new InvalidOperationException($"Concat failed: {Tail(res.StdErr, 300)}");

                LastSaveDurationMs = saveTimer.ElapsedMilliseconds;
                Log.Write("capture", $"replay saved in {LastSaveDurationMs}ms: {outPath}");
                ClipSaved?.Invoke(outPath);
                return outPath;
            });
        }
        catch (Exception ex)
        {
            Log.Write("capture", $"save replay failed: {ex.Message}");
            CaptureError?.Invoke($"Save replay failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- manual recording

    public void StartRecording()
    {
        if (State == CaptureState.Recording) return;
        if (State == CaptureState.ReplayBuffer)
        {
            _resumeBufferAfterRecording = true;
            StopReplayBuffer();
        }

        var ff = RequireFfmpeg(); if (ff == null) return;
        var pipeline = RequirePipeline(); if (pipeline == null) return;

        _recordingPath = UniquePath(Path.Combine(S.OutputDirectory,
            $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4"));

        var outputArgs = new List<string> { "-movflags", "+faststart", _recordingPath };
        if (StartFfmpeg(pipeline, outputArgs, forMp4: true))
        {
            State = CaptureState.Recording;
            StartedAt = DateTime.Now;
            StateChanged?.Invoke(State);
            Log.Write("capture", $"recording started: {_recordingPath}");
        }
        else if (_resumeBufferAfterRecording)
        {
            _resumeBufferAfterRecording = false;
            StartReplayBuffer();
        }
    }

    public void StopRecording()
    {
        if (State != CaptureState.Recording) return;
        StopProcess();
        State = CaptureState.Idle;
        StartedAt = null;
        StateChanged?.Invoke(State);

        if (_recordingPath != null && File.Exists(_recordingPath))
        {
            Log.Write("capture", $"recording saved: {_recordingPath}");
            ClipSaved?.Invoke(_recordingPath);
        }
        _recordingPath = null;

        if (_resumeBufferAfterRecording)
        {
            _resumeBufferAfterRecording = false;
            StartReplayBuffer();
        }
    }

    // ---------------------------------------------------------------- process management

    private bool StartFfmpeg(VideoPipeline pipeline, List<string> outputArgs, bool forMp4)
    {
        var ff = _ffmpeg()!;
        _stopping = false;
        while (_stderrTail.TryDequeue(out _)) { }
        _statFps = _statSpeed = 0;
        _statDup = _statDrop = 0;

        // audio sources must exist before ffmpeg starts so their formats are known
        // and the pipe servers are listening when ffmpeg opens its inputs
        _audio.Clear();
        if (S.Audio.CaptureSystemAudio)
            TryAddAudio(AudioPipeSource.CreateSystemLoopback);
        if (S.Audio.CaptureMicrophone)
            TryAddAudio(() => AudioPipeSource.CreateMicrophone(S.Audio.MicrophoneDeviceId));

        var inv = CultureInfo.InvariantCulture;
        // -progress on stdout feeds the live diagnostics panel (1s cadence keeps the
        // chatter negligible); -nostats silences the default stderr status line so the
        // error tail stays meaningful.
        var args = new List<string>
            { "-y", "-hide_banner", "-loglevel", "warning", "-nostats", "-stats_period", "1", "-progress", "pipe:1" };
        args.AddRange(pipeline.InputArgs);

        foreach (var a in _audio)
        {
            args.AddRange(new[] { "-thread_queue_size", "2048" });
            if (S.Audio.AudioDelayMs != 0)
                args.AddRange(new[] { "-itsoffset", (S.Audio.AudioDelayMs / 1000.0).ToString("0.###", inv) });
            args.AddRange(new[]
            {
                "-f", "f32le",
                "-ar", a.Format.SampleRate.ToString(inv),
                "-ac", a.Format.Channels.ToString(inv),
                "-i", a.PipePath,
            });
        }

        args.Add("-map"); args.Add(pipeline.VideoMap);
        for (int i = 0; i < _audio.Count; i++)
        {
            args.Add("-map"); args.Add($"{pipeline.AudioStartIndex + i}:a");
        }

        args.AddRange(pipeline.EncoderArgs);
        if (forMp4 && S.Video.Codec == "hevc")
            args.AddRange(new[] { "-tag:v", "hvc1" });

        for (int i = 0; i < _audio.Count; i++)
            args.AddRange(new[] { $"-metadata:s:a:{i}", $"title={_audio[i].Label}" });
        if (_audio.Count > 0)
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });

        args.AddRange(outputArgs);

        var psi = new ProcessStartInfo
        {
            FileName = ff.Ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Log.Write("ffmpeg", "args: " + string.Join(" ", args));

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                _stderrTail.Enqueue(e.Data);
                while (_stderrTail.Count > 200) _stderrTail.TryDequeue(out _);
                Log.Write("ffmpeg", e.Data);
            };
            _proc.OutputDataReceived += OnProgressLine;
            _proc.Exited += OnProcessExited;
            _proc.Start();
            // If Wispclip is ever killed without running its own shutdown (crash, Task
            // Manager, forced kill), the OS force-kills ffmpeg too instead of leaving it
            // running forever and locking the buffer files for the next launch.
            if (!_job.TryAssign(_proc))
                Log.Write("capture", "could not bind ffmpeg to job object; it may survive a forced Wispclip kill");
            try
            {
                // Protect the game from CPU contention if a software fallback is
                // ever required. Hardware encode paths are effectively unaffected.
                _proc.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex)
            {
                Log.Write("capture", $"could not lower ffmpeg process priority: {ex.Message}");
            }
            _proc.BeginErrorReadLine();
            _proc.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            DisposeAudio();
            Fail($"Could not start ffmpeg: {ex.Message}");
            return false;
        }

        foreach (var a in _audio) a.Start();
        return true;
    }

    // fields of the progress block currently being received; ffmpeg emits key=value lines
    // and closes each block with a "progress=continue|end" line
    private double _statFps, _statSpeed;
    private long _statDup, _statDrop;

    private void OnProgressLine(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        int eq = e.Data.IndexOf('=');
        if (eq <= 0) return;
        string key = e.Data[..eq].Trim();
        string val = e.Data[(eq + 1)..].Trim();
        var inv = CultureInfo.InvariantCulture;

        switch (key)
        {
            case "fps":
                double.TryParse(val, NumberStyles.Float, inv, out _statFps);
                break;
            case "speed":
                double.TryParse(val.TrimEnd('x'), NumberStyles.Float, inv, out _statSpeed);
                break;
            case "dup_frames":
                long.TryParse(val, NumberStyles.Integer, inv, out _statDup);
                break;
            case "drop_frames":
                long.TryParse(val, NumberStyles.Integer, inv, out _statDrop);
                break;
            case "progress":
                StatsUpdated?.Invoke(new CaptureStats(_statFps, _statSpeed, _statDup, _statDrop));
                break;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_stopping) return;
        var tail = string.Join("\n", _stderrTail.TakeLast(8));
        Log.Write("capture", $"ffmpeg exited unexpectedly:\n{tail}");
        var died = State;
        State = CaptureState.Idle;
        StartedAt = null;
        DisposeAudio();
        StateChanged?.Invoke(State);
        CaptureError?.Invoke($"Capture stopped unexpectedly ({died}). Last ffmpeg output:\n{tail}");

        if (died == CaptureState.ReplayBuffer)
            TryAutoRestartBuffer(tail);
    }

    /// <summary>
    /// Brings the replay buffer back after an unexpected crash (most commonly a transient
    /// DXGI_ERROR_ACCESS_LOST from desktop duplication). Gives up after a few attempts in a
    /// short window so a genuinely broken pipeline doesn't relaunch ffmpeg in a tight loop.
    /// If the crashes look like repeated desktop-duplication access loss, tries switching to
    /// the Windows.Graphics.Capture input before restarting, since that API tends to survive
    /// mode/state changes that break ddagrab.
    /// </summary>
    private void TryAutoRestartBuffer(string crashTail)
    {
        var now = DateTime.UtcNow;
        _recentBufferCrashes.RemoveAll(t => now - t > CrashWindow);
        _recentBufferCrashes.Add(now);
        if (_recentBufferCrashes.Count > MaxAutoRestarts)
        {
            Log.Write("capture", "replay buffer crashed too many times recently; not auto-restarting");
            return;
        }

        bool looksLikeAccessLost =
            crashTail.Contains("AcquireNextFrame failed", StringComparison.OrdinalIgnoreCase) ||
            crashTail.Contains("ACCESS_LOST", StringComparison.OrdinalIgnoreCase);
        bool tryGfxFallback = looksLikeAccessLost && !_gfxFallbackAttempted &&
            _recentBufferCrashes.Count >= AccessLostFallbackThreshold &&
            (S.Video.PipelineId?.StartsWith("ddagrab", StringComparison.Ordinal) ?? false);

        Log.Write("capture", "replay buffer crashed, attempting automatic restart in 2s");
        _ = RestartAfterCrashAsync(tryGfxFallback);
    }

    private async Task RestartAfterCrashAsync(bool tryGfxFallback)
    {
        await Task.Delay(2000);
        if (State != CaptureState.Idle) return;

        if (tryGfxFallback)
        {
            _gfxFallbackAttempted = true;
            await TrySwitchToGfxCaptureAsync();
        }

        if (State == CaptureState.Idle) StartReplayBuffer();
    }

    /// <summary>
    /// Swaps the ddagrab input for its Windows.Graphics.Capture equivalent (same encoder),
    /// validated with a real 1s test capture, and persists it if it works. Falls through
    /// silently to a normal same-pipeline restart if gfxcapture isn't available/working either.
    /// </summary>
    private async Task TrySwitchToGfxCaptureAsync()
    {
        var ff = _ffmpeg();
        string? currentId = S.Video.PipelineId;
        if (ff == null || currentId == null) return;

        var parts = currentId.Split('+');
        if (parts.Length != 2 || !parts[0].StartsWith("ddagrab", StringComparison.Ordinal)) return;
        string suffix = parts[0]["ddagrab".Length..]; // "", "-cpu", or "-qsv"
        string candidateId = $"gfxcapture{suffix}+{parts[1]}";

        Log.Write("capture", $"desktop duplication keeps losing access; testing fallback pipeline '{candidateId}'");
        bool ok = await EncoderProber.TestPipelineAsync(ff, S, candidateId);
        if (ok)
        {
            S.Video.PipelineId = candidateId;
            _settings.Save();
            Log.Write("capture", $"switched capture pipeline to '{candidateId}' after repeated desktop-duplication crashes");
            CaptureError?.Invoke(
                $"Switched capture method to Windows Graphics Capture after repeated crashes ({PipelineBuilder.Describe(candidateId)}).");
        }
        else
        {
            Log.Write("capture", $"fallback pipeline '{candidateId}' failed validation; keeping '{currentId}'");
        }
    }

    /// <summary>Best-effort cleanup of leftover per-run buffer folders from earlier sessions.
    /// A folder still held by an orphaned ffmpeg is skipped rather than failing startup.</summary>
    private static void RemoveStaleSessions(string bufferRoot, string currentDir)
    {
        foreach (var dir in Directory.GetDirectories(bufferRoot))
        {
            if (string.Equals(dir, currentDir, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { Log.Write("capture", $"could not remove stale buffer session '{dir}': {ex.Message}"); }
        }
        // Leftovers from before per-session subfolders existed; harmless if a couple can't be removed.
        foreach (var f in Directory.GetFiles(bufferRoot, "seg_*.ts"))
        {
            try { File.Delete(f); } catch { }
        }
    }

    private void StopProcess()
    {
        _stopping = true;
        _pruneTimer?.Dispose();
        _pruneTimer = null;

        var proc = _proc;
        _proc = null;
        if (proc != null)
        {
            try
            {
                proc.Exited -= OnProcessExited;
                if (!proc.HasExited)
                {
                    // 'q' asks ffmpeg to finalize the output (writes the mp4 index)
                    try { proc.StandardInput.Write('q'); proc.StandardInput.Flush(); } catch { }
                    if (!proc.WaitForExit(8000))
                    {
                        Log.Write("capture", "ffmpeg did not exit on 'q', killing");
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("capture", $"stop error: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }
        DisposeAudio();
    }

    // ---------------------------------------------------------------- helpers

    private void TryAddAudio(Func<AudioPipeSource> factory)
    {
        try { _audio.Add(factory()); }
        catch (Exception ex)
        {
            Log.Write("audio", $"audio source unavailable, continuing without it: {ex.Message}");
        }
    }

    private void PruneSegments()
    {
        var dir = _bufferDir;
        if (dir == null || !Directory.Exists(dir)) return;
        try
        {
            int seg = Math.Clamp(S.Replay.SegmentSeconds, 1, 10);
            int keep = Math.Max(5, S.Replay.DurationSeconds) / seg + 3;
            var files = Directory.GetFiles(dir, "seg_*.ts")
                .Select(p => (Path: p, Index: ParseIndex(p)))
                .Where(t => t.Index >= 0)
                .OrderByDescending(t => t.Index)
                .Skip(keep);
            foreach (var f in files)
            {
                try { File.Delete(f.Path); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Write("capture", $"prune error: {ex.Message}");
        }
    }

    private void CleanupBufferDir()
    {
        var dir = _bufferDir;
        _bufferDir = null;
        if (dir == null) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private FfmpegPaths? RequireFfmpeg()
    {
        var ff = _ffmpeg();
        if (ff == null)
            Fail("ffmpeg was not found. Download it from https://ffmpeg.org/download.html, then add it to PATH, drop it in tools\\ next to Wispclip.exe, or set its folder in Settings > Capture engine.");
        return ff;
    }

    private VideoPipeline? RequirePipeline()
    {
        var id = S.Video.PipelineId;
        if (string.IsNullOrEmpty(id))
        {
            Fail("No capture pipeline detected yet. Run detection in Settings → Engine.");
            return null;
        }
        try { return PipelineBuilder.Build(id, S); }
        catch (Exception ex)
        {
            Fail($"Bad pipeline '{id}': {ex.Message}");
            return null;
        }
    }

    private void Fail(string message)
    {
        Log.Write("capture", message);
        CaptureError?.Invoke(message);
    }

    private void DisposeAudio()
    {
        foreach (var a in _audio) a.Dispose();
        _audio.Clear();
    }

    private static int ParseIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path); // seg_000042
        return int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : -1;
    }

    private static List<string> ListSegments(string directory) =>
        Directory.GetFiles(directory, "seg_*.ts")
            .Select(p => (Path: p, Index: ParseIndex(p)))
            .Where(t => t.Index >= 0)
            .OrderBy(t => t.Index)
            .Select(t => t.Path)
            .ToList();

    private static async Task<bool> IsHevcAsync(FfmpegPaths ff, string file)
    {
        var res = await ProcessRunner.RunAsync(ff.Ffprobe, new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=codec_name",
            "-of", "default=noprint_wrappers=1:nokey=1", file,
        }, 15000);
        return res.StdOut.Trim().Contains("hevc");
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private static string Tail(string s, int n) => s.Length <= n ? s.Trim() : s[^n..].Trim();

    public void Dispose()
    {
        StopProcess();
        CleanupBufferDir();
        _job.Dispose();
    }
}
