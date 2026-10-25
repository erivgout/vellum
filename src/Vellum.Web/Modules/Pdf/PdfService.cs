using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Ui = Vellum.Infrastructure.Ui.Ui;

namespace Vellum.Modules.Pdf;

/// <summary>Options for <see cref="IPdfService.RenderRecordAsync"/>.</summary>
public sealed record RecordPdfOptions
{
    public bool Signatures { get; init; } = true;
    public bool PhaseHistory { get; init; } = true;
    public bool Landscape { get; init; }
    /// <summary>Audit the output as "Printed" (default) or not (for example when it's embedded in another document).</summary>
    public bool Audit { get; init; } = true;
}

/// <summary>
/// PDF output for every module, without a browser engine (PDFsharp/MigraDoc, MIT):
/// record print views (PLT-809, DOC-044), generic layouts for official forms (INT-409, e.g. MedWatch 3500A),
/// approval manifests (DOC-043), controlled copies, conversion and watermarking.
/// Resolve <see cref="IPdfService"/> from DI. Every record method checks read access (INV-081) and audits the output.
/// </summary>
public interface IPdfService
{
    /// <summary>Render a generic layout (header band, title block, field grids, tables, banners, page numbers).</summary>
    byte[] RenderReport(PdfReport report);

    /// <summary>The record's print view as a <see cref="PdfReport"/>, to render as is or extend with more blocks first.</summary>
    Task<PdfReport?> BuildRecordReportAsync(Guid recordId, RecordPdfOptions? options = null);

    /// <summary>The record's print view as PDF (visible sections, signature manifestation, phase history), or null without read access.</summary>
    Task<byte[]?> RenderRecordAsync(Guid recordId, RecordPdfOptions? options = null);

    /// <summary>DOC-043: the signature page of a revision (every signature, meaning, time with zone, certification text, content hash).</summary>
    Task<byte[]?> ApprovalManifestAsync(Guid recordId);

    /// <summary>DOC-043: the rendition (marked per SAF-043) followed by the approval manifest. Null without access.</summary>
    Task<byte[]?> ControlledCopyAsync(long renditionId);

    /// <summary>Convert a file to PDF (PDF pass-through, image, text, or Office via LibreOffice). Throws <see cref="PdfProcessingException"/>.</summary>
    Task<ConversionOutput> ConvertAsync(byte[] content, string fileName, string? contentType = null, CancellationToken ct = default);

    /// <summary>Stamp watermarks on every page.</summary>
    byte[] Stamp(byte[] pdf, params WatermarkSpec[] marks);

    /// <summary>Concatenate PDFs.</summary>
    byte[] Combine(params byte[][] pdfs);
}

