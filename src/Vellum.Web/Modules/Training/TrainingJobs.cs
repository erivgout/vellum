using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure.Jobs;
using Vellum.Modules.Documents;

namespace Vellum.Modules.Training;

/// <summary>TRN-090 / TRN-024: completed assignments past their expiration become Expired (no data changes: INV-030).</summary>
public sealed class TrainingExpiryJob(VellumDbContext db, TrainingService t, WorkflowEngine workflow, NotificationService notify) : IScheduledJob
{
    public string Key => "training-expire";
    public string Name => "Expire completed training";
    public string Description => "Moves completed training assignments to Expired once their course's expiration period has passed (TRN-024).";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var completed = t.Phase(TrainingKeys.Completed).Id;
        var rows = await t.RowsAsync(db.Records.AsNoTracking().Where(r => r.FormId == t.AssignmentFormId && r.PhaseId == completed));
        var n = 0;
        foreach (var r in rows.Where(r => r.ExpiresOn is { } e && e < BusinessCalendar.Today))
        {
            var res = await workflow.SystemRouteAsync(r.Id, TrainingKeys.Expired, $"Training expired on {r.ExpiresOn:dd MMM yyyy} (TRN-024)");
            if (!res.Ok) { ctx.Log($"{r.Number}: {string.Join("; ", res.Errors.Select(e => e.Message))}"); continue; }
            if (r.EmployeeId is { } emp)
                notify.Notify(emp, NotificationKind.Info, $"Your training on {r.CourseName} has expired", $"It expired on {r.ExpiresOn:dd MMM yyyy}. A retraining assignment follows if the course requires one.", r.Id);
            n++;
        }
        await db.SaveChangesAsync(ct);
        return new JobResult(n, $"Expired {n} training record(s).");
    }
}

/// <summary>TRN-090 / TRN-025: creates retraining assignments ahead of expiry so qualification has no gap.</summary>
public sealed class TrainingRetrainingJob(VellumDbContext db, TrainingService t) : IScheduledJob
{
    public string Key => "training-retraining";
    public string Name => "Create retraining assignments";
    public string Description => "Creates the next assignment for completed training that expires within the course's retraining lead time (TRN-025).";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var all = await t.RowsAsync(db.Records.AsNoTracking().Where(r => r.FormId == t.AssignmentFormId));
        var n = 0;
        foreach (var r in all.Where(r => r.ExpiresOn != null && r.Phase?.Key is TrainingKeys.Completed or TrainingKeys.Expired && r.CourseId != null && r.EmployeeId != null))
        {
            if (all.Any(o => o.Id != r.Id && o.CourseId == r.CourseId && o.EmployeeId == r.EmployeeId && o.CreatedAt > r.CreatedAt && o.PhaseType != PhaseType.Voided)) continue;
            var course = await t.CourseAsync(r.CourseId!.Value);
            if (course is not { Enabled: true }) continue;
            var start = r.ExpiresOn!.Value.AddDays(-(course.RetrainingDays ?? 0));
            if (BusinessCalendar.Today < start) continue;
            var due = r.ExpiresOn.Value > BusinessCalendar.Today ? r.ExpiresOn.Value : BusinessCalendar.Today.AddDays(course.DueDays);
            var (created, skipped) = await Tx.RunAsync(db, () => t.AssignAsync(course.Id, [r.EmployeeId!.Value], due,
                $"Retraining: {r.Number} expires {r.ExpiresOn:dd MMM yyyy} (TRN-025)", forceRetraining: true));
            n += created.Count;
            foreach (var s in skipped) ctx.Log($"{r.Number}: {s}");
        }
        return new JobResult(n, $"Created {n} retraining assignment(s).");
    }
}

/// <summary>TRN-090 / TRN-043: voids pending assignments that are no longer valid (course disabled, employee inactive).</summary>
public sealed class TrainingVoidInvalidJob(VellumDbContext db, TrainingService t, WorkflowEngine workflow, UserDirectory dir) : IScheduledJob
{
    public string Key => "training-void-invalid";
    public string Name => "Void non-applicable training assignments";
    public string Description => "Voids pending assignments of disabled courses and of employees who are no longer active (TRN-027, TRN-043).";
    public int DefaultIntervalMinutes => 15;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var pending = await t.RowsAsync(db.Records.AsNoTracking().Where(r => r.FormId == t.AssignmentFormId && r.PhaseType == PhaseType.Normal));
        var courses = new Dictionary<Guid, CourseInfo?>();
        var n = 0;
        foreach (var r in pending)
        {
            string? why = null;
            if (r.EmployeeId is not { } emp || dir.Get(emp) is not { IsActive: true }) why = $"{dir.Name(r.EmployeeId)} is no longer an active employee";
            else if (r.CourseId is { } cid)
            {
                if (!courses.TryGetValue(cid, out var c)) courses[cid] = c = await t.CourseAsync(cid);
                if (c is not { Enabled: true }) why = $"Course {c?.Number} is disabled";
            }
            if (why == null) continue;
            var res = await workflow.SystemRouteAsync(r.Id, TrainingKeys.Voided, why + " (TRN-043)");
            if (res.Ok) n++; else ctx.Log($"{r.Number}: {string.Join("; ", res.Errors.Select(e => e.Message))}");
        }
        return new JobResult(n, $"Voided {n} invalid assignment(s).");
    }
}

