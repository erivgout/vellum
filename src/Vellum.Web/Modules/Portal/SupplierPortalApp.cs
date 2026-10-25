using Vellum.Domain;
using Vellum.Packaging;

namespace Vellum.Modules.Portal;

/// <summary>
/// Supplier requests answered through the supplier portal (SUP-004): document requests (certificates, SDSs, declarations),
/// supplier questionnaires (SUP-005 self-assessment) and responses to supplier audit findings (AUD-031). SCARs (CAPA-011) and
/// scorecards (SUP-036) live in their own applications and are shared with the portal by the behaviors in PortalBehaviors.cs.
/// Each form runs Draft → With Supplier → Review → Accepted/Completed: entering With Supplier shares the record with the
/// supplier's portal contacts, the supplier's submit routes it to Review, and Review either accepts it or returns it.
/// </summary>
public sealed class SupplierPortalApp : IPackagedApp
{
    public string Key => "supplier_portal";
    public int Order => 80;

    public void Define(AppBuilder app)
    {
        app.Name("Supplier Requests")
            .Description("Document requests, questionnaires and audit finding responses sent to suppliers through the supplier portal.")
            .Icon("bi-send-check").Color("#0f766e").Suite("Supplier").Order(20).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("@auditors", AppRole.Author)
            .Grant("@quality", AppRole.Author)
            .Grant("cwu", AppRole.Manager, designer: true);

        app.Keywords("sdr_doc_type", "Requested document type", k => k
            .Add("iso_cert", "Quality system certificate").Add("sds", "Safety data sheet (SDS)").Add("coc", "Certificate of conformance")
            .Add("coa", "Certificate of analysis").Add("compliance", "RoHS / REACH / Prop 65 declaration").Add("insurance", "Certificate of insurance")
            .Add("other", "Other document"));
        app.Keywords("sdr_review", "Document review result", k => k
            .Add("accepted", "Accepted", "success").Add("rejected", "Not acceptable", "danger"));
        app.Keywords("sq_type", "Questionnaire type", k => k
            .Add("self_assessment", "Supplier self-assessment").Add("onboarding", "Onboarding and qualification").Add("annual", "Annual survey")
            .Add("esg", "Sustainability and ESG").Add("other", "Other"));
        app.Keywords("sq_answer", "Answer type", k => k.Add("text", "Written answer").Add("yes_no", "Yes / No"));
        app.Keywords("sq_review", "Questionnaire review result", k => k
            .Add("acceptable", "Acceptable", "success").Add("follow_up", "Needs follow-up", "warning").Add("unacceptable", "Unacceptable", "danger"));
        app.Keywords("afr_review", "Finding response review", k => k
            .Add("accepted", "Accepted", "success").Add("rejected", "Not acceptable", "danger"));

        DefineDocumentRequest(app);
        DefineQuestionnaire(app);
        DefineAuditResponse(app);

        app.View("open", "Open supplier requests", v => v.Category("1. Requests").Forms(PortalKinds.DocForm, PortalKinds.QuestionnaireForm, PortalKinds.AuditResponseForm)
            .Open().Columns("number", "title", "supplier_name", "phase", "due", "owner").Sort("due", false).Default());
        app.View("with_supplier", "Waiting for the supplier", v => v.Category("1. Requests")
            .Forms(PortalKinds.DocForm, PortalKinds.QuestionnaireForm, PortalKinds.AuditResponseForm).Open().Phases("with_supplier")
            .Columns("number", "title", "supplier_name", "due_date", "due", "owner").Sort("due", false));
        app.View("to_review", "Supplier responses to review", v => v.Category("1. Requests")
            .Forms(PortalKinds.DocForm, PortalKinds.QuestionnaireForm, PortalKinds.AuditResponseForm).Open().Phases("review")
            .Columns("number", "title", "supplier_name", "responded_by", "responded_on", "owner").Sort("due", false));
        app.View("documents", "Document requests", v => v.Category("2. By type").Forms(PortalKinds.DocForm).All()
            .Columns("number", "title", "supplier_name", "doc_type", "expires_on", "phase", "due").Sort("created"));
        app.View("questionnaires", "Questionnaires", v => v.Category("2. By type").Forms(PortalKinds.QuestionnaireForm).All()
            .Columns("number", "title", "supplier_name", "questionnaire_type", "review_result", "phase").Sort("created"));
        app.View("audit_responses", "Audit finding responses", v => v.Category("2. By type").Forms(PortalKinds.AuditResponseForm).All()
            .Columns("number", "title", "supplier_name", "audit_date", "review_result", "phase").Sort("created"));
        app.View("closed", "Closed requests", v => v.Category("3. Closed").Forms(PortalKinds.DocForm, PortalKinds.QuestionnaireForm, PortalKinds.AuditResponseForm)
            .Closed().Columns("number", "title", "supplier_name", "phase", "modified").Sort("modified"));
    }

