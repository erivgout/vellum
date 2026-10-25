using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages;

/// <summary>Generic per-application dashboard (RPT-701), computed from the app's configuration.</summary>
public class AppDashboardModel(DashboardService dashboards, ConfigCache cfg, AccessService access) : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Period { get; set; } = "12m";
    [BindProperty(SupportsGet = true)] public bool Refresh { get; set; }

    public AppDefinition App { get; private set; } = null!;
    public AppDashboard Data { get; private set; } = null!;
    public DateTime At { get; private set; }

    private bool Resolve()
    {
        if (!cfg.Current.AppsByKey.TryGetValue(Key, out var app) || app.IsSystem || !access.CanOpenApp(app.Id)) return false;
        if (InsightsQueries.MainForms(app).Count == 0) return false;
        App = app;
        if (DashboardService.Periods.All(p => p.Key != Period)) Period = "12m";
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Resolve()) return NotFound();
        (Data, At) = await dashboards.AppAsync(App, Period, Refresh);
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync(string chart)
    {
        if (!Resolve()) return NotFound();
        (Data, At) = await dashboards.AppAsync(App, Period, false);
        var c = Data.Chart(chart);
        return c == null ? NotFound() : await CsvAsync(c, $"{App.Name} dashboard");
    }

    public string Csv(string chart) => $"/insights/apps/{App.Key}?handler=Csv&chart={chart}&period={Period}";
    public string Link(string period) => $"/insights/apps/{App.Key}?period={period}";
}
