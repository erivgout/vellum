using Vellum.Domain;
using Vellum.Packaging;

namespace Vellum.Modules.Documents;

/// <summary>
/// Document Control §10: type profiles (DOC-001..006), controlled documents with the packaged approval workflow (DOC-030),
/// e-signatures (DOC-040), release and supersession (DOC-050..055, INV-100), change requests (DOC-090..097),
/// periodic reviews (DOC-070..076) and obsolete requests (DOC-100..107). Business rules live in
/// <see cref="DocumentBehavior"/> and the request behaviors; shared logic in <see cref="DocumentControlService"/>.
/// </summary>
public sealed partial class DocumentControlApp : IPackagedApp
{
    public string Key => DocKeys.App;
    public int Order => 20;

    public void Define(AppBuilder app)
    {
        app.Name("Document Control")
            .Description("Controlled SOPs, policies, work instructions, test methods, specifications and forms: authoring, approval, release, periodic review, change and obsolescence.")
            .Icon("bi-journal-text").Color("#2563eb").Suite("Documents").Order(1).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("@engineering", AppRole.Author)
            .Grant("@qc-lab", AppRole.Author)
            .Grant("@ehs", AppRole.Author)
            .Grant("@regulatory", AppRole.Author)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("@training-coordinators", AppRole.Author)
            .Grant("@" + DocKeys.DocControllers, AppRole.Manager)
            .Grant("sortiz", AppRole.Manager, designer: true);

        app.Keywords("doc_type", "Document type", k => k
            .Add("procedure", "Procedure", "primary").Add("policy", "Policy", "violet").Add("work_instruction", "Work Instruction", "info")
            .Add("test_method", "Test Method", "warning").Add("specification", "Specification", "secondary").Add("form", "Form", "dark"));
        app.Keywords("doc_revision_format", "Revision format", k => k
            .Add("alphabetic", "Alphabetic (A, B … Z, AA)").Add("numeric_0", "Numeric from 0").Add("numeric_1", "Numeric from 1"));
        app.Keywords("doc_review_outcome", "Periodic review outcome", k => k
            .Add("no_change", "No change needed", "success").Add("change_required", "Change required", "warning").Add("obsolete", "Obsolete", "danger"));
        app.Keywords("org_department", "Department", k => k
            .Add("quality", "Quality").Add("quality_systems", "Quality Systems").Add("quality_assurance", "Quality Assurance")
            .Add("regulatory", "Regulatory").Add("operations", "Operations").Add("supply_chain", "Supply Chain").Add("ehs", "EHS")
            .Add("qc_laboratory", "QC Laboratory").Add("human_resources", "Human Resources").Add("facilities", "Facilities").Add("it", "IT"));
        app.Keywords("org_site", "Site", k => k
            .Add("kzo", "Kalamazoo").Add("bos", "Boston").Add("gal", "Galway").Add("sin", "Singapore"));
        // Keys are the group keys of the directory, so applicability and distribution resolve to group members.
        app.Keywords("trn_group", "Training group", k => k
            .Add("all-employees", "All employees").Add("quality", "Quality").Add("operations", "Operations").Add("engineering", "Engineering")
            .Add("qc-lab", "QC Laboratory").Add("maintenance", "Maintenance & Calibration").Add("ehs", "EHS Team")
            .Add("supplier-quality", "Supplier Quality").Add("regulatory", "Regulatory Affairs").Add("auditors", "Auditors")
            .Add("document-control", "Document Controllers").Add("qa-approvers", "Quality Approvers"));

        DefineProfile(app);
        DefineDocument(app);
        DefineRequests(app);
        DefineViews(app);
    }

