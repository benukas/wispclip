using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wispclip.Services;

namespace Wispclip;

public partial class MainWindow : Window
{
    private HotkeyService? _hotkeys;
    private TrayService? _tray;
    private OverlayNotificationService? _overlay;
    private bool _exiting;
    private bool _savingReplay;
    private readonly bool _startHidden;
    private readonly DispatcherTimer _elapsedTimer;

    public MainWindow(bool startHidden = false)
    {
        _startHidden = startHidden;
        InitializeComponent();

        App.Capture.StateChanged += s => Dispatcher.Invoke(() => OnCaptureStateChanged(s));
        App.Capture.ClipSaved += p => Dispatcher.Invoke(() => OnClipSaved(p));
        App.Capture.CaptureError += m => Dispatcher.Invoke(() => OnCaptureError(m));

        LibraryPage.PlayRequested += clip => Player.Open(clip);
        Player.ClipsChanged += () => LibraryPage.Refresh();
        SettingsPage.Applied += OnSettingsApplied;

        // Only ticks while a recording is in progress (started/stopped in UpdateCaptureUi);
        // an always-on 1 Hz timer would wake the UI thread forever for nothing while the
        // app idles in the tray.
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar only — the window keeps its solid BgBrush fill declared in XAML.
        // (Mica translucency was tried and dropped in favor of deliberate flat colors.)
        Ui.EnableDarkTitleBar(this);

        _hotkeys = new HotkeyService(this);
        RegisterHotkeys();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tray = new TrayService(
            openApp: () => Dispatcher.Invoke(ShowFromTray),
            saveReplay: () => Dispatcher.Invoke(HotkeySaveReplay),
            toggleRecording: () => Dispatcher.Invoke(HotkeyToggleRecording),
            exit: () => Dispatcher.Invoke(ExitApp));
        // Window.Icon is set declaratively in XAML from Assets/wispclip.ico; the tray icon
        // is built separately by TrayService since it needs a GDI Icon, not a WPF ImageSource.
        _overlay = new OverlayNotificationService();

        if (_startHidden)
        {
            Hide();
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
        }
        else
        {
            // When launched hidden at sign-in, skip the library scan (and the thumbnail
            // ffmpeg jobs it spawns) — ShowFromTray does it when the window first appears.
            LibraryPage.Refresh();
        }

        await InitializeEngineAsync();
    }

    /// <summary>Locate ffmpeg, probe the capture pipeline if needed, then start the replay buffer.</summary>
    public async Task InitializeEngineAsync()
    {
        var ff = App.Ffmpeg;
        if (ff == null)
        {
            SetStatus("Capture unavailable", "FFmpeg was not found. Open Settings, then Capture engine.", isError: true);
            SettingsPage.RefreshEngineInfo();
            return;
        }

        var settings = App.Settings.Current;
        if (string.IsNullOrEmpty(settings.Video.PipelineId))
        {
            SetStatus("Optimizing capture", "Testing the lowest-impact hardware path");
            var engine = await EncoderProber.QueryAsync(ff);
            var progress = new Progress<string>(msg => SetStatus(msg, "Testing capture hardware"));
            var id = await EncoderProber.ProbeAsync(ff, settings, engine, progress);
            if (id == null)
            {
                SetStatus("Capture unavailable", "No working pipeline found. Check the log in Settings.", isError: true);
                SettingsPage.RefreshEngineInfo();
                return;
            }
            settings.Video.PipelineId = id;
            App.Settings.Save();
        }

        SettingsPage.RefreshEngineInfo();
        UpdateCaptureUi(App.Capture.State);

        if (settings.Replay.AutoStart && App.Capture.State == CaptureState.Idle)
            App.Capture.StartReplayBuffer();
    }

    // ------------------------------------------------------------------ capture UI

    private void OnCaptureStateChanged(CaptureState state)
    {
        UpdateCaptureUi(state);
        if (state == CaptureState.Recording && App.Settings.Current.OverlayToasts)
            _overlay?.Show("Recording started", $"Stop with {App.Settings.Current.Hotkeys.ToggleRecording}");
    }

    private void UpdateCaptureUi(CaptureState state)
    {
        switch (state)
        {
            case CaptureState.Idle:
                StatusDot.Fill = (Brush)FindResource("TextDimBrush");
                SetDotPulse(false);
                SetStatus(App.Settings.Current.Video.PipelineId == null ? "Not ready" : "Capture idle",
                    App.Settings.Current.Video.PipelineId == null ? "Choose a capture pipeline in Settings" : "Buffer is off");
                break;
            case CaptureState.ReplayBuffer:
                StatusDot.Fill = (Brush)FindResource("LiveBrush");
                SetDotPulse(false);
                SetStatus("Buffer ready", $"Last {App.Settings.Current.Replay.DurationSeconds}s protected");
                break;
            case CaptureState.Recording:
                StatusDot.Fill = (Brush)FindResource("DangerBrush");
                SetDotPulse(false);
                SetStatus("Recording 0:00", "Full recording in progress");
                break;
        }

        if (state == CaptureState.Recording) _elapsedTimer.Start();
        else _elapsedTimer.Stop();

        BtnBuffer.IsChecked = state == CaptureState.ReplayBuffer;
        BufferLabel.Text = state == CaptureState.ReplayBuffer ? "Buffer on" : "Buffer off";
        BtnBuffer.IsEnabled = state != CaptureState.Recording;
        BtnSaveReplay.IsEnabled = state == CaptureState.ReplayBuffer && !_savingReplay;
        RecordLabel.Text = state == CaptureState.Recording ? "Stop" : "Record";
        RecordGlyph.CornerRadius = state == CaptureState.Recording
            ? new CornerRadius(1)
            : new CornerRadius(5);
        _tray?.SetRecording(state == CaptureState.Recording);
        _tray?.SetStatus(state switch
        {
            CaptureState.ReplayBuffer => "Wispclip | buffer ready",
            CaptureState.Recording => "Wispclip | recording",
            _ => "Wispclip",
        });
    }