    /// <summary>
    /// Draft → With Supplier → Review → completed, shared by the three request forms. <paramref name="rows"/> is the subform the
    /// internal side defines in Draft and the supplier answers in With Supplier (questions, findings).
    /// </summary>
    private static void RequestWorkflow(WorkflowBuilder w, string request, string response, string review, string? rows, string done, string doneName,
        string acceptLabel, string acceptWhen, string noun)
    {
        string[] draftEdit = rows == null ? [request] : [request, rows];
        string[] supplierEdit = rows == null ? [response] : [response, rows];
        string[] reviewEdit = rows == null ? [review] : [review, rows];
        w.Start("draft", "Draft").Purpose($"Prepare the {noun}. Sending it shares it with the supplier's portal contacts and emails them.")
            .DueDays(5).Edit(draftEdit).Hide(response, review);
        w.Phase("with_supplier", "With Supplier").Purpose("The supplier answers in the supplier portal. Their submit sends it back to you for review.")
            .AssignField("owner").DueFrom("due_date").DueDays(15).Edit(supplierEdit).ReadOnly(request).Hide(review);
        w.Phase("review", "Review").Purpose("Check the supplier's response. Accept it, or return it to the supplier: your comment is shown to them in the portal.")
            .AssignField("owner").DueDays(5).Edit(reviewEdit).ReadOnlyRest();
        w.Completed(done, doneName);
        w.Voided();

        w.Forward("draft", "with_supplier", label: "Send to supplier portal");
        w.Void("draft");
        w.Forward("with_supplier", "review", label: "Review the response");
        w.Back("with_supplier", "draft", label: "Withdraw to draft");
        w.Void("with_supplier", label: "Cancel request");
        w.Forward("review", done, label: acceptLabel, when: acceptWhen);
        w.Back("review", "with_supplier", label: "Return to supplier");
        w.Void("review");
    }

