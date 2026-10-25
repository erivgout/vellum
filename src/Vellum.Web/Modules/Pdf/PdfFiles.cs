using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Files;

namespace Vellum.Modules.Pdf;

/// <summary>
/// One row of an attachment field. <see cref="File"/> is what the row shows: the original, or (in a PDF-only phase,
/// DOC-023) its rendition. <see cref="Pdf"/> is the current rendition of an original shown next to it as "PDF".
/// </summary>
public sealed record PdfFileItem(Attachment File, Attachment? Pdf, string State, string? Error, bool RenditionOnly, bool CanDownload)
{
    public string ViewUrl => RenditionOnly ? $"/pdf/{File.Id}" : $"/files/{File.Id}?inline=true";
    public string? DownloadUrl => RenditionOnly ? (CanDownload ? $"/pdf/{File.Id}?handler=Download" : null) : $"/files/{File.Id}";
}

public sealed record PdfFieldFiles(List<PdfFileItem> Items, bool PdfOnly, int HiddenOriginals);

/// <summary>Builds attachment field rows for the record page and print view, with rendition state (cached per record per request).</summary>
[ScopedService]
public sealed class PdfFilePresenter(ConfigCache cfg, PdfRuleStore rules, RenditionService renditions)
{
    private readonly Dictionary<Guid, Dictionary<long, PdfJob>> _jobs = [];
    private readonly Dictionary<(Guid, string), PdfAccess> _access = [];

    public async Task<PdfFieldFiles> FilesAsync(Record? record, IReadOnlyList<Attachment> all, FieldDefinition field, bool editing)
    {
        var originals = all.Where(a => a.FieldKey == field.Key && a.RowId == null && !a.Removed && a.RenditionOfId == null).ToList();
        if (record == null) return new PdfFieldFiles(originals.Select(o => new PdfFileItem(o, null, "none", null, false, true)).ToList(), false, 0);
        var form = cfg.Current.FormsById[record.FormId];
        var formRules = await rules.ForFormAsync(form.Key);
        var hasRenditions = all.Any(a => a.FieldKey == field.Key && a.RenditionOfId != null && !a.Removed);
        if ((formRules is not { Enabled: true } || !formRules.ConvertsAnywhere(field.Key)) && !hasRenditions)
            return new PdfFieldFiles(originals.Select(o => new PdfFileItem(o, null, "none", null, false, true)).ToList(), false, 0);

        if (!_jobs.TryGetValue(record.Id, out var jobs)) _jobs[record.Id] = jobs = await renditions.LatestJobsAsync(record.Id);
        if (!_access.TryGetValue((record.Id, field.Key), out var access)) _access[(record.Id, field.Key)] = access = await renditions.AccessAsync(record, field.Key, formRules);
        var pdfOnly = !editing && !access.SeeOriginals;
        var items = new List<PdfFileItem>();
        var hidden = 0;
        foreach (var o in originals)
        {
            var pdf = RenditionService.CurrentOf(all, o);
            var job = jobs.GetValueOrDefault(o.Id);
            var state = job?.Status switch
            {
                PdfJobStatus.Queued => job.Attempts > 0 ? "retrying" : "queued",
                PdfJobStatus.Converting => "converting",
                PdfJobStatus.Failed when pdf == null => "failed",
                _ => pdf != null ? "done" : "none",
            };
            if (pdfOnly)
            {
                if (pdf != null) items.Add(new PdfFileItem(pdf, null, "done", null, true, access.Download));
                else hidden++;
                continue;
            }
            items.Add(new PdfFileItem(o, pdf, state, job?.Status == PdfJobStatus.Failed ? job.LastError : null, false, true));
        }
        return new PdfFieldFiles(items, pdfOnly, hidden);
    }
}

/// <summary>
/// /files rules (DOC-023, DOC-081, SAF-043): renditions are served through the viewer (marking and download permission);
/// originals of PDF-only fields are hidden from readers who aren't authorized editors.
/// </summary>
[ScopedService(typeof(IAttachmentPolicy))]
public sealed class PdfAttachmentPolicy(VellumDbContext db, ConfigCache cfg, PdfRuleStore rules, RenditionService renditions) : IAttachmentPolicy
{
    public async Task<AttachmentDecision> DecideAsync(Attachment attachment, bool inline)
    {
        if (attachment.RenditionOfId != null)
            return AttachmentDecision.Redirect(inline ? $"/pdf/{attachment.Id}" : $"/pdf/{attachment.Id}?handler=Download");
        var record = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == attachment.RecordId);
        if (record == null) return AttachmentDecision.Deny("Not found.");
        var formRules = await rules.ForFormAsync(cfg.Current.FormsById[record.FormId].Key);
        if (formRules?.PdfOnly(attachment.FieldKey, cfg.Current.Phase(record.PhaseId)?.Key) != true) return AttachmentDecision.Allow;
        var access = await renditions.AccessAsync(record, attachment.FieldKey, formRules);
        if (access.SeeOriginals) return AttachmentDecision.Allow;
        var pdf = await renditions.CurrentRenditionAsync(attachment.Id);
        return pdf != null ? AttachmentDecision.Redirect($"/pdf/{pdf.Id}") : AttachmentDecision.Deny("Only the PDF rendition is available in this phase (DOC-023).");
    }
}
