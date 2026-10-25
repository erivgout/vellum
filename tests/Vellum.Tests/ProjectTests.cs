using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Projects;

namespace Vellum.Tests;

/// <summary>Projects and tasks: recurrence (TSK-002..005) with catch-up, stage gates (PRJ-020), PRJ-007/008.</summary>
[Collection("vellum")]
public class ProjectTests(VellumFixture fx)
{
    [Fact] // TSK-002: occurrences are computed from the start date, so month ends don't drift
    public void Recurrence_patterns()
    {
        var start = new DateOnly(2026, 1, 31);
        Assert.Equal(new DateOnly(2026, 2, 28), Recurrence.Occurrence("monthly", 1, start, 1));
        Assert.Equal(new DateOnly(2026, 3, 31), Recurrence.Occurrence("monthly", 1, start, 2));
        Assert.Equal(new DateOnly(2026, 7, 31), Recurrence.Occurrence("monthly", 3, start, 2));
        Assert.Equal(new DateOnly(2026, 2, 14), Recurrence.Occurrence("weekly", 2, start, 1));
        Assert.Equal(new DateOnly(2026, 2, 3), Recurrence.Occurrence("daily", 3, start, 1));
        Assert.Equal(new DateOnly(2028, 1, 31), Recurrence.Occurrence("yearly", 2, start, 1));
        Assert.True(Recurrence.Ended("after", 3, null, 3, start));
        Assert.False(Recurrence.Ended("after", 3, null, 2, start));
        Assert.True(Recurrence.Ended("on_date", null, new DateOnly(2026, 2, 1), 1, new DateOnly(2026, 2, 2)));
        Assert.False(Recurrence.Ended("never", null, null, 1000, start));
    }

