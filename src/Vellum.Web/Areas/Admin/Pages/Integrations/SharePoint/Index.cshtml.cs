using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Engine;
using Vellum.Modules.Integrations.SharePoint;

namespace Vellum.Areas.Admin.Pages.Integrations.SharePoint;

public sealed record SpRuleRow(SharePointRule Rule, string FormName, string PhaseName, string? AppColor, string? AppIcon, SharePointProfile? Profile, SharePointLibrary? Library,
    string? MappingName, int CurrentFiles);

public sealed record SpMappingRow(SharePointMapping Mapping, string FormName, int Columns, int UsedBy);

/// <summary>Administration › Integrations › SharePoint: profiles (INT-301), rules (DSN-602), mappings (INT-302) and queue health.</summary>
public class IndexModel(VellumDbContext db, ConfigCache cfg, SharePointAdminService admin) : AdminPageModel
{
    public List<SharePointProfile> Profiles { get; private set; } = [];
    public List<SpRuleRow> Rules { get; private set; } = [];
    public List<SpMappingRow> Mappings { get; private set; } = [];
    public int CurrentFiles { get; private set; }
    public int Pending { get; private set; }
    public int Retrying { get; private set; }
    public int Failed { get; private set; }
    public int PublishedWeek { get; private set; }

    public async Task OnGetAsync()
    {
        Profiles = await db.Set<SharePointProfile>().AsNoTracking().Include(p => p.Libraries).OrderBy(p => p.Name).ToListAsync();
        var libs = Profiles.SelectMany(p => p.Libraries).ToDictionary(l => l.Id);
        var mappings = await db.Set<SharePointMapping>().AsNoTracking().Include(m => m.Entries).OrderBy(m => m.Name).ToListAsync();
        var rules = await db.Set<SharePointRule>().AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        var pubs = db.Set<SharePointPublication>().AsNoTracking();
        var current = await pubs.Where(p => p.Status == SpPublicationStatus.Published).GroupBy(p => p.RuleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var c = cfg.Current;
        Rules = rules.Select(r =>
        {
            var form = c.FormsByKey.GetValueOrDefault(r.FormKey);
            var app = form != null ? c.AppsById.GetValueOrDefault(form.AppId) : null;
            var lib = libs.GetValueOrDefault(r.LibraryId);
            return new SpRuleRow(r, form?.Name ?? r.FormKey, form != null ? SharePointAdminService.PhaseLabel(form, r.PhaseKey) : r.PhaseKey, app?.Color, form?.Icon,
                Profiles.FirstOrDefault(p => p.Id == r.ProfileId), lib, mappings.FirstOrDefault(m => m.Id == r.MappingId)?.Name, current.GetValueOrDefault(r.Id));
        }).ToList();
        Mappings = mappings.Select(m => new SpMappingRow(m, c.FormsByKey.GetValueOrDefault(m.FormKey)?.Name ?? m.FormKey, m.Entries.Count, rules.Count(r => r.MappingId == m.Id))).ToList();
        CurrentFiles = current.Values.Sum();
        var jobs = db.Set<SharePointJob>().AsNoTracking();
        Pending = await jobs.CountAsync(j => j.Status == SpJobStatus.Pending);
        Retrying = await jobs.CountAsync(j => j.Status == SpJobStatus.Pending && j.Attempts > 0);
        Failed = await jobs.CountAsync(j => j.Status == SpJobStatus.Failed);
        var week = DateTime.UtcNow.AddDays(-7);
        PublishedWeek = await pubs.CountAsync(p => p.PublishedAt >= week || p.UpdatedAt >= week);
    }

    public async Task<IActionResult> OnPostToggleAsync(int id, bool enabled) => Done(await admin.SetRuleEnabledAsync(id, enabled), "/admin/integrations/sharepoint");
    public async Task<IActionResult> OnPostPublishExistingAsync(int id) => Done(await admin.PublishExistingAsync(id), "/admin/integrations/sharepoint");
    public async Task<IActionResult> OnPostProcessAsync() => Done(await admin.ProcessNowAsync(), "/admin/integrations/sharepoint");
}
