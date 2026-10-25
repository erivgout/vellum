using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Data;
using Vellum.Engine.Audit;
using Vellum.Modules.Insights;
using Vellum.Modules.Insights.Charts;

namespace Vellum.Areas.Insights.Pages;

/// <summary>Every Insights page requires a signed-in user (areas aren't covered by the root folder convention).</summary>
[Authorize]
public abstract class InsightsPageModel : PageModel
{
    protected async Task EnsureSchemaAsync() => await InsightsSchema.EnsureAsync(HttpContext.RequestServices.GetRequiredService<VellumDbContext>());

    /// <summary>RPT-403: export a chart's report table as CSV, audited as a global event.</summary>
    protected async Task<IActionResult> CsvAsync(ChartData chart, string context)
    {
        var audit = HttpContext.RequestServices.GetRequiredService<AuditService>();
        var db = HttpContext.RequestServices.GetRequiredService<VellumDbContext>();
        var csv = ChartRenderer.Csv(chart);
        var rows = Math.Max(0, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        audit.Global("Chart exported", $"{context} › {chart.Title}: report table with {rows} row(s) exported to CSV");
        await db.SaveChangesAsync();
        var name = new string(chart.Title.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (name.Contains("--")) name = name.Replace("--", "-");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", $"{name}-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    protected void Toast(string text, string kind = "ok")
    {
        TempData["Toast"] = text;
        TempData["ToastKind"] = kind;
    }
}
