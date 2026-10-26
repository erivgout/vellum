using System.Net;
using Vellum.Engine.Records;

namespace Vellum.Tests.E2e;

/// <summary>
/// Document Control end to end through the pages: the revision cycle (change request → revision B → release → A superseded),
/// workflow guard rails, periodic review and obsolescence, the review schedule and the application's views.
/// </summary>
[Collection("vellum")]
public class DocumentLifecycleE2eTests(VellumFixture fx)
{
    [Fact] // DOC-090..093, DOC-053/054, DOC-016/017, INV-100, INV-103, SAF-043
    public async Task A_change_request_creates_revision_B_which_supersedes_A_on_release()
    {
        using var c = new Cast(fx);
        var title = $"Label reconciliation {E2e.Tag()}";
        var revA = await c.EffectiveSopAsync(fx, title);
        var urlA = revA.Path;
        var number = revA.Number();

        // Another document refers to revision A.
        var referring = await (await c.As("jlee")).CreateAsync("document", f => f
            .Pick("f.doc_type", "work_instruction").Set("f.title", $"Refers to {title}").Set("f.owner", fx.Uid("sortiz").ToString())
            .Set("f.reviewers", fx.Uids("dokafor", "tbrooks")).Set("f.related_documents", revA.RecordId.ToString()));

        // The owner raises a change request from the effective revision's Revision control menu.
        var sortizA = await c.Open("sortiz", urlA);
        Assert.Contains("request_change", sortizA.Actions());
        var cr = await sortizA.ActionAsync("request_change", f => f
            .Set("a.summary", "Add a second-person label check")
            .Set("a.reason", "Audit finding IA-2026-07: labels were reconciled by one person.")
            .Set("a.assign_to", fx.Uid("jlee").ToString()));
        Assert.StartsWith("Change request CR-", cr.Toast);
        var crNumber = cr.Number();
        Assert.Equal("Requested", cr.Phase());
        cr.ExpectText(number);
        var crUrl = cr.Path;

        // Revision A stays effective and says a change is pending; a second request is refused (INV-103).
        var a = await c.Open("sortiz", urlA);
        Assert.True(a.IsEffective());
        Assert.Contains(crNumber, a.Banner("Change requested").Text);
        Assert.DoesNotContain("request_change", a.Actions());
        Assert.Contains(a.DisabledActions(), x => x.Key.StartsWith("Request change") && x.Value.Contains(crNumber) && x.Value.Contains("INV-103"));

        // The approver accepts with an e-signature; a wrong password changes nothing.
        var bad = await (await c.Open("sortiz", crUrl)).RouteAsync("Accept: create new revision", password: "not-my-password");
        Assert.NotEmpty(bad.RouteErrors());
        Assert.Equal("Requested", (await c.Open("sortiz", crUrl)).Phase());
        cr = await (await c.Open("sortiz", crUrl)).RouteOkAsync("Accept: create new revision");
        Assert.Equal("Accepted", cr.Phase());
        Assert.Contains(cr.Comments(), t => t.Contains($"{number} rev B in Draft") && t.Contains(c.Name("jlee")));

        // Revision B is a Draft assigned to jlee, reachable from revision A's banner, with the change request's reason.
        a = await c.Open("jlee", urlA);
        var inProgress = a.Banner("Revision B in progress");
        Assert.NotNull(inProgress.Href);
        var urlB = inProgress.Href!;
        var b = await c.Open("jlee", urlB);
        Assert.Equal(number, b.Number());
        Assert.Equal("Draft", b.Phase());
        b.ExpectText("Rev B");
        Assert.StartsWith("Draft of revision B", b.Banners()[0]);
        Assert.Equal("Audit finding IA-2026-07: labels were reconciled by one person.", b.FieldValue("revision_reason"));
        (await c.Open("jlee", "/work")).ExpectText(number);

        // The training decision isn't carried over: it has to be made again for B (DOC-033).
        var refused = await b.RouteAsync("Submit to Document Control");
        Assert.Contains(refused.RouteErrors(), e => e.Contains("training decision"));
        b = await (await c.Open("jlee", urlB)).SaveAsync(f => f.Pick("f.training_required", "no").Set("f.training_justification", "Adds one check to an existing step."));
        Assert.Equal("Saved.", b.Toast);

        await c.ToAwaitingReleaseAsync(urlB);
        b = await c.ReleaseAsync(urlB);
        b.ExpectText("Rev B");

        // Revision A is superseded: archived, read-only, pointing at B; the history lists both with their approvals.
        a = await c.Open("pshah", urlA);
        Assert.False(a.IsEffective());
        Assert.NotNull(a.Q(".vl-record-id .vl-chip-archived"));
        var superseded = a.Banner("Superseded: revision B is current");
        Assert.Equal(urlB, superseded.Href);
        a.ExpectText("This revision is archived and can't be used for operational purposes");
        Assert.Null(a.Q("[data-save-button]"));
        Assert.Empty(a.RouteOptions());
        Assert.DoesNotContain("request_change", a.Actions());

        var history = (await c.Open("pshah", urlB)).RevisionHistory();
        Assert.Equal(["B", "A"], history.Select(h => h.Revision));
        Assert.True(history[0].Current);
        Assert.Contains("Effective", history[0].Status);
        Assert.Contains(crNumber, history[0].Related);
        Assert.Contains("Audit finding IA-2026-07", history[0].Reason);
        Assert.Contains(c.Name("pshah"), history[0].Approvals);
        Assert.Contains("Superseded", history[1].Status);
        Assert.Equal("New procedure written for the end-to-end test.", history[1].Reason);
        Assert.Equal(urlA, history[1].Href);

        // The change request travelled to the archive with revision A (DOC-053).
        cr = await c.Open("sortiz", crUrl);
        Assert.Null(cr.Q("[data-save-button]"));

        // Effective views list only B; the archive lists A.
        var pshah = await c.As("pshah");
        var effective = (await pshah.DocumentViewAsync("effective", number)).RowsWith(number);
        Assert.Single(effective);
        Assert.Contains(" B ", $" {effective[0]} ");
        var archive = (await pshah.DocumentViewAsync("archive_superseded", number)).RowsWith(number);
        Assert.Single(archive);
        Assert.Contains(" A ", $" {archive[0]} ");

        // A production operator finds the current revision; opening A tells them to use B.
        var nflores = await c.As("nflores");
        var search = await nflores.GoAsync($"/search?q={Uri.EscapeDataString(number)}");
        search.ExpectText("rev B").ExpectNoText("rev A");
        var old = await nflores.GoAsync(urlA);
        Assert.Equal(urlB, old.Banner("Superseded").Href);
        (await nflores.GoAsync(urlB)).ExpectText(title);

        // Links to revision A now lead to the current revision (DOC-054).
        var link = (await c.Open("tbrooks", referring.Path)).One("[data-ro-field='related_documents'] a[href]");
        Assert.Equal(urlB, link.GetAttribute("href"));
        Assert.Contains($"{number} rev B", link.TextContent);
    }

