using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Engine.Security;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages.Alerts;

/// <summary>
/// An alert report (RPT-604): origin, action outcome and the matched records, match columns first. Records are
/// re-trimmed for the viewer (INV-081): anything they can't open is left out.
/// </summary>
public class ReportModel(AlertStore store, AccessService access) : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    public InsightsAlertReport R { get; private set; } = null!;
    public InsightsAlertTemplate T { get; private set; } = null!;
    public AlertResult Result { get; private set; } = new();
    public JsonElement Origin { get; private set; }
    public HashSet<Guid> Visible { get; private set; } = [];
    public int Hidden { get; private set; }

    private async Task<bool> LoadAsync()
    {
        var x = await store.GetReportAsync(Id);
        if (x == null) return false;
        (R, T) = x.Value;
        Result = JsonSerializer.Deserialize<AlertResult>(R.ResultJson, AlertRecipients.JsonOpts) ?? new AlertResult();
        Origin = JsonSerializer.Deserialize<JsonElement>(R.OriginJson);
        var ids = Result.Groups.SelectMany(g => g.Records).Select(r => r.Id).Distinct().ToList();
        Visible = (await access.Readable().Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync()).ToHashSet();
        Hidden = ids.Count - Visible.Count;
        return true;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    /// <summary>RPT-604: machine-readable data, trimmed to the records the viewer can open.</summary>
    public async Task<IActionResult> OnGetJsonAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var trimmed = new AlertResult
        {
            Columns = Result.Columns, Evaluated = Result.Evaluated, Truncated = Result.Truncated,
            Groups = Result.Groups.Select(g => new AlertGroup { Values = g.Values, Count = g.Count, Records = g.Records.Where(r => Visible.Contains(r.Id)).ToList() }).ToList(),
        };
        var json = JsonSerializer.Serialize(new { report = R.Number, generatedAt = R.GeneratedAt, test = R.IsTest, origin = Origin, action = new { status = R.ActionStatus, detail = R.ActionDetail }, result = trimmed },
            new JsonSerializerOptions(AlertRecipients.JsonOpts) { WriteIndented = true });
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"{R.Number}.json");
    }
}
