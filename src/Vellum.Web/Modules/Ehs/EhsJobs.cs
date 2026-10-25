using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;
using Vellum.Modules.Kit;

namespace Vellum.Modules.Ehs;

/// <summary>
/// EHS-114 / SAF-020: watches every open OSHA severe-injury clock. Sends the initial alert if it hasn't gone out yet, escalates
/// every hour until EHS acknowledges, warns when less than 2 hours are left and raises a missed-deadline alert.
/// State-based, so a run after downtime catches up instead of skipping (SAF-030).
/// </summary>
public sealed class OshaClockJob(VellumDbContext db, RecordService records, NotificationService notify, UserDirectory dir,
    ConfigCache cfg, AuditService audit) : IScheduledJob
{
    public string Key => "osha-clock-monitor";
    public string Name => "OSHA reporting clock monitor";
    public string Description => "Alerts and escalates fatality (8-hour) and serious-injury (24-hour) OSHA reporting clocks until they're acknowledged and reported.";
    public int DefaultIntervalMinutes => 5;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var ids = await OpenClockIdsAsync(ct);
        var sent = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var s = await records.LoadRequiredAsync(id);
            var n = await EvaluateAsync(s, DateTime.UtcNow);
            if (n > 0) ctx.Log($"{s.Record.Number}: {n} alert(s) sent.");
            sent += n;
            await db.SaveChangesAsync(ct);
        }
        return new JobResult(sent, $"{ids.Count} open OSHA clock(s) checked; {sent} alert(s) sent.");
    }

    public async Task<List<Guid>> OpenClockIdsAsync(CancellationToken ct = default)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("incident", out var form)) return [];
        return await db.Records.AsNoTracking()
            .Where(r => r.FormId == form.Id && r.PhaseType != PhaseType.Voided &&
                        db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "osha_deadline") &&
                        !db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "osha_reported_at"))
            .Select(r => r.Id).ToListAsync(ct);
    }

    /// <summary>Evaluate one clock at <paramref name="now"/>. Returns the number of alerts (one per recipient group message) sent.</summary>
    public async Task<int> EvaluateAsync(RecordSnapshot s, DateTime now)
    {
        var rule = OshaClock.RuleFor(s.KeywordKey("osha_severe_event"));
        if (rule == null || s.DateTime("osha_deadline") is not { } d || s.DateTime("osha_reported_at") != null || s.Record.PhaseType == PhaseType.Voided) return 0;
        var deadline = ModuleKit.Utc(d);
        var past = await db.Notifications.AsNoTracking()
            .Where(n => n.RecordId == s.Record.Id && n.Kind == NotificationKind.SafetyCritical)
            .Select(n => new { n.Title, n.CreatedAt }).ToListAsync();
        bool Sent(string prefix) => past.Any(p => p.Title.StartsWith(prefix, StringComparison.Ordinal));
        var recipients = IncidentAlerts.Recipients(dir, s);
        var summary = IncidentAlerts.Summary(s, rule, deadline);
        var left = deadline - now;
        var sent = 0;

        if (!Sent(IncidentAlerts.StartedPrefix))
        {
            notify.SafetyCritical(recipients, $"{IncidentAlerts.StartedPrefix}: {s.Record.Number} ({rule.Hours}-hour report)", summary, s.Record.Id);
            audit.Record(s.Record.Id, "Safety-critical alert sent", $"OSHA {rule.Hours}-hour clock started; alerted {string.Join(", ", recipients.Select(dir.Name))} (SAF-020).");
            sent++;
        }
        else if (s.UserId("osha_ack_by") == null)
        {
            var last = past.Where(p => p.Title.StartsWith(IncidentAlerts.StartedPrefix, StringComparison.Ordinal) ||
                                       p.Title.StartsWith(IncidentAlerts.EscalationPrefix, StringComparison.Ordinal))
                .Select(p => p.CreatedAt).DefaultIfEmpty(DateTime.MinValue).Max();
            if (now - last >= OshaClock.EscalationInterval - TimeSpan.FromMinutes(1))
            {
                var when = left > TimeSpan.Zero ? $"{ModuleKit.Span(left)} left" : $"deadline passed {ModuleKit.Span(left)} ago";
                notify.SafetyCritical(recipients, $"{IncidentAlerts.EscalationPrefix}: {s.Record.Number} ({when})",
                    summary + " Nobody in EHS has acknowledged the clock yet; this alert repeats every hour until someone does.", s.Record.Id);
                audit.Record(s.Record.Id, "OSHA clock escalated", $"Not acknowledged, {when}; alerted {recipients.Count} people (EHS-114).");
                sent++;
            }
        }

        if (left > TimeSpan.Zero && left <= OshaClock.DueSoonWindow && !Sent(IncidentAlerts.DueSoonPrefix))
        {
            notify.SafetyCritical(recipients, $"{IncidentAlerts.DueSoonPrefix}: {s.Record.Number}", summary, s.Record.Id);
            audit.Record(s.Record.Id, "OSHA deadline warning", $"{ModuleKit.Span(left)} left and no report recorded.");
            sent++;
        }
        if (left <= TimeSpan.Zero && !Sent(IncidentAlerts.MissedPrefix))
        {
            notify.SafetyCritical(recipients, $"{IncidentAlerts.MissedPrefix}: {s.Record.Number}",
                summary + " The deadline has passed without a recorded report. Report to OSHA now and record it on the incident.", s.Record.Id);
            audit.Record(s.Record.Id, "OSHA reporting deadline missed", $"Deadline {Sites.Local(deadline, TimeZoneInfo.Utc)} passed with no report recorded.");
            sent++;
        }
        return sent;
    }
}

