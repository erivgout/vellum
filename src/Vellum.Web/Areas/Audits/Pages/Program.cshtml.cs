using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Audits;

namespace Vellum.Areas.Audits.Pages;

public sealed record ProgramEntity(string Name, Keyword? Kind, Record? LastAudit, DateOnly? LastDate, Keyword? LastResult, Keyword? Frequency,
    int FrequencyMonths, DateOnly? NextDue, Record? OpenAudit, PhaseDefinition? OpenPhase, DateOnly? PlannedDate, Record? Plan, int OpenCapas,
    string Status, string Tone, int Rank);

/// <summary>
/// AUD-015 (simplified): every auditable entity seen in audits and audit plans, with its last audit and result, the frequency
/// recommendation, the next due date (audit type frequency adjusted by the recommendation) and what's scheduled or planned.
/// </summary>
[Authorize]
public class ProgramModel(VellumDbContext db, AccessService access, ConfigCache cfg) : PageModel
{
    public List<ProgramEntity> Entities { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var c = cfg.Current;
        if (!c.AppsByKey.TryGetValue("audits", out var app) || !access.CanOpenApp(app.Id) || !c.FormsByKey.TryGetValue("audit", out var auditForm)) return NotFound();
        var planForm = c.FormsByKey.GetValueOrDefault("audit_plan");
        var typeForm = c.FormsByKey.GetValueOrDefault("audit_type");

        var audits = await access.Readable().AsNoTracking().Where(r => r.FormId == auditForm.Id && r.PhaseType != PhaseType.Voided).ToListAsync();
        var ids = audits.Select(a => a.Id).ToList();
        string[] keys = ["auditee", "entity_kind", "start_at", "result", "frequency_rec", "audit_type"];
        var vals = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && keys.Contains(v.FieldKey)).ToListAsync();
        RecordValue? V(Guid id, string key) => vals.FirstOrDefault(v => v.RecordId == id && v.FieldKey == key);

        var types = typeForm == null ? [] : await db.RecordValues.AsNoTracking()
            .Where(v => v.FieldKey == "frequency_months" && v.RowId == null && db.Records.Any(r => r.Id == v.RecordId && r.FormId == typeForm.Id))
            .ToDictionaryAsync(v => v.RecordId, v => (int)(v.Number ?? 12));

        var plans = planForm == null ? [] : await access.Readable().AsNoTracking().Where(r => r.FormId == planForm.Id && r.PhaseType == PhaseType.Normal).ToListAsync();
        var planIds = plans.Select(p => p.Id).ToList();
        var planRows = await db.SubformRows.AsNoTracking().Where(r => planIds.Contains(r.RecordId) && r.SectionKey == "planned_audits" && r.CreatedRecordId == null).ToListAsync();
        var rowIds = planRows.Select(r => r.Id).ToList();
        var rowVals = await db.RecordValues.AsNoTracking().Where(v => v.RowId != null && rowIds.Contains(v.RowId.Value)).ToListAsync();

        var capaLinks = await db.Links.AsNoTracking().Where(l => ids.Contains(l.FromId) && l.Kind == LinkKind.Source && l.FieldKey != null && l.FieldKey.StartsWith("finding:")).ToListAsync();
        var capaIds = capaLinks.Select(l => l.ToId).ToList();
        var openCapas = await db.Records.AsNoTracking().Where(r => capaIds.Contains(r.Id) && r.PhaseType == PhaseType.Normal).Select(r => r.Id).ToListAsync();

        var today = BusinessCalendar.Today;
        var names = audits.Select(a => V(a.Id, "auditee")?.Text).Concat(rowVals.Where(v => v.FieldKey == "auditee").Select(v => v.Text))
            .OfType<string>().Select(n => n.Trim()).Where(n => n.Length > 0).DistinctBy(n => n.ToLowerInvariant()).ToList();
        foreach (var name in names)
        {
            bool Is(string? s) => string.Equals(s?.Trim(), name, StringComparison.OrdinalIgnoreCase);
            var mine = audits.Where(a => Is(V(a.Id, "auditee")?.Text)).ToList();
            DateOnly? Start(Record a) => V(a.Id, "start_at")?.Date is { } d ? DateOnly.FromDateTime(d) : null;
            var last = mine.Where(a => a.PhaseType == PhaseType.Completed).OrderByDescending(a => Start(a)).FirstOrDefault();
            var open = mine.Where(a => a.PhaseType == PhaseType.Normal).OrderBy(a => Start(a)).FirstOrDefault();
            var planned = planRows.Where(r => Is(rowVals.FirstOrDefault(v => v.RowId == r.Id && v.FieldKey == "auditee")?.Text))
                .Select(r => (Row: r, Date: rowVals.FirstOrDefault(v => v.RowId == r.Id && v.FieldKey == "planned_start")?.Date))
                .OrderBy(x => x.Date).FirstOrDefault();
            var kind = c.Keyword((mine.Select(a => V(a.Id, "entity_kind")?.RefId).FirstOrDefault(k => k != null))
                ?? rowVals.FirstOrDefault(v => planned.Row != null && v.RowId == planned.Row.Id && v.FieldKey == "entity_kind")?.RefId);
            var typeId = (last ?? open ?? mine.FirstOrDefault()) is { } ra ? V(ra.Id, "audit_type")?.RefGuid : null;
            typeId ??= planned.Row != null ? rowVals.FirstOrDefault(v => v.RowId == planned.Row.Id && v.FieldKey == "audit_type")?.RefGuid : null;
            var months = typeId is { } t && types.TryGetValue(t, out var m) ? m : 12;
            var lastDate = last == null ? null : Start(last);
            var freq = last == null ? null : c.Keyword(V(last.Id, "frequency_rec")?.RefId);
            var plannedDate = planned.Date is { } pd ? DateOnly.FromDateTime(pd) : (DateOnly?)null;
            DateOnly? next = lastDate is { } ld ? AuditRules.NextDue(ld, months, freq?.Key) : plannedDate;
            var (status, tone, rank) = open != null ? ("Scheduled", "info", 3)
                : next is { } n && n < today ? ("Overdue", "bad", 0)
                : next is { } s && s <= today.AddDays(60) ? ("Due soon", "warn", 1)
                : last == null && plannedDate == null ? ("Never audited", "warn", 2)
                : ("On track", "ok", 4);
            var capas = capaLinks.Count(l => mine.Any(a => a.Id == l.FromId) && openCapas.Contains(l.ToId));
            Entities.Add(new ProgramEntity(name, kind, last, lastDate, last == null ? null : c.Keyword(V(last.Id, "result")?.RefId), freq, months, next,
                open, open == null ? null : c.Phase(open.PhaseId), plannedDate, planned.Row == null ? null : plans.FirstOrDefault(p => p.Id == planned.Row.RecordId),
                capas, status, tone, rank));
        }
        Entities = Entities.OrderBy(e => e.Rank).ThenBy(e => e.NextDue ?? DateOnly.MaxValue).ThenBy(e => e.Name).ToList();
        return Page();
    }

    public IEnumerable<ProgramEntity> Shown => string.IsNullOrEmpty(Status) ? Entities : Entities.Where(e => e.Status == Status);
}
