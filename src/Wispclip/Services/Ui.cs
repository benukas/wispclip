using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Wispclip.Services;

public static class Ui
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Ask DWM for a dark title bar (Windows 10 20H1+).</summary>
    public static void EnableDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int on = 1;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>
    /// Reads the accent color the user picked in Windows Settings > Personalization > Colors,
    /// so the app's single accent matches the rest of the OS instead of a hardcoded brand color.
    /// Returns null (caller should keep its own default) if the value can't be read.
    /// </summary>
    public static Color? GetSystemAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int raw)
            {
                uint v = unchecked((uint)raw);
                // Stored as ABGR: low byte is red, next green, next blue, high byte alpha.
                byte r = (byte)(v & 0xFF);
                byte g = (byte)((v >> 8) & 0xFF);
                byte b = (byte)((v >> 16) & 0xFF);
                return Color.FromRgb(r, g, b);
            }
        }
        catch { /* fall back to the app default */ }
        return null;
    }

    /// <summary>Mixes a color toward white by <paramref name="amount"/> (0..1). Used for hover states.</summary>
    public static Color Lighten(Color c, double amount)
    {
        byte Mix(byte ch) => (byte)Math.Round(ch + (255 - ch) * amount);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    /// <summary>True if a color reads as visually light, so dark text should sit on top of it.</summary>
    public static bool IsColorLight(Color c)
    {
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance > 0.55;
    }

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}
