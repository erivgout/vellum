using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Ehs;
using Vellum.Modules.Kit;

namespace Vellum.Areas.Ehs.Pages;

/// <summary>OSHA 300-style log, 300A summary totals and TRIR / DART rates (EHS-110, EHS-111, EHS-113, RPT-702).</summary>
public class OshaModel(EhsReports reports, AccessService access, ConfigCache cfg) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Year { get; set; }
    [BindProperty(SupportsGet = true)] public string? Site { get; set; }

    public int SelectedYear { get; private set; }
    public List<IncidentCase> Cases { get; private set; } = [];
    public List<IncidentCase> Log => Cases.Where(c => c.Recordable).ToList();
    public List<HoursRow> Hours { get; private set; } = [];
    public List<RateRow> BySite { get; } = [];
    public RateRow Total { get; private set; } = new("All sites", 0, 0, 0);
    public bool CanSeeNames { get; private set; }
    public List<int> Years { get; } = [];

    public record MonthRow(int Month, decimal Hours, int Recordable, int Dart, decimal? YtdTrir, decimal? YtdDart);
    public List<MonthRow> Months { get; } = [];

    private async Task<bool> LoadAsync()
    {
        if (!cfg.Current.AppsByKey.TryGetValue("ehs", out var app) || !access.CanOpenApp(app.Id)) return false;
        var thisYear = BusinessCalendar.Today.Year;
        SelectedYear = Year is >= 2000 and <= 2100 ? Year.Value : thisYear;
        if (Site != null && Sites.All.All(s => s.Key != Site)) Site = null;
        for (var y = thisYear; y > thisYear - 5; y--) Years.Add(y);

        Cases = await reports.CasesAsync(SelectedYear, Site);
        Hours = await reports.HoursAsync(SelectedYear, Site);
        CanSeeNames = reports.CanSeeNames;
        foreach (var s in Sites.All.Where(s => Site == null || s.Key == Site))
            BySite.Add(EhsReports.Rates(s.Name, Cases, Hours, s.Key));
        Total = EhsReports.Rates(Site == null ? "All sites" : Sites.Name(Site), Cases, Hours);

        decimal cumHours = 0; int cumRec = 0, cumDart = 0;
        for (var m = 1; m <= 12; m++)
        {
            var h = Hours.Where(x => x.Month.Month == m).Sum(x => x.Hours);
            var c = Cases.Where(x => x.LocalDate.Month == m).ToList();
            cumHours += h; cumRec += c.Count(x => x.Recordable); cumDart += c.Count(x => x.Dart);
            Months.Add(new MonthRow(m, h, c.Count(x => x.Recordable), c.Count(x => x.Dart), OshaRates.Trir(cumRec, cumHours), OshaRates.Dart(cumDart, cumHours)));
        }
        return true;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    /// <summary>The OSHA 300-style log as CSV (EHS-110). Names follow the same masking rules as the page.</summary>
    public async Task<IActionResult> OnGetCsvAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var sb = new StringBuilder();
        sb.AppendLine("Case no.,Employee name,Job title,Date of injury or illness,Where the event occurred,Description,Death,Days away,Job transfer or restriction,Other recordable,Days away from work,Days of job transfer or restriction,Injury or illness type,Site");
        foreach (var c in Log)
        {
            string[] cells =
            [
                c.Number, NameFor(c), c.JobTitle ?? "", c.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), c.Area ?? "", c.Title,
                c.Classification == "death" ? "X" : "", c.Classification == "days_away" ? "X" : "", c.Classification == "restricted" ? "X" : "",
                c.Classification == "other_recordable" ? "X" : "", c.DaysAway.ToString("0", CultureInfo.InvariantCulture), c.DaysRestricted.ToString("0", CultureInfo.InvariantCulture),
                CaseTypeLabel(c.CaseType), Sites.Name(c.Site),
            ];
            sb.AppendLine(string.Join(",", cells.Select(Csv)));
        }
        var name = $"osha-300-log-{SelectedYear}{(Site != null ? "-" + Site : "")}.csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", name);
    }

    private static string Csv(string s) => Vellum.Infrastructure.CsvCell.Of(s, quote: true);

    public string NameFor(IncidentCase c) => c.PrivacyCase ? "Privacy case" : CanSeeNames ? c.InjuredName ?? "(name not recorded)" : "Restricted";

    public static string CaseTypeLabel(string? key) => key switch
    {
        "injury" => "Injury", "skin_disorder" => "Skin disorder", "respiratory" => "Respiratory condition", "poisoning" => "Poisoning",
        "hearing_loss" => "Hearing loss", "other_illness" => "All other illnesses", _ => "",
    };

    public string Link(int? year = null, string? site = "__keep") =>
        $"/ehs/osha?year={year ?? SelectedYear}" + ((site == "__keep" ? Site : site) is { } s ? $"&site={s}" : "");
}
