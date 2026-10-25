using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure.Jobs;
using Vellum.Modules.Kit;

namespace Vellum.Modules.Projects;

public sealed record StageTasks(string Stage, string Label, int Total, int Done, int Open, int Overdue, Keyword? Decision, DateOnly? ReviewDate);
public sealed record ProjectProgressVm(int Total, int Done, int Open, int Overdue, int DueSoon, string Risk, decimal Percent, List<StageTasks> Stages, string? CurrentStage);

/// <summary>Project progress and calculated schedule risk (PRJ-008): High if any task is overdue, Low if any is due within 3 days.</summary>
public static class ProjectProgress
{
    public const int DueSoonDays = 3;

    public static string Risk(int overdue, int dueSoon) => overdue > 0 ? "high" : dueSoon > 0 ? "low" : "none";

    public static async Task<ProjectProgressVm> ComputeAsync(VellumDbContext db, ConfigModel cfg, RecordSnapshot project)
    {
        var taskForm = cfg.Form("task").Id;
        var tasks = await db.Records.AsNoTracking().Where(r => r.ParentId == project.Record.Id && r.FormId == taskForm && r.PhaseType != PhaseType.Voided)
            .Select(r => new { r.Id, r.PhaseType, r.DueDate }).ToListAsync();
        var stages = await ModuleKit.MainValuesAsync(db, tasks.Select(t => t.Id).ToList(), "stage");
        var today = BusinessCalendar.Today;
        bool IsOpen(PhaseType p) => p == PhaseType.Normal;
        var open = tasks.Where(t => IsOpen(t.PhaseType)).ToList();
        var overdue = open.Count(t => t.DueDate < today);
        var dueSoon = open.Count(t => t.DueDate >= today && t.DueDate <= today.AddDays(DueSoonDays));
        var done = tasks.Count - open.Count;
        var reviews = project.Rows("gate_reviews");
        var byStage = ProjectsApp.Stages.Select(st =>
        {
            var mine = tasks.Where(t => cfg.Keyword(stages[t.Id].Ref("stage"))?.Key == st).ToList();
            var review = reviews.Where(r => project.KeywordKey(r, "gate") == st).OrderByDescending(r => r.Values.Date("review_date")).FirstOrDefault();
            return new StageTasks(st, cfg.Keyword("npi_stage", st)?.Label ?? st, mine.Count, mine.Count(t => !IsOpen(t.PhaseType)),
                mine.Count(t => IsOpen(t.PhaseType)), mine.Count(t => IsOpen(t.PhaseType) && t.DueDate < today),
                review == null ? null : cfg.Keyword(review.Values.Ref("decision")), review?.Values.Date("review_date"));
        }).ToList();
        var percent = tasks.Count == 0 ? 0m : Math.Round(done * 100m / tasks.Count, 0, MidpointRounding.AwayFromZero);
        return new ProjectProgressVm(tasks.Count, done, open.Count, overdue, dueSoon, Risk(overdue, dueSoon), percent, byStage,
            project.Record.PhaseType == PhaseType.Normal ? project.PhaseKey : null);
    }

    /// <summary>Store the calculated values on the project (for views) when they changed.</summary>
    public static async Task RefreshAsync(IServiceProvider sp, Guid projectId)
    {
        var records = sp.GetRequiredService<RecordService>();
        var p = await records.LoadAsync(projectId);
        if (p == null || p.Form.Key != "project") return;
        var vm = await ComputeAsync(sp.GetRequiredService<VellumDbContext>(), p.Config, p);
        if (p.KeywordKey("risk_status") == vm.Risk && p.Number("percent_complete") == vm.Percent &&
            p.Number("open_tasks") == vm.Open && p.Number("overdue_tasks") == vm.Overdue) return;
        await records.UpdateAsync(projectId, new()
        {
            ["risk_status"] = vm.Risk, ["percent_complete"] = vm.Percent, ["open_tasks"] = (decimal)vm.Open, ["overdue_tasks"] = (decimal)vm.Overdue,
        }, "Project progress recalculated");
    }
}

