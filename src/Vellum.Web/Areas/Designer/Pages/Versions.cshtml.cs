using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

public sealed record VersionRow(ConfigVersion Version, List<ConfigPublication> Publications, int AuditEntries);

/// <summary>DSN-1203: numbered configuration versions, compare any two (from the configuration audit trail) and restore.</summary>
public class VersionsModel(DesignerStore store, DesignerPublisher publisher, VellumDbContext db) : DesignerPageModel(store)
{
    [BindProperty(SupportsGet = true)] public int? A { get; set; }
    [BindProperty(SupportsGet = true)] public int? B { get; set; }
    [BindProperty(SupportsGet = true)] public int? V { get; set; }
    [BindProperty] public int Version { get; set; }

    public List<VersionRow> Rows { get; private set; } = [];
    public List<ObjectChange>? Compare { get; private set; }
    public List<ConfigAuditEntry> CompareEntries { get; private set; } = [];
    public List<string> Accepted { get; private set; } = [];
    public string? CompareTitle { get; private set; }
    public List<(string Key, string Name)> AppChoices { get; private set; } = [];
    public HashSet<string> Designable { get; private set; } = [];
    public string? AppName => AppChoices.FirstOrDefault(a => a.Key == App).Name;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Store.IsAnyDesigner) return Forbid();
        await Store.EnsureSchemaAsync();
        var targets = await Store.MyTargetsAsync();
        Designable = targets.Select(t => t.AppKey).ToHashSet();
        AppChoices = targets.Where(t => !t.IsNew).Select(t => (t.AppKey, t.Doc.Name)).OrderBy(x => x.Name).ToList();
        if (!string.IsNullOrEmpty(App) && !Designable.Contains(App)) return Forbid();

        var versions = await db.ConfigVersions.AsNoTracking().OrderByDescending(v => v.Number).Take(300).ToListAsync();
        var pubs = await Store.Publications.AsNoTracking().OrderByDescending(p => p.VersionNumber).Take(1000)
            .Select(p => new ConfigPublication { Id = p.Id, VersionNumber = p.VersionNumber, AppKey = p.AppKey, AppName = p.AppName, PublishedAt = p.PublishedAt, PublishedById = p.PublishedById, PublishedByName = p.PublishedByName, Note = p.Note, Changes = p.Changes, AcceptedWarnings = p.AcceptedWarnings })
            .ToListAsync();
        var counts = await db.ConfigAudit.AsNoTracking().Where(e => e.ConfigVersion > 0).GroupBy(e => e.ConfigVersion).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        foreach (var v in versions)
        {
            var mine = pubs.Where(p => p.VersionNumber == v.Number).ToList();
            if (!string.IsNullOrEmpty(App) && mine.All(p => p.AppKey != App) && mine.Count > 0) continue;
            if (!string.IsNullOrEmpty(App) && mine.Count == 0 && !(v.Note ?? "").Contains(App, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(new VersionRow(v, mine, counts.GetValueOrDefault(v.Number)));
        }

        if (V is { } single) { A = single - 1; B = single; }
        if (A is { } a && B is { } b)
        {
            if (a > b) (a, b) = (b, a);
            var q = db.ConfigAudit.AsNoTracking().Where(e => e.ConfigVersion > a && e.ConfigVersion <= b);
            var entries = await q.OrderBy(e => e.Id).ToListAsync();
            entries = entries.Where(e => Visible(e.ObjectKey)).ToList();
            Accepted = entries.Where(e => e.ObjectType == "Publication" && e.After != null)
                .SelectMany(e => System.Text.Json.JsonSerializer.Deserialize<List<string>>(e.After!) ?? []).ToList();
            CompareEntries = entries.Where(e => e.ObjectType != "Publication").ToList();
            Compare = DesignDiff.FromAudit(CompareEntries);
            CompareTitle = V != null ? $"Version {b}" : $"Version {a} → version {b}";
            A = a; B = b;
        }
        return Page();
    }

    /// <summary>Only show configuration audit entries for applications the user can design (and the filtered app, if any).</summary>
    private bool Visible(string objectKey)
    {
        var app = objectKey.Split('/', 2)[0];
        if (!string.IsNullOrEmpty(App)) return app.Equals(App, StringComparison.OrdinalIgnoreCase);
        return Store.Me.IsAdmin || Designable.Contains(app);
    }

    public async Task<IActionResult> OnPostRestoreAsync()
    {
        if (string.IsNullOrEmpty(App)) return BadRequest();
        if (await OpenAsync() is { } deny) return deny;
        var (ok, message) = await publisher.RestoreAsync(App, Version);
        Toast(message, ok ? "ok" : "bad");
        return Redirect(ok ? $"/designer/apps/{App}/publish" : $"/designer/versions?app={App}");
    }
}
