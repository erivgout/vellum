using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

/// <summary>Platform Engine (DSN-1205, ADM-1202): every configuration object, searchable and filterable; orphans and broken references.</summary>
public class EngineModel(DesignerStore store, VellumDbContext db) : DesignerPageModel(store)
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    [BindProperty(SupportsGet = true)] public int? Days { get; set; }

    public List<CatalogItem> Items { get; private set; } = [];
    public int Total { get; private set; }
    public Dictionary<string, int> TypeCounts { get; private set; } = [];
    public Dictionary<string, DateTime> LastChanged { get; private set; } = [];
    public List<AppDefinition> Apps { get; private set; } = [];
    public List<KeywordList> UnusedLists { get; private set; } = [];
    public List<UsageRef> Broken { get; private set; } = [];
    public List<(FormDefinition Form, int Fields)> Forms { get; private set; } = [];
    public const int Limit = 400;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Store.IsAnyDesigner) return Forbid();
        await Store.EnsureSchemaAsync();
        var cfg = Store.Cfg;
        bool Visible(AppDefinition a) => Store.Me.IsAdmin || (!a.IsSystem && Store.CanDesign(a));
        Apps = cfg.Apps.Where(Visible).ToList();
        Forms = Apps.SelectMany(a => a.Forms).Select(f => (f, f.Fields.Count)).ToList();

        if (Tab == "orphans")
        {
            UnusedLists = EngineCatalog.UnusedLists(cfg);
            Broken = EngineCatalog.BrokenReferences(cfg, Visible);
            return Page();
        }

        var all = EngineCatalog.Items(cfg, a => Visible(a) && (string.IsNullOrEmpty(App) || a.Key == App)).ToList();
        if (!string.IsNullOrEmpty(App)) all = all.Where(i => i.AppKey == App).ToList();
        LastChanged = await db.ConfigAudit.AsNoTracking().GroupBy(e => e.ObjectKey).Select(g => new { g.Key, At = g.Max(e => e.At) })
            .ToDictionaryAsync(x => x.Key, x => x.At);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            all = all.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                 (i.Parent?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) || (i.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }
        if (Days is > 0)
        {
            var since = DateTime.UtcNow.AddDays(-Days.Value);
            all = all.Where(i => LastChanged.TryGetValue(i.AuditKey, out var at) && at >= since).ToList();
        }
        TypeCounts = all.GroupBy(i => i.Type).ToDictionary(g => g.Key, g => g.Count());
        if (!string.IsNullOrEmpty(Type)) all = all.Where(i => i.Type == Type).ToList();
        Total = all.Count;
        Items = all.Take(Limit).ToList();
        return Page();
    }

    public string Link(string? type = null, string? tab = null, bool clearType = false)
    {
        var p = new List<string>();
        if (!string.IsNullOrEmpty(App)) p.Add("app=" + Uri.EscapeDataString(App));
        if (!string.IsNullOrEmpty(Q)) p.Add("q=" + Uri.EscapeDataString(Q));
        var t = clearType ? null : type ?? Type;
        if (!string.IsNullOrEmpty(t)) p.Add("type=" + Uri.EscapeDataString(t));
        if (Days is > 0) p.Add("days=" + Days);
        if (tab != null) p.Add("tab=" + tab);
        return "/designer/engine" + (p.Count > 0 ? "?" + string.Join("&", p) : "");
    }
}
