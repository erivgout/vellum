using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.AdverseEvents;

namespace Vellum.Areas.Quality.Pages.Aer;

/// <summary>
/// A printable FDA Form 3500A layout of a MedWatch (sections A, B, D, E, G and H), with its signatures and acknowledgements. Printing is
/// audited; sensitive patient identifiers show only to managers (SEC-520). Browser print produces the PDF.
/// </summary>
[Authorize]
public class Print3500AModel(AccessService access, RecordService records, ConfigCache cfg, AuditService audit, VellumDbContext db, EmdrSubmissionService subs) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public RecordSnapshot Snap { get; private set; } = null!;
    public EmdrReport Report { get; private set; } = null!;
    public bool SeeSensitive { get; private set; }
    public List<Signature> Signatures { get; private set; } = [];
    public EmdrSubmission? Submission { get; private set; }
    public string? PhaseName => Snap.Phase?.Name;
    public bool Draft => Snap.Record.PhaseType == PhaseType.Normal && Snap.PhaseKey != "submitted";
    public string CurrentHash { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await access.CanReadAsync(Id)) return NotFound();
        var snap = await records.LoadRequiredAsync(Id);
        if (snap.Form.Key != AerKeys.MedWatch) return NotFound();
        Snap = snap;
        Report = EmdrReport.From(snap);
        SeeSensitive = access.CanSeeSensitive(snap.Record.AppId);
        Signatures = await db.Signatures.AsNoTracking().Where(s => s.RecordId == Id).OrderBy(s => s.SignedAt).ToListAsync();
        Submission = await subs.CurrentAsync(Id);
        CurrentHash = records.ComputeHash(snap);
        audit.Record(Id, "Printed", $"Printed FDA 3500A layout of {snap.Record.Number}" + (Report.ReportNumber.Length > 0 ? $" ({Report.ReportNumber})" : ""));
        await db.SaveChangesAsync();
        return Page();
    }

    public string Label(string list, string? key) => key == null ? "" : cfg.Current.Keyword(list, key)?.Label ?? key;
    public string Labels(string list, IEnumerable<string> keys) => string.Join("; ", keys.Select(k => Label(list, k)));
    public static string D(DateOnly? d) => d?.ToString("MM/dd/yyyy") ?? "";
    public static string YN(bool? b) => b switch { true => "Yes", false => "No", _ => "" };
}
