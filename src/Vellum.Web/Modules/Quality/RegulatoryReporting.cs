using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Quality;

/// <param name="Jurisdiction">FDA or EU.</param>
/// <param name="Rule">The rule that produced the deadline (SAF-024).</param>
/// <param name="Provisional">The decision is still pending; the clock runs anyway.</param>
public sealed record ReportingDeadline(string Jurisdiction, string Rule, string Basis, DateOnly Due, bool Provisional, DateOnly? SubmittedOn)
{
    public bool SubmittedLate => SubmittedOn is { } s && s > Due;
    public int DaysLeft(DateOnly today) => Due.DayNumber - today.DayNumber;
}

/// <summary>
/// CMP-009 / SAF-023 / SAF-024: regulatory reporting clocks, always calculated from the awareness date.
/// FDA MDR: 30 calendar days (21 CFR 803.50), or 5 working days on the business calendar when remedial action is needed (803.53).
/// EU MDR Article 87: 2 days (serious public health threat), 10 days (death or unanticipated serious deterioration), 15 days (other serious incidents).
/// Calendar-day deadlines ignore the business calendar.
/// </summary>
public static class ReportingClock
{
    /// <summary>Entering a complaint more than this many days after awareness is flagged in the audit trail (SAF-023).</summary>
    public const int LateEntryDays = 2;
    public const int DueSoonDays = 5;

    public static async Task<List<ReportingDeadline>> ComputeAsync(DateOnly? awareness, string? mdrDecision, bool euMarket, string? vigilanceDecision,
        DateOnly? fdaSubmitted, DateOnly? euSubmitted, BusinessCalendar calendar)
    {
        var list = new List<ReportingDeadline>();
        if (awareness is not { } a) return list;
        switch (mdrDecision)
        {
            case "reportable_5":
                list.Add(new("FDA", "FDA MDR 5-day report (21 CFR 803.53)",
                    "5 working days from awareness: remedial action is needed to prevent an unreasonable risk of substantial harm", await calendar.AddAsync(a, 5, true), false, fdaSubmitted));
                break;
            case "reportable_30":
                list.Add(new("FDA", "FDA MDR 30-day report (21 CFR 803.50)", "30 calendar days from awareness", a.AddDays(30), false, fdaSubmitted));
                break;
            case "not_reportable":
                break;
            default:
                list.Add(new("FDA", "FDA MDR 30-day report (provisional)",
                    "MDR decision pending: the 30-calendar-day clock runs from awareness until the decision tree concludes", a.AddDays(30), true, fdaSubmitted));
                break;
        }
        if (euMarket)
            switch (vigilanceDecision)
            {
                case "reportable_2":
                    list.Add(new("EU", "EU MDR serious public health threat (Art. 87(4))", "2 calendar days from awareness", a.AddDays(2), false, euSubmitted));
                    break;
                case "reportable_10":
                    list.Add(new("EU", "EU MDR death or serious deterioration in health (Art. 87(5))", "10 calendar days from awareness", a.AddDays(10), false, euSubmitted));
                    break;
                case "reportable_15":
                    list.Add(new("EU", "EU MDR serious incident (Art. 87(3))", "15 calendar days from awareness", a.AddDays(15), false, euSubmitted));
                    break;
                case "not_reportable" or "not_applicable":
                    break;
                default:
                    list.Add(new("EU", "EU MDR serious incident (provisional)",
                        "Vigilance decision pending: the 15-day clock runs from awareness until the decision tree concludes", a.AddDays(15), true, euSubmitted));
                    break;
            }
        return list;
    }

    /// <summary>none · running · due_soon (≤ 5 days) · missed · submitted.</summary>
    public static string Status(IReadOnlyCollection<ReportingDeadline> clock, DateOnly today)
    {
        if (clock.Count == 0) return "none";
        var open = clock.Where(c => c.SubmittedOn == null).ToList();
        if (open.Count == 0) return "submitted";
        var left = open.Min(c => c.Due).DayNumber - today.DayNumber;
        return left < 0 ? "missed" : left <= DueSoonDays ? "due_soon" : "running";
    }

