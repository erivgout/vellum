using Vellum.Modules.Insights.Charts;

namespace Vellum.Modules.Insights;

/// <summary>A chart in a dashboard card (title, optional subtitle, CSV export, report table).</summary>
public sealed record ChartCard(ChartData Chart, string? Sub = null, string? CsvHref = null, TableMode Table = TableMode.Collapsed,
    string? Icon = null, int? Width = null, string? MoreHref = null, string? MoreLabel = null);

/// <summary>RPT-704: "Data refreshed at …" with a forced-refresh link.</summary>
public sealed record Refreshed(DateTime At, string RefreshHref);
