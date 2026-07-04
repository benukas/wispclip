using System.Globalization;
using System.Text;
using Wispclip.Models;

namespace Wispclip.Services;

/// <summary>
/// The zoom animation model, implemented twice from the same math:
/// evaluated in C# for the live editor preview, and emitted as ffmpeg zoompan
/// expressions for export — so what you see is what renders.
///
/// Each segment contributes a smoothstep ramp: ease in over RampSeconds, hold,
/// ease out over RampSeconds. Zoom level and focus point are blended by the
/// same ramp, which also produces a smooth pan.
/// </summary>
public static class ZoomMath
{
    public static double Ramp(ZoomSegment z, double t)
    {
        double r = EffectiveRamp(z);
        double up = Smooth(Clamp01((t - z.Start) / r));
        double down = Smooth(Clamp01((t - (z.End - r)) / r));
        return up - down;
    }

    public static (double Zoom, double Fx, double Fy) Evaluate(IReadOnlyList<ZoomSegment> segments, double t)
    {
        double zoom = 1, fx = 0.5, fy = 0.5;
        foreach (var s in segments)
        {
            double f = Ramp(s, t);
            if (f <= 0) continue;
            zoom += (s.Level - 1) * f;
            fx += (s.FocusX - 0.5) * f;
            fy += (s.FocusY - 0.5) * f;
        }
        return (Math.Clamp(zoom, 1, 10), Clamp01(fx), Clamp01(fy));
    }

    private static double EffectiveRamp(ZoomSegment z) =>
        Math.Max(0.05, Math.Min(z.RampSeconds, (z.End - z.Start) / 2));

    private static double Smooth(double u) => u * u * (3 - 2 * u);
    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    // ------------------------------------------------------------------ ffmpeg expressions

    /// <summary>
    /// zoompan z/x/y expressions. Time inside zoompan is frame-based: T = in/fps + tOffset
    /// (tOffset re-aligns to the original clip timeline when the input is trimmed).
    /// </summary>
    public static (string Z, string X, string Y) BuildExpressions(
        IReadOnlyList<ZoomSegment> segments, double fps, double tOffset)
    {
        var inv = CultureInfo.InvariantCulture;
        string T = string.Create(inv, $"(in/{fps:0.###}+{tOffset:0.###})");

        var zTerms = new StringBuilder("1");
        var fxTerms = new StringBuilder("0.5");
        var fyTerms = new StringBuilder("0.5");

        foreach (var s in segments)
        {
            double r = EffectiveRamp(s);
            string f = RampExpr(T, s.Start, s.End, r);
            zTerms.Append(string.Create(inv, $"+{s.Level - 1:0.####}*{f}"));
            fxTerms.Append(string.Create(inv, $"+{s.FocusX - 0.5:0.####}*{f}"));
            fyTerms.Append(string.Create(inv, $"+{s.FocusY - 0.5:0.####}*{f}"));
        }

        // zoompan exposes the evaluated zoom as 'zoom' inside the x/y expressions
        string z = $"min(max({zTerms},1),10)";
        string x = $"min(max(({fxTerms})*iw-(iw/zoom)/2,0),iw-iw/zoom)";
        string y = $"min(max(({fyTerms})*ih-(ih/zoom)/2,0),ih-ih/zoom)";
        return (z, x, y);
    }

    /// <summary>smoothstep((T-start)/r) - smoothstep((T-(end-r))/r), written with expr primitives.</summary>
    private static string RampExpr(string T, double start, double end, double r)
    {
        string up = SmoothstepExpr(T, start, r);
        string down = SmoothstepExpr(T, end - r, r);
        return $"({up}-{down})";
    }

    private static string SmoothstepExpr(string T, double from, double r)
    {
        var inv = CultureInfo.InvariantCulture;
        string u = string.Create(inv, $"clip(({T}-{from:0.###})/{r:0.###},0,1)");
        return $"(pow({u},2)*(3-2*{u}))";
    }
}