    private static void DefineProfile(AppBuilder app) =>
        app.Form(DocKeys.Profile, "Document Type Profile", f => f
            .Icon("bi-sliders")
            .Description("Numbering, revision format, periodic review frequency and training rules for one document type (DOC-001). Application managers only.")
            .NonWorkflow()
            .Numbering("DTP-", 3)
            .Title("{profile_name}")
            .Section("profile", "Type profile", s =>
            {
                s.Keyword("doc_type", "Document type", "doc_type").Required().Header()
                    .Help("Each type can have at most one enabled profile (INV-101).");
                s.Text("profile_name", "Profile name", 8).Required().MaxLength(100).Placeholder("For example: Standard operating procedure");
                s.Text("number_prefix", "Number prefix", 4).Required().MaxLength(12)
                    .Help("Prefix of new document numbers, for example SOP- gives SOP-00012. Numbers are never reused (DOC-010, INV-020).");
                s.Keyword("revision_format", "Revision format", "doc_revision_format", 4).Required().Default("alphabetic")
                    .Help("How revision identifiers increase (DOC-014). Applies to documents created after a change.");
                s.Number("review_months", "Periodic review every (months)", 4).Min(0).Max(120).Decimals(0).Default("24")
                    .Help("Next review date = effective date + this many months (DOC-070/071). 0 means no periodic review.");
                s.YesNo("training_considered", "Training must be considered", 4).Default("yes")
                    .Help("When on, every revision must record whether training is required before it leaves Draft (DOC-006, DOC-060).");
                s.YesNo("enabled", "Enabled", 4).Default("yes").Header()
                    .Help("Disabling blocks new documents of this type. Existing documents aren't changed (DOC-004).");
                s.TextArea("usage", "When to use this type");
            }, icon: "bi-sliders"));

