using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace Vellum.Modules.Risk;

/// <summary>One FMEA worksheet row for the Pareto chart.</summary>
public sealed record ParetoRow(int Index, string Label, int S, int O, int D, int Rpn, int? RevisedRpn, bool Flagged, bool Handled);

/// <summary>
/// Server-rendered, accessible inline SVG (role="img" with title and description, a hover title on every cell or bar,
/// text labels so colour is never the only cue). Colours come from design-system CSS variables in risk.css, so the
/// charts follow light and dark mode.
/// </summary>
public static class RiskCharts
{
    private static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "");
    private static string N(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static string Describe(int s, int l)
    {
        var score = RiskMatrix.Score(s, l);
        return $"severity {s} ({RiskMatrix.Severity[s - 1]}) × likelihood {l} ({RiskMatrix.Likelihood[l - 1]}) = {score}, {RiskMatrix.BandFor(score)?.Label ?? "no matching result"}";
    }

    /// <summary>The record-page heat map with the initial (I) and residual (R) positions and an arrow between them.</summary>
    public static IHtmlContent RecordMatrix(RiskState st, string title)
    {
        const double cw = 50, ch = 40, gap = 3, ml = 34, mt = 6, mb = 38, mr = 4;
        var w = ml + RiskMatrix.Size * cw + (RiskMatrix.Size - 1) * gap + mr;
        var h = mt + RiskMatrix.Size * ch + (RiskMatrix.Size - 1) * gap + mb;
        var id = Id("rm");
        double Cx(int s) => ml + (s - 1) * (cw + gap) + cw / 2;
        double Cy(int l) => mt + (RiskMatrix.Size - l) * (ch + gap) + ch / 2;

        var desc = new List<string>();
        if (st.Severity is { } s0 && st.Likelihood is { } l0) desc.Add("Initial risk: " + Describe(s0, l0) + ".");
        if (st.ResidualSeverity is { } s1 && st.ResidualLikelihood is { } l1) desc.Add("Residual risk: " + Describe(s1, l1) + ".");
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"rk-svg\" viewBox=\"0 0 {N(w)} {N(h)}\" role=\"img\" aria-labelledby=\"{id}-t {id}-d\">");
        sb.Append($"<title id=\"{id}-t\">Risk matrix: {E(title)}</title><desc id=\"{id}-d\">{E(string.Join(" ", desc))}</desc>");
        sb.Append($"<defs><marker id=\"{id}-arrow\" viewBox=\"0 0 10 10\" refX=\"8\" refY=\"5\" markerWidth=\"6\" markerHeight=\"6\" orient=\"auto-start-reverse\"><path class=\"rk-arrowhead\" d=\"M 0 0 L 10 5 L 0 10 z\"/></marker></defs>");
        for (var l = RiskMatrix.Size; l >= 1; l--)
            for (var s = 1; s <= RiskMatrix.Size; s++)
            {
                var score = RiskMatrix.Score(s, l);
                var band = RiskMatrix.BandFor(score);
                var x = ml + (s - 1) * (cw + gap);
                var y = mt + (RiskMatrix.Size - l) * (ch + gap);
                sb.Append($"<g><title>{E(char.ToUpperInvariant(Describe(s, l)[0]) + Describe(s, l)[1..])}</title>");
                sb.Append($"<rect class=\"rk-cell rk-{band?.Key}\" x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(cw)}\" height=\"{N(ch)}\" rx=\"4\"/>");
                sb.Append($"<text class=\"rk-corner rk-on-{band?.Key}\" x=\"{N(x + 5)}\" y=\"{N(y + 12)}\">{score}</text></g>");
            }
        for (var i = 1; i <= RiskMatrix.Size; i++)
        {
            sb.Append($"<text class=\"rk-tick\" x=\"{N(ml - 8)}\" y=\"{N(Cy(i) + 4)}\" text-anchor=\"end\">{i}</text>");
            sb.Append($"<text class=\"rk-tick\" x=\"{N(Cx(i))}\" y=\"{N(h - mb + 16)}\" text-anchor=\"middle\">{i}</text>");
        }
        sb.Append($"<text class=\"rk-axis\" transform=\"translate(10 {N(mt + (h - mt - mb) / 2)}) rotate(-90)\" text-anchor=\"middle\">Likelihood</text>");
        sb.Append($"<text class=\"rk-axis\" x=\"{N(ml + (w - ml - mr) / 2)}\" y=\"{N(h - 6)}\" text-anchor=\"middle\">Severity</text>");

        (double X, double Y)? ip = st.Severity is { } a && st.Likelihood is { } b ? (Cx(a), Cy(b)) : null;
        (double X, double Y)? rp = st.ResidualSeverity is { } c && st.ResidualLikelihood is { } d ? (Cx(c), Cy(d)) : null;
        var same = ip != null && rp != null && ip.Value == rp.Value;
        if (ip is { } i0 && rp is { } r0 && !same)
        {
            var dx = r0.X - i0.X;
            var dy = r0.Y - i0.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            const double rad = 13;
            sb.Append($"<line class=\"rk-arrow\" x1=\"{N(i0.X + dx / len * rad)}\" y1=\"{N(i0.Y + dy / len * rad)}\" x2=\"{N(r0.X - dx / len * (rad + 2))}\" y2=\"{N(r0.Y - dy / len * (rad + 2))}\" marker-end=\"url(#{id}-arrow)\"/>");
        }
        if (ip is { } ii) Marker(sb, same ? ii.X - 10 : ii.X, ii.Y, "I", "rk-mark-initial", same ? 10 : 12, "Initial risk");
        if (rp is { } rr) Marker(sb, same ? rr.X + 10 : rr.X, rr.Y, "R", "rk-mark-residual", same ? 10 : 12, "Residual risk");
        sb.Append("</svg>");
        return new HtmlString(sb.ToString());
    }

    private static void Marker(StringBuilder sb, double x, double y, string letter, string cls, double r, string label)
    {
        sb.Append($"<g class=\"{cls}\"><title>{label}</title><circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"{N(r)}\"/>");
        sb.Append($"<text x=\"{N(x)}\" y=\"{N(y + 4)}\" text-anchor=\"middle\">{letter}</text></g>");
    }

    /// <summary>The summary heat map: number of open risks per cell; every cell links to its list.</summary>
    public static IHtmlContent SummaryMatrix(int[,] counts, Func<int, int, string> href, (int S, int L)? selected, string basisLabel)
    {
        const double cw = 104, ch = 62, gap = 4, ml = 122, mt = 8, mb = 54, mr = 6;
        var w = ml + RiskMatrix.Size * cw + (RiskMatrix.Size - 1) * gap + mr;
        var h = mt + RiskMatrix.Size * ch + (RiskMatrix.Size - 1) * gap + mb;
        var id = Id("hm");
        var total = 0;
        var parts = new List<string>();
        for (var s = 1; s <= RiskMatrix.Size; s++)
            for (var l = 1; l <= RiskMatrix.Size; l++)
                if (counts[s - 1, l - 1] > 0) { total += counts[s - 1, l - 1]; parts.Add($"{counts[s - 1, l - 1]} at severity {s} × likelihood {l}"); }
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"rk-svg rk-summary\" viewBox=\"0 0 {N(w)} {N(h)}\" role=\"img\" aria-labelledby=\"{id}-t {id}-d\">");
        sb.Append($"<title id=\"{id}-t\">Heat map of {total} open risk{(total == 1 ? "" : "s")} ({E(basisLabel)})</title><desc id=\"{id}-d\">{E(parts.Count == 0 ? "No open risks." : string.Join("; ", parts) + ".")}</desc>");
        for (var l = RiskMatrix.Size; l >= 1; l--)
            for (var s = 1; s <= RiskMatrix.Size; s++)
            {
                var score = RiskMatrix.Score(s, l);
                var band = RiskMatrix.BandFor(score);
                var n = counts[s - 1, l - 1];
                var x = ml + (s - 1) * (cw + gap);
                var y = mt + (RiskMatrix.Size - l) * (ch + gap);
                var sel = selected is { } p && p.S == s && p.L == l;
                var label = $"Severity {s} × likelihood {l} = {score} ({band?.Label}): {n} open risk{(n == 1 ? "" : "s")}";
                sb.Append($"<a href=\"{E(href(s, l))}\" class=\"rk-link{(sel ? " rk-selected" : "")}\" aria-label=\"{E(label)}\"><title>{E(label)}</title>");
                sb.Append($"<rect class=\"rk-cell rk-{band?.Key}\" x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(cw)}\" height=\"{N(ch)}\" rx=\"5\"/>");
                if (sel) sb.Append($"<rect class=\"rk-sel\" x=\"{N(x + 1.5)}\" y=\"{N(y + 1.5)}\" width=\"{N(cw - 3)}\" height=\"{N(ch - 3)}\" rx=\"4\"/>");
                sb.Append($"<text class=\"rk-corner rk-on-{band?.Key}\" x=\"{N(x + 7)}\" y=\"{N(y + 15)}\">{band?.Letter}{score}</text>");
                sb.Append($"<text class=\"rk-count rk-on-{band?.Key}{(n == 0 ? " zero" : "")}\" x=\"{N(x + cw / 2)}\" y=\"{N(y + ch / 2 + 9)}\" text-anchor=\"middle\">{(n == 0 ? "·" : n.ToString(CultureInfo.InvariantCulture))}</text></a>");
            }
        for (var i = 1; i <= RiskMatrix.Size; i++)
        {
            var cy = mt + (RiskMatrix.Size - i) * (ch + gap) + ch / 2;
            sb.Append($"<text class=\"rk-tick\" x=\"{N(ml - 10)}\" y=\"{N(cy + 4)}\" text-anchor=\"end\">{i} · {RiskMatrix.Likelihood[i - 1]}</text>");
            var cx = ml + (i - 1) * (cw + gap) + cw / 2;
            sb.Append($"<text class=\"rk-tick\" x=\"{N(cx)}\" y=\"{N(h - mb + 18)}\" text-anchor=\"middle\">{i} · {RiskMatrix.Severity[i - 1]}</text>");
        }
        sb.Append($"<text class=\"rk-axis\" x=\"{N(ml - 10)}\" y=\"{N(h - mb + 18)}\" text-anchor=\"end\">Likelihood ↑</text>");
        sb.Append($"<text class=\"rk-axis\" x=\"{N(ml + (w - ml - mr) / 2)}\" y=\"{N(h - 8)}\" text-anchor=\"middle\">Severity →</text>");
        sb.Append("</svg>");
        return new HtmlString(sb.ToString());
    }

    /// <summary>Pareto of the highest RPNs: sorted bars on one axis, the RPN threshold, and revised RPN ticks.</summary>
    public static IHtmlContent Pareto(IReadOnlyList<ParetoRow> rows, int rpnThreshold, int severityThreshold, string title)
    {
        var top = rows.OrderByDescending(r => r.Rpn).ThenBy(r => r.Index).Take(10).ToList();
        if (top.Count == 0) return HtmlString.Empty;
        const double w = 700, labelW = 230, ml = 240, mr = 46, rowH = 30, barH = 16, mt = 26, mb = 30;
        var h = mt + top.Count * rowH + mb;
        var maxV = Math.Max(top.Max(r => Math.Max(r.Rpn, r.RevisedRpn ?? 0)), rpnThreshold);
        var step = maxV <= 150 ? 25 : maxV <= 300 ? 50 : maxV <= 600 ? 100 : 200;
        var scaleMax = (int)Math.Ceiling(maxV * 1.08 / step) * step;
        double X(double v) => ml + v / scaleMax * (w - ml - mr);
        var id = Id("pf");
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"pf-svg\" viewBox=\"0 0 {N(w)} {N(h)}\" role=\"img\" aria-labelledby=\"{id}-t {id}-d\">");
        sb.Append($"<title id=\"{id}-t\">Pareto of the highest RPNs: {E(title)}</title>");
        sb.Append($"<desc id=\"{id}-d\">{E(string.Join("; ", top.Select(r => $"row {r.Index}, {r.Label}: RPN {r.Rpn}" + (r.RevisedRpn is { } rv ? $", revised {rv}" : ""))))}. RPN threshold {rpnThreshold}.</desc>");
        for (var t = 0; t <= scaleMax; t += step)
        {
            sb.Append($"<line class=\"pf-grid\" x1=\"{N(X(t))}\" x2=\"{N(X(t))}\" y1=\"{N(mt - 4)}\" y2=\"{N(h - mb)}\"/>");
            sb.Append($"<text class=\"pf-tick\" x=\"{N(X(t))}\" y=\"{N(h - mb + 16)}\" text-anchor=\"middle\">{t}</text>");
        }
        for (var i = 0; i < top.Count; i++)
        {
            var r = top[i];
            var y = mt + i * rowH + (rowH - barH) / 2;
            var x0 = X(0);
            var x1 = X(r.Rpn);
            var label = r.Label.Length > 34 ? r.Label[..33].TrimEnd() + "…" : r.Label;
            var tip = $"Row {r.Index}: {r.Label}. S{r.S} × O{r.O} × D{r.D} = RPN {r.Rpn}" +
                      (r.RevisedRpn is { } rv ? $"; revised RPN {rv}" : "") +
                      (r.Flagged ? r.Handled ? "; over a threshold, action or justification recorded" : "; over a threshold, needs a recommended action or justification" : "");
            sb.Append($"<g class=\"pf-row\"><title>{E(tip)}</title>");
            sb.Append($"<rect class=\"pf-hit\" x=\"0\" y=\"{N(mt + i * rowH)}\" width=\"{N(w)}\" height=\"{N(rowH)}\"/>");
            sb.Append($"<text class=\"pf-label\" x=\"{N(labelW)}\" y=\"{N(y + barH - 3)}\" text-anchor=\"end\">{E($"{r.Index}. {label}")}</text>");
            var cls = r.Flagged ? "pf-bar pf-flag" : "pf-bar";
            if (x1 - x0 > 5)
                sb.Append($"<path class=\"{cls}\" d=\"M {N(x0)} {N(y)} H {N(x1 - 4)} Q {N(x1)} {N(y)} {N(x1)} {N(y + 4)} V {N(y + barH - 4)} Q {N(x1)} {N(y + barH)} {N(x1 - 4)} {N(y + barH)} H {N(x0)} Z\"/>");
            else
                sb.Append($"<rect class=\"{cls}\" x=\"{N(x0)}\" y=\"{N(y)}\" width=\"{N(Math.Max(1, x1 - x0))}\" height=\"{N(barH)}\"/>");
            sb.Append($"<text class=\"pf-value\" x=\"{N(x1 + 6)}\" y=\"{N(y + barH - 3)}\">{r.Rpn}{(r.Flagged && !r.Handled ? " !" : "")}</text>");
            if (r.RevisedRpn is { } rev)
                sb.Append($"<line class=\"pf-rev\" x1=\"{N(X(rev))}\" x2=\"{N(X(rev))}\" y1=\"{N(y - 4)}\" y2=\"{N(y + barH + 4)}\"/>");
            sb.Append("</g>");
        }
        var tx = X(rpnThreshold);
        sb.Append($"<line class=\"pf-threshold\" x1=\"{N(tx)}\" x2=\"{N(tx)}\" y1=\"{N(mt - 8)}\" y2=\"{N(h - mb)}\"/>");
        sb.Append($"<text class=\"pf-tick\" x=\"{N(tx)}\" y=\"{N(mt - 12)}\" text-anchor=\"middle\">RPN threshold {rpnThreshold}</text>");
        sb.Append("</svg>");
        return new HtmlString(sb.ToString());
    }
}
