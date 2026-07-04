using System.IO;
using System.Text.Json;
using Wispclip.Models;

namespace Wispclip.Services;

public static class EditProjectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string SidecarPath(string clipPath) => clipPath + ".wispclip.json";

    public static string LegacySidecarPath(string clipPath) => clipPath + ".clipps.json";

    public static bool HasSidecar(string clipPath) =>
        File.Exists(SidecarPath(clipPath)) || File.Exists(LegacySidecarPath(clipPath));

    public static EditProject Load(string clipPath)
    {
        try
        {
            string? path = File.Exists(SidecarPath(clipPath))
                ? SidecarPath(clipPath)
                : File.Exists(LegacySidecarPath(clipPath))
                    ? LegacySidecarPath(clipPath)
                    : null;
            if (path != null)
                return JsonSerializer.Deserialize<EditProject>(File.ReadAllText(path), JsonOpts) ?? new EditProject();
        }
        catch (Exception ex)
        {
            Log.Write("edit", $"failed to load edit project: {ex.Message}");
        }
        return new EditProject();
    }

    public static void Save(string clipPath, EditProject project)
    {
        try
        {
            File.WriteAllText(SidecarPath(clipPath), JsonSerializer.Serialize(project, JsonOpts));
            string legacy = LegacySidecarPath(clipPath);
            if (File.Exists(legacy))
                File.Delete(legacy);
        }
        catch (Exception ex)
        {
            Log.Write("edit", $"failed to save edit project: {ex.Message}");
        }
    }

    public static void DeleteSidecar(string clipPath)
    {
        try
        {
            if (File.Exists(SidecarPath(clipPath)))
                File.Delete(SidecarPath(clipPath));
            if (File.Exists(LegacySidecarPath(clipPath)))
                File.Delete(LegacySidecarPath(clipPath));
        }
        catch { }
    }

    public static void MoveSidecar(string sourceClipPath, string destClipPath)
    {
        foreach (var sidecar in new[] { SidecarPath(sourceClipPath), LegacySidecarPath(sourceClipPath) })
        {
            if (!File.Exists(sidecar))
                continue;

            string destSidecar = sidecar.EndsWith(".clipps.json", StringComparison.OrdinalIgnoreCase)
                ? LegacySidecarPath(destClipPath)
                : SidecarPath(destClipPath);
            try { File.Move(sidecar, destSidecar); } catch { }
        }
    }
}