/// <summary>
/// TRN-090 / DOC-065 / DOC-066: disables courses whose source document was voided or made obsolete, and retires courses of
/// superseded revisions once nobody is still pending.
/// </summary>
public sealed class TrainingCourseRetireJob(VellumDbContext db, TrainingService t, RecordService records) : IScheduledJob
{
    public string Key => "training-course-retire";
    public string Name => "Disable courses of archived documents";
    public string Description => "Disables courses when their source document is voided or obsolete, and retires courses of superseded revisions once pending trainees finish (DOC-065/066).";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == t.CourseFormId).Select(r => r.Id).ToListAsync(ct);
        var n = 0;
        foreach (var id in ids)
        {
            var c = await t.CourseAsync(id);
            if (c is not { Enabled: true } || c.SourceDocumentId is not { } docId) continue;
            var doc = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == docId, ct);
            if (doc == null) continue;
            string? why = null;
            if (doc.PhaseType == PhaseType.Voided) why = $"Source document {doc.Number} rev {doc.Revision} was voided";
            else if (doc.ArchiveReason == DocKeys.ObsoleteReason) why = $"Source document {doc.Number} is obsolete";
            else if (doc.ArchiveReason == DocKeys.Superseded && !await t.AssignmentsOf(id).AnyAsync(r => r.PhaseType == PhaseType.Normal, ct))
                why = $"Retired: {doc.Number} rev {doc.Revision} was superseded and no trainees are pending";
            if (why == null) continue;
            var res = await t.ElevatedAsync(() => records.UpdateAsync(id, new() { ["enabled"] = false }, why + " (DOC-066)"));
            if (!res.Ok) { ctx.Log($"{c.Number}: {string.Join("; ", res.Errors.Select(e => e.Message))}"); continue; }
            await t.VoidPendingAsync(id, why);
            ctx.Log($"{c.Number}: {why}.");
            n++;
        }
        return new JobResult(n, $"Disabled {n} course(s).");
    }
}

/// <summary>TRN-090: daily digest of overdue training to the training coordinators (assignees get the core overdue reminders and escalation).</summary>
public sealed class TrainingOverdueDigestJob(VellumDbContext db, TrainingService t, NotificationService notify, UserDirectory dir) : IScheduledJob
{
    public string Key => "training-overdue-digest";
    public string Name => "Overdue training digest";
    public string Description => "Sends training coordinators a daily summary of overdue training. Trainees and their managers get the standard overdue reminders and escalation.";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var today = BusinessCalendar.Today;
        var overdue = (await t.RowsAsync(db.Records.AsNoTracking().Where(r => r.FormId == t.AssignmentFormId && r.PhaseType == PhaseType.Normal && r.DueDate < today))).ToList();
        if (overdue.Count == 0) return new JobResult(0, "No overdue training.");
        var byCourse = overdue.GroupBy(r => r.CourseName).OrderByDescending(g => g.Count()).Take(8)
            .Select(g => $"{g.Key}: {g.Count()} ({string.Join(", ", g.Select(r => dir.Name(r.EmployeeId)).Take(4))}{(g.Count() > 4 ? "…" : "")})");
        var dayStart = DateTime.UtcNow.Date;
        foreach (var u in dir.ActiveMembers(DocKeys.TrainingCoordinators))
        {
            if (await db.Notifications.AnyAsync(n => n.UserId == u.Id && n.CreatedAt >= dayStart && n.Title.StartsWith("Overdue training:"), ct)) continue;
            notify.Notify(u.Id, NotificationKind.Reminder, $"Overdue training: {overdue.Count} assignment{(overdue.Count == 1 ? "" : "s")}",
                string.Join(" · ", byCourse), url: "/apps/training?view=overdue");
        }
        await db.SaveChangesAsync(ct);
        return new JobResult(overdue.Count, $"{overdue.Count} overdue assignment(s) reported.");
    }
}
