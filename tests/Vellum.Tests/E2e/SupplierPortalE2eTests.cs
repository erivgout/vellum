using System.Net;
using Vellum.Engine.Records;

namespace Vellum.Tests.E2e;

/// <summary>
/// Supplier quality and the supplier portal end to end through the web pages: qualifying and approving a supplier, inviting
/// a supplier contact (email, set-password link, sign-in), and the SCAR, certificate, questionnaire and scorecard round trips
/// between the internal pages and the portal, plus the portal's isolation rules (SEC-804, INV-088).
/// </summary>
[Collection("vellum")]
public class SupplierPortalE2eTests(VellumFixture fx)
{
    [Fact] // SUP-001, SUP-003, INV-089: approval needs the evidence, a Quality approver and an e-signature
    public async Task A_supplier_is_qualified_and_approved_by_a_quality_approver_with_a_signature()
    {
        var tag = E2e.Tag();
        using var cwu = await fx.SignedInAsync("cwu");
        var material = await cwu.ApprovedMaterialAsync("H-" + tag);
        var name = $"Northwind Plastics {tag}";
        var supplier = await cwu.NewSupplierAsync(name, "high", "Robin Ames", $"robin.{tag.ToLowerInvariant()}@northwind.example", material.RecordId);
        Assert.StartsWith("SUP-", supplier.Number());
        Assert.Equal("Prospective", supplier.FieldText("approval_status"));
        var url = supplier.Path;

        // The supplier quality engineer can't approve their own supplier: approval belongs to Quality (segregation of duties).
        Assert.Equal("Only Quality approvers can approve or disqualify suppliers.", supplier.DisabledAction("Approve supplier"));

        // A high-risk supplier needs a quality agreement and a valid certificate before full approval.
        using var sortiz = await fx.SignedInAsync("sortiz");
        var blocked = await (await sortiz.GoAsync(url)).ActionAsync("approve", f => f.Set("a.scope", "Molded housings"), password: TestHelpers.Password);
        Assert.Contains(blocked.Errors, e => e.Contains("need a quality agreement"));
        Assert.Contains(blocked.Errors, e => e.Contains("valid quality system certificate"));
        Assert.Equal("Prospective", (await sortiz.GoAsync(url)).FieldText("approval_status"));

        var page = await cwu.GoAsync(url);
        page = await page.SaveAsync(f => f
            .Pick("f.quality_agreement", "yes").Set("f.quality_agreement_on", SupplierSteps.Iso(BusinessCalendar.Today.AddDays(-30)))
            .AddRow("certifications", ("standard", page.RowOption("certifications", "standard", "iso_13485")), ("cert_number", "MD-" + tag),
                ("registrar", "BSI"), ("expires_on", SupplierSteps.Iso(BusinessCalendar.Today.AddYears(2)))));
        Assert.Equal("Saved.", page.Toast);
        Assert.Equal("Valid", page.FieldText("cert_status"));

        // A wrong password is refused and nothing changes.
        var wrong = await (await sortiz.GoAsync(url)).ActionAsync("approve", f => f.Set("a.scope", "Molded housings"), password: "not-my-password");
        Assert.NotEmpty(wrong.Errors);
        Assert.Equal("Prospective", (await sortiz.GoAsync(url)).FieldText("approval_status"));

        var approved = await sortiz.ApproveSupplierAsync(url);
        Assert.Equal(BusinessCalendar.Today.AddYears(2).ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), approved.FieldText("requalify_on")); // high risk: 2 years (SUP-005)
        approved.ExpectText("Approved supplier"); // the signature on the approval history tab

