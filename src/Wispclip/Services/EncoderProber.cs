using System.IO;
using Wispclip.Models;

namespace Wispclip.Services;

public class EngineInfo
{
    public HashSet<string> Encoders { get; } = new();
    public bool HasDdagrab { get; set; }
    public bool HasGfxCapture { get; set; }
}

public static class EncoderProber
{
    /// <summary>Ask the ffmpeg build what it supports.</summary>
    public static async Task<EngineInfo> QueryAsync(FfmpegPaths ff)
    {
        var info = new EngineInfo();

        var encoders = await ProcessRunner.RunAsync(ff.Ffmpeg, new[] { "-hide_banner", "-encoders" }, 15000);
        foreach (var enc in PipelineBuilder.KnownEncoders)
            if (encoders.StdOut.Contains($" {enc} ")) info.Encoders.Add(enc);

        var filters = await ProcessRunner.RunAsync(ff.Ffmpeg, new[] { "-hide_banner", "-filters" }, 15000);
        info.HasDdagrab = filters.StdOut.Contains(" ddagrab ");
        info.HasGfxCapture = filters.StdOut.Contains(" gfxcapture ");

        Log.Write("probe", $"encoders: {string.Join(", ", info.Encoders)}; " +
            $"ddagrab: {info.HasDdagrab}; gfxcapture: {info.HasGfxCapture}");
        return info;
    }

    /// <summary>
    /// Find the best working pipeline by actually capturing ~1s of the desktop with each
    /// candidate until one produces a valid file. Listing an encoder doesn't mean the
    /// driver/GPU accepts our frames, so we trust nothing but a successful test run.
    /// </summary>
    public static async Task<string?> ProbeAsync(FfmpegPaths ff, AppSettings settings, EngineInfo engine,
        IProgress<string>? progress = null)
    {
        var candidates = PipelineBuilder.Candidates(
            settings.Video.Codec, engine.Encoders, engine.HasDdagrab, engine.HasGfxCapture);
        Log.Write("probe", $"candidates: {string.Join(" | ", candidates)}");

        foreach (var id in candidates)
        {
            progress?.Report($"Testing {PipelineBuilder.Describe(id)}…");
            string tmp = Path.Combine(Path.GetTempPath(), $"wispclip_probe_{Guid.NewGuid():N}.mp4");
            try
            {
                var p = PipelineBuilder.Build(id, settings);
                var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
                args.AddRange(p.InputArgs);
                args.Add("-map"); args.Add(p.VideoMap);
                args.AddRange(p.EncoderArgs);
                args.AddRange(new[] { "-t", "1", "-f", "mp4", tmp });

                var res = await ProcessRunner.RunAsync(ff.Ffmpeg, args, 30000);
                bool ok = res.Success && File.Exists(tmp) && new FileInfo(tmp).Length > 5_000;
                Log.Write("probe", $"{id}: exit={res.ExitCode} ok={ok}" +
                                   (ok ? "" : $" stderr: {Tail(res.StdErr, 400)}"));
                if (ok) return id;
            }
            catch (Exception ex)
            {
                Log.Write("probe", $"{id}: exception {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        return null;
    }

    private static string Tail(string s, int n) =>
        s.Length <= n ? s.Trim() : s[^n..].Trim();
}
