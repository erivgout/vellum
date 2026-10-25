using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Modules.Insights.Charts;
using static Vellum.Modules.Insights.Charts.ChartMath;

namespace Vellum.Modules.Insights;

/// <summary>A KPI tile (stat tile contract: label · value · optional delta · optional 12-point trend).</summary>
public sealed class Kpi
{
    public required string Label { get; init; }
    public required double Value { get; init; }
    public ValueFormat Format { get; init; } = ValueFormat.Count;
    public string? Sub { get; init; }
    public double? Previous { get; init; }
    /// <summary>true: up is good; false: up is bad; null: neutral.</summary>
    public bool? UpIsGood { get; init; }
    public double[]? Trend { get; init; }
    public string? Href { get; init; }
    public string Icon { get; init; } = "bi-bar-chart";
    public string Tone { get; init; } = "info";
    public string? ValueClass { get; init; }
}

public sealed record AppSummary(AppDefinition App, int Open, int Overdue, double[] CreatedWeekly);

public sealed record OverdueItem(Guid Id, string Number, string Title, AppDefinition App, FormDefinition Form, string? Phase, DateOnly Due, int DaysOverdue, List<int> Assignees);

public sealed class HomeDashboard
{
    public DateTime At { get; set; }
    public List<Kpi> Tiles { get; } = [];
    public List<ChartData> Charts { get; } = [];
    public List<AppSummary> Apps { get; } = [];
    public List<OverdueItem> TopOverdue { get; } = [];
    public string? CycleApp { get; set; }
    public List<(string Key, string Name)> CycleChoices { get; } = [];
    public ChartData Chart(string id) => Charts.First(c => c.Id == id);
}

public sealed class AppDashboard
{
    public required AppDefinition App { get; init; }
    public DateTime At { get; set; }
    public string Period { get; set; } = "12m";
    public DateOnly? From { get; set; }
    public DateOnly To { get; set; }
    public List<Kpi> Tiles { get; } = [];
    public List<ChartData> Charts { get; } = [];
    public List<string> ParetoIds { get; } = [];
    public ChartData? Chart(string id) => Charts.FirstOrDefault(c => c.Id == id);
}

/// <summary>
/// Packaged KPI dashboards (RPT-701), computed generically from whatever applications are configured. Every number
/// comes from the user's security-trimmed record set (INV-081) and links to the records behind it (RPT-403).
/// Results are cached per user for up to five minutes (NFR-006, RPT-704).
/// </summary>
[ScopedService]
public sealed class DashboardService(VellumDbContext db, InsightsQueries q, ConfigCache cfg, UserDirectory dir, CurrentUser me, AccessService access)
{
    public const int Weeks = 12;
    private ConfigModel Cfg => cfg.Current;

    // ------------------------------------------------------------------ helpers

    private static DateOnly Today => BusinessCalendar.Today;

    private static List<DateOnly> WeekStarts()
    {
        var w = RelativeDates.WeekStart(Today);
        return Enumerable.Range(0, Weeks).Select(k => w.AddDays(-7 * (Weeks - 1 - k))).ToList();
    }

    private static List<ChartCategory> WeekCategories(List<DateOnly> weeks, Func<DateOnly, string?>? href = null) =>
        weeks.Select((w, i) => new ChartCategory(DateBuckets.Key(w), DateBuckets.Label(w, "week", i == 0 || w.Year != weeks[i - 1].Year), href?.Invoke(w))).ToList();

    private static double[] PerWeek(List<DateOnly> weeks, IEnumerable<DateTime> times)
    {
        var counts = new double[weeks.Count];
        foreach (var t in times)
        {
            var d = DateOnly.FromDateTime(t);
            var k = weeks.FindLastIndex(w => w <= d);
            if (k >= 0 && d < weeks[k].AddDays(7)) counts[k]++;
        }
        return counts;
    }

    private static int InWindow(IEnumerable<DateTime> times, int fromDaysAgo, int toDaysAgo)
    {
        var a = DateTime.UtcNow.AddDays(-fromDaysAgo);
        var b = DateTime.UtcNow.AddDays(-toDaysAgo);
        return times.Count(t => t >= a && t < b);
    }


    // ------------------------------------------------------------------ Insights home (RPT-701)

    public Task<(HomeDashboard Value, DateTime At)> HomeAsync(string? cycleApp, bool refresh) =>
        InsightsCache.GetAsync(me.Id, $"home|{Cfg.Version}|{cycleApp}", refresh, () => BuildHomeAsync(cycleApp));