    [Fact] // DOC-031: the minor-change shortcut needs the flag, a comment and an approved document
    public async Task A_minor_change_goes_from_draft_straight_to_awaiting_release()
    {
        using var c = new Cast(fx);

        // A brand-new document can't take the shortcut, even when flagged.
        var fresh = await DocumentE2eTests.NewSopAsync(fx, await c.As("jlee"), $"Minor on new {E2e.Tag()}");
        fresh = await fresh.SaveAsync(f => f.Pick("f.minor_change", "yes"));
        var refusedNew = await fresh.RouteAsync("Minor change: send to release", comment: "Typo fix");
        Assert.Contains(refusedNew.RouteErrors(), e => e.Contains("only for revisions") && e.Contains("DOC-031"));
        Assert.Equal("Draft", (await c.Open("jlee", fresh.Path)).Phase());

        var revA = await c.EffectiveSopAsync(fx, $"Gowning sequence {E2e.Tag()}");
        var cr = await (await c.Open("pshah", revA.Path)).ActionAsync("request_change", f => f
            .Set("a.summary", "Fix a typo in step 4").Set("a.reason", "Typo in step 4.").Set("a.assign_to", fx.Uid("jlee").ToString()));
        await (await c.Open("sortiz", cr.Path)).RouteOkAsync("Accept: create new revision");
        var urlB = (await c.Open("jlee", revA.Path)).Banner("Revision B in progress").Href!;

        var b = await c.Open("jlee", urlB);
        Assert.DoesNotContain("Minor change: send to release", b.RouteOptions());
        b = await b.SaveAsync(f => f.Pick("f.minor_change", "yes").Pick("f.training_required", "no").Set("f.training_justification", "Typo only."));
        Assert.Contains("Minor change: send to release", b.RouteOptions());
        var noComment = await b.RouteAsync("Minor change: send to release");
        Assert.Contains(noComment.RouteErrors(), e => e.Contains("comment"));

        b = await (await c.Open("jlee", urlB)).RouteOkAsync("Minor change: send to release", comment: "Typo fix in step 4.");
        Assert.Equal("Awaiting Release", b.Phase()); // the author still reads their revision once it leaves Draft (DOC-080)
        b = await c.ReleaseAsync(urlB);

        // Only the release was signed on B; the flag is in the audit trail (DOC-031).
        var history = b.RevisionHistory();
        Assert.Equal("B", history[0].Revision);
        Assert.Contains(c.Name("pshah"), history[0].Approvals);
        Assert.DoesNotContain(c.Name("dokafor"), history[0].Approvals);
        Assert.Contains(b.FieldChanges("Minor or editorial change?"), t => t.Contains("Yes"));
        Assert.NotNull((await c.Open("pshah", revA.Path)).Q(".vl-record-id .vl-chip-archived"));
    }

