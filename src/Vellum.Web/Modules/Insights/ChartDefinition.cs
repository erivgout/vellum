using System.Globalization;
using System.Text;
using System.Text.Json;
using Vellum.Domain;
using Vellum.Engine.Views;
using Vellum.Modules.Insights.Charts;

namespace Vellum.Modules.Insights;

/// <summary>
/// A chart template or chart document (RPT §7.1–7.2): source view, chart type, category, value, optional Compare To,
/// date grouping, relative date range, location filter, control limits and gauge ranges.
/// </summary>
public sealed class ChartDefinition
{
    public string App { get; set; } = "";
    public string View { get; set; } = "";
    /// <summary>column | stacked_column | bar | stacked_bar | line | area | donut | pareto | heatmap | gauge</summary>
    public string Type { get; set; } = "column";

    /// <summary>Category (X axis): a system column (phase, form, status, location, creator, assignee, created, modified, due) or a field key.</summary>
    public string? Category { get; set; } = "phase";
    /// <summary>day | week | month | quarter | year (date categories only).</summary>
    public string? CategoryDates { get; set; }

    /// <summary>count | sum | avg</summary>
    public string Aggregate { get; set; } = "count";
    /// <summary>Number field for sum / avg.</summary>
    public string? ValueField { get; set; }

    /// <summary>Compare To (series split).</summary>
    public string? Compare { get; set; }
    public string? CompareDates { get; set; }

    /// <summary>Date-range filter column: created | modified | due | a date field.</summary>
    public string? DateField { get; set; }
    /// <summary>none | ytd | qtd | mtd | wtd | last_n_days | last_week | last_month | last_quarter | last_year | custom</summary>
    public string Range { get; set; } = "none";
    public int? RangeDays { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    /// <summary>RPT-203: all | only | lower | higher | both</summary>
    public string LocationScope { get; set; } = "all";
    public int? LocationId { get; set; }
    /// <summary>RPT-502: use the viewing user's primary location (dynamic).</summary>
    public bool LocationMine { get; set; }

    /// <summary>none | asc | desc</summary>
    public string Sort { get; set; } = "none";
    public bool HideTotal { get; set; }
    public string? YTitle { get; set; }
    public string? XTitle { get; set; }
    /// <summary>Maximum categories shown; the rest fold into "Other".</summary>
    public int? Top { get; set; }

    /// <summary>RPT-301: none | calculated | manual</summary>
    public string Limits { get; set; } = "none";
    /// <summary>trailing_12_months | last_calendar_year | custom</summary>
    public string LimitsPeriod { get; set; } = "trailing_12_months";
    public DateOnly? LimitsFrom { get; set; }
    public DateOnly? LimitsTo { get; set; }
    public double? Lcl { get; set; }
    public double? Center { get; set; }
    public double? Ucl { get; set; }
    public string? LclTitle { get; set; }
    public string? CenterTitle { get; set; }
    public string? UclTitle { get; set; }

    public double? GaugeMin { get; set; }
    public double? GaugeMax { get; set; }
    public List<GaugeRangeDef> Ranges { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ChartDefinition FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ChartDefinition();
        try { return JsonSerializer.Deserialize<ChartDefinition>(json) ?? new ChartDefinition(); }
        catch (JsonException) { return new ChartDefinition(); }
    }

    /// <summary>Compact URL-safe token for unsaved charts (preview drill-through).</summary>
    public string ToToken() => Convert.ToBase64String(Encoding.UTF8.GetBytes(ToJson())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static ChartDefinition? FromToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 8000) return null;
        try
        {
            var s = token.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(s)));
        }
        catch (FormatException) { return null; }
    }

    public ChartDefinition Clone() => FromJson(ToJson());

    public ChartKind Kind => Type switch
    {
        "stacked_column" => ChartKind.StackedColumn, "bar" => ChartKind.Bar, "stacked_bar" => ChartKind.StackedBar,
        "line" => ChartKind.Line, "area" => ChartKind.Area, "donut" => ChartKind.Donut, "pareto" => ChartKind.Pareto,
        "heatmap" => ChartKind.HeatMap, "gauge" => ChartKind.Gauge, _ => ChartKind.Column,
    };

    public static readonly (string Key, string Label, string Icon)[] Types =
    [
        ("column", "Column", "bi-bar-chart"), ("stacked_column", "Stacked column", "bi-bar-chart-steps"),
        ("bar", "Bar", "bi-bar-chart-line"), ("stacked_bar", "Stacked bar", "bi-distribute-horizontal"),
        ("line", "Line", "bi-graph-up"), ("area", "Area", "bi-graph-up-arrow"), ("donut", "Donut", "bi-pie-chart"),
        ("pareto", "Pareto", "bi-sort-down"), ("heatmap", "Heat map", "bi-grid-3x3"), ("gauge", "Gauge", "bi-speedometer2"),
    ];

    public static readonly (string Key, string Label)[] RangeOptions =
    [
        ("none", "All dates"), ("ytd", "Year to date"), ("qtd", "Quarter to date"), ("mtd", "Month to date"), ("wtd", "Week to date"),
        ("last_n_days", "Last N days to date"), ("last_week", "Last week"), ("last_month", "Last month"),
        ("last_quarter", "Last quarter"), ("last_year", "Last year"), ("custom", "Specific dates"),
    ];

    public static readonly (string Key, string Label)[] DateGroups =
        [("day", "Day"), ("week", "Week"), ("month", "Month"), ("quarter", "Quarter"), ("year", "Year")];
}

