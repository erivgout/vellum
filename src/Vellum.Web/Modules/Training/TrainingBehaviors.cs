using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Modules.Documents;

namespace Vellum.Modules.Training;

public sealed record CourseAssignmentsModel(CourseInfo Course, List<AssignmentRow> Rows, CourseStats Stats, bool Detail, int Applicable, int NotAssigned, UserDirectory Dir);

public sealed record AssignmentBriefModel(Record Assignment, CourseInfo? Course, List<Attachment> Materials, Record? SourceDoc, bool SourceReadable,
    int? EmployeeId, DateOnly? Due, string PhaseKey, DateTime? CompletedAt, DateOnly? ExpiresOn, bool IsMine, List<string> MissingPrerequisites, UserDirectory Dir);

/// <summary>TRN-020..028: course profiles. Maintained by training coordinators and course owners; applicability rows are never empty (INV-106).</summary>
public sealed class CourseBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => [TrainingKeys.Course];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var t = ctx.Services.GetRequiredService<TrainingService>();
        var me = ctx.Services.GetRequiredService<CurrentUser>();
        if (!me.IsSystem && !t.SystemWrite && !t.IsCoordinator && (ctx.IsNew || ctx.Current!.UserId("owner") != me.Id))
            ctx.Error(null, "Only training coordinators and the course owner can change course profiles (TRN-003).");

        var rows = ctx.Proposed.RowsOf("applicability");
        for (var i = 0; i < rows.Count; i++)
        {
            var v = rows[i].Values;
            if (!v.Has("departments") && !v.Has("sites") && !v.Has("groups") && string.IsNullOrWhiteSpace(v.Text("job_title")))
                ctx.Error("departments", $"Applicability rule {i + 1} is empty. Fill in at least one field or remove the rule (INV-106).", rows[i].Id);
        }
        if (ctx.Current != null && ctx.Proposed.Main.Links("prerequisites").Contains(ctx.Current.Record.Id))
            ctx.Error("prerequisites", "A course can't be its own prerequisite.");

        var src = ctx.Proposed.Main.Link("source_document");
        var doc = src == null ? null : await ctx.Services.GetRequiredService<VellumDbContext>().Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == src);
        ctx.Set("source_ref", doc == null ? null : DocumentControlService.DocRef(doc));
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var t = ctx.Get<TrainingService>();
        var c = TrainingService.ToCourse(ctx.Snapshot);
        if (!t.CanManageCourse(c)) return [];
        return
        [
            new RecordAction
            {
                Key = "assign_employees", Label = "Assign employees", Icon = "bi-person-plus", Group = "Assignments",
                Description = "Create assignments for the people this course applies to and anyone you add (TRN-041). Each person is notified.",
                DisabledReason = c.Enabled ? null : "The course is disabled (TRN-027).",
                Inputs =
                [
                    new ActionField("applicable", "Include everyone the applicability rules select", "yesno", Default: "yes"),
                    new ActionField("force", "Force retraining for people who already completed it", "yesno", Default: "no"),
                    new ActionField("people", "Also assign", "users"),
                    new ActionField("due", "Due date", "date", Help: $"Leave blank to use the course's due-date delay ({c.DueDays} days)."),
                ],
            },
        ];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "assign_employees") return ActionOutcome.Fail("This action isn't available.");
        var t = ctx.Get<TrainingService>();
        var c = TrainingService.ToCourse(ctx.Snapshot);
        if (!t.CanManageCourse(c)) return ActionOutcome.Fail("Only training coordinators and the course owner can assign this course (TRN-003).");
        var due = ActionInputs.Date(input.Get("due"));
        if (due < BusinessCalendar.Today) return ActionOutcome.Fail("The due date can't be in the past.");
        var people = ActionInputs.Users(input.GetAll("people"));
        if (input.Get("applicable") != "no") people.AddRange((await t.ApplicableAsync(c)).Select(u => u.Id));
        if (people.Count == 0) return ActionOutcome.Fail("Nobody to assign: add people or applicability rules.");
        try
        {
            var (created, skipped) = await Tx.RunAsync(ctx.Db, () => t.AssignAsync(c.Id, people, due, $"Assigned from {c.Number} by {ctx.Me.Name}", input.Get("force") == "yes"));
            var msg = $"{created.Count} assignment{(created.Count == 1 ? "" : "s")} created.";
            if (skipped.Count > 0) msg += $" {skipped.Count} skipped: {string.Join("; ", skipped.Take(3))}{(skipped.Count > 3 ? "…" : "")}.";
            return ActionOutcome.Ok(msg);
        }
        catch (WorkflowException ex) { return ActionOutcome.Fail(ex.Message); }
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var t = ctx.Get<TrainingService>();
        var c = TrainingService.ToCourse(ctx.Snapshot);
        var rows = await t.RowsAsync(t.AssignmentsOf(c.Id));
        var stats = await t.StatsAsync([c.Id]);
        var applicable = await t.ApplicableAsync(c);
        var notAssigned = applicable.Count(u => rows.All(r => r.EmployeeId != u.Id || r.PhaseType == PhaseType.Voided));
        var model = new CourseAssignmentsModel(c, rows.OrderBy(r => r.PhaseType != PhaseType.Normal).ThenBy(r => r.Due).ToList(), stats, t.CanManageCourse(c),
            applicable.Count, notAssigned, ctx.Directory);
        return
        [
            new RecordPanel { Key = "course-assignments", Title = "Assignments", Icon = "bi-people", Count = stats.Active, Partial = "~/Modules/Training/Views/_CourseAssignments.cshtml", Model = model },
            new RecordPanel { Key = "course-summary", Title = "Training summary", Icon = "bi-pie-chart", Placement = "side", Partial = "~/Modules/Training/Views/_CourseSummary.cshtml", Model = model },
        ];
    }
}