    private static void DefineDocumentRequest(AppBuilder app) => app.Form(PortalKinds.DocForm, "Document request", f => f
        .Icon("bi-file-earmark-arrow-up")
        .Description("Ask a supplier for a certificate, safety data sheet or declaration. The supplier uploads it in the supplier portal.")
        .Numbering("SDR-")
        .Title("{subject}")
        .Section("sdr_request", "Request", s =>
        {
            s.Link("supplier", "Supplier", "supplier").Required().Help("The request is shared with this supplier's portal contacts.");
            s.Text("supplier_name", "Supplier name", 6).ReadOnly().Header();
            s.Keyword("doc_type", "Document type", "sdr_doc_type").Required().Header();
            s.Text("subject", "Document requested", 8).Required().MaxLength(200).Placeholder("For example: Renewed ISO 13485 certificate");
            s.Keyword("standard", "Standard", "sup_cert_std").VisibleWhen("doc_type=iso_cert")
                .Help("When accepted, the certificate is added to the supplier profile's certificates (SUP-003).");
            s.Link("material", "Material", "material").Help("For a safety data sheet or certificate of analysis: the material it covers.");
            s.User("owner", "Requested by").Required().Header().Default("$me");
            s.Date("due_date", "Needed by").Required().Default("$today+14");
            s.TextArea("instructions", "Instructions to the supplier").Help("Shown to the supplier in the portal.");
            s.Attachment("request_files", "Files for the supplier").Help("Templates or forms the supplier needs. The supplier can download them in the portal.");
        }, icon: "bi-file-earmark-text")
        .Section("sdr_response", "Supplier response", s =>
        {
            s.Prompt("sdr_prompt", "The supplier fills this in through the supplier portal. You can also record a document the supplier emailed.");
            s.Attachment("doc_file", "Document");
            s.Text("doc_number", "Document or certificate number", 4);
            s.Text("issuer", "Issued by", 4).Placeholder("Certification body or manufacturer");
            s.Date("issued_on", "Issued on", 2);
            s.Date("expires_on", "Expires on", 2);
            s.TextArea("supplier_comments", "Supplier comments");
            s.Text("responded_by", "Submitted by", 6).ReadOnly();
            s.Date("responded_on", "Submitted on", 6).ReadOnly();
        }, tab: "Supplier response", icon: "bi-reply")
        .Section("sdr_review", "Review", s =>
        {
            s.Keyword("review_result", "Review result", "sdr_review").RequiredIn("review").Header();
            s.TextArea("review_notes", "Review notes (internal)").Help("Never shown to the supplier. To ask the supplier for changes, use Return to supplier.");
            s.YesNo("add_certificate", "Add to the supplier's certificates?").Default("yes").VisibleWhen("doc_type=iso_cert");
            s.Attachment("review_files", "Internal review files").Help("Internal only: never shown in the portal.");
        }, tab: "Supplier response", icon: "bi-patch-check")
        .Workflow("sdr", "Document request", w => RequestWorkflow(w, "sdr_request", "sdr_response", "sdr_review", null, "accepted", "Accepted",
            "Accept document", "review_result=accepted", "document request")));

    private static void DefineQuestionnaire(AppBuilder app) => app.Form(PortalKinds.QuestionnaireForm, "Supplier questionnaire", f => f
        .Icon("bi-ui-checks")
        .Description("Questions for a supplier (self-assessment, onboarding, annual survey). The supplier answers in the supplier portal.")
        .Numbering("SQ-")
        .Title("{subject}")
        .Section("sq_info", "Questionnaire", s =>
        {
            s.Link("supplier", "Supplier", "supplier").Required().Help("The questionnaire is shared with this supplier's portal contacts.");
            s.Text("supplier_name", "Supplier name", 6).ReadOnly().Header();
            s.Text("subject", "Title", 8).Required().MaxLength(200).Placeholder("For example: 2026 supplier self-assessment");
            s.Keyword("questionnaire_type", "Type", "sq_type").Required().Header();
            s.User("owner", "Owner").Required().Header().Default("$me");
            s.Date("due_date", "Answers due").Required().Default("$today+21");
            s.TextArea("instructions", "Instructions to the supplier").Help("Shown to the supplier in the portal.");
            s.Attachment("request_files", "Files for the supplier");
        }, icon: "bi-ui-checks")
        .Subform("questions", "Questions", s =>
        {
            s.Text("q_text", "Question", 5).Required().MaxLength(500);
            s.Text("q_guidance", "Guidance for the supplier", 3).MaxLength(500);
            s.Keyword("q_type", "Answer", "sq_answer", 2);
            s.YesNo("q_required", "Req.", 1);
            s.Text("answer", "Supplier answer", 12).MaxLength(4000);
            s.Text("q_note", "Reviewer note (internal)", 12).MaxLength(1000);
        }, tab: "Questions", rowNoun: "question", required: true, icon: "bi-question-square",
           description: "Define the questions in Draft. The supplier answers them in the portal; reviewer notes are internal and never shown to the supplier.")
        .Section("sq_response", "Supplier response", s =>
        {
            s.Prompt("sq_prompt", "The supplier fills this in through the supplier portal.");
            s.TextArea("supplier_comments", "Supplier comments");
            s.Attachment("sq_file", "Supporting documents");
            s.Text("responded_by", "Submitted by", 6).ReadOnly();
            s.Date("responded_on", "Submitted on", 6).ReadOnly();
        }, tab: "Questions", icon: "bi-reply")
        .Section("sq_review", "Review", s =>
        {
            s.Keyword("review_result", "Review result", "sq_review").RequiredIn("review").Header();
            s.TextArea("review_notes", "Review notes (internal)").Help("Never shown to the supplier.");
        }, tab: "Questions", icon: "bi-patch-check")
        .Workflow("sq", "Supplier questionnaire", w => RequestWorkflow(w, "sq_info", "sq_response", "sq_review", "questions", "completed", "Completed",
            "Complete review", "review_result", "questionnaire")));