[ScopedService(typeof(IPdfService))]
public sealed class PdfService(
    VellumDbContext db, RecordService records, AccessService access, CurrentUser me, ConfigCache cfg, UserDirectory dir,
    AuditService audit, PdfConverter converter, PdfRuleStore rules, RenditionService renditions) : IPdfService
{
    private string? _org;
    private string Org => _org ??= db.Settings.AsNoTracking().Select(s => s.OrganizationName).FirstOrDefault() ?? "Vellum";

    public byte[] RenderReport(PdfReport report) => PdfLayout.Render(report, Org);

    public Task<ConversionOutput> ConvertAsync(byte[] content, string fileName, string? contentType = null, CancellationToken ct = default) =>
        converter.ConvertAsync(content, fileName, contentType, ct);

    public byte[] Stamp(byte[] pdf, params WatermarkSpec[] marks) => PdfKit.Stamp(pdf, marks);

    public byte[] Combine(params byte[][] pdfs) => PdfKit.Combine(pdfs);

    private static string When(DateTime utc, TimeZoneInfo tz)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(u, tz);
        var main = local.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " " + Ui.Abbrev(tz, local);
        return tz == TimeZoneInfo.Utc ? main : main + $"\n{u:yyyy-MM-dd HH:mm:ss} UTC";
    }

    private async Task<TimeZoneInfo> RecordZoneAsync(Record r)
    {
        var id = r.LocationId is { } l ? await db.Locations.AsNoTracking().Where(x => x.Id == l).Select(x => x.TimeZone).FirstOrDefaultAsync() : null;
        return RecordService.Tz(id);
    }

    private static (string Text, PdfTone Tone) StatusOf(Record r, PhaseDefinition? phase) =>
        r.IsArchived ? (r.ArchiveReason == "obsolete" ? "Obsolete: withdrawn from use" : "Superseded: not for use", PdfTone.Warn)
        : r.IsEffective ? ("Effective revision", PdfTone.Ok)
        : r.PhaseType == PhaseType.Normal && r.Revision != null ? ("Draft: not approved for use", PdfTone.Info)
        : r.PhaseType == PhaseType.Voided ? ("Voided", PdfTone.Bad)
        : (phase?.Name ?? "Record", PdfTone.Neutral);

    // ------------------------------------------------------------------ record print view (PLT-809)

    public async Task<PdfReport?> BuildRecordReportAsync(Guid recordId, RecordPdfOptions? options = null)
    {
        options ??= new RecordPdfOptions();
        if (!me.IsSystem && !await access.CanReadAsync(recordId)) return null;
        var snap = await records.LoadAsync(recordId);
        if (snap == null) return null;
        var r = snap.Record;
        var form = snap.Form;
        var phase = snap.Phase;
        var tz = records.TimeZone;
        var formRules = await rules.ForFormAsync(form.Key);
        var seeSensitive = access.CanSeeSensitive(form.AppId);
        var labels = await records.LinkLabelsAsync(snap.Data.Main.Keys.SelectMany(k => snap.Data.Main[k])
            .Concat(snap.Data.Rows.Values.SelectMany(rows => rows.SelectMany(x => x.Values.Keys.SelectMany(k => x.Values[k])))));
        var (status, tone) = StatusOf(r, phase);
        var now = DateTime.UtcNow;
        var hash = records.ComputeHash(snap);
        var report = new PdfReport
        {
            Title = r.Title,
            Reference = $"{r.Number}" + (r.Revision != null ? $" · Revision {r.Revision}" : "") + $" · {form.Name}",
            Subtitle = $"Phase: {phase?.Name ?? "–"} · Created {r.CreatedAt:dd MMM yyyy} by {dir.Name(r.CreatedById)}" +
                       (r.EffectiveDate is { } eff ? $" · Effective {eff:dd MMM yyyy}" : ""),
            Context = $"{snap.App.Name} · {form.Name}",
            Status = status, StatusTone = tone,
            Marking = RenditionService.MarkingFor(r, form, formRules),
            Landscape = options.Landscape,
            Author = me.Name,
            Footer = $"Printed by {me.Name} on {When(now, tz).Replace('\n', ' ')} · Record version {r.Version} · Content SHA-256 {hash}",
            FooterNote = "Printed copies are uncontrolled. Always check the electronic system for the effective revision.",
        };

        foreach (var section in form.Sections.OrderBy(s => s.Order))
        {
            if (FormRules.StateOf(section, phase, r) == SectionState.Hidden) continue;
            var fields = form.Fields.Where(f => f.SectionKey == section.Key && f.Type != FieldType.Prompt).OrderBy(f => f.Order).ToList();
            if (fields.Count == 0) continue;
            report.Heading(section.Name, section.Description);
            if (!section.IsSubform)
            {
                var items = fields.Where(f => FormRules.IsFieldVisible(f, snap.Data.Main, form, snap.Config))
                    .Select(f => new PdfField(f.Label, Value(f, snap, snap.Data.Main, null, seeSensitive, tz, labels), Wide: f.Width >= 12 || f.Type == FieldType.TextArea))
                    .ToList();
                report.Fields(items, options.Landscape ? 3 : 2);
                continue;
            }
            var rows = snap.Data.RowsOf(section.Key);
            report.Table(["#", .. fields.Select(f => f.Label)],
                rows.Select(row => (IReadOnlyList<string?>)[(row.Order + 1).ToString(CultureInfo.InvariantCulture), .. fields.Select(f => Value(f, snap, row.Values, row.Id, seeSensitive, tz, labels))]),
                [0.35, .. fields.Select(f => (double)Math.Max(1, f.Width))], empty: $"No {section.RowNoun ?? "row"}s.");
        }

        if (options.Signatures)
        {
            var sigs = await db.Signatures.AsNoTracking().Where(s => s.RecordId == r.Id).OrderBy(s => s.SignedAt).ToListAsync();
            report.Heading("Electronic signatures", "Signature manifestation: printed name, date and time, and meaning (21 CFR 11.50).");
            report.Table(["Signer", "Meaning", "Phase / action", "Date and time", "Status"],
                sigs.Select(s => (IReadOnlyList<string?>)[
                    s.SignerName + "\n" + s.SignerUsername + (s.OnBehalfOfName != null ? $" · on behalf of {s.OnBehalfOfName}" : ""),
                    s.Meaning, s.PhaseName + "\n" + s.Action, When(s.SignedAt, tz),
                    s.Superseded ? "Superseded" : s.RecordHash == hash ? "Valid" : "Content changed after signing"]),
                [1.6, 1.1, 1.5, 1.5, 1], empty: "No electronic signatures.");
        }
        if (options.PhaseHistory)
        {
            var entries = await db.PhaseEntries.AsNoTracking().Where(e => e.RecordId == r.Id).OrderBy(e => e.Id).ToListAsync();
            if (entries.Count > 0)
            {
                report.Heading("Phase history");
                report.Table(["Phase", "Entered", "Due", "Completed", "By", "Comment"],
                    entries.Select(e => (IReadOnlyList<string?>)[e.PhaseName, e.EnteredAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                        e.DueDate?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), e.ExitedAt?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                        e.ExitedById != null ? dir.Name(e.ExitedById) : null, e.Comment]),
                    [1.4, 1, 1, 1, 1.3, 2.2]);
            }
        }
        return report;
    }

    private string Value(FieldDefinition f, RecordSnapshot snap, ValueBag bag, Guid? rowId, bool seeSensitive, TimeZoneInfo tz, IReadOnlyDictionary<Guid, string> labels)
    {
        if (f.Sensitive && !seeSensitive) return "Restricted";
        if (f.Type == FieldType.Attachment)
        {
            var files = snap.Attachments.Where(a => a.FieldKey == f.Key && a.RowId == rowId && !a.Removed && a.RenditionOfId == null).ToList();
            if (files.Count == 0) return "No files";
            return string.Join("\n", files.Select(a =>
            {
                var pdf = RenditionService.CurrentOf(snap.Attachments, a);
                return $"{a.FileName} ({Ui.FileSize(a.Size)}, SHA-256 {a.Sha256[..12]}…)" + (pdf != null ? $" · PDF rendition {pdf.FileName}" : "");
            }));
        }
        return FieldCodec.Display(f, bag[f.Key], snap.Config, dir, tz, labels);
    }

    public async Task<byte[]?> RenderRecordAsync(Guid recordId, RecordPdfOptions? options = null)
    {
        options ??= new RecordPdfOptions();
        var report = await BuildRecordReportAsync(recordId, options);
        if (report == null) return null;
        var formRules = await rules.ForFormAsync(cfg.Current.FormsById[(await db.Records.AsNoTracking().FirstAsync(r => r.Id == recordId)).FormId].Key);
        var pdf = PdfLayout.Render(report, Org, formRules?.Profile.Color ?? "#b91c1c", formRules?.Profile.Opacity ?? 14);
        if (options.Audit)
        {
            audit.Record(recordId, "Printed", $"Printed {report.Reference} to PDF ({PdfKit.PageCount(pdf)} pages)");
            await db.SaveChangesAsync();
        }
        return pdf;
    }

    // ------------------------------------------------------------------ approval manifest (DOC-043)

    public async Task<byte[]?> ApprovalManifestAsync(Guid recordId) => await ManifestAsync(recordId, auditIt: true);

    private async Task<byte[]?> ManifestAsync(Guid recordId, bool auditIt)
    {
        if (!me.IsSystem && !await access.CanReadAsync(recordId)) return null;
        var snap = await records.LoadAsync(recordId);
        if (snap == null) return null;
        var r = snap.Record;
        var form = snap.Form;
        var formRules = await rules.ForFormAsync(form.Key);
        var tz = await RecordZoneAsync(r);
        var hash = records.ComputeHash(snap);
        var sigs = await db.Signatures.AsNoTracking().Where(s => s.RecordId == r.Id).OrderBy(s => s.SignedAt).ToListAsync();
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var (status, tone) = StatusOf(r, snap.Phase);
        var now = DateTime.UtcNow;
        var active = sigs.Where(s => !s.Superseded).ToList();
        var superseded = sigs.Where(s => s.Superseded).ToList();
        var location = r.LocationId is { } lid ? await db.Locations.AsNoTracking().Where(l => l.Id == lid).Select(l => l.Name).FirstOrDefaultAsync() : null;

        var report = new PdfReport
        {
            Title = "Approval manifest",
            Reference = $"{r.Number}" + (r.Revision != null ? $" · Revision {r.Revision}" : "") + $" · {r.Title}",
            Context = $"{snap.App.Name} · Approval manifest",
            Status = status, StatusTone = tone,
            Marking = RenditionService.MarkingFor(r, form, formRules),
            Author = "Vellum",
            Footer = $"Approval manifest of {r.Number}" + (r.Revision != null ? $" rev {r.Revision}" : "") + $" · generated {When(now, tz).Replace('\n', ' ')} · content SHA-256 {hash}",
            FooterNote = "Electronic signatures are linked to this record and can't be copied or changed (21 CFR 11.70). Verify in Vellum.",
        };
        report.Fields(
        [
            new("Record", $"{r.Number} · {form.Name}"),
            new("Revision", r.Revision ?? "–"),
            new("Title", r.Title, Wide: true),
            new("Status", status),
            new("Effective date", r.EffectiveDate?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? (r.IsArchived ? "–" : "Not yet effective")),
            new("Signatures in force", active.Count.ToString(CultureInfo.InvariantCulture)),
            new("Times shown in", tz == TimeZoneInfo.Utc ? "UTC" : $"{tz.Id}{(location != null ? $" ({location})" : "")}, with UTC"),
            new("Record content hash (SHA-256)", hash, Wide: true),
        ], columns: 2);
        if (formRules?.Profile.ManifestNote is { Length: > 0 } note) report.Text(note, muted: true);
        report.Banner("Signer certification", $"Before signing, each signer confirmed: “{settings.ESignatureCertification}”", PdfTone.Violet);
        report.Heading("Signatures", "Every electronic signature in force on this revision, in the order signed.");
        report.Table(["#", "Signer", "Meaning", "Phase and action", "Signed", "Content hash at signing"],
            active.Select((s, i) => (IReadOnlyList<string?>)[
                (i + 1).ToString(CultureInfo.InvariantCulture),
                s.SignerName + "\n" + s.SignerUsername + (s.OnBehalfOfName != null ? $"\non behalf of {s.OnBehalfOfName}" : ""),
                s.Meaning + (string.IsNullOrWhiteSpace(s.Comment) ? "" : $"\n“{s.Comment}”"),
                s.PhaseName + "\n" + s.Action,
                When(s.SignedAt, tz),
                s.RecordHash[..16] + "…\n" + (s.RecordHash == hash ? "Matches current content" : "Content changed after signing"),
            ]), [0.3, 1.5, 1.4, 1.5, 1.45, 1.35], empty: "No electronic signatures are in force on this revision.");
        if (superseded.Count > 0)
        {
            report.Heading("Superseded signatures", "Signatures from earlier passes through the workflow, kept for the record (DOC-042, INV-052).");
            report.Table(["Signer", "Meaning", "Phase", "Signed", "Superseded"],
                superseded.Select(s => (IReadOnlyList<string?>)[s.SignerName, s.Meaning, s.PhaseName, When(s.SignedAt, tz),
                    s.SupersededAt is { } at ? When(at, tz) : "–"]), [1.4, 1.2, 1.2, 1.5, 1.5]);
        }
        var pdf = PdfLayout.Render(report, settings.OrganizationName, formRules?.Profile.Color ?? "#b91c1c", formRules?.Profile.Opacity ?? 14);
        if (auditIt)
        {
            audit.Record(r.Id, "Approval manifest generated", $"{active.Count} signature(s) in force; content SHA-256 {hash[..12]}… (DOC-043)");
            await db.SaveChangesAsync();
        }
        return pdf;
    }

    public async Task<byte[]?> ControlledCopyAsync(long renditionId)
    {
        var rendition = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == renditionId && a.RenditionOfId != null && !a.Removed);
        if (rendition == null || (!me.IsSystem && !await access.CanReadAsync(rendition.RecordId))) return null;
        var record = await db.Records.AsNoTracking().FirstAsync(r => r.Id == rendition.RecordId);
        var policy = await renditions.AccessAsync(record, rendition.FieldKey);
        if (!policy.Download) return null;
        var body = await renditions.ReadMarkedAsync(rendition, record);
        var manifest = await ManifestAsync(record.Id, auditIt: false);
        if (manifest == null) return null;
        var pdf = PdfKit.Combine(body, manifest);
        audit.Record(record.Id, "Controlled copy generated", $"{rendition.FileName} with the approval manifest (DOC-043)", fieldKey: rendition.FieldKey);
        await db.SaveChangesAsync();
        return pdf;
    }
}
