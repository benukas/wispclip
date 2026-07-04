using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Wispclip.Models;

namespace Wispclip.Services;

public record ClipMeta(int Width, int Height, double Duration, double Fps, bool HasAudio);

/// <summary>
/// Renders an EditProject to a new mp4. The scene (background, shadow, rounded video card)
/// is composed in an ffmpeg filter graph; the static assets (mask, shadow, gradient) are
/// rendered by WPF at export time so the preview and the export share one look. Zoom uses
/// zoompan with expressions from ZoomMath, using the same curves the preview evaluates in C#.
/// </summary>
public class EditRenderService
{
    private readonly Func<FfmpegPaths?> _ffmpeg;

    public EditRenderService(Func<FfmpegPaths?> ffmpegResolver) => _ffmpeg = ffmpegResolver;

    // ------------------------------------------------------------------ presets

    /// <summary>Gradient backdrop presets: key → (display name, top-left color, bottom-right color).</summary>
    public static readonly (string Key, string Name, string C0, string C1)[] GradientPresets =
    {
        ("graphite", "Graphite", "#23272E", "#0D0F13"),
        ("ocean",    "Ocean",    "#1E4258", "#0C1622"),
        ("dusk",     "Dusk",     "#4A3B63", "#191524"),
        ("forest",   "Forest",   "#23483C", "#0C1512"),
        ("ember",    "Ember",    "#5A3038", "#190F11"),
        ("sand",     "Sand",     "#55503F", "#161410"),
    };

    // ------------------------------------------------------------------ probing

    public async Task<ClipMeta> ProbeAsync(string clipPath)
    {
        var ff = _ffmpeg() ?? throw new InvalidOperationException("ffmpeg not found.");
        var res = await ProcessRunner.RunAsync(ff.Ffprobe, new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height,avg_frame_rate",
            "-show_entries", "format=duration",
            "-of", "json", clipPath,
        }, 20000);
        if (!res.Success) throw new InvalidOperationException("Could not read the clip.");