    private async Task<HomeDashboard> BuildHomeAsync(string? cycleApp)
    {
        var d = new HomeDashboard { At = DateTime.UtcNow };
        var apps = q.Apps();
        var today = Today;
        var weeks = WeekStarts();
        var since = InsightsQueries.Utc(weeks[0] < today.AddDays(-60) ? weeks[0] : today.AddDays(-60));
        var readable = q.Readable(apps);
        var open = readable.Where(r => r.PhaseType == PhaseType.Normal && !r.IsArchived);
        var readIds = readable.Select(r => r.Id);

        // Open and overdue by application
        var byApp = await open.GroupBy(r => r.AppId)
            .Select(g => new { AppId = g.Key, Open = g.Count(), Overdue = g.Count(r => r.DueDate < today) }).ToListAsync();
        var totalOpen = byApp.Sum(x => x.Open);
        var totalOverdue = byApp.Sum(x => x.Overdue);

        // Weekly flows
        var created = await readable.Where(r => r.CreatedAt >= since).Select(r => new { r.AppId, r.CreatedAt }).ToListAsync();
        var done = q.CompletedPhaseIds();
        var completed = await (from e in db.PhaseEntries
                               join r in readable on e.RecordId equals r.Id
                               where done.Contains(e.PhaseId) && e.EnteredAt >= since
                               select new { r.AppId, e.EnteredAt }).ToListAsync();
        var signatures = await db.Signatures.Where(s => s.SignedAt >= since && readIds.Contains(s.RecordId)).Select(s => s.SignedAt).ToListAsync();

        // Phase exits with a due date in the last 90 days (on-time completion)
        var since90 = DateTime.UtcNow.AddDays(-90);
        var exits = await (from e in db.PhaseEntries
                           join r in readable on e.RecordId equals r.Id
                           where e.ExitedAt >= since90 && e.DueDate != null && e.ExitAction != "void"
                           select new { r.AppId, e.ExitedAt, e.DueDate }).ToListAsync();
        var onTimeFlags = exits.Select(e => (e.AppId, OnTime: DateOnly.FromDateTime(e.ExitedAt!.Value) <= e.DueDate!.Value)).ToList();
        double? onTimePct = onTimeFlags.Count == 0 ? null : Math.Round(100.0 * onTimeFlags.Count(x => x.OnTime) / onTimeFlags.Count, 1);

        var createdW = PerWeek(weeks, created.Select(c => c.CreatedAt));
        var completedW = PerWeek(weeks, completed.Select(c => c.EnteredAt));
        var signedW = PerWeek(weeks, signatures);
        var d30 = today.AddDays(-29);

        // ---- KPI tiles
        d.Tiles.Add(new Kpi
        {
            Label = "Open items", Value = totalOpen, Icon = "bi-folder2-open", Tone = "info",
            Sub = $"across {Plural(apps.Count(a => byApp.Any(b => b.AppId == a.Id)), "application")}", Href = new RecordFilter { Status = "open", Title = "Open items" }.Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Overdue", Value = totalOverdue, Icon = totalOverdue > 0 ? "bi-exclamation-octagon-fill" : "bi-check-circle-fill", Tone = totalOverdue > 0 ? "bad" : "ok",
            ValueClass = totalOverdue > 0 ? "text-danger" : null,
            Sub = totalOpen > 0 ? $"{Percent((double)totalOverdue / totalOpen)} of open items" : "Nothing open",
            Href = new RecordFilter { Status = "overdue", Title = "Overdue items" }.Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Created, last 30 days", Value = InWindow(created.Select(c => c.CreatedAt), 30, 0), Previous = InWindow(created.Select(c => c.CreatedAt), 60, 30),
            Trend = createdW, Icon = "bi-plus-circle", Tone = "info",
            Href = new RecordFilter { Status = "all", On = "created", From = d30, To = today, Title = "Created in the last 30 days" }.Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Completed, last 30 days", Value = InWindow(completed.Select(c => c.EnteredAt), 30, 0), Previous = InWindow(completed.Select(c => c.EnteredAt), 60, 30),
            UpIsGood = true, Trend = completedW, Icon = "bi-check2-circle", Tone = "ok",
            Href = new RecordFilter { Status = "all", On = "completed", From = d30, To = today, Title = "Completed in the last 30 days" }.Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Phases completed on time", Value = onTimePct ?? 0, Format = ValueFormat.Percent, Icon = "bi-stopwatch",
            Tone = onTimePct is null ? "neutral" : onTimePct >= 90 ? "ok" : onTimePct >= 70 ? "warn" : "bad",
            Sub = onTimeFlags.Count == 0 ? "No phases with due dates finished in 90 days" : $"{onTimeFlags.Count(x => x.OnTime)} of {onTimeFlags.Count} in the last 90 days",
            Href = new RecordFilter { Status = "all", On = "late", From = today.AddDays(-89), To = today, Title = "Late phase completions, last 90 days" }.Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Signatures, last 30 days", Value = InWindow(signatures, 30, 0), Previous = InWindow(signatures, 60, 30),
            Trend = signedW, Icon = "bi-pen", Tone = "violet",
            Href = new RecordFilter { Status = "all", On = "signed", From = d30, To = today, Title = "Signed in the last 30 days" }.Href(),
        });

        // ---- Open items by application (overdue emphasised, the rest de-emphasised)
        var appRows = byApp.Select(x => (App: Cfg.AppsById[x.AppId], x.Open, x.Overdue)).OrderByDescending(x => x.Open).ThenBy(x => x.App.Name).ToList();
        d.Charts.Add(new ChartData
        {
            Id = "open-by-app", Title = "Open items by application", Kind = ChartKind.StackedBar, CategoryHeader = "Application",
            Categories = appRows.Select(x => new ChartCategory(x.App.Key, x.App.Name, $"/insights/apps/{x.App.Key}")).ToList(),
            Series =
            [
                new ChartSeries { Key = "overdue", Name = "Overdue", Tone = "bad", Values = appRows.Select(x => (double?)x.Overdue).ToArray(),
                    Hrefs = appRows.Select(x => new RecordFilter { App = x.App.Key, Status = "overdue", Title = $"Overdue · {x.App.Name}" }.Href()).ToArray() },
                new ChartSeries { Key = "ontrack", Name = "Not overdue", Tone = "muted", Values = appRows.Select(x => (double?)(x.Open - x.Overdue)).ToArray(),
                    Hrefs = appRows.Select(x => new RecordFilter { App = x.App.Key, Status = "ontrack", Title = $"Open, not overdue · {x.App.Name}" }.Href()).ToArray() },
            ],
            EmptyText = "Nothing is open in the applications you can see.",
        });

        // ---- Overdue by owner (active assignees of overdue records)
        var overdueIds = open.Where(r => r.DueDate < today).Select(r => r.Id);
        var pairs = await db.Assignments.Where(a => a.Status == AssignmentStatus.Active && overdueIds.Contains(a.RecordId))
            .Select(a => new { a.RecordId, a.UserId }).Distinct().ToListAsync();
        var owners = pairs.GroupBy(p => p.UserId).Select(g => (User: g.Key, Count: g.Count())).OrderByDescending(x => x.Count).ThenBy(x => dir.Name(x.User)).Take(10).ToList();
        d.Charts.Add(new ChartData
        {
            Id = "overdue-by-owner", Title = "Overdue by owner", Kind = ChartKind.Bar, CategoryHeader = "Assignee", NoShare = false,
            Categories = owners.Select(o => new ChartCategory(o.User.ToString(), dir.Name(o.User))).ToList(),
            Series = [new ChartSeries { Key = "overdue", Name = "Overdue records", Tone = "bad", Values = owners.Select(o => (double?)o.Count).ToArray(),
                Hrefs = owners.Select(o => new RecordFilter { Status = "overdue", Assignee = o.User, Title = $"Overdue · {dir.Name(o.User)}" }.Href()).ToArray() }],
            EmptyText = "No overdue assignments.", Note = "Top 10 current assignees. A record with several assignees counts for each of them.",
        });

        // ---- Created vs completed per week
        d.Charts.Add(new ChartData
        {
            Id = "throughput", Title = "Created vs completed per week", Kind = ChartKind.Column, Ordered = true, CategoryHeader = "Week starting",
            Categories = WeekCategories(weeks), NoShare = true, Height = 240,
            Series =
            [
                new ChartSeries { Key = "created", Name = "Created", Tone = "s1", Values = createdW.Select(v => (double?)v).ToArray(),
                    Hrefs = weeks.Select(w => new RecordFilter { Status = "all", On = "created", From = w, To = w.AddDays(6), Title = $"Created week of {w:d MMM}" }.Href()).ToArray() },
                new ChartSeries { Key = "completed", Name = "Completed", Tone = "s2", Values = completedW.Select(v => (double?)v).ToArray(),
                    Hrefs = weeks.Select(w => new RecordFilter { Status = "all", On = "completed", From = w, To = w.AddDays(6), Title = $"Completed week of {w:d MMM}" }.Href()).ToArray() },
            ],
            EmptyText = "Nothing was created or completed in the last 12 weeks.",
        });

        // ---- Signatures per week
        d.Charts.Add(new ChartData
        {
            Id = "signatures", Title = "Signatures per week", Kind = ChartKind.Column, Ordered = true, CategoryHeader = "Week starting", NoShare = true, Height = 240,
            Categories = WeekCategories(weeks),
            Series = [new ChartSeries { Key = "signed", Name = "Signatures", Tone = "s7", Values = signedW.Select(v => (double?)v).ToArray(),
                Hrefs = weeks.Select(w => new RecordFilter { Status = "all", On = "signed", From = w, To = w.AddDays(6), Title = $"Signed week of {w:d MMM}" }.Href()).ToArray() }],
            EmptyText = "No electronic signatures in the last 12 weeks.",
        });

        // ---- On-time phase completion: gauge + by application
        d.Charts.Add(new ChartData
        {
            Id = "on-time", Title = "Phases completed on time", Kind = ChartKind.Gauge, Format = ValueFormat.Percent,
            GaugeValue = onTimePct, GaugeMin = 0, GaugeMax = 100, GaugeLabel = "on time, last 90 days",
            Bands = [new GaugeBand(0, 70, "bad", "Below 70%: action needed"), new GaugeBand(70, 90, "warn", "70–90%: watch"), new GaugeBand(90, 100, "ok", "90% or better: on target")],
            GaugeHref = new RecordFilter { Status = "all", On = "late", From = today.AddDays(-89), To = today, Title = "Late phase completions, last 90 days" }.Href(),
            EmptyText = "No phases with due dates finished in the last 90 days.",
        });
        var onTimeApps = onTimeFlags.GroupBy(x => x.AppId).Select(g => (App: Cfg.AppsById[g.Key], Pct: Math.Round(100.0 * g.Count(x => x.OnTime) / g.Count(), 1), N: g.Count()))
            .OrderBy(x => x.Pct).ThenBy(x => x.App.Name).ToList();
        d.Charts.Add(new ChartData
        {
            Id = "on-time-by-app", Title = "On-time phase completion by application", Kind = ChartKind.Bar, Format = ValueFormat.Percent, NoShare = true, AverageRow = true,
            CategoryHeader = "Application",
            Categories = onTimeApps.Select(x => new ChartCategory(x.App.Key, $"{x.App.Name}", $"/insights/apps/{x.App.Key}")).ToList(),
            Series = [new ChartSeries { Key = "pct", Name = "On time %", Tone = "s1", Values = onTimeApps.Select(x => (double?)x.Pct).ToArray(),
                Hrefs = onTimeApps.Select(x => new RecordFilter { App = x.App.Key, Status = "all", On = "late", From = today.AddDays(-89), To = today, Title = $"Late phase completions · {x.App.Name}" }.Href()).ToArray() }],
            EmptyText = "No phases with due dates finished in the last 90 days.", Note = "Phase exits with a due date in the last 90 days. Bars link to the late ones.",
        });

        // ---- Median cycle time per phase for a chosen application
        var withPhases = apps.Where(a => byApp.Any(b => b.AppId == a.Id) || created.Any(c => c.AppId == a.Id)).ToList();
        foreach (var a in (withPhases.Count > 0 ? withPhases : apps)) d.CycleChoices.Add((a.Key, a.Name));
        var cycle = apps.FirstOrDefault(a => a.Key == cycleApp)
            ?? apps.Where(a => byApp.Any(b => b.AppId == a.Id)).OrderByDescending(a => byApp.First(b => b.AppId == a.Id).Open).FirstOrDefault()
            ?? apps.FirstOrDefault();
        d.CycleApp = cycle?.Key;
        d.Charts.Add(cycle == null ? Empty("cycle-time", "Median cycle time per phase") : await CycleTimeAsync(cycle, today.AddDays(-179), today, "cycle-time", $"Median days per phase · {cycle.Name}"));

        // ---- Applications grid and top overdue
        foreach (var a in apps)
        {
            var row = byApp.FirstOrDefault(b => b.AppId == a.Id);
            d.Apps.Add(new AppSummary(a, row?.Open ?? 0, row?.Overdue ?? 0, PerWeek(weeks, created.Where(c => c.AppId == a.Id).Select(c => c.CreatedAt))));
        }
        d.Apps.Sort((x, y) => y.Open != x.Open ? y.Open.CompareTo(x.Open) : string.Compare(x.App.Name, y.App.Name, StringComparison.CurrentCulture));

        var top = await open.Where(r => r.DueDate < today).OrderBy(r => r.DueDate).ThenBy(r => r.Number).Take(10)
            .Select(r => new { r.Id, r.Number, r.Title, r.AppId, r.FormId, r.PhaseId, r.DueDate }).ToListAsync();
        var topIds = top.Select(t => t.Id).ToList();
        var topAssignees = await db.Assignments.Where(a => topIds.Contains(a.RecordId) && a.Status == AssignmentStatus.Active).Select(a => new { a.RecordId, a.UserId }).ToListAsync();
        foreach (var t in top)
            d.TopOverdue.Add(new OverdueItem(t.Id, t.Number, t.Title, Cfg.AppsById[t.AppId], Cfg.FormsById[t.FormId], Cfg.Phase(t.PhaseId)?.Name, t.DueDate!.Value,
                today.DayNumber - t.DueDate!.Value.DayNumber, topAssignees.Where(a => a.RecordId == t.Id).Select(a => a.UserId).Distinct().ToList()));
        return d;
    }

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static ChartData Empty(string id, string title) => new() { Id = id, Title = title, EmptyText = "No applications with workflows yet." };