        // It's on the approved supplier list, and an approved material on its list can now be received.
        (await cwu.GoAsync("/apps/suppliers?view=asl")).ExpectText(name);
        (await cwu.GoAsync("/apps/suppliers?view=prospective")).ExpectNoText(name);
    }

    [Fact] // CAPA-011, SEC-804: a SCAR answered by the supplier in the portal, returned once, then accepted and verified
    public async Task A_scar_is_answered_in_the_supplier_portal_returned_for_changes_and_then_accepted()
    {
        using var cwu = await fx.SignedInAsync("cwu");
        using var sortiz = await fx.SignedInAsync("sortiz");
        var (supplierUrl, supplierId, supplierName, username) = await SupplierSteps.SupplierWithPortalContactAsync(fx, cwu, sortiz, "Dana Whitfield");

        // The contact signs in at the normal sign-in page and lands in their own portal, with nothing to do yet.
        using var dana = new Browser(fx);
        var home = await dana.SignInAsync(username);
        Assert.Equal("/portal", home.Url.AbsolutePath);
        home.ExpectText("You're all caught up").ExpectText(supplierName);

        // The SQE raises the SCAR; Quality approves issuing it with a signature.
        var subject = $"Sink marks on housing bosses {E2e.Tag()}";
        var scar = await cwu.CreateAsync("scar", f => f
            .Set("f.subject", subject).Set("f.description", "Sink marks on 14 of 80 housings, lot 5521, boss height below drawing minimum.")
            .Set("f.requirement", "DWG-200 rev C, note 4").Set("f.supplier_name", supplierName).Set("f.part_number", "200-114")
            .Set("f.lot_number", "5521").Set("f.quantity_affected", "14")
            .Set("f.containment_actions", "Quarantine all housings from lots 5519 to 5523 at your site and in transit.")
            .Set("f.response_due", SupplierSteps.Iso(BusinessCalendar.Today.AddDays(20))));
        var url = scar.Path;
        var number = scar.Number();
        scar = await scar.RouteOkAsync("Submit for approval");
        Assert.Equal("Initial Approval", scar.Phase());
        scar = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve and issue to supplier");
        Assert.Equal("Assign to Supplier", scar.Phase());

        // Shared with the supplier through the portal.
        scar = await (await cwu.GoAsync(url)).ActionAsync("portal-send", f => f.Set("a.supplier", supplierId.ToString())
            .Set("a.message", "Please contain the suspect lots and send us your 8D."));
        Assert.Contains("Sent to " + supplierName, scar.Toast);
        scar.ExpectText("Waiting for the supplier");

        // The contact sees it waiting for them, opens it and sees the problem, but none of the internal sections.
        home = await dana.GoAsync("/portal");
        home.ExpectText("needs 1 response from you").ExpectText(number).ExpectText(subject);
        var request = await dana.OpenPortalRequestAsync(number);
        request.ExpectText("Sink marks on 14 of 80 housings").ExpectText("Please contain the suspect lots").ExpectText("Action needed");
        Assert.Null(request.Q("[name='f.verification_method']"));
        Assert.Null(request.Q($"a[href^='/records/']"));
        var shareUrl = request.Path;

        // A draft can be saved half-done; submitting needs the required answers.
        var draft = await request.PortalForm().Set("f.sr_root_cause", "Hold pressure drifted low after a pump repair.").SubmitResponseAsync(submit: false);
        Assert.Equal("Draft saved. Submit when you're ready.", draft.Toast);
        var missing = await draft.PortalForm().SubmitResponseAsync();
        missing.ExpectText("Your response wasn't submitted");
        Assert.Contains(missing.Errors, e => e.Contains("Corrective action is required"));
        Assert.Contains(missing.Errors, e => e.Contains("Supplier containment actions is required")); // containment was asked for

        var submitted = await (await dana.GoAsync(shareUrl)).PortalForm()
            .Set("f.sr_containment", "Lots 5519–5523 quarantined; 2,400 housings sorted, 31 rejected.")
            .Set("f.sr_corrective_action", "Hold pressure interlock added to press 7.")
            .Set("f.sr_implementation_date", SupplierSteps.Iso(BusinessCalendar.Today.AddDays(-1)))
            .Set("f.sr_responder", "Dana Whitfield, Quality Manager")
            .Attach("file.sr_file", "8D-5521.pdf", E2e.Pdf("8D report"), "application/pdf")
            .SubmitResponseAsync();
        Assert.Contains("Your response was submitted", submitted.Toast);
        submitted.ExpectText("under review").ExpectText("8D-5521.pdf");
        Assert.Null(submitted.Q("form[data-portal-form]")); // locked while it's reviewed

        // Internally the response is in the SCAR's supplier response section, from the contact, and the SQE is told.
        var notes = await cwu.GoAsync("/notifications");
        notes.ExpectText($"{supplierName} responded to {number}");
        scar = await cwu.GoAsync(url);
        Assert.Equal("Hold pressure drifted low after a pump repair.", scar.FieldValue("sr_root_cause")?.Trim());
        scar.ExpectText("Supplier responded: review").ExpectText("8D-5521.pdf");

        // The SQE returns it with feedback; the contact sees the feedback and answers again.
        scar = await scar.ActionAsync("portal-return", comment: "Please add why the drift wasn't detected by the in-process check.");
        Assert.Equal("Returned to the supplier with your feedback.", scar.Toast);
        var returned = await dana.GoAsync(shareUrl);
        returned.ExpectText("asked for changes").ExpectText("why the drift wasn't detected").ExpectText("Changes requested");
        var again = await returned.PortalForm()
            .Set("f.sr_root_cause", "Hold pressure drifted low after a pump repair; the in-process check sampled only cavity 1.")
            .Set("f.sr_preventive_action", "In-process check now samples every cavity.")
            .SubmitResponseAsync();
        Assert.Contains("Your response was submitted", again.Toast);

        // Routing the SCAR on to verification accepts the response in the portal.
        scar = await cwu.GoAsync(url);
        var toVerify = await scar.RouteAsync("Supplier responded: verify");
        Assert.Contains(toVerify.Errors.Concat(toVerify.RouteErrors()), e => e.Contains("Verified by is required"));
        scar = await (await cwu.GoAsync(url)).SaveAsync(f => f.Set("f.scar_verifier", fx.Uid("jlee").ToString()));
        scar = await scar.RouteOkAsync("Supplier responded: verify");
        Assert.Equal("Verification & Effectiveness", scar.Phase());
        var accepted = await dana.GoAsync(shareUrl);
        accepted.ExpectText("Accepted");
        Assert.Null(accepted.Q("form[data-portal-form]"));

        // The verifier signs it off.
        using var jlee = await fx.SignedInAsync("jlee");
        (await jlee.GoAsync("/work")).ExpectText(number);
        scar = await (await jlee.GoAsync(url)).SaveAsync(f => f
            .Set("f.verification_method", "Receiving inspection of the next three lots at 100%.")
            .Pick("f.scar_result", "effective").Set("f.verification_evidence", "Lots 5530–5532: 0 of 240 housings with sink marks."));
        scar = await scar.RouteOkAsync("Verified effective: complete");
        Assert.Equal("Completed", scar.Phase());

        // The supplier's own record of it stays in the portal, closed.
        (await dana.GoAsync("/portal")).ExpectText("Recently closed").ExpectText(number);
        (await cwu.GoAsync(supplierUrl)).ExpectText(number).ExpectText("Accepted"); // the profile's Supplier portal tab
    }

    [Fact] // SUP-004, SUP-003: a certificate requested in the portal, uploaded by the supplier, accepted and filed on the profile
    public async Task A_requested_certificate_is_uploaded_in_the_portal_and_filed_on_the_supplier_profile()
    {
        using var cwu = await fx.SignedInAsync("cwu");
        using var sortiz = await fx.SignedInAsync("sortiz");
        var (supplierUrl, _, _, username) = await SupplierSteps.SupplierWithPortalContactAsync(fx, cwu, sortiz, "Greg Halvorsen");
        var certNo = "MD-" + E2e.Tag();

        var request = await (await cwu.GoAsync(supplierUrl)).ActionAsync("portal-request-doc", f => f
            .Pick("a.doc_type", "iso_cert").Set("a.subject", "Renewed ISO 13485 certificate").Pick("a.standard", "iso_13485")
            .Set("a.instructions", "Upload the full certificate, including the scope page."));
        Assert.Contains("sent to the supplier through the portal", request.Toast);
        Assert.Equal("With Supplier", request.Phase());
        var number = request.Number();
        var url = request.Path;
        (await cwu.GoAsync(supplierUrl)).ExpectText(number).ExpectText("Waiting for the supplier");

        using var greg = new Browser(fx);
        await greg.SignInAsync(username);
        var page = await greg.OpenPortalRequestAsync(number);
        page.ExpectText("Upload the full certificate, including the scope page.").ExpectText("Quality system certificate");

        // The document itself and (for a certificate) its expiry date are needed to submit.
        var refused = await page.PortalForm().Set("f.doc_number", certNo).SubmitResponseAsync();
        Assert.Contains(refused.Errors, e => e.Contains("Upload the document before submitting"));
        Assert.Contains(refused.Errors, e => e.Contains("Expires on is required"));
        Assert.Equal(certNo, refused.PortalForm().Values("f.doc_number").Single()); // what they typed is kept

        var expires = BusinessCalendar.Today.AddYears(3);
        var sent = await (await greg.GoAsync(page.Path)).PortalForm()
            .Set("f.doc_number", certNo).Set("f.issuer", "BSI").Set("f.issued_on", SupplierSteps.Iso(BusinessCalendar.Today.AddDays(-3)))
            .Set("f.expires_on", SupplierSteps.Iso(expires))
            .Attach("file.doc_file", "ISO13485-certificate.pdf", E2e.Pdf("ISO 13485 certificate"), "application/pdf")
            .SubmitResponseAsync();
        Assert.Contains("Your response was submitted", sent.Toast);
        sent.ExpectText("under review");

        // The supplier's submit sent it to Review, in the requester's work queue.
        (await cwu.GoAsync("/work")).ExpectText(number);
        var sdr = await cwu.GoAsync(url);
        Assert.Equal("Review", sdr.Phase());
        sdr.ExpectText("ISO13485-certificate.pdf").ExpectText(certNo);

        // "Accept document" is offered once the review result says accepted.
        Assert.DoesNotContain("Accept document", sdr.RouteOptions());
        sdr = await sdr.SaveAsync(f => f.Pick("f.review_result", "accepted").Set("f.review_notes", "Scope covers molding of class II components."));
        sdr = await sdr.RouteOkAsync("Accept document");
        Assert.Equal("Accepted", sdr.Phase());

        // The certificate and a copy of the file are on the supplier profile now, and the portal shows it accepted.
        var profile = await cwu.GoAsync(supplierUrl);
        Assert.Contains(certNo, profile.All("[name^='r.certifications.'][name$='.cert_number']").Select(e => e.GetAttribute("value")));
        profile.ExpectText("ISO13485-certificate.pdf");
        Assert.Equal(expires.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), profile.FieldText("next_cert_expiry"));
        (await greg.GoAsync(page.Path)).ExpectText("accepted your response");
    }

    [Fact] // SUP-005: a self-assessment questionnaire answered in the portal; reviewer notes stay internal
    public async Task A_self_assessment_questionnaire_is_answered_in_the_portal_and_reviewed()
    {
        using var cwu = await fx.SignedInAsync("cwu");
        using var sortiz = await fx.SignedInAsync("sortiz");
        var (supplierUrl, _, _, username) = await SupplierSteps.SupplierWithPortalContactAsync(fx, cwu, sortiz, "Kim Osei");
        var title = $"Self-assessment {E2e.Tag()}";

        var sq = await (await cwu.GoAsync(supplierUrl)).ActionAsync("portal-questionnaire", f => f.Set("a.subject", title));
        Assert.Contains("created in Draft", sq.Toast);
        Assert.Equal("Draft", sq.Phase());
        var number = sq.Number();
        var url = sq.Path;
        sq = await sq.RouteOkAsync("Send to supplier portal");
        Assert.Equal("With Supplier", sq.Phase());

        using var kim = new Browser(fx);
        await kim.SignInAsync(username);
        var page = await kim.OpenPortalRequestAsync(number);
        page.ExpectText("Does your company hold a current ISO 9001 or ISO 13485 certificate?");
        var rows = page.All("article.vp-row").Count;
        Assert.Equal(8, rows);

        // Answer only the first question: the other required ones are listed.
        var names = page.PortalForm().Element.QuerySelectorAll("[name^='r.'][name$='.answer']").Select(e => e.GetAttribute("name")!).Distinct().ToList();
        var partial = await page.PortalForm().Set(names[0], "Yes").SubmitResponseAsync();
        Assert.Contains(partial.Errors, e => e.Contains("Question 2: Supplier answer is required"));
        Assert.DoesNotContain(partial.Errors, e => e.Contains("Question 7")); // optional

        var form = (await kim.GoAsync(page.Path)).PortalForm();
        string[] answers = ["Yes", "No", "Change requests go through our ECR process; customers are told 90 days ahead.",
            "Annual audits and monthly scorecards.", "Yes", "Quarantine, MRB and notice to the customer within 24 hours.", "Yes", "None."];
        for (var i = 0; i < names.Count; i++) form.Set(names[i], answers[i]);
        var done = await form.Set("f.supplier_comments", "Certificate attached.")
            .Attach("file.sq_file", "iso-certificate.pdf", E2e.Pdf(), "application/pdf").SubmitResponseAsync();
        Assert.Contains("Your response was submitted", done.Toast);

        // Review: an internal note on an answer, then the review result.
        var review = await cwu.GoAsync(url);
        Assert.Equal("Review", review.Phase());
        Assert.StartsWith("Change requests go through our ECR process", review.RecordForm().Values("r.questions.2.answer").Single());
        var secret = "Follow up at the next audit " + E2e.Tag();
        review = await review.SaveAsync(f => f.Set("r.questions.2.q_note", secret).Pick("f.review_result", "acceptable"));
        Assert.Equal("Saved.", review.Toast);
        review = await review.RouteOkAsync("Complete review");
        Assert.Equal("Completed", review.Phase());

        var closed = await kim.GoAsync(page.Path);
        closed.ExpectText("accepted your response").ExpectText("Change requests go through our ECR process").ExpectNoText(secret);
    }

    [Fact] // SUP-030, INV-094/095, SUP-036: a scorecard calculated, published, shared and seen read-only in the portal
    public async Task A_published_scorecard_updates_the_supplier_rating_and_is_shared_read_only()
    {
        using var cwu = await fx.SignedInAsync("cwu");
        using var sortiz = await fx.SignedInAsync("sortiz");
        var (supplierUrl, _, supplierName, username) = await SupplierSteps.SupplierWithPortalContactAsync(fx, cwu, sortiz, "Lee Varga");

        var card = await (await cwu.GoAsync(supplierUrl)).ActionAsync("create_scorecard");
        Assert.Contains("Scorecard SCR-", card.Toast);
        Assert.Equal("Draft", card.Phase());
        var url = card.Path;
        var number = card.Number();

        // The supplier's weights were copied in; they must total 100% (INV-094).
        Assert.Equal(40m, decimal.Parse(card.FieldValue("w_quality")!, System.Globalization.CultureInfo.InvariantCulture));
        var bad = await card.SaveAsync(f => f.Set("f.ppm", "1000").Set("f.otd_pct", "90").Set("f.responsiveness", "3").Set("f.w_quality", "50"));
        Assert.Contains(bad.Errors, e => e.Contains("must total 100% (they total 110%)"));

        // No audit in the period: its weight is shared by the others (INV-095). 80×40 + 90×30 + 60×15 over 85 = 80.
        card = await (await cwu.GoAsync(url)).SaveAsync(f => f.Set("f.ppm", "1000").Set("f.otd_pct", "90").Set("f.responsiveness", "3")
            .Set("f.comments", "On-time delivery slipped in August; quality is steady."));
        Assert.Equal("Saved.", card.Toast);
        Assert.StartsWith("80", card.FieldText("overall_score"));
        Assert.Equal("B · Approved", card.FieldText("grade"));
        Assert.DoesNotContain(card.Actions(), a => a.StartsWith("portal-")); // drafts aren't shared

        card = await card.RouteOkAsync("Publish scorecard");
        Assert.Equal("Published", card.Phase());
        var profile = await cwu.GoAsync(supplierUrl);
        Assert.Equal("B · Approved", profile.FieldText("current_grade"));
        profile.ExpectText(number);

        card = await (await cwu.GoAsync(url)).ActionAsync("portal-share");
        Assert.Equal($"Shared with {supplierName} in the supplier portal.", card.Toast);

        using var lee = new Browser(fx);
        await lee.SignInAsync(username);
        var list = await lee.GoAsync("/portal/scorecards");
        list.ExpectText(number).ExpectText("B · Approved").ExpectText("80.00");
        var row = list.All("tr[data-href]").Single(r => r.TextContent.Contains(number));
        var shown = await lee.GoAsync(row.GetAttribute("data-href")!);
        shown.ExpectText("B · Approved").ExpectText("On-time delivery slipped in August");
        Assert.Null(shown.Q("form[data-portal-form]"));
    }

    [Fact] // SEC-804, INV-088: a contact sees only their own company's requests, is refused everywhere internal; staff can't use the portal
    public async Task Portal_contacts_see_only_their_own_company_and_are_refused_everywhere_internal()
    {
        using var cwu = await fx.SignedInAsync("cwu");
        using var sortiz = await fx.SignedInAsync("sortiz");
        var a = await SupplierSteps.SupplierWithPortalContactAsync(fx, cwu, sortiz, "Avery Stone");
        var b = await SupplierSteps.SupplierWithPortalContactAsync(fx, cwu, sortiz, "Blake Moss");

        var request = await (await cwu.GoAsync(a.Url)).ActionAsync("portal-request-doc", f => f
            .Pick("a.doc_type", "sds").Set("a.subject", $"SDS for mold release agent {E2e.Tag()}"));
        var number = request.Number();
        var recordUrl = request.Path;

        using var avery = new Browser(fx);
        await avery.SignInAsync(a.Username);
        var own = await avery.OpenPortalRequestAsync(number);
        var uploaded = await own.PortalForm().Set("f.doc_number", "SDS-114")
            .Attach("file.doc_file", "sds.pdf", E2e.Pdf("SDS"), "application/pdf").SubmitResponseAsync(submit: false);
        var fileHref = uploaded.One("a[href*='handler=File']").GetAttribute("href")!;
        Assert.Equal(HttpStatusCode.OK, (await avery.RawGetAsync(fileHref)).StatusCode);

        // The other supplier's contact can't list, open or download it.
        using var blake = new Browser(fx);
        var bHome = await blake.SignInAsync(b.Username);
        bHome.ExpectText(b.Name).ExpectNoText(a.Name).ExpectNoText(number);
        (await blake.GoAsync("/portal/requests")).ExpectNoText(number);
        await blake.GoAsync(own.Path, HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await blake.RawGetAsync(fileHref)).StatusCode);

        // Every internal page, API, search, export and file download is refused (deny by default).
        var attachmentId = fileHref.Split("fileId=")[1].Split('&')[0];
        string[] probes =
        [
            "/apps", "/apps/suppliers", "/apps/capa", "/work", "/search?q=" + number, "/notifications", "/account/profile", "/admin", "/admin/users",
            "/designer", "/insights", "/audits/findings", "/audits/program", "/change/timeliness", "/risk/heatmap", "/ehs/osha", "/ehs/near-miss",
            "/api/lookup/users", "/api/lookup/records?q=" + number, "/api/search/quick?q=" + number, "/api/notifications",
            "/files/" + attachmentId, "/apps/suppliers/export.csv", "/apps/supplier_portal/export.csv", recordUrl, recordUrl + "/print",
            recordUrl + "/audit.csv", a.Url, "/records/new?form=scar", "/forbidden",
        ];
        var leaks = new List<string>();
        foreach (var p in probes)
            if ((await avery.RawGetAsync(p)).StatusCode is var s && s != HttpStatusCode.Forbidden) leaks.Add($"{p} → {(int)s}");
        Assert.Empty(leaks);

        // Posting to an internal handler is refused too, and the refusal is shown in the portal's own chrome.
        var post = await own.PortalForm().Set("text", "Hello").SubmitToAsync(recordUrl + "?handler=Comment");
        Assert.Equal(HttpStatusCode.Forbidden, post.Status);
        Assert.Null(post.Q(".vl-sidebar"));
        var refusal = await avery.GoAsync(recordUrl, HttpStatusCode.Forbidden);
        refusal.ExpectText("part of the supplier portal").ExpectNoText(request.Title());

        // Internal users are refused from the portal.
        (await cwu.GoAsync("/portal")).ExpectText("You don't have access");
        (await cwu.GoAsync(own.Path)).ExpectText("You don't have access");
    }
}
