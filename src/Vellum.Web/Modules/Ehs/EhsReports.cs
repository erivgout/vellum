using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Modules.Kit;

namespace Vellum.Modules.Ehs;

public sealed record IncidentCase(
    Guid Id, string Number, string Title, DateTime OccurredUtc, DateOnly LocalDate, string? Site, string? Area, string? Description,
    bool Recordable, bool Dart, string? Classification, string? CaseType, decimal DaysAway, decimal DaysRestricted,
    string? InjuredName, string? JobTitle, bool PrivacyCase, string? IncidentType);

public sealed record HoursRow(string? Site, DateOnly Month, decimal Hours, decimal? Employees);

public sealed record RateRow(string Label, decimal Hours, int Recordable, int DartCases)
{
    public decimal? Trir => OshaRates.Trir(Recordable, Hours);
    public decimal? DartRate => OshaRates.Dart(DartCases, Hours);
}

/// <summary>
/// OSHA log and incidence-rate data (EHS-110, EHS-111, RPT-702). Every query starts from the security-trimmed record set (INV-081);
/// injured persons' names are returned only to people allowed to see sensitive fields (SEC-520).
/// </summary>
[ScopedService]
public sealed class EhsReports(VellumDbContext db, AccessService access, ConfigCache cfg)
{
    private static readonly string[] CaseFields =
    [
        "occurred_at", "site", "area", "description", "recordable", "dart", "osha_classification", "osha_case_type",
        "days_away", "days_restricted", "injured_name", "injured_job_title", "privacy_case", "incident_type",
    ];

    public async Task<List<IncidentCase>> CasesAsync(int year, string? site = null, bool recordableOnly = false)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("incident", out var form)) return [];
        var from = new DateTime(year - 1, 12, 30, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(year + 1, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var records = await access.Readable().AsNoTracking()
            .Where(r => r.FormId == form.Id && r.PhaseType != PhaseType.Voided &&
                        db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "occurred_at" && v.Date >= from && v.Date < to))
            .Select(r => new { r.Id, r.Number, r.Title }).ToListAsync();
        var values = await ModuleKit.MainValuesAsync(db, records.Select(r => r.Id).ToList(), CaseFields);
        var showNames = access.CanSeeSensitive(form.AppId);
        var kw = cfg.Current;
        var list = new List<IncidentCase>();
        foreach (var r in records)
        {
            var v = values[r.Id];
            if (v.DateTime("occurred_at") is not { } at) continue;
            var siteKey = kw.Keyword(v.Ref("site"))?.Key;
            var local = Sites.LocalDate(at, siteKey);
            if (local.Year != year || (site != null && siteKey != site)) continue;
            var recordable = v.Bool("recordable") == true;
            if (recordableOnly && !recordable) continue;
            list.Add(new IncidentCase(r.Id, r.Number, r.Title, ModuleKit.Utc(at), local, siteKey, v.Text("area"), v.Text("description"),
                recordable, v.Bool("dart") == true, kw.Keyword(v.Ref("osha_classification"))?.Key, kw.Keyword(v.Ref("osha_case_type"))?.Key,
                v.Number("days_away") ?? 0, v.Number("days_restricted") ?? 0,
                showNames ? v.Text("injured_name") : null, v.Text("injured_job_title"), v.Bool("privacy_case") == true, kw.Keyword(v.Ref("incident_type"))?.Key));
        }
        return list.OrderBy(c => c.OccurredUtc).ToList();
    }

    public async Task<List<HoursRow>> HoursAsync(int year, string? site = null)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("hours_worked", out var form)) return [];
        var from = new DateTime(year, 1, 1);
        var to = new DateTime(year + 1, 1, 1);
        var ids = await access.Readable().AsNoTracking()
            .Where(r => r.FormId == form.Id && r.PhaseType != PhaseType.Voided &&
                        db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "period" && v.Date >= from && v.Date < to))
            .Select(r => r.Id).ToListAsync();
        var values = await ModuleKit.MainValuesAsync(db, ids, "site", "period", "hours", "employees");
        return values.Values
            .Select(v => new HoursRow(cfg.Current.Keyword(v.Ref("site"))?.Key, v.Date("period") ?? default, v.Number("hours") ?? 0, v.Number("employees")))
            .Where(h => site == null || h.Site == site)
            .OrderBy(h => h.Month).ThenBy(h => h.Site).ToList();
    }

    /// <summary>RPT-702 rates for one site (or all sites when <paramref name="site"/> is null).</summary>
    public static RateRow Rates(string label, IEnumerable<IncidentCase> cases, IEnumerable<HoursRow> hours, string? site = null)
    {
        var c = cases.Where(x => site == null || x.Site == site).ToList();
        return new RateRow(label, hours.Where(h => site == null || h.Site == site).Sum(h => h.Hours), c.Count(x => x.Recordable), c.Count(x => x.Dart));
    }

    public bool CanSeeNames => cfg.Current.AppsByKey.TryGetValue("ehs", out var app) && access.CanSeeSensitive(app.Id);
}