    [Fact] // back routes need a comment (DOC-030 "Return to author")
    public async Task Document_Control_returns_a_draft_to_the_author_with_a_comment()
    {
        using var c = new Cast(fx);
        var draft = await DocumentE2eTests.NewSopAsync(fx, await c.As("jlee"), $"Returned SOP {E2e.Tag()}");
        var url = draft.Path;
        await draft.RouteOkAsync("Submit to Document Control");

        var pshah = await c.Open("pshah", url);
        Assert.Contains("Return to author", pshah.RouteOptions());
        var noComment = await pshah.RouteAsync("Return to author");
        Assert.Contains(noComment.RouteErrors(), e => e.Contains("Add a comment"));
        Assert.Equal("Document Control Review", (await c.Open("pshah", url)).Phase());

        var back = await (await c.Open("pshah", url)).RouteOkAsync("Return to author", comment: "Use the current SOP template, then resubmit.");
        Assert.Equal("Draft", back.Phase());

        var jlee = await c.Open("jlee", url);
        Assert.Contains(jlee.Comments(), t => t.Contains("Use the current SOP template"));
        Assert.NotNull(jlee.Q("[data-save-button]"));
        (await c.Open("jlee", "/work")).ExpectText(draft.Number());
        Assert.Equal("Document Control Review", (await jlee.RouteOkAsync("Submit to Document Control")).Phase());
    }

    [Fact] // DOC-030 (Draft is the author's), DOC-091/093: a returned revision goes back to the author it was assigned to
    public async Task Returning_a_new_revision_gives_it_back_to_its_author_not_the_requester()
    {
        using var c = new Cast(fx);
        var revA = await c.EffectiveSopAsync(fx, $"Returned revision {E2e.Tag()}");
        // Document Control requests the change (so it is revision B's creator, DOC-093); jlee is assigned to write it.
        var cr = await (await c.Open("pshah", revA.Path)).ActionAsync("request_change", f => f
            .Set("a.summary", "Update the flow chart").Set("a.reason", "New packaging line.").Set("a.assign_to", fx.Uid("jlee").ToString()));
        await (await c.Open("sortiz", cr.Path)).RouteOkAsync("Accept: create new revision");
        var urlB = (await c.Open("jlee", revA.Path)).Banner("Revision B in progress").Href!;
        var b = await (await c.Open("jlee", urlB)).SaveAsync(f => f.Pick("f.training_required", "no").Set("f.training_justification", "Diagram only."));
        await b.RouteOkAsync("Submit to Document Control");

        var returned = await (await c.Open("pshah", urlB)).RouteOkAsync("Return to author", comment: "The flow chart is missing step 3.");
        Assert.Equal("Draft", returned.Phase());
        Assert.Contains(c.Name("jlee"), returned.One(".vl-current").TextContent);
        Assert.DoesNotContain(c.Name("pshah"), returned.One(".vl-current").TextContent);
        Assert.Empty((await c.Open("pshah", urlB)).RouteOptions());
        var jlee = await c.Open("jlee", urlB);
        Assert.Contains("Submit to Document Control", jlee.RouteOptions());
        (await c.Open("jlee", "/work")).ExpectText(revA.Number());
    }

