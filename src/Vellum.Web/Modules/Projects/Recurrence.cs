using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Projects;

/// <summary>
/// Recurrence patterns (TSK-002, simplified): every N days, weeks, months or years from the first occurrence. Occurrence k is always
/// computed from the start date (never from the previous occurrence), so month-end dates don't drift (31 Jan → 28 Feb → 31 Mar).
/// </summary>
public static class Recurrence
{
    public static DateOnly Occurrence(string frequency, int interval, DateOnly start, int k) => frequency switch
    {
        "daily" => start.AddDays(interval * k),
        "weekly" => start.AddDays(7 * interval * k),
        "monthly" => start.AddMonths(interval * k),
        "yearly" => start.AddYears(interval * k),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown recurrence frequency."),
    };

    /// <summary>TSK-004: has the schedule ended before occurrence <paramref name="k"/>?</summary>
    public static bool Ended(string? endRule, int? endAfter, DateOnly? endOn, int k, DateOnly occurrence) => endRule switch
    {
        "after" => endAfter is { } n && k >= n,
        "on_date" => endOn is { } e && occurrence > e,
        _ => false,
    };

    /// <summary>
    /// TSK-003 / TSK-005: every occurrence due on or before <paramref name="today"/> that hasn't been generated yet, oldest first.
    /// After an outage this returns all the missed occurrences, so none is skipped.
    /// </summary>
    public static List<(int Index, DateOnly Date)> Pending(string frequency, int interval, DateOnly start, int generated,
        string? endRule, int? endAfter, DateOnly? endOn, DateOnly today, int max = 500)
    {
        var list = new List<(int, DateOnly)>();
        if (interval < 1) return list;
        for (var k = generated; list.Count < max; k++)
        {
            var occ = Occurrence(frequency, interval, start, k);
            if (occ > today || Ended(endRule, endAfter, endOn, k, occ)) break;
            list.Add((k, occ));
        }
        return list;
    }
}

/// <summary>Creates the tasks of recurring task definitions (TSK-003..005). Used by the scheduled job and by demo seeding.</summary>
[ScopedService]
public sealed class RecurringTaskGenerator(VellumDbContext db, RecordService records, ConfigCache cfg)
{
    public async Task<int> RunAsync(DateOnly today, Action<string>? log = null)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("recurring_task", out var form)) return 0;
        var active = cfg.Current.Keyword("recurrence_status", "active")?.Id;
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id &&
                db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "recurrence_status" && v.RefId == active))
            .OrderBy(r => r.Sequence).Select(r => r.Id).ToListAsync();
        var total = 0;
        foreach (var id in ids)
        {
            var n = await GenerateAsync(id, today);
            if (n > 0) log?.Invoke($"{n} task(s) generated for {id}.");
            total += n;
        }
        return total;
    }

    /// <summary>Generate the pending occurrences of one definition. Each occurrence and the counter update commit together (idempotent).</summary>
    public async Task<int> GenerateAsync(Guid definitionId, DateOnly today)
    {
        db.ChangeTracker.Clear();
        var s = await records.LoadRequiredAsync(definitionId);
        if (s.KeywordKey("recurrence_status") != "active") return 0;
        var frequency = s.KeywordKey("frequency");
        var interval = (int)(s.Number("interval") ?? 0);
        if (frequency == null || interval < 1 || s.Date("start_date") is not { } start || s.UserId("assignee") is not { } assignee) return 0;
        var endRule = s.KeywordKey("end_rule");
        var endAfter = (int?)s.Number("end_after");
        var endOn = s.Date("end_on");
        var generated = (int)(s.Number("generated_count") ?? 0);
        var dueDays = (int)(s.Number("due_days") ?? 0);
        var taskForm = cfg.Current.Form("task");

        var created = 0;
        foreach (var (k, occ) in Recurrence.Pending(frequency, interval, start, generated, endRule, endAfter, endOn, today))
        {
            await Tx.RunAsync(db, async () =>
            {
                db.ChangeTracker.Clear();
                var late = occ < today;
                var occDate = occ.ToDateTime(TimeOnly.MinValue);
                var exists = await db.Records.AnyAsync(r => r.ParentId == definitionId && r.FormId == taskForm.Id &&
                    db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "occurrence_date" && v.Date == occDate));
                if (!exists)
                {
                    var due = occ.AddDays(dueDays);
                    var res = await records.CreateAsync(new CreateRequest
                    {
                        FormKey = "task", ParentId = definitionId, SkipAccessCheck = true, Assignees = [assignee], DueDate = due,
                        Values = new()
                        {
                            ["subject"] = $"{s.Text("subject")} · {occ:dd MMM yyyy}", ["description"] = s.Text("description"),
                            ["assignee"] = assignee, ["start_date"] = occ, ["due_date"] = due, ["occurrence_date"] = occ, ["late_generated"] = late,
                            ["priority"] = s.KeywordKey("priority"),
                        },
                        CreateComment = late
                            ? $"Late-generated by {s.Record.Number} for the {occ:dd MMM yyyy} occurrence: the scheduler caught up a missed run and kept the original due date (TSK-005)."
                            : $"Generated by recurring task {s.Record.Number} for the {occ:dd MMM yyyy} occurrence (TSK-003).",
                    });
                    if (!res.Ok) throw new InvalidOperationException($"{s.Record.Number}: couldn't generate the {occ:dd MMM yyyy} task: {string.Join("; ", res.Errors.Select(e => e.Message))}");
                    created++;
                }
                var lateCount = (int)((await records.LoadRequiredAsync(definitionId)).Number("late_count") ?? 0) + (late && !exists ? 1 : 0);
                var upd = await records.UpdateAsync(definitionId, new() { ["generated_count"] = k + 1, ["last_occurrence"] = occ, ["late_count"] = lateCount },
                    $"Occurrence {k + 1} ({occ:dd MMM yyyy}) generated");
                if (!upd.Ok) throw new InvalidOperationException(string.Join("; ", upd.Errors.Select(e => e.Message)));
                return true;
            });
            generated = k + 1;
        }

        // TSK-004: when the end condition is met the schedule stops itself.
        var nextOcc = Recurrence.Occurrence(frequency, interval, start, generated);
        db.ChangeTracker.Clear();
        if (Recurrence.Ended(endRule, endAfter, endOn, generated, nextOcc))
            await records.UpdateAsync(definitionId, new() { ["recurrence_status"] = "ended", ["next_occurrence"] = null }, "End condition reached (TSK-004)");
        else
            await records.UpdateAsync(definitionId, new() { ["next_occurrence"] = nextOcc });
        return created;
    }
}

/// <summary>TSK-005 / SAF-030: hourly generation of recurring tasks with catch-up of every missed occurrence.</summary>
public sealed class RecurringTaskJob(RecurringTaskGenerator generator) : IScheduledJob
{
    public string Key => "recurring-tasks";
    public string Name => "Recurring task generator";
    public string Description => "Creates the tasks of recurring task schedules, catching up every occurrence missed during downtime (marked late-generated).";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var n = await generator.RunAsync(BusinessCalendar.Today, ctx.Log);
        return new JobResult(n, $"Generated {n} task(s).");
    }
}
