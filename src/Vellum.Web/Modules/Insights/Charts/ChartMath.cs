using System.Globalization;
using System.Net;

namespace Vellum.Modules.Insights.Charts;

/// <summary>Scales, number formatting and text measurement for the SVG toolkit.</summary>
public static class ChartMath
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Coordinates are written with one decimal, invariant culture.</summary>
    public static string N(double v) => Math.Round(v, 1).ToString("0.#", Inv);

    public static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>
    /// Approximate rendered width of <paramref name="s"/> in the UI sans (Inter) at <paramref name="size"/> px.
    /// Errs slightly wide so labels never collide.
    /// </summary>
    public static double TextWidth(string? s, double size)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double w = 0;
        foreach (var ch in s)
        {
            w += ch switch
            {
                'i' or 'l' or 'j' or '.' or ',' or ':' or ';' or '|' or '!' or '\'' or 'I' => 0.28,
                ' ' or 'f' or 't' or 'r' or '(' or ')' or '-' or '/' or '[' or ']' => 0.36,
                'm' or 'w' or 'M' or 'W' or '%' or '@' or '—' => 0.88,
                >= '0' and <= '9' => 0.61,
                >= 'A' and <= 'Z' => 0.68,
                _ => 0.56,
            };
        }
        return w * size;
    }

    /// <summary>Truncate with an ellipsis so the text fits <paramref name="maxWidth"/>. Full text goes in a &lt;title&gt;.</summary>
    public static string Fit(string s, double maxWidth, double size)
    {
        if (TextWidth(s, size) <= maxWidth) return s;
        if (maxWidth < TextWidth("…", size) * 2) return "";
        var t = s;
        while (t.Length > 1 && TextWidth(t + "…", size) > maxWidth) t = t[..^1];
        return t.TrimEnd() + "…";
    }

    /// <summary>"Nice" axis: ~<paramref name="target"/> ticks at 1/2/2.5/5 × 10ⁿ, always including 0.</summary>
    public static (double Min, double Max, double Step) NiceScale(double min, double max, int target = 4, bool integer = true)
    {
        min = Math.Min(0, min);
        max = Math.Max(0, max);
        if (max - min <= 0) max = integer ? 4 : 1;
        var raw = (max - min) / target;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / mag;
        var step = (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * mag;
        if (integer) step = Math.Max(1, Math.Ceiling(step));
        var lo = Math.Floor(min / step) * step;
        var hi = Math.Ceiling(max / step) * step;
        if (hi <= 0 && max > 0) hi = step;
        return (lo, hi, step);
    }

    public static IEnumerable<double> Ticks(double min, double max, double step)
    {
        for (var t = min; t <= max + step * 1e-9; t += step) yield return Math.Round(t, 10);
    }

    /// <summary>Compact axis label: 0 / 950 / 1.2K / 12K / 3.4M.</summary>
    public static string Compact(double v)
    {
        var a = Math.Abs(v);
        if (a >= 1_000_000) return (v / 1_000_000).ToString("0.#", Inv) + "M";
        if (a >= 10_000) return (v / 1_000).ToString("0", Inv) + "K";
        if (a >= 1_000) return (v / 1_000).ToString("0.#", Inv) + "K";
        return Math.Abs(v - Math.Round(v)) < 1e-9 ? v.ToString("0", Inv) : v.ToString("0.##", Inv);
    }

    /// <summary>Full value for labels, tooltips and the report table. Averages round to 2 decimals (RPT-104).</summary>
    public static string Format(double v, ValueFormat f) => f switch
    {
        ValueFormat.Percent => v.ToString("0.#", Inv) + "%",
        ValueFormat.Days => (Math.Abs(v) < 10 ? v.ToString("0.#", Inv) : v.ToString("#,0", Inv)) + " d",
        ValueFormat.Decimal => v.ToString("#,0.##", Inv),
        _ => Math.Abs(v - Math.Round(v)) < 1e-9 ? v.ToString("#,0", Inv) : v.ToString("#,0.##", Inv),
    };

    public static string Tick(double v, ValueFormat f) => f switch
    {
        ValueFormat.Percent => Compact(v) + "%",
        ValueFormat.Days => Compact(v) + " d",
        _ => Compact(v),
    };

    public static string Percent(double share) => (share * 100).ToString(share < 0.1 && share > 0 ? "0.#" : "0", Inv) + "%";

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(v => v).ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }

    /// <summary>Nearest-rank percentile (0–100).</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(p / 100 * s.Count);
        return s[Math.Clamp(rank - 1, 0, s.Count - 1)];
    }
}

/// <summary>RPT-301: statistical control limits.</summary>
public static class ControlLimits
{
    /// <summary>
    /// c-chart limits for counts per period: centre line C̄ (mean count), UCL = C̄ + 3√C̄, LCL = C̄ − 3√C̄ floored at 0.
    /// </summary>
    public static (double Center, double Ucl, double Lcl) CChart(IEnumerable<double> counts)
    {
        var list = counts.ToList();
        if (list.Count == 0) return (0, 0, 0);
        var c = list.Average();
        var sigma3 = 3 * Math.Sqrt(c);
        return (c, c + sigma3, Math.Max(0, c - sigma3));
    }
}