    [Fact] // TSK-005: after an outage every missed occurrence is pending, oldest first; none is skipped
    public void Pending_occurrences_include_every_missed_run()
    {
        var start = new DateOnly(2026, 3, 2);
        var pending = Recurrence.Pending("weekly", 1, start, generated: 1, endRule: "never", endAfter: null, endOn: null, today: new DateOnly(2026, 3, 30));
        Assert.Equal(new[] { new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 23), new DateOnly(2026, 3, 30) }, pending.Select(p => p.Date));
        Assert.Equal(new[] { 1, 2, 3, 4 }, pending.Select(p => p.Index));
        var capped = Recurrence.Pending("weekly", 1, start, 0, "after", 2, null, new DateOnly(2026, 6, 1));
        Assert.Equal(2, capped.Count);
    }

    private static async Task<Guid> ScheduleAsync(IServiceScope s, DateOnly start, string endRule = "never", decimal? endAfter = null) =>
        (await s.CreateAsync("recurring_task", new()
        {
            ["subject"] = "Eyewash check (test)", ["recurrence_status"] = "active", ["assignee"] = s.UserId("mchen"),
            ["frequency"] = "weekly", ["interval"] = 1m, ["start_date"] = start, ["end_rule"] = endRule, ["end_after"] = endAfter, ["due_days"] = 2m,
        })).Id;

    [Fact] // TSK-003/004/005 end to end
    public async Task Recurring_tasks_catch_up_missed_runs_with_original_due_dates_and_stop_at_the_end()
    {
        using var s = fx.As("mchen");
        var today = BusinessCalendar.Today;
        var id = await ScheduleAsync(s, today.AddDays(-21));
        s.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("Recurring task generator");
        var generator = s.ServiceProvider.GetRequiredService<RecurringTaskGenerator>();

        Assert.Equal(4, await generator.GenerateAsync(id, today)); // -21, -14, -7 caught up, plus today
        var tasks = await s.Db().Records.AsNoTracking().Where(r => r.ParentId == id).ToListAsync();
        var values = await Vellum.Modules.Kit.ModuleKit.MainValuesAsync(s.Db(), tasks.Select(t => t.Id).ToList(), "occurrence_date", "due_date", "late_generated");
        var byOcc = values.Values.OrderBy(v => v.Date("occurrence_date")).ToList();
        Assert.Equal(new[] { today.AddDays(-21), today.AddDays(-14), today.AddDays(-7), today }, byOcc.Select(v => v.Date("occurrence_date")!.Value));
        Assert.Equal(new[] { today.AddDays(-19), today.AddDays(-12), today.AddDays(-5), today.AddDays(2) }, byOcc.Select(v => v.Date("due_date")!.Value)); // original due dates kept
        Assert.Equal(new[] { true, true, true, false }, byOcc.Select(v => v.Bool("late_generated") == true));
        Assert.All(tasks, t => Assert.Equal(PhaseType.Normal, t.PhaseType));

        Assert.Equal(0, await generator.GenerateAsync(id, today)); // idempotent: a second run creates nothing
        var def = await s.LoadAsync(id);
        Assert.Equal(4m, def.Number("generated_count"));
        Assert.Equal(3m, def.Number("late_count"));
        Assert.Equal(today.AddDays(7), def.Date("next_occurrence"));

        // TSK-004: a schedule ending after two occurrences stops itself.
        var ending = await ScheduleAsync(s, today.AddDays(-30), "after", 2m);
        Assert.Equal(2, await generator.GenerateAsync(ending, today));
        var ended = await s.LoadAsync(ending);
        Assert.Equal("ended", ended.KeywordKey("recurrence_status"));
        Assert.Null(ended.Date("next_occurrence"));
        Assert.Equal(0, await generator.GenerateAsync(ending, today.AddDays(30)));
    }

    [Fact] // TSK-002: the pattern is locked once tasks exist
    public async Task Recurrence_pattern_cannot_change_after_generation()
    {
        using var s = fx.As("mchen");
        var id = await ScheduleAsync(s, BusinessCalendar.Today.AddDays(-7));
        using (var sys = fx.As(null))
        {
            sys.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("test");
            await sys.ServiceProvider.GetRequiredService<RecurringTaskGenerator>().GenerateAsync(id, BusinessCalendar.Today);
        }
        var res = await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, new() { ["interval"] = 2m });
        Assert.False(res.Ok);
    }

    [Fact] // PRJ-020 stage gates, PRJ-007, PRJ-008
    public async Task Gates_need_a_go_decision_and_closed_stage_tasks_and_every_approver()
    {
        using var pm = fx.As("akim");
        var p = await pm.CreateAsync("project", new()
        {
            ["project_name"] = "Gate test", ["project_type"] = "npi", ["project_manager"] = pm.UserId("akim"), ["sponsor"] = pm.UserId("lgarcia"),
            ["gate_approvers"] = new[] { pm.UserId("akim"), pm.UserId("lgarcia") }, ["start_date"] = BusinessCalendar.Today,
            ["target_launch"] = BusinessCalendar.Today.AddDays(200), ["objectives"] = "Test",
        });
        var task = await pm.CreateAsync("task", new()
        {
            ["subject"] = "Concept task", ["assignee"] = pm.UserId("akim"), ["stage"] = "concept", ["start_date"] = BusinessCalendar.Today.AddDays(-5),
            ["due_date"] = BusinessCalendar.Today.AddDays(-1),
        }, parent: p.Id);
        Assert.Equal("high", (await pm.LoadAsync(p.Id)).KeywordKey("risk_status")); // an overdue task → High (PRJ-008)

        var noReview = await pm.RouteAsync(p.Id, "feasibility");
        Assert.False(noReview.Ok);
        Assert.Contains(noReview.Errors, e => e.Message.Contains("gate review"));

        var records = pm.ServiceProvider.GetRequiredService<RecordService>();
        var snap = await pm.LoadAsync(p.Id);
        var data = new RecordData();
        var bag = new ValueBag();
        foreach (var (k, v) in new (string, object?)[] { ("gate", "concept"), ("review_date", BusinessCalendar.Today), ("decision", "go"), ("summary", "OK") })
            bag.Set(k, FieldCodec.FromObject(snap.Form.Fields.First(f => f.Key == k), v, snap.Config));
        data.RowsOf("gate_reviews").Add(new RowBag { IsNew = true, Values = bag });
        Assert.True((await records.SaveAsync(snap, data, new SaveOptions { System = true })).Ok);

        var openTask = await pm.RouteAsync(p.Id, "feasibility");
        Assert.False(openTask.Ok);
        Assert.Contains(openTask.Errors, e => e.Message.Contains("Close the open"));

        await records.UpdateAsync(task.Id, new() { ["outcome"] = "Done" });
        Assert.True((await pm.RouteAsync(task.Id, "completed")).Ok);
        Assert.Equal(100m, (await pm.LoadAsync(task.Id)).Number("percent_complete")); // PRJ-007
        var progress = await pm.LoadAsync(p.Id);
        Assert.Equal(100m, progress.Number("percent_complete"));
        Assert.Equal("none", progress.KeywordKey("risk_status"));

        var first = await pm.RouteAsync(p.Id, "feasibility");
        Assert.True(first.Ok);
        Assert.False(first.Moved); // one of two approvers has signed
        using var sponsor = fx.As("lgarcia");
        var second = await sponsor.RouteAsync(p.Id, "feasibility");
        Assert.True(second.Ok && second.Moved);
        Assert.Equal("feasibility", (await sponsor.LoadAsync(p.Id)).PhaseKey);
        Assert.Equal(2, await sponsor.Db().Signatures.CountAsync(x => x.RecordId == p.Id && x.Meaning == "Gate approved: Concept"));
    }
}
