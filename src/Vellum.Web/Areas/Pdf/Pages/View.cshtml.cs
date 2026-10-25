using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure.Files;
using Vellum.Modules.Pdf;

namespace Vellum.Areas.Pdf.Pages;

/// <summary>
/// DOC-081: the in-browser PDF viewer (pdf.js, no download needed), with download and print permitted per application
/// role and phase. Renditions are always served marked per SAF-043 and checksum-verified (INV-053); every view,
/// download and controlled copy is audited. Read access is re-checked on every request (INV-081).
/// </summary>
[Authorize]
public class ViewModel(VellumDbContext db, AccessService access, ConfigCache cfg, RenditionService renditions, PdfRuleStore rules,
    IPdfService pdf, AuditService audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    public Attachment Rendition { get; private set; } = null!;
    public Attachment? Original { get; private set; }
    public Record Record { get; private set; } = null!;
    public FormDefinition Form { get; private set; } = null!;
    public PhaseDefinition? Phase { get; private set; }
    public PdfAccess Access { get; private set; } = null!;
    public PdfJob? Job { get; private set; }
    public string? Marking { get; private set; }
    public bool Manifest { get; private set; }
    public Record? EffectiveRevision { get; private set; }
    public bool SeeOriginal { get; private set; }

    private async Task<bool> LoadAsync()
    {
        var att = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == Id && !a.Removed);
        if (att == null || !await access.CanReadAsync(att.RecordId)) return false;
        // An original's id opens its current rendition.
        if (att.RenditionOfId == null)
        {
            var current = await renditions.CurrentRenditionAsync(att.Id);
            if (current == null) return false;
            Original = att;
            att = current;
        }
        else Original = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == att.RenditionOfId);
        Rendition = att;
        Record = await db.Records.AsNoTracking().FirstAsync(r => r.Id == att.RecordId);
        Form = cfg.Current.FormsById[Record.FormId];
        Phase = cfg.Current.Phase(Record.PhaseId);
        var formRules = await rules.ForFormAsync(Form.Key);
        Access = await renditions.AccessAsync(Record, att.FieldKey, formRules);
        SeeOriginal = Access.SeeOriginals && Original is { Removed: false };
        Marking = RenditionService.MarkingFor(Record, Form, formRules);
        Manifest = formRules?.Profile.Manifest == true && await db.Signatures.AnyAsync(s => s.RecordId == Record.Id && !s.Superseded);
        Job = await db.Set<PdfJob>().AsNoTracking().Where(j => j.RenditionId == att.Id).FirstOrDefaultAsync();
        if (Record.FamilyId is { } fam && !Record.IsEffective)
            EffectiveRevision = await access.Readable().AsNoTracking().FirstOrDefaultAsync(r => r.FamilyId == fam && r.IsEffective && r.Id != Record.Id);
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        if (Rendition.Id != Id) return Redirect($"/pdf/{Rendition.Id}");
        return Page();
    }

    private async Task<IActionResult> ServeAsync(string action, bool attachment, Func<Task<byte[]?>> produce, string fileName)
    {
        byte[]? bytes;
        try { bytes = await produce(); }
        catch (FileIntegrityException ex)
        {
            audit.Record(Record.Id, "File integrity failure", ex.Message);
            await db.SaveChangesAsync();
            return new ObjectResult(new ProblemDetails { Title = "File failed its integrity check", Detail = ex.Message, Status = 409 }) { StatusCode = 409 };
        }
        if (bytes == null) return NotFound();
        if (action.Length > 0)
        {
            audit.Record(Record.Id, action, $"{fileName} (rendition SHA-256 {Rendition.Sha256[..12]}…)" + (Marking != null ? $", marked {Marking}" : ""), fieldKey: Rendition.FieldKey);
            await db.SaveChangesAsync();
        }
        Response.Headers.CacheControl = "no-store";
        return attachment ? File(bytes, "application/pdf", fileName) : File(bytes, "application/pdf");
    }

    /// <summary>The PDF bytes for the viewer (inline, marked).</summary>
    public async Task<IActionResult> OnGetFileAsync()
    {
        if (!await LoadAsync()) return NotFound();
        return await ServeAsync("Rendition viewed", false, async () => await renditions.ReadMarkedAsync(Rendition, Record), Rendition.FileName);
    }

    public async Task<IActionResult> OnGetDownloadAsync()
    {
        if (!await LoadAsync()) return NotFound();
        if (!Access.Download) return Forbid();
        return await ServeAsync("Rendition downloaded", true, async () => await renditions.ReadMarkedAsync(Rendition, Record), Rendition.FileName);
    }

    /// <summary>DOC-043: the rendition with the approval manifest appended.</summary>
    public async Task<IActionResult> OnGetCopyAsync()
    {
        if (!await LoadAsync()) return NotFound();
        if (!Manifest || !Access.Download) return Forbid();
        var name = Path.GetFileNameWithoutExtension(Rendition.FileName) + "_controlled_copy.pdf";
        return await ServeAsync("", true, () => pdf.ControlledCopyAsync(Rendition.Id), name);
    }
}
