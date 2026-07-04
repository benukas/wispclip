using System.Windows;
using System.Windows.Media;
using Wispclip.Services;

namespace Wispclip;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private static FfmpegPaths? _ffmpeg;
    private static bool _ffmpegResolved;

    public static SettingsService Settings { get; private set; } = null!;
    public static CaptureService Capture { get; private set; } = null!;
    public static ClipLibraryService Library { get; private set; } = null!;
    public static EditRenderService EditRender { get; private set; } = null!;

    public static FfmpegPaths? Ffmpeg
    {
        get
        {
            if (!_ffmpegResolved)
            {
                _ffmpeg = FfmpegLocator.Locate(Settings.Current.FfmpegDirectory);
                _ffmpegResolved = true;
                Log.Write("ffmpeg", _ffmpeg == null ? "not found" : $"using {_ffmpeg.Ffmpeg}");
            }
            return _ffmpeg;
        }
    }

    public static void InvalidateFfmpeg() => _ffmpegResolved = false;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool launchedAtStartup = e.Args.Any(arg =>
            string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));

        _instanceMutex = new Mutex(true, @"Local\Wispclip_SingleInstance", out bool isNew);
        if (!isNew)
        {
            if (!launchedAtStartup)
            {
                MessageBox.Show("Wispclip is already running. Look for it in the system tray.",
                    "Wispclip", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Log.Init();
        ApplyAccentTheme();

        Settings = new SettingsService();
        Settings.Load();
        Settings.Changed += InvalidateFfmpeg;
        if (!StartupService.TrySync(Settings.Current.LaunchAtWindowsStartup, out var startupError))
            Log.Write("startup", startupError ?? "startup registration failed");

        Capture = new CaptureService(Settings, () => Ffmpeg);
        Library = new ClipLibraryService(Settings, () => Ffmpeg);
        EditRender = new EditRenderService(() => Ffmpeg);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("fatal", args.Exception.ToString());
            MessageBox.Show($"Unexpected error:\n{args.Exception.Message}\n\nDetails were written to the log.",
                "Wispclip", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        MainWindow = new MainWindow(launchedAtStartup);
        MainWindow.Show();
    }

    /// <summary>
    /// Matches the app's single accent color to the user's Windows personalization accent,
    /// so it reads as a first-party Windows app rather than a fixed brand color. Falls back
    /// to the built-in orange (declared in App.xaml) when the accent can't be read.
    /// </summary>
    private static void ApplyAccentTheme()
    {
        if (Ui.GetSystemAccentColor() is not Color accent) return;

        var hover = Ui.Lighten(accent, 0.18);
        var wash = Color.FromArgb(0x33, accent.R, accent.G, accent.B);
        var foreground = Ui.IsColorLight(accent) ? Color.FromRgb(0x0B, 0x0B, 0x0D) : Colors.White;

        var res = Current.Resources;
        res["AccentBrush"] = new SolidColorBrush(accent);
        res["AccentHoverBrush"] = new SolidColorBrush(hover);
        res["AccentDimBrush"] = new SolidColorBrush(wash);
        res["PrimaryBrush"] = new SolidColorBrush(accent);
        res["PrimaryHoverBrush"] = new SolidColorBrush(hover);
        res["SelectedBrush"] = new SolidColorBrush(wash);
        res["SelectedEdgeBrush"] = new SolidColorBrush(accent);
        res["AccentForegroundBrush"] = new SolidColorBrush(foreground);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Capture?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