/// <summary>Stage gates (PRJ-020): a gate passes only with a Go decision for the stage and every task of the stage closed.</summary>
public sealed class ProjectBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["project"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        if (m.Date("target_launch") is { } t && m.Date("start_date") is { } s && t < s)
            ctx.Error("target_launch", "The target launch can't be before the start date.");
        var approvers = m.Refs("gate_approvers");
        if (approvers.Count > 0 && approvers.Count < 2)
            ctx.Error("gate_approvers", "Name at least two gate approvers, so no gate is approved by one person alone.");
        if (m.Ref("project_manager") is { } pm && approvers.Count > 0 && !approvers.Contains(pm))
            ctx.Error("gate_approvers", "Include the project manager among the gate approvers.");
        return Task.CompletedTask;
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.Kind != RouteKind.Forward) return;
        var s = ctx.Snapshot;
        var stage = ctx.From.Key;
        var label = ctx.From.Name;
        if (ProjectsApp.Stages.Contains(stage) && stage != "launch")
        {
            var go = s.Rows("gate_reviews").Any(r => s.KeywordKey(r, "gate") == stage && s.KeywordKey(r, "decision") is "go" or "go_conditions");
            if (!go) ctx.Error($"Record the {label} gate review with a Go or Go-with-conditions decision (Gates tab) before signing the gate.");
        }
        var taskForm = ctx.Config.Form("task").Id;
        var open = await ctx.Db.Records.AsNoTracking().Where(r => r.ParentId == s.Record.Id && r.FormId == taskForm && r.PhaseType == PhaseType.Normal)
            .Select(r => new { r.Id, r.Number }).ToListAsync();
        var stages = await ModuleKit.MainValuesAsync(ctx.Db, open.Select(o => o.Id).ToList(), "stage");
        var blocking = ctx.To.Key == "closed"
            ? open.Select(o => o.Number).ToList()
            : open.Where(o => ctx.Config.Keyword(stages[o.Id].Ref("stage"))?.Key == stage).Select(o => o.Number).ToList();
        if (blocking.Count > 0)
            ctx.Error($"Close the open {(ctx.To.Key == "closed" ? "" : label + " ")}task{(blocking.Count > 1 ? "s" : "")} first: {string.Join(", ", blocking)}.");
    }

    public override Task OnRoutedAsync(RoutingContext ctx) => ProjectProgress.RefreshAsync(ctx.Services, ctx.Record.Id);

    public override Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (ctx.Me.IsSystem || ctx.Record.PhaseType == PhaseType.Voided) return Task.FromResult(Enumerable.Empty<RecordAction>());
        var meeting = ctx.Config.FormsByKey.GetValueOrDefault("meeting");
        return Task.FromResult<IEnumerable<RecordAction>>([new RecordAction
        {
            Key = "schedule_meeting", Label = "Schedule meeting", Icon = "bi-calendar-plus", Group = "Project",
            Description = "Create a meeting for this project with the team invited (PRJ-010).",
            DisabledReason = meeting != null && ctx.Access.CanCreate(meeting) ? null : "You can't create meetings.",
            Inputs =
            [
                new ActionField("type", "Type", "select", true, Options: [("project_review", "Project review"), ("design_review", "Design review"), ("gate_review", "Gate review")], Default: "project_review"),
                new ActionField("date", "Date", "date", true, Default: BusinessCalendar.Today.AddDays(7).ToString("yyyy-MM-dd")),
            ],
        }]);
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "schedule_meeting") return ActionOutcome.Fail("This action isn't available.");
        if (!ctx.Access.CanCreate(ctx.Config.Form("meeting"))) return ActionOutcome.Fail("You can't create meetings.");
        if (!DateOnly.TryParseExact(input.Get("date"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) || date < BusinessCalendar.Today) return ActionOutcome.Fail("Choose a date from today on.");
        var s = ctx.Snapshot;
        var type = input.Get("type") is "design_review" or "gate_review" ? input.Get("type")! : "project_review";
        var tz = ctx.Records.TimeZone;
        var startsLocal = date.ToDateTime(new TimeOnly(10, 0));
        var invitees = s.UserIds("gate_approvers").Concat(s.UserIds("team")).Distinct().ToList();
        var pm = s.UserId("project_manager") ?? ctx.Me.Id;
        var res = await ctx.Records.CreateAsync(new CreateRequest
        {
            FormKey = "meeting",
            Values = new()
            {
                ["subject"] = $"{s.Record.Title}: {ctx.Config.Keyword("meeting_type", type)?.Label.ToLowerInvariant()}",
                ["meeting_type"] = type, ["starts_at"] = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startsLocal, DateTimeKind.Unspecified), tz),
                ["chair"] = pm, ["minute_taker"] = ctx.Me.Id, ["invitees"] = invitees, ["project"] = s.Record.Id,
            },
            RowValues = new()
            {
                ["agenda"] =
                [
                    new() { ["topic"] = "Status against plan and open tasks", ["presenter"] = pm },
                    new() { ["topic"] = "Risks, issues and help needed" },
                    new() { ["topic"] = "Actions and next steps" },
                ],
            },
            CreateComment = $"Scheduled from project {s.Record.Number} (PRJ-010)",
        });
        return res.Ok ? ActionOutcome.Ok($"Meeting {res.Record!.Number} scheduled.", res.Record.Id) : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var vm = await ProjectProgress.ComputeAsync(ctx.Db, ctx.Config, ctx.Snapshot);
        return [new RecordPanel { Key = "progress", Title = "Stage gates & progress", Icon = "bi-signpost-2", Placement = "side", Order = -10, Partial = "~/Modules/Projects/Views/_ProjectPanel.cshtml", Model = vm }];
    }
}

