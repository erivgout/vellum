using Microsoft.AspNetCore.Mvc;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages;

/// <summary>Insights home: KPI dashboard across every application the user can read (RPT-701, RPT-704).</summary>
public class IndexModel(DashboardService dashboards) : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public string? Cycle { get; set; }
    [BindProperty(SupportsGet = true)] public bool Refresh { get; set; }

    public HomeDashboard Data { get; private set; } = null!;
    public DateTime At { get; private set; }

    public async Task OnGetAsync() => (Data, At) = await dashboards.HomeAsync(Cycle, Refresh);

    public async Task<IActionResult> OnGetCsvAsync(string chart)
    {
        (Data, At) = await dashboards.HomeAsync(Cycle, false);
        var c = Data.Charts.FirstOrDefault(x => x.Id == chart);
        return c == null ? NotFound() : await CsvAsync(c, "Insights dashboard");
    }

    public string Csv(string chart) => $"/insights?handler=Csv&chart={chart}" + (Cycle != null ? $"&cycle={Uri.EscapeDataString(Cycle)}" : "");

    public string RefreshHref => "/insights?refresh=true" + (Cycle != null ? $"&cycle={Uri.EscapeDataString(Cycle)}" : "");
}
