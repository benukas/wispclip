using System.IO;

namespace Wispclip.Services;

public static class Log
{
    private static readonly object Gate = new();
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispclip", "logs");
    public static string LogFile { get; } = Path.Combine(LogDirectory, "wispclip.log");

    public static void Init()
    {
        Directory.CreateDirectory(LogDirectory);
        try
        {
            // keep the log from growing without bound
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 4 * 1024 * 1024)
                File.Delete(LogFile);
        }
        catch { /* best effort */ }
        Write("=== Wispclip started ===");
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        lock (Gate)
        {
            try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
        }
    }

    public static void Write(string tag, string message) => Write($"[{tag}] {message}");
}