    [Fact] // DOC-034, INV-031
    public async Task Voiding_a_draft_needs_a_reason_and_leaves_it_read_only()
    {
        using var c = new Cast(fx);
        var draft = await DocumentE2eTests.NewSopAsync(fx, await c.As("jlee"), $"Abandoned SOP {E2e.Tag()}");
        var url = draft.Path;
        var number = draft.Number();
        var voidLabel = draft.RouteOptions().Single(o => o.StartsWith("Void"));

        var noReason = await draft.RouteAsync(voidLabel);
        Assert.Contains(noReason.RouteErrors(), e => e.Contains("reason for voiding"));
        Assert.Equal("Draft", (await c.Open("jlee", url)).Phase());

        var stale = await c.Open("jlee", url);
        var voided = await (await c.Open("jlee", url)).RouteOkAsync(voidLabel, comment: "Superseded by a corporate procedure.");
        Assert.Equal("Voided", voided.Phase());
        Assert.Contains("Voided", voided.Banners());
        Assert.Null(voided.Q("[data-save-button]"));
        Assert.Empty(voided.RouteOptions());
        Assert.Null(voided.Q("[name='f.title']"));
        Assert.Equal(draft.Title(), voided.FieldText("title"));
        Assert.Contains(voided.Comments(), t => t.Contains("Superseded by a corporate procedure."));

        // Saving from a page opened before the void changes nothing.
        var late = await stale.SaveAsync(f => f.Set("f.title", "Edited after voiding"));
        Assert.NotEqual("Saved.", late.Toast);
        Assert.Equal(draft.Title(), (await c.Open("jlee", url)).Title());

        var view = await (await c.As("pshah")).DocumentViewAsync("voided", number);
        Assert.Single(view.RowsWith(number));
    }

    [Fact] // PLT routing: only the current assignee is offered (and may use) the routes
    public async Task Only_the_current_assignee_can_send_the_document_on()
    {
        using var c = new Cast(fx);
        var draft = await DocumentE2eTests.NewSopAsync(fx, await c.As("jlee"), $"Assignee check {E2e.Tag()}");
        var url = draft.Path;
        await draft.RouteOkAsync("Submit to Document Control");
        await (await c.Open("pshah", url)).RouteOkAsync("Format checked: send for initial approval");

        // Initial Approval belongs to the owner. Document Control (a manager), the author and a reviewer get no Send button.
        foreach (var user in new[] { "pshah", "jlee", "dokafor" })
        {
            var page = await c.Open(user, url);
            Assert.Empty(page.RouteOptions());
            Assert.Null(page.Q("#route-dialog"));
        }
        var owner = await c.Open("sortiz", url);
        Assert.Equal(["Approve: send to reviewers", "Return to Document Control"], owner.RouteOptions().Where(o => !o.StartsWith("Void")));

        // A crafted post of the owner's route by Document Control is refused, and says why.
        var approve = owner.One("[data-route-option]").GetAttribute("data-route-option")!;
        var pshah = await c.Open("pshah", url);
        var forged = await pshah.Form("#record-form").Set("routeId", approve).Set("password", TestHelpers.Password).Set("certify", "true")
            .SubmitToAsync($"{url}?handler=Route");
        Assert.Contains(forged.Errors, e => e.Contains("Only the current assignee"));
        Assert.Equal("Initial Approval", (await c.Open("pshah", url)).Phase());

        // In Review the owner can't sign for the reviewers; a reviewer who has signed has nothing left to send.
        await (await c.Open("sortiz", url)).RouteOkAsync("Approve: send to reviewers");
        Assert.Empty((await c.Open("sortiz", url)).RouteOptions());
        var dokafor = await c.Open("dokafor", url);
        var staleReview = await c.Open("dokafor", url);
        await dokafor.RouteOkAsync("Reviewed");
        Assert.Empty((await c.Open("dokafor", url)).RouteOptions());

        // Sending again from the page opened before signing explains why nothing happened.
        var again = await staleReview.RouteAsync("Reviewed");
        Assert.Contains(again.Errors, e => e.Contains("Someone else changed this record") || e.Contains("current assignee"));
        Assert.Equal("Review", again.Phase());
        Assert.Contains((await c.Open("tbrooks", url)).RouteOptions(), o => o.StartsWith("Reviewed"));
    }