    /// <summary>Median and 90th-percentile days spent in each Normal phase, from phase tracking (PLT-1004).</summary>
    private async Task<ChartData> CycleTimeAsync(AppDefinition app, DateOnly from, DateOnly to, string id, string title, int width = 560)
    {
        var rq = q.ReadableMain(app);
        var a = InsightsQueries.Utc(from);
        var b = InsightsQueries.Utc(to.AddDays(1));
        var rows = await (from e in db.PhaseEntries
                          join r in rq on e.RecordId equals r.Id
                          where e.ExitedAt >= a && e.ExitedAt < b
                          select new { e.PhaseId, e.EnteredAt, e.ExitedAt }).ToListAsync();
        var phases = InsightsQueries.MainForms(app).SelectMany(f => f.Workflows).SelectMany(w => w.Phases)
            .Where(p => p.Type == PhaseType.Normal).GroupBy(p => p.Key).Select(g => g.OrderBy(p => p.Order).First()).OrderBy(p => p.Order).ToList();
        var stats = phases.Select(p =>
        {
            var ids = Cfg.PhasesById.Values.Where(x => x.Key == p.Key && x.Workflow.Form.AppId == app.Id).Select(x => x.Id).ToHashSet();
            var days = rows.Where(r => ids.Contains(r.PhaseId)).Select(r => Math.Max(0, (r.ExitedAt!.Value - r.EnteredAt).TotalDays)).ToList();
            return (Phase: p, N: days.Count, Median: days.Count == 0 ? (double?)null : Math.Round(Median(days), 1), P90: days.Count == 0 ? (double?)null : Math.Round(Percentile(days, 90), 1));
        }).Where(s => s.N > 0).ToList();
        return new ChartData
        {
            Id = id, Title = title, Kind = ChartKind.Bar, Format = ValueFormat.Days, NoShare = true, HideTotal = true, CategoryHeader = "Phase", Width = width,
            Categories = stats.Select(s => new ChartCategory(s.Phase.Key, s.Phase.Name)).ToList(),
            Series =
            [
                new ChartSeries { Key = "median", Name = "Median", Tone = "s1", Values = stats.Select(s => s.Median).ToArray(),
                    Hrefs = stats.Select(s => new RecordFilter { App = app.Key, Status = "all", Phase = s.Phase.Key, On = "exited", From = from, To = to, Title = $"Left {s.Phase.Name}" }.Href()).ToArray() },
                new ChartSeries { Key = "p90", Name = "90th percentile", Tone = "muted", Values = stats.Select(s => s.P90).ToArray(),
                    Hrefs = stats.Select(s => new RecordFilter { App = app.Key, Status = "all", Phase = s.Phase.Key, On = "exited", From = from, To = to, Title = $"Left {s.Phase.Name}" }.Href()).ToArray() },
            ],
            EmptyText = "No phases were completed in this period.",
            Note = $"Calendar days from entering to leaving each phase, for {stats.Sum(s => s.N)} phase exits {RelativeDates.Span(from, to)}.",
        };
    }

