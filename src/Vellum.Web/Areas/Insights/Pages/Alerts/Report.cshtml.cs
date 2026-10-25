using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Engine.Security;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages.Alerts;

/// <summary>
/// An alert report (RPT-604): origin, action outcome, the records the create-record action created or would create
/// (RPT-603/605) and its failures (INV-127, RPT-606), and the matched records, match columns first. Records are
/// re-trimmed for the viewer (INV-081): anything they can't open is left out or shown without a link.
/// </summary>
public class ReportModel(AlertStore store, AccessService access, AlertEngine engine, CurrentUser me) : InsightsPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    public InsightsAlertReport R { get; private set; } = null!;
    public InsightsAlertTemplate T { get; private set; } = null!;
    public AlertResult Result { get; private set; } = new();
    public JsonElement Origin { get; private set; }
    public HashSet<Guid> Visible { get; private set; } = [];
    public int Hidden { get; private set; }
    public List<InsightsAlertCreation> Creations { get; private set; } = [];
    /// <summary>Created records the viewer can open.</summary>
    public HashSet<Guid> VisibleCreated { get; private set; } = [];
    /// <summary>RPT-603: the owner or an administrator can retry a failed create-record action (idempotent).</summary>
    public bool CanRetry => !R.IsTest && T.Action == "create" && R.ActionStatus is "Failed" or "Pending" && R.GroupCount > 0 && (T.OwnerId == me.Id || access.IsAdmin);

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
        Creations = await store.CreationsAsync(R.Id);
        var created = Creations.Where(c => c.RecordId != null).Select(c => c.RecordId!.Value).ToList();
        VisibleCreated = (await access.Readable().Where(r => created.Contains(r.Id)).Select(r => r.Id).ToListAsync()).ToHashSet();
        return true;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public static AlertCreationDetail? Detail(InsightsAlertCreation c) =>
        c.DetailJson == null ? null : JsonSerializer.Deserialize<AlertCreationDetail>(c.DetailJson, AlertRecipients.JsonOpts);

    /// <summary>RPT-603: create the records that are still missing. Items that already have a record are skipped.</summary>
    public async Task<IActionResult> OnPostRetryAsync()
    {
        if (!await LoadAsync()) return NotFound();
        if (!CanRetry) return Forbid();
        try
        {
            var r = await engine.RetryAsync(Id);
            Toast(r.ActionStatus == "Failed" ? "Some records still couldn't be created. See the failures below." : "Retried: the missing records were created.",
                r.ActionStatus == "Failed" ? "bad" : "ok");
        }
        catch (AlertFailure ex) { Toast(ex.Message, "bad"); }
        return Redirect($"/insights/alerts/reports/{Id}");
    }

    /// <summary>RPT-604: machine-readable data, trimmed to the records the viewer can open.</summary>
    public async Task<IActionResult> OnGetJsonAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var trimmed = new AlertResult
        {
            Columns = Result.Columns, Evaluated = Result.Evaluated, Truncated = Result.Truncated,
            Groups = Result.Groups.Select(g => new AlertGroup { Values = g.Values, Count = g.Count, Records = g.Records.Where(r => Visible.Contains(r.Id)).ToList() }).ToList(),
        };
        var created = Creations.Select(c => new
        {
            item = c.ItemLabel, status = c.Status, message = c.Message,
            record = c.RecordId is { } rid && VisibleCreated.Contains(rid) ? new { id = rid, number = c.RecordNumber, title = c.RecordTitle } : null,
        });
        var json = JsonSerializer.Serialize(new { report = R.Number, generatedAt = R.GeneratedAt, test = R.IsTest, origin = Origin, action = new { status = R.ActionStatus, detail = R.ActionDetail, records = created }, result = trimmed },
            new JsonSerializerOptions(AlertRecipients.JsonOpts) { WriteIndented = true });
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"{R.Number}.json");
    }
}