    private static void DefineDocument(AppBuilder app) =>
        app.Form(DocKeys.Document, "Controlled Document", f => f
            .Icon("bi-file-earmark-text")
            .Description("An SOP, policy, work instruction, test method, specification or form under revision control.")
            .Numbering("DOC-")
            .Title("{title}")
            .Revisions(RevisionFormat.Alphabetic)
            .Section("identification", "Identification", s =>
            {
                s.Keyword("doc_type", "Document type", "doc_type", 4).Required().Header()
                    .Help("Sets the number prefix, revision format and review frequency from the type's profile. It can't change after the document is created.");
                s.Text("title", "Title", 8).Required().MaxLength(200).Placeholder("For example: Cleanroom gowning and entry");
                s.User("owner", "Document owner", 4).Required().Header().Default("$me")
                    .Help("Approves the content in Initial and Final Approval and owns the periodic review.");
                s.Users("reviewers", "Reviewers", 8).Required()
                    .Help("Every reviewer signs in Review (all must approve). Reviewers must be independent of the author (SAF-044).");
                s.Keyword("department", "Department", "org_department", 4);
                s.Keywords("sites", "Applies to sites", "org_site", 4);
                s.Number("effective_delay", "Effective date delay (days)", 4).Min(0).Max(90).Decimals(0).Default("0")
                    .Help("Calendar days between release and the effective date, so people can finish training first (DOC-051).");
                s.Date("target_effective", "Target effective date", 4)
                    .Help("Optional. The revision won't become effective before this date, even when released earlier.");
                s.TextArea("purpose", "Purpose and scope");
            }, icon: "bi-card-heading")
            .Section("revision", "Revision", s =>
            {
                s.TextArea("revision_reason", "Reason for new or revised document").Required()
                    .Help("Shown in the revision history (DOC-016/017). Inherited from the change request for a new revision.");
                s.Link("change_request", "Originating change request", DocKeys.ChangeRequest).ReadOnly();
                s.YesNo("minor_change", "Minor or editorial change?", 6).Default("no")
                    .Help("Yes lets a revision go from Draft straight to Awaiting Release, skipping review and approval (DOC-031). Only for changes that don't alter meaning: typos, formatting, references. The flag is kept in the audit trail.");
            }, icon: "bi-arrow-repeat")
            .Section("training", "Training decision", s =>
            {
                s.Prompt("training_prompt", "Decide whether people need training on this revision. If training is required, use Training › Set up training to create the course; assignments are created when the revision reaches Awaiting Release (DOC-063).");
                s.YesNo("training_required", "Is training required for this revision?", 6)
                    .Help("Required before leaving Draft for document types that consider training (DOC-033, DOC-060).");
                s.TextArea("training_justification", "Why is no training needed?").VisibleWhen("training_required=no")
                    .Help("Mandatory when training isn't required. Kept in the audit history (DOC-060).");
                s.YesNo("training_gated", "Hold release until training is complete?", 6).Default("no").VisibleWhen("training_required=yes")
                    .Help("Training-gated release (TRN-071): the revision can't become effective until enough trainees have completed, unless Document Control overrides with a justification.");
                s.Number("training_gate_percent", "Required completion (%)", 6).Min(1).Max(100).Decimals(0).Default("100").VisibleWhen("training_gated=yes");
            }, icon: "bi-mortarboard")
            .Section("content", "Controlled content", s =>
            {
                s.Attachment("primary_file", "Primary controlled file").Required()
                    .Help("The controlled content, preferably as PDF. Readers see this file once the revision is effective (DOC-020).");
                s.Attachment("supporting_files", "Supporting files");
                s.Link("related_documents", "Related documents", DocKeys.Document)
                    .Help("Other controlled documents this one refers to.");
                s.Url("external_reference", "External reference", 12).Placeholder("https://");
            }, tab: "Content", icon: "bi-file-earmark-richtext")
            .Section("distribution", "Distribution", s =>
            {
                s.Users("distribution", "Distribution list", 6)
                    .Help("Notified with a link when this revision becomes effective or is made obsolete (DOC-055).");
                s.Keywords("distribution_groups", "Distribute to groups", "trn_group", 6);
            }, tab: "Content", icon: "bi-send")
            .Workflow("document", "Document Control", w =>
            {
                w.Start(DocKeys.Draft, "Draft")
                    .Purpose("Write the document, attach the controlled file, choose the owner and reviewers and record the training decision.")
                    .DueDays(15).Edit("identification", "revision", "training", "content", "distribution");
                w.Phase(DocKeys.DcReview, "Document Control Review")
                    .Purpose("Document Control checks the template, format, numbering and SOP adherence. Formatting fixes can be made to the content here.")
                    .AssignGroup(DocKeys.DocControllers).DueDays(3).Edit("content").ReadOnlyRest();
                w.Phase(DocKeys.InitialApproval, "Initial Approval")
                    .Purpose("The owner approves the content before it goes out for review.")
                    .AssignField("owner").DueDays(5).ReadOnlyRest();
                w.Phase(DocKeys.Review, "Review")
                    .Purpose("Each reviewer reviews the content and signs. The document moves on once every reviewer has approved.")
                    .AssignField("reviewers").Voting(100).DueDays(10).ReadOnlyRest();
                w.Phase(DocKeys.FinalApproval, "Final Approval")
                    .Purpose("The owner gives final approval for distribution.")
                    .AssignField("owner").DueDays(5).ReadOnlyRest();
                w.Phase(DocKeys.AwaitingRelease, "Awaiting Release")
                    .Purpose("Document Control releases the revision once its effective date is reached and any required training is complete.")
                    .AssignGroup(DocKeys.DocControllers).DueDays(10).ReadOnlyRest();
                w.Completed(DocKeys.Approved, "Approved");
                w.Voided();
                w.Obsolete(DocKeys.Obsolete, "Obsolete");

                w.Forward(DocKeys.Draft, DocKeys.DcReview, label: "Submit to Document Control");
                w.Forward(DocKeys.Draft, DocKeys.AwaitingRelease, label: "Minor change: send to release", when: "minor_change=yes", comment: true);
                w.Void(DocKeys.Draft);
                w.Forward(DocKeys.DcReview, DocKeys.InitialApproval, label: "Format checked: send for initial approval");
                w.Back(DocKeys.DcReview, DocKeys.Draft, label: "Return to author");
                w.Forward(DocKeys.InitialApproval, DocKeys.Review, sign: "Approved", label: "Approve: send to reviewers");
                w.Back(DocKeys.InitialApproval, DocKeys.DcReview, label: "Return to Document Control");
                w.Void(DocKeys.InitialApproval);
                w.Forward(DocKeys.Review, DocKeys.FinalApproval, sign: "Reviewed", label: "Reviewed: send for final approval");
                w.Back(DocKeys.Review, DocKeys.InitialApproval, label: "Reject: back to initial approval");
                w.Forward(DocKeys.FinalApproval, DocKeys.AwaitingRelease, sign: "Approved", label: "Approve for release");
                w.Back(DocKeys.FinalApproval, DocKeys.Review, label: "Back to review");
                w.Void(DocKeys.FinalApproval);
                w.Forward(DocKeys.AwaitingRelease, DocKeys.Approved, sign: DocKeys.SignReleased, label: "Release: make effective");
                w.Back(DocKeys.AwaitingRelease, DocKeys.FinalApproval, label: "Hold: back to final approval");
            }));