    private static void DefineAuditResponse(AppBuilder app) => app.Form(PortalKinds.AuditResponseForm, "Audit finding response", f => f
        .Icon("bi-clipboard2-check")
        .Description("The findings of a supplier audit, sent to the supplier to answer through the supplier portal (AUD-031).")
        .Numbering("AFR-")
        .Title("{subject}")
        .HiddenFromNew()
        .Section("afr_info", "Audit", s =>
        {
            s.Link("supplier", "Supplier", "supplier").Required();
            s.Text("supplier_name", "Supplier name", 6).ReadOnly().Header();
            s.Text("subject", "Title", 12).Required().MaxLength(200);
            s.Link("audit", "Audit", "audit").ReadOnly();
            s.Text("audit_title", "Audit", 6).ReadOnly();
            s.Date("audit_date", "Audit date", 3).ReadOnly();
            s.Text("auditor_name", "Lead auditor", 3).ReadOnly();
            s.User("owner", "Owner").Required().Header().Default("$me");
            s.Date("due_date", "Responses due").Required().Default("$today+30");
            s.TextArea("instructions", "Instructions to the supplier").Help("Shown to the supplier in the portal.");
        }, icon: "bi-clipboard2-check")
        .Subform("findings", "Findings", s =>
        {
            s.Text("finding_no", "No.", 1).ReadOnly();
            s.Text("clause", "Clause", 2).ReadOnly();
            s.Keyword("classification", "Classification", "aud_classification", 2).ReadOnly();
            s.Text("finding", "Finding", 4).ReadOnly().MaxLength(1000);
            s.Text("evidence", "Objective evidence", 3).ReadOnly().MaxLength(2000);
            s.Text("sr_root_cause", "Supplier root cause", 6).MaxLength(4000);
            s.Text("sr_correction", "Correction / containment", 6).MaxLength(4000);
            s.Text("sr_corrective_action", "Corrective action", 6).MaxLength(4000);
            s.Date("sr_target_date", "Target date", 3);
            s.YesNo("accepted", "Accepted?", 3);
        }, tab: "Findings", rowNoun: "finding", icon: "bi-list-check",
           description: "Copied from the audit's findings. The supplier answers each one in the portal; minor and major nonconformances need a root cause, corrective action and target date. Accepted? is internal.")
        .Section("afr_response", "Supplier response", s =>
        {
            s.TextArea("supplier_comments", "Supplier comments");
            s.Attachment("afr_file", "Supporting evidence");
            s.Text("responded_by", "Submitted by", 6).ReadOnly();
            s.Date("responded_on", "Submitted on", 6).ReadOnly();
        }, tab: "Findings", icon: "bi-reply")
        .Section("afr_review", "Review", s =>
        {
            s.Keyword("review_result", "Review result", "afr_review").RequiredIn("review").Header();
            s.TextArea("review_notes", "Review notes (internal)").Help("Never shown to the supplier.");
        }, tab: "Findings", icon: "bi-patch-check")
        .Workflow("afr", "Audit finding response", w => RequestWorkflow(w, "afr_info", "afr_response", "afr_review", "findings", "completed", "Completed",
            "Accept responses", "review_result=accepted", "audit finding response")));

    public Task SeedDemoAsync(DemoContext demo) => PortalDemo.SeedAsync(demo);
}
