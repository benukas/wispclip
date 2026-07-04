using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wispclip.Services;

namespace Wispclip.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Fired after settings are saved. Argument: the capture pipeline must be re-probed.</summary>
    public event Action<bool>? Applied;

    private bool _loaded;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            LoadFromSettings();
            RefreshEngineInfo();
        };
    }

    public void LoadFromSettings()
    {
        var s = App.Settings.Current;

        MonitorCombo.Items.Clear();
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Display {i} | {b.Width}x{b.Height}{(screens[i].Primary ? " (primary)" : "")}",
                Tag = i.ToString(),
            });
        }
        if (MonitorCombo.Items.Count == 0)
            MonitorCombo.Items.Add(new ComboBoxItem { Content = "Display 0", Tag = "0" });
        SelectByTag(MonitorCombo, s.Video.MonitorIndex.ToString());

        SelectByTag(FpsCombo, s.Video.Fps.ToString());
        SelectByTag(CodecCombo, s.Video.Codec);
        QualitySlider.Value = s.Video.Quality;
        QualityLabel.Text = $"CQ {s.Video.Quality}";

        SelectByTag(ReplayLenCombo, s.Replay.DurationSeconds.ToString());
        AutoStartCheck.IsChecked = s.Replay.AutoStart;
        LaunchWithWindowsCheck.IsChecked = s.LaunchAtWindowsStartup;
        CloseToTrayCheck.IsChecked = s.CloseToTray;

        SysAudioCheck.IsChecked = s.Audio.CaptureSystemAudio;
        MicCheck.IsChecked = s.Audio.CaptureMicrophone;
        DelayBox.Text = s.Audio.AudioDelayMs.ToString();

        MicCombo.Items.Clear();
        MicCombo.Items.Add(new ComboBoxItem { Content = "Default microphone", Tag = null });
        foreach (var (id, name) in AudioPipeSource.ListMicrophones())
            MicCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        SelectByTag(MicCombo, s.Audio.MicrophoneDeviceId);

        OutDirBox.Text = s.OutputDirectory;
        OverlayCheck.IsChecked = s.OverlayToasts;
        FfmpegDirBox.Text = s.FfmpegDirectory ?? "";

        HkSaveBox.Text = s.Hotkeys.SaveReplay;
        HkRecordBox.Text = s.Hotkeys.ToggleRecording;
        HkBufferBox.Text = s.Hotkeys.ToggleReplayBuffer;

        ApplyStatus.Text = "";
    }

    public void RefreshEngineInfo()
    {
        var ff = App.Ffmpeg;
        FfmpegStatusText.Text = ff != null
            ? $"Found: {ff.Ffmpeg}"
            : "Not found. Download FFmpeg from https://ffmpeg.org/download.html, add it to PATH, " +
              "or set the folder below (must contain ffmpeg.exe and ffprobe.exe).";

        var id = App.Settings.Current.Video.PipelineId;
        PipelineText.Text = id != null
            ? $"{PipelineBuilder.Describe(id)} | {id}"
            : "Not detected yet. Detection runs automatically on startup, or select Detect best pipeline.";
    }

    // ------------------------------------------------------------------ handlers

    private void Quality_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityLabel != null) QualityLabel.Text = $"CQ {(int)e.NewValue}";
    }

    private void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        ((TextBox)sender).Text = string.Join("+", parts);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where clips are saved",
            SelectedPath = Directory.Exists(OutDirBox.Text) ? OutDirBox.Text : "",
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutDirBox.Text = dialog.SelectedPath;
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Folder containing ffmpeg.exe and ffprobe.exe",
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            FfmpegDirBox.Text = dialog.SelectedPath;
    }

    private void Redetect_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.Video.PipelineId = null;
        App.Settings.Save();
        RefreshEngineInfo();
        Applied?.Invoke(true);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Log.LogFile))
            Process.Start(new ProcessStartInfo(Log.LogFile) { UseShellExecute = true });
        else
            ApplyStatus.Text = "No log file yet.";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings.Current;

        // validate before mutating anything
        foreach (var (box, label) in new[]
        {
            (HkSaveBox, "Save replay"), (HkRecordBox, "Start/stop recording"), (HkBufferBox, "Toggle replay buffer"),
        })
        {
            if (!HotkeyService.TryParse(box.Text, out _, out _))
            {
                ApplyStatus.Text = $"Invalid hotkey for \"{label}\".";
                return;
            }
        }

        string outDir = OutDirBox.Text.Trim();
        try { Directory.CreateDirectory(outDir); }
        catch
        {
            ApplyStatus.Text = "The clips folder path is not valid.";
            return;
        }

        if (!int.TryParse(DelayBox.Text.Trim(), out int delayMs)) delayMs = 0;

        bool launchWithWindows = LaunchWithWindowsCheck.IsChecked == true;
        if (!StartupService.TrySync(launchWithWindows, out var startupError))
        {
            ApplyStatus.Text = $"Windows startup could not be updated: {startupError}";
            return;
        }

        bool needsReprobe = false;
        string codec = TagOf(CodecCombo) ?? "h264";
        if (codec != s.Video.Codec)
        {
            s.Video.Codec = codec;
            s.Video.PipelineId = null;
            needsReprobe = true;
        }

        string? ffDir = string.IsNullOrWhiteSpace(FfmpegDirBox.Text) ? null : FfmpegDirBox.Text.Trim();
        if (ffDir != s.FfmpegDirectory)
        {
            s.FfmpegDirectory = ffDir;
            s.Video.PipelineId = null;
            needsReprobe = true;
        }

        s.Video.MonitorIndex = int.TryParse(TagOf(MonitorCombo), out int mon) ? mon : 0;
        s.Video.Fps = int.TryParse(TagOf(FpsCombo), out int fps) ? fps : 60;
        s.Video.Quality = (int)QualitySlider.Value;
        s.Replay.DurationSeconds = int.TryParse(TagOf(ReplayLenCombo), out int len) ? len : 60;
        s.Replay.AutoStart = AutoStartCheck.IsChecked == true;
        s.LaunchAtWindowsStartup = launchWithWindows;
        s.CloseToTray = CloseToTrayCheck.IsChecked == true;
        s.Audio.CaptureSystemAudio = SysAudioCheck.IsChecked == true;
        s.Audio.CaptureMicrophone = MicCheck.IsChecked == true;
        s.Audio.MicrophoneDeviceId = TagOf(MicCombo);
        s.Audio.AudioDelayMs = delayMs;
        s.OutputDirectory = outDir;
        s.OverlayToasts = OverlayCheck.IsChecked == true;
        s.Hotkeys.SaveReplay = HkSaveBox.Text;
        s.Hotkeys.ToggleRecording = HkRecordBox.Text;
        s.Hotkeys.ToggleReplayBuffer = HkBufferBox.Text;

        App.Settings.Save();
        ApplyStatus.Text = "Settings applied.";
        RefreshEngineInfo();
        Applied?.Invoke(needsReprobe);
    }

    // ------------------------------------------------------------------ helpers

    private static string? TagOf(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void SelectByTag(ComboBox combo, string? tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((item.Tag as string) == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }
}
