using Microsoft.Win32;
using System.IO;
using System.Reflection;

namespace Wispclip.Services;

/// <summary>
/// Registers the unpackaged desktop app for the current user's Windows sign-in.
/// HKCU Run entries are surfaced by Windows in Settings > Apps > Startup and
/// Task Manager, where the user remains in control of the enabled state.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Wispclip";
    private const string LegacyValueName = "Clipps";

    public static bool IsRegistered
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return (key?.GetValue(ValueName) is string command &&
                        !string.IsNullOrWhiteSpace(command)) ||
                       (key?.GetValue(LegacyValueName) is string legacy &&
                        !string.IsNullOrWhiteSpace(legacy));
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetRegistered(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows startup settings could not be opened.");
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The Wispclip executable path could not be resolved.");
        string command;
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            // Only reachable when launched via the dotnet host (e.g. `dotnet run` /
            // framework-dependent deploys) — a single-file publish's ProcessPath is the
            // packaged exe itself, never "dotnet", so it never takes this branch. Assembly
            // .Location is safe here despite the IL3000 trim warning, which only applies to
            // assemblies embedded in a single-file bundle.
#pragma warning disable IL3000
            string assembly = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("The Wispclip assembly path could not be resolved.");
#pragma warning restore IL3000
            command = $"\"{executable}\" \"{assembly}\" --startup";
        }
        else
        {
            command = $"\"{executable}\" --startup";
        }
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public static bool TrySync(bool enabled, out string? error)
    {
        try
        {
            SetRegistered(enabled);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Write("startup", $"registration failed: {ex}");
            return false;
        }
    }
}
