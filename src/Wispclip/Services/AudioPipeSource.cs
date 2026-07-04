using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Wispclip.Services;

/// <summary>
/// Captures a WASAPI stream (system loopback or microphone) and serves the raw float32 PCM
/// to ffmpeg over a named pipe.
///
/// WASAPI loopback only delivers packets while something is actually rendering audio, so the
/// pump pads with silence against a wall clock — otherwise ffmpeg would stall the whole
/// muxer (video included) waiting on the audio input during quiet moments.
/// </summary>
public sealed class AudioPipeSource : IDisposable
{
    public string PipeName { get; } = $"wispclip_{Guid.NewGuid():N}";
    public string PipePath => $@"\\.\pipe\{PipeName}";
    public WaveFormat Format => _capture.WaveFormat;
    public string Label { get; }

    private readonly WasapiCapture _capture;
    private readonly NamedPipeServerStream _server;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private long _queuedBytes;
    private Thread? _pump;
    private volatile bool _running;
    private volatile bool _connected;

    private AudioPipeSource(WasapiCapture capture, string label)
    {
        _capture = capture;
        Label = label;
        _server = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        _capture.DataAvailable += OnDataAvailable;
    }

    public static AudioPipeSource CreateSystemLoopback() =>
        new(new WasapiLoopbackCapture(), "System");

    public static AudioPipeSource CreateMicrophone(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = deviceId != null
            ? enumerator.GetDevice(deviceId)
            : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        return new AudioPipeSource(new WasapiCapture(device), "Microphone");
    }

    public static List<(string Id, string Name)> ListMicrophones()
    {
        var result = new List<(string, string)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                result.Add((d.ID, d.FriendlyName));
        }
        catch (Exception ex)
        {
            Log.Write("audio", $"mic enumeration failed: {ex.Message}");
        }
        return result;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_connected || e.BytesRecorded == 0) return;
        // cap the backlog at ~5s in case ffmpeg stops reading
        if (Interlocked.Read(ref _queuedBytes) > Format.AverageBytesPerSecond * 5L) return;
        var buf = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buf, 0, e.BytesRecorded);
        _queue.Enqueue(buf);
        Interlocked.Add(ref _queuedBytes, buf.Length);
    }

    /// <summary>Start capturing and begin serving ffmpeg once it connects to the pipe.</summary>
    public void Start()
    {
        _running = true;
        _capture.StartRecording();
        _pump = new Thread(Pump) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = $"audio-{Label}" };
        _pump.Start();
    }

    private void Pump()
    {
        try
        {
            var connect = _server.WaitForConnectionAsync();
            if (!connect.Wait(10000))
            {
                Log.Write("audio", $"{Label}: ffmpeg never connected to pipe");
                return;
            }
            _connected = true;

            int bps = Format.AverageBytesPerSecond;
            int block = Format.BlockAlign;
            var sw = Stopwatch.StartNew();
            long written = 0;
            var silence = new byte[Math.Max(block, bps / 50 / block * block)]; // ~20ms

            while (_running)
            {
                bool wroteData = false;
                while (_queue.TryDequeue(out var buf))
                {
                    Interlocked.Add(ref _queuedBytes, -buf.Length);
                    _server.Write(buf, 0, buf.Length);
                    written += buf.Length;
                    wroteData = true;
                }

                long expected = (long)(sw.Elapsed.TotalSeconds * bps);
                long deficit = expected - written;
                if (deficit > bps / 16) // >~60ms behind wall clock → pad silence
                {
                    long pad = deficit - deficit % block;
                    while (pad > 0 && _running)
                    {
                        int n = (int)Math.Min(pad, silence.Length);
                        _server.Write(silence, 0, n);
                        written += n;
                        pad -= n;
                    }
                }

                if (!wroteData) Thread.Sleep(10);
            }
            _server.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AggregateException)
        {
            // ffmpeg closed the pipe (normal on stop)
        }
        catch (Exception ex)
        {
            Log.Write("audio", $"{Label} pump error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _capture.StopRecording(); } catch { }
        _pump?.Join(1500);
        try { _capture.Dispose(); } catch { }
        try { _server.Dispose(); } catch { }
    }
}
