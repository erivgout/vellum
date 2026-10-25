using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Data;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Pages.Records;

/// <summary>PLT-809 / DOC-044 / SEC-605: printable record with the signature manifestation. Printing is audited.</summary>
public class PrintModel(AccessService access, RecordService records, RecordVmBuilder builder, AuditService audit, VellumDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public RecordVm Vm { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await access.CanReadAsync(Id)) return NotFound();
        var snap = await records.LoadRequiredAsync(Id);
        Vm = await builder.BuildAsync(snap, readOnly: true);
        audit.Record(Id, "Printed", $"Printed {snap.Record.Number}" + (snap.Record.Revision != null ? $" rev {snap.Record.Revision}" : ""));
        await db.SaveChangesAsync();
        return Page();
    }
}
