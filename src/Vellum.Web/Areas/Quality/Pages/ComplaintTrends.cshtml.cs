using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Areas.Quality.Pages;

public sealed record TrendBar(string Label, int Count, string? Href = null);
public sealed record TrendSignal(string Kind, string Label, int Count, string? Href);

/// <summary>CMP-010: complaint trending by product, failure mode, lot and site, with simple signal detection. Security-trimmed (INV-081).</summary>
[Authorize]
public class ComplaintTrendsModel(AccessService access, ConfigCache cfg, VellumDbContext db) : PageModel
{
    public static readonly int[] Ranges = [3, 6, 12, 24];

    [BindProperty(SupportsGet = true)] public int Months { get; set; } = 12;

    public int Total { get; private set; }
    public int Open { get; private set; }
    public int Reportable { get; private set; }
    public int PatientInvolved { get; private set; }
    public List<TrendBar> Monthly { get; } = [];
    public List<TrendBar> ByProduct { get; private set; } = [];
    public List<TrendBar> ByFailureMode { get; private set; } = [];
    public List<TrendBar> BySite { get; private set; } = [];
    public List<TrendBar> ByLot { get; private set; } = [];
    public List<TrendSignal> Signals { get; } = [];
    public const int SignalWindowDays = 90;
    public const int SignalThreshold = 3;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!cfg.Current.AppsByKey.TryGetValue("complaints", out var app) || !access.CanOpenApp(app.Id)) return NotFound();
        if (!cfg.Current.FormsByKey.TryGetValue("complaint", out var form)) return NotFound();
        if (!Ranges.Contains(Months)) Months = 12;

        var today = BusinessCalendar.Today;
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(Months - 1));
        var since = firstMonth.ToDateTime(TimeOnly.MinValue);
        var recs = await access.Readable().AsNoTracking()
            .Where(r => r.FormId == form.Id && r.PhaseType != PhaseType.Voided && !r.IsArchived)
            .Select(r => new { r.Id, r.CreatedAt, r.PhaseType }).ToListAsync();
        var ids = recs.Select(r => r.Id).ToList();
        string[] keys = ["product", "failure_mode", "lot_number", "site_reported", "mdr_decision", "patient_involved", "awareness_date"];
        var vals = ids.Count == 0 ? [] : await db.RecordValues.AsNoTracking()
            .Where(v => ids.Contains(v.RecordId) && v.RowId == null && keys.Contains(v.FieldKey)).ToListAsync();
        var byRecord = vals.GroupBy(v => v.RecordId).ToDictionary(g => g.Key, g => g.ToList());
        RecordValue? Val(Guid id, string key) => byRecord.TryGetValue(id, out var l) ? l.FirstOrDefault(v => v.FieldKey == key) : null;
        DateOnly When(Guid id, DateTime created) => Val(id, "awareness_date")?.Date is { } d ? DateOnly.FromDateTime(d) : DateOnly.FromDateTime(created);

        var inRange = recs.Where(r => When(r.Id, r.CreatedAt) >= firstMonth).ToList();
        Total = inRange.Count;
        Open = inRange.Count(r => r.PhaseType == PhaseType.Normal);
        Reportable = inRange.Count(r => cfg.Current.Keyword(Val(r.Id, "mdr_decision")?.RefId)?.Key is "reportable_30" or "reportable_5");
        PatientInvolved = inRange.Count(r => Val(r.Id, "patient_involved")?.RefId == 1);

        for (var m = firstMonth; m <= today; m = m.AddMonths(1))
        {
            var next = m.AddMonths(1);
            Monthly.Add(new TrendBar(m.ToString("MMM yy"), inRange.Count(r => When(r.Id, r.CreatedAt) >= m && When(r.Id, r.CreatedAt) < next)));
        }

        string Link(string field, string value) => $"/apps/complaints?view=all&f.{field}={Uri.EscapeDataString(value)}";
        List<TrendBar> Text(string field, int take) => inRange.Select(r => Val(r.Id, field)?.Text).OfType<string>()
            .GroupBy(t => t.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => new TrendBar(g.Key, g.Count(), Link(field, g.Key)))
            .OrderByDescending(b => b.Count).ThenBy(b => b.Label).Take(take).ToList();
        List<TrendBar> Keyword(string field) => inRange.Select(r => cfg.Current.Keyword(Val(r.Id, field)?.RefId)).OfType<Keyword>()
            .GroupBy(k => k.Id).Select(g => new TrendBar(g.First().Label, g.Count(), Link(field, g.First().Key)))
            .OrderByDescending(b => b.Count).ThenBy(b => b.Label).ToList();

        ByProduct = Text("product", 8);
        ByLot = Text("lot_number", 8);
        ByFailureMode = Keyword("failure_mode");
        BySite = Keyword("site_reported");

        // Signal detection: the same lot or failure mode SignalThreshold+ times within the last SignalWindowDays.
        var window = recs.Where(r => When(r.Id, r.CreatedAt) >= today.AddDays(-SignalWindowDays)).ToList();
        foreach (var g in window.Select(r => Val(r.Id, "lot_number")?.Text).OfType<string>().GroupBy(t => t.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= SignalThreshold))
            Signals.Add(new TrendSignal("Lot", g.Key, g.Count(), Link("lot_number", g.Key)));
        foreach (var g in window.Select(r => cfg.Current.Keyword(Val(r.Id, "failure_mode")?.RefId)).OfType<Keyword>().GroupBy(k => k.Id).Where(g => g.Count() >= SignalThreshold))
            Signals.Add(new TrendSignal("Failure mode", g.First().Label, g.Count(), Link("failure_mode", g.First().Key)));
        return Page();
    }
}
