using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Wispclip.Services;

/// <summary>
/// In-game toast notifications: a topmost, click-through, non-activating window on the
/// captured monitor. Unlike tray balloons it renders over borderless/windowed fullscreen
/// games and never steals focus, so saving a clip doesn't interrupt play.
/// (True exclusive-fullscreen bypasses the compositor and can hide it — most modern
/// games run borderless by default.)
/// </summary>
public class OverlayNotificationService : IDisposable
{
    private const int GwlExstyle = -20;
    private const uint WsExTransparent = 0x20, WsExNoActivate = 0x8000000, WsExToolWindow = 0x80, WsExTopmost = 0x8;

    [DllImport("user32.dll")] private static extern uint GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint SetWindowLong(IntPtr hwnd, int index, uint value);

    private readonly Window _window;
    private readonly Border _card;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _icon;
    private readonly DispatcherTimer _hideTimer;

    public OverlayNotificationService()
    {
        _icon = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)),
        };
        _title = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF1)),
        };
        _subtitle = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA0, 0xB4)),
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var textStack = new StackPanel { Margin = new Thickness(12, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(_title);
        textStack.Children.Add(_subtitle);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(textStack);

        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x13, 0x17, 0x1D)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x33, 0x40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 18, 12),
            Child = row,
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Content = _card,
            Opacity = 0,
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            SetWindowLong(hwnd, GwlExstyle,
                GetWindowLong(hwnd, GwlExstyle) | WsExTransparent | WsExNoActivate | WsExToolWindow | WsExTopmost);
        };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); FadeOut(); };
    }

    /// <summary>Show a toast in the top-right corner of the captured monitor.</summary>
    public void Show(string title, string? subtitle = null, bool isError = false)
    {
        _title.Text = title;
        _subtitle.Text = subtitle ?? "";
        _subtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _icon.Text = isError ? "" : ""; // error badge / checkmark seal
        _icon.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(0xD6, 0x45, 0x45)
            : Color.FromRgb(0x5B, 0x8C, 0xFF));

        if (!_window.IsVisible) _window.Show();
        Position();

        _hideTimer.Stop();
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
        _window.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        _hideTimer.Start();
    }

    private void FadeOut()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(280));
        fade.Completed += (_, _) => { if (_window.Opacity < 0.05) _window.Hide(); };
        _window.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void Position()
    {
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int idx = Math.Clamp(App.Settings.Current.Video.MonitorIndex, 0, screens.Length - 1);
            var bounds = screens[idx].Bounds;

            _window.UpdateLayout();
            var source = PresentationSource.FromVisual(_window);
            double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double wPx = _window.ActualWidth * scale;

            const int marginPx = 24;
            _window.Left = (bounds.Right - wPx - marginPx) / scale;
            _window.Top = (bounds.Top + marginPx) / scale;
        }
        catch (Exception ex)
        {
            Log.Write("overlay", $"positioning failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _hideTimer.Stop();
        try { _window.Close(); } catch { }
    }
}