    [Fact] // SAF-044: reviewers must be independent of the author
    public async Task An_author_cannot_review_their_own_document()
    {
        using var c = new Cast(fx);
        var jlee = await c.As("jlee");
        var draft = await jlee.CreateAsync("document", f => f
            .Pick("f.doc_type", "procedure").Set("f.title", $"Self-review {E2e.Tag()}")
            .Set("f.owner", fx.Uid("sortiz").ToString()).Set("f.reviewers", fx.Uids("jlee", "tbrooks"))
            .Set("f.revision_reason", "New.").Pick("f.training_required", "no").Set("f.training_justification", "None.")
            .Attach("file.primary_file", "self.pdf", E2e.Pdf(), "application/pdf"));
        var refused = await draft.RouteAsync("Submit to Document Control");
        Assert.Contains(refused.RouteErrors(), e => e.Contains("SAF-044") && e.Contains(c.Name("jlee")));
        Assert.Equal("Draft", (await jlee.GoAsync(draft.Path)).Phase());

        var fixedDraft = await (await jlee.GoAsync(draft.Path)).SaveAsync(f => f.Set("f.reviewers", fx.Uids("dokafor", "tbrooks")));
        Assert.Equal("Document Control Review", (await fixedDraft.RouteOkAsync("Submit to Document Control")).Phase());
    }

    [Fact] // SAF-044 for revisions: whoever is assigned to write revision B is its author
    public async Task The_author_of_a_new_revision_cannot_be_one_of_its_reviewers()
    {
        using var c = new Cast(fx);
        var revA = await c.EffectiveSopAsync(fx, $"Revision author {E2e.Tag()}");
        // Revision B is assigned to dokafor, who is also one of the document's reviewers.
        var cr = await (await c.Open("sortiz", revA.Path)).ActionAsync("request_change", f => f
            .Set("a.summary", "Clarify step 2").Set("a.reason", "Operators asked for clarification.").Set("a.assign_to", fx.Uid("dokafor").ToString()));
        await (await c.Open("sortiz", cr.Path)).RouteOkAsync("Accept: create new revision");
        var urlB = (await c.Open("dokafor", revA.Path)).Banner("Revision B in progress").Href!;
        var b = await (await c.Open("dokafor", urlB)).SaveAsync(f => f.Pick("f.training_required", "no").Set("f.training_justification", "Clarification only."));
        var refused = await b.RouteAsync("Submit to Document Control");
        Assert.Contains(refused.RouteErrors(), e => e.Contains("SAF-044") && e.Contains(c.Name("dokafor")));
        Assert.Equal("Draft", (await c.Open("dokafor", urlB)).Phase());
    }

    [Fact] // DOC-073..076, DOC-071, DOC-120
    public async Task A_periodic_review_is_started_and_completed_from_the_document()
    {
        using var c = new Cast(fx);
        var title = $"Reviewed SOP {E2e.Tag()}";
        var doc = await c.EffectiveSopAsync(fx, title);
        var url = doc.Path;
        var number = doc.Number();

        // The schedule shows the next review 24 months after release (Procedure profile).
        var schedule = await (await c.As("sortiz")).GoAsync($"/documents/reviews?q={Uri.EscapeDataString(number)}");
        var row = Assert.Single(schedule.All("tbody tr[data-href]"));
        Assert.Contains(DocumentSteps.Day(BusinessCalendar.Today.AddMonths(24)), Page.Squash(row.TextContent));
        Assert.Contains("Every 24 months", row.TextContent);

        var started = await (await c.Open("sortiz", url)).ActionAsync("start_review");
        Assert.StartsWith("Periodic review PR-", started.Toast);
        Assert.Equal("In Review", started.Phase());
        var prUrl = started.Path;
        var pr = started.Number();
        Assert.Contains(pr, (await c.Open("sortiz", url)).Banner("Periodic review in progress").Text);
        schedule = await (await c.As("sortiz")).GoAsync($"/documents/reviews?q={Uri.EscapeDataString(number)}");
        schedule.ExpectText($"{pr} in progress");
        (await c.Open("sortiz", "/work")).ExpectText(pr);

        // Outcome and comments are required to complete it.
        var incomplete = await (await c.Open("sortiz", prUrl)).RouteAsync("Complete periodic review");
        Assert.Contains(incomplete.Errors, e => e.Contains("Outcome"));
        Assert.Equal("In Review", (await c.Open("sortiz", prUrl)).Phase());

        var next = BusinessCalendar.Today.AddMonths(12);
        var done = await (await c.Open("sortiz", prUrl)).RouteOkAsync("Complete periodic review", fill: f => f
            .Pick("f.outcome", "no_change").Set("f.review_comments", "Still accurate and followed on the line.")
            .Set("f.next_review_date", next.ToString("yyyy-MM-dd")));
        Assert.Equal("Closed", done.Phase());

        var after = await c.Open("sortiz", url);
        Assert.Contains(after.Comments(), t => t.Contains($"Periodic review {pr} completed") && t.Contains("No change needed"));
        Assert.DoesNotContain(after.Banners(), t => t.StartsWith("Periodic review in progress"));
        Assert.Contains(pr, after.RevisionHistory()[0].Related);
        schedule = await (await c.As("sortiz")).GoAsync($"/documents/reviews?q={Uri.EscapeDataString(number)}");
        row = Assert.Single(schedule.All("tbody tr[data-href]"));
        Assert.Contains(DocumentSteps.Day(next), Page.Squash(row.TextContent));
        Assert.Contains(DocumentSteps.Day(BusinessCalendar.Today), Page.Squash(row.TextContent));
        Assert.Contains("start_review", (await c.Open("sortiz", url)).Actions());
    }