/// <summary>EHS-406 / SAF-030: flags permits expiring soon, expires them at the end of their window and tells the crew to stop work.</summary>
public sealed class PermitExpiryJob(VellumDbContext db, RecordService records, WorkflowEngine workflow, NotificationService notify, ConfigCache cfg) : IScheduledJob
{
    public string Key => "permit-expiry";
    public string Name => "Work permit expiry";
    public string Description => "Warns permit holders before a work permit expires and expires it automatically at the end of its validity window.";
    public int DefaultIntervalMinutes => 15;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct) => new(await RunAsync(DateTime.UtcNow, ctx.Log, ct), null);

    public async Task<int> RunAsync(DateTime now, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("permit", out var form)) return 0;
        var phaseIds = form.Workflows.SelectMany(w => w.Phases).Where(p => p.Key == "active").Select(p => p.Id).ToList();
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseId != null && phaseIds.Contains(r.PhaseId.Value))
            .Select(r => r.Id).ToListAsync(ct);
        var changed = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var s = await records.LoadRequiredAsync(id);
            if (s.DateTime("valid_from") is not { } f || s.DateTime("valid_to") is not { } t) continue;
            var to = ModuleKit.Utc(t);
            var status = PermitValidity.Status(ModuleKit.Utc(f), to, now);
            var crew = new[] { s.UserId("receiver"), s.UserId("issuer") }.OfType<int>().Distinct().ToList();
            var tz = Sites.Tz(s.KeywordKey("site"));
            if (status == "expired")
            {
                var res = await workflow.SystemRouteAsync(id, "expired", $"Validity window ended at {Sites.Local(to, tz)}", skipValidation: true);
                if (!res.Ok) { log?.Invoke($"{s.Record.Number}: couldn't expire ({string.Join("; ", res.Errors.Select(e => e.Message))})"); continue; }
                var title = $"Work permit {s.Record.Number} has expired: stop work";
                var body = $"{s.Record.Title} at {s.Text("location")}. The permit expired at {Sites.Local(to, tz)}. Work must stop until a new permit is authorized.";
                foreach (var uid in crew) notify.Notify(uid, NotificationKind.Escalation, title, body, id);
                notify.ToGroup("ehs", NotificationKind.Escalation, title, body, id);
                log?.Invoke($"{s.Record.Number}: expired.");
                changed++;
            }
            else if (status != s.KeywordKey("validity_status"))
            {
                await records.UpdateAsync(id, new() { ["validity_status"] = status }, "Permit validity re-evaluated");
                if (status == "expiring")
                    foreach (var uid in crew)
                        notify.Notify(uid, NotificationKind.Reminder, $"Work permit {s.Record.Number} expires at {Sites.Local(to, tz)}",
                            $"{s.Record.Title}. Finish and close out the work, or request a new permit before it expires.", id);
                log?.Invoke($"{s.Record.Number}: {status}.");
                changed++;
            }
            await db.SaveChangesAsync(ct);
        }
        return changed;
    }
}

/// <summary>Keeps the "this year" flag of incidents current across the year boundary (view "Recordable incidents this year").</summary>
public sealed class EhsYearRolloverJob(VellumDbContext db, RecordService records, ConfigCache cfg) : IScheduledJob
{
    public string Key => "ehs-year-rollover";
    public string Name => "EHS calendar-year flags";
    public string Description => "Refreshes the current-year flag on incidents so year-to-date OSHA views stay correct after New Year.";
    public int DefaultIntervalMinutes => 720;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("incident", out var form)) return new JobResult(0);
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id).Select(r => r.Id).ToListAsync(ct);
        var values = await ModuleKit.MainValuesAsync(db, ids, "occurred_at", "site", "in_current_year");
        var siteKeys = cfg.Current.KeywordsById;
        var updated = 0;
        foreach (var (id, v) in values)
        {
            if (v.DateTime("occurred_at") is not { } at) continue;
            var site = v.Ref("site") is { } sid && siteKeys.TryGetValue(sid, out var k) ? k.Key : null;
            var current = Sites.LocalDate(at, site).Year == Sites.Today.Year;
            if (v.Bool("in_current_year") == current) continue;
            db.ChangeTracker.Clear();
            await records.UpdateAsync(id, new() { ["in_current_year"] = current }, "Calendar year rollover");
            updated++;
        }
        return new JobResult(updated, $"Updated {updated} incident(s).");
    }
}

/// <summary>Sidebar links for the near-miss quick report and the OSHA log.</summary>
public sealed class EhsNav(AccessService access, ConfigCache cfg) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        if (!cfg.Current.AppsByKey.TryGetValue("ehs", out var app) || !access.CanOpenApp(app.Id)) yield break;
        if (cfg.Current.FormsByKey.TryGetValue("incident", out var incident) && access.CanCreate(incident))
            yield return new NavItem("Safety", "Report a near miss", "/ehs/near-miss", "bi-megaphone", 10);
        yield return new NavItem("Safety", "OSHA log & rates", "/ehs/osha", "bi-clipboard-data", 20);
    }
}
