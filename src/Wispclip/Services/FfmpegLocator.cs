using System.IO;

namespace Wispclip.Services;

public record FfmpegPaths(string Ffmpeg, string Ffprobe)
{
    public string Directory => Path.GetDirectoryName(Ffmpeg)!;
}

public static class FfmpegLocator
{
    /// <summary>
    /// Search order: explicit setting → app directory → app\tools → tools\ in parent dirs
    /// (covers running from the repo during development) → PATH.
    /// </summary>
    public static FfmpegPaths? Locate(string? overrideDir)
    {
        foreach (var dir in CandidateDirs(overrideDir))
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
            var ffprobe = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                return new FfmpegPaths(ffmpeg, ffprobe);
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
            yield return overrideDir;

        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "tools");

        // walk up from bin\Debug\net8.0-windows to the repo root looking for tools\
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "tools");

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return p;
    }
}