    [Fact] // DOC-101..103, INV-103
    public async Task An_accepted_obsolete_request_withdraws_the_document_from_every_effective_view()
    {
        using var c = new Cast(fx);
        var title = $"Retired SOP {E2e.Tag()}";
        var doc = await c.EffectiveSopAsync(fx, title);
        var url = doc.Path;
        var number = doc.Number();

        // Only Document Control and managers may request obsolescence.
        Assert.DoesNotContain("request_obsolete", (await c.Open("jlee", url)).Actions());
        var or = await (await c.Open("pshah", url)).ActionAsync("request_obsolete", f => f.Set("a.reason", "The line moved to the Galway site."));
        Assert.StartsWith("Obsolete request OR-", or.Toast);
        Assert.Equal("New", or.Phase());
        var orUrl = or.Path;
        var orNumber = or.Number();
        var pending = await c.Open("pshah", url);
        Assert.Contains(orNumber, pending.Banner("Obsoletion in progress").Text);
        Assert.Contains(pending.DisabledActions(), x => x.Key.StartsWith("Request change") && x.Value.Contains(orNumber));

        or = await (await c.Open("pshah", orUrl)).RouteOkAsync("Send for approval");
        Assert.Equal("Approval", or.Phase());
        (await c.Open("dokafor", "/work")).ExpectText(orNumber);
        var accepted = await (await c.Open("sortiz", orUrl)).RouteOkAsync("Accept: make obsolete");
        Assert.Equal("Accepted", accepted.Phase());

        var obsolete = await c.Open("pshah", url);
        Assert.Equal("Obsolete", obsolete.Phase());
        Assert.False(obsolete.IsEffective());
        Assert.Contains(orNumber, obsolete.Banner("Obsolete: withdrawn from use").Text);
        Assert.Empty(obsolete.RouteOptions());
        Assert.Null(obsolete.Q("[data-save-button]"));
        Assert.Contains(obsolete.Actions(), a => a == "restore");

        var pshah = await c.As("pshah");
        Assert.Empty((await pshah.DocumentViewAsync("effective", number)).RowsWith(number));
        Assert.Empty((await pshah.DocumentViewAsync("effective_procedure", number)).RowsWith(number));
        Assert.Single((await pshah.DocumentViewAsync("archive_obsolete", number)).RowsWith(number));
        (await pshah.GoAsync($"/documents/reviews?q={Uri.EscapeDataString(number)}")).ExpectText("Nothing due");
        (await (await c.As("nflores")).GoAsync($"/search?q={Uri.EscapeDataString(number)}")).ExpectText("No results for");
    }

