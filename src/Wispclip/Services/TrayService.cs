using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Wispclip.Services;

/// <summary>Tray icon so the app can live in the background while gaming.</summary>
public class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _recordItem;

    public Icon AppIcon { get; }

    public TrayService(Action openApp, Action saveReplay, Action toggleRecording, Action exit)
    {
        AppIcon = CreateIcon();

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open Wispclip", null, (_, _) => openApp());
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        var saveItem = new ToolStripMenuItem("Save Replay", null, (_, _) => saveReplay());
        _recordItem = new ToolStripMenuItem("Start Recording", null, (_, _) => toggleRecording());
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(saveItem);
        menu.Items.Add(_recordItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new NotifyIcon
        {
            Icon = AppIcon,
            Text = "Wispclip",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => openApp();
    }

    public void SetRecording(bool recording) =>
        _recordItem.Text = recording ? "Stop Recording" : "Start Recording";

    public void SetStatus(string tooltip) =>
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;

    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(2500, title, message, ToolTipIcon.None);

    /// <summary>
    /// Builds the tray/window icon from the app logo (Assets/wispclip-small.png, embedded as
    /// a WPF resource: the mark cropped out of the full wordmark lockup so it stays legible
    /// at tray size), resized to a crisp small size. Falls back to a small drawn glyph if the
    /// resource can't be loaded for any reason.
    /// </summary>
    private static Icon CreateIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/wispclip-small.png");
            var resource = System.Windows.Application.GetResourceStream(uri);
            if (resource != null)
            {
                using var source = new Bitmap(resource.Stream);
                using var resized = DownsampleHighQuality(source, 64);
                return Icon.FromHandle(resized.GetHicon());
            }
        }
        catch { /* fall through to the drawn fallback */ }

        return CreateFallbackIcon();
    }

    /// <summary>
    /// Shrinks in successive halving steps instead of one big jump. GDI+'s bicubic filter
    /// aliases thin strokes away when the reduction ratio is large (e.g. 1024 -> 64 is 16x);
    /// keeping every step close to 2x keeps the logo's thin ring intact at tray size.
    /// </summary>
    private static Bitmap DownsampleHighQuality(Bitmap source, int targetSize)
    {
        var current = new Bitmap(source);
        while (current.Width / 2 >= targetSize && current.Width / 2 >= 1)
        {
            var half = current.Width / 2;
            var next = new Bitmap(half, half, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(next))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(current, 0, 0, half, half);
            }
            current.Dispose();
            current = next;
        }

        if (current.Width == targetSize) return current;

        var final = new Bitmap(targetSize, targetSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(final))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(current, 0, 0, targetSize, targetSize);
        }
        current.Dispose();
        return final;
    }

    /// <summary>Drawn rounded-square + play glyph, used only if the logo resource is missing.</summary>
    private static Icon CreateFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(255, 22, 26, 33));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 8);
            g.FillPath(bg, path);

            using var accent = new SolidBrush(Color.FromArgb(255, 255, 133, 58));
            g.FillPolygon(accent, new[]
            {
                new PointF(12, 8.5f),
                new PointF(25, 16),
                new PointF(12, 23.5f),
            });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
