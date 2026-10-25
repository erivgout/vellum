using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Documents;

/// <summary>
/// DOC-050: releases revisions in Awaiting Release whose effective date has arrived, once Document Control has signed the release
/// and any training gate is met (TRN-071). Runs as the system identity; the release signature was captured from a person beforehand.
/// </summary>
public sealed class DocumentReleaseJob(VellumDbContext db, RecordService records, WorkflowEngine workflow, DocumentControlService svc,
    Pdf.RenditionService renditions) : IScheduledJob
{
    public string Key => "documents-scheduled-release";
    public string Name => "Release documents on their effective date";
    public string Description => "Makes scheduled revisions effective on their effective date (DOC-050), superseding the previous revision in the same transaction (INV-100).";
    public int DefaultIntervalMinutes => 15;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var phaseId = svc.DocPhase(DocKeys.AwaitingRelease).Id;
        var ids = await db.Records.AsNoTracking().Where(r => r.PhaseId == phaseId && r.PhaseType == PhaseType.Normal).Select(r => r.Id).ToListAsync(ct);
        var released = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(id);
            var label = $"{snap.Record.Number} rev {snap.Record.Revision}";
            var planned = await svc.PlannedEffectiveAsync(snap);
            if (planned > BusinessCalendar.Today) continue;
            var sig = await svc.PhaseSignatureAsync(id, DocKeys.SignReleased);
            if (sig == null) { ctx.Log($"{label}: effective date reached; waiting for Document Control to release it."); continue; }
            var gate = await svc.GateAsync(snap);
            if (!gate.Satisfied) { ctx.Log($"{label}: {gate.Message}"); continue; }
            // INV-054: never effective without its PDF rendition; wait for the conversion instead of failing the run.
            var missing = await renditions.MissingRequiredAsync(snap.Record, snap.Form, svc.DocPhase(DocKeys.Approved), producedOnEntry: true);
            if (missing.Count > 0) { ctx.Log($"{label}: {string.Join(" ", missing)}"); continue; }
            try
            {
                var res = await workflow.SystemRouteAsync(id, DocKeys.Approved,
                    $"Effective date {planned:dd MMM yyyy} reached; release signed by {sig.SignerName} on {sig.SignedAt:dd MMM yyyy} (DOC-050)");
                if (res.Ok) { released++; ctx.Log($"{label}: released."); }
                else ctx.Log($"{label}: {string.Join("; ", res.Errors.Select(e => e.Message))}");
            }
            catch (WorkflowException ex) { ctx.Log($"{label}: not released: {ex.Message}"); }
        }
        return new JobResult(released, $"Released {released} document revision(s); {ids.Count - released} still awaiting release.");
    }
}

/// <summary>DOC-072: reminds owners of upcoming and overdue periodic reviews and escalates overdue reviews to their manager.</summary>
public sealed class DocumentReviewReminderJob(VellumDbContext db, DocumentControlService svc, NotificationService notify, UserDirectory dir) : IScheduledJob
{
    public string Key => "documents-review-reminders";
    public string Name => "Periodic review reminders";
    public string Description => "Reminds document owners 30 days before, on and after the periodic review date, and escalates reviews more than 14 days overdue (DOC-072).";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var formId = svc.DocumentForm.Id;
        var docs = await db.Records.AsNoTracking().Where(r => r.FormId == formId && r.IsEffective).ToListAsync(ct);
        var weekAgo = DateTime.UtcNow.AddDays(-6);
        var sent = 0;
        foreach (var info in await svc.ReviewScheduleAsync(docs))
        {
            if (info.NextReview is not { } next || info.OwnerId is not { } owner || info.OpenReview != null || info.DaysLeft > 30) continue;
            var d = info.Doc;
            var already = await db.Notifications.AnyAsync(n => n.RecordId == d.Id && n.UserId == owner && n.CreatedAt > weekAgo && n.Title.StartsWith("Periodic review"), ct);
            if (already) continue;
            var days = info.DaysLeft!.Value;
            var when = days < 0 ? $"is {-days} day{(days == -1 ? "" : "s")} overdue" : days == 0 ? "is due today" : $"is due in {days} days";
            notify.Notify(owner, NotificationKind.Reminder, $"Periodic review of {d.Number} {when}",
                $"{d.Title} (rev {d.Revision}) needs its periodic review by {next:dd MMM yyyy}. Open the document and choose Revision control › Start periodic review.", d.Id);
            sent++;
            if (days < -14 && dir.Get(owner)?.ReportsToId is { } mgr && dir.Get(mgr) is { IsActive: true })
            {
                notify.Notify(mgr, NotificationKind.Escalation, $"Escalation: periodic review of {d.Number} is {-days} days overdue",
                    $"{dir.Name(owner)} owns {d.Title} (rev {d.Revision}); its review was due {next:dd MMM yyyy}.", d.Id);
                sent++;
            }
        }
        await db.SaveChangesAsync(ct);
        return new JobResult(sent, $"Sent {sent} periodic review reminder(s).");
    }
}
