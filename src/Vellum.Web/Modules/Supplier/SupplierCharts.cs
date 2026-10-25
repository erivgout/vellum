using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace Vellum.Modules.Supplier;

/// <summary>
/// Server-rendered, accessible inline SVG for supplier scores: a single-series line on one axis, grade thresholds as
/// gridlines, hover titles on every point, and a text alternative (the table beside the chart carries every value).
/// Colours come from the design-system CSS variables (see wwwroot/css/modules/risk.css).
/// </summary>
public static class SupplierCharts
{
    private static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "");
    private static string F(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    private static string N(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);

    public static IHtmlContent Trend(IReadOnlyList<ScorePoint> points, string supplierName, bool compact = false)
    {
        var pts = points.Where(p => p.Score != null).ToList();
        var id = "sc" + Guid.NewGuid().ToString("N")[..8];
        if (pts.Count == 0) return HtmlString.Empty;
        double w = compact ? 260 : 640, h = compact ? 64 : 230;
        double ml = compact ? 6 : 40, mr = compact ? 40 : 30, mt = compact ? 14 : 16, mb = compact ? 6 : 30;
        var min = (double)pts.Min(p => p.Score!.Value);
        double yMin = compact ? Math.Min(60, Math.Floor((min - 5) / 10) * 10) : Math.Min(50, Math.Floor((min - 5) / 10) * 10);
        yMin = Math.Max(0, yMin);
        const double yMax = 100;
        double X(int i) => pts.Count == 1 ? ml + (w - ml - mr) / 2 : ml + i * (w - ml - mr) / (pts.Count - 1);
        double Y(decimal v) => mt + (yMax - (double)v) / (yMax - yMin) * (h - mt - mb);

        var desc = string.Join("; ", pts.Select(p => $"{p.Period}: {F(p.Score!.Value)} ({p.Grade?.Label ?? "no grade"}){(p.Published ? "" : ", draft")}"));
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"sc-chart{(compact ? " compact" : "")}\" viewBox=\"0 0 {N(w)} {N(h)}\" role=\"img\" aria-labelledby=\"{id}-t {id}-d\" preserveAspectRatio=\"xMidYMid meet\">");
        sb.Append($"<title id=\"{id}-t\">{E(supplierName)} scorecard trend</title><desc id=\"{id}-d\">{E(desc)}</desc>");

        if (!compact)
        {
            for (var t = yMin; t <= yMax + 0.01; t += 10)
            {
                var y = Y((decimal)t);
                sb.Append($"<line class=\"sc-grid{(t is 70 or 80 or 90 ? " sc-grade-line" : "")}\" x1=\"{N(ml)}\" x2=\"{N(w - mr)}\" y1=\"{N(y)}\" y2=\"{N(y)}\"/>");
                sb.Append($"<text class=\"sc-tick\" x=\"{N(ml - 8)}\" y=\"{N(y + 4)}\" text-anchor=\"end\">{N(t)}</text>");
            }
            foreach (var (letter, lo, hi) in new[] { ("A", 90.0, 100.0), ("B", 80.0, 90.0), ("C", 70.0, 80.0), ("D", yMin, 70.0) })
            {
                if (hi <= yMin) continue;
                var y = Y((decimal)((Math.Max(lo, yMin) + hi) / 2));
                sb.Append($"<text class=\"sc-band\" x=\"{N(w - mr + 10)}\" y=\"{N(y + 4)}\">{letter}</text>");
            }
            for (var i = 0; i < pts.Count; i++)
                sb.Append($"<text class=\"sc-tick\" x=\"{N(X(i))}\" y=\"{N(h - 8)}\" text-anchor=\"middle\">{E(pts[i].Period)}</text>");
        }

        if (pts.Count > 1)
            sb.Append($"<polyline class=\"sc-line\" points=\"{string.Join(" ", pts.Select((p, i) => $"{N(X(i))},{N(Y(p.Score!.Value))}"))}\"/>");
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var last = i == pts.Count - 1;
            if (!compact || last)
            {
                sb.Append($"<g class=\"sc-pt-g\"><title>{E($"{p.Period}: {F(p.Score!.Value)} · {p.Grade?.Label ?? "no grade"}{(p.Published ? "" : " (draft)")}")}</title>");
                sb.Append($"<circle class=\"sc-hit\" cx=\"{N(X(i))}\" cy=\"{N(Y(p.Score!.Value))}\" r=\"12\"/>");
                sb.Append($"<circle class=\"sc-pt{(p.Published ? "" : " draft")}\" cx=\"{N(X(i))}\" cy=\"{N(Y(p.Score.Value))}\" r=\"{(compact ? 4 : 5)}\"/></g>");
            }
        }
        var lp = pts[^1];
        var lx = X(pts.Count - 1);
        var ly = Y(lp.Score!.Value);
        var label = $"{F(lp.Score.Value)}{(lp.Grade != null ? " · " + lp.Grade.Key.ToUpperInvariant() : "")}";
        if (compact)
            sb.Append($"<text class=\"sc-value\" x=\"{N(lx + 9)}\" y=\"{N(ly + 4)}\">{E(label)}</text>");
        else
            sb.Append($"<text class=\"sc-value\" x=\"{N(lx)}\" y=\"{N(Math.Max(ly - 12, 11))}\" text-anchor=\"{(pts.Count == 1 ? "middle" : "end")}\">{E(label)}</text>");
        sb.Append("</svg>");
        return new HtmlString(sb.ToString());
    }
}
