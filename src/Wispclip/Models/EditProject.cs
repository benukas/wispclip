namespace Wispclip.Models;

/// <summary>Per-clip edit settings, stored as a sidecar "&lt;clip&gt;.wispclip.json" next to the mp4.</summary>
public class EditProject
{
    public double TrimStart { get; set; }

    /// <summary>Zero means the original end of the clip.</summary>
    public double TrimEnd { get; set; }

    public BackgroundSettings Background { get; set; } = new();
    public List<ZoomSegment> Zooms { get; set; } = new();

    /// <summary>Output size preset: "source", "1080p", or "720p". Preserves aspect ratio.</summary>
    public string OutputResolution { get; set; } = "source";

    /// <summary>Fade the audio in at the start and out at the end of the exported clip.</summary>
    public bool AudioFade { get; set; }

    /// <summary>Fade the video from/to black at the start and end of the exported clip.</summary>
    public bool VideoFade { get; set; }

    /// <summary>Audio fade in/out length in seconds.</summary>
    public double AudioFadeSeconds { get; set; } = 0.35;

    /// <summary>Video fade in/out length in seconds.</summary>
    public double VideoFadeSeconds { get; set; } = 0.35;

    public bool HasBackground => Background.Style != "none";
    public bool HasEdits(double duration) =>
        HasBackground || Zooms.Count > 0 || TrimStart > 0.01 ||
        (TrimEnd > 0.01 && TrimEnd < duration - 0.01) ||
        OutputResolution != "source" || AudioFade || VideoFade;
}

public class BackgroundSettings
{
    /// <summary>"none", "blur", or "gradient:{presetKey}".</summary>
    public string Style { get; set; } = "none";

    /// <summary>Scene padding around the video card, percent of frame (0–20).</summary>
    public double PaddingPercent { get; set; } = 7;

    /// <summary>Corner radius of the video card in output pixels.</summary>
    public double CornerRadius { get; set; } = 22;

    public double ShadowOpacity { get; set; } = 0.55;
}

public class ZoomSegment
{
    public double Start { get; set; }
    public double End { get; set; }

    /// <summary>Zoom factor while held (1.2–4).</summary>
    public double Level { get; set; } = 2.0;

    /// <summary>Focus point in video-relative coordinates (0–1).</summary>
    public double FocusX { get; set; } = 0.5;
    public double FocusY { get; set; } = 0.5;

    /// <summary>Ease in/out duration in seconds.</summary>
    public double RampSeconds { get; set; } = 0.6;
}