/// <summary>Tasks (PRJ-007): completing sets % complete to 100; project progress is recalculated on every change.</summary>
public sealed class TaskBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["task"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        if (m.Date("due_date") is { } due && m.Date("start_date") is { } start && due < start)
            ctx.Error("due_date", "The due date can't be before the start.");
        return Task.CompletedTask;
    }

    public override Task OnCreatedAsync(RecordContext ctx) =>
        ctx.Record.ParentId is { } p ? ProjectProgress.RefreshAsync(ctx.Services, p) : Task.CompletedTask;

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key == "completed" && ctx.Snapshot.Number("percent_complete") != 100)
            await ctx.Records.UpdateAsync(ctx.Record.Id, new() { ["percent_complete"] = 100m }, "Task completed (PRJ-007)");
        if (ctx.Record.ParentId is { } p) await ProjectProgress.RefreshAsync(ctx.Services, p);
    }
}

/// <summary>Recurring task schedules: validated pattern, locked once tasks exist, next occurrence shown (TSK-002..004).</summary>
public sealed class RecurringTaskBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["recurring_task"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        var end = ctx.KeywordKey("end_rule");
        if (end == "after" && m.Number("end_after") == null) ctx.Error("end_after", "Give the number of occurrences.");
        if (end == "on_date" && m.Date("end_on") == null) ctx.Error("end_on", "Give the last date.");
        if (end == "on_date" && m.Date("end_on") is { } last && m.Date("start_date") is { } first && last < first)
            ctx.Error("end_on", "The last date can't be before the first occurrence.");

        var generated = (int)(m.Number("generated_count") ?? 0);
        if (ctx.Current is { } cur && generated > 0)
            foreach (var key in new[] { "frequency", "interval", "start_date" })
                if (cur.Data.Main.Canonical(key) != m.Canonical(key))
                    ctx.Error(key, "The pattern can't change once tasks have been generated. End this schedule and create a new one.");

        if (ctx.KeywordKey("frequency") is { } freq && m.Number("interval") is { } iv && iv >= 1 && m.Date("start_date") is { } start)
        {
            var next = Recurrence.Occurrence(freq, (int)iv, start, generated);
            var ended = ctx.KeywordKey("recurrence_status") == "ended" || Recurrence.Ended(end, (int?)m.Number("end_after"), m.Date("end_on"), generated, next);
            ctx.Set("next_occurrence", ended ? null : next);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Meetings: attendees loaded from invitees (MTG-005); action items created when the minutes are published (MTG-006).</summary>
public sealed class MeetingBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["meeting"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        var both = m.Refs("attendees").Intersect(m.Refs("absentees")).ToList();
        if (both.Count > 0) ctx.Error("absentees", "Someone can't be both an attendee and an absentee.");
        return Task.CompletedTask;
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "completed") return Task.CompletedTask;
        if (!ctx.Snapshot.Rows("agenda").Any(r => !string.IsNullOrWhiteSpace(r.Values.Text("minutes"))))
            ctx.Error("Record the minutes of at least one agenda item before publishing.");
        if (ctx.Snapshot.UserIds("attendees").Count == 0) ctx.Error("Record who attended.");
        return Task.CompletedTask;
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.To.Key == "minutes" && s.UserIds("attendees").Count == 0 && s.UserIds("invitees").Count > 0)
        {
            await ctx.Records.UpdateAsync(s.Record.Id, new() { ["attendees"] = s.UserIds("invitees").ToList() }, "Attendees loaded from the invitees (MTG-005)");
            return;
        }
        if (ctx.To.Key != "completed") return;
        var proposed = new RecordData();
        var updated = proposed.RowsOf("actions");
        var created = 0;
        foreach (var row in s.Rows("actions"))
        {
            var clone = row.Clone();
            if (row.CreatedRecordId == null && row.Values.Text("action") is { } action && row.Values.Ref("assignee") is { } assignee)
            {
                var due = row.Values.Date("due") is { } d && d >= BusinessCalendar.Today ? d : BusinessCalendar.Today.AddDays(7);
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "action_item", StartPhaseKey = "open", ParentId = s.Record.Id, SkipAccessCheck = true, Assignees = [assignee], DueDate = due,
                    Values = new()
                    {
                        ["subject"] = action, ["assignee"] = assignee, ["due_date"] = due,
                        ["description"] = $"Action from meeting {s.Record.Number}: {s.Record.Title}" + (row.Values.Text("agenda_ref") is { } a ? $" (agenda item: {a})" : ""),
                    },
                    CreateComment = $"Created from the minutes of {s.Record.Number} (MTG-006)",
                });
                if (!res.Ok) throw new WorkflowException("Couldn't create an action item: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                clone.CreatedRecordId = res.Record!.Id;
                created++;
            }
            updated.Add(clone);
        }
        if (created > 0)
            await ctx.Records.SaveAsync(await ctx.Records.LoadRequiredAsync(s.Record.Id), proposed, new SaveOptions { System = true });
    }
}

/// <summary>Daily recalculation of project schedule risk (tasks become overdue with time, PRJ-008).</summary>
public sealed class ProjectHealthJob(VellumDbContext db, ConfigCache cfg, IServiceProvider sp) : IScheduledJob
{
    public string Key => "project-health";
    public string Name => "Project schedule risk";
    public string Description => "Recalculates progress and schedule risk of open projects as tasks become due or overdue.";
    public int DefaultIntervalMinutes => 360;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("project", out var form)) return new JobResult(0);
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseType == PhaseType.Normal).Select(r => r.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            await ProjectProgress.RefreshAsync(sp, id);
        }
        return new JobResult(ids.Count, $"{ids.Count} open project(s) recalculated.");
    }
}
