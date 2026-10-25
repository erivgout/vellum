using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>ADM-1201: Administration Center home with overview cards and the numbered category navigation.</summary>
public class IndexModel(AdminOverviewService overview, VellumDbContext db, TaskAdminService tasks) : AdminPageModel
{
    public AdminStats Stats { get; private set; } = null!;
    public List<AuditEntry> Recent { get; private set; } = [];
    public List<SecurityEvent> Failures { get; private set; } = [];
    public List<TaskProfile> FailingTasks { get; private set; } = [];
    public List<User> Locked { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await tasks.EnsureProfilesAsync();
        Stats = await overview.StatsAsync();
        Recent = await db.Audit.AsNoTracking().Where(a => a.RecordId == null).OrderByDescending(a => a.Id).Take(8).ToListAsync();
        Failures = await db.SecurityEvents.AsNoTracking().Where(e => !e.Success).OrderByDescending(e => e.Id).Take(6).ToListAsync();
        FailingTasks = await db.Tasks.AsNoTracking().Where(t => t.Enabled && t.LastOutcome == "Failed").ToListAsync();
        Locked = await db.Users.AsNoTracking().Where(u => !u.IsSystemIdentity && u.Status == UserStatus.Disabled && u.StatusReason != null && u.StatusReason.StartsWith("Locked"))
            .OrderBy(u => u.DisplayName).Take(5).ToListAsync();
    }
}
