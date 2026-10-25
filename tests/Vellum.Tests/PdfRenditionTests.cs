using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure.Files;
using Vellum.Modules.Documents;
using Vellum.Modules.Pdf;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>Skips the test when LibreOffice (soffice) isn't installed on this machine.</summary>
public sealed class LibreOfficeFactAttribute : FactAttribute
{
    public LibreOfficeFactAttribute()
    {
        if (new OfficeConverter(new PdfOptions(), NullLogger.Instance).Locate() == null)
            Skip = "LibreOffice isn't installed (soffice not found).";
    }
}

/// <summary>
/// The PDF engine (PLT-804..807, DOC-021..023, DOC-043, DOC-081, INV-054, SAF-043, SAF-063, NFR-005, NFR-025) against the
/// real engine: the rendition contract, the route block, watermarks, the approval manifest and LibreOffice conversion.
/// </summary>
[Collection("vellum")]
public class PdfRenditionTests(VellumFixture fx)
{
    private static readonly Lazy<byte[]> SamplePdf = new(() => PdfLayout.Render(new PdfReport { Title = "Sample controlled content" }.Text("Step 1. Do the thing."), "Acme"));

    internal static byte[] Docx(string text)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
            Add("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            Add("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>""");
        }
        return ms.ToArray();
    }

    private static async Task<Record> NewDocAsync(IServiceScope s, string title, string fileName, byte[] content, string contentType = "application/pdf")
    {
        var res = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = DocKeys.Document,
            Values = new()
            {
                ["doc_type"] = "procedure", ["title"] = title, ["owner"] = s.UserId("sortiz"),
                ["reviewers"] = new List<int> { s.UserId("dokafor"), s.UserId("tbrooks") }, ["department"] = "operations",
                ["revision_reason"] = "New document for the PDF test.", ["training_required"] = false,
                ["training_justification"] = "Administrative content only.",
            },
            Files = [("primary_file", fileName, contentType, new MemoryStream(content))],
        });
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => $"{e.FieldKey}: {e.Message}")));
        return res.Record!;
    }

    private static List<Attachment> Atts(IServiceScope s, Guid id) =>
        s.Db().Attachments.AsNoTracking().Where(a => a.RecordId == id).OrderBy(a => a.Id).ToList();

    [Fact] // SAF-063, PLT-804, DOC-021, INV-114: the rendition storage contract
    public async Task Renditions_are_derived_linked_copies_that_never_touch_the_original_or_the_content_hash()
    {
        Guid id;
        string hashAtDraft;
        using (var s = fx.As("jlee"))
        {
            id = (await NewDocAsync(s, "Contract SOP", "Contract SOP.pdf", SamplePdf.Value)).Id;
            hashAtDraft = s.ServiceProvider.GetRequiredService<RecordService>().ComputeHash(await s.LoadAsync(id));
            Assert.DoesNotContain(Atts(s, id), a => a.RenditionOfId != null); // Draft doesn't convert
        }
        using (var a = fx.As("jlee")) DocumentControlTests.Ok(await a.RouteAsync(id, DocKeys.DcReview));

        using var s2 = fx.As("pshah");
        var atts = Atts(s2, id);
        var original = Assert.Single(atts, a => a.RenditionOfId == null);
        var rendition = Assert.Single(atts, a => a.RenditionOfId != null);
        Assert.Equal(original.Id, rendition.RenditionOfId);
        Assert.Equal("application/pdf", rendition.ContentType);
        Assert.Equal(original.RecordId, rendition.RecordId);
        Assert.Equal(original.FieldKey, rendition.FieldKey);
        Assert.Equal(original.RowId, rendition.RowId);
        Assert.Equal("Contract SOP.pdf", rendition.FileName); // PLT-804: an existing .pdf name is unchanged
        // SAF-063 / INV-053: the original is byte-for-byte the upload; both are verified by the file store.
        var files = s2.ServiceProvider.GetRequiredService<FileStore>();
        Assert.Equal(SamplePdf.Value, await files.ReadVerifiedAsync(original.Sha256));
        Assert.True(files.Verify(rendition.Sha256));

        var snap = await s2.LoadAsync(id);
        Assert.Single(snap.AttachmentsOf("primary_file")); // renditions aren't separate uploads
        Assert.Equal(rendition.Id, RenditionService.CurrentOf(snap.Attachments, original)!.Id);
        Assert.Equal(rendition.Id, (await s2.ServiceProvider.GetRequiredService<RenditionService>().CurrentRenditionAsync(original.Id))!.Id);
        Assert.Equal(hashAtDraft, s2.ServiceProvider.GetRequiredService<RecordService>().ComputeHash(snap)); // derived copies don't change the hash

        // A rendition made after signing doesn't invalidate the signature (INV-114).
        using (var dc = fx.As("pshah")) DocumentControlTests.Ok(await dc.RouteAsync(id, DocKeys.InitialApproval));
        using (var o = fx.As("sortiz")) DocumentControlTests.Ok(await o.RouteAsync(id, DocKeys.Review));
        using var s3 = fx.As("sortiz");
        var queue = s3.ServiceProvider.GetRequiredService<PdfQueue>();
        var jobs = await queue.EnqueueAsync(await s3.LoadAsync(id), ["primary_file"], "manual", force: true);
        await queue.ProcessInlineAsync(jobs);
        await s3.Db().SaveChangesAsync();
        Assert.Equal(2, Atts(s3, id).Count(a => a.RenditionOfId == original.Id));
        var after = await s3.LoadAsync(id);
        var sig = await s3.Db().Signatures.AsNoTracking().SingleAsync(x => x.RecordId == id && !x.Superseded);
        Assert.Equal(sig.RecordHash, s3.ServiceProvider.GetRequiredService<RecordService>().ComputeHash(after));
        Assert.Equal(jobs.Single().RenditionId, RenditionService.CurrentOf(after.Attachments, original)!.Id); // latest wins
    }

    [Fact] // INV-054, PLT-807: never enter a phase that requires renditions without them
    public async Task Routing_into_a_phase_that_requires_renditions_is_blocked_until_they_exist()
    {
        Guid id;
        using (var s = fx.As("jlee"))
            id = (await NewDocAsync(s, "Blocked SOP", "Blocked SOP.docx", Encoding.UTF8.GetBytes("not really a Word file"),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document")).Id;
        await DocumentControlTests.ToAwaitingReleaseAsync(fx, id);

        using (var s = fx.As("pshah"))
        {
            // The Office file waits for the background converter, so release is blocked with a clear message.
            var job = await s.Db().Set<PdfJob>().AsNoTracking().Where(j => j.RecordId == id).OrderByDescending(j => j.Id).FirstAsync();
            Assert.Equal(PdfJobStatus.Queued, job.Status);
            var res = await s.RouteAsync(id, DocKeys.Approved);
            Assert.False(res.Ok);
            var error = Assert.Single(res.Errors, e => e.Message.Contains("INV-054"));
            Assert.Contains("Blocked SOP.docx", error.Message);
            Assert.Contains("being converted", error.Message);
        }

        // System routes can't bypass it either (the entry hook re-checks inside the transaction).
        using (var s = fx.As("pshah"))
        {
            var ex = await Assert.ThrowsAsync<WorkflowException>(() => s.ServiceProvider.GetRequiredService<WorkflowEngine>().SystemRouteAsync(id, DocKeys.Approved, "test"));
            Assert.Contains("INV-054", ex.Message);
            Assert.Equal(DocKeys.AwaitingRelease, (await s.LoadAsync(id)).PhaseKey);
        }

        // Once a current rendition exists and is linked, the release goes through.
        using (var s = fx.As("pshah"))
        {
            var original = Atts(s, id).Single(a => a.RenditionOfId == null);
            var rs = s.ServiceProvider.GetRequiredService<RecordService>();
            var snap = await s.LoadAsync(id);
            var pdf = await rs.AddAttachmentAsync(snap.Record, "primary_file", null, "Blocked SOP_docx.pdf", "application/pdf", new MemoryStream(SamplePdf.Value));
            pdf.RenditionOfId = original.Id;
            await s.Db().SaveChangesAsync();
            DocumentControlTests.Ok(await s.RouteAsync(id, DocKeys.Approved));
            Assert.True((await s.LoadAsync(id)).Record.IsEffective);
        }
    }

    [Fact] // PLT-807: a corrupt file fails cleanly, is logged and notified, and blocks release
    public async Task A_corrupt_file_fails_conversion_with_a_clear_reason_and_blocks_release()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Corrupt SOP", "Corrupt SOP.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 garbage"))).Id;
        using (var a = fx.As("jlee")) DocumentControlTests.Ok(await a.RouteAsync(id, DocKeys.DcReview));
        using (var s = fx.As("pshah"))
        {
            var job = await s.Db().Set<PdfJob>().AsNoTracking().SingleAsync(j => j.RecordId == id);
            Assert.Equal(PdfJobStatus.Failed, job.Status);
            Assert.True(job.Permanent);
            Assert.Contains("corrupt", job.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.True(await s.Db().Audit.AnyAsync(e => e.RecordId == id && e.Action == "PDF conversion failed"));
            Assert.True(await s.Db().Notifications.AnyAsync(n => n.RecordId == id && n.UserId == s.UserId("jlee") && n.Title.StartsWith("PDF conversion failed")));
            var snap = await s.LoadAsync(id);
            var approved = snap.Workflow!.Phases.Single(p => p.Key == DocKeys.Approved);
            var missing = await s.ServiceProvider.GetRequiredService<RenditionService>().MissingRequiredAsync(snap.Record, snap.Form, approved, producedOnEntry: true);
            Assert.Contains(missing, m => m.Contains("failed") && m.Contains("INV-054"));
        }
    }

    [Fact] // DOC-022, SAF-043: phase watermarks with tokens are stamped; DRAFT/SUPERSEDED marking is added when shown
    public async Task Watermark_tokens_are_resolved_per_phase_and_drafts_are_marked()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Watermark SOP", "Watermark SOP.pdf", SamplePdf.Value)).Id;
        await DocumentControlTests.ReleaseAsync(fx, id);

        using var s2 = fx.As("pshah");
        var snap = await s2.LoadAsync(id);
        var r = snap.Record;
        var expected = $"{r.Number} rev {r.Revision} · Effective {r.EffectiveDate:dd MMM yyyy} · Uncontrolled when printed";
        var job = await s2.Db().Set<PdfJob>().AsNoTracking().Where(j => j.RecordId == id && j.Status == PdfJobStatus.Done).OrderByDescending(j => j.Id).FirstAsync();
        Assert.Equal(DocKeys.Approved, job.PhaseKey);
        Assert.Equal(expected, job.Watermark);
        Assert.Contains("(cached)", job.Converter); // converted once, re-stamped per phase (PLT-804)
        var awaiting = await s2.Db().Set<PdfJob>().AsNoTracking().Where(j => j.RecordId == id && j.PhaseKey == DocKeys.AwaitingRelease).SingleAsync();
        Assert.Equal($"{r.Number} rev {r.Revision} · Approved, not yet effective", awaiting.Watermark);

        // Token resolution on its own, including any field key and unknown tokens left visible.
        var text = PdfRuleStore.ResolveTokens("{number}/{revision} {phase} {title} {department} {nope}", snap, s2.ServiceProvider.GetRequiredService<Engine.Security.UserDirectory>(), "Acme", TimeZoneInfo.Utc);
        Assert.Equal($"{r.Number}/{r.Revision} Approved Watermark SOP Operations {{nope}}", text);

        // The stored rendition carries the stamp; the effective revision is served as stored, a draft gets marked.
        var svc = s2.ServiceProvider.GetRequiredService<RenditionService>();
        var rendition = (await s2.Db().Attachments.AsNoTracking().FirstAsync(a => a.Id == job.RenditionId));
        var stored = await s2.ServiceProvider.GetRequiredService<FileStore>().ReadVerifiedAsync(rendition.Sha256);
        Assert.Equal(PdfKit.PageCount(SamplePdf.Value), PdfKit.PageCount(stored));
        Assert.NotEqual(SamplePdf.Value, stored);
        Assert.Null(RenditionService.MarkingFor(r, snap.Form, null));
        Assert.Equal(stored, await svc.ReadMarkedAsync(rendition, r));
        var draft = new Record { Id = r.Id, FormId = r.FormId, PhaseType = PhaseType.Normal, Revision = "B", IsEffective = false };
        Assert.Equal("DRAFT", RenditionService.MarkingFor(draft, snap.Form, null));
        Assert.Equal("SUPERSEDED", RenditionService.MarkingFor(new Record { IsArchived = true, ArchiveReason = DocKeys.Superseded }, snap.Form, null));
        var marked = await svc.ReadMarkedAsync(rendition, draft);
        Assert.NotEqual(stored, marked);
        Assert.Equal(PdfKit.PageCount(stored), PdfKit.PageCount(marked));
    }

    [Fact] // DOC-043, DOC-044, INT-409: the approval manifest lists every signature
    public async Task Approval_manifest_lists_every_signature_with_meaning_time_zone_certification_and_hash()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Manifest SOP", "Manifest SOP.pdf", SamplePdf.Value)).Id;
        await DocumentControlTests.ReleaseAsync(fx, id);

        using var s2 = fx.As("tbrooks");
        var pdf = s2.ServiceProvider.GetRequiredService<IPdfService>();
        var report = await pdf.BuildApprovalManifestAsync(id);
        Assert.NotNull(report);
        var text = string.Join("\n", report!.PlainText);
        var snap = await s2.LoadAsync(id);
        var hash = s2.ServiceProvider.GetRequiredService<RecordService>().ComputeHash(snap);
        var sigs = await s2.Db().Signatures.AsNoTracking().Where(x => x.RecordId == id && !x.Superseded).ToListAsync();
        Assert.True(sigs.Count >= 5); // initial approval, two reviews, final approval, release
        foreach (var sig in sigs)
        {
            Assert.Contains(sig.SignerName, text);
            Assert.Contains(sig.Meaning, text);
            Assert.Contains(sig.SignedAt.ToString(":mm:ss"), text);
        }
        Assert.Contains("UTC", text);
        Assert.Contains(hash, text);
        Assert.Contains("Matches current content", text);
        var settings = await s2.Db().Settings.AsNoTracking().FirstAsync();
        Assert.Contains(settings.ESignatureCertification, text);

        var bytes = await pdf.ApprovalManifestAsync(id);
        Assert.True(PdfKit.PageCount(bytes!) >= 1);
        Assert.True(await s2.Db().Audit.AnyAsync(e => e.RecordId == id && e.Action == "Approval manifest generated"));

        // Controlled copy: the rendition followed by the manifest.
        var rendition = await s2.ServiceProvider.GetRequiredService<RenditionService>().CurrentRenditionAsync(snap.AttachmentsOf("primary_file").Single().Id);
        var copy = await pdf.ControlledCopyAsync(rendition!.Id);
        Assert.Equal(PdfKit.PageCount(SamplePdf.Value) + PdfKit.PageCount(bytes!), PdfKit.PageCount(copy!));

        // The record's print view renders as PDF too (PLT-809), security-trimmed and audited.
        var printed = await pdf.RenderRecordAsync(id);
        Assert.True(PdfKit.PageCount(printed!) >= 1);
        var printReport = await pdf.BuildRecordReportAsync(id);
        Assert.Contains(printReport!.PlainText, l => l.Contains("Manifest SOP"));
        Assert.Contains(printReport.PlainText, l => l == "Electronic signatures");
        Assert.True(await s2.Db().Audit.AnyAsync(e => e.RecordId == id && e.Action == "Printed"));
    }

    [Fact] // DOC-023, DOC-081: PDF-only read mode and download permission per phase
    public async Task Pdf_only_phases_hide_originals_from_readers_and_download_follows_the_phase_role()
    {
        // Rules on the change request's "supporting" field, restored afterwards.
        using (var admin = fx.As("admin"))
        {
            var store = admin.ServiceProvider.GetRequiredService<PdfRuleStore>();
            var (ok, message) = await store.SaveAsync(DocKeys.ChangeRequest, new PdfRulesInput
            {
                Phases = [new PdfPhaseSetting { PhaseKey = "requested", DownloadRole = AppRole.Manager, PrintRole = AppRole.Author }],
                Fields = [new PdfFieldSetting { FieldKey = "supporting", PhaseKey = "requested", Convert = true, PdfOnly = true }],
            });
            Assert.True(ok, message);
        }
        try
        {
            Guid docId, crId;
            using (var s = fx.As("jlee")) docId = (await NewDocAsync(s, "PDF-only target", "PDF-only target.pdf", SamplePdf.Value)).Id;
            await DocumentControlTests.ReleaseAsync(fx, docId);
            using (var s = fx.As("jlee"))
            {
                var res = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
                {
                    FormKey = DocKeys.ChangeRequest, ParentId = docId, SkipAccessCheck = true,
                    Values = new()
                    {
                        ["target_document"] = docId, ["summary"] = "Clarify step 2", ["reason"] = "Feedback from the line.",
                        ["approver"] = s.UserId("sortiz"), ["assign_to"] = new List<int> { s.UserId("jlee") },
                    },
                    Files = [("supporting", "markup.txt", "text/plain", new MemoryStream("Step 2: use the new torque value."u8.ToArray()))],
                });
                Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
                crId = res.Record!.Id;
                // Convert-on-save in the phase (PLT-804) is picked up by the sweep.
                var queue = s.ServiceProvider.GetRequiredService<PdfQueue>();
                Assert.True(await queue.SweepUploadsAsync(DateTime.UtcNow.AddMinutes(-5)) >= 1);
                foreach (var jobId in await s.Db().Set<PdfJob>().Where(j => j.RecordId == crId).Select(j => j.Id).ToListAsync())
                    Assert.True(await queue.ProcessAsync(jobId));
                var pdf = Atts(s, crId).Single(a => a.RenditionOfId != null);
                Assert.Equal("markup_txt.pdf", pdf.FileName); // PLT-804 naming
            }

            Attachment original;
            using (var reader = fx.As("tbrooks"))
            {
                original = Atts(reader, crId).Single(a => a.RenditionOfId == null);
                var record = await reader.Db().Records.AsNoTracking().FirstAsync(r => r.Id == crId);
                var access = await reader.ServiceProvider.GetRequiredService<RenditionService>().AccessAsync(record, "supporting");
                Assert.False(access.SeeOriginals);
                Assert.False(access.Download);
                Assert.True(access.Print);
                var policy = reader.ServiceProvider.GetServices<IAttachmentPolicy>().OfType<PdfAttachmentPolicy>().Single();
                var decision = await policy.DecideAsync(original, inline: false);
                Assert.StartsWith("/pdf/", decision.RedirectUrl); // readers get the PDF, not the editable source
                var files = await reader.ServiceProvider.GetRequiredService<PdfFilePresenter>().FilesAsync(record, Atts(reader, crId),
                    (await reader.LoadAsync(crId)).Form.Fields.Single(f => f.Key == "supporting"), editing: false);
                Assert.True(files.PdfOnly);
                Assert.True(Assert.Single(files.Items).RenditionOnly);
            }
            using (var author = fx.As("jlee"))
            {
                var record = await author.Db().Records.AsNoTracking().FirstAsync(r => r.Id == crId);
                Assert.True((await author.ServiceProvider.GetRequiredService<RenditionService>().AccessAsync(record, "supporting")).SeeOriginals);
                var policy = author.ServiceProvider.GetServices<IAttachmentPolicy>().OfType<PdfAttachmentPolicy>().Single();
                Assert.True((await policy.DecideAsync(original, inline: true)).Allowed);
            }
        }
        finally
        {
            using var admin = fx.As("admin");
            await admin.ServiceProvider.GetRequiredService<PdfRuleStore>().SaveAsync(DocKeys.ChangeRequest, new PdfRulesInput { Enabled = false });
        }
    }

    [Fact] // PLT-804: a new revision copies originals only; unchanged files reuse the conversion cache
    public async Task A_new_revision_rederives_renditions_from_the_conversion_cache()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Revision SOP", "Revision SOP.pdf", SamplePdf.Value)).Id;
        await DocumentControlTests.ReleaseAsync(fx, id);
        Guid revId;
        using (var s = fx.As("sortiz"))
        {
            var svc = s.ServiceProvider.GetRequiredService<DocumentControlService>();
            var rev = await Data.Tx.RunAsync(s.Db(), async () => await svc.CreateRevisionAsync(await s.LoadAsync(id), s.UserId("jlee"), [s.UserId("jlee")], null, "Next revision", null, "test"));
            revId = rev.Id;
            var atts = Atts(s, revId);
            Assert.Single(atts);
            Assert.Null(atts[0].RenditionOfId);
        }
        using (var a = fx.As("jlee"))
        {
            Assert.True((await a.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(revId,
                new() { ["training_required"] = false, ["training_justification"] = "Editorial only." })).Ok);
            DocumentControlTests.Ok(await a.RouteAsync(revId, DocKeys.DcReview));
        }
        using (var s = fx.As("pshah"))
        {
            var job = await s.Db().Set<PdfJob>().AsNoTracking().SingleAsync(j => j.RecordId == revId);
            Assert.Equal(PdfJobStatus.Done, job.Status);
            Assert.EndsWith("(cached)", job.Converter);
        }
    }

    [Fact] // NFR-025: exponential backoff; PLT-807 / NFR-025: a missing LibreOffice fails cleanly and retryably
    public async Task Office_conversion_fails_cleanly_when_LibreOffice_is_missing()
    {
        Assert.Equal(30, PdfQueue.BackoffSeconds(1, 30));
        Assert.Equal(60, PdfQueue.BackoffSeconds(2, 30));
        Assert.Equal(240, PdfQueue.BackoffSeconds(4, 30));
        Assert.Equal(3600, PdfQueue.BackoffSeconds(20, 30));

        var missing = new OfficeConverter(new PdfOptions { SofficePath = Path.Combine(Path.GetTempPath(), "no-libreoffice", "soffice.exe") }, NullLogger.Instance);
        Assert.Null(missing.Locate());
        var ex = await Assert.ThrowsAsync<PdfProcessingException>(() => missing.ConvertAsync(Docx("Hello"), "Hello.docx"));
        Assert.False(ex.Permanent); // retried with backoff once LibreOffice is installed
        Assert.Contains("LibreOffice", ex.Message);
        Assert.Contains("SofficePath", ex.Message);

        var disabled = new OfficeConverter(new PdfOptions { SofficePath = "none" }, NullLogger.Instance);
        Assert.Null(disabled.Locate());
        Assert.Contains("isn't installed", (await Assert.ThrowsAsync<PdfProcessingException>(() => disabled.ConvertAsync(Docx("x"), "x.docx"))).Message);

        // Unsupported types and size limits fail permanently with a reason.
        using var s = fx.As("jlee");
        var converter = s.ServiceProvider.GetRequiredService<PdfConverter>();
        var unsupported = await Assert.ThrowsAsync<PdfProcessingException>(() => converter.ConvertAsync([1, 2, 3], "archive.zip", "application/zip"));
        Assert.True(unsupported.Permanent);
        Assert.Contains("ZIP", unsupported.Message);
        var big = await Assert.ThrowsAsync<PdfProcessingException>(() => converter.ConvertAsync(new byte[6 * 1024 * 1024], "huge.txt", "text/plain"));
        Assert.Contains("up to 5 MB", big.Message);
        Assert.Equal("Report_docx.pdf", SourceTypes.RenditionName("Report.docx"));
        Assert.Equal("README.pdf", SourceTypes.RenditionName("README"));
    }

    [LibreOfficeFact] // NFR-005, DOC-021: Office files go through LibreOffice headless in the background queue
    public async Task Office_files_are_converted_with_LibreOffice_in_the_background()
    {
        var direct = await new OfficeConverter(new PdfOptions(), NullLogger.Instance).ConvertAsync(Docx("Direct conversion check"), "check.docx");
        Assert.True(PdfKit.PageCount(direct) >= 1);

        Guid id;
        using (var s = fx.As("jlee"))
            id = (await NewDocAsync(s, "Word SOP", "Word SOP.docx", Docx("Cleanroom entry: gown in the order shown."),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document")).Id;
        using (var a = fx.As("jlee")) DocumentControlTests.Ok(await a.RouteAsync(id, DocKeys.DcReview));
        using var s2 = fx.As("pshah");
        var job = await s2.Db().Set<PdfJob>().AsNoTracking().SingleAsync(j => j.RecordId == id);
        Assert.Equal(PdfJobStatus.Queued, job.Status); // not converted inline: it needs LibreOffice
        Assert.True(await s2.ServiceProvider.GetRequiredService<PdfQueue>().ProcessAsync(job.Id));
        s2.Db().ChangeTracker.Clear();
        job = await s2.Db().Set<PdfJob>().AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.True(job.Status == PdfJobStatus.Done, job.LastError);
        Assert.Equal("libreoffice", job.Converter);
        var rendition = Atts(s2, id).Single(a => a.RenditionOfId != null);
        Assert.Equal("Word SOP_docx.pdf", rendition.FileName);
        Assert.True(PdfKit.PageCount(await s2.ServiceProvider.GetRequiredService<FileStore>().ReadVerifiedAsync(rendition.Sha256)) >= 1);
        Assert.Equal(1, await s2.Db().Set<PdfConversion>().CountAsync(c => c.SourceSha == job.SourceSha && c.Converter == "libreoffice"));
    }
}