    // ------------------------------------------------------------------ module dashboards (RPT-701)

    public static readonly (string Key, string Label)[] Periods = [("90d", "Last 90 days"), ("12m", "Last 12 months"), ("ytd", "Year to date"), ("all", "All time")];

    public static (DateOnly? From, DateOnly To) PeriodRange(string period) => period switch
    {
        "90d" => (Today.AddDays(-89), Today),
        "ytd" => (new DateOnly(Today.Year, 1, 1), Today),
        "all" => (null, Today),
        _ => (Today.AddMonths(-12).AddDays(1), Today),
    };

    public Task<(AppDashboard Value, DateTime At)> AppAsync(AppDefinition app, string period, bool refresh) =>
        InsightsCache.GetAsync(me.Id, $"app|{Cfg.Version}|{app.Key}|{period}", refresh, () => BuildAppAsync(app, period));

    private async Task<AppDashboard> BuildAppAsync(AppDefinition app, string period)
    {
        var today = Today;
        var (from, to) = PeriodRange(period);
        var d = new AppDashboard { App = app, At = DateTime.UtcNow, Period = period, From = from, To = to };
        var rq = q.ReadableMain(app);
        var open = rq.Where(r => r.PhaseType == PhaseType.Normal && !r.IsArchived);
        var weeks = WeekStarts();
        var since = InsightsQueries.Utc(weeks[0] < today.AddDays(-60) ? weeks[0] : today.AddDays(-60));
        var forms = InsightsQueries.MainForms(app);
        var done = q.CompletedPhaseIds();
        RecordFilter R(Action<RecordFilter> set) { var f = new RecordFilter { App = app.Key }; set(f); return f; }

        // ---- Current state
        var openRows = await open.Select(r => new { r.Id, r.PhaseId, r.DueDate, r.CreatedAt }).ToListAsync();
        var overdue = openRows.Count(r => r.DueDate < today);
        var created = await rq.Where(r => r.CreatedAt >= since).Select(r => r.CreatedAt).ToListAsync();
        var completed = await (from e in db.PhaseEntries join r in rq on e.RecordId equals r.Id
                               where done.Contains(e.PhaseId) && e.EnteredAt >= since select e.EnteredAt).ToListAsync();
        var createdW = PerWeek(weeks, created);
        var completedW = PerWeek(weeks, completed);

        // Period-scoped: record cycle time (created → completed) and on-time phase exits
        var pa = from is { } pf ? InsightsQueries.Utc(pf) : DateTime.MinValue;
        var cycle = await (from e in db.PhaseEntries join r in rq on e.RecordId equals r.Id
                           where done.Contains(e.PhaseId) && e.EnteredAt >= pa
                           select new { r.Id, r.CreatedAt, e.EnteredAt }).ToListAsync();
        var cycleDays = cycle.GroupBy(c => c.Id).Select(g => Math.Max(0, (g.Min(x => x.EnteredAt) - g.First().CreatedAt).TotalDays)).ToList();
        var exits = await (from e in db.PhaseEntries join r in rq on e.RecordId equals r.Id
                           where e.ExitedAt >= pa && e.DueDate != null && e.ExitAction != "void"
                           select new { e.ExitedAt, e.DueDate }).ToListAsync();
        var onTime = exits.Count(e => DateOnly.FromDateTime(e.ExitedAt!.Value) <= e.DueDate!.Value);
        double? onTimePct = exits.Count == 0 ? null : Math.Round(100.0 * onTime / exits.Count, 1);
        var d30 = today.AddDays(-29);
        var periodName = Periods.First(p => p.Key == period).Label.ToLowerInvariant();

        d.Tiles.Add(new Kpi { Label = "Open", Value = openRows.Count, Icon = "bi-folder2-open", Tone = "info", Href = R(f => { f.Status = "open"; f.Title = $"Open · {app.Name}"; }).Href(),
            Sub = $"{forms.Count switch { 1 => forms[0].Name, _ => Plural(forms.Count, "form") }}" });
        d.Tiles.Add(new Kpi
        {
            Label = "Overdue", Value = overdue, Icon = overdue > 0 ? "bi-exclamation-octagon-fill" : "bi-check-circle-fill", Tone = overdue > 0 ? "bad" : "ok",
            ValueClass = overdue > 0 ? "text-danger" : null, Sub = openRows.Count > 0 ? $"{Percent((double)overdue / openRows.Count)} of open" : "Nothing open",
            Href = R(f => { f.Status = "overdue"; f.Title = $"Overdue · {app.Name}"; }).Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Created, last 30 days", Value = InWindow(created, 30, 0), Previous = InWindow(created, 60, 30), Trend = createdW, Icon = "bi-plus-circle", Tone = "info",
            Href = R(f => { f.Status = "all"; f.On = "created"; f.From = d30; f.To = today; f.Title = $"Created in the last 30 days · {app.Name}"; }).Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Completed, last 30 days", Value = InWindow(completed, 30, 0), Previous = InWindow(completed, 60, 30), UpIsGood = true, Trend = completedW,
            Icon = "bi-check2-circle", Tone = "ok",
            Href = R(f => { f.Status = "all"; f.On = "completed"; f.From = d30; f.To = today; f.Title = $"Completed in the last 30 days · {app.Name}"; }).Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Median time to complete", Value = cycleDays.Count == 0 ? 0 : Math.Round(Median(cycleDays), 1), Format = ValueFormat.Days, Icon = "bi-hourglass-split", Tone = "violet",
            Sub = cycleDays.Count == 0 ? $"Nothing completed, {periodName}" : $"{Plural(cycleDays.Count, "record")} completed, {periodName}",
            Href = R(f => { f.Status = "all"; f.On = "completed"; f.From = from; f.To = today; f.Title = $"Completed · {app.Name}"; }).Href(),
        });
        d.Tiles.Add(new Kpi
        {
            Label = "Phases completed on time", Value = onTimePct ?? 0, Format = ValueFormat.Percent, Icon = "bi-stopwatch",
            Tone = onTimePct is null ? "neutral" : onTimePct >= 90 ? "ok" : onTimePct >= 70 ? "warn" : "bad",
            Sub = exits.Count == 0 ? $"No phases with due dates finished, {periodName}" : $"{onTime} of {exits.Count}, {periodName}",
            Href = R(f => { f.Status = "all"; f.On = "late"; f.From = from; f.To = today; f.Title = $"Late phase completions · {app.Name}"; }).Href(),
        });

        // ---- Open by phase (overdue emphasised)
        var phases = forms.SelectMany(f => f.Workflows).SelectMany(w => w.Phases).Where(p => p.Type == PhaseType.Normal)
            .GroupBy(p => p.Key).Select(g => g.OrderBy(p => p.Order).First()).OrderBy(p => p.Order).ToList();
        var phaseOf = openRows.Select(r => (r, Key: Cfg.Phase(r.PhaseId)?.Key)).ToList();
        var shown = phases.Where(p => phaseOf.Any(x => x.Key == p.Key)).ToList();
        d.Charts.Add(new ChartData
        {
            Id = "open-by-phase", Title = "Open by phase", Kind = ChartKind.StackedBar, CategoryHeader = "Phase",
            Categories = shown.Select(p => new ChartCategory(p.Key, p.Name, R(f => { f.Status = "open"; f.Phase = p.Key; f.Title = $"Open in {p.Name}"; }).Href())).ToList(),
            Series =
            [
                new ChartSeries { Key = "overdue", Name = "Overdue", Tone = "bad", Values = shown.Select(p => (double?)phaseOf.Count(x => x.Key == p.Key && x.r.DueDate < today)).ToArray(),
                    Hrefs = shown.Select(p => R(f => { f.Status = "overdue"; f.Phase = p.Key; f.Title = $"Overdue in {p.Name}"; }).Href()).ToArray() },
                new ChartSeries { Key = "ontrack", Name = "Not overdue", Tone = "muted", Values = shown.Select(p => (double?)phaseOf.Count(x => x.Key == p.Key && !(x.r.DueDate < today))).ToArray(),
                    Hrefs = shown.Select(p => R(f => { f.Status = "ontrack"; f.Phase = p.Key; f.Title = $"Open, not overdue, in {p.Name}"; }).Href()).ToArray() },
            ],
            EmptyText = $"Nothing is open in {app.Name}.",
        });

        // ---- Aging of open items (ordinal buckets)
        var buckets = new (string Label, int Min, int? Max)[] { ("0–7 days", 0, 7), ("8–30 days", 8, 30), ("31–60 days", 31, 60), ("61–90 days", 61, 90), ("Over 90 days", 91, null) };
        var ages = openRows.Select(r => (int)(DateTime.UtcNow - r.CreatedAt).TotalDays).ToList();
        d.Charts.Add(new ChartData
        {
            Id = "aging", Title = "Age of open items", Kind = ChartKind.Column, CategoryHeader = "Open for", Height = 230,
            Categories = buckets.Select(b => new ChartCategory(b.Label, b.Label)).ToList(),
            Series = [new ChartSeries { Key = "open", Name = "Open items", Tone = "o3",
                Values = buckets.Select(b => (double?)ages.Count(x => x >= b.Min && (b.Max == null || x <= b.Max))).ToArray(),
                PointTones = ["o1", "o2", "o3", "o4", "o5"],
                Hrefs = buckets.Select(b => R(f => { f.Status = "open"; f.AgeMin = b.Min; f.AgeMax = b.Max; f.Title = $"Open {b.Label.ToLowerInvariant()} · {app.Name}"; }).Href()).ToArray() }],
            EmptyText = $"Nothing is open in {app.Name}.",
        });

        // ---- Trends: overdue snapshot per week, throughput, c-chart of records created per month
        var weekEnds = weeks.Select(w => w.AddDays(7) > today ? DateTime.UtcNow : InsightsQueries.Utc(w.AddDays(7))).ToList();
        var startW = InsightsQueries.Utc(weeks[0]);
        var dueEntries = await (from e in db.PhaseEntries join r in rq on e.RecordId equals r.Id
                                where e.DueDate != null && (e.ExitedAt == null || e.ExitedAt >= startW)
                                select new { e.RecordId, e.EnteredAt, e.ExitedAt, e.DueDate, e.PhaseId }).ToListAsync();
        var normalIds = Cfg.PhasesById.Values.Where(p => p.Type == PhaseType.Normal).Select(p => p.Id).ToHashSet();
        var overdueW = weekEnds.Select(end => (double?)dueEntries
            .Where(e => normalIds.Contains(e.PhaseId) && e.EnteredAt <= end && (e.ExitedAt == null || e.ExitedAt > end) && e.DueDate < DateOnly.FromDateTime(end))
            .Select(e => e.RecordId).Distinct().Count()).ToArray();
        d.Charts.Add(new ChartData
        {
            Id = "overdue-trend", Title = "Overdue at the end of each week", Kind = ChartKind.Line, Ordered = true, CategoryHeader = "Week starting", NoShare = true, AverageRow = true,
            Categories = WeekCategories(weeks), Height = 230,
            Series = [new ChartSeries { Key = "overdue", Name = "Overdue", Tone = "bad", Values = overdueW,
                Hrefs = weeks.Select((w, i) => i == weeks.Count - 1 ? R(f => { f.Status = "overdue"; f.Title = $"Overdue · {app.Name}"; }).Href() : null).ToArray() }],
            EmptyText = "No due dates were tracked in the last 12 weeks.", Note = "Snapshot at the end of each week; the latest point is today.",
        });
        d.Charts.Add(new ChartData
        {
            Id = "throughput", Title = "Created vs completed per week", Kind = ChartKind.Column, Ordered = true, CategoryHeader = "Week starting", NoShare = true, Height = 230,
            Categories = WeekCategories(weeks),
            Series =
            [
                new ChartSeries { Key = "created", Name = "Created", Tone = "s1", Values = createdW.Select(v => (double?)v).ToArray(),
                    Hrefs = weeks.Select(w => R(f => { f.Status = "all"; f.On = "created"; f.From = w; f.To = w.AddDays(6); f.Title = $"Created week of {w:d MMM}"; }).Href()).ToArray() },
                new ChartSeries { Key = "completed", Name = "Completed", Tone = "s2", Values = completedW.Select(v => (double?)v).ToArray(),
                    Hrefs = weeks.Select(w => R(f => { f.Status = "all"; f.On = "completed"; f.From = w; f.To = w.AddDays(6); f.Title = $"Completed week of {w:d MMM}"; }).Href()).ToArray() },
            ],
            EmptyText = "Nothing was created or completed in the last 12 weeks.",
        });

        var m0 = new DateOnly(today.Year, today.Month, 1).AddMonths(-12);
        var months = Enumerable.Range(0, 12).Select(i => m0.AddMonths(i)).ToList();
        var createdM = await rq.Where(r => r.CreatedAt >= InsightsQueries.Utc(m0) && r.CreatedAt < InsightsQueries.Utc(m0.AddMonths(12))).Select(r => r.CreatedAt).ToListAsync();
        var monthly = months.Select(m => (double)createdM.Count(t => t >= InsightsQueries.Utc(m) && t < InsightsQueries.Utc(m.AddMonths(1)))).ToList();
        var (cl, ucl, lcl) = ControlLimits.CChart(monthly);
        d.Charts.Add(new ChartData
        {
            Id = "c-chart", Title = "Records created per month", Kind = ChartKind.Line, Ordered = true, CategoryHeader = "Month", NoShare = true, MarkOutOfControl = true,
            Categories = months.Select((m, i) => new ChartCategory(DateBuckets.Key(m), DateBuckets.Label(m, "month", i == 0 || m.Month == 1))).ToList(), Height = 230,
            Series = [new ChartSeries { Key = "created", Name = "Created", Tone = "s1", Values = monthly.Select(v => (double?)v).ToArray(),
                Hrefs = months.Select(m => R(f => { f.Status = "all"; f.On = "created"; f.From = m; f.To = m.AddMonths(1).AddDays(-1); f.Title = $"Created in {m:MMMM yyyy}"; }).Href()).ToArray() }],
            Limits = monthly.Sum() > 0 ? [new LimitLine(ucl, "UCL", "ucl"), new LimitLine(cl, "C̄", "cl"), new LimitLine(lcl, "LCL", "lcl")] : [],
            EmptyText = "Nothing was created in the last 12 complete months.",
            Note = "c-chart: centre line C̄ is the mean monthly count over the last 12 complete months; limits C̄ ± 3√C̄, LCL floored at 0. Points outside the limits are marked.",
        });

        // ---- Analysis (scoped by the period filter)
        d.Charts.Add(await CycleTimeAsync(app, from ?? new DateOnly(2000, 1, 1), today, "cycle-time", "Median days per phase", 560));

        var periodRecords = from is { } f0 ? rq.Where(r => r.CreatedAt >= InsightsQueries.Utc(f0) && !r.IsArchived) : rq.Where(r => !r.IsArchived);
        var kwFields = forms.SelectMany(f => f.Fields).Where(f => f.Type is FieldType.Keyword or FieldType.KeywordMulti && f.KeywordList != null)
            .Where(f => !f.Sensitive || access.CanSeeSensitive(app.Id)).DistinctBy(f => f.Key).ToList();
        if (kwFields.Count > 0)
        {
            var keys = kwFields.Select(f => f.Key).ToList();
            var ids = periodRecords.Select(r => r.Id);
            var vals = await db.RecordValues.Where(v => v.RowId == null && keys.Contains(v.FieldKey) && ids.Contains(v.RecordId))
                .Select(v => new { v.RecordId, v.FieldKey, v.RefId }).Distinct().ToListAsync();
            var periodCount = await periodRecords.CountAsync();
            foreach (var field in kwFields)
            {
                var counts = vals.Where(v => v.FieldKey == field.Key && v.RefId != null).GroupBy(v => v.RefId!.Value)
                    .Select(g => (Kw: Cfg.Keyword(g.Key), N: g.Select(x => x.RecordId).Distinct().Count())).Where(x => x.Kw != null).OrderByDescending(x => x.N).ToList();
                var withValue = vals.Where(v => v.FieldKey == field.Key).Select(v => v.RecordId).Distinct().Count();
                var shownK = counts.Take(counts.Count > 10 ? 9 : 10).ToList();
                var rest = counts.Skip(shownK.Count).Sum(x => x.N);
                var cats = shownK.Select(x => new ChartCategory(x.Kw!.Key, x.Kw.Label)).ToList();
                var values = shownK.Select(x => (double?)x.N).ToList();
                var hrefs = shownK.Select(x => (string?)R(f => { f.Status = "all"; f.Field = field.Key; f.Value = x.Kw!.Key; f.On = from != null ? "created" : null; f.From = from; f.To = today; f.Title = $"{field.Label}: {x.Kw.Label}"; }).Href()).ToList();
                if (rest > 0) { cats.Add(new ChartCategory("__other", "Other")); values.Add(rest); hrefs.Add(null); }
                var id = "pareto-" + field.Key;
                d.ParetoIds.Add(id);
                d.Charts.Add(new ChartData
                {
                    Id = id, Title = $"Pareto by {field.Label.ToLowerInvariant()}", Kind = ChartKind.Pareto, CategoryHeader = field.Label, Height = 240,
                    Categories = cats, Series = [new ChartSeries { Key = "n", Name = "Records", Tone = "s1", Values = values.ToArray(), Hrefs = hrefs.ToArray() }],
                    EmptyText = $"No {field.Label.ToLowerInvariant()} recorded, {periodName}.",
                    Note = periodCount > withValue ? $"{periodCount - withValue} of {periodCount} records have no {field.Label.ToLowerInvariant()}." : null,
                });
            }

            // Heat map: open records, phase × the most "severity-like" single keyword field.
            var heatField = kwFields.Where(f => f.Type == FieldType.Keyword && Cfg.Options(f.KeywordList).Count() is >= 2 and <= 8)
                .OrderBy(f => new[] { "priority", "severity", "risk", "critical", "class", "impact" }.Any(k => f.Key.Contains(k, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
                .FirstOrDefault();
            if (heatField != null && shown.Count > 0)
            {
                var openIds = openRows.Select(r => r.Id).ToHashSet();
                var openVals = await db.RecordValues.Where(v => v.RowId == null && v.FieldKey == heatField.Key && open.Select(r => r.Id).Contains(v.RecordId))
                    .Select(v => new { v.RecordId, v.RefId }).ToListAsync();
                var kwByRecord = openVals.GroupBy(v => v.RecordId).ToDictionary(g => g.Key, g => g.First().RefId);
                var options = Cfg.Options(heatField.KeywordList).ToList();
                d.Charts.Add(new ChartData
                {
                    Id = "heat", Title = $"Open items by phase and {heatField.Label.ToLowerInvariant()}", Kind = ChartKind.HeatMap, CategoryHeader = "Phase",
                    Categories = shown.Select(p => new ChartCategory(p.Key, p.Name)).ToList(),
                    Series = options.Select(k => new ChartSeries
                    {
                        Key = k.Key, Name = k.Label, Tone = "s1",
                        Values = shown.Select(p => (double?)phaseOf.Count(x => x.Key == p.Key && kwByRecord.GetValueOrDefault(x.r.Id) == k.Id)).ToArray(),
                        Hrefs = shown.Select(p => (string?)R(f => { f.Status = "open"; f.Phase = p.Key; f.Field = heatField.Key; f.Value = k.Key; f.Title = $"Open in {p.Name} · {heatField.Label}: {k.Label}"; }).Href()).ToArray(),
                    }).ToList(),
                    EmptyText = $"Nothing is open in {app.Name}.",
                });
            }
        }
        return d;
    }
}
