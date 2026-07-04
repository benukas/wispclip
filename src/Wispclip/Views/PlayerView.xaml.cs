using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wispclip.Models;
using Wispclip.Services;

namespace Wispclip.Views;

/// <summary>
/// The focused clip editor: precise trimming, smooth zoom segments, and backdrops.
/// The preview composes the scene with WPF (gradient/blur backdrop, shadowed rounded
/// card, live zoom transform); export renders the identical scene through ffmpeg.
/// </summary>
public partial class PlayerView : UserControl
{
    public event Action? ClipsChanged;

    private ClipInfo? _clip;
    private EditProject _project = new();
    private bool _dirty;
    private bool _playing;
    private bool _exporting;
    private bool _zoomPlacementMode;
    private double _durationSec;
    private int _videoW = 1920, _videoH = 1080;
    private ZoomSegment? _selected;
    private bool _suppressUi;
    private bool _loop;
    private bool _muted;
    private double _speed = 1.0;
    private double _frameStep = 1.0 / 60;
    private bool _fullscreen;
    private WindowState _prevWindowState;
    private WindowStyle _prevWindowStyle;
    private ResizeMode _prevResizeMode;
    private readonly DispatcherTimer _timer;

    private static readonly Geometry SpeakerOn =
        Geometry.Parse("M1,5 H4 L8,2 V12 L4,9 H1 Z M10.5,4 A4,4 0 0 1 10.5,10 A6.2,6.2 0 0 0 10.5,4 Z");
    private static readonly Geometry SpeakerMuted =
        Geometry.Parse("M1,5 H4 L8,2 V12 L4,9 H1 Z M10,5 L15,10 L13.6,11.4 L8.6,6.4 Z");

    // timeline interaction
    private enum TimelineMode { None, Scrub, DragBlock, DragTrimStart, DragTrimEnd, DragZoomStart, DragZoomEnd }
    private TimelineMode _tlMode;
    private ZoomSegment? _dragSeg;
    private double _dragOffset;
    private Rectangle? _playhead;
    private bool _seeking;
    private bool _resumeAfterSeek;
    private double _scrubSeconds;
    private long _lastSeekAppliedMs;
    private long _seekPreviewUntilMs;
    private long _ignoreMediaEndedUntilMs;
    private int _seekGeneration;
    private int _recoveredSeekGeneration = -1;
    private int _mediaSession;

    // scene layout cache (preview DIPs)
    private double _sceneW, _sceneH, _cardW, _cardH, _padX, _padY;

    public PlayerView()
    {
        InitializeComponent();
        PopulateBackdropCombo();
        PopulateSpeedCombo();
        PopulateResolutionCombo();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        PreviewKeyDown += OnKeyDown;
    }

    private void PopulateSpeedCombo()
    {
        foreach (double s in new[] { 0.25, 0.5, 1.0, 1.5, 2.0 })
            SpeedCombo.Items.Add(new ComboBoxItem
            {
                Content = s == 1.0 ? "1\u00d7" : $"{s:0.##}\u00d7",
                Tag = s,
            });
        SpeedCombo.SelectedIndex = 2; // 1x
    }

    private void PopulateResolutionCombo()
    {
        ResolutionCombo.Items.Add(new ComboBoxItem { Content = "Source", Tag = "source" });
        ResolutionCombo.Items.Add(new ComboBoxItem { Content = "1080p", Tag = "1080p" });
        ResolutionCombo.Items.Add(new ComboBoxItem { Content = "720p", Tag = "720p" });
        ResolutionCombo.SelectedIndex = 0;
    }

    private void PopulateBackdropCombo()
    {
        BgStyleCombo.Items.Add(new ComboBoxItem { Content = "None", Tag = "none" });
        BgStyleCombo.Items.Add(new ComboBoxItem { Content = "Blurred video", Tag = "blur" });
        foreach (var (key, name, c0, c1) in EditRenderService.GradientPresets)
        {
            var swatch = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                Background = EditRenderService.MakeGradientBrush(c0, c1),
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            BgStyleCombo.Items.Add(new ComboBoxItem { Content = row, Tag = $"gradient:{key}" });
        }
    }

    // ------------------------------------------------------------------ open / close

    public void Open(ClipInfo clip)
    {
        ReleaseCurrentMedia(saveProject: true);

        _seekGeneration++;
        _seeking = false;
        _resumeAfterSeek = false;
        _scrubSeconds = 0;
        _seekPreviewUntilMs = 0;
        _ignoreMediaEndedUntilMs = 0;
        _recoveredSeekGeneration = -1;
        _clip = clip;
        _project = EditProjectStore.Load(clip.Path);
        _dirty = false;
        DirtyDot.Visibility = Visibility.Hidden;
        _selected = null;
        SetZoomPlacementMode(false, announce: false);
        _durationSec = clip.DurationSeconds ?? 0;
        _frameStep = 1.0 / Math.Clamp(App.Settings.Current.Video.Fps, 1, 240);
        TitleText.Text = clip.Name;
        PlayerStatus.Text = "";

        _suppressUi = true;
        SelectComboTag(BgStyleCombo, _project.Background.Style);
        PadSlider.Value = _project.Background.PaddingPercent;
        RadiusSlider.Value = _project.Background.CornerRadius;
        SelectComboTag(ResolutionCombo, _project.OutputResolution);
        AudioFadeCheck.IsChecked = _project.AudioFade;
        VideoFadeCheck.IsChecked = _project.VideoFade;
        VideoFadeSlider.Value = Math.Clamp(_project.VideoFadeSeconds, 0.1, 2);
        AudioFadeSlider.Value = Math.Clamp(_project.AudioFadeSeconds, 0.1, 2);
        _suppressUi = false;
        UpdateFadeTexts();

        Visibility = Visibility.Visible;
        _timer.Start();
        NormalizeTrimRange();
        UpdateTrimText();
        UpdateZoomEditor();
        UpdateSceneLayout();
        RebuildTimeline();
        if (_project.Background.Style == "blur") _ = EnsureBlurPreviewAsync();
        Focus();

        int session = _mediaSession;
        _ = OpenMediaAsync(clip.Path, session);
    }

