using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages.Charts;

/// <summary>
/// Chart builder (RPT §7.1–7.2): application → view → chart type → category → value → optional Compare To, date grouping,
/// relative date range, location filter, control limits and gauge ranges, with a live, security-trimmed preview.
/// </summary>
public class EditModel(ChartStore store, ChartEngine engine, BuilderCatalog catalog, ConfigCache cfg, AccessService access, VellumDbContext db, CurrentUser me)
    : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public ChartDefinition Def { get; set; } = new();
    [BindProperty] public string Title { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public bool IsTemplate { get; set; }
    [BindProperty] public int? TemplateLocationId { get; set; }
    [BindProperty] public bool Disabled { get; set; }

    public InsightsChart? Existing { get; private set; }
    public List<ChartIssue> Issues { get; private set; } = [];
    public ChartEngine.Evaluation? Preview { get; private set; }
    public BuilderCatalog Catalog => catalog;
    public List<Location> Locations { get; private set; } = [];
    public bool CanManageTemplates => store.CanManageTemplates;
    public bool Saving { get; private set; }

    private async Task<bool> LoadExistingAsync()
    {
        if (Id is not { } id) return true;
        Existing = await store.GetAsync(id);
        return Existing != null && (Existing.OwnerId == me.Id || access.IsAdmin);
    }

    public async Task<IActionResult> OnGetAsync(string? app, string? view)
    {
        await store.EnsureAsync();
        if (!await LoadExistingAsync()) return NotFound();
        if (Existing != null)
        {
            Def = ChartDefinition.FromJson(Existing.DefinitionJson);
            Title = Existing.Title;
            Description = Existing.Description;
            IsTemplate = Existing.IsTemplate;
            TemplateLocationId = Existing.LocationId;
            Disabled = Existing.Disabled;
        }
        else
        {
            var apps = catalog.Apps();
            var a = apps.FirstOrDefault(x => x.Key == app) ?? apps.FirstOrDefault();
            if (a != null)
            {
                var def = cfg.Current.AppsByKey[a.Key].Views.FirstOrDefault(v => v.IsDefault && !v.AssignedToMe && !v.Hidden && a.Views.Any(x => x.Key == v.Key));
                var open = a.Views.FirstOrDefault(v => v.Key == "open");
                Def = new ChartDefinition { App = a.Key, View = a.Views.FirstOrDefault(v => v.Key == view)?.Key ?? open?.Key ?? def?.Key ?? a.Views[0].Key, Type = "column", Category = "phase" };
                Title = "";
            }
        }
        await LoadLookupsAsync();
        Preview = await EvaluateAsync();
        return Page();
    }

    /// <summary>Live preview for the builder script (returns the preview fragment only).</summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        Normalize();
        Preview = await EvaluateAsync();
        return Partial("_BuilderPreview", this);
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadExistingAsync()) return NotFound();
        Normalize();
        Saving = true;
        var tracked = Existing == null ? null : await store.GetAsync(Existing.Id, tracking: true);
        var (chart, issues) = await store.SaveAsync(tracked, Title, Description, Def, Existing?.IsTemplate ?? IsTemplate, IsTemplate ? TemplateLocationId : null, Disabled);
        if (chart == null)
        {
            Issues = issues;
            await LoadLookupsAsync();
            Preview = await EvaluateAsync();
            return Page();
        }
        Toast(Existing == null ? $"Saved {chart.Number}" : "Chart saved");
        return Redirect($"/insights/charts/{chart.Id}");
    }

    private async Task LoadLookupsAsync() => Locations = await db.Locations.AsNoTracking().Where(l => !l.Disabled).OrderBy(l => l.Path).ToListAsync();

    private Task<ChartEngine.Evaluation> EvaluateAsync()
    {
        var token = Def.ToToken();
        return engine.EvaluateAsync(Def, "preview", string.IsNullOrWhiteSpace(Title) ? "Preview" : Title,
            (c, s) => $"/insights/records?def={token}&c={Uri.EscapeDataString(c)}" + (s != null ? $"&s={Uri.EscapeDataString(s)}" : ""), 700, 300);
    }

    /// <summary>Tidy what the form posts: blank selects become null, and options that don't apply are dropped.</summary>
    private void Normalize()
    {
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        Def.Category = N(Def.Category);
        Def.Compare = N(Def.Compare);
        Def.ValueField = N(Def.ValueField);
        Def.DateField = N(Def.DateField);
        Def.YTitle = N(Def.YTitle);
        Def.XTitle = N(Def.XTitle);
        Def.LclTitle = N(Def.LclTitle); Def.CenterTitle = N(Def.CenterTitle); Def.UclTitle = N(Def.UclTitle);
        Def.Range = N(Def.Range) ?? "none";
        Def.Sort = N(Def.Sort) ?? "none";
        Def.Limits = N(Def.Limits) ?? "none";
        Def.LocationScope = N(Def.LocationScope) ?? "all";
        var cols = catalog.Columns(Def.App, Def.View);
        bool IsDate(string? k) => k != null && cols.FirstOrDefault(c => c.Key == k)?.IsDate == true;
        Def.CategoryDates = IsDate(Def.Category) ? N(Def.CategoryDates) ?? "month" : null;
        Def.CompareDates = IsDate(Def.Compare) ? N(Def.CompareDates) ?? "month" : null;
        if (Def.Aggregate == "count") Def.ValueField = null;
        if (Def.Range != "last_n_days") Def.RangeDays = null;
        if (Def.Range != "custom") { Def.From = null; Def.To = null; }
        if (Def.Range == "none") Def.DateField = null; else Def.DateField ??= "created";
        if (Def.LocationScope == "all") { Def.LocationId = null; Def.LocationMine = false; }
        else Def.LocationMine = Def.LocationId == null;
        if (Def.Type == "gauge") { Def.Category = null; Def.CategoryDates = null; }
        else { Def.Ranges = []; Def.GaugeMin = null; Def.GaugeMax = null; }
        Def.Ranges = Def.Ranges.Where(r => r.Min != null || r.Max != null).ToList();
        if (Def.Limits != "manual") { Def.Lcl = null; Def.Center = null; Def.Ucl = null; }
        if (Def.Limits != "calculated") { Def.LimitsFrom = null; Def.LimitsTo = null; }
        if (Def.Limits == "none") { Def.LclTitle = null; Def.CenterTitle = null; Def.UclTitle = null; }
    }

    public string FieldError(string field) => string.Join(" ", Issues.Concat(Preview?.Issues ?? []).Where(i => i.Field == field).Select(i => i.Message).Distinct());
}