public sealed class GaugeRangeDef
{
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>ok | warn | bad | info</summary>
    public string Tone { get; set; } = "ok";
    public string? Label { get; set; }
}

/// <summary>A column a chart can use. Kind: phase | form | status | location | user | users | keyword | keywords | bool | text | date | number.</summary>
public sealed record ChartColumn(string Key, string Label, string Kind, FieldDefinition? Field = null)
{
    public bool IsDate => Kind == "date";
    public bool IsNumber => Kind == "number";
    public bool CanGroup => Kind is not ("number");
}

public static class ChartColumns
{
    /// <summary>System columns plus the chartable fields of the view's forms.</summary>
    public static List<ChartColumn> For(IEnumerable<FormDefinition> forms)
    {
        var list = new List<ChartColumn>
        {
            new("phase", "Phase", "phase"), new("form", "Type", "form"), new("status", "Status", "status"),
            new("location", "Location", "location"), new("creator", "Created by", "user"), new("assignee", "Assigned to", "users"),
            new("created", "Created", "date"), new("modified", "Modified", "date"), new("due", "Due date", "date"),
        };
        foreach (var f in forms.SelectMany(f => f.Fields).DistinctBy(f => f.Key))
        {
            var kind = f.Type switch
            {
                FieldType.Keyword => "keyword", FieldType.KeywordMulti => "keywords", FieldType.YesNo => "bool",
                FieldType.User => "user", FieldType.UserMulti => "users", FieldType.Date or FieldType.DateTime => "date",
                FieldType.Number or FieldType.Currency => "number", FieldType.Text => "text",
                _ => null,
            };
            if (kind == null || list.Any(c => c.Key == f.Key)) continue;
            list.Add(new ChartColumn(f.Key, f.Label, kind, f));
        }
        return list;
    }

    public static List<ChartColumn> For(ViewService views, ViewDefinition v) => For(views.FormsOf(v));
}

public sealed record ChartIssue(string Field, string Message);

/// <summary>
/// RPT-105 / INV-124: the rule set every saved chart satisfies. Pure (no database), so it is used by the builder,
/// on save, before every evaluation and by the tests.
/// </summary>
public static class ChartValidator
{
    private static readonly HashSet<string> KnownTypes = ChartDefinition.Types.Select(t => t.Key).ToHashSet();
    private static readonly HashSet<string> KnownRanges = ChartDefinition.RangeOptions.Select(t => t.Key).ToHashSet();
    private static readonly HashSet<string> KnownGroups = ChartDefinition.DateGroups.Select(t => t.Key).ToHashSet();
    private static readonly HashSet<string> LimitTypes = ["column", "bar", "line", "pareto", "stacked_column", "stacked_bar"];