    public void ClosePlayer()
    {
        if (_exporting)
        {
            PlayerStatus.Text = "Export is still running. Wait for it to finish before closing the editor.";
            return;
        }
        SetFullscreen(false);
        ReleaseCurrentMedia(saveProject: true);
        Visibility = Visibility.Collapsed;
    }

    private async Task OpenMediaAsync(string path, int session)
    {
        // Let WPF finish tearing down the previous decoder before assigning a
        // new source. The session check prevents an older request winning.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_clip == null || session != _mediaSession ||
            !string.Equals(_clip.Path, path, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Media.Source = new Uri(path, UriKind.Absolute);
            Media.Volume = Vol.Value;
            Media.IsMuted = _muted;
            Media.SpeedRatio = _speed;
            Media.Play();
            SetPlayingUi(true);
            Log.Write("player", $"opening: {path}");
        }
        catch (Exception ex)
        {
            SetPlayingUi(false);
            PlayerStatus.Text = $"Could not open this clip: {ex.Message}";
            Log.Write("player", $"open failed for {path}: {ex}");
        }
    }

    private void ReleaseCurrentMedia(bool saveProject)
    {
        _mediaSession++;
        _seekGeneration++;
        _seeking = false;
        _resumeAfterSeek = false;
        _tlMode = TimelineMode.None;
        _timer.Stop();
        SetZoomPlacementMode(false, announce: false);

        if (saveProject && _dirty && _clip != null)
            EditProjectStore.Save(_clip.Path, _project);
        _dirty = false;

        try { Media.Pause(); } catch { }
        try { Media.Stop(); } catch { }
        try { Media.Close(); } catch { }
        try { Media.Source = null; } catch { }

        SetPlayingUi(false);
        _clip = null;
        _selected = null;
    }

