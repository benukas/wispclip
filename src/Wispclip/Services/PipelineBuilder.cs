using System.Globalization;
using Wispclip.Models;

namespace Wispclip.Services;

/// <summary>
/// A capture+encode pipeline. Id format is "{input}+{encoder}", e.g. "ddagrab+h264_amf".
///
/// Inputs:
///   ddagrab      – DXGI Desktop Duplication, frames stay on the GPU end-to-end (lowest overhead)
///   ddagrab-cpu  – GPU capture, frames downloaded to system RAM before encoding
///   ddagrab-qsv  – GPU capture mapped onto an Intel QSV device
///   gfxcapture   – Windows.Graphics.Capture, kept on the GPU (newer FFmpeg builds)
///   gdigrab      – legacy GDI capture (last-resort software fallback)
/// </summary>
public record VideoPipeline(
    string Id,
    List<string> InputArgs,
    List<string> EncoderArgs,
    string VideoMap,
    int AudioStartIndex,
    string EncoderName);

public static class PipelineBuilder
{
    public static readonly string[] KnownEncoders =
    {
        "h264_amf", "hevc_amf",
        "av1_amf",
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_mf", "hevc_mf", "av1_mf",
        "libx264", "libx265",
    };

    /// <summary>Candidate pipelines for a codec, best first. The prober tries them in order.</summary>
    public static List<string> Candidates(string codec, ISet<string> encoders,
        bool hasDdagrab, bool hasGfxCapture)
    {
        string amf = codec switch
        {
            "av1" => "av1_amf",
            "hevc" => "hevc_amf",
            _ => "h264_amf",
        };
        string nvenc = codec switch
        {
            "av1" => "av1_nvenc",
            "hevc" => "hevc_nvenc",
            _ => "h264_nvenc",
        };
        string qsv = codec switch
        {
            "av1" => "av1_qsv",
            "hevc" => "hevc_qsv",
            _ => "h264_qsv",
        };
        string mf = codec switch
        {
            "av1" => "av1_mf",
            "hevc" => "hevc_mf",
            _ => "h264_mf",
        };
        string? soft = codec switch
        {
            "hevc" => "libx265",
            "h264" => "libx264",
            _ => null,
        };

        var list = new List<string>();

        void AddZeroCopyFamily(string gpuInput, string qsvInput)
        {
            if (encoders.Contains(amf)) list.Add($"{gpuInput}+{amf}");
            if (encoders.Contains(nvenc)) list.Add($"{gpuInput}+{nvenc}");
            if (encoders.Contains(qsv)) list.Add($"{qsvInput}+{qsv}");
        }

        void AddFallbackFamily(string cpuInput)
        {
            if (encoders.Contains(amf)) list.Add($"{cpuInput}+{amf}");
            if (encoders.Contains(nvenc)) list.Add($"{cpuInput}+{nvenc}");
            if (encoders.Contains(qsv)) list.Add($"{cpuInput}+{qsv}");
            if (encoders.Contains(mf)) list.Add($"{cpuInput}+{mf}");
            if (soft != null && encoders.Contains(soft)) list.Add($"{cpuInput}+{soft}");
        }

        // gfxcapture (Windows.Graphics.Capture) is preferred over ddagrab (DXGI Desktop
        // Duplication): both are zero-copy, but duplication loses access on desktop state
        // changes (DXGI_ERROR_ACCESS_LOST) and the resulting restarts tank in-game frame
        // pacing — benchmarked ~11% better 1% lows with gfxcapture on real hardware.
        if (hasGfxCapture)
            AddZeroCopyFamily("gfxcapture", "gfxcapture-qsv");
        if (hasDdagrab)
            AddZeroCopyFamily("ddagrab", "ddagrab-qsv");
        if (hasGfxCapture)
            AddFallbackFamily("gfxcapture-cpu");
        if (hasDdagrab)
            AddFallbackFamily("ddagrab-cpu");

        if (encoders.Contains(mf)) list.Add($"gdigrab+{mf}");
        if (soft != null && encoders.Contains(soft)) list.Add($"gdigrab+{soft}");
        return list;
    }

    /// <summary>
    /// Encoder arguments for a given quality target, also reused by the edit exporter.
    /// performanceMode picks the fastest presets (lowest encode-side load) at the same quality
    /// target; exports leave it off since they don't compete with a running game.
    /// </summary>
    public static List<string> EncoderArgsFor(string enc, int quality, bool performanceMode = false)
    {
        string qs = Math.Clamp(quality, 10, 40).ToString(CultureInfo.InvariantCulture);
        string amfQuality = performanceMode ? "speed" : "balanced";
        string nvPreset = performanceMode ? "p1" : "p4";
        string nvTune = performanceMode ? "ll" : "hq";
        string x26xPreset = performanceMode ? "ultrafast" : "superfast";
        return enc switch
        {
            "h264_amf" or "hevc_amf" => new()
                { "-c:v", enc, "-quality", amfQuality, "-rc", "cqp", "-qp_i", qs, "-qp_p", qs, "-bf", "0" },
            "av1_amf" => new()
                { "-c:v", enc, "-quality", amfQuality, "-rc", "cqp", "-qp_i", qs, "-qp_p", qs },
            "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" => new()
                { "-c:v", enc, "-preset", nvPreset, "-tune", nvTune, "-multipass", "disabled", "-rc", "vbr", "-cq", qs, "-b:v", "0" },
            "h264_qsv" or "hevc_qsv" or "av1_qsv" => new()
                { "-c:v", enc, "-preset", "veryfast", "-global_quality", qs },
            "h264_mf" or "hevc_mf" or "av1_mf" => new()
                {
                    "-c:v", enc,
                    "-hw_encoding", "1",
                    "-rate_control", "quality",
                    "-quality", Math.Clamp(100 - (quality - 15) * 3, 40, 95).ToString(CultureInfo.InvariantCulture),
                },
            "libx264" => new() { "-c:v", "libx264", "-preset", x26xPreset, "-crf", qs },
            "libx265" => new() { "-c:v", "libx265", "-preset", x26xPreset, "-crf", qs },
            _ => throw new ArgumentException($"Unknown encoder: {enc}"),
        };
    }