    public static List<ChartIssue> Validate(ChartDefinition d, IReadOnlyList<ChartColumn>? columns = null)
    {
        var issues = new List<ChartIssue>();
        void Err(string field, string msg) => issues.Add(new ChartIssue(field, msg));
        ChartColumn? Col(string? key) => key == null || columns == null ? null : columns.FirstOrDefault(c => c.Key == key);

        if (string.IsNullOrWhiteSpace(d.App)) Err("App", "Choose the application the chart reads from.");
        if (string.IsNullOrWhiteSpace(d.View)) Err("View", "Choose the view the chart reads from.");
        if (!KnownTypes.Contains(d.Type)) Err("Type", $"Unknown chart type '{d.Type}'.");
        var gauge = d.Type == "gauge";

        // Category
        ChartColumn? cat = null;
        if (!gauge)
        {
            if (string.IsNullOrWhiteSpace(d.Category)) Err("Category", "Choose a category (the X axis).");
            else if (columns != null)
            {
                cat = Col(d.Category);
                if (cat == null) Err("Category", $"The category column '{d.Category}' isn't in this view's forms.");
                else if (!cat.CanGroup) Err("Category", $"{cat.Label} is a number field and can't be a category. Use it as the value instead.");
            }
        }
        // Date grouping (RPT-105: only when the first category or the Compare To column is a date)
        if (d.CategoryDates != null)
        {
            if (!KnownGroups.Contains(d.CategoryDates)) Err("CategoryDates", "Group dates by day, week, month, quarter or year.");
            else if (columns != null && cat is { IsDate: false }) Err("CategoryDates", "Group dates by applies only when the category or Compare To is a date column.");
        }

        // Value
        if (d.Aggregate is not ("count" or "sum" or "avg")) Err("Aggregate", "The value is Count, Sum or Average.");
        if (d.Aggregate == "avg" && string.IsNullOrEmpty(d.ValueField)) Err("Aggregate", "Average isn't allowed with Count. Choose a number field to average.");
        if (d.Aggregate == "sum" && string.IsNullOrEmpty(d.ValueField)) Err("ValueField", "Choose the number field to sum.");
        if (d.Aggregate != "count" && !string.IsNullOrEmpty(d.ValueField) && columns != null)
        {
            var vf = Col(d.ValueField);
            if (vf == null) Err("ValueField", $"The value field '{d.ValueField}' isn't in this view's forms.");
            else if (!vf.IsNumber) Err("ValueField", $"{vf.Label} isn't a number field, so it can't be summed or averaged.");
        }

        // Compare To
        var hasCompare = !string.IsNullOrEmpty(d.Compare);
        if (d.Type is "stacked_column" or "stacked_bar" or "heatmap" && !hasCompare)
            Err("Compare", d.Type == "heatmap" ? "A heat map needs a Compare To column (its columns)." : "Stacked charts need a Compare To column (the stacked segments).");
        if (hasCompare)
        {
            if (d.Type is "pareto" or "gauge" or "donut")
                Err("Compare", $"Compare To isn't allowed for {TypeName(d.Type)} charts.");
            if (d.Limits != "none") Err("Compare", "Compare To isn't allowed with control limits.");
            if (d.Compare == d.Category) Err("Compare", "Compare To must be a different column from the category.");
            if (columns != null)
            {
                var cmp = Col(d.Compare);
                if (cmp == null) Err("Compare", $"The Compare To column '{d.Compare}' isn't in this view's forms.");
                else if (!cmp.CanGroup) Err("Compare", $"{cmp.Label} is a number field and can't split series.");
                else if (d.CompareDates != null && !cmp.IsDate) Err("CompareDates", "Group dates by applies only when the category or Compare To is a date column.");
            }
        }
        else if (d.CompareDates != null) Err("CompareDates", "Group dates by applies only when the category or Compare To is a date column.");
        if (d.CompareDates != null && !KnownGroups.Contains(d.CompareDates)) Err("CompareDates", "Group dates by day, week, month, quarter or year.");

        // Sort (RPT-105: no effect with Compare To; hidden for Pareto, pie and gauge)
        if (d.Sort is not ("none" or "asc" or "desc")) Err("Sort", "Sort is ascending, descending or none.");
        else if (d.Sort != "none")
        {
            if (hasCompare) Err("Sort", "Sorting has no effect with Compare To. Set Sort to None.");
            else if (d.Type is "pareto" or "donut" or "gauge" or "heatmap") Err("Sort", $"Sorting isn't available for {TypeName(d.Type)} charts (Pareto always sorts descending).");
        }

        // Date range (RPT-202)
        if (!KnownRanges.Contains(d.Range)) Err("Range", $"Unknown date range '{d.Range}'.");
        if (d.Range == "last_n_days" && d.RangeDays is not > 0) Err("RangeDays", "Last N days needs N to be a positive whole number.");
        if (d.Range == "last_n_days" && d.RangeDays > 3660) Err("RangeDays", "Last N days can look back at most 3,660 days.");
        if (d.Range == "custom")
        {
            if (d.From == null && d.To == null) Err("From", "Specific dates need a start date, an end date or both.");
            if (d.From is { } f && d.To is { } t && f > t) Err("To", "The end date must be on or after the start date.");
        }
        if (d.Range != "none" && columns != null && d.DateField != null && Col(d.DateField) is { IsDate: false })
            Err("DateField", "The date-range filter needs a date column.");

        // Location (RPT-203)
        if (d.LocationScope is not ("all" or "only" or "lower" or "higher" or "both")) Err("LocationScope", "Unknown location option.");
        else if (d.LocationScope != "all" && d.LocationId == null && !d.LocationMine) Err("LocationId", "Choose a location, or use your primary location.");

        if (d.Top is < 1 or > 50) Err("Top", "Show between 1 and 50 categories.");

        // Control limits (RPT-301)
        if (d.Limits is not ("none" or "calculated" or "manual")) Err("Limits", "Control limits are none, calculated or manual.");
        else if (d.Limits != "none")
        {
            if (!LimitTypes.Contains(d.Type)) Err("Limits", "Control limits are available for column, bar, line, Pareto and stacked charts.");
            if (d.Limits == "manual")
            {
                if (d.Lcl == null && d.Ucl == null && d.Center == null) Err("Ucl", "Enter at least one manual limit (LCL, average or UCL).");
                if (d.Lcl is { } l && d.Ucl is { } u && l > u) Err("Lcl", "In manual control limits, LCL must be less than or equal to UCL.");
            }
            else
            {
                if (d.Aggregate != "count") Err("Limits", "Calculated limits are c-chart limits (C ± 3√C), so the value must be Count.");
                if (d.LimitsPeriod is not ("trailing_12_months" or "last_calendar_year" or "custom")) Err("LimitsPeriod", "Choose the period the limits are calculated over.");
                if (d.LimitsPeriod == "custom")
                {
                    if (d.LimitsFrom == null || d.LimitsTo == null) Err("LimitsFrom", "A specific limits period needs a start and an end date.");
                    else if (d.LimitsFrom > d.LimitsTo) Err("LimitsTo", "The limits period must end on or after its start.");
                }
            }
        }

        // Gauge (RPT-105: ranges can't overlap; min ≤ max; blank first min / last max extend to the edge)
        if (gauge)
        {
            var min = d.GaugeMin ?? 0;
            var max = d.GaugeMax ?? 100;
            if (min >= max) Err("GaugeMax", "The gauge maximum must be greater than its minimum.");
            var ranges = d.Ranges.Where(r => r.Min != null || r.Max != null).ToList();
            for (var i = 0; i < ranges.Count; i++)
            {
                var r = ranges[i];
                if (r.Min == null && i > 0) Err("Ranges", $"Range {i + 1} needs a minimum (only the first range may leave it blank).");
                if (r.Max == null && i < ranges.Count - 1) Err("Ranges", $"Range {i + 1} needs a maximum (only the last range may leave it blank).");
                if (r.Min is { } a && r.Max is { } b && a > b) Err("Ranges", $"Range {i + 1}: the minimum must be less than or equal to the maximum.");
                if ((r.Min is { } a2 && (a2 < min || a2 > max)) || (r.Max is { } b2 && (b2 < min || b2 > max)))
                    Err("Ranges", $"Range {i + 1} must sit inside the gauge scale ({min:0.##}–{max:0.##}).");
            }
            var resolved = ranges.Select(r => (Lo: r.Min ?? min, Hi: r.Max ?? max)).OrderBy(r => r.Lo).ToList();
            for (var i = 1; i < resolved.Count; i++)
                if (resolved[i].Lo < resolved[i - 1].Hi) { Err("Ranges", "Gauge ranges can't overlap."); break; }
        }
        return issues;
    }

