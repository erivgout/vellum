using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Areas.Audits.Pages;

/// <summary>
/// CHG-061 timeliness reports: aging of open changes, created vs completed by month, time to complete, and timeliness by phase
/// (days = due date − date completed: Early &gt; 0, On time = 0, Late &lt; 0), grouped by change type, department or reason code.
/// </summary>
[Authorize]
public class ChangeTimelinessModel(VellumDbContext db, AccessService access, ConfigCache cfg, UserDirectory dir) : PageModel
{
    [BindProperty(SupportsGet = true)] public string By { get; set; } = "type";

    public sealed record Item(Record Record, string Group, int AgeDays, DateTime? CompletedAt);
    public sealed record PhaseTimeliness(string Phase, int Early, int OnTime, int Late)
    {
        public int Total => Early + OnTime + Late;
    }

    public static readonly (string Label, int From, int To)[] Buckets = [("0–30 days", 0, 30), ("31–60 days", 31, 60), ("61–90 days", 61, 90), ("Over 90 days", 91, int.MaxValue)];

    public List<Item> Open { get; private set; } = [];
    public List<Item> Done { get; private set; } = [];
    public List<(string Group, int[] Counts)> Aging { get; private set; } = [];
    public List<(DateOnly Month, int Created, int Completed)> Monthly { get; private set; } = [];
    public List<(string Group, int Count, double Avg, int Min, int Max)> TimeToComplete { get; private set; } = [];
    public List<PhaseTimeliness> ByPhase { get; private set; } = [];
    public string GroupLabel => By switch { "department" => "Department", "reason" => "Reason", _ => "Change type" };

    public async Task<IActionResult> OnGetAsync()
    {
        var c = cfg.Current;
        if (!c.AppsByKey.TryGetValue("change", out var app) || !access.CanOpenApp(app.Id) || !c.FormsByKey.TryGetValue("change", out var form)) return NotFound();
        if (By is not ("type" or "department" or "reason")) By = "type";

        var changes = await access.Readable().AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseType != PhaseType.Voided).ToListAsync();
        var ids = changes.Select(r => r.Id).ToList();
        string[] keys = ["change_type", "owner", "reason_code"];
        var vals = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && keys.Contains(v.FieldKey)).ToListAsync();
        var wf = form.Workflows.First();
        var completedPhases = wf.Phases.Where(p => p.Type == PhaseType.Completed).Select(p => p.Id).ToList();
        var entries = await db.PhaseEntries.AsNoTracking().Where(e => ids.Contains(e.RecordId)).ToListAsync();

        string GroupOf(Guid id) => By switch
        {
            "department" => dir.Get(vals.FirstOrDefault(v => v.RecordId == id && v.FieldKey == "owner")?.RefId)?.Department ?? "(no department)",
            "reason" => c.Keyword(vals.FirstOrDefault(v => v.RecordId == id && v.FieldKey == "reason_code")?.RefId)?.Label ?? "(no reason)",
            _ => vals.FirstOrDefault(v => v.RecordId == id && v.FieldKey == "change_type")?.Text ?? "(no type)",
        };
        var now = DateTime.UtcNow;
        foreach (var r in changes)
        {
            var done = entries.Where(e => e.RecordId == r.Id && completedPhases.Contains(e.PhaseId)).Select(e => (DateTime?)e.EnteredAt).Max();
            var item = new Item(r, GroupOf(r.Id), (int)((done ?? now) - r.CreatedAt).TotalDays, done);
            if (r.PhaseType == PhaseType.Completed) Done.Add(item);
            else if (r.PhaseType == PhaseType.Normal) Open.Add(item);
        }

        Aging = Open.GroupBy(i => i.Group).OrderBy(g => g.Key)
            .Select(g => (g.Key, Buckets.Select(b => g.Count(i => i.AgeDays >= b.From && i.AgeDays <= b.To)).ToArray())).ToList();

        var first = new DateOnly(BusinessCalendar.Today.Year, BusinessCalendar.Today.Month, 1).AddMonths(-5);
        Monthly = Enumerable.Range(0, 6).Select(i => first.AddMonths(i)).Select(m => (m,
            changes.Count(r => r.CreatedAt.Year == m.Year && r.CreatedAt.Month == m.Month),
            Done.Count(d => d.CompletedAt is { } at && at.Year == m.Year && at.Month == m.Month))).ToList();

        TimeToComplete = Done.GroupBy(i => i.Group).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count(), g.Average(i => (double)i.AgeDays), g.Min(i => i.AgeDays), g.Max(i => i.AgeDays))).ToList();

        // Timeliness by phase: each exited phase entry with a due date (days = due − date completed).
        ByPhase = wf.Phases.Where(p => p.Type == PhaseType.Normal).OrderBy(p => p.Order).Select(p =>
        {
            var list = entries.Where(e => e.PhaseId == p.Id && e.ExitedAt != null && e.DueDate != null && e.ExitAction is not ("void" or "back"))
                .Select(e => e.DueDate!.Value.DayNumber - DateOnly.FromDateTime(e.ExitedAt!.Value).DayNumber).ToList();
            return new PhaseTimeliness(p.Name, list.Count(d => d > 0), list.Count(d => d == 0), list.Count(d => d < 0));
        }).ToList();
        return Page();
    }

    public string Link(string by) => "/change/timeliness?by=" + by;
}
