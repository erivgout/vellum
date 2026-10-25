using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Risk;

/// <summary>
/// Periodic risk review: once a day re-evaluates each risk assessment's review status (current, due within 30 days,
/// overdue) through the record service, and notifies the risk owner when a review becomes due or overdue.
/// </summary>
public sealed class RiskReviewJob(VellumDbContext db, RecordService records, ConfigCache cfg, NotificationService notify) : IScheduledJob
{
    public string Key => "risk-review";
    public string Name => "Risk periodic review reminders";
    public string Description => "Flags risk assessments due for periodic review within 30 days or overdue, and reminds their owners.";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("risk_assessment", out var form)) return new JobResult(0, "Risk Register isn't installed.");
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseType != PhaseType.Voided && !r.IsArchived)
            .Select(r => r.Id).ToListAsync(ct);
        var changed = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(id);
            var before = snap.KeywordKey("review_state");
            var res = await records.SaveAsync(snap, new RecordData(), new SaveOptions { System = true, Reason = "Daily periodic review check" });
            if (!res.Ok) { ctx.Log($"{snap.Record.Number}: {string.Join("; ", res.Errors.Select(e => e.Message))}"); continue; }
            if (!res.Changed) continue;
            changed++;
            db.ChangeTracker.Clear();
            snap = await records.LoadRequiredAsync(id);
            var after = snap.KeywordKey("review_state");
            if (after is "due_soon" or "overdue" && after != before && snap.Record.PhaseType == PhaseType.Completed && snap.UserId("owner") is { } owner)
            {
                notify.Notify(owner, after == "overdue" ? NotificationKind.Escalation : NotificationKind.Reminder,
                    $"Risk review {(after == "overdue" ? "overdue" : "due")}: {snap.Record.Number}",
                    $"{snap.Record.Title} ({snap.Keyword("current_level")?.Label}) is due for periodic review on {snap.Date("review_date"):dd MMM yyyy}. Use Reassess risk to start the review.", id);
                ctx.Log($"{snap.Record.Number}: review {after}");
            }
        }
        await db.SaveChangesAsync(ct);
        return new JobResult(changed, $"Checked {ids.Count} risk assessment(s); {changed} review status change(s).");
    }
}

/// <summary>Adds the risk heat map to the Insights section for people who can open the Risk Register.</summary>
public sealed class RiskNav(AccessService access, ConfigCache cfg) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        if (cfg.Current.AppsByKey.TryGetValue("risk", out var app) && access.CanOpenApp(app.Id))
            yield return new NavItem("Insights", "Risk heat map", "/risk/heatmap", "bi-grid-3x3-gap-fill", 40);
    }
}