    /// <summary>Data rules checked when the chart is generated (RPT-105: pie charts reject negative values).</summary>
    public static List<ChartIssue> ValidateData(ChartDefinition d, IEnumerable<double> values)
    {
        if (d.Type == "donut" && values.Any(v => v < 0))
            return [new ChartIssue("Type", "Donut charts can't show negative values. Use a column or bar chart instead.")];
        return [];
    }

    public static string TypeName(string type) => ChartDefinition.Types.FirstOrDefault(t => t.Key == type).Label?.ToLowerInvariant() ?? type;
}

/// <summary>RPT-202 / INV-125: relative ranges are resolved against the generation time, never the creation time.</summary>
public static class RelativeDates
{
    public static DateOnly WeekStart(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>Inclusive [From, To] for the range at <paramref name="today"/>. Nulls are open ends.</summary>
    public static (DateOnly? From, DateOnly? To) Resolve(string range, int? days, DateOnly? from, DateOnly? to, DateOnly today)
    {
        switch (range)
        {
            case "ytd": return (new DateOnly(today.Year, 1, 1), today);
            case "qtd": return (new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today);
            case "mtd": return (new DateOnly(today.Year, today.Month, 1), today);
            case "wtd": return (WeekStart(today), today);
            case "last_n_days": return (today.AddDays(-(Math.Max(1, days ?? 1) - 1)), today);
            case "last_week": { var s = WeekStart(today).AddDays(-7); return (s, s.AddDays(6)); }
            case "last_month": { var s = new DateOnly(today.Year, today.Month, 1).AddMonths(-1); return (s, s.AddMonths(1).AddDays(-1)); }
            case "last_quarter": { var s = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1).AddMonths(-3); return (s, s.AddMonths(3).AddDays(-1)); }
            case "last_year": return (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31));
            case "custom": return (from, to);
            default: return (null, null);
        }
    }

    public static (DateOnly? From, DateOnly? To) Resolve(ChartDefinition d, DateOnly today) => Resolve(d.Range, d.RangeDays, d.From, d.To, today);

    public static string Describe(ChartDefinition d, DateOnly today)
    {
        if (d.Range == "none") return "All dates";
        var (f, t) = Resolve(d, today);
        var name = d.Range == "last_n_days" ? $"Last {d.RangeDays} days to date" : ChartDefinition.RangeOptions.FirstOrDefault(r => r.Key == d.Range).Label ?? d.Range;
        return $"{name} ({Span(f, t)})";
    }

    public static string Span(DateOnly? f, DateOnly? t)
    {
        static string D(DateOnly x) => x.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return (f, t) switch
        {
            ({ } a, { } b) when a == b => D(a),
            ({ } a, { } b) => $"{(a.Year == b.Year ? a.ToString("d MMM", CultureInfo.InvariantCulture) : D(a))} – {D(b)}",
            ({ } a, null) => $"from {D(a)}",
            (null, { } b) => $"until {D(b)}",
            _ => "all dates",
        };
    }

    /// <summary>RPT-301: the period calculated control limits are based on.</summary>
    public static (DateOnly From, DateOnly To) LimitsPeriod(ChartDefinition d, DateOnly today) => d.LimitsPeriod switch
    {
        "last_calendar_year" => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
        "custom" when d.LimitsFrom is { } f && d.LimitsTo is { } t => (f, t),
        _ => (new DateOnly(today.Year, today.Month, 1).AddMonths(-12), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
    };
}

