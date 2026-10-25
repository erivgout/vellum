using Microsoft.AspNetCore.Mvc;
using Vellum.Engine.Security;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages.Charts;

/// <summary>A chart document or template: chart, report table, drill-through, CSV export, pin, duplicate (RPT-403..405).</summary>
public class ViewModel(ChartStore store, CurrentUser me, AccessService access) : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty(SupportsGet = true)] public bool Refresh { get; set; }

    public InsightsChart Chart { get; private set; } = null!;
    public ChartEngine.Evaluation Result { get; private set; } = null!;
    public DateTime At { get; private set; }
    public bool IsOwner => Chart.OwnerId == me.Id;
    public bool CanEdit => IsOwner || access.IsAdmin;

    private async Task<bool> LoadAsync()
    {
        var c = await store.GetAsync(Id);
        if (c == null) return false;
        Chart = c;
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        (Result, At) = await store.EvaluateAsync(Chart, Refresh);
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var (r, _) = await store.EvaluateAsync(Chart, false);
        return r.Chart == null ? NotFound() : await CsvAsync(r.Chart, $"Chart {Chart.Number}");
    }

    public async Task<IActionResult> OnPostPinAsync(bool pin, string? mode)
    {
        if (!await LoadAsync()) return NotFound();
        if (await store.PinAsync(Id, pin, mode)) Toast(pin ? "Pinned to your homepage" : "Removed from your homepage", "info");
        return Redirect($"/insights/charts/{Id}");
    }

    public async Task<IActionResult> OnPostDuplicateAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var c = Chart.IsTemplate ? await store.FromTemplateAsync(Chart) : await store.DuplicateAsync(Chart);
        Toast($"Created {c.Number}");
        return Redirect($"/insights/charts/{c.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync()) return NotFound();
        if (!await store.DeleteAsync(Id)) return Forbid();
        Toast($"Deleted {Chart.Number}", "info");
        return Redirect("/insights/charts");
    }
}