    public static Task<List<ReportingDeadline>> ForAsync(RecordContext ctx) => ForAsync(ctx.Snapshot, ctx.Get<BusinessCalendar>());

    public static Task<List<ReportingDeadline>> ForAsync(RecordSnapshot s, BusinessCalendar calendar) =>
        ComputeAsync(s.Date("awareness_date"), s.KeywordKey("mdr_decision"), s.Bool("eu_market") == true, s.KeywordKey("vigilance_decision"),
            s.Date("fda_submitted_on"), s.Date("eu_submitted_on"), calendar);
}

public sealed record ReportingClockModel(List<ReportingDeadline> Deadlines, Keyword? MdrDecision, Keyword? VigilanceDecision, Keyword? Status,
    DateOnly? Awareness, int? LateEntryDays, List<(Guid Id, string Number, string Form, string? Phase, PhaseType Type)> Trees, DateOnly Today, bool Closed);

/// <summary>
/// CMP-009 / SAF-020: refreshes complaint clocks, raises safety-critical alerts to regulatory affairs at configurable points before each
/// deadline (Vellum:Quality:ReportingAlertDays, default 5,2,1,0) and when a deadline is missed. Each alert is sent once per deadline and point.
/// Looks at current state, so a run after downtime catches up (SAF-030).
/// </summary>
public sealed class RegulatoryClockJob(VellumDbContext db, ConfigCache cfg, RecordService records, NotificationService notify, UserDirectory dir,
    AuditService audit, BusinessCalendar calendar, IConfiguration config) : IScheduledJob
{
    public string Key => "quality-reporting-clock";
    public string Name => "Regulatory reporting clocks";
    public string Description => "Refreshes complaint reporting deadlines (FDA MDR, EU vigilance) and raises safety-critical alerts to regulatory affairs as deadlines approach and when they're missed (CMP-009).";
    public int DefaultIntervalMinutes => 60;

    public IReadOnlyList<int> AlertPoints =>
        (config["Vellum:Quality:ReportingAlertDays"] ?? "5,2,1,0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => int.TryParse(p, out var n) ? n : -1).Where(n => n >= 0).Distinct().Order().ToList();

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("complaint", out var form)) return new JobResult(0, "Complaints aren't installed.");
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseType == PhaseType.Normal && !r.IsArchived).Select(r => r.Id).ToListAsync(ct);
        var today = BusinessCalendar.Today;
        var points = AlertPoints;
        var alerts = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var refreshed = await records.UpdateAsync(id, new(), "Reporting clock refresh");
            if (!refreshed.Ok) { ctx.Log($"{id}: couldn't refresh: {string.Join("; ", refreshed.Errors.Select(e => e.Message))}"); continue; }
            var s = await records.LoadRequiredAsync(id);
            var recipients = dir.ActiveMembers("regulatory").Select(u => u.Id).Append(s.UserId("owner") ?? s.Record.CreatedById).Distinct().ToList();
            foreach (var c in (await ReportingClock.ForAsync(s, calendar)).Where(c => c.SubmittedOn == null))
            {
                var left = c.DaysLeft(today);
                string? title = null;
                string body;
                if (left < 0)
                {
                    title = $"MISSED reporting deadline: {s.Record.Number} {c.Jurisdiction} report was due {c.Due:dd MMM yyyy}";
                    body = $"{c.Rule} for \"{s.Record.Title}\" was due {c.Due:dd MMM yyyy} ({-left} day{(left == -1 ? "" : "s")} ago) and no submission is recorded. Rule: {c.Basis}.";
                }
                else
                {
                    var point = points.Where(p => left <= p).DefaultIfEmpty(-1).Min();
                    if (point < 0) continue;
                    title = $"{s.Record.Number}: {c.Jurisdiction} report due {(left == 0 ? "today" : $"in {left} day{(left == 1 ? "" : "s")}")} ({c.Due:dd MMM yyyy}, {point}-day warning)";
                    body = $"{c.Rule}{(c.Provisional ? " — decision still pending" : "")} for \"{s.Record.Title}\". Awareness date {s.Date("awareness_date"):dd MMM yyyy}. Rule: {c.Basis}.";
                }
                if (await db.Notifications.AnyAsync(n => n.RecordId == id && n.Title == title, ct)) continue;
                notify.SafetyCritical(recipients, title, body, id);
                if (left < 0) audit.Record(id, "Regulatory deadline missed", $"{c.Rule} due {c.Due:dd MMM yyyy}: no submission recorded. Regulatory affairs alerted (CMP-009).");
                ctx.Log(title);
                alerts++;
            }
            await db.SaveChangesAsync(ct);
        }
        return new JobResult(alerts, $"Checked {ids.Count} open complaint(s); raised {alerts} safety-critical alert(s).");
    }
}