        using var doc = JsonDocument.Parse(res.StdOut);
        var stream = doc.RootElement.GetProperty("streams")[0];
        int w = stream.GetProperty("width").GetInt32();
        int h = stream.GetProperty("height").GetInt32();
        double fps = ParseRate(stream.GetProperty("avg_frame_rate").GetString() ?? "60/1");
        double dur = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString() ?? "0",
            CultureInfo.InvariantCulture);
        bool hasAudio = await HasAudioAsync(ff, clipPath);
        return new ClipMeta(w, h, dur, fps, hasAudio);
    }

    private static async Task<bool> HasAudioAsync(FfmpegPaths ff, string clipPath)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(ff.Ffprobe, new[]
            {
                "-v", "error",
                "-select_streams", "a",
                "-show_entries", "stream=index",
                "-of", "csv=p=0", clipPath,
            }, 15000);
            return res.Success && !string.IsNullOrWhiteSpace(res.StdOut);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Target output size for a resolution preset, preserving aspect and never upscaling.</summary>
    private static (int W, int H) ResolveOutputSize(int w, int h, string resolution)
    {
        int target = resolution switch { "1080p" => 1080, "720p" => 720, _ => 0 };
        if (target == 0) return (w, h);

        int nw, nh;
        if (w >= h)
        {
            nh = Math.Min(h, target);
            nw = (int)Math.Round(nh * (double)w / h);
        }
        else
        {
            nw = Math.Min(w, target);
            nh = (int)Math.Round(nw * (double)h / w);
        }
        nw &= ~1; nh &= ~1;
        return (Math.Max(2, nw), Math.Max(2, nh));
    }

    private static double ParseRate(string rate)
    {
        var parts = rate.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return Math.Clamp(n / d, 10, 240);
        return 60;
    }

    /// <summary>Blurred still used by the editor to approximate the "blur" backdrop live.</summary>
    public async Task<string?> GenerateBlurPreviewAsync(string clipPath)
    {
        var ff = _ffmpeg();
        if (ff == null) return null;
        string outPath = Path.Combine(Path.GetTempPath(),
            $"wispclip_blurprev_{Math.Abs(clipPath.GetHashCode())}.jpg");
        if (File.Exists(outPath)) return outPath;
        var res = await ProcessRunner.RunAsync(ff.Ffmpeg, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", "1", "-i", clipPath, "-frames:v", "1",
            "-vf", "scale=640:-2,gblur=sigma=18,eq=brightness=-0.08",
            "-q:v", "5", outPath,
        }, 20000);
        return res.Success && File.Exists(outPath) ? outPath : null;
    }

    // ------------------------------------------------------------------ export

    /// <summary>Must be called from the UI thread (WPF renders the overlay assets).</summary>
    public async Task<string> ExportAsync(string clipPath, EditProject project, AppSettings settings,
        IProgress<string>? progress = null)
    {
        var ff = _ffmpeg() ?? throw new InvalidOperationException("ffmpeg not found.");
        progress?.Report("Analyzing clip...");
        var meta = await ProbeAsync(clipPath);

        int W = meta.Width & ~1, H = meta.Height & ~1;
        double fps = meta.Fps;
        var inv = CultureInfo.InvariantCulture;

        var (outW, outH) = ResolveOutputSize(W, H, project.OutputResolution);
        string scaleFilter = (outW == W && outH == H)
            ? ""
            : $"scale={outW}:{outH}:flags=lanczos,";

        double trimStart = Math.Clamp(project.TrimStart, 0, Math.Max(0, meta.Duration - 0.1));
        double trimEnd = project.TrimEnd > 0.01
            ? Math.Clamp(project.TrimEnd, trimStart + 0.1, meta.Duration)
            : meta.Duration;
        double outDur = trimEnd - trimStart;

        bool hasBg = project.HasBackground;
        var zooms = project.Zooms
            .Where(z => z.End > trimStart && z.Start < trimEnd && z.End > z.Start + 0.15)
            .OrderBy(z => z.Start).ToList();

        // card geometry: video scaled by (1 - 2p), centered
        double p = Math.Clamp(project.Background.PaddingPercent, 0, 20) / 100.0;
        int cw = hasBg ? (int)(W * (1 - 2 * p)) & ~1 : W;
        int ch = hasBg ? (int)(H * (1 - 2 * p)) & ~1 : H;
        int px = (W - cw) / 2, py = (H - ch) / 2;
        double radius = Math.Clamp(project.Background.CornerRadius, 0, Math.Min(cw, ch) / 2.0);

        string tempDir = Path.Combine(Path.GetTempPath(), $"wispclip_edit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // ---- inputs ----
            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };

            // Looped image inputs (mask/shadow/backdrop) default to 25 fps. If left at 25,
            // the final overlay adopts the background's rate and decimates the video to 25 fps
            // while the stream is still tagged at the source rate — the clip then plays too
            // fast and freezes. Pin the images to the clip's fps so nothing is decimated.
            string fpsInput = fps.ToString("0.###", inv);
            void AddImageInput(string path)
            {
                args.AddRange(new[] { "-loop", "1", "-framerate", fpsInput, "-t", outDur.ToString("0.###", inv), "-i", path });
            }

            if (trimStart > 0.005)
                args.AddRange(new[] { "-ss", trimStart.ToString("0.###", inv) });
            args.AddRange(new[] { "-t", outDur.ToString("0.###", inv), "-i", clipPath });

            string bgStyle = project.Background.Style;
            if (hasBg)
            {
                progress?.Report("Rendering backdrop assets...");
                string maskPath = Path.Combine(tempDir, "mask.png");
                string shadowPath = Path.Combine(tempDir, "shadow.png");
                RenderMaskPng(maskPath, cw, ch, radius);
                RenderShadowPng(shadowPath, W, H, cw, ch, radius, project.Background.ShadowOpacity);
                AddImageInput(maskPath);   // input 1
                AddImageInput(shadowPath); // input 2
                if (bgStyle.StartsWith("gradient:"))
                {
                    string bgPath = Path.Combine(tempDir, "bg.png");
                    RenderGradientPng(bgPath, W, H, bgStyle["gradient:".Length..]);
                    AddImageInput(bgPath); // input 3
                }
            }

            // ---- filter graph ----
            var g = new StringBuilder();
            string fpsStr = fps.ToString("0.###", inv);
            string scene;

            if (!hasBg)
            {
                g.Append($"[0:v]fps={fpsStr}[scene];");
                scene = "[scene]";
            }
            else
            {
                bool blur = bgStyle == "blur";
                if (blur)
                {
                    g.Append($"[0:v]fps={fpsStr},split=2[v0][v1];");
                    g.Append($"[v1]scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},");
                    g.Append("gblur=sigma=36,eq=brightness=-0.08[bg];");
                }
                else
                {
                    g.Append($"[0:v]fps={fpsStr}[v0];");
                    g.Append($"[3:v]scale={W}:{H}[bg];");
                }
                g.Append($"[v0]scale={cw}:{ch}:flags=lanczos,format=rgba[vid];");
                g.Append("[1:v]format=gray[mg];");
                g.Append("[vid][mg]alphamerge[vidA];");
                g.Append("[bg][2:v]overlay=0:0[b1];");
                g.Append($"[b1][vidA]overlay={px}:{py}[scene];");
                scene = "[scene]";
            }

            string encName = ResolveEncoder(settings);
            string swFmt = encName.StartsWith("lib") ? "yuv420p" : "nv12";

            // Optional video fade from/to black.
            string videoFade = "";
            if (project.VideoFade)
            {
                double vf = Math.Clamp(Math.Min(project.VideoFadeSeconds, outDur / 2 - 0.02), 0.05, 3.0);
                double vfOut = Math.Max(0, outDur - vf);
                videoFade = string.Create(inv,
                    $"fade=t=in:st=0:d={vf:0.###},fade=t=out:st={vfOut:0.###}:d={vf:0.###},");
            }

            if (zooms.Count > 0)
            {
                int up = Math.Clamp((int)Math.Ceiling(zooms.Max(z => z.Level)), 2, 3);
                var (zx, xx, yx) = ZoomMath.BuildExpressions(zooms, fps, trimStart);
                g.Append($"{scene}scale={W * up}:{H * up}:flags=lanczos,");
                g.Append($"zoompan=z='{zx}':x='{xx}':y='{yx}':d=1:s={W}x{H}:fps={fpsStr},");
                g.Append($"{scaleFilter}{videoFade}format={swFmt}[vout]");
            }
            else
            {
                g.Append($"{scene}{scaleFilter}{videoFade}format={swFmt}[vout]");
            }

            // Optional audio fade in/out. Only wire it when the source actually has audio,
            // otherwise the filtergraph would reference a missing [0:a] and fail.
            bool fadeAudio = project.AudioFade && meta.HasAudio;
            if (fadeAudio)
            {
                double fade = Math.Clamp(Math.Min(project.AudioFadeSeconds, outDur / 2 - 0.02), 0.05, 3.0);
                double fadeOutStart = Math.Max(0, outDur - fade);
                g.Append(string.Create(inv,
                    $";[0:a]afade=t=in:st=0:d={fade:0.###},afade=t=out:st={fadeOutStart:0.###}:d={fade:0.###}[aout]"));
            }

            string scriptPath = Path.Combine(tempDir, "filter.txt");
            File.WriteAllText(scriptPath, g.ToString());

            // ---- encode ----
            args.AddRange(new[] { "-filter_complex_script", scriptPath });
            args.Add("-map");
            args.Add("[vout]");
            args.Add("-map");
            args.Add(fadeAudio ? "[aout]" : "0:a?");
            args.AddRange(PipelineBuilder.EncoderArgsFor(encName, settings.Video.Quality));
            if (encName.Contains("hevc") || encName.Contains("265"))
                args.AddRange(new[] { "-tag:v", "hvc1" });
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });

            string outPath = UniquePath(Path.Combine(
                Path.GetDirectoryName(clipPath)!,
                Path.GetFileNameWithoutExtension(clipPath) + " [edit].mp4"));
            args.AddRange(new[] { "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", outPath });

            progress?.Report("Rendering video...");
            Log.Write("edit", $"export: {string.Join(" ", args)}");
            Log.Write("edit", $"filter: {g}");
            var res = await ProcessRunner.RunAsync(ff.Ffmpeg, args, 30 * 60 * 1000);
            if (!res.Success || !File.Exists(outPath))
                throw new InvalidOperationException($"Render failed: {Tail(res.StdErr, 400)}");
            return outPath;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string ResolveEncoder(AppSettings settings)
    {
        var id = settings.Video.PipelineId;
        if (id != null && id.Contains('+')) return id.Split('+')[1];
        return "libx264";
    }

    // ------------------------------------------------------------------ WPF-rendered assets

    private static void RenderMaskPng(string path, int w, int h, double radius)
    {
        var root = new Grid { Width = w, Height = h, Background = Brushes.Black };
        root.Children.Add(new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(radius),
        });
        SavePng(root, w, h, path);
    }

    private static void RenderShadowPng(string path, int w, int h, int cw, int ch, double radius, double opacity)
    {
        var root = new Grid { Width = w, Height = h, Background = Brushes.Transparent };
        root.Children.Add(new Border
        {
            Width = cw,
            Height = ch,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(radius),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = Math.Max(24, w * 0.03),
                ShadowDepth = Math.Max(6, h * 0.012),
                Direction = 270,
                Opacity = Math.Clamp(opacity, 0, 1),
            },
        });
        SavePng(root, w, h, path);
    }

    private static void RenderGradientPng(string path, int w, int h, string presetKey)
    {
        var preset = GradientPresets.FirstOrDefault(g => g.Key == presetKey);
        if (preset.Key == null) preset = GradientPresets[0];

        var root = new Grid { Width = w, Height = h };
        root.Children.Add(new Border { Background = MakeGradientBrush(preset.C0, preset.C1) });
        // subtle vignette keeps the card the focal point
        root.Children.Add(new Border
        {
            Background = new RadialGradientBrush(
                Color.FromArgb(0, 0, 0, 0), Color.FromArgb(70, 0, 0, 0))
            { RadiusX = 0.85, RadiusY = 0.85 },
        });
        SavePng(root, w, h, path);
    }

    public static LinearGradientBrush MakeGradientBrush(string c0, string c1) =>
        new((Color)ColorConverter.ConvertFromString(c0)!,
            (Color)ColorConverter.ConvertFromString(c1)!,
            new Point(0, 0), new Point(1, 1));

    private static void SavePng(FrameworkElement visual, int w, int h, string path)
    {
        visual.Measure(new Size(w, h));
        visual.Arrange(new Rect(0, 0, w, h));
        visual.UpdateLayout();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string Tail(string s, int n) => s.Length <= n ? s.Trim() : s[^n..].Trim();
}