    private static void DefineRequests(AppBuilder app)
    {
        app.Form(DocKeys.ChangeRequest, "Change Request", f => f
            .Icon("bi-pencil-square")
            .Description("Request a new revision of an effective document (DOC-090). Raised from the document's Revision control menu.")
            .Numbering("CR-")
            .Title("{summary}")
            .Activity(DocKeys.Document)
            .Section("target", "Document", s =>
            {
                s.Link("target_document", "Document", DocKeys.Document).ReadOnly().Required();
                s.Text("doc_ref", "Document reference", 12).ReadOnly();
            }, icon: "bi-file-earmark-text")
            .Section("request", "Change request", s =>
            {
                s.Text("summary", "What should change", 12).Required().MaxLength(200);
                s.TextArea("reason", "Reason for change").Required()
                    .Help("Becomes the new revision's reason for revision (DOC-017).");
                s.User("approver", "Accepted by", 4).Required().Header()
                    .Help("Decides whether to accept the change. Defaults to the document owner.");
                s.Users("assign_to", "Assign new revision to", 8).Required()
                    .Help("Author(s) of the new Draft revision created on acceptance (DOC-091).");
                s.Users("notify", "Notify of new revision", 8);
                s.Date("revision_due", "New revision due", 4).Default("$today+30");
                s.Attachment("supporting", "Supporting files");
            }, icon: "bi-pencil-square")
            .Workflow("change_request", "Change Request", w =>
            {
                w.Start("requested", "Requested").Purpose("The approver accepts the change, which creates the new Draft revision in one step (DOC-093), or voids it.")
                    .AssignField("approver").DueDays(5).Edit("request").ReadOnly("target");
                w.Completed("accepted", "Accepted");
                w.Voided();
                w.Forward("requested", "accepted", sign: "Approved", label: "Accept: create new revision");
                w.Void("requested");
            }));

        app.Form(DocKeys.PeriodicReview, "Periodic Review", f => f
            .Icon("bi-calendar2-check")
            .Description("Scheduled or ad hoc review of an effective document (DOC-073..076).")
            .Numbering("PR-")
            .Title("Periodic review: {doc_ref}")
            .Activity(DocKeys.Document)
            .Section("target", "Document", s =>
            {
                s.Link("target_document", "Document", DocKeys.Document).ReadOnly().Required();
                s.Text("doc_ref", "Document reference", 12).ReadOnly();
            }, icon: "bi-file-earmark-text")
            .Section("review", "Review", s =>
            {
                s.User("reviewer", "Reviewer", 4).Required().Header();
                s.Keyword("outcome", "Outcome", "doc_review_outcome", 4).RequiredIn("in_review").Header()
                    .Help("Change required starts a change request; Obsolete starts an obsolete request (DOC-076).");
                s.Date("next_review_date", "Next review date", 4)
                    .Help("Leave blank to calculate it as the completion date plus the type's review frequency (DOC-074).");
                s.TextArea("review_comments", "Review comments").RequiredIn("in_review")
                    .Help("Added to the document's comment log as a review entry when the review is completed (DOC-075).");
                s.Attachment("evidence", "Review evidence");
            }, icon: "bi-calendar2-check")
            .Workflow("periodic_review", "Periodic Review", w =>
            {
                w.Start("in_review", "In Review").Purpose("Check the document is still accurate, current and followed, then record the outcome and sign.")
                    .AssignField("reviewer").DueDays(20).Edit("review").ReadOnly("target");
                w.Completed("completed", "Closed");
                w.Voided();
                w.Forward("in_review", "completed", sign: "Reviewed", label: "Complete periodic review");
                w.Void("in_review");
            }));

        app.Form(DocKeys.ObsoleteRequest, "Obsolete Request", f => f
            .Icon("bi-archive")
            .Description("Withdraw an effective document from use (DOC-101..103).")
            .Numbering("OR-")
            .Title("Obsolete: {doc_ref}")
            .Activity(DocKeys.Document)
            .Section("target", "Document", s =>
            {
                s.Link("target_document", "Document", DocKeys.Document).ReadOnly().Required();
                s.Text("doc_ref", "Document reference", 12).ReadOnly();
            }, icon: "bi-file-earmark-text")
            .Section("request", "Obsolescence", s =>
            {
                s.TextArea("reason", "Reason for obsolescence").Required();
                s.Link("replacement", "Replaced by", DocKeys.Document).Help("The document that replaces this one, if any.");
                s.Attachment("evidence", "Supporting files");
            }, icon: "bi-archive")
            .Workflow("obsolete_request", "Obsolete Request", w =>
            {
                w.Start("new", "New").Purpose("Explain why the document is no longer needed, then send it for approval.")
                    .DueDays(5).Edit("request").ReadOnly("target");
                w.Phase("approval", "Approval").Purpose("Quality approves withdrawing the document. On acceptance it becomes Obsolete and leaves every effective view (DOC-103).")
                    .AssignGroup(DocKeys.QaApprovers).DueDays(5).ReadOnlyRest();
                w.Completed("accepted", "Accepted");
                w.Voided();
                w.Forward("new", "approval", label: "Send for approval");
                w.Void("new");
                w.Forward("approval", "accepted", sign: "Approved for obsolescence", label: "Accept: make obsolete");
                w.Back("approval", "new", label: "Return to requester");
                w.Void("approval");
            }));
    }

