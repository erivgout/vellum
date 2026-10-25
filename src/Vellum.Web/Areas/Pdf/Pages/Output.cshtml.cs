using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Engine.Security;
using Vellum.Modules.Pdf;

namespace Vellum.Areas.Pdf.Pages;

/// <summary>
/// Record-level PDF output: <c>/pdf/record/{id}</c> the print view as PDF (PLT-809, DOC-044), <c>/pdf/manifest/{id}</c>
/// the approval manifest (DOC-043) and <c>/pdf/status/{id}</c> the conversion state polled by the record page (NFR-005).
/// </summary>
[Authorize]
public class OutputModel(VellumDbContext db, AccessService access, IPdfService pdf, RenditionService renditions) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "";
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await access.CanReadAsync(Id)) return NotFound();
        var number = await db.Records.AsNoTracking().Where(r => r.Id == Id).Select(r => r.Number + (r.Revision != null ? "_rev_" + r.Revision : "")).FirstAsync();
        Response.Headers.CacheControl = "no-store";
        switch (Kind)
        {
            case "record":
            {
                var bytes = await pdf.RenderRecordAsync(Id);
                return bytes == null ? NotFound() : File(bytes, "application/pdf", $"{number}.pdf");
            }
            case "manifest":
            {
                var bytes = await pdf.ApprovalManifestAsync(Id);
                return bytes == null ? NotFound() : File(bytes, "application/pdf", $"{number}_approval_manifest.pdf");
            }
            case "status":
            {
                var pairs = await renditions.ForRecordAsync(Id);
                return new JsonResult(new
                {
                    pending = pairs.Count(p => p.Pending),
                    items = pairs.Select(p => new { id = p.Original.Id, state = p.State, rendition = p.Rendition?.Id, error = p.Job?.LastError }),
                });
            }
        }
        return NotFound();
    }
}
