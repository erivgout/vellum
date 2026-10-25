using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Tasks;

public sealed record TaskRow(TaskProfile Task, int Runs24h, int Failures24h, TaskRun? Last);

/// <summary>ADM §5.8 task profiles: enabled flag, schedule, last and next run, run now.</summary>
public class IndexModel(VellumDbContext db, TaskAdminService tasks) : AdminPageModel
{
    public List<TaskRow> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await tasks.EnsureProfilesAsync();
        var list = await db.Tasks.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        var since = DateTime.UtcNow.AddHours(-24);
        var stats = await db.TaskRuns.Where(r => r.StartedAt >= since).GroupBy(r => r.TaskId)
            .Select(g => new { g.Key, Runs = g.Count(), Failed = g.Count(r => r.Outcome == "Failed") }).ToDictionaryAsync(x => x.Key);
        var lastIds = await db.TaskRuns.GroupBy(r => r.TaskId).Select(g => g.Max(r => r.Id)).ToListAsync();
        var last = await db.TaskRuns.AsNoTracking().Where(r => lastIds.Contains(r.Id)).ToDictionaryAsync(r => r.TaskId);
        Rows = list.Select(t => new TaskRow(t, stats.GetValueOrDefault(t.Id)?.Runs ?? 0, stats.GetValueOrDefault(t.Id)?.Failed ?? 0, last.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<IActionResult> OnPostToggleAsync(string key, bool enabled) => Done(await tasks.SetEnabledAsync(key, enabled), "/admin/tasks");
    public async Task<IActionResult> OnPostRunAsync(string key) => Done(await tasks.RunNowAsync(key), "/admin/tasks");
}
