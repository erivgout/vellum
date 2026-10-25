using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>SAF-011: the compliance posture report for administrators and auditors.</summary>
public class ComplianceModel(AdminOverviewService overview, VellumDbContext db) : AdminPageModel
{
    public List<PostureItem> Items { get; private set; } = [];
    public SystemSettings Settings { get; private set; } = null!;
    public List<ConfigAuditEntry> RecentChanges { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Items = await overview.PostureAsync();
        Settings = await db.Settings.AsNoTracking().FirstAsync();
        RecentChanges = await db.ConfigAudit.AsNoTracking().Where(c => c.ObjectType == "SystemSettings").OrderByDescending(c => c.Id).Take(5).ToListAsync();
    }
}