/// <summary>Date bucketing for date categories (RPT-104 "Group dates by").</summary>
public static class DateBuckets
{
    public static DateOnly Start(DateOnly d, string g) => g switch
    {
        "day" => d,
        "week" => RelativeDates.WeekStart(d),
        "quarter" => new DateOnly(d.Year, (d.Month - 1) / 3 * 3 + 1, 1),
        "year" => new DateOnly(d.Year, 1, 1),
        _ => new DateOnly(d.Year, d.Month, 1),
    };

    public static DateOnly Next(DateOnly s, string g) => g switch
    {
        "day" => s.AddDays(1), "week" => s.AddDays(7), "quarter" => s.AddMonths(3), "year" => s.AddYears(1), _ => s.AddMonths(1),
    };

    public static string Key(DateOnly s) => s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Label(DateOnly s, string g, bool withYear) => g switch
    {
        "day" or "week" => s.ToString(withYear ? "d MMM yy" : "d MMM", CultureInfo.InvariantCulture),
        "quarter" => $"Q{(s.Month - 1) / 3 + 1} {s.Year}",
        "year" => s.Year.ToString(CultureInfo.InvariantCulture),
        _ => s.ToString(withYear ? "MMM yyyy" : "MMM", CultureInfo.InvariantCulture),
    };

    public static string Name(string? g) => ChartDefinition.DateGroups.FirstOrDefault(x => x.Key == g).Label ?? "Month";
}
