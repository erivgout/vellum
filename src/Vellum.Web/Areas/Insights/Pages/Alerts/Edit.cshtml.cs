using Microsoft.AspNetCore.Mvc;
using Vellum.Engine.Security;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages.Alerts;

/// <summary>Create or edit a threshold alert template (RPT-601/602).</summary>
public class EditModel(AlertStore store, BuilderCatalog catalog, CurrentUser me) : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public AlertInput Input { get; set; } = new();

    public InsightsAlertTemplate? Existing { get; private set; }
    public List<ChartIssue> Issues { get; private set; } = [];
    public BuilderCatalog Catalog => catalog;

    public async Task<IActionResult> OnGetAsync(string? app)
    {
        await store.EnsureAsync();
        if (Id is { } id)
        {
            Existing = await store.GetAsync(id);
            if (Existing == null) return NotFound();
            Input = AlertInput.From(Existing);
        }
        else
        {
            var apps = catalog.Apps();
            var a = apps.FirstOrDefault(x => x.Key == app) ?? apps.FirstOrDefault();
            if (a != null)
            {
                Input.App = a.Key;
                Input.View = (a.Views.FirstOrDefault(v => v.Key == "open") ?? a.Views[0]).Key;
            }
            Input.Users = [me.Id];
            Input.ReportColumns = ["title", "phase", "created"];
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        InsightsAlertTemplate? tracked = null;
        if (Id is { } id)
        {
            tracked = await store.GetAsync(id, tracking: true);
            if (tracked == null) return NotFound();
            Existing = tracked;
        }
        var (t, issues) = await store.SaveAsync(tracked, Input);
        if (t == null)
        {
            Issues = issues;
            return Page();
        }
        Toast(Id == null ? $"Created alert {t.Number}" : "Alert saved");
        return Redirect($"/insights/alerts/{t.Id}");
    }

    public string FieldError(string field) => string.Join(" ", Issues.Where(i => i.Field == field).Select(i => i.Message).Distinct());
}
