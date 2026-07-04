using System.IO;
using System.Text.Json;
using Wispclip.Models;

namespace Wispclip.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispclip", "settings.json");

    private static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipps", "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        try
        {
            string? source = File.Exists(SettingsPath)
                ? SettingsPath
                : File.Exists(LegacySettingsPath) ? LegacySettingsPath : null;
            if (source != null)
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source), JsonOpts) ?? new AppSettings();
                if (!string.Equals(source, SettingsPath, StringComparison.OrdinalIgnoreCase))
                    Save();
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings", $"Failed to load settings, using defaults: {ex.Message}");
            Current = new AppSettings();
        }
        Directory.CreateDirectory(Current.OutputDirectory);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOpts));
        Directory.CreateDirectory(Current.OutputDirectory);
        Changed?.Invoke();
    }
}
