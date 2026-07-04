using System.IO;

namespace Wispclip.Services;

public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

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
            try
            {
                // One persistent append stream instead of an open/write/close cycle per
                // line — log calls land on capture/audio callback threads, so each write
                // must stay as close to a single syscall as possible. FileShare.Read keeps
                // "Open log" in Settings working while the app runs.
                _writer ??= new StreamWriter(
                    new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                    { AutoFlush = true };
                _writer.WriteLine(line);
            }
            catch { }
        }
    }

    public static void Write(string tag, string message) => Write($"[{tag}] {message}");
}
