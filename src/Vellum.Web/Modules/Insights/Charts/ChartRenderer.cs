using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using static Vellum.Modules.Insights.Charts.ChartMath;

namespace Vellum.Modules.Insights.Charts;

/// <summary>
/// Server-side SVG charts (no client chart library; strict CSP). Every chart is an accessible, responsive inline SVG
/// (viewBox + width 100%) with a &lt;title&gt;/&lt;desc&gt;, native tooltips via &lt;title&gt; on each mark, drill-through links
/// on marks (RPT-403), a legend for two or more series and a report table (RPT-404). Colours come from CSS variables
/// (wwwroot/css/modules/insights.css), so light and dark themes each use their own validated palette steps.
/// Mark specs: bars ≤ 24px with a 4px rounded data end, square at the baseline; 2px surface gaps between touching
/// marks; 2px lines; ≥ 8px end markers with a 2px surface ring; hairline, solid, recessive grid.
/// </summary>
public static partial class ChartRenderer
{
    private const double Font = 11;
    private const double Gap = 2;
    private const double MaxBar = 24;

    // ------------------------------------------------------------------ public API

    /// <summary>Chart + legend + optional report table, wrapped in a &lt;figure&gt;.</summary>
    /// <param name="width">viewBox width override for this placement (keeps text near its nominal size).</param>
    public static IHtmlContent Figure(ChartData d, TableMode table = TableMode.Collapsed, int? width = null, int? height = null)
    {
        var uid = Uid(d.Id);
        var sb = new StringBuilder();
        sb.Append($"<figure class=\"ins-figure ins-kind-{d.Kind.ToString().ToLowerInvariant()}\" id=\"{uid}\">");
        if (d.IsEmpty)
        {
            sb.Append("<div class=\"ins-empty\"><i class=\"bi bi-bar-chart\" aria-hidden=\"true\"></i><span>")
              .Append(Esc(d.EmptyText ?? "No data for this period")).Append("</span></div></figure>");
            return new HtmlString(sb.ToString());
        }
        if (d.Kind == ChartKind.Donut)
        {
            sb.Append("<div class=\"ins-donut-wrap\"><div class=\"ins-plot ins-donut-plot\">").Append(Svg(d, uid, width, height)).Append("</div>")
              .Append(DonutLegend(d)).Append("</div>");
        }
        else
        {
            sb.Append(Legend(d));
            sb.Append("<div class=\"ins-plot\">").Append(Svg(d, uid, width, height)).Append("</div>");
        }
        if (d.Kind == ChartKind.Gauge && d.GaugeValue is { } gv && BandOf(d, gv) is { } band)
            sb.Append($"<div class=\"ins-gauge-status tone-{ToneClass(band.Tone)}\"><i class=\"bi {StatusIcon(band.Tone)}\" aria-hidden=\"true\"></i>{Esc(band.Label)}</div>");
        if (d.Note != null) sb.Append($"<p class=\"ins-note\">{Esc(d.Note)}</p>");
        if (table != TableMode.None && d.Kind != ChartKind.Gauge)
        {
            if (table == TableMode.Collapsed)
                sb.Append("<details class=\"ins-data\"><summary><i class=\"bi bi-table\" aria-hidden=\"true\"></i>Report table</summary>")
                  .Append(Table(d)).Append("</details>");
            else
                sb.Append(Table(d));
        }
        sb.Append("</figure>");
        return new HtmlString(sb.ToString());
    }

