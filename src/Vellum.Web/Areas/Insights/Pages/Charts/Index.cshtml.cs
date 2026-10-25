using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages.Charts;

/// <summary>"My charts" and shared chart templates (RPT-101, RPT-405).</summary>
public class IndexModel(ChartStore store, VellumDbContext db) : InsightsPageModel
{
    public List<InsightsChart> Mine { get; private set; } = [];
    public List<InsightsChart> Templates { get; private set; } = [];
    public Dictionary<int, string> LocationNames { get; private set; } = [];
    public bool CanManageTemplates => store.CanManageTemplates;

    public async Task OnGetAsync()
    {
        Mine = await store.MineAsync();
        Templates = await store.TemplatesAsync();
        LocationNames = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name);
    }

    public async Task<IActionResult> OnPostPinAsync(int id, bool pin, string? mode)
    {
        if (await store.PinAsync(id, pin, mode)) Toast(pin ? "Pinned to your homepage" : "Removed from your homepage", "info");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        Toast(await store.DeleteAsync(id) ? "Chart deleted" : "You can't delete that chart", "info");
        return RedirectToPage();
    }

    /// <summary>RPT-101: create a chart document from a template.</summary>
    public async Task<IActionResult> OnPostUseAsync(int id)
    {
        var t = await store.GetAsync(id);
        if (t is not { IsTemplate: true }) return NotFound();
        var c = await store.FromTemplateAsync(t);
        Toast($"Created {c.Number} from template {t.Number}");
        return Redirect($"/insights/charts/{c.Id}");
    }
}
