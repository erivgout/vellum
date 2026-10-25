using System.Net;
using System.Text.Json;
using Vellum.Engine.Records;
using Vellum.Modules.Integrations.SharePoint;
using Vellum.Modules.Pdf;

namespace Vellum.Tests.E2e;

/// <summary>
/// PDF output and SharePoint publishing end to end through the pages: the in-browser viewer and its markings, downloads,
/// the approval manifest, controlled copies and record PDFs, Office conversion through LibreOffice, the PDF administration
/// pages, and publishing effective documents to a (local-folder) SharePoint library configured in the admin pages.
/// </summary>
[Collection("vellum")]
public class PdfSharePointE2eTests(VellumFixture fx)
{
    /// <summary>The "View" link of the PDF renditions card on a record page (the rendition's viewer URL).</summary>
    private static string ViewerUrl(Page record) =>
        record.All(".vl-pdf-card a[href^='/pdf/']").Select(a => a.GetAttribute("href")!).FirstOrDefault(h => !h.Contains('?') && !h.StartsWith("/pdf/record") && !h.StartsWith("/pdf/manifest"))
            ?? throw new Xunit.Sdk.XunitException($"No PDF rendition to view on {record.Path}.\n{record.Excerpt()}");

    private static async Task<(HttpResponseMessage Res, byte[] Body)> DownloadAsync(Browser b, string url)
    {
        var res = await b.RawGetAsync(url);
        return (res, await res.Content.ReadAsByteArrayAsync());
    }