    /// <summary>The bare &lt;svg&gt; element.</summary>
    public static string Svg(ChartData d, string? uid = null, int? width = null, int? height = null)
    {
        uid ??= Uid(d.Id);
        var sb = new StringBuilder();
        var cw = width ?? d.Width;
        var (w, h) = d.Kind switch
        {
            ChartKind.Bar or ChartKind.StackedBar => (cw, HorizontalHeight(d)),
            ChartKind.HeatMap => (cw, HeatHeight(d)),
            ChartKind.Donut => (200, 200),
            ChartKind.Gauge => (320, 184),
            _ => (cw, height ?? d.Height),
        };
        sb.Append($"<svg class=\"ins-svg\" viewBox=\"0 0 {w} {h}\" width=\"100%\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\" aria-labelledby=\"{uid}-t {uid}-d\"")
          .Append(d.Kind is ChartKind.Donut or ChartKind.Gauge ? $" style=\"max-width:{w * 1.25}px\"" : $" style=\"min-width:{Math.Min(w, 320)}px\"")
          .Append('>');
        sb.Append($"<title id=\"{uid}-t\">{Esc(d.Title)}</title><desc id=\"{uid}-d\">{Esc(Describe(d))}</desc>");
        switch (d.Kind)
        {
            case ChartKind.Bar or ChartKind.StackedBar: Horizontal(sb, d, w, h); break;
            case ChartKind.Donut: Donut(sb, d); break;
            case ChartKind.HeatMap: Heat(sb, d, w); break;
            case ChartKind.Gauge: Gauge(sb, d); break;
            default: Vertical(sb, d, w, h); break;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Tiny trend line for stat tiles: de-emphasis ink, latest point in the accent. Decorative (the tile states the value).</summary>
    public static IHtmlContent Sparkline(IReadOnlyList<double> values, string label, string tone = "s1", int width = 120, int height = 34)
    {
        if (values.Count < 2) return HtmlString.Empty;
        var sb = new StringBuilder();
        var max = Math.Max(1, values.Max());
        var min = Math.Min(0, values.Min());
        const double pad = 5;
        double X(int i) => pad + i * (width - 2 * pad) / (values.Count - 1);
        double Y(double v) => height - pad - (v - min) / (max - min) * (height - 2 * pad);
        sb.Append($"<svg class=\"ins-spark\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" aria-hidden=\"true\" focusable=\"false\">");
        var line = string.Join(" ", values.Select((v, i) => $"{(i == 0 ? "M" : "L")}{N(X(i))},{N(Y(v))}"));
        sb.Append($"<path class=\"ins-spark-area ins-t-{tone}\" d=\"{line} L{N(X(values.Count - 1))},{N(height - pad)} L{N(X(0))},{N(height - pad)} Z\"/>");
        sb.Append($"<path class=\"ins-spark-line\" d=\"{line}\"/>");
        sb.Append($"<circle class=\"ins-dot ins-t-{tone}\" cx=\"{N(X(values.Count - 1))}\" cy=\"{N(Y(values[^1]))}\" r=\"3.5\"/>");
        sb.Append("</svg>");
        sb.Append($"<span class=\"visually-hidden\">{Esc(label)}</span>");
        return new HtmlString(sb.ToString());
    }

    /// <summary>RPT-404: values, categories, each category's share of the total, and a total (or average) row.</summary>
    public static string Table(ChartData d)
    {
        var sb = new StringBuilder();
        var cats = OrderedIndexes(d);
        var multi = d.Series.Count > 1;
        var pareto = d.Kind == ChartKind.Pareto;
        var share = !d.NoShare;
        var rowTotals = d.Categories.Select((_, i) => d.Series.Sum(s => s.Values[i] ?? 0)).ToArray();
        var grand = rowTotals.Where(v => v > 0).Sum();

        sb.Append("<div class=\"ins-table vl-table-scroll\"><table class=\"table vl-table vl-table-compact ins-report\">");
        sb.Append($"<caption class=\"visually-hidden\">Report table: {Esc(d.Title)}</caption><thead><tr><th scope=\"col\">{Esc(d.CategoryHeader)}</th>");
        foreach (var s in d.Series) sb.Append($"<th scope=\"col\" class=\"numeric\">{Esc(s.Name)}</th>");
        if (multi) sb.Append("<th scope=\"col\" class=\"numeric\">Total</th>");
        if (share) sb.Append("<th scope=\"col\" class=\"numeric\">Share</th>");
        if (pareto) sb.Append("<th scope=\"col\" class=\"numeric\">Cumulative</th>");
        sb.Append("</tr></thead><tbody>");
        double cum = 0;
        foreach (var i in cats)
        {
            var c = d.Categories[i];
            sb.Append("<tr><th scope=\"row\">");
            sb.Append(c.Href != null ? $"<a href=\"{Esc(c.Href)}\">{Esc(c.Label)}</a>" : Esc(c.Label));
            sb.Append("</th>");
            for (var j = 0; j < d.Series.Count; j++)
            {
                var s = d.Series[j];
                var v = s.Values[i];
                var href = HrefOf(d, j, i);
                var text = v is { } x ? Format(x, d.Format) : "–";
                sb.Append("<td class=\"numeric\">").Append(href != null && v is not null and not 0 ? $"<a href=\"{Esc(href)}\">{text}</a>" : text).Append("</td>");
            }
            if (multi) sb.Append($"<td class=\"numeric fw-semibold\">{Format(rowTotals[i], d.Format)}</td>");
            if (share) sb.Append($"<td class=\"numeric text-body-secondary\">{(rowTotals[i] > 0 && grand > 0 ? Percent(rowTotals[i] / grand) : "–")}</td>");
            if (pareto)
            {
                cum += Math.Max(0, rowTotals[i]);
                sb.Append($"<td class=\"numeric text-body-secondary\">{(grand > 0 ? Percent(cum / grand) : "–")}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody>");
        if (!d.HideTotal && d.Categories.Count > 0)
        {
            sb.Append($"<tfoot><tr><th scope=\"row\">{(d.AverageRow ? "Average" : "Total")}</th>");
            foreach (var s in d.Series)
            {
                var vals = s.Values.Where(v => v != null).Select(v => v!.Value).ToList();
                var agg = d.AverageRow ? (vals.Count > 0 ? vals.Average() : 0) : vals.Sum();
                sb.Append($"<td class=\"numeric\">{Format(agg, d.AverageRow && d.Format == ValueFormat.Count ? ValueFormat.Decimal : d.Format)}</td>");
            }
            if (multi)
            {
                var all = rowTotals.ToList();
                sb.Append($"<td class=\"numeric\">{Format(d.AverageRow ? (all.Count > 0 ? all.Average() : 0) : all.Sum(), d.Format)}</td>");
            }
            if (share) sb.Append($"<td class=\"numeric\">{(grand > 0 ? "100%" : "–")}</td>");
            if (pareto) sb.Append("<td></td>");
            sb.Append("</tr></tfoot>");
        }
        sb.Append("</table></div>");
        if (d.Limits.Count > 0)
            sb.Append("<p class=\"ins-note\">").Append(Esc(string.Join(" · ", d.Limits.Select(l => LimitText(l, d.Format))))).Append("</p>");
        return sb.ToString();
    }

    /// <summary>The report table as CSV (RPT-403 export), same columns as <see cref="Table"/>.</summary>
    public static string Csv(ChartData d)
    {
        static string C(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        static string V(double? v) => v is { } x ? x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder();
        var multi = d.Series.Count > 1;
        var rowTotals = d.Categories.Select((_, i) => d.Series.Sum(s => s.Values[i] ?? 0)).ToArray();
        var grand = rowTotals.Where(v => v > 0).Sum();
        if (d.Kind == ChartKind.Gauge)
        {
            sb.Append("Measure,Value\r\n").Append(C(d.GaugeLabel ?? d.Title)).Append(',').Append(V(d.GaugeValue)).Append("\r\n");
            return sb.ToString();
        }
        var head = new List<string> { d.CategoryHeader };
        head.AddRange(d.Series.Select(s => s.Name));
        if (multi) head.Add("Total");
        if (!d.NoShare) head.Add("Share %");
        sb.Append(string.Join(',', head.Select(C))).Append("\r\n");
        foreach (var i in OrderedIndexes(d))
        {
            var row = new List<string> { C(d.Categories[i].Label) };
            row.AddRange(d.Series.Select(s => V(s.Values[i])));
            if (multi) row.Add(V(rowTotals[i]));
            if (!d.NoShare) row.Add(rowTotals[i] > 0 && grand > 0 ? V(Math.Round(rowTotals[i] / grand * 100, 1)) : "");
            sb.Append(string.Join(',', row)).Append("\r\n");
        }
        if (!d.HideTotal)
        {
            var row = new List<string> { d.AverageRow ? "Average" : "Total" };
            row.AddRange(d.Series.Select(s =>
            {
                var vals = s.Values.Where(v => v != null).Select(v => v!.Value).ToList();
                return V(d.AverageRow ? (vals.Count > 0 ? vals.Average() : 0) : vals.Sum());
            }));
            if (multi) row.Add(V(d.AverageRow ? rowTotals.DefaultIfEmpty().Average() : rowTotals.Sum()));
            if (!d.NoShare) row.Add(grand > 0 ? "100" : "");
            sb.Append(string.Join(',', row.Select(C))).Append("\r\n");
        }
        foreach (var l in d.Limits) sb.Append(C(l.Label)).Append(',').Append(V(l.Value)).Append("\r\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ legend & description

    private static string Legend(ChartData d)
    {
        var items = new List<(string Name, string Tone, bool Line)>();
        if (d.Kind == ChartKind.Pareto && d.Series.Count > 0)
        {
            items.Add((d.Series[0].Name, d.Series[0].Tone, false));
            items.Add(("Cumulative %", "s2", true));
        }
        else if (d.Series.Count >= 2 && d.Kind is not (ChartKind.HeatMap or ChartKind.Gauge))
        {
            var line = d.Kind is ChartKind.Line;
            items.AddRange(d.Series.Select(s => (s.Name, s.Tone, line)));
        }
        if (items.Count == 0) return "";
        var sb = new StringBuilder("<ul class=\"ins-legend\">");
        foreach (var (name, tone, line) in items)
            sb.Append($"<li><span class=\"ins-key {(line ? "ins-key-line" : "ins-key-rect")} ins-t-{tone}\" aria-hidden=\"true\"></span>{Esc(name)}</li>");
        return sb.Append("</ul>").ToString();
    }

    private static string DonutLegend(ChartData d)
    {
        var (labels, values, tones, hrefs) = DonutSlices(d);
        var total = values.Where(v => v > 0).Sum();
        var sb = new StringBuilder("<ul class=\"ins-legend ins-legend-v\">");
        for (var i = 0; i < labels.Count; i++)
        {
            var label = hrefs[i] != null ? $"<a href=\"{Esc(hrefs[i])}\">{Esc(labels[i])}</a>" : Esc(labels[i]);
            sb.Append($"<li><span class=\"ins-key ins-key-rect ins-t-{tones[i]}\" aria-hidden=\"true\"></span><span class=\"ins-leg-label\">{label}</span>")
              .Append($"<span class=\"ins-leg-val\">{Format(values[i], d.Format)}</span><span class=\"ins-leg-share\">{(total > 0 && values[i] > 0 ? Percent(values[i] / total) : "–")}</span></li>");
        }
        return sb.Append("</ul>").ToString();
    }

    /// <summary>Plain-language summary for screen readers (&lt;desc&gt;).</summary>
    public static string Describe(ChartData d)
    {
        var kind = d.Kind switch
        {
            ChartKind.Column => "Column chart", ChartKind.StackedColumn => "Stacked column chart", ChartKind.Bar => "Bar chart",
            ChartKind.StackedBar => "Stacked bar chart", ChartKind.Line => "Line chart", ChartKind.Area => "Area chart",
            ChartKind.Donut => "Donut chart", ChartKind.Pareto => "Pareto chart", ChartKind.HeatMap => "Heat map", ChartKind.Gauge => "Gauge",
            _ => "Chart",
        };
        if (d.Kind == ChartKind.Gauge)
            return $"{kind}: {d.GaugeLabel ?? d.Title} is {(d.GaugeValue is { } g ? Format(g, d.Format) : "not available")} on a scale of {Format(d.GaugeMin, d.Format)} to {Format(d.GaugeMax, d.Format)}.";
        if (d.IsEmpty) return $"{kind} with no data.";
        var parts = new List<string> { $"{kind} of {d.Categories.Count} {(d.Ordered ? "periods" : "categories")}" };
        if (d.Series.Count > 1) parts.Add($"{d.Series.Count} series: {string.Join(", ", d.Series.Select(s => s.Name))}");
        var totals = d.Categories.Select((c, i) => (c.Label, V: d.Series.Sum(s => s.Values[i] ?? 0))).ToList();
        if (d.Ordered) parts.Add($"from {d.Categories[0].Label} to {d.Categories[^1].Label}; latest {Format(totals[^1].V, d.Format)}");
        else
        {
            var top = totals.OrderByDescending(t => t.V).First();
            parts.Add($"highest {top.Label} at {Format(top.V, d.Format)}");
        }
        if (d.Limits.Count > 0) parts.Add(string.Join(", ", d.Limits.Select(l => LimitText(l, d.Format))));
        return string.Join("; ", parts) + ". The report table lists every value.";
    }

    // ------------------------------------------------------------------ vertical cartesian (column, stacked, line, area, pareto)

    private static void Vertical(StringBuilder sb, ChartData d, double W, double H)
    {
        var n = d.Categories.Count;
        var order = OrderedIndexes(d);
        var series = d.Series;
        var stacked = d.Kind == ChartKind.StackedColumn;
        var pareto = d.Kind == ChartKind.Pareto;
        var line = d.Kind is ChartKind.Line or ChartKind.Area;

        double lo = 0, hi = 0, total = 0;
        foreach (var i in order)
        {
            if (stacked)
            {
                hi = Math.Max(hi, series.Sum(s => Math.Max(0, s.Values[i] ?? 0)));
                lo = Math.Min(lo, series.Sum(s => Math.Min(0, s.Values[i] ?? 0)));
            }
            else
                foreach (var s in series)
                    if (s.Values[i] is { } v) { hi = Math.Max(hi, v); lo = Math.Min(lo, v); }
        }
        if (pareto) { total = order.Sum(i => Math.Max(0, series[0].Values[i] ?? 0)); hi = total; }
        foreach (var l in d.Limits) { hi = Math.Max(hi, l.Value); lo = Math.Min(lo, l.Value); }
        var integer = d.Format is ValueFormat.Count or ValueFormat.Percent;
        var (yMin, yMax, step) = NiceScale(lo, hi * (line && !pareto ? 1.04 : 1), H < 170 ? 3 : 4, integer);
        if (pareto && total > 0) yMax = total;
        var ticks = Ticks(yMin, yMax, step).Where(t => t <= yMax + 1e-9).ToList();

        var tickLabels = ticks.Select(t => Tick(t, d.Format)).ToList();
        var left = Math.Max(22, tickLabels.Max(l => TextWidth(l, Font)) + 10) + (d.YTitle != null ? 18 : 0);
        double right = 12;
        if (d.Limits.Count > 0) right = Math.Max(right, d.Limits.Max(l => TextWidth(LimitText(l, d.Format), 10)) + 12);
        if (pareto) right = Math.Max(right, TextWidth("100%", Font) + 14);
        double top = 18, bottom = 24 + (d.XTitle != null ? 16 : 0);
        double x0 = left, x1 = W - right, y0 = top, y1 = H - bottom;
        var band = (x1 - x0) / Math.Max(1, n);
        double Y(double v) => y1 - (v - yMin) / (yMax - yMin) * (y1 - y0);
        double Cx(int pos) => x0 + band * (pos + 0.5);

        // Grid and value axis
        sb.Append("<g class=\"ins-axes\" aria-hidden=\"true\">");
        for (var k = 0; k < ticks.Count; k++)
        {
            var y = Y(ticks[k]);
            if (Math.Abs(ticks[k]) > 1e-9) sb.Append($"<line class=\"ins-grid\" x1=\"{N(x0)}\" x2=\"{N(x1)}\" y1=\"{N(y)}\" y2=\"{N(y)}\"/>");
            sb.Append($"<text class=\"ins-tick\" x=\"{N(x0 - 7)}\" y=\"{N(y + 3.8)}\" text-anchor=\"end\">{Esc(tickLabels[k])}</text>");
        }
        if (d.YTitle != null)
            sb.Append($"<text class=\"ins-axis-title\" transform=\"translate(11 {N((y0 + y1) / 2)}) rotate(-90)\" text-anchor=\"middle\">{Esc(d.YTitle)}</text>");
        if (pareto && total > 0)
            foreach (var p in new[] { 0, 25, 50, 75, 100 })
                sb.Append($"<text class=\"ins-tick\" x=\"{N(x1 + 7)}\" y=\"{N(Y(total * p / 100.0) + 3.8)}\">{p}%</text>");
        XLabels(sb, d, order, x0, band, y1, W);
        if (d.XTitle != null) sb.Append($"<text class=\"ins-axis-title\" x=\"{N((x0 + x1) / 2)}\" y=\"{N(H - 4)}\" text-anchor=\"middle\">{Esc(d.XTitle)}</text>");
        sb.Append("</g>");

        // Limits and reference lines sit under the marks.
        if (pareto && total > 0)
        {
            var y80 = Y(total * 0.8);
            sb.Append($"<line class=\"ins-ref\" x1=\"{N(x0)}\" x2=\"{N(x1)}\" y1=\"{N(y80)}\" y2=\"{N(y80)}\" aria-hidden=\"true\"/>");
        }
        LimitLines(sb, d, x0, x1, Y);

        if (line) Lines(sb, d, order, Cx, Y, y0, y1, band, x0, x1);
        else if (stacked) StackedColumns(sb, d, order, Cx, Y, band);
        else Columns(sb, d, order, x0, band, Y, y0, y1, pareto ? total : null);

        // Baseline on top of the grid
        sb.Append($"<line class=\"ins-axis\" x1=\"{N(x0)}\" x2=\"{N(x1)}\" y1=\"{N(Y(0))}\" y2=\"{N(Y(0))}\" aria-hidden=\"true\"/>");

        if (pareto && total > 0) ParetoLine(sb, d, order, Cx, Y, total);
    }

    private static void XLabels(StringBuilder sb, ChartData d, List<int> order, double x0, double band, double y1, double W)
    {
        var n = order.Count;
        if (n == 0) return;
        var widths = order.Select(i => TextWidth(d.Categories[i].Label, Font)).ToList();
        var every = 1;
        if (d.Ordered || band < 30) every = Math.Max(1, (int)Math.Ceiling((widths.Max() + 10) / band));
        for (var pos = 0; pos < n; pos++)
        {
            if ((n - 1 - pos) % every != 0) continue;
            var c = d.Categories[order[pos]];
            var text = every > 1 ? c.Label : Fit(c.Label, band - 6, Font);
            var tw = TextWidth(text, Font);
            var cx = x0 + band * (pos + 0.5);
            cx = Math.Clamp(cx, tw / 2 + 2, W - tw / 2 - 2);
            sb.Append($"<text class=\"ins-xlab\" x=\"{N(cx)}\" y=\"{N(y1 + 16)}\" text-anchor=\"middle\">{Esc(text)}");
            if (text != c.Label) sb.Append($"<title>{Esc(c.Label)}</title>");
            sb.Append("</text>");
        }
    }

    private static void Columns(StringBuilder sb, ChartData d, List<int> order, double x0, double band, Func<double, double> Y, double y0, double y1, double? paretoTotal)
    {
        var series = d.Series;
        var s = series.Count;
        var slot = Math.Min(band * 0.72, s * MaxBar + (s - 1) * Gap);
        var bw = Math.Max(2, Math.Min(MaxBar, (slot - (s - 1) * Gap) / s));
        var gw = s * bw + (s - 1) * Gap;
        var capLabels = s == 1 && paretoTotal == null && order.Count <= 16;
        double cum = 0;
        for (var pos = 0; pos < order.Count; pos++)
        {
            var i = order[pos];
            var c = d.Categories[i];
            for (var j = 0; j < s; j++)
            {
                var v = series[j].Values[i];
                if (v is not { } val || val == 0) continue;
                var x = x0 + band * pos + (band - gw) / 2 + j * (bw + Gap);
                var yv = Y(val);
                var yb = Y(0);
                var topY = Math.Min(yv, yb);
                var h = Math.Max(1, Math.Abs(yb - yv));
                if (val > 0 && Math.Abs(yb - yv) < 1) topY = yb - 1;
                var tone = series[j].PointTones?[i] ?? series[j].Tone;
                var tip = s == 1 ? $"{c.Label}: {Format(val, d.Format)}" : $"{c.Label} · {series[j].Name}: {Format(val, d.Format)}";
                if (paretoTotal is { } pt && pt > 0)
                {
                    cum += val;
                    tip += $" ({Percent(val / pt)}, cumulative {Percent(cum / pt)})";
                }
                var href = HrefOf(d, j, i);
                Open(sb, href, tip);
                var hx = s == 1 ? x0 + band * pos : x - Gap / 2;
                var hw = s == 1 ? band : bw + Gap;
                sb.Append($"<rect class=\"ins-hit\" x=\"{N(hx)}\" y=\"{N(y0)}\" width=\"{N(hw)}\" height=\"{N(y1 - y0)}\"/>");
                sb.Append($"<path class=\"ins-bar ins-t-{tone}\" d=\"{BarPath(x, topY, bw, h, val >= 0 ? "top" : "bottom")}\"/>");
                Close(sb, href);
                if (capLabels)
                {
                    var lab = Format(val, d.Format);
                    if (TextWidth(lab, Font) <= band - 2)
                        sb.Append($"<text class=\"ins-val\" x=\"{N(x + bw / 2)}\" y=\"{N(val >= 0 ? yv - 6 : yv + 14)}\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(lab)}</text>");
                }
            }
        }
    }

    private static void StackedColumns(StringBuilder sb, ChartData d, List<int> order, Func<int, double> Cx, Func<double, double> Y, double band)
    {
        var series = d.Series;
        var bw = Math.Max(3, Math.Min(MaxBar, band * 0.62));
        var totals = order.Count <= 16;
        for (var pos = 0; pos < order.Count; pos++)
        {
            var i = order[pos];
            var c = d.Categories[i];
            var x = Cx(pos) - bw / 2;
            var topPos = -1;
            for (var j = 0; j < series.Count; j++) if ((series[j].Values[i] ?? 0) > 0) topPos = j;
            var bottomNeg = -1;
            for (var j = 0; j < series.Count; j++) if ((series[j].Values[i] ?? 0) < 0) bottomNeg = j;
            double pc = 0, nc = 0;
            for (var j = 0; j < series.Count; j++)
            {
                var v = series[j].Values[i] ?? 0;
                if (v == 0) continue;
                double yTop, yBot;
                string end;
                if (v > 0)
                {
                    yTop = Y(pc + v); yBot = Y(pc);
                    if (pc > 0) yBot -= Gap;
                    pc += v;
                    end = j == topPos ? "top" : "none";
                }
                else
                {
                    yTop = Y(nc); yBot = Y(nc + v);
                    if (nc < 0) yTop += Gap;
                    nc += v;
                    end = j == bottomNeg ? "bottom" : "none";
                }
                var h = yBot - yTop;
                if (h < 0.5) continue;
                var tip = $"{c.Label} · {series[j].Name}: {Format(v, d.Format)}";
                var href = HrefOf(d, j, i);
                Open(sb, href, tip);
                sb.Append($"<path class=\"ins-bar ins-t-{series[j].Tone}\" d=\"{BarPath(x, yTop, bw, h, end)}\"/>");
                Close(sb, href);
            }
            if (totals && pc > 0)
            {
                var lab = Format(pc, d.Format);
                if (TextWidth(lab, Font) <= band - 2)
                    sb.Append($"<text class=\"ins-val\" x=\"{N(Cx(pos))}\" y=\"{N(Y(pc) - 6)}\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(lab)}</text>");
            }
        }
    }

    private static void Lines(StringBuilder sb, ChartData d, List<int> order, Func<int, double> Cx, Func<double, double> Y,
        double y0, double y1, double band, double x0, double x1)
    {
        var series = d.Series;
        var ucl = d.Limits.FirstOrDefault(l => l.Kind == "ucl")?.Value;
        var lcl = d.Limits.FirstOrDefault(l => l.Kind == "lcl")?.Value;
        bool Out(double v) => d.MarkOutOfControl && ((ucl is { } u && v > u + 1e-9) || (lcl is { } l && l > 0 && v < l - 1e-9));

        foreach (var s in series)
        {
            var path = new StringBuilder();
            var pen = false;
            var pts = new List<(double X, double Y, int I)>();
            for (var pos = 0; pos < order.Count; pos++)
            {
                var v = s.Values[order[pos]];
                if (v is not { } val) { pen = false; continue; }
                var (px, py) = (Cx(pos), Y(val));
                path.Append(pen ? $"L{N(px)},{N(py)} " : $"M{N(px)},{N(py)} ");
                pen = true;
                pts.Add((px, py, order[pos]));
            }
            if (pts.Count == 0) continue;
            if (d.Kind == ChartKind.Area && pts.Count > 1)
                sb.Append($"<path class=\"ins-area ins-t-{s.Tone}\" d=\"M{N(pts[0].X)},{N(Y(0))} {string.Join(" ", pts.Select(p => $"L{N(p.X)},{N(p.Y)}"))} L{N(pts[^1].X)},{N(Y(0))} Z\" aria-hidden=\"true\"/>");
            sb.Append($"<path class=\"ins-line ins-t-{s.Tone}\" d=\"{path.ToString().Trim()}\" aria-hidden=\"true\"/>");
            var allMarkers = d.MarkOutOfControl || pts.Count <= 2;
            foreach (var (px, py, i) in allMarkers ? pts : [pts[^1]])
            {
                var v = s.Values[i]!.Value;
                var tone = Out(v) ? "bad" : s.PointTones?[i] ?? s.Tone;
                sb.Append($"<circle class=\"ins-dot ins-t-{tone}\" cx=\"{N(px)}\" cy=\"{N(py)}\" r=\"{(Out(v) ? "5" : "4")}\" aria-hidden=\"true\"/>");
            }
        }
        // Direct label: the latest value of a single series.
        if (series.Count == 1 && series[0].Values.LastOrDefault(v => v != null) is { } lastV)
        {
            var lastPos = order.FindLastIndex(i => series[0].Values[i] != null);
            var lx = Math.Clamp(Cx(lastPos), x0 + 12, x1 - 12);
            sb.Append($"<text class=\"ins-val\" x=\"{N(lx)}\" y=\"{N(Y(lastV) - 10)}\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(Format(lastV, d.Format))}</text>");
        }
        // Hover layer: one hit column per period with a crosshair and every series' value in the tooltip.
        for (var pos = 0; pos < order.Count; pos++)
        {
            var i = order[pos];
            var c = d.Categories[i];
            var vals = series.Where(s => s.Values[i] != null).ToList();
            if (vals.Count == 0) continue;
            var tip = series.Count == 1
                ? $"{c.Label}: {Format(series[0].Values[i]!.Value, d.Format)}{(Out(series[0].Values[i]!.Value) ? " — outside the control limits" : "")}"
                : $"{c.Label} — " + string.Join(" · ", vals.Select(s => $"{s.Name}: {Format(s.Values[i]!.Value, d.Format)}"));
            var href = c.Href ?? HrefOf(d, 0, i);
            sb.Append("<g class=\"ins-x\">");
            Open(sb, href, tip);
            sb.Append($"<rect class=\"ins-hit\" x=\"{N(x0 + band * pos)}\" y=\"{N(y0)}\" width=\"{N(band)}\" height=\"{N(y1 - y0)}\"/>");
            sb.Append($"<line class=\"ins-cross\" x1=\"{N(Cx(pos))}\" x2=\"{N(Cx(pos))}\" y1=\"{N(y0)}\" y2=\"{N(y1)}\"/>");
            foreach (var s in vals)
                sb.Append($"<circle class=\"ins-hdot ins-t-{s.Tone}\" cx=\"{N(Cx(pos))}\" cy=\"{N(Y(s.Values[i]!.Value))}\" r=\"4\"/>");
            Close(sb, href);
            sb.Append("</g>");
        }
    }

    private static void ParetoLine(StringBuilder sb, ChartData d, List<int> order, Func<int, double> Cx, Func<double, double> Y, double total)
    {
        double cum = 0;
        var pts = new List<(double X, double Y)>();
        for (var pos = 0; pos < order.Count; pos++)
        {
            cum += Math.Max(0, d.Series[0].Values[order[pos]] ?? 0);
            pts.Add((Cx(pos), Y(cum)));
        }
        sb.Append($"<path class=\"ins-line ins-t-s2\" d=\"{string.Join(" ", pts.Select((p, k) => $"{(k == 0 ? "M" : "L")}{N(p.X)},{N(p.Y)}"))}\" aria-hidden=\"true\"/>");
        foreach (var p in pts) sb.Append($"<circle class=\"ins-dot ins-t-s2\" cx=\"{N(p.X)}\" cy=\"{N(p.Y)}\" r=\"3.5\" aria-hidden=\"true\"/>");
    }

    private static void LimitLines(StringBuilder sb, ChartData d, double x0, double x1, Func<double, double> Y)
    {
        if (d.Limits.Count == 0) return;
        var labels = new List<(double Y, string Text, string Kind)>();
        foreach (var l in d.Limits)
        {
            var y = Y(l.Value);
            sb.Append($"<line class=\"ins-limit ins-limit-{l.Kind}\" x1=\"{N(x0)}\" x2=\"{N(x1)}\" y1=\"{N(y)}\" y2=\"{N(y)}\"><title>{Esc(LimitText(l, d.Format))}</title></line>");
            labels.Add((y, LimitText(l, d.Format), l.Kind));
        }
        // Keep limit labels at least 12px apart (they would otherwise overprint when limits are close).
        labels = labels.OrderBy(l => l.Y).ToList();
        for (var k = 1; k < labels.Count; k++)
            if (labels[k].Y - labels[k - 1].Y < 12) labels[k] = labels[k] with { Y = labels[k - 1].Y + 12 };
        foreach (var (y, text, kind) in labels)
            sb.Append($"<text class=\"ins-limit-lab ins-limit-lab-{kind}\" x=\"{N(x1 + 6)}\" y=\"{N(y + 3.5)}\" aria-hidden=\"true\">{Esc(text)}</text>");
    }

    // ------------------------------------------------------------------ horizontal bars

    private static double RowHeight(ChartData d) => d.Kind == ChartKind.Bar && d.Series.Count > 1 ? Math.Max(30, d.Series.Count * 13 + 12) : 30;

    private static double HorizontalHeight(ChartData d) => 8 + d.Categories.Count * RowHeight(d) + (d.Limits.Count > 0 ? 18 : 4);

    private static void Horizontal(StringBuilder sb, ChartData d, double W, double H)
    {
        var order = OrderedIndexes(d);
        var series = d.Series;
        var stacked = d.Kind == ChartKind.StackedBar;
        var rowH = RowHeight(d);
        double top = d.Limits.Count > 0 ? 18 : 6;

        var totals = order.ToDictionary(i => i, i => stacked ? series.Sum(s => Math.Max(0, s.Values[i] ?? 0)) : series.Max(s => s.Values[i] ?? 0));
        var mins = order.ToDictionary(i => i, i => series.Min(s => Math.Min(0, s.Values[i] ?? 0)));
        var hi = Math.Max(totals.Values.DefaultIfEmpty().Max(), d.Limits.Select(l => l.Value).DefaultIfEmpty().Max());
        var lo = Math.Min(0, mins.Values.DefaultIfEmpty().Min());
        if (hi - lo <= 0) hi = 1;

        var labelW = Math.Min(order.Select(i => TextWidth(d.Categories[i].Label, Font)).DefaultIfEmpty().Max() + 14, W * 0.36);
        var valueW = order.Select(i => TextWidth(Format(totals[i], d.Format), Font)).DefaultIfEmpty().Max() + 12;
        if (series.Count > 1 && !stacked) valueW = series.SelectMany(s => s.Values).Where(v => v != null).Select(v => TextWidth(Format(v!.Value, d.Format), 10)).DefaultIfEmpty().Max() + 12;
        double x0 = labelW, x1 = W - valueW;
        double X(double v) => x0 + (v - lo) / (hi - lo) * (x1 - x0);

        // Every bar is labelled at its tip, so the value axis is dropped; the baseline stays.
        sb.Append($"<line class=\"ins-axis\" x1=\"{N(X(0))}\" x2=\"{N(X(0))}\" y1=\"{N(top - 2)}\" y2=\"{N(top + order.Count * rowH + 2)}\" aria-hidden=\"true\"/>");
        foreach (var l in d.Limits)
        {
            var x = X(l.Value);
            sb.Append($"<line class=\"ins-limit ins-limit-{l.Kind}\" x1=\"{N(x)}\" x2=\"{N(x)}\" y1=\"{N(top - 2)}\" y2=\"{N(top + order.Count * rowH)}\"><title>{Esc(LimitText(l, d.Format))}</title></line>");
            sb.Append($"<text class=\"ins-limit-lab\" x=\"{N(x)}\" y=\"11\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(LimitText(l, d.Format))}</text>");
        }

        for (var pos = 0; pos < order.Count; pos++)
        {
            var i = order[pos];
            var c = d.Categories[i];
            var yc = top + rowH * pos + rowH / 2;
            var label = Fit(c.Label, labelW - 14, Font);
            sb.Append($"<text class=\"ins-ylab\" x=\"{N(x0 - 9)}\" y=\"{N(yc + 3.8)}\" text-anchor=\"end\">{Esc(label)}{(label != c.Label ? $"<title>{Esc(c.Label)}</title>" : "")}</text>");

            if (stacked)
            {
                var t = Math.Min(MaxBar, rowH * 0.62);
                var lastPos = -1;
                for (var j = 0; j < series.Count; j++) if ((series[j].Values[i] ?? 0) > 0) lastPos = j;
                double cum = 0;
                for (var j = 0; j < series.Count; j++)
                {
                    var v = series[j].Values[i] ?? 0;
                    if (v <= 0) continue;
                    var xa = X(cum) + (cum > 0 ? Gap : 0);
                    var xb = X(cum + v);
                    cum += v;
                    if (xb - xa < 0.5) continue;
                    var tip = $"{c.Label} · {series[j].Name}: {Format(v, d.Format)}";
                    var href = HrefOf(d, j, i);
                    Open(sb, href, tip);
                    sb.Append($"<path class=\"ins-bar ins-t-{series[j].Tone}\" d=\"{BarPath(xa, yc - t / 2, xb - xa, t, j == lastPos ? "right" : "none")}\"/>");
                    Close(sb, href);
                }
                if (cum > 0)
                    sb.Append($"<text class=\"ins-val\" x=\"{N(X(cum) + 6)}\" y=\"{N(yc + 3.8)}\" aria-hidden=\"true\">{Esc(Format(cum, d.Format))}</text>");
            }
            else
            {
                var s = series.Count;
                var t = s == 1 ? Math.Min(MaxBar - 4, rowH * 0.62) : Math.Min(12, (rowH - 10 - (s - 1) * Gap) / s);
                var gh = s * t + (s - 1) * Gap;
                if (s == 1) Open(sb, HrefOf(d, 0, i), $"{c.Label}: {Format(series[0].Values[i] ?? 0, d.Format)}");
                if (s == 1) sb.Append($"<rect class=\"ins-hit\" x=\"0\" y=\"{N(yc - rowH / 2)}\" width=\"{N(W)}\" height=\"{N(rowH)}\"/>");
                for (var j = 0; j < s; j++)
                {
                    if (series[j].Values[i] is not { } v) continue;
                    var y = yc - gh / 2 + j * (t + Gap);
                    var xa = X(Math.Min(0, v));
                    var w = Math.Max(v == 0 ? 0 : 1, Math.Abs(X(v) - X(0)));
                    var tone = series[j].PointTones?[i] ?? series[j].Tone;
                    var href = HrefOf(d, j, i);
                    if (s > 1) Open(sb, href, $"{c.Label} · {series[j].Name}: {Format(v, d.Format)}");
                    if (w > 0) sb.Append($"<path class=\"ins-bar ins-t-{tone}\" d=\"{BarPath(xa, y, w, t, v >= 0 ? "right" : "left")}\"/>");
                    if (s > 1) Close(sb, href);
                    var lab = Format(v, d.Format);
                    var tx = v >= 0 ? X(v) + 6 : X(v) - 6;
                    sb.Append($"<text class=\"ins-val{(s > 1 ? " ins-val-sm" : "")}\" x=\"{N(tx)}\" y=\"{N(y + t / 2 + (s > 1 ? 3.4 : 3.8))}\"{(v < 0 ? " text-anchor=\"end\"" : "")} aria-hidden=\"true\">{Esc(lab)}</text>");
                }
                if (s == 1) Close(sb, HrefOf(d, 0, i));
            }
        }
    }

    // ------------------------------------------------------------------ donut

    private static (List<string> Labels, List<double> Values, List<string> Tones, List<string?> Hrefs) DonutSlices(ChartData d)
    {
        var s = d.Series[0];
        var items = d.Categories.Select((c, i) => (c.Label, V: s.Values[i] ?? 0, Href: s.Hrefs?[i] ?? c.Href)).Where(x => x.V > 0).ToList();
        var labels = new List<string>(); var values = new List<double>(); var tones = new List<string>(); var hrefs = new List<string?>();
        const int max = 6;
        for (var k = 0; k < items.Count; k++)
        {
            if (k == max && items.Count > max + 1)
            {
                labels.Add("Other"); values.Add(items.Skip(max).Sum(x => x.V)); tones.Add("muted"); hrefs.Add(null);
                break;
            }
            labels.Add(items[k].Label); values.Add(items[k].V); tones.Add("s" + (k + 1)); hrefs.Add(items[k].Href);
        }
        return (labels, values, tones, hrefs);
    }

    private static void Donut(StringBuilder sb, ChartData d)
    {
        var (labels, values, tones, hrefs) = DonutSlices(d);
        var total = values.Sum();
        const double cx = 100, cy = 100, ro = 90, ri = 68;
        var a = -Math.PI / 2;
        var gapA = labels.Count > 1 ? Gap / ((ro + ri) / 2) : 0;
        for (var k = 0; k < labels.Count; k++)
        {
            var sweep = values[k] / total * 2 * Math.PI;
            var a0 = a + gapA / 2;
            var a1 = a + sweep - gapA / 2;
            a += sweep;
            if (a1 <= a0) continue;
            var tip = $"{labels[k]}: {Format(values[k], d.Format)} ({Percent(values[k] / total)})";
            Open(sb, hrefs[k], tip);
            if (labels.Count == 1)
                sb.Append($"<path class=\"ins-bar ins-t-{tones[k]}\" fill-rule=\"evenodd\" d=\"M{N(cx + ro)},{N(cy)} A{N(ro)},{N(ro)} 0 1 1 {N(cx - ro)},{N(cy)} A{N(ro)},{N(ro)} 0 1 1 {N(cx + ro)},{N(cy)} Z M{N(cx + ri)},{N(cy)} A{N(ri)},{N(ri)} 0 1 0 {N(cx - ri)},{N(cy)} A{N(ri)},{N(ri)} 0 1 0 {N(cx + ri)},{N(cy)} Z\"/>");
            else
            {
                var large = a1 - a0 > Math.PI ? 1 : 0;
                sb.Append($"<path class=\"ins-bar ins-t-{tones[k]}\" d=\"M{N(cx + ro * Math.Cos(a0))},{N(cy + ro * Math.Sin(a0))} A{N(ro)},{N(ro)} 0 {large} 1 {N(cx + ro * Math.Cos(a1))},{N(cy + ro * Math.Sin(a1))} L{N(cx + ri * Math.Cos(a1))},{N(cy + ri * Math.Sin(a1))} A{N(ri)},{N(ri)} 0 {large} 0 {N(cx + ri * Math.Cos(a0))},{N(cy + ri * Math.Sin(a0))} Z\"/>");
            }
            Close(sb, hrefs[k]);
        }
        sb.Append($"<text class=\"ins-donut-total\" x=\"{cx}\" y=\"{cy + 5}\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(Format(total, d.Format))}</text>");
        sb.Append($"<text class=\"ins-donut-sub\" x=\"{cx}\" y=\"{cy + 23}\" text-anchor=\"middle\" aria-hidden=\"true\">Total</text>");
    }

    // ------------------------------------------------------------------ heat map

    private const double HeatHead = 30, HeatRow = 30, HeatLegend = 34;

    private static double HeatHeight(ChartData d) => HeatHead + d.Categories.Count * HeatRow + HeatLegend;

    private static void Heat(StringBuilder sb, ChartData d, double W)
    {
        var rows = d.Categories.Count;
        var cols = d.Series.Count;
        var labelW = Math.Min(d.Categories.Select(c => TextWidth(c.Label, Font)).DefaultIfEmpty().Max() + 14, W * 0.3);
        var cellW = (W - labelW - 4) / Math.Max(1, cols);
        var max = d.Series.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty().Max();
        int Bin(double v) => v <= 0 || max <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(v / max * 6), 1, 6);

        for (var j = 0; j < cols; j++)
        {
            var name = d.Series[j].Name;
            var text = Fit(name, cellW - 6, Font);
            sb.Append($"<text class=\"ins-xlab\" x=\"{N(labelW + cellW * (j + 0.5))}\" y=\"18\" text-anchor=\"middle\">{Esc(text)}{(text != name ? $"<title>{Esc(name)}</title>" : "")}</text>");
        }
        for (var i = 0; i < rows; i++)
        {
            var c = d.Categories[i];
            var y = HeatHead + i * HeatRow;
            var label = Fit(c.Label, labelW - 14, Font);
            sb.Append($"<text class=\"ins-ylab\" x=\"{N(labelW - 9)}\" y=\"{N(y + HeatRow / 2 + 3.8)}\" text-anchor=\"end\">{Esc(label)}{(label != c.Label ? $"<title>{Esc(c.Label)}</title>" : "")}</text>");
            for (var j = 0; j < cols; j++)
            {
                var v = d.Series[j].Values[i] ?? 0;
                var bin = Bin(v);
                var x = labelW + j * cellW;
                var tip = $"{c.Label} · {d.Series[j].Name}: {Format(v, d.Format)}";
                var href = v != 0 ? HrefOf(d, j, i) : null;
                Open(sb, href, tip);
                sb.Append($"<rect class=\"ins-cell ins-q{bin}\" x=\"{N(x + Gap / 2)}\" y=\"{N(y + Gap / 2)}\" width=\"{N(cellW - Gap)}\" height=\"{N(HeatRow - Gap)}\" rx=\"3\"/>");
                var lab = v == 0 ? "" : Format(v, d.Format);
                if (lab.Length > 0 && TextWidth(lab, Font) <= cellW - 8)
                    sb.Append($"<text class=\"ins-cell-lab ins-q{bin}-ink\" x=\"{N(x + cellW / 2)}\" y=\"{N(y + HeatRow / 2 + 3.8)}\" text-anchor=\"middle\">{Esc(lab)}</text>");
                Close(sb, href);
            }
        }
        // Scale legend (sequential, one hue).
        var ly = HeatHead + rows * HeatRow + 14;
        var lx = labelW;
        sb.Append($"<g aria-hidden=\"true\"><text class=\"ins-tick\" x=\"{N(lx)}\" y=\"{N(ly + 9)}\">Fewer</text>");
        lx += TextWidth("Fewer", Font) + 6;
        for (var b = 1; b <= 6; b++)
        {
            sb.Append($"<rect class=\"ins-cell ins-q{b}\" x=\"{N(lx)}\" y=\"{N(ly)}\" width=\"18\" height=\"11\" rx=\"2\"/>");
            lx += 20;
        }
        sb.Append($"<text class=\"ins-tick\" x=\"{N(lx + 4)}\" y=\"{N(ly + 9)}\">More (max {Esc(Format(max, d.Format))})</text></g>");
    }

    // ------------------------------------------------------------------ gauge

    private static GaugeBand? BandOf(ChartData d, double v) =>
        d.Bands.FirstOrDefault(b => v >= b.From && v <= b.To) ?? (d.Bands.Count > 0 ? (v < d.Bands.Min(b => b.From) ? d.Bands.MinBy(b => b.From) : d.Bands.MaxBy(b => b.To)) : null);

    private static void Gauge(StringBuilder sb, ChartData d)
    {
        const double cx = 160, cy = 146, r = 108, t = 16;
        var span = Math.Max(1e-9, d.GaugeMax - d.GaugeMin);
        double P(double v) => Math.Clamp((v - d.GaugeMin) / span, 0, 1);
        (double X, double Y) At(double rad, double p) { var a = Math.PI + p * Math.PI; return (cx + rad * Math.Cos(a), cy + rad * Math.Sin(a)); }
        string Arc(double rad, double p0, double p1)
        {
            var (ax, ay) = At(rad, p0); var (bx, by) = At(rad, p1);
            return $"M{N(ax)},{N(ay)} A{N(rad)},{N(rad)} 0 0 1 {N(bx)},{N(by)}";
        }
        var v = d.GaugeValue ?? d.GaugeMin;
        var p = P(v);
        var band = BandOf(d, v);
        sb.Append($"<path class=\"ins-gauge-track\" d=\"{Arc(r, 0, 1)}\" aria-hidden=\"true\"/>");
        if (p > 0.002) sb.Append($"<path class=\"ins-gauge-val ins-t-{band?.Tone ?? "s1"}\" d=\"{Arc(r, 0, p)}\" aria-hidden=\"true\"/>");
        // Range bands just outside the track, separated by surface gaps, with boundary values.
        var rb = r + t / 2 + 6;
        foreach (var b in d.Bands.OrderBy(b => b.From))
        {
            var p0 = P(b.From) + (b.From > d.GaugeMin ? 0.006 : 0);
            var p1 = P(b.To) - (b.To < d.GaugeMax ? 0.006 : 0);
            if (p1 > p0) sb.Append($"<path class=\"ins-gauge-band ins-t-{b.Tone}\" d=\"{Arc(rb, p0, p1)}\"><title>{Esc($"{b.Label}: {Format(b.From, d.Format)}–{Format(b.To, d.Format)}")}</title></path>");
            if (b.From > d.GaugeMin)
            {
                // Boundary label just outside the band, anchored away from the arc so it never overprints it.
                var pb = P(b.From);
                var (lx, ly) = At(rb + 7, pb);
                var anchor = pb < 0.4 ? "end" : pb > 0.6 ? "start" : "middle";
                sb.Append($"<text class=\"ins-tick\" x=\"{N(lx + (anchor == "start" ? 2 : anchor == "end" ? -2 : 0))}\" y=\"{N(ly + (anchor == "middle" ? -2 : 3))}\" text-anchor=\"{anchor}\" aria-hidden=\"true\">{Esc(Tick(b.From, d.Format))}</text>");
            }
        }
        var (nx0, ny0) = At(r - t / 2 - 7, p);
        var (nx1, ny1) = At(r + t / 2 + 2, p);
        var tip = $"{d.GaugeLabel ?? d.Title}: {Format(v, d.Format)}";
        Open(sb, d.GaugeHref, tip);
        sb.Append($"<line class=\"ins-gauge-hit\" x1=\"{N(nx0)}\" y1=\"{N(ny0)}\" x2=\"{N(nx1)}\" y2=\"{N(ny1)}\"/>");
        sb.Append($"<line class=\"ins-gauge-needle\" x1=\"{N(nx0)}\" y1=\"{N(ny0)}\" x2=\"{N(nx1)}\" y2=\"{N(ny1)}\"/>");
        Close(sb, d.GaugeHref);
        sb.Append($"<text class=\"ins-gauge-value\" x=\"{cx}\" y=\"{N(cy - 22)}\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(d.GaugeValue is { } gv ? Format(gv, d.Format) : "–")}</text>");
        if (d.GaugeLabel != null) sb.Append($"<text class=\"ins-gauge-label\" x=\"{cx}\" y=\"{N(cy)}\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(Fit(d.GaugeLabel, 2 * r - 2 * t - 10, 12))}</text>");
        sb.Append($"<text class=\"ins-tick\" x=\"{N(cx - r)}\" y=\"{N(cy + 20)}\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(Tick(d.GaugeMin, d.Format))}</text>");
        sb.Append($"<text class=\"ins-tick\" x=\"{N(cx + r)}\" y=\"{N(cy + 20)}\" text-anchor=\"middle\" aria-hidden=\"true\">{Esc(Tick(d.GaugeMax, d.Format))}</text>");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Category display order: Pareto sorts descending by value; everything else keeps the given order.</summary>
    public static List<int> OrderedIndexes(ChartData d)
    {
        var idx = Enumerable.Range(0, d.Categories.Count).ToList();
        if (d.Kind == ChartKind.Pareto && d.Series.Count > 0)
            idx = idx.OrderByDescending(i => d.Series[0].Values[i] ?? 0).ThenBy(i => i).ToList();
        return idx;
    }

    private static string? HrefOf(ChartData d, int series, int cat) =>
        series < d.Series.Count && d.Series[series].Hrefs is { } h && cat < h.Length && h[cat] != null ? h[cat] : d.Categories[cat].Href;

    private static void Open(StringBuilder sb, string? href, string tip)
    {
        if (href != null) sb.Append($"<a href=\"{Esc(href)}\" aria-label=\"{Esc(tip)}. Open the records\"><title>{Esc(tip)}</title>");
        else sb.Append($"<g class=\"ins-m\"><title>{Esc(tip)}</title>");
    }

    private static void Close(StringBuilder sb, string? href) => sb.Append(href != null ? "</a>" : "</g>");

    /// <summary>Bar with a 4px rounded data end and a square baseline end. <paramref name="end"/>: top | bottom | right | left | none.</summary>
    public static string BarPath(double x, double y, double w, double h, string end)
    {
        var r = end is "top" or "bottom" ? Math.Min(4, Math.Min(w / 2, h)) : Math.Min(4, Math.Min(h / 2, w));
        if (end == "none" || r < 0.5) return $"M{N(x)},{N(y)}h{N(w)}v{N(h)}h{N(-w)}Z";
        var R = N(r);
        return end switch
        {
            "top" => $"M{N(x)},{N(y + h)}V{N(y + r)}A{R},{R} 0 0 1 {N(x + r)},{N(y)}H{N(x + w - r)}A{R},{R} 0 0 1 {N(x + w)},{N(y + r)}V{N(y + h)}Z",
            "bottom" => $"M{N(x)},{N(y)}H{N(x + w)}V{N(y + h - r)}A{R},{R} 0 0 1 {N(x + w - r)},{N(y + h)}H{N(x + r)}A{R},{R} 0 0 1 {N(x)},{N(y + h - r)}Z",
            "right" => $"M{N(x)},{N(y)}H{N(x + w - r)}A{R},{R} 0 0 1 {N(x + w)},{N(y + r)}V{N(y + h - r)}A{R},{R} 0 0 1 {N(x + w - r)},{N(y + h)}H{N(x)}Z",
            _ => $"M{N(x + w)},{N(y)}V{N(y + h)}H{N(x + r)}A{R},{R} 0 0 1 {N(x)},{N(y + h - r)}V{N(y + r)}A{R},{R} 0 0 1 {N(x + r)},{N(y)}Z",
        };
    }

    private static string LimitText(LimitLine l, ValueFormat f) => $"{l.Label} {Format(Math.Round(l.Value, 2), f == ValueFormat.Count ? ValueFormat.Decimal : f)}";

    private static string ToneClass(string tone) => tone switch { "ok" => "ok", "warn" => "warn", "bad" => "bad", "info" => "info", _ => "neutral" };

    private static string StatusIcon(string tone) => tone switch
    {
        "ok" => "bi-check-circle-fill", "warn" => "bi-exclamation-triangle-fill", "bad" => "bi-exclamation-octagon-fill", _ => "bi-info-circle-fill",
    };

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex IdChars();

    private static string Uid(string id) => "ins-" + IdChars().Replace(id, "") + "-" + Guid.NewGuid().ToString("N")[..5];
}
