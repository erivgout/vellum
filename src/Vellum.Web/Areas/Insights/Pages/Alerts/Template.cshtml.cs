using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages.Alerts;

/// <summary>An alert template: settings, its reports, "Test report" (RPT-605), enable/disable and delete.</summary>
public class TemplateModel(AlertStore store, AlertEngine engine, VellumDbContext db) : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }

    public InsightsAlertTemplate T { get; private set; } = null!;
    public List<(InsightsAlertReport Report, InsightsAlertTemplate Template)> Reports { get; private set; } = [];
    public List<string> Match { get; private set; } = [];
    public List<string> Extra { get; private set; } = [];
    public AlertRecipients To { get; private set; } = new();
    public AlertRecipients Cc { get; private set; } = new();
    /// <summary>RPT-603: the create-record action (when the alert creates records).</summary>
    public AlertCreateConfig Create { get; private set; } = new();
    public bool Creates => T.Action == "create";

    public async Task<IActionResult> OnGetAsync()
    {
        var t = await store.GetAsync(Id);
        if (t == null) return NotFound();
        T = t;
        Create = AlertCreateCatalog.Normalize(AlertCreateConfig.Parse(t.CreateJson));
        Match = JsonSerializer.Deserialize<List<string>>(t.MatchColumnsJson) ?? [];
        // Number and title always lead the report, so they aren't listed again (the engine skips them too).
        Extra = (JsonSerializer.Deserialize<List<string>>(t.ReportColumnsJson) ?? []).Where(k => k is not ("number" or "title")).ToList();
        To = AlertRecipients.Parse(t.RecipientsJson);
        Cc = AlertRecipients.Parse(t.CcJson);
        Reports = await store.VisibleReportsAsync(t.Id);
        return Page();
    }

    /// <summary>RPT-605: run now, even when disabled. Nothing is sent or created; the report shows what would happen.</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        if (await store.GetAsync(Id) == null) return NotFound();
        var r = await engine.RunAsync(Id, manual: true, test: true);
        Toast($"Test report {r.Number} created", r.ActionStatus == "Failed" ? "bad" : "ok");
        return Redirect($"/insights/alerts/reports/{r.Id}");
    }

    public async Task<IActionResult> OnPostToggleAsync()
    {
        var t = await store.GetAsync(Id, tracking: true);
        if (t == null) return NotFound();
        t.Enabled = !t.Enabled;
        t.ModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Toast(t.Enabled ? "Alert enabled" : "Alert disabled", "info");
        return Redirect($"/insights/alerts/{Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var t = await store.GetAsync(Id, tracking: true);
        if (t == null) return NotFound();
        // Reports are the record of what was found and sent, so a template that produced any is disabled, never deleted.
        if (await db.Set<InsightsAlertReport>().AnyAsync(r => r.TemplateId == Id))
        {
            Toast("This alert has reports, so it can't be deleted. Disable it instead.", "bad");
            return Redirect($"/insights/alerts/{Id}");
        }
        db.Set<InsightsAlertTemplate>().Remove(t);
        await db.SaveChangesAsync();
        Toast($"Deleted alert {t.Number}", "info");
        return Redirect("/insights/alerts");
    }

    public bool HasReports => Reports.Count > 0 || db.Set<InsightsAlertReport>().Any(r => r.TemplateId == Id);
}