    private static void DefineViews(AppBuilder app)
    {
        // DOC-080 / SAF-043: readers land on effective revisions.
        app.View("effective", "All effective documents", v => v.Category("1. Effective documents").Forms(DocKeys.Document).Effective()
            .Columns("number", "revision", "title", "doc_type", "owner", "department", "effective").Sort("number", false).Default());
        foreach (var (key, label) in new[] { ("procedure", "Procedures"), ("policy", "Policies"), ("work_instruction", "Work instructions"),
                     ("test_method", "Test methods"), ("specification", "Specifications"), ("form", "Forms") })
            app.View("effective_" + key, label, v => v.Category("1. Effective documents").Forms(DocKeys.Document).Effective().Filter("doc_type", "eq", key)
                .Columns("number", "revision", "title", "owner", "department", "effective").Sort("number", false));
        app.View("effective_by_owner", "By owner", v => v.Category("1. Effective documents").Forms(DocKeys.Document).Effective()
            .Columns("owner", "number", "revision", "title", "doc_type", "effective").Sort("owner", false));
        app.View("effective_mine", "Documents I own", v => v.Category("1. Effective documents").Forms(DocKeys.Document).Effective().Filter("owner", "eq", "$me")
            .Columns("number", "revision", "title", "doc_type", "effective"));

        app.View("my_tasks", "My document tasks", v => v.Category("2. In progress").Forms(DocKeys.Document, DocKeys.ChangeRequest, DocKeys.PeriodicReview, DocKeys.ObsoleteRequest).Mine()
            .Columns("number", "revision", "title", "form", "phase", "due").Sort("due", false));
        app.View("pending", "Pending documents", v => v.Category("2. In progress").Forms(DocKeys.Document).Open()
            .Columns("number", "revision", "title", "doc_type", "phase", "due", "assignees").Sort("modified"));
        app.View("drafts", "Drafts", v => v.Category("2. In progress").Forms(DocKeys.Document).Open().Phases(DocKeys.Draft)
            .Columns("number", "revision", "title", "doc_type", "owner", "due", "assignees"));
        app.View("in_approval", "In review and approval", v => v.Category("2. In progress").Forms(DocKeys.Document).Open()
            .Phases(DocKeys.DcReview, DocKeys.InitialApproval, DocKeys.Review, DocKeys.FinalApproval)
            .Columns("number", "revision", "title", "phase", "due", "assignees").Sort("phase", false));
        app.View("awaiting_release", "Awaiting release", v => v.Category("2. In progress").Forms(DocKeys.Document).Open().Phases(DocKeys.AwaitingRelease)
            .Columns("number", "revision", "title", "doc_type", "training_gated", "due", "assignees"));
        app.View("no_training_decision", "Without training decision", v => v.Category("2. In progress").Forms(DocKeys.Document).Open().Filter("training_required", "empty")
            .Columns("number", "revision", "title", "doc_type", "phase", "owner"));

        app.View("cr_open", "Open change requests", v => v.Category("3. Requests").Forms(DocKeys.ChangeRequest).Open()
            .Columns("number", "title", "doc_ref", "approver", "phase", "due", "created").Sort("due", false));
        app.View("cr_closed", "Accepted and voided change requests", v => v.Category("3. Requests").Forms(DocKeys.ChangeRequest).Closed()
            .Columns("number", "title", "doc_ref", "phase", "modified"));
        app.View("pr_open", "Open periodic reviews", v => v.Category("3. Requests").Forms(DocKeys.PeriodicReview).Open()
            .Columns("number", "doc_ref", "reviewer", "phase", "due").Sort("due", false));
        app.View("pr_closed", "Closed periodic reviews", v => v.Category("3. Requests").Forms(DocKeys.PeriodicReview).Closed()
            .Columns("number", "doc_ref", "reviewer", "outcome", "modified"));
        app.View("or_open", "Open obsolete requests", v => v.Category("3. Requests").Forms(DocKeys.ObsoleteRequest).Open()
            .Columns("number", "doc_ref", "phase", "due", "assignees", "created"));
        app.View("or_archived", "Obsolete requests in the archive", v => v.Category("3. Requests").Forms(DocKeys.ObsoleteRequest).Archived()
            .Columns("number", "doc_ref", "phase", "status", "modified"));

        app.View("archive_superseded", "Superseded revisions", v => v.Category("4. Archive").Forms(DocKeys.Document).Archived().Phases(DocKeys.Approved)
            .Columns("number", "revision", "title", "doc_type", "effective", "status", "modified").Sort("modified"));
        app.View("archive_obsolete", "Obsolete documents", v => v.Category("4. Archive").Forms(DocKeys.Document).Archived().Phases(DocKeys.Obsolete)
            .Columns("number", "revision", "title", "doc_type", "owner", "modified").Sort("modified"));
        app.View("voided", "Voided documents", v => v.Category("4. Archive").Forms(DocKeys.Document).Voided()
            .Columns("number", "revision", "title", "doc_type", "owner", "modified").Sort("modified"));

        app.View("profiles_active", "Document type profiles", v => v.Category("5. Administration").Forms(DocKeys.Profile).All().Filter("enabled", "eq", "yes")
            .Columns("number", "title", "doc_type", "number_prefix", "revision_format", "review_months", "training_considered").Sort("number", false));
        app.View("profiles_disabled", "Disabled type profiles", v => v.Category("5. Administration").Forms(DocKeys.Profile).All().Filter("enabled", "eq", "no")
            .Columns("number", "title", "doc_type", "number_prefix", "modified"));
    }
}
