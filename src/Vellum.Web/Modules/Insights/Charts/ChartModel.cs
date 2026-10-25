namespace Vellum.Modules.Insights.Charts;

/// <summary>Chart forms supported by the server-side SVG toolkit (RPT-103).</summary>
public enum ChartKind
{
    Column,
    StackedColumn,
    Bar,
    StackedBar,
    Line,
    Area,
    Donut,
    Pareto,
    HeatMap,
    Gauge,
}

public enum ValueFormat { Count, Decimal, Percent, Days }

/// <summary>One position on the category axis. <paramref name="Href"/> is the drill-through target for the whole category.</summary>
public sealed record ChartCategory(string Key, string Label, string? Href = null);

/// <summary>
/// A value series. Tone is a palette slot (<c>s1</c>..<c>s8</c>), a status (<c>bad</c>, <c>warn</c>, <c>ok</c>),
/// <c>muted</c> (de-emphasis) or an ordinal step (<c>o1</c>..<c>o5</c>). Colours always come from CSS variables so
/// light and dark themes each get their own validated steps.
/// </summary>
public sealed class ChartSeries
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required double?[] Values { get; init; }
    /// <summary>Drill-through per point (aligned with the categories). Null entries fall back to the category link.</summary>
    public string?[]? Hrefs { get; init; }
    public string Tone { get; set; } = "s1";
    /// <summary>Optional per-point tone (ordinal ramps, out-of-control points).</summary>
    public string?[]? PointTones { get; init; }
}

/// <summary>A horizontal reference line: control limits (RPT-301), averages or targets.</summary>
/// <param name="Kind">ucl | lcl | cl | target</param>
public sealed record LimitLine(double Value, string Label, string Kind);

/// <summary>Gauge colour range (RPT-104). Ranges never overlap (RPT-105).</summary>
/// <param name="Tone">ok | warn | bad | info | s1..s8</param>
public sealed record GaugeBand(double From, double To, string Tone, string Label);

/// <summary>Everything the renderer needs to draw a chart, its legend and its report table (RPT-404).</summary>
public sealed class ChartData
{
    /// <summary>Stable key (CSV export, DOM ids).</summary>
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public ChartKind Kind { get; init; } = ChartKind.Column;
    public List<ChartCategory> Categories { get; init; } = [];
    public List<ChartSeries> Series { get; init; } = [];
    public string? XTitle { get; init; }
    public string? YTitle { get; init; }
    public ValueFormat Format { get; init; } = ValueFormat.Count;
    /// <summary>Header of the category column in the report table.</summary>
    public string CategoryHeader { get; init; } = "Category";
    public List<LimitLine> Limits { get; init; } = [];
    /// <summary>RPT-404: the total (or average) row can be hidden.</summary>
    public bool HideTotal { get; init; }
    /// <summary>Show an average row instead of a total (rates, durations).</summary>
    public bool AverageRow { get; init; }
    /// <summary>Share-of-total column is meaningless for rates and durations.</summary>
    public bool NoShare { get; init; }
    /// <summary>Time axis: thin out labels instead of truncating them, keep the latest period labelled.</summary>
    public bool Ordered { get; init; }
    /// <summary>Highlight points outside the control limits (c-chart).</summary>
    public bool MarkOutOfControl { get; init; }
    /// <summary>viewBox width; match the column the chart sits in so text renders near its nominal size.</summary>
    public int Width { get; init; } = 560;
    public int Height { get; init; } = 250;
    public string? EmptyText { get; init; }
    /// <summary>Footnote under the chart (method, period, caveats).</summary>
    public string? Note { get; init; }

    // Gauge
    public double? GaugeValue { get; init; }
    public double GaugeMin { get; init; }
    public double GaugeMax { get; init; } = 100;
    public List<GaugeBand> Bands { get; init; } = [];
    public string? GaugeLabel { get; init; }
    public string? GaugeHref { get; init; }

    public bool IsEmpty => Kind == ChartKind.Gauge
        ? GaugeValue == null
        : Categories.Count == 0 || Series.Count == 0 || Series.All(s => s.Values.All(v => v is null or 0));
}

/// <summary>How the report table is shown under a chart.</summary>
public enum TableMode { None, Collapsed, Open }