    public void Shutdown()
    {
        _exporting = false;
        SetFullscreen(false);
        ReleaseCurrentMedia(saveProject: true);
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Called when the window hides to the tray with the editor still open: stops video
    /// decode and the 30 Hz UI tick so an invisible player can't burn CPU/GPU while gaming.
    /// The clip and edit state stay loaded for when the window comes back.
    /// </summary>
    public void SuspendForBackground()
    {
        if (_clip == null) return;
        if (_playing)
        {
            try { Media.Pause(); } catch { }
            SetPlayingUi(false);
        }
        _timer.Stop();
    }

    /// <summary>Restarts the UI tick after the window is shown again (playback stays paused).</summary>
    public void ResumeFromBackground()
    {
        if (_clip != null) _timer.Start();
    }

    // ------------------------------------------------------------------ media events

    private void Media_Opened(object sender, RoutedEventArgs e)
    {
        if (!IsCurrentMedia()) return;
        if (Media.NaturalDuration.HasTimeSpan)
            _durationSec = Media.NaturalDuration.TimeSpan.TotalSeconds;
        if (Media.NaturalVideoWidth > 0)
        {
            _videoW = Media.NaturalVideoWidth;
            _videoH = Media.NaturalVideoHeight;
        }
        NormalizeTrimRange();
        UpdateTrimText();
        UpdateSceneLayout();
        RebuildTimeline();
        SetPlayingUi(true);
        Log.Write("player", $"opened: {_clip!.Path}");
    }

    private void Media_Ended(object sender, RoutedEventArgs e)
    {
        if (!IsCurrentMedia()) return;
        long now = Environment.TickCount64;
        if (_seeking)
            return;

        // A decoder may report an old end event after a reverse seek. Never
        // auto-restart from that event, which is what creates visible loops.
        if (now < _ignoreMediaEndedUntilMs)
        {
            SetPlayingUi(false);
            if (_recoveredSeekGeneration != _seekGeneration)
            {
                _recoveredSeekGeneration = _seekGeneration;
                _ = RecoverFromStaleEndAsync(_seekGeneration, _scrubSeconds);
            }
            return;
        }

        if (_loop && _clip != null)
        {
            double loopStart = _project.TrimStart;
            Media.Position = TimeSpan.FromSeconds(loopStart);
            Media.Play();
            SetPlayingUi(true);
            _scrubSeconds = loopStart;
            _seekPreviewUntilMs = now + 200;
            _ignoreMediaEndedUntilMs = now + 500;
            return;
        }

        SetPlayingUi(false);
    }

    private async Task RecoverFromStaleEndAsync(int generation, double target)
    {
        await Task.Delay(75);
        if (_clip == null || _seeking || generation != _seekGeneration) return;
        Media.Position = TimeSpan.FromSeconds(Math.Clamp(target, 0, _durationSec));
        Media.Play();
        SetPlayingUi(true);
    }

    private void Media_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!IsCurrentMedia()) return;
        SetPlayingUi(false);
        PlayerStatus.Text = "Playback failed: a decoder for this format may be missing. " +
            "HEVC and AV1 clips may need their matching Microsoft Store video extension. " +
            "You can also switch recording to H.264 in Settings. The file itself is fine.";
        Log.Write("player", $"playback failed: {e.ErrorException?.Message}");
    }

    private bool IsCurrentMedia() =>
        _clip != null && Media.Source != null &&
        string.Equals(Media.Source.LocalPath, _clip.Path, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ preview loop

    private void OnTick()
    {
        if (_clip == null) return;
        double t = CurrentDisplaySeconds();

        if (_playing && !_seeking)
        {
            double endLimit = HasActiveTrim ? TrimEndSeconds : _durationSec;
            if (endLimit > 0 && t >= endLimit - 0.03)
            {
                if (_loop)
                {
                    double loopStart = _project.TrimStart;
                    long tick = Environment.TickCount64;
                    Media.Position = TimeSpan.FromSeconds(loopStart);
                    _scrubSeconds = loopStart;
                    _seekPreviewUntilMs = tick + 200;
                    _ignoreMediaEndedUntilMs = tick + 500;
                    t = loopStart;
                }
                else if (HasActiveTrim)
                {
                    Media.Pause();
                    Media.Position = TimeSpan.FromSeconds(TrimEndSeconds);
                    SetPlayingUi(false);
                    t = TrimEndSeconds;
                }
            }
        }

        TimeText.Text = $"{Ui.FormatDuration(t)} / {Ui.FormatDuration(_durationSec)}";
        UpdatePlayheadActionLabels(t);

        // live zoom transform, same math as the export expressions
        var (z, fx, fy) = ZoomMath.Evaluate(_project.Zooms, t);
        if (z <= 1.001 || _sceneW <= 0)
        {
            SceneScale.ScaleX = SceneScale.ScaleY = 1;
            SceneTranslate.X = SceneTranslate.Y = 0;
        }
        else
        {
            double fxs = (_padX + fx * _cardW) / _sceneW;
            double fys = (_padY + fy * _cardH) / _sceneH;
            double x0 = Math.Clamp(fxs * _sceneW - _sceneW / (2 * z), 0, _sceneW - _sceneW / z);
            double y0 = Math.Clamp(fys * _sceneH - _sceneH / (2 * z), 0, _sceneH - _sceneH / z);
            SceneScale.ScaleX = SceneScale.ScaleY = z;
            SceneTranslate.X = -x0 * z;
            SceneTranslate.Y = -y0 * z;
        }

        if (_playhead != null && _durationSec > 0)
            Canvas.SetLeft(_playhead, XFor(t));
    }

    private double CurrentDisplaySeconds()
    {
        long now = Environment.TickCount64;
        if (_seeking || now < _seekPreviewUntilMs)
            return Math.Clamp(_scrubSeconds, 0, _durationSec);
        return Math.Clamp(Media.Position.TotalSeconds, 0, _durationSec);
    }

    private double TrimEndSeconds =>
        _project.TrimEnd > 0.01
            ? Math.Clamp(_project.TrimEnd, 0, _durationSec)
            : _durationSec;

    private bool HasActiveTrim =>
        _project.TrimStart > 0.01 || TrimEndSeconds < _durationSec - 0.01;

    private void NormalizeTrimRange()
    {
        if (_durationSec <= 0) return;
        _project.TrimStart = Math.Clamp(_project.TrimStart, 0, Math.Max(0, _durationSec - 0.1));
        if (_project.TrimEnd > 0.01)
            _project.TrimEnd = Math.Clamp(_project.TrimEnd, _project.TrimStart + 0.1, _durationSec);
        if (_project.TrimEnd >= _durationSec - 0.01)
            _project.TrimEnd = 0;
    }

    // ------------------------------------------------------------------ scene layout

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSceneLayout();

    private void UpdateSceneLayout()
    {
        double hostW = PreviewHost.ActualWidth - 16, hostH = PreviewHost.ActualHeight - 16;
        if (hostW < 50 || hostH < 50) return;

        double aspect = _videoW / (double)Math.Max(1, _videoH);
        _sceneW = Math.Min(hostW, hostH * aspect);
        _sceneH = _sceneW / aspect;
        SceneViewport.Width = Scene.Width = _sceneW;
        SceneViewport.Height = Scene.Height = _sceneH;

        bool hasBg = _project.HasBackground;
        double p = hasBg ? Math.Clamp(_project.Background.PaddingPercent, 0, 20) / 100.0 : 0;
        _cardW = _sceneW * (1 - 2 * p);
        _cardH = _sceneH * (1 - 2 * p);
        _padX = (_sceneW - _cardW) / 2;
        _padY = (_sceneH - _cardH) / 2;
        CardHost.Width = _cardW;
        CardHost.Height = _cardH;

        double previewScale = _sceneW / _videoW;
        double r = hasBg ? _project.Background.CornerRadius * previewScale : 0;
        CardShadow.CornerRadius = new CornerRadius(r);
        CardClip.Clip = r > 0 || hasBg
            ? new RectangleGeometry(new Rect(0, 0, _cardW, _cardH), r, r)
            : null;

        CardShadow.Effect = hasBg
            ? new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = Math.Max(14, _sceneW * 0.03),
                ShadowDepth = Math.Max(3, _sceneH * 0.012),
                Direction = 270,
                Opacity = _project.Background.ShadowOpacity,
            }
            : null;
        CardShadow.Visibility = hasBg ? Visibility.Visible : Visibility.Collapsed;

        // backdrop fill
        string style = _project.Background.Style;
        if (style.StartsWith("gradient:"))
        {
            var preset = EditRenderService.GradientPresets.FirstOrDefault(g => g.Key == style["gradient:".Length..]);
            if (preset.Key == null) preset = EditRenderService.GradientPresets[0];
            SceneBg.Background = EditRenderService.MakeGradientBrush(preset.C0, preset.C1);
            SceneBgImage.Visibility = Visibility.Collapsed;
            SceneVignette.Background = new RadialGradientBrush(
                Color.FromArgb(0, 0, 0, 0), Color.FromArgb(70, 0, 0, 0)) { RadiusX = 0.85, RadiusY = 0.85 };
            SceneVignette.Visibility = Visibility.Visible;
        }
        else if (style == "blur")
        {
            SceneBg.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16));
            SceneBgImage.Visibility = Visibility.Visible;
            SceneVignette.Visibility = Visibility.Collapsed;
        }
        else
        {
            SceneBg.Background = null;
            SceneBgImage.Visibility = Visibility.Collapsed;
            SceneVignette.Visibility = Visibility.Collapsed;
        }

        PadSlider.IsEnabled = RadiusSlider.IsEnabled = hasBg;
        UpdateFocusMarker();
    }

    private async Task EnsureBlurPreviewAsync()
    {
        if (_clip == null) return;
        var path = await App.EditRender.GenerateBlurPreviewAsync(_clip.Path);
        if (path == null || _clip == null || _project.Background.Style != "blur") return;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        SceneBgImage.Source = bmp;
    }

    // ------------------------------------------------------------------ timeline

    private double XFor(double t) =>
        8 + Math.Clamp(t / Math.Max(0.1, _durationSec), 0, 1) * (Timeline.ActualWidth - 16);

    private double TAt(double x) =>
        Math.Clamp((x - 8) / Math.Max(1, Timeline.ActualWidth - 16), 0, 1) * _durationSec;

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildTimeline();

    private void RebuildTimeline()
    {
        Timeline.Children.Clear();
        _playhead = null;
        double w = Timeline.ActualWidth, h = Timeline.ActualHeight;
        if (w < 20 || h < 10 || _durationSec <= 0) return;

        const double rulerH = 18;

        // ruler: tick marks and time labels along the top
        double step = ChooseRulerStep(_durationSec);
        for (double tk = 0; tk <= _durationSec + 0.001; tk += step)
        {
            double tx = XFor(tk);
            var tick = new Rectangle
            {
                Width = 1, Height = 5,
                Fill = (Brush)FindResource("StrongEdgeBrush"),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tick, tx);
            Canvas.SetTop(tick, 10);
            Timeline.Children.Add(tick);

            var label = new TextBlock
            {
                Text = Ui.FormatDuration(tk),
                FontSize = 8.5,
                Foreground = (Brush)FindResource("TextFaintBrush"),
                IsHitTestVisible = false,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(tx + 3, 2, Math.Max(2, w - label.DesiredSize.Width - 2)));
            Canvas.SetTop(label, 1);
            Timeline.Children.Add(label);
        }

        // base track
        var track = new Rectangle
        {
            Width = w - 16, Height = 6, RadiusX = 3, RadiusY = 3,
            Fill = (Brush)FindResource("StrongEdgeBrush"),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(track, 8);
        Canvas.SetTop(track, h - 13);
        Timeline.Children.Add(track);

        // Active trim range and clearly excluded footage.
        double trimStartX = XFor(_project.TrimStart);
        double trimEndX = XFor(TrimEndSeconds);
        void AddExcluded(double left, double width)
        {
            if (width <= 0.5) return;
            var excluded = new Rectangle
            {
                Width = width,
                Height = h - rulerH,
                Fill = new SolidColorBrush(Color.FromArgb(0xA8, 0x03, 0x04, 0x06)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(excluded, left);
            Canvas.SetTop(excluded, rulerH);
            Timeline.Children.Add(excluded);
        }
        AddExcluded(0, trimStartX);
        AddExcluded(trimEndX, Math.Max(0, w - trimEndX));

        var trimRange = new Rectangle
        {
            Width = Math.Max(1, trimEndX - trimStartX),
            Height = h - rulerH - 6,
            RadiusX = 4,
            RadiusY = 4,
            Fill = (Brush)FindResource("SelectedBrush"),
            Stroke = (Brush)FindResource("SelectedEdgeBrush"),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(trimRange, trimStartX);
        Canvas.SetTop(trimRange, rulerH + 2);
        Timeline.Children.Add(trimRange);

        // zoom blocks
        foreach (var seg in _project.Zooms.OrderBy(s => s.Start))
        {
            bool sel = ReferenceEquals(seg, _selected);
            double dur = Math.Max(0, seg.End - seg.Start);
            var block = new Border
            {
                Width = Math.Max(10, XFor(seg.End) - XFor(seg.Start)),
                Height = h - rulerH - 24,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(sel ? (byte)0xF0 : (byte)0xA8, 0xFF, 0x85, 0x3A)),
                BorderBrush = sel ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("SelectedEdgeBrush"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = seg,
                Child = new TextBlock
                {
                    Text = $"{seg.Level:0.#}\u00d7 \u00b7 {dur:0.0}s",
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextBrush"),
                },
            };
            Canvas.SetLeft(block, XFor(seg.Start));
            Canvas.SetTop(block, rulerH + 6);
            Timeline.Children.Add(block);
        }

        // resize handles for the selected zoom (drag to change its length)
        if (_selected != null && _project.Zooms.Contains(_selected))
        {
            void AddZoomEdge(double x, string tag)
            {
                var handle = new Border
                {
                    Width = 7,
                    Height = h - rulerH - 20,
                    CornerRadius = new CornerRadius(3),
                    Background = (Brush)FindResource("AccentBrush"),
                    Cursor = Cursors.SizeWE,
                    Tag = tag,
                    ToolTip = "Drag to change zoom length",
                };
                Canvas.SetLeft(handle, Math.Clamp(x - 3, 0, Math.Max(0, w - 7)));
                Canvas.SetTop(handle, rulerH + 4);
                Timeline.Children.Add(handle);
            }
            AddZoomEdge(XFor(_selected.Start), "zoom-start");
            AddZoomEdge(XFor(_selected.End), "zoom-end");
        }

        void AddTrimHandle(double x, string tag)
        {
            var handle = new Border
            {
                Width = 10,
                Height = h - rulerH - 2,
                CornerRadius = new CornerRadius(3),
                Background = (Brush)FindResource("PrimaryBrush"),
                Cursor = Cursors.SizeWE,
                Tag = tag,
                ToolTip = tag == "trim-start" ? "Drag trim start" : "Drag trim end",
            };
            Canvas.SetLeft(handle, Math.Clamp(x - 5, 0, Math.Max(0, w - 10)));
            Canvas.SetTop(handle, rulerH);
            Timeline.Children.Add(handle);
        }
        AddTrimHandle(trimStartX, "trim-start");
        AddTrimHandle(trimEndX, "trim-end");

        // playhead
        _playhead = new Rectangle
        {
            Width = 3, Height = h - 4,
            Fill = Brushes.White,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_playhead, XFor(CurrentDisplaySeconds()));
        Canvas.SetTop(_playhead, 2);
        Timeline.Children.Add(_playhead);
    }

    private static double ChooseRulerStep(double duration)
    {
        foreach (double s in new[] { 1.0, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
            if (duration / s <= 8) return s;
        return 600;
    }

    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(Timeline);
        string? trimHandle = FindTrimHandle(e.OriginalSource);
        string? zoomEdge = FindZoomEdge(e.OriginalSource);
        var block = FindZoomBlock(e.OriginalSource);
        if (trimHandle != null)
        {
            BeginScrub();
            bool start = trimHandle == "trim-start";
            _tlMode = start ? TimelineMode.DragTrimStart : TimelineMode.DragTrimEnd;
            double target = UpdateTrimFromPointer(start, pos.X);
            SeekTo(target, force: true);
        }
        else if (zoomEdge != null && _selected != null)
        {
            _tlMode = zoomEdge == "zoom-start" ? TimelineMode.DragZoomStart : TimelineMode.DragZoomEnd;
            _dragSeg = _selected;
        }
        else if (block?.Tag is ZoomSegment seg)
        {
            Select(seg);
            _tlMode = TimelineMode.DragBlock;
            _dragSeg = seg;
            _dragOffset = TAt(pos.X) - seg.Start;
        }
        else
        {
            _tlMode = TimelineMode.Scrub;
            BeginScrub();
            SeekTo(TAt(pos.X), force: true);
        }
        Timeline.CaptureMouse();
        e.Handled = true;
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tlMode == TimelineMode.None) return;
        var pos = e.GetPosition(Timeline);
        if (_tlMode == TimelineMode.Scrub)
        {
            SeekTo(TAt(pos.X));
        }
        else if (_tlMode is TimelineMode.DragTrimStart or TimelineMode.DragTrimEnd)
        {
            bool start = _tlMode == TimelineMode.DragTrimStart;
            double target = UpdateTrimFromPointer(start, pos.X);
            SeekTo(target);
        }
        else if (_tlMode == TimelineMode.DragBlock && _dragSeg != null)
        {
            double len = _dragSeg.End - _dragSeg.Start;
            double start = Math.Clamp(TAt(pos.X) - _dragOffset, 0, Math.Max(0, _durationSec - len));
            _dragSeg.Start = start;
            _dragSeg.End = start + len;
            MarkDirty();
            RebuildTimeline();
            UpdateZoomEditor();
        }
        else if (_tlMode == TimelineMode.DragZoomStart && _dragSeg != null)
        {
            _dragSeg.Start = Math.Round(Math.Clamp(TAt(pos.X), 0, _dragSeg.End - 0.3), 3);
            MarkDirty();
            RebuildTimeline();
            UpdateZoomEditor();
        }
        else if (_tlMode == TimelineMode.DragZoomEnd && _dragSeg != null)
        {
            _dragSeg.End = Math.Round(Math.Clamp(TAt(pos.X), _dragSeg.Start + 0.3, _durationSec), 3);
            MarkDirty();
            RebuildTimeline();
            UpdateZoomEditor();
        }
    }

    private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        bool finishSeek = _tlMode is TimelineMode.Scrub
            or TimelineMode.DragTrimStart
            or TimelineMode.DragTrimEnd;
        if (_tlMode == TimelineMode.Scrub)
        {
            SeekTo(TAt(e.GetPosition(Timeline).X), force: true);
        }
        else if (_tlMode is TimelineMode.DragTrimStart or TimelineMode.DragTrimEnd)
        {
            bool start = _tlMode == TimelineMode.DragTrimStart;
            double target = UpdateTrimFromPointer(start, e.GetPosition(Timeline).X);
            SeekTo(target, force: true);
        }
        _tlMode = TimelineMode.None;
        _dragSeg = null;
        Timeline.ReleaseMouseCapture();
        if (finishSeek)
            FinishScrub();
    }

    private void Timeline_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_tlMode == TimelineMode.None) return;
        bool finishSeek = _tlMode is TimelineMode.Scrub
            or TimelineMode.DragTrimStart
            or TimelineMode.DragTrimEnd;
        _tlMode = TimelineMode.None;
        _dragSeg = null;
        if (finishSeek)
            FinishScrub();
    }

    private static string? FindTrimHandle(object source)
    {
        var el = source as DependencyObject;
        while (el != null)
        {
            if (el is FrameworkElement { Tag: string tag } &&
                (tag == "trim-start" || tag == "trim-end"))
                return tag;
            if (el is Canvas) return null;
            el = VisualTreeHelper.GetParent(el);
        }
        return null;
    }

    private static Border? FindZoomBlock(object source)
    {
        var el = source as DependencyObject;
        while (el != null)
        {
            if (el is Border b && b.Tag is ZoomSegment) return b;
            if (el is Canvas) return null;
            el = VisualTreeHelper.GetParent(el);
        }
        return null;
    }

    private static string? FindZoomEdge(object source)
    {
        var el = source as DependencyObject;
        while (el != null)
        {
            if (el is FrameworkElement { Tag: string tag } &&
                (tag == "zoom-start" || tag == "zoom-end"))
                return tag;
            if (el is Canvas) return null;
            el = VisualTreeHelper.GetParent(el);
        }
        return null;
    }

    private double UpdateTrimFromPointer(bool start, double x)
    {
        double t = TAt(x);
        if (start)
        {
            t = Math.Clamp(t, 0, Math.Max(0, TrimEndSeconds - 0.1));
            _project.TrimStart = t <= 0.01 ? 0 : Math.Round(t, 3);
        }
        else
        {
            t = Math.Clamp(t, _project.TrimStart + 0.1, _durationSec);
            _project.TrimEnd = t >= _durationSec - 0.01 ? 0 : Math.Round(t, 3);
        }
        MarkDirty();
        UpdateTrimText();
        RebuildTimeline();
        return start ? _project.TrimStart : TrimEndSeconds;
    }

    private void BeginScrub()
    {
        _seekGeneration++;
        _scrubSeconds = CurrentDisplaySeconds();
        _resumeAfterSeek = _playing;
        if (_playing)
            Media.Pause();
        SetPlayingUi(false);
        _seeking = true;
    }

    private void SeekTo(double t, bool force = false)
    {
        _scrubSeconds = Math.Clamp(t, 0, _durationSec);
        long now = Environment.TickCount64;
        _seekPreviewUntilMs = now + 300;

        // MediaElement seeks are asynchronous. Throttling reverse scrubs prevents
        // stale decoder work from overtaking the newest target position.
        if (force || now - _lastSeekAppliedMs >= 55)
        {
            Media.Position = TimeSpan.FromSeconds(_scrubSeconds);
            _lastSeekAppliedMs = now;
            _ignoreMediaEndedUntilMs = now + 900;
        }
        OnTick();
    }

    private async void FinishScrub()
    {
        if (!_seeking || _clip == null) return;

        _seeking = false;
        double target = _scrubSeconds;
        bool resume = _resumeAfterSeek;
        _resumeAfterSeek = false;
        int generation = ++_seekGeneration;
        long now = Environment.TickCount64;
        _ignoreMediaEndedUntilMs = now + 900;
        _seekPreviewUntilMs = now + 350;
        Media.Position = TimeSpan.FromSeconds(target);

        if (!resume)
        {
            SetPlayingUi(false);
            return;
        }

        // Give MediaElement one dispatcher turn to settle the final seek before
        // playback resumes. This avoids the forward/backward seek loop.
        await Task.Delay(75);
        if (_clip == null || _seeking || generation != _seekGeneration) return;
        Media.Position = TimeSpan.FromSeconds(target);
        Media.Play();
        SetPlayingUi(true);
    }

    // ------------------------------------------------------------------ zoom editing

    private void Select(ZoomSegment? seg)
    {
        _selected = seg;
        if (seg == null)
            SetZoomPlacementMode(false, announce: false);
        UpdateZoomEditor();
        UpdateFocusMarker();
        RebuildTimeline();
    }

    private void UpdateZoomEditor()
    {
        bool has = _selected != null;
        ZoomEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ZoomHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (_selected == null) return;

        _suppressUi = true;
        ZoomLevelSlider.Value = _selected.Level;
        RampSlider.Value = _selected.RampSeconds;
        _suppressUi = false;
        ZoomLevelText.Text = $"{_selected.Level:0.0}×";
        double dur = Math.Max(0, _selected.End - _selected.Start);
        ZoomRangeText.Text = $"{Ui.FormatDuration(_selected.Start)}\u2013{Ui.FormatDuration(_selected.End)} \u00b7 {dur:0.0}s";
    }

    private void UpdateFocusMarker()
    {
        if (_selected == null || _cardW <= 0)
        {
            FocusMarker.Visibility = Visibility.Collapsed;
            return;
        }
        FocusMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(FocusMarker, _padX + _selected.FocusX * _cardW - 13);
        Canvas.SetTop(FocusMarker, _padY + _selected.FocusY * _cardH - 13);
    }

    private void AddZoom_Click(object sender, RoutedEventArgs e)
    {
        if (_clip == null || _durationSec < 1) return;
        if (_zoomPlacementMode)
        {
            SetZoomPlacementMode(false);
            return;
        }
        double t = Math.Clamp(CurrentDisplaySeconds(), 0, Math.Max(0, _durationSec - 1));
        var seg = new ZoomSegment
        {
            Start = t,
            End = Math.Min(t + 3, _durationSec),
            Level = 2.0,
        };
        _project.Zooms.Add(seg);
        MarkDirty();
        Select(seg);
        SetZoomPlacementMode(true);
    }

    private void RemoveZoom_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        SetZoomPlacementMode(false, announce: false);
        _project.Zooms.Remove(_selected);
        MarkDirty();
        Select(null);
    }

    private void ZoomParam_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi || _selected == null) return;
        _selected.Level = Math.Round(ZoomLevelSlider.Value, 1);
        _selected.RampSeconds = Math.Round(RampSlider.Value, 2);
        ZoomLevelText.Text = $"{_selected.Level:0.0}×";
        MarkDirty();
        RebuildTimeline();
    }

    private void Scene_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_selected == null || _cardW <= 0) return;
        var pos = e.GetPosition(CardClip);
        if (pos.X < 0 || pos.Y < 0 || pos.X > _cardW || pos.Y > _cardH) return;
        _selected.FocusX = Math.Round(pos.X / _cardW, 3);
        _selected.FocusY = Math.Round(pos.Y / _cardH, 3);
        MarkDirty();
        UpdateFocusMarker();
        if (_zoomPlacementMode)
        {
            ZoomPlacementText.Text = "Focus set. Click again to adjust, or finish placement.";
            PlayerStatus.Text = "Zoom focus updated.";
        }
    }

    private void DoneZoomPlacement_Click(object sender, RoutedEventArgs e)
    {
        // Deselect the zoom: this ends placement mode, hides the focus crosshair,
        // closes the zoom editor, and clears the timeline resize handles.
        Select(null);
        PlayerStatus.Text = "Zoom placed.";
        Focus();
    }

    private void SetZoomPlacementMode(bool active, bool announce = true)
    {
        _zoomPlacementMode = active && _selected != null;
        ZoomPlacementBanner.Visibility = _zoomPlacementMode ? Visibility.Visible : Visibility.Collapsed;
        PreviewHost.Cursor = _zoomPlacementMode ? Cursors.Cross : Cursors.Arrow;
        AddZoomLabel.Text = _zoomPlacementMode ? "Finish zoom" : "Add zoom";
        AddZoomBtn.Background = _zoomPlacementMode
            ? (Brush)FindResource("SelectedBrush")
            : Brushes.Transparent;
        AddZoomBtn.BorderBrush = _zoomPlacementMode
            ? (Brush)FindResource("SelectedEdgeBrush")
            : (Brush)FindResource("StrongEdgeBrush");
        ZoomPlacementText.Text = "Click the preview to place the zoom focus.";
        if (announce)
            PlayerStatus.Text = _zoomPlacementMode
                ? "Zoom placement active. Choose the focus point in the preview."
                : "Zoom placement finished.";
    }

    // ------------------------------------------------------------------ backdrop editing

    private void BgStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || _clip == null) return;
        _project.Background.Style = (BgStyleCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
        MarkDirty();
        UpdateSceneLayout();
        if (_project.Background.Style == "blur") _ = EnsureBlurPreviewAsync();
    }

    private void BgParam_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi || _clip == null) return;
        _project.Background.PaddingPercent = Math.Round(PadSlider.Value, 1);
        _project.Background.CornerRadius = Math.Round(RadiusSlider.Value);
        MarkDirty();
        UpdateSceneLayout();
    }

    // ------------------------------------------------------------------ trim

    private void SetIn_Click(object sender, RoutedEventArgs e)
    {
        if (_clip == null || _durationSec <= 0) return;
        double end = TrimEndSeconds;
        _project.TrimStart = Math.Round(
            Math.Clamp(CurrentDisplaySeconds(), 0, Math.Max(0, end - 0.1)), 3);
        if (_project.TrimStart <= 0.01)
            _project.TrimStart = 0;
        MarkDirty();
        UpdateTrimText();
        RebuildTimeline();
        PlayerStatus.Text = $"Trim starts at {Ui.FormatDuration(_project.TrimStart)}.";
    }

    private void SetOut_Click(object sender, RoutedEventArgs e)
    {
        if (_clip == null || _durationSec <= 0) return;
        double end = Math.Round(
            Math.Clamp(CurrentDisplaySeconds(), _project.TrimStart + 0.1, _durationSec), 3);
        _project.TrimEnd = end >= _durationSec - 0.01 ? 0 : end;
        MarkDirty();
        UpdateTrimText();
        RebuildTimeline();
        PlayerStatus.Text = $"Trim ends at {Ui.FormatDuration(TrimEndSeconds)}.";
    }

    private void ClearTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_clip == null) return;
        _project.TrimStart = 0;
        _project.TrimEnd = 0;
        MarkDirty();
        UpdateTrimText();
        RebuildTimeline();
        PlayerStatus.Text = "Using the full clip.";
    }

    private void UpdateTrimText()
    {
        if (_durationSec <= 0)
        {
            TrimText.Text = "";
            return;
        }

        TrimText.Text = HasActiveTrim
            ? $"{Ui.FormatDuration(_project.TrimStart)} to {Ui.FormatDuration(TrimEndSeconds)} selected"
            : "Full clip selected";
        double selectedDuration = Math.Max(0, TrimEndSeconds - _project.TrimStart);
        string resLabel = _project.OutputResolution == "source"
            ? $"{_videoW}\u00d7{_videoH}"
            : _project.OutputResolution;
        ExportSummaryText.Text =
            $"{Ui.FormatDuration(selectedDuration)} selected  ·  {resLabel}";
    }

    private void UpdatePlayheadActionLabels(double seconds)
    {
        string time = Ui.FormatDuration(seconds);
        string start = $"Start at {time}";
        string end = $"End at {time}";
        if (SetStartLabel.Text != start) SetStartLabel.Text = start;
        if (SetEndLabel.Text != end) SetEndLabel.Text = end;
    }

    // ------------------------------------------------------------------ export

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_clip == null || _exporting) return;

        if (_dirty)
        {
            EditProjectStore.Save(_clip.Path, _project);
            _dirty = false;
            DirtyDot.Visibility = Visibility.Hidden;
        }

        double trimEnd = TrimEndSeconds;
        bool hasZoom = _project.Zooms.Any(z =>
            z.End > _project.TrimStart && z.Start < trimEnd);
        bool hasTrim = HasActiveTrim;

        if (!_project.HasBackground && !hasZoom && !hasTrim)
        {
            PlayerStatus.Text = "Nothing to export yet. Trim the clip, add a zoom, or choose a background.";
            return;
        }

        _exporting = true;
        EditorControls.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg => PlayerStatus.Text = msg);
            string outPath = await App.EditRender.ExportAsync(
                _clip.Path, _project, App.Settings.Current, progress);
            PlayerStatus.Text = $"Saved {System.IO.Path.GetFileName(outPath)}";
            ClipsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            PlayerStatus.Text = ex.Message;
            Log.Write("export", ex.ToString());
        }
        finally
        {
            _exporting = false;
            EditorControls.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ transport & misc

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_clip == null || _seeking) return;
        _seekGeneration++;
        if (_playing)
        {
            Media.Pause();
            SetPlayingUi(false);
        }
        else
        {
            double position = Media.Position.TotalSeconds;
            if (_durationSec > 0 &&
                (position < _project.TrimStart - 0.025 || position >= TrimEndSeconds - 0.05))
            {
                Media.Position = TimeSpan.FromSeconds(_project.TrimStart);
                _scrubSeconds = _project.TrimStart;
                _seekPreviewUntilMs = Environment.TickCount64 + 250;
            }
            Media.Play();
            SetPlayingUi(true);
        }
    }

    private void SetPlayingUi(bool playing)
    {
        _playing = playing;
        PlayLabel.Text = playing ? "Pause" : "Play";
        PlayIcon.Data = Geometry.Parse(playing
            ? "M2,1 H6 V13 H2 Z M9,1 H13 V13 H9 Z"
            : "M2,1 L13,7 L2,13 Z");
    }

    private void SeekBy(double seconds)
    {
        if (_clip == null || _seeking) return;
        BeginScrub();
        SeekTo(_scrubSeconds + seconds, force: true);
        FinishScrub();
    }

    private void Vol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media != null && !_muted) Media.Volume = e.NewValue;
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedItem is ComboBoxItem { Tag: double s })
        {
            _speed = s;
            try { Media.SpeedRatio = s; } catch { }
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        try { Media.IsMuted = _muted; } catch { }
        MuteIcon.Data = _muted ? SpeakerMuted : SpeakerOn;
        if (!_muted && Media != null) Media.Volume = Vol.Value;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        var win = Window.GetWindow(this);
        if (on)
        {
            HeaderBar.Visibility = Visibility.Collapsed;
            HeaderRowDef.Height = new GridLength(0);
            EditorControls.Visibility = Visibility.Collapsed;
            EditorRoot.Margin = new Thickness(0);
            PreviewBorder.Margin = new Thickness(0);
            PreviewBorder.CornerRadius = new CornerRadius(0);
            PreviewBorder.BorderThickness = new Thickness(0);
            if (win != null)
            {
                _prevWindowState = win.WindowState;
                _prevWindowStyle = win.WindowStyle;
                _prevResizeMode = win.ResizeMode;
                win.WindowState = WindowState.Normal;
                win.WindowStyle = WindowStyle.None;
                win.ResizeMode = ResizeMode.NoResize;
                win.WindowState = WindowState.Maximized;
            }
        }
        else
        {
            HeaderBar.Visibility = Visibility.Visible;
            HeaderRowDef.Height = new GridLength(44);
            EditorControls.Visibility = Visibility.Visible;
            EditorRoot.Margin = new Thickness(24, 18, 24, 20);
            PreviewBorder.Margin = new Thickness(0, 10, 0, 0);
            PreviewBorder.CornerRadius = new CornerRadius(5);
            PreviewBorder.BorderThickness = new Thickness(1);
            if (win != null)
            {
                win.WindowStyle = _prevWindowStyle;
                win.ResizeMode = _prevResizeMode;
                win.WindowState = _prevWindowState;
            }
        }
        UpdateSceneLayout();
    }

    private void VideoFade_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUi || _clip == null) return;
        _project.VideoFade = VideoFadeCheck.IsChecked == true;
        MarkDirty();
    }

    private void VideoFadeLen_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi || _clip == null) return;
        _project.VideoFadeSeconds = Math.Round(VideoFadeSlider.Value, 2);
        VideoFadeText.Text = $"{_project.VideoFadeSeconds:0.0}s";
        MarkDirty();
    }

    private void AudioFadeLen_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi || _clip == null) return;
        _project.AudioFadeSeconds = Math.Round(AudioFadeSlider.Value, 2);
        AudioFadeText.Text = $"{_project.AudioFadeSeconds:0.0}s";
        MarkDirty();
    }

    private void UpdateFadeTexts()
    {
        VideoFadeText.Text = $"{_project.VideoFadeSeconds:0.0}s";
        AudioFadeText.Text = $"{_project.AudioFadeSeconds:0.0}s";
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        _loop = LoopBtn.IsChecked == true;
        PlayerStatus.Text = _loop ? "Looping the selected range." : "Loop off.";
    }

    private void JumpStart_Click(object sender, RoutedEventArgs e) => SeekAbsolute(_project.TrimStart);

    private void JumpEnd_Click(object sender, RoutedEventArgs e) =>
        SeekAbsolute(Math.Max(_project.TrimStart, TrimEndSeconds - 0.05));

    private void PrevFrame_Click(object sender, RoutedEventArgs e) => SeekBy(-_frameStep);

    private void NextFrame_Click(object sender, RoutedEventArgs e) => SeekBy(_frameStep);

    private void SeekAbsolute(double t)
    {
        if (_clip == null || _seeking) return;
        BeginScrub();
        SeekTo(t, force: true);
        FinishScrub();
    }

    private void Resolution_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || _clip == null) return;
        _project.OutputResolution = (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "source";
        MarkDirty();
        UpdateTrimText();
    }

    private void AudioFade_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUi || _clip == null) return;
        _project.AudioFade = AudioFadeCheck.IsChecked == true;
        MarkDirty();
    }

    private void ShrinkZoom_Click(object sender, RoutedEventArgs e) => AdjustZoomLength(-0.5);
    private void GrowZoom_Click(object sender, RoutedEventArgs e) => AdjustZoomLength(0.5);

    private void AdjustZoomLength(double delta)
    {
        if (_selected == null) return;
        double newEnd = Math.Clamp(_selected.End + delta, _selected.Start + 0.3, _durationSec);
        if (Math.Abs(newEnd - _selected.End) < 0.001) return;
        _selected.End = Math.Round(newEnd, 3);
        MarkDirty();
        UpdateZoomEditor();
        RebuildTimeline();
    }

    private void MarkDirty()
    {
        _dirty = true;
        DirtyDot.Visibility = Visibility.Visible;
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((item.Tag as string) == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Visibility != Visibility.Visible) return;
        if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
        else if (e.Key == Key.Left) { SeekBy(-5); e.Handled = true; }
        else if (e.Key == Key.Right) { SeekBy(5); e.Handled = true; }
        else if (e.Key == Key.Home) { SeekAbsolute(_project.TrimStart); e.Handled = true; }
        else if (e.Key == Key.End) { SeekAbsolute(Math.Max(_project.TrimStart, TrimEndSeconds - 0.05)); e.Handled = true; }
        else if (e.Key == Key.OemComma) { SeekBy(-_frameStep); e.Handled = true; }
        else if (e.Key == Key.OemPeriod) { SeekBy(_frameStep); e.Handled = true; }
        else if (e.Key == Key.OemOpenBrackets) { SetIn_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets) { SetOut_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F) { SetFullscreen(!_fullscreen); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_fullscreen) SetFullscreen(false);
            else if (_zoomPlacementMode) SetZoomPlacementMode(false);
            else if (_selected != null) Select(null);
            else ClosePlayer();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selected != null)
        {
            RemoveZoom_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Backdrop_Click(object sender, MouseButtonEventArgs e) => ClosePlayer();
    private void Panel_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void Close_Click(object sender, RoutedEventArgs e) => ClosePlayer();
}