    private static void AssertPdf(HttpResponseMessage res, byte[] body, string? fileName = null)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(body, 0, 4));
        if (fileName != null) Assert.Equal(fileName, res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact] // DOC-081, SAF-043, DOC-043, DOC-044, PLT-809
    public async Task The_viewer_marks_drafts_and_superseded_revisions_and_effective_ones_come_with_manifest_and_copies()
    {
        using var c = new Cast(fx);
        var title = $"Viewer SOP {E2e.Tag()}";
        var draft = await DocumentE2eTests.NewSopAsync(fx, await c.As("jlee"), title);
        var url = draft.Path;
        var number = draft.Number();

        // Entering Document Control Review makes the first rendition; the author views it marked DRAFT.
        var inReview = await draft.RouteOkAsync("Submit to Document Control");
        var draftViewer = ViewerUrl(inReview);
        inReview.ExpectText("DRAFT");
        var jlee = await c.As("jlee");
        var viewer = await jlee.GoAsync(draftViewer);
        Assert.Equal("Draft: not for operational use", viewer.One(".vl-banner .ttl").TextContent.Trim());
        viewer.ExpectText($"{title}.pdf");
        var (file, bytes) = await DownloadAsync(jlee, draftViewer + "?handler=File");
        AssertPdf(file, bytes);
        await (await c.As("nflores")).GoAsync(draftViewer, HttpStatusCode.NotFound); // a draft isn't for readers (DOC-080)

        await c.ToAwaitingReleaseAsync(url);
        var effective = await c.ReleaseAsync(url);
        var effectiveViewer = ViewerUrl(effective);

        // A production operator views the effective revision: no marking, print allowed, manifest and copies offered.
        var nflores = await c.As("nflores");
        viewer = await nflores.GoAsync(effectiveViewer);
        Assert.Null(viewer.Q(".vl-banner[role=note]"));
        viewer.ExpectText($"Effective {DocumentSteps.Day(BusinessCalendar.Today)}");
        Assert.NotNull(viewer.Q("[data-pdf-print]"));
        Assert.NotNull(viewer.Q($"a[href='{effectiveViewer}?handler=Download']"));
        var config = JsonDocument.Parse(viewer.One("#vl-pdf-config").TextContent).RootElement;
        Assert.True(config.GetProperty("canPrint").GetBoolean());

        var (dl, dlBytes) = await DownloadAsync(nflores, effectiveViewer + "?handler=Download");
        AssertPdf(dl, dlBytes);
        var (manifest, manifestBytes) = await DownloadAsync(nflores, viewer.One("a[href^='/pdf/manifest/']").GetAttribute("href")!);
        AssertPdf(manifest, manifestBytes, $"{number}_rev_A_approval_manifest.pdf");
        var (copy, copyBytes) = await DownloadAsync(nflores, viewer.One("a[href$='?handler=Copy']").GetAttribute("href")!);
        AssertPdf(copy, copyBytes);
        Assert.EndsWith("_controlled_copy.pdf", copy.Content.Headers.ContentDisposition?.FileNameStar ?? copy.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(PdfKit.PageCount(dlBytes) + PdfKit.PageCount(manifestBytes), PdfKit.PageCount(copyBytes));

        var (recordPdf, recordBytes) = await DownloadAsync(nflores, effective.One(".vl-pdf-card a[href^='/pdf/record/']").GetAttribute("href")!);
        AssertPdf(recordPdf, recordBytes, $"{number}_rev_A.pdf");
        var print = await nflores.GoAsync(url + "/print");
        print.ExpectText(title).ExpectText(c.Name("sortiz")).ExpectText(c.Name("dokafor")).ExpectText(c.Name("tbrooks")).ExpectText("Released");

        // Opening an original's id opens its current rendition.
        var original = effective.All(".vl-file a.fname").Select(a => a.GetAttribute("href")!).First(h => h.StartsWith("/files/"));
        var originalId = original["/files/".Length..].Split('?')[0];
        Assert.Equal(effectiveViewer, (await nflores.GoAsync($"/pdf/{originalId}")).Url.AbsolutePath);

        // Once revision B is released, A's rendition is shown SUPERSEDED with a pointer to B.
        var urlB = await c.MinorRevisionAsync(fx, url);
        viewer = await nflores.GoAsync(effectiveViewer);
        Assert.Equal("Superseded: not for use", viewer.One(".vl-banner .ttl").TextContent.Trim());
        viewer.ExpectText("Revision B is in force");
        Assert.Equal(urlB, viewer.One(".vl-banner a[href^='/records/']").GetAttribute("href"));
        Assert.Contains("SUPERSEDED", (await nflores.GoAsync(url)).One(".vl-pdf-card").TextContent);
    }

    [Fact] // DOC-081, DOC-023: download and print per phase and role, maintained in Administration › PDF renditions
    public async Task Download_and_print_follow_the_rules_set_in_the_PDF_administration()
    {
        using var c = new Cast(fx);
        var doc = await c.EffectiveSopAsync(fx, $"Print control {E2e.Tag()}");
        var viewerUrl = ViewerUrl(doc);
        var admin = await c.As("admin");

        var index = await admin.GoAsync("/admin/pdf");
        index.ExpectText("Controlled Document");
        var rules = await admin.GoAsync("/admin/pdf/form/document");
        Assert.Equal("Author", rules.Form("form[data-pdf-rules]").Values("dl.approved").Single());
        Assert.Contains("true", rules.Form("form[data-pdf-rules]").Values("rq.primary_file.approved"));

        try
        {
            // Downloading effective documents becomes a manager-only right.
            var saved = await rules.Form("form[data-pdf-rules]").Pick("dl.approved", "Manager").SubmitAsync();
            Assert.NotNull(saved.Toast);
            Assert.Equal("Manager", saved.Form("form[data-pdf-rules]").Values("dl.approved").Single());
            Assert.Contains("true", saved.Form("form[data-pdf-rules]").Values("cv.primary_file.dc_review")); // the rest is unchanged

            var nflores = await c.As("nflores");
            var viewer = await nflores.GoAsync(viewerUrl);
            Assert.NotNull(viewer.Q("[data-pdf-print]"));
            Assert.Null(viewer.Q("a[href$='?handler=Download']"));
            viewer.ExpectText("Downloading isn't permitted for your role in Approved");
            var refused = await nflores.RawGetAsync(viewerUrl + "?handler=Download");
            Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
            Assert.NotEqual("application/pdf", refused.Content.Headers.ContentType?.MediaType);
            Assert.NotNull((await c.Open("pshah", viewerUrl)).Q("a[href$='?handler=Download']"));
        }
        finally
        {
            var back = await (await admin.GoAsync("/admin/pdf/form/document")).Form("form[data-pdf-rules]").Pick("dl.approved", "Author").SubmitAsync();
            Assert.Equal("Author", back.Form("form[data-pdf-rules]").Values("dl.approved").Single());
        }

        // Obsolete documents can be viewed but neither downloaded nor printed by readers (packaged rule).
        var or = await (await c.Open("pshah", doc.Path)).ActionAsync("request_obsolete", f => f.Set("a.reason", "Withdrawn for the print-control test."));
        await (await c.Open("pshah", or.Path)).RouteOkAsync("Send for approval");
        await (await c.Open("sortiz", or.Path)).RouteOkAsync("Accept: make obsolete");
        var obsoleteViewer = await (await c.As("nflores")).GoAsync(viewerUrl);
        Assert.Equal("Obsolete: withdrawn from use", obsoleteViewer.One(".vl-banner .ttl").TextContent.Trim());
        obsoleteViewer.ExpectText("Viewing only: printing and downloading aren't permitted for your role in Obsolete");
        Assert.Null(obsoleteViewer.Q("[data-pdf-print]"));
        Assert.False(JsonDocument.Parse(obsoleteViewer.One("#vl-pdf-config").TextContent).RootElement.GetProperty("canPrint").GetBoolean());
        Assert.NotEqual(HttpStatusCode.OK, (await (await c.As("nflores")).RawGetAsync(viewerUrl + "?handler=Download")).StatusCode);
    }

    [LibreOfficeFact] // DOC-021, NFR-005: an Office primary file is rendered through LibreOffice in the background queue
    public async Task A_Word_primary_file_is_converted_through_the_queue_and_viewed_as_PDF()
    {
        using var c = new Cast(fx);
        var title = $"Word SOP {E2e.Tag()}";
        var jlee = await c.As("jlee");
        var draft = await jlee.CreateAsync("document", f => f
            .Pick("f.doc_type", "procedure").Set("f.title", title).Set("f.owner", fx.Uid("sortiz").ToString())
            .Set("f.reviewers", fx.Uids("dokafor", "tbrooks")).Set("f.revision_reason", "New.").Pick("f.training_required", "no")
            .Set("f.training_justification", "None.")
            .Attach("file.primary_file", $"{title}.docx", PdfRenditionTests.Docx("Gown in the order shown."), "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
        var queued = await draft.RouteOkAsync("Submit to Document Control");
        var state = Page.Squash(queued.One(".vl-pdf-card .vl-pdf-item .vl-kw").TextContent);
        Assert.True(state is "Queued" or "Converting" or "Ready", state);

        var admin = await c.As("admin");
        (await admin.GoAsync("/admin/pdf")).ExpectText("Available");
        var queue = await admin.GoAsync("/admin/pdf/queue?status=pending");
        if (state != "Ready")
        {
            queue.ExpectText($"{title}.docx");
            var processed = await queue.Form("form[action*='handler=Process']").SubmitAsync();
            Assert.StartsWith("Processed", processed.Toast);
        }

        var ready = await jlee.GoAsync(draft.Path);
        Assert.Equal("Ready", Page.Squash(ready.One(".vl-pdf-card .vl-pdf-item .vl-kw").TextContent));
        ready.ExpectText($"{title}_docx.pdf");
        var viewer = await jlee.GoAsync(ViewerUrl(ready));
        viewer.ExpectText($"derived from {title}.docx").ExpectText("libreoffice");
        var (res, body) = await DownloadAsync(jlee, viewer.Url.AbsolutePath + "?handler=File");
        AssertPdf(res, body);
        Assert.True(PdfKit.PageCount(body) >= 1);
        (await admin.GoAsync("/admin/pdf/queue?status=done")).ExpectText($"{title}.docx");
    }

    [Fact] // INT-301..308, DOC-084, SAF-021, ADM-1005
    public async Task SharePoint_publishing_is_set_up_in_the_admin_pages_and_follows_the_document_through_supersession()
    {
        using var c = new Cast(fx);
        var admin = await c.As("admin");
        var tag = E2e.Tag();
        var host = $"e2e{tag.ToLowerInvariant()}.test.local";
        var profileName = $"E2E site {tag}";
        int? ruleId = null;
        try
        {
            (await admin.GoAsync("/admin/integrations/sharepoint")).ExpectText("SharePoint");

            // Profile with a local-folder connection, then its library and a connection test.
            var profile = await (await admin.GoAsync("/admin/integrations/sharepoint/profiles/new")).Form("form.sp-profile-form")
                .Set("Input.Name", profileName).Set("Input.Host", host).Pick("Input.Connection", "LocalFolder").SubmitAsync();
            Assert.NotNull(profile.Toast);
            Assert.Matches(@"^/admin/integrations/sharepoint/profiles/\d+", profile.Url.AbsolutePath);
            var profileUrl = profile.Url.AbsolutePath;
            var lib = await profile.Form("form[action*='handler=Library']").Set("Lib.Name", "Controlled Documents").Set("Lib.Key", "docs")
                .Set("Lib.RelativeUrl", "/Controlled Documents")
                .Set("Lib.SimulatedColumns", "Title\nDocumentNumber\nRevision\nDocumentType\nDocumentStatus\nDepartment").SubmitAsync();
            Assert.NotNull(lib.Toast);
            lib.ExpectText("Controlled Documents");
            var tested = await (await admin.GoAsync(profileUrl)).Form("form[action*='handler=Test']").SubmitAsync();
            Assert.Empty(tested.Errors);
            tested.ExpectText("Controlled Documents");

            // Mapping profile: record values → library columns, checked against the library.
            var mapping = await (await admin.GoAsync("/admin/integrations/sharepoint/mappings/new?form=document")).Form("form[action*='handler=Save']")
                .Set("name", $"E2E metadata {tag}")
                .Set("source", "$title", "$number", "$revision", "doc_type", "$status", "department")
                .Set("template", "", "", "", "", "", "")
                .Set("column", "Title", "DocumentNumber", "Revision", "DocumentType", "DocumentStatus", "Department").SubmitAsync();
            Assert.NotNull(mapping.Toast);
            Assert.Matches(@"^/admin/integrations/sharepoint/mappings/\d+", mapping.Url.AbsolutePath);
            var libId = mapping.One($"#mp-lib optgroup[label='{profileName}'] option").GetAttribute("value")!;
            var check = await mapping.Form("form[action*='handler=Check']").Set("libraryId", libId).SubmitAsync();
            Assert.NotNull(check.Q(".vl-banner-ok[role=status]"));
            Assert.Empty(check.All(".sp-check li.bad"));

            // Publishing rule for effective documents: one file per revision; superseded files are deleted.
            var newRule = await admin.GoAsync("/admin/integrations/sharepoint/rules/new?form=document");
            var ruleLib = newRule.One($"select[name='Input.LibraryId'] optgroup[label='{profileName}'] option").GetAttribute("value")!;
            var ruleMapping = newRule.All("select[name='Input.MappingId'] option").First(o => o.TextContent.Contains($"E2E metadata {tag}")).GetAttribute("value")!;
            var rule = await newRule.Form("form[action*='handler=Save']")
                .Set("Input.Name", $"E2E effective documents {tag}").Pick("Input.PhaseKey", "approved").Set("Input.LibraryId", ruleLib)
                .Set("Input.MappingId", ruleMapping).Set("Input.FolderTemplate", "{doc_type}").Set("Input.FileNameTemplate", "{number} rev {revision}")
                .Pick("Input.WithdrawAction", "Delete").SubmitAsync();
            Assert.NotNull(rule.Toast);
            Assert.Matches(@"^/admin/integrations/sharepoint/rules/\d+", rule.Url.AbsolutePath);
            ruleId = int.Parse(rule.Url.AbsolutePath.Split('/').Last());
            (await admin.GoAsync("/admin/integrations/sharepoint")).ExpectText($"E2E effective documents {tag}");

            // Releasing a document queues its publication; processing the queue puts the PDF rendition in the library.
            var title = $"Published SOP {tag}";
            var revA = await c.EffectiveSopAsync(fx, title);
            var number = revA.Number();
            Assert.Equal("pending", revA.One("[data-sp-state]").GetAttribute("data-sp-state"));
            var log = await admin.GoAsync($"/admin/integrations/sharepoint/log?q={Uri.EscapeDataString(number)}");
            log.ExpectText(number);
            var processed = await log.Form("form[action*='handler=Process']").SubmitAsync();
            Assert.NotNull(processed.Toast);

            var library = Path.Combine(fx.SharePointRoot, host, "Controlled Documents");
            var fileA = Path.Combine(library, "Procedure", $"{number} rev A.pdf");
            Assert.True(File.Exists(fileA), fileA);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(fileA), 0, 4));
            var meta = JsonDocument.Parse(File.ReadAllText(fileA + LocalFolderSharePointClient.SidecarSuffix)).RootElement.GetProperty("fields");
            Assert.Equal(number, meta.GetProperty("DocumentNumber").GetString());
            Assert.Equal("A", meta.GetProperty("Revision").GetString());
            Assert.Equal("Effective", meta.GetProperty("DocumentStatus").GetString());
            Assert.Equal("Operations", meta.GetProperty("Department").GetString());
            Assert.Equal("published", (await c.Open("pshah", revA.Path)).One("[data-sp-state]").GetAttribute("data-sp-state"));
            var files = await admin.GoAsync($"/admin/integrations/sharepoint/log?view=files&q={Uri.EscapeDataString(number)}");
            files.ExpectText($"{number} rev A.pdf");

            // Revision B replaces A in the library: A's file is deleted per the rule (SAF-021).
            var urlB = await c.MinorRevisionAsync(fx, revA.Path);
            await (await admin.GoAsync("/admin/integrations/sharepoint/log")).Form("form[action*='handler=Process']").SubmitAsync();
            Assert.True(File.Exists(Path.Combine(library, "Procedure", $"{number} rev B.pdf")));
            Assert.False(File.Exists(fileA), "Revision A's file should have been withdrawn.");
            Assert.Equal("withdrawn", (await c.Open("pshah", revA.Path)).One("[data-sp-state]").GetAttribute("data-sp-state"));
            Assert.Equal("published", (await c.Open("pshah", urlB)).One("[data-sp-state]").GetAttribute("data-sp-state"));
            files = await admin.GoAsync($"/admin/integrations/sharepoint/log?view=files&status=all&q={Uri.EscapeDataString(number)}");
            files.ExpectText($"{number} rev B.pdf").ExpectText($"{number} rev A.pdf").ExpectText("Removed");
            var queueTab = await admin.GoAsync($"/admin/integrations/sharepoint/log?status=succeeded&q={Uri.EscapeDataString(number)}");
            queueTab.ExpectText(number);
        }
        finally
        {
            // Switch the rule off again from the overview, so later tests' releases aren't published.
            if (ruleId is { } id)
            {
                var index = await admin.GoAsync("/admin/integrations/sharepoint");
                var off = await index.FormOf($"form[action*='handler=Toggle'] input[name=id][value='{id}']").SubmitAsync();
                Assert.Equal("true", off.FormOf($"form[action*='handler=Toggle'] input[name=id][value='{id}']").Values("enabled").Single());
            }
        }
    }
}
