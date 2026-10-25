using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Infrastructure.Ui;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

public sealed record AppCard(DesignTarget Target, int Records, int Changes, int Forms, int Workflows, int Views, DateTime? LastPublished);

/// <summary>Designer home (DSN-006): the applications you can design, drafts in progress and "New application".</summary>
public class IndexModel(DesignerStore store, VellumDbContext db) : DesignerPageModel(store)
{
    public List<AppCard> Cards { get; private set; } = [];
    public List<AppDefinition> SystemApps { get; private set; } = [];
    public bool CanCreate => Store.CanCreateApps;
    public int Version => Store.Cfg.Version;
    public List<ConfigPublication> Recent { get; private set; } = [];
    public IEnumerable<string> Suites => UiContext.SuiteOrder.Select(s => s.Name).Concat(Store.Cfg.Apps.Select(a => a.Suite)).Distinct();

    [BindProperty] public string? NewName { get; set; }
    [BindProperty] public string? NewKey { get; set; }
    [BindProperty] public string? NewDescription { get; set; }
    [BindProperty] public string? NewIcon { get; set; }
    [BindProperty] public string? NewColor { get; set; }
    [BindProperty] public string? NewSuite { get; set; }
    public string? NewError { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Store.IsAnyDesigner && !CanCreate) return Forbid();
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var targets = await Store.MyTargetsAsync();
        var records = await db.Records.AsNoTracking().GroupBy(r => r.AppId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var pubs = await Store.Publications.AsNoTracking().OrderByDescending(p => p.VersionNumber).Take(200).ToListAsync();
        foreach (var t in targets.OrderBy(t => t.Doc.Suite).ThenBy(t => t.Doc.Name))
        {
            var changes = 0;
            if (t.HasDraft)
            {
                var doc = DesignDocs.Clone(t.Doc);
                DesignDocs.Reconcile(doc, t.LiveDoc);
                changes = DesignDiff.Compare(t.LiveDoc, doc).Count;
            }
            Cards.Add(new AppCard(t, t.Live != null ? records.GetValueOrDefault(t.Live.Id) : 0, changes, t.Doc.Forms.Count,
                t.Doc.Forms.Sum(f => f.Workflows.Count), t.Doc.Views.Count, pubs.FirstOrDefault(p => p.AppKey == t.AppKey)?.PublishedAt));
        }
        if (Store.Me.IsAdmin) SystemApps = Store.Cfg.Apps.Where(a => a.IsSystem).ToList();
        var mine = targets.Select(t => t.AppKey).ToHashSet();
        Recent = pubs.Where(p => mine.Contains(p.AppKey)).Take(6).ToList();
    }

    /// <summary>DSN-001: "New application" needs the Application Creator permission. The app exists only as a draft until published.</summary>
    public async Task<IActionResult> OnPostNewAppAsync()
    {
        if (!CanCreate) return Forbid();
        var name = (NewName ?? "").Trim();
        var key = string.IsNullOrWhiteSpace(NewKey) ? DesignDocs.Slug(name) : NewKey.Trim();
        var drafts = await Store.Drafts.AsNoTracking().Select(d => d.AppKey).ToListAsync();
        if (name.Length == 0) NewError = "Give the application a name.";
        else if (DesignDocs.KeyProblem(key) is { } kp) NewError = $"The design name {kp}.";
        else if (Store.Cfg.AppsByKey.ContainsKey(key) || drafts.Contains(key, StringComparer.OrdinalIgnoreCase)) NewError = $"The design name '{key}' is already used.";
        if (NewError != null)
        {
            await LoadAsync();
            return Page();
        }
        var taken = Store.Cfg.FormsByKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var icon = NewIcon is { Length: > 3 } i && i.StartsWith("bi-") ? i : "bi-grid-1x2";
        var color = NewColor is { Length: 7 } c && c.StartsWith('#') ? c : "#0f766e";
        var doc = DesignerOps.NewApp(name, key, string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim(), icon, color,
            string.IsNullOrWhiteSpace(NewSuite) ? "Operations" : NewSuite.Trim(), taken);
        await Store.CreateAppDraftAsync(doc);
        Toast($"{name} created as a draft. Design it, then publish to make it available.");
        return Redirect($"/designer/apps/{key}");
    }
}
