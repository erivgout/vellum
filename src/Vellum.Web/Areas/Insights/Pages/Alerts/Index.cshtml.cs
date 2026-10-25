using Microsoft.AspNetCore.Mvc;
using Vellum.Engine.Security;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages.Alerts;

/// <summary>RPT-607: alert templates by application, creator and frequency; alert reports by application, creator and date.</summary>
public class IndexModel(AlertStore store, AccessService access) : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "templates";
    [BindProperty(SupportsGet = true)] public string? App { get; set; }
    [BindProperty(SupportsGet = true)] public int? Owner { get; set; }
    [BindProperty(SupportsGet = true)] public int? Every { get; set; }
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;

    public List<InsightsAlertTemplate> Templates { get; private set; } = [];
    public List<InsightsAlertTemplate> AllTemplates { get; private set; } = [];
    public List<(InsightsAlertReport Report, InsightsAlertTemplate Template)> Reports { get; private set; } = [];
    public bool IsAdmin => access.IsAdmin;

    public async Task OnGetAsync()
    {
        Tab = Tab == "reports" ? "reports" : "templates";
        AllTemplates = await store.VisibleTemplatesAsync();
        Templates = AllTemplates.Where(t => (App == null || t.AppKey == App) && (Owner == null || t.OwnerId == Owner) && (Every == null || t.FrequencyDays == Every)).ToList();
        if (Tab == "reports")
        {
            var since = Days > 0 ? DateTime.UtcNow.AddDays(-Days) : (DateTime?)null;
            Reports = (await store.VisibleReportsAsync(since: since))
                .Where(x => (App == null || x.Template.AppKey == App) && (Owner == null || x.Template.OwnerId == Owner)).ToList();
        }
    }

    public string Link(string? tab = null, string? app = "\u0000", int? owner = -1, int? every = -1, int? days = -1)
    {
        var p = new List<string> { "tab=" + (tab ?? Tab) };
        var a = app == "\u0000" ? App : app;
        var o = owner == -1 ? Owner : owner;
        var e = every == -1 ? Every : every;
        var d = days == -1 ? Days : days;
        if (a != null) p.Add("app=" + Uri.EscapeDataString(a));
        if (o != null) p.Add("owner=" + o);
        if (e != null) p.Add("every=" + e);
        if ((tab ?? Tab) == "reports") p.Add("days=" + d);
        return "/insights/alerts?" + string.Join("&", p);
    }
}