    /// <summary>Keep the semantic state marker static to avoid needless UI work in the background.</summary>
    private void SetDotPulse(bool on)
    {
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
    }

    private void UpdateElapsed()
    {
        if (App.Capture.State == CaptureState.Recording && App.Capture.StartedAt is { } start)
            SetStatus($"Recording {Ui.FormatDuration((DateTime.Now - start).TotalSeconds)}",
                "Full recording in progress");
    }

    private void SetStatus(string text, string detail = "", bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "TextBrush");
        StatusDetailText.Text = detail;
        if (isError)
            StatusDot.Fill = (Brush)FindResource("DangerBrush");
    }

    private void OnClipSaved(string path)
    {
        SystemSounds.Asterisk.Play();
        string name = System.IO.Path.GetFileName(path);
        if (App.Settings.Current.OverlayToasts)
            _overlay?.Show("Clip saved", name);
        else
            _tray?.Notify("Clip saved", name);
        // Refreshing the library kicks off thumbnail/duration ffmpeg jobs for new clips.
        // While hidden in the tray (i.e. mid-game) that work is invisible and competes
        // with the game, so defer it — ShowFromTray refreshes when the window returns.
        if (IsVisible)
            LibraryPage.Refresh();
    }

    private void OnCaptureError(string message)
    {
        SetStatus("Capture problem", message.Split('\n')[0], isError: true);
        string brief = message.Length > 160 ? message[..160] : message;
        if (App.Settings.Current.OverlayToasts)
            _overlay?.Show("Capture problem", brief, isError: true);
        else
            _tray?.Notify("Wispclip", brief);
    }

    // ------------------------------------------------------------------ buttons & hotkeys

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (LibraryPage == null || SettingsPage == null) return; // during InitializeComponent
        bool library = ReferenceEquals(sender, NavLibrary);
        LibraryPage.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
        if (!library) SettingsPage.LoadFromSettings();
    }

    private void BtnBuffer_Click(object sender, RoutedEventArgs e)
    {
        if (BtnBuffer.IsChecked == true) App.Capture.StartReplayBuffer();
        else App.Capture.StopReplayBuffer();
        UpdateCaptureUi(App.Capture.State);
    }

    private async void BtnSaveReplay_Click(object sender, RoutedEventArgs e) => await SaveReplayAsync();

    private void BtnRecord_Click(object sender, RoutedEventArgs e) => HotkeyToggleRecording();

    private async Task SaveReplayAsync()
    {
        if (_savingReplay || App.Capture.State != CaptureState.ReplayBuffer) return;
        _savingReplay = true;
        BtnSaveReplay.IsEnabled = false;
        try { await App.Capture.SaveReplayAsync(); }
        finally
        {
            _savingReplay = false;
            BtnSaveReplay.IsEnabled = App.Capture.State == CaptureState.ReplayBuffer;
        }
    }

    private void HotkeySaveReplay() => _ = SaveReplayAsync();

    private void HotkeyToggleRecording()
    {
        if (App.Capture.State == CaptureState.Recording) App.Capture.StopRecording();
        else App.Capture.StartRecording();
    }

    private void HotkeyToggleBuffer()
    {
        if (App.Capture.State == CaptureState.ReplayBuffer) App.Capture.StopReplayBuffer();
        else if (App.Capture.State == CaptureState.Idle) App.Capture.StartReplayBuffer();
    }

    public void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.UnregisterAll();
        var hk = App.Settings.Current.Hotkeys;
        var problems = new List<string>();
        if (!_hotkeys.Register(hk.SaveReplay, HotkeySaveReplay, out var e1) && e1 != null) problems.Add(e1);
        if (!_hotkeys.Register(hk.ToggleRecording, HotkeyToggleRecording, out var e2) && e2 != null) problems.Add(e2);
        if (!_hotkeys.Register(hk.ToggleReplayBuffer, HotkeyToggleBuffer, out var e3) && e3 != null) problems.Add(e3);
        foreach (var p in problems) Log.Write("hotkeys", p);
        if (problems.Count > 0)
            SetStatus(problems[0], isError: true);
    }

    private async void OnSettingsApplied(bool needsReprobe)
    {
        RegisterHotkeys();
        LibraryPage.Refresh();

        bool bufferWasRunning = App.Capture.State == CaptureState.ReplayBuffer;
        if (bufferWasRunning) App.Capture.StopReplayBuffer();
        if (needsReprobe || bufferWasRunning || App.Settings.Current.Replay.AutoStart)
            await InitializeEngineAsync();
    }

    // ------------------------------------------------------------------ tray / lifecycle

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Player.ResumeFromBackground();
        LibraryPage.Refresh();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || !App.Settings.Current.CloseToTray)
        {
            ExitApp();
            return;
        }
        e.Cancel = true;
        // Freeze all UI-side activity before disappearing into the tray: a hidden
        // MediaElement would otherwise keep decoding video during gameplay.
        Player.SuspendForBackground();
        Hide();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Player.Shutdown();
        App.Capture.Dispose();
        _hotkeys?.Dispose();
        _overlay?.Dispose();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }
}