    public static bool IsHardware(string pipelineId) =>
        !pipelineId.EndsWith("libx264") && !pipelineId.EndsWith("libx265");

    public static string Describe(string pipelineId)
    {
        var parts = pipelineId.Split('+');
        string input = parts[0] switch
        {
            "ddagrab" => "GPU capture (zero-copy)",
            "ddagrab-cpu" => "GPU capture + CPU download",
            "ddagrab-qsv" => "GPU capture (QSV mapped)",
            "gfxcapture" => "Windows GPU capture (zero-copy)",
            "gfxcapture-cpu" => "Windows GPU capture + CPU download",
            "gfxcapture-qsv" => "Windows GPU capture (QSV mapped)",
            "gdigrab" => "GDI capture (software)",
            _ => parts[0],
        };
        string enc = parts[1] switch
        {
            "h264_amf" or "hevc_amf" or "av1_amf" => $"{parts[1]} (AMD hardware)",
            "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" => $"{parts[1]} (NVIDIA hardware)",
            "h264_qsv" or "hevc_qsv" or "av1_qsv" => $"{parts[1]} (Intel hardware)",
            "h264_mf" or "hevc_mf" or "av1_mf" => $"{parts[1]} (Windows hardware)",
            _ => $"{parts[1]} (CPU)",
        };
        return $"{input} → {enc}";
    }

    public static VideoPipeline Build(string pipelineId, AppSettings s)
    {
        var parts = pipelineId.Split('+');
        if (parts.Length != 2) throw new ArgumentException($"Bad pipeline id: {pipelineId}");
        string input = parts[0], enc = parts[1];

        int fps = Math.Clamp(s.Video.Fps, 10, 240);
        int mon = Math.Max(0, s.Video.MonitorIndex);
        var inv = CultureInfo.InvariantCulture;

        List<string> inputArgs;
        string map;
        int audioStart;

        string grab = string.Create(inv, $"ddagrab=output_idx={mon}:framerate={fps}");
        string gfx = string.Create(inv, $"gfxcapture=monitor_idx={mon}:max_framerate={fps},fps={fps}");
        switch (input)
        {
            case "ddagrab":
                inputArgs = new() { "-init_hw_device", "d3d11va", "-filter_complex", $"{grab}[v]" };
                map = "[v]"; audioStart = 0;
                break;
            case "ddagrab-cpu":
            {
                string swfmt = enc.StartsWith("lib") ? "yuv420p" : "nv12";
                inputArgs = new() { "-init_hw_device", "d3d11va", "-filter_complex", $"{grab},hwdownload,format=bgra,format={swfmt}[v]" };
                map = "[v]"; audioStart = 0;
                break;
            }
            case "ddagrab-qsv":
                inputArgs = new() { "-init_hw_device", "d3d11va", "-filter_complex", $"{grab},hwmap=derive_device=qsv,format=qsv[v]" };
                map = "[v]"; audioStart = 0;
                break;
            case "gfxcapture":
                inputArgs = new() { "-init_hw_device", "d3d11va", "-filter_complex", $"{gfx}[v]" };
                map = "[v]"; audioStart = 0;
                break;
            case "gfxcapture-cpu":
            {
                string swfmt = enc.StartsWith("lib") ? "yuv420p" : "nv12";
                inputArgs = new() { "-init_hw_device", "d3d11va", "-filter_complex", $"{gfx},hwdownload,format=bgra,format={swfmt}[v]" };
                map = "[v]"; audioStart = 0;
                break;
            }
            case "gfxcapture-qsv":
                inputArgs = new() { "-init_hw_device", "d3d11va", "-filter_complex", $"{gfx},hwmap=derive_device=qsv,format=qsv[v]" };
                map = "[v]"; audioStart = 0;
                break;
            case "gdigrab":
                inputArgs = new() { "-f", "gdigrab", "-framerate", fps.ToString(inv), "-i", "desktop" };
                map = "0:v"; audioStart = 1;
                break;
            default:
                throw new ArgumentException($"Unknown input: {input}");
        }

        List<string> encArgs = EncoderArgsFor(enc, s.Video.Quality, s.Video.PerformanceMode);

        if (input == "gdigrab")
            encArgs.AddRange(new[] { "-pix_fmt", enc.EndsWith("_mf") ? "nv12" : "yuv420p" });

        // keyframe every replay segment so the segment muxer splits exactly on time
        int segmentSeconds = Math.Max(1, s.Replay.SegmentSeconds);
        int gop = fps * segmentSeconds;
        encArgs.AddRange(new[]
        {
            "-g", gop.ToString(inv),
            "-force_key_frames", $"expr:gte(t,n_forced*{segmentSeconds})",
            "-fps_mode", "cfr",
        });

        return new VideoPipeline(pipelineId, inputArgs, encArgs, map, audioStart, enc);
    }
}
