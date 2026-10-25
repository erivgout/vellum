using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Files;

namespace Vellum.Modules.Pdf;

/// <summary>An original upload with its current PDF rendition (if any) and the latest conversion job.</summary>
public sealed record RenditionPair(Attachment Original, Attachment? Rendition, PdfJob? Job)
{
    public bool Pending => Job is { Status: PdfJobStatus.Queued or PdfJobStatus.Converting };
    public bool Failed => Job is { Status: PdfJobStatus.Failed } && Rendition == null;
    /// <summary>queued | converting | done | failed | none</summary>
    public string State => Job?.Status switch
    {
        PdfJobStatus.Queued => Job.Attempts > 0 ? "retrying" : "queued",
        PdfJobStatus.Converting => "converting",
        PdfJobStatus.Failed => "failed",
        _ => Rendition != null ? "done" : "none",
    };
}

/// <summary>What the current user may do with a record's files in its current phase (DOC-023, DOC-081).</summary>
public sealed record PdfAccess(bool SeeOriginals, bool Download, bool Print, AppRole Role);

/// <summary>
/// The rendition contract and read side of the PDF engine. A rendition is an <see cref="Attachment"/> with
/// <see cref="Attachment.RenditionOfId"/> = the original's Id, <c>application/pdf</c>, the original's record, field and
/// row, and bytes in <see cref="FileStore"/>. The current rendition of an original is the latest non-removed one
/// (SAF-063: originals are never changed).
/// </summary>
[ScopedService]
public sealed class RenditionService(
    VellumDbContext db, AccessService access, CurrentUser me, PdfRuleStore rules, ConfigCache cfg, FileStore files,
    UserDirectory dir)
{
    // ------------------------------------------------------------------ contract (pure helpers)

    public static bool IsRendition(Attachment a) => a.RenditionOfId != null;

    /// <summary>The current rendition of <paramref name="original"/> among a record's attachments, or null.</summary>
    public static Attachment? CurrentOf(IEnumerable<Attachment> attachments, Attachment original) =>
        attachments.Where(a => a.RenditionOfId == original.Id && !a.Removed && a.ContentType == "application/pdf")
            .OrderByDescending(a => a.UploadedAt).ThenByDescending(a => a.Id).FirstOrDefault();

    /// <summary>Current originals of a field (any subform row), excluding renditions and removed files.</summary>
    public static IEnumerable<Attachment> OriginalsOf(IEnumerable<Attachment> attachments, string fieldKey) =>
        attachments.Where(a => a.FieldKey == fieldKey && !a.Removed && a.RenditionOfId == null);

    /// <summary>Current rendition of an original by Id (no access check: for services that already checked read access).</summary>
    public Task<Attachment?> CurrentRenditionAsync(long originalId) =>
        db.Attachments.AsNoTracking().Where(a => a.RenditionOfId == originalId && !a.Removed && a.ContentType == "application/pdf")
            .OrderByDescending(a => a.UploadedAt).ThenByDescending(a => a.Id).FirstOrDefaultAsync();

    /// <summary>Originals of a record (optionally one field) with their current renditions and latest jobs. Read access is checked.</summary>
    public async Task<List<RenditionPair>> ForRecordAsync(Guid recordId, string? fieldKey = null)
    {
        if (!me.IsSystem && !await access.CanReadAsync(recordId)) return [];
        var atts = await db.Attachments.AsNoTracking().Where(a => a.RecordId == recordId && !a.Removed && (fieldKey == null || a.FieldKey == fieldKey)).ToListAsync();
        var jobs = await LatestJobsAsync(recordId);
        return atts.Where(a => a.RenditionOfId == null).OrderBy(a => a.FieldKey).ThenBy(a => a.UploadedAt)
            .Select(o => new RenditionPair(o, CurrentOf(atts, o), jobs.GetValueOrDefault(o.Id))).ToList();
    }

    /// <summary>Latest job per original attachment of a record.</summary>
    public async Task<Dictionary<long, PdfJob>> LatestJobsAsync(Guid recordId)
    {
        var jobs = await db.Set<PdfJob>().AsNoTracking().Where(j => j.RecordId == recordId).OrderByDescending(j => j.Id).ToListAsync();
        return jobs.GroupBy(j => j.AttachmentId).ToDictionary(g => g.Key, g => g.First());
    }

    // ------------------------------------------------------------------ policy

    /// <summary>
    /// DOC-023: in a PDF-only phase, readers see renditions only. Originals stay available to "authorized editors":
    /// application managers, whoever can edit the record now or is assigned to it, its creator and its owner field.
    /// DOC-081: download and print of renditions by minimum application role per phase (managers always may).
    /// </summary>
    public async Task<PdfAccess> AccessAsync(Record record, string? fieldKey, PdfFormRules? formRules = null)
    {
        var form = cfg.Current.FormsById[record.FormId];
        formRules ??= await rules.ForFormAsync(form.Key);
        var phaseKey = cfg.Current.Phase(record.PhaseId)?.Key;
        var role = access.RoleFor(record.AppId, form.Key);
        var manager = me.IsSystem || role >= AppRole.Manager;
        var pdfOnly = fieldKey != null && formRules?.PdfOnly(fieldKey, phaseKey) == true;
        var seeOriginals = !pdfOnly || manager || await IsEditorAsync(record);
        var download = manager || formRules == null || role >= formRules.DownloadRole(phaseKey);
        var print = manager || formRules == null || role >= formRules.PrintRole(phaseKey);
        return new PdfAccess(seeOriginals, download, print, role);
    }

    private async Task<bool> IsEditorAsync(Record r)
    {
        if (!me.IsAuthenticated) return false;
        if (r.CreatedById == me.Id) return true;
        if ((await access.CanEditAsync(r)).Allowed || await access.ActingAssignmentAsync(r.Id) != null) return true;
        return await db.RecordValues.AnyAsync(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "owner" && v.RefId == me.Id);
    }

    /// <summary>SAF-043: DRAFT / SUPERSEDED / OBSOLETE / VOID marking of revision-controlled records that aren't effective.</summary>
    public static string? MarkingFor(Record r, FormDefinition form, PdfFormRules? formRules)
    {
        if (formRules is { Profile.AutoMark: false } || !form.RevisionControlled) return null;
        if (r.IsArchived) return r.ArchiveReason == "obsolete" ? "OBSOLETE" : "SUPERSEDED";
        return r.PhaseType switch
        {
            PhaseType.Obsolete => "OBSOLETE",
            PhaseType.Voided => "VOID",
            PhaseType.Normal when !r.IsEffective => "DRAFT",
            _ => null,
        };
    }

    /// <summary>The rendition bytes as shown to people: checksum verified (INV-053) and marked per SAF-043.</summary>
    public async Task<byte[]> ReadMarkedAsync(Attachment rendition, Record record)
    {
        var bytes = await files.ReadVerifiedAsync(rendition.Sha256);
        var form = cfg.Current.FormsById[record.FormId];
        var formRules = await rules.ForFormAsync(form.Key);
        var marking = MarkingFor(record, form, formRules);
        if (marking == null) return bytes;
        try
        {
            return PdfKit.Stamp(bytes, new WatermarkSpec(marking, WatermarkPosition.Diagonal, formRules?.Profile.Color ?? "#b91c1c",
                Math.Max(12, formRules?.Profile.Opacity ?? 18)));
        }
        catch (PdfProcessingException)
        {
            // Never serve unmarked draft or superseded content silently (SAF-043).
            throw new FileIntegrityException($"{rendition.FileName} couldn't be marked {marking}, so it wasn't shown.");
        }
    }

    // ------------------------------------------------------------------ INV-054

    /// <summary>
    /// INV-054 / PLT-807: messages for every required field whose current originals lack a current rendition when
    /// entering <paramref name="target"/>. With <paramref name="producedOnEntry"/>, files the target phase converts
    /// inline on entry (PDF, image, text or already converted) count as satisfied; the entry hook verifies them.
    /// </summary>
    public async Task<List<string>> MissingRequiredAsync(Record record, FormDefinition form, PhaseDefinition target, bool producedOnEntry)
    {
        var formRules = await rules.ForFormAsync(form.Key);
        var required = formRules?.RequiredFields(target.Key).ToList() ?? [];
        if (required.Count == 0) return [];
        var atts = await db.Attachments.AsNoTracking().Where(a => a.RecordId == record.Id && !a.Removed && required.Contains(a.FieldKey)).ToListAsync();
        var jobs = await LatestJobsAsync(record.Id);
        var cached = await CachedShasAsync(atts.Select(a => a.Sha256));
        var errors = new List<string>();
        foreach (var field in required)
        {
            var label = form.Fields.FirstOrDefault(f => f.Key == field)?.Label ?? field;
            foreach (var o in OriginalsOf(atts, field))
            {
                if (CurrentOf(atts, o) != null) continue;
                var job = jobs.GetValueOrDefault(o.Id);
                if (producedOnEntry && formRules!.Converts(field, target.Key) && job?.Status != PdfJobStatus.Failed &&
                    (PdfConverter.IsFast(o.FileName, o.ContentType, []) || cached.Contains(o.Sha256)))
                    continue;
                errors.Add(job?.Status switch
                {
                    PdfJobStatus.Failed => $"{label}: the PDF rendition of {o.FileName} failed: {job.LastError} {target.Name} requires a current PDF rendition (INV-054). Replace the file or use PDF › Retry conversion.",
                    PdfJobStatus.Queued or PdfJobStatus.Converting => $"{label}: {o.FileName} is still being converted to PDF ({(job.Status == PdfJobStatus.Converting ? "converting" : job.Attempts > 0 ? $"retry {job.Attempts + 1} queued" : "queued")}). {target.Name} requires its PDF rendition first (INV-054); try again in a moment.",
                    _ => $"{label}: {o.FileName} has no PDF rendition, and {target.Name} requires one (INV-054). Use PDF › Convert to PDF now, then route again.",
                });
            }
        }
        return errors;
    }

    public async Task<HashSet<string>> CachedShasAsync(IEnumerable<string> shas)
    {
        var list = shas.Distinct().ToList();
        return list.Count == 0 ? [] : (await db.Set<PdfConversion>().AsNoTracking().Where(c => list.Contains(c.SourceSha)).Select(c => c.SourceSha).ToListAsync()).ToHashSet();
    }

    public string Name(int? userId) => dir.Name(userId);
}
