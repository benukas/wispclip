using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Wispclip.Models;

namespace Wispclip.Services;

public class ClipLibraryService
{
    private readonly SettingsService _settings;
    private readonly Func<FfmpegPaths?> _ffmpeg;
    private readonly SemaphoreSlim _thumbGate = new(2); // at most 2 ffmpeg thumbnail jobs at once

    public string ThumbnailDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wispclip", "thumbs");

    private static string LegacyThumbnailDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipps", "thumbs");

    public ClipLibraryService(SettingsService settings, Func<FfmpegPaths?> ffmpegResolver)
    {
        _settings = settings;
        _ffmpeg = ffmpegResolver;
        Directory.CreateDirectory(ThumbnailDirectory);
    }

    public List<ClipInfo> Scan()
    {
        var dir = _settings.Current.OutputDirectory;
        if (!Directory.Exists(dir)) return new();

        return Directory.GetFiles(dir, "*.mp4", SearchOption.TopDirectoryOnly)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.CreationTime)
            .Select(f => new ClipInfo
            {
                Path = f.FullName,
                Name = Path.GetFileNameWithoutExtension(f.Name),
                SizeBytes = f.Length,
                CreatedAt = f.CreationTime,
                ThumbnailPath = ExistingThumb(f.FullName, f.LastWriteTimeUtc),
            })
            .ToList();
    }

    /// <summary>Generate (or return the cached) thumbnail for a clip.</summary>
    public async Task<string?> EnsureThumbnailAsync(ClipInfo clip)
    {
        var ff = _ffmpeg();
        if (ff == null || !File.Exists(clip.Path)) return null;

        var mtime = File.GetLastWriteTimeUtc(clip.Path);
        string thumb = ThumbPathFor(clip.Path, mtime);
        if (File.Exists(thumb)) return thumb;

        await _thumbGate.WaitAsync();
        try
        {
            if (File.Exists(thumb)) return thumb;
            foreach (var seek in new[] { "1", "0" }) // -ss 1 fails on sub-second clips
            {
                var res = await ProcessRunner.RunAsync(ff.Ffmpeg, new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-ss", seek, "-i", clip.Path,
                    "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", thumb,
                }, 20000);
                if (res.Success && File.Exists(thumb) && new FileInfo(thumb).Length > 0)
                    return thumb;
                try { if (File.Exists(thumb)) File.Delete(thumb); } catch { }
            }
            return null;
        }
        finally
        {
            _thumbGate.Release();
        }
    }

    public async Task<double?> GetDurationAsync(string path)
    {
        var ff = _ffmpeg();
        if (ff == null) return null;
        var res = await ProcessRunner.RunAsync(ff.Ffprobe, new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", path,
        }, 15000);
        return double.TryParse(res.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    public void Delete(ClipInfo clip)
    {
        File.Delete(clip.Path);
        EditProjectStore.DeleteSidecar(clip.Path);
        if (clip.ThumbnailPath != null)
            try { File.Delete(clip.ThumbnailPath); } catch { }
    }

    public string Rename(ClipInfo clip, string newName)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            newName = newName.Replace(c, '_');
        string dest = Path.Combine(Path.GetDirectoryName(clip.Path)!, newName + ".mp4");
        if (File.Exists(dest)) throw new IOException("A clip with that name already exists.");
        File.Move(clip.Path, dest);

        EditProjectStore.MoveSidecar(clip.Path, dest);
        return dest;
    }

    private string? ExistingThumb(string path, DateTime mtimeUtc)
    {
        string p = ThumbPathFor(path, mtimeUtc);
        if (File.Exists(p))
            return p;

        string legacy = Path.Combine(LegacyThumbnailDirectory, Path.GetFileName(p));
        return File.Exists(legacy) ? legacy : null;
    }

    private string ThumbPathFor(string path, DateTime mtimeUtc)
    {
        var hash = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes($"{path.ToLowerInvariant()}|{mtimeUtc.Ticks}")));
        return Path.Combine(ThumbnailDirectory, hash + ".jpg");
    }
}
