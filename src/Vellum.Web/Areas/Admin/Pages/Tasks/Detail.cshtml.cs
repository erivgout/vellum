using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Tasks;

/// <summary>One task profile: schedule, run now and run history with logs (ADM-801..804, ADM-1006).</summary>
public class DetailModel(VellumDbContext db, TaskAdminService tasks) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Outcome { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public TaskProfile Profile { get; private set; } = null!;
    public Paged<TaskRun> Runs { get; private set; } = null!;
    public int Total { get; private set; }
    public int Failed { get; private set; }
    public double? AvgSeconds { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await tasks.EnsureProfilesAsync();
        var t = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Key == Key);
        if (t == null) return NotFound();
        Profile = t;
        var all = db.TaskRuns.AsNoTracking().Where(r => r.TaskId == t.Id);
        Total = await all.CountAsync();
        Failed = await all.CountAsync(r => r.Outcome == "Failed");
        var recent = await all.Where(r => r.EndedAt != null).OrderByDescending(r => r.Id).Take(50).Select(r => new { r.StartedAt, r.EndedAt }).ToListAsync();
        AvgSeconds = recent.Count == 0 ? null : recent.Average(r => (r.EndedAt!.Value - r.StartedAt).TotalSeconds);
        var q = all;
        if (Outcome is "Succeeded" or "Failed") q = q.Where(r => r.Outcome == Outcome);
        Runs = await q.OrderByDescending(r => r.Id).PageAsync(P, 25);
        return Page();
    }

    private string Self => $"/admin/tasks/{Key}";

    public async Task<IActionResult> OnPostToggleAsync(bool enabled) => Done(await tasks.SetEnabledAsync(Key, enabled), Self);
    public async Task<IActionResult> OnPostRunAsync() => Done(await tasks.RunNowAsync(Key), Self);

    public async Task<IActionResult> OnPostIntervalAsync(int amount, string unit)
    {
        var minutes = unit switch { "days" => amount * 1440, "hours" => amount * 60, _ => amount };
        return Done(await tasks.SetIntervalAsync(Key, minutes), Self);
    }
}