    [Fact] // DOC-080 views and the toolbar filters of the Document Control application
    public async Task The_document_views_filter_by_type_and_search()
    {
        using var c = new Cast(fx);
        var tag = E2e.Tag();
        var sop = await c.EffectiveSopAsync(fx, $"View filter SOP {tag}");
        var jlee = await c.As("jlee");
        var wi = await jlee.CreateAsync("document", f => f
            .Pick("f.doc_type", "work_instruction").Set("f.title", $"View filter WI {tag}").Set("f.owner", fx.Uid("sortiz").ToString())
            .Set("f.reviewers", fx.Uids("dokafor", "tbrooks")).Set("f.revision_reason", "New.")
            .Pick("f.training_required", "no").Set("f.training_justification", "None.")
            .Attach("file.primary_file", "wi.pdf", E2e.Pdf(), "application/pdf"));
        Assert.StartsWith("WI-", wi.Number());

        var pshah = await c.As("pshah");
        var app = await pshah.GoAsync("/apps/documents");
        app.ExpectText("All effective documents").ExpectText("Superseded revisions").ExpectText("Pending documents");

        var effective = await pshah.DocumentViewAsync("effective", tag);
        Assert.Single(effective.RowsWith(sop.Number()));
        Assert.Empty(effective.RowsWith(wi.Number()));
        var procedures = await effective.Form("form.vl-toolbar").Pick("f.doc_type", "procedure").SubmitAsync();
        Assert.Single(procedures.RowsWith(sop.Number()));
        var wis = await procedures.Form("form.vl-toolbar").Pick("f.doc_type", "work_instruction").SubmitAsync();
        Assert.Empty(wis.RowsWith(sop.Number()));
        wis.ExpectText("No records match");

        var drafts = await pshah.DocumentViewAsync("drafts", tag);
        Assert.Single(drafts.RowsWith(wi.Number()));
        Assert.Empty(drafts.RowsWith(sop.Number()));
        var pendingDocs = await pshah.DocumentViewAsync("pending", tag);
        Assert.Single(pendingDocs.RowsWith(wi.Number()));

        // A reader's effective view has the SOP but never the draft.
        var reader = await (await c.As("nflores")).DocumentViewAsync("effective", tag);
        Assert.Single(reader.RowsWith(sop.Number()));
        var readerDrafts = await (await c.As("nflores")).DocumentViewAsync("drafts", tag);
        Assert.Empty(readerDrafts.RowsWith(wi.Number()));
    }

    [Fact] // INV-081, DOC-080: a link never reveals a draft to someone who can't open it
    public async Task A_link_to_a_draft_does_not_reveal_the_draft_to_readers()
    {
        using var c = new Cast(fx);
        var tag = E2e.Tag();
        var jlee = await c.As("jlee");
        var hidden = await DocumentE2eTests.NewSopAsync(fx, jlee, $"Unreleased recall plan {tag}");
        var doc = await jlee.CreateAsync("document", f => f
            .Pick("f.doc_type", "procedure").Set("f.title", $"Public SOP {tag}").Set("f.owner", fx.Uid("sortiz").ToString())
            .Set("f.reviewers", fx.Uids("dokafor", "tbrooks")).Pick("f.department", "operations").Set("f.revision_reason", "New.")
            .Pick("f.training_required", "no").Set("f.training_justification", "None.").Set("f.related_documents", hidden.RecordId.ToString())
            .Attach("file.primary_file", "public.pdf", E2e.Pdf(), "application/pdf"));
        await c.ToAwaitingReleaseAsync(doc.Path);
        await c.ReleaseAsync(doc.Path);

        // The author still sees the link; a production operator sees that a related record exists, but not which.
        Assert.Contains($"Unreleased recall plan {tag}", (await c.Open("jlee", doc.Path)).One("[data-ro-field='related_documents']").TextContent);
        var reader = await c.Open("nflores", doc.Path);
        var related = Page.Squash(reader.One("[data-ro-field='related_documents']").TextContent);
        Assert.Equal("Restricted record", related);
        Assert.DoesNotContain(hidden.Number(), related);
        Assert.Null(reader.Q($"a[href='{hidden.Path}']"));
    }

    [Fact] // DOC-080: a draft is visible only to the people involved
    public async Task A_draft_is_hidden_from_readers_until_it_is_released()
    {
        using var c = new Cast(fx);
        var draft = await DocumentE2eTests.NewSopAsync(fx, await c.As("jlee"), $"Hidden draft {E2e.Tag()}");
        await (await c.As("nflores")).GoAsync(draft.Path, HttpStatusCode.NotFound);
        Assert.StartsWith("Draft: not yet in force", (await c.Open("tbrooks", draft.Path)).Banners()[0]);
        (await (await c.As("nflores")).GoAsync($"/search?q={Uri.EscapeDataString(draft.Number())}")).ExpectText("No results for");
    }
}