public sealed record ClockWidgetItem(Guid Id, string Number, string Title, DateOnly Due, string? Rule, string? StatusKey, string? StatusLabel, string? StatusColor);

/// <summary>Home page card for regulatory affairs and quality approvers: complaints whose reporting deadline is near or missed.</summary>
public sealed class ReportingClockWidget(VellumDbContext db, CurrentUser me, UserDirectory dir, AccessService access, ConfigCache cfg) : IHomeWidget
{
    public string Key => "quality-reporting-clocks";
    public int Order => 20;
    public string Column => "side";

    public async Task<HomeWidgetModel?> BuildAsync()
    {
        if (!me.IsAuthenticated || me.IsSystem) return null;
        if (!dir.IsMember(me.Id, "regulatory") && !dir.IsMember(me.Id, "qa-approvers")) return null;
        if (!cfg.Current.FormsByKey.TryGetValue("complaint", out var form)) return null;
        var horizon = BusinessCalendar.Today.AddDays(14).ToDateTime(TimeOnly.MinValue);
        var running = new[] { "running", "due_soon", "missed" }.Select(k => cfg.Current.Keyword("reporting_clock_status", k)?.Id).OfType<int>().ToList();
        var rows = await access.Readable().AsNoTracking()
            .Where(r => r.FormId == form.Id && r.PhaseType == PhaseType.Normal &&
                        db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "clock_status" && v.RefId != null && running.Contains(v.RefId.Value)) &&
                        db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "report_due" && v.Date <= horizon))
            .Take(50).ToListAsync();
        if (rows.Count == 0) return null;
        var ids = rows.Select(r => r.Id).ToList();
        var vals = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null &&
            (v.FieldKey == "report_due" || v.FieldKey == "report_rule" || v.FieldKey == "clock_status")).ToListAsync();
        var items = rows.Select(r =>
        {
            var kw = cfg.Current.Keyword(vals.FirstOrDefault(v => v.RecordId == r.Id && v.FieldKey == "clock_status")?.RefId);
            var due = vals.First(v => v.RecordId == r.Id && v.FieldKey == "report_due").Date!.Value;
            return new ClockWidgetItem(r.Id, r.Number, r.Title, DateOnly.FromDateTime(due), vals.FirstOrDefault(v => v.RecordId == r.Id && v.FieldKey == "report_rule")?.Text,
                kw?.Key, kw?.Label, kw?.Color);
        }).OrderBy(i => i.Due).Take(6).ToList();
        return new HomeWidgetModel("~/Modules/Quality/Views/_ClockWidget.cshtml", items);
    }
}

/// <summary>Sidebar link to the complaint trending page (CMP-010).</summary>
public sealed class QualityNav(AccessService access, ConfigCache cfg) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        if (cfg.Current.AppsByKey.TryGetValue("complaints", out var app) && access.CanOpenApp(app.Id))
            yield return new NavItem("Insights", "Complaint trends", "/quality/complaint-trends", "bi-graph-up-arrow", 40);
    }
}
