using System.IO;

namespace Wispclip.Models;

public class AppSettings
{
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Wispclip");

    /// <summary>Directory containing ffmpeg.exe / ffprobe.exe. Null = auto-detect.</summary>
    public string? FfmpegDirectory { get; set; }

    public bool CloseToTray { get; set; } = true;

    /// <summary>Launch quietly at Windows sign-in and arm the replay buffer.</summary>
    public bool LaunchAtWindowsStartup { get; set; } = true;

    /// <summary>Show the in-game overlay toast when clips are saved / recording starts.</summary>
    public bool OverlayToasts { get; set; } = true;

    public VideoSettings Video { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public ReplaySettings Replay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
}

public class VideoSettings
{
    public int Fps { get; set; } = 60;

    /// <summary>"h264", "hevc", or "av1".</summary>
    public string Codec { get; set; } = "h264";

    /// <summary>Constant-quality value (CQP/CRF-style). Lower = better quality, bigger files.</summary>
    public int Quality { get; set; } = 22;

    /// <summary>
    /// Use the fastest encoder presets to minimize GPU load while capturing, trading some
    /// compression efficiency (larger files at the same CQ). The quality target itself is
    /// unchanged; only how hard the encoder works to hit it.
    /// </summary>
    public bool PerformanceMode { get; set; } = false;

    /// <summary>ddagrab output index (roughly matches display order).</summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>Probed capture+encode pipeline id, e.g. "ddagrab+h264_amf". Null = not probed yet.</summary>
    public string? PipelineId { get; set; }
}

public class AudioSettings
{
    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; } = false;

    /// <summary>WASAPI device id for the microphone. Null = default capture device.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>Manual A/V calibration in milliseconds; positive delays audio.</summary>
    public int AudioDelayMs { get; set; } = 0;
}

public class ReplaySettings
{
    public bool AutoStart { get; set; } = true;
    public int DurationSeconds { get; set; } = 60;
    public int SegmentSeconds { get; set; } = 2;
}

public class HotkeySettings
{
    public string SaveReplay { get; set; } = "Ctrl+Alt+S";
    public string ToggleRecording { get; set; } = "Ctrl+Alt+R";
    public string ToggleReplayBuffer { get; set; } = "Ctrl+Alt+B";
}