/// <summary>
/// Training assignments: one pending assignment per employee and course (INV-109), completion only by the trainee's e-signed
/// acknowledgment or a coordinator's signed on-behalf record (TRN-040, TRN-015), prerequisites (INV-107) and immutable
/// completed records (INV-030).
/// </summary>
public sealed class TrainingAssignmentBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => [TrainingKeys.Assignment];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        if (!ctx.IsNew && ctx.Current!.Record.PhaseType != PhaseType.Normal)
        {
            ctx.Error(null, "Completed, expired and voided training records can't be changed (INV-030). A coordinator can use Void and reissue to correct one.");
            return;
        }
        var t = ctx.Services.GetRequiredService<TrainingService>();
        var me = ctx.Services.GetRequiredService<CurrentUser>();
        var dir = ctx.Services.GetRequiredService<UserDirectory>();
        var employee = ctx.Proposed.Main.Ref("employee");
        var courseId = ctx.Proposed.Main.Link("course");
        var course = courseId is { } cid ? await t.CourseAsync(cid) : null;

        if (ctx.IsNew && !me.IsSystem && !t.SystemWrite && !t.IsCoordinator && (course == null || course.OwnerId != me.Id))
            ctx.Error(null, "Only training coordinators and the course owner can create training assignments (TRN-003).");
        if (employee is { } e && dir.Get(e) is not { IsActive: true })
            ctx.Error("employee", "Choose an active employee.");
        if (courseId != null && course == null) ctx.Error("course", "That course doesn't exist.");
        if (course == null) return;

        if (ctx.IsNew && !course.Enabled) ctx.Error("course", $"{course.Number} is disabled and can't get new assignments (TRN-027).");
        ctx.Set("course_name", course.Name);
        if (!ctx.Proposed.Main.Has("source_doc") && course.SourceDocumentId != null) ctx.Set("source_doc", course.SourceDocumentId);

        // INV-109: at most one pending assignment per employee and course.
        var changed = ctx.IsNew || ctx.Current!.UserId("employee") != employee || ctx.Current.Data.Main.Link("course") != courseId;
        if (changed && employee is { } emp && await t.HasPendingAsync(emp, course.Id, ctx.Current?.Record.Id))
            ctx.Error("employee", $"{dir.Name(emp)} already has a pending assignment of {course.Name}. An employee can have only one pending assignment per course (INV-109).");
    }

    /// <summary>TRN-002: an assignment is visible to the employee, their manager, the course owner, coordinators and its assignees.</summary>
    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        var snap = ctx.Snapshot;
        var ids = new HashSet<int>();
        if (snap.UserId("employee") is { } e)
        {
            ids.Add(e);
            if (ctx.Directory.Get(e)?.ReportsToId is { } mgr) ids.Add(mgr);
        }
        if (snap.Data.Main.Link("course") is { } cid && await ctx.Get<TrainingService>().CourseAsync(cid) is { OwnerId: { } owner }) ids.Add(owner);
        foreach (var id in ids)
            ctx.Db.RecordAccess.Add(new RecordAccess { RecordId = snap.Record.Id, PrincipalKind = PrincipalKind.User, PrincipalId = id, Level = RecordAccessLevel.Reader });
        if (ids.Count > 0)
            ctx.Get<AuditService>().Record(snap.Record.Id, "Visibility changed", $"Readable by {string.Join(", ", ids.Select(ctx.Directory.Name))}, training coordinators and assignees (TRN-002)");
        await ctx.Db.SaveChangesAsync();
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != TrainingKeys.Completed || ctx.IsSystem) return;
        var snap = ctx.Snapshot;
        if (snap.UserId("employee") != ctx.Me.Id)
            ctx.Error("Only the trainee can acknowledge their own training; delegates can't. A training coordinator can record completion on their behalf (TRN-015).");
        var t = ctx.Get<TrainingService>();
        var course = snap.Data.Main.Link("course") is { } cid ? await t.CourseAsync(cid) : null;
        if (course == null) { ctx.Error("The course no longer exists."); return; }
        if (!course.Enabled) ctx.Error("This course was disabled, so the assignment is no longer valid and will be voided (TRN-043).");
        if (course.InstructorLed) ctx.Error("Instructor-led training is completed by the training coordinator after the session, not by acknowledgment (TRN-040).");
        var missing = await t.MissingPrerequisitesAsync(course, snap.UserId("employee") ?? 0);
        if (missing.Count > 0) ctx.Error($"Complete the prerequisite course{(missing.Count == 1 ? "" : "s")} first: {string.Join(", ", missing)} (INV-107).");
    }

    public override Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var t = ctx.Get<TrainingService>();
        if (!t.IsCoordinator) return Task.FromResult(Enumerable.Empty<RecordAction>());
        var list = new List<RecordAction>();
        var phase = ctx.Snapshot.PhaseKey;
        if (ctx.Record.PhaseType == PhaseType.Normal)
        {
            list.Add(new RecordAction
            {
                Key = "complete_on_behalf", Label = "Record completion", Icon = "bi-check2-circle", Group = "Training record",
                Description = $"Complete this assignment on behalf of {ctx.Directory.Name(ctx.Snapshot.UserId("employee"))}, for example after an instructor-led session (TRN-015).",
                SignatureMeaning = TrainingKeys.SignOnBehalf, RequiresComment = true, CommentLabel = "Justification (session, date, evidence)",
            });
            list.Add(new RecordAction
            {
                Key = "void_assignment", Label = "Void assignment", Icon = "bi-slash-circle", Group = "Training record", Variant = "danger",
                Description = "The assignment no longer applies. Voided assignments stay in the training history.",
                RequiresComment = true, CommentLabel = "Reason",
            });
        }
        else if (phase is TrainingKeys.Completed or TrainingKeys.Expired)
            list.Add(new RecordAction
            {
                Key = "void_reissue", Label = "Void and reissue", Icon = "bi-arrow-repeat", Group = "Training record", Variant = "danger",
                Description = "Correct a completed record: it is voided (and kept) and a new pending assignment is issued (INV-030, TRN-061).",
                RequiresComment = true, CommentLabel = "Reason for the correction",
            });
        return Task.FromResult<IEnumerable<RecordAction>>(list);
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        var t = ctx.Get<TrainingService>();
        if (!t.IsCoordinator) return ActionOutcome.Fail("Only training coordinators can do this (TRN-015).");
        if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("A justification is required.");
        var snap = ctx.Snapshot;
        var employee = snap.UserId("employee");
        var course = snap.Data.Main.Link("course") is { } cid ? await t.CourseAsync(cid) : null;
        var comment = input.Comment.Trim();
        try
        {
            switch (actionKey)
            {
                case "complete_on_behalf" when ctx.Record.PhaseType == PhaseType.Normal:
                {
                    if (course == null) return ActionOutcome.Fail("The course no longer exists.");
                    if (!course.Enabled) return ActionOutcome.Fail("The course is disabled, so this assignment can't be completed (TRN-043).");
                    var missing = await t.MissingPrerequisitesAsync(course, employee ?? 0);
                    if (missing.Count > 0) return ActionOutcome.Fail($"{ctx.Directory.Name(employee)} hasn't completed the prerequisite(s): {string.Join(", ", missing)} (INV-107).");
                    var outcome = await Tx.RunAsync(ctx.Db, async () =>
                    {
                        var sig = await ctx.Get<SignatureService>().SignAsync(snap, TrainingKeys.SignOnBehalf, $"Record completion for {ctx.Directory.Name(employee)}",
                            input.Password, input.Certify, null, ctx.Records.ComputeHash, comment);
                        if (!sig.Ok) return ActionOutcome.Fail(sig.Error!);
                        await ctx.Db.SaveChangesAsync();
                        var res = await ctx.Workflow.SystemRouteAsync(ctx.Record.Id, TrainingKeys.Completed, $"Completed on behalf of {ctx.Directory.Name(employee)} by {ctx.Me.Name} (TRN-015)", comment: comment);
                        return res.Ok ? ActionOutcome.Ok("Completion recorded.") : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                    }, o => o.Success);
                    return outcome;
                }
                case "void_assignment" when ctx.Record.PhaseType == PhaseType.Normal:
                {
                    var res = await ctx.Workflow.SystemRouteAsync(ctx.Record.Id, TrainingKeys.Voided, $"Voided by {ctx.Me.Name}: {comment}");
                    return res.Ok ? ActionOutcome.Ok("Assignment voided.") : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                }
                case "void_reissue" when snap.PhaseKey is TrainingKeys.Completed or TrainingKeys.Expired:
                {
                    if (course == null || employee == null) return ActionOutcome.Fail("The course or employee no longer exists.");
                    var created = await Tx.RunAsync(ctx.Db, async () =>
                    {
                        var res = await ctx.Workflow.SystemRouteAsync(ctx.Record.Id, TrainingKeys.Voided, $"Voided for correction by {ctx.Me.Name}: {comment} (INV-030)");
                        if (!res.Ok) throw new WorkflowException(string.Join("; ", res.Errors.Select(e => e.Message)));
                        var (made, skipped) = await t.AssignAsync(course.Id, [employee.Value], null, $"Reissued from {ctx.Record.Number}: {comment}", forceRetraining: true);
                        if (made.Count == 0) throw new WorkflowException("Couldn't reissue: " + string.Join("; ", skipped));
                        return made[0];
                    });
                    return ActionOutcome.Ok($"Voided and reissued as {created.Number}.", created.Id);
                }
            }
        }
        catch (WorkflowException ex) { return ActionOutcome.Fail(ex.Message); }
        return ActionOutcome.Fail("This action isn't available.");
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var t = ctx.Get<TrainingService>();
        var snap = ctx.Snapshot;
        var course = snap.Data.Main.Link("course") is { } cid ? await t.CourseAsync(cid) : null;
        var materials = course == null ? [] : course.Snapshot.AttachmentsOf("materials").ToList();
        var docId = snap.Data.Main.Link("source_doc");
        var doc = docId == null ? null : await ctx.Db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == docId);
        var row = (await t.RowsAsync(ctx.Db.Records.Where(r => r.Id == snap.Record.Id))).FirstOrDefault();
        var employee = snap.UserId("employee");
        var missing = course != null && snap.Record.PhaseType == PhaseType.Normal ? await t.MissingPrerequisitesAsync(course, employee ?? 0) : [];
        return
        [
            new RecordPanel
            {
                Key = "assignment-brief", Title = "Training", Placement = "top", Partial = "~/Modules/Training/Views/_AssignmentBrief.cshtml",
                Model = new AssignmentBriefModel(snap.Record, course, materials, doc, doc != null && await ctx.Access.CanReadAsync(doc.Id), employee,
                    row?.Due ?? snap.Date("due_date"), snap.PhaseKey, row?.CompletedAt, row?.ExpiresOn, employee == ctx.Me.Id, missing, ctx.Directory),
            },
        ];
    }
}
