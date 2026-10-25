using Vellum.Domain;
using Vellum.Packaging;

namespace Vellum.Modules.Audits;

/// <summary>
/// Audits §14.1–14.3: checklist templates and audit types (setup), audit plans (Draft → Approval → Active → Completed) with
/// planned audits and "Schedule audit" (AUD-012, INV-165), and audits (Draft → Scheduled → Report Approval → Completed) with
/// participants, documents reviewed, scored checklist questions (INV-067, AUD-022), findings that raise CAPAs and
/// recommendations (AUD-025), and the audit summary (AUD-028).
/// </summary>
public sealed class AuditsApp : IPackagedApp
{
    public string Key => "audits";
    public int Order => 50;

    public void Define(AppBuilder app)
    {
        app.Name("Audits")
            .Description("Plan audit programs, run scored checklist audits, classify findings and raise CAPAs and recommendations.")
            .Icon("bi-clipboard2-check").Color("#b45309").Suite("Quality").Order(30).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@auditors", AppRole.Author)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("tbrooks", AppRole.Manager, designer: true)
            .Grant("sortiz", AppRole.Manager);

        app.Keywords("aud_classification", "Audit finding classification", k => k
            .Add("conforming", "Conforming", "success").Add("observation", "Observation", "info").Add("minor_nc", "Minor nonconformance", "warning")
            .Add("major_nc", "Major nonconformance", "danger").Add("ofi", "Opportunity for improvement", "violet"));
        app.Keywords("aud_program", "Audit program", k => k
            .Add("quality", "Quality").Add("supplier", "Supplier").Add("environmental", "Environmental").Add("safety", "Safety"));
        app.Keywords("aud_evaluation", "Audit evaluation", k => k.Add("normal", "Normal").Add("scoring", "Scoring"));
        app.Keywords("aud_result", "Audit result", k => k
            .Add("acceptable", "Acceptable", "success").Add("marginal", "Marginal", "warning").Add("unacceptable", "Unacceptable", "danger"));
        app.Keywords("aud_frequency", "Frequency recommendation", k => k
            .Add("increase", "Increase frequency", "warning").Add("keep", "Keep frequency", "secondary").Add("decrease", "Decrease frequency", "success"));
        app.Keywords("aud_role", "Audit participant role", k => k
            .Add("lead_auditor", "Lead auditor").Add("auditor", "Auditor").Add("auditee", "Auditee").Add("observer", "Observer")
            .Add("expert", "Technical expert").Add("guide", "Guide"));
        app.Keywords("aud_entity", "Auditable entity", k => k
            .Add("process", "Process").Add("supplier", "Supplier").Add("site", "Site").Add("department", "Department").Add("clause", "Clause / requirement"));
        app.Keywords("aud_kind", "Audit kind", k => k
            .Add("internal", "Internal", "info").Add("supplier", "Supplier", "warning").Add("certification", "Certification / notified body", "violet")
            .Add("regulatory", "Regulatory inspection", "danger").Add("customer", "Customer", "secondary"));

        // ------------------------------------------------------------------ setup (AUD-001, AUD-002, AUD-004)
        app.Form("checklist_template", "Checklist template", f => f
            .Icon("bi-card-checklist")
            .Description("A reusable bank of audit questions with clause references and scores.")
            .Numbering("CKL-", 3)
            .Title("{name}")
            .NonWorkflow()
            .Section("checklist", "Checklist", s =>
            {
                s.Text("name", "Checklist name", 8).Required().MaxLength(150);
                s.YesNo("enabled", "Enabled", 4).Default("yes").Help("Disabled checklists can't be added to audits (AUD-001).");
                s.Text("standard", "Standard / regulation", 6).Placeholder("ISO 13485:2016");
                s.Keyword("program", "Audit program", "aud_program", 6);
                s.TextArea("description", "Purpose and instructions");
            }, icon: "bi-card-checklist")
            .Subform("questions", "Questions", s =>
            {
                s.Text("question", "Question", 5).Required();
                s.Text("clause", "Clause", 2).Placeholder("ISO 13485:2016 §7.5.6");
                s.Text("hint", "Hint / evidence to look for", 3);
                s.Number("out_of", "Out of", 1).Min(0).Max(100).Decimals(1);
                s.YesNo("required", "Req.", 1);
            }, rowNoun: "question", required: true, icon: "bi-question-square",
               description: "Copied into each audit that uses this checklist. \"Out of\" sets the maximum score (scoring audits).")
        );

        app.Form("audit_type", "Audit type", f => f
            .Icon("bi-tags")
            .Description("Evaluation type, default checklists, guidance text and frequency for a kind of audit.")
            .Numbering("ATY-", 3)
            .Title("{name}")
            .NonWorkflow()
            .Section("type", "Audit type", s =>
            {
                s.Text("name", "Name", 8).Required().MaxLength(120);
                s.YesNo("enabled", "Enabled", 4).Default("yes");
                s.Keyword("evaluation", "Evaluation", "aud_evaluation").Required().Default("scoring").Help("Scoring adds per-question scores and a total (AUD-004).");
                s.Keyword("program", "Audit program", "aud_program");
                s.Number("frequency_months", "Frequency (months)", 4).Min(1).Max(60).Decimals(0).Default("12");
                s.Link("checklists", "Default checklists", "checklist_template").Help("Their questions are copied into each new audit of this type.");
                s.Number("acceptable_pct", "Acceptable at ≥ %", 3).Min(0).Max(100).Default("85");
                s.Number("marginal_pct", "Marginal at ≥ %", 3).Min(0).Max(100).Default("70");
            }, icon: "bi-tags")
            .Section("guidance", "Guidance text", s =>
            {
                s.TextArea("introduction", "Introduction");
                s.TextArea("scoring_guidelines", "Scoring guidelines");
            }, icon: "bi-journal-text"));

        // ------------------------------------------------------------------ audit plans (AUD-010..014)
        app.Form("audit_plan", "Audit plan", f => f
            .Icon("bi-calendar2-range")
            .Description("An audit program for a period: the planned audits, approved and then scheduled one by one.")
            .Numbering("APL-")
            .Title("{name}")
            .Section("plan_info", "Plan information", s =>
            {
                s.Text("name", "Plan name", 12).Required().MaxLength(150);
                s.Keyword("program", "Program", "aud_program").Required().Header();
                s.Date("period_start", "Period start").Required();
                s.Date("period_end", "Period end").Required().Header();
                s.User("lead_auditor", "Program lead auditor").Required().Header();
                s.Date("effective_date", "Effective date");
                s.Date("review_date", "Review date");
                s.Users("distribution", "Distribution list", 12);
                s.TextArea("scope", "Scope and objectives");
                s.Link("previous_plan", "Previous plan", "audit_plan");
            }, icon: "bi-calendar2-range")
            .Subform("planned_audits", "Planned audits", s =>
            {
                s.Text("auditee", "Auditee / area", 3).Required();
                s.Keyword("entity_kind", "Entity", "aud_entity", 2);
                s.Link("audit_type", "Audit type", "audit_type").Width(2);
                s.Date("planned_start", "Planned start", 2).Required();
                s.Date("planned_end", "Planned end", 1);
                s.User("planned_lead", "Lead auditor", 2);
            }, rowNoun: "audit", required: true, icon: "bi-list-task",
               description: "Actions › Schedule audit creates the audit in Scheduled, linked both ways (AUD-012). Rows with a scheduled audit are removed with Actions › Remove planned audit, which voids the audit (INV-165).")
            .Section("additional", "Additional (unplanned) audits", s =>
            {
                s.Link("additional_audits", "Additional audits", "audit").Help("Link pending audits that weren't in the original plan (AUD-010).");
            }, icon: "bi-plus-square")
            .Workflow("audit_plan", "Audit plan", w =>
            {
                w.Start("draft", "Draft").Purpose("Define the program period, scope and the audits planned for it.")
                    .DueDays(10).Edit("plan_info", "planned_audits", "additional");
                w.Phase("approval", "Approval").Purpose("Quality approves the audit program.")
                    .AssignGroup("qa-approvers").DueDays(5).Edit("planned_audits", "additional").ReadOnlyRest();
                w.Phase("active", "Active").Purpose("Schedule the planned audits as their dates approach. Complete the plan at the end of the period.")
                    .AssignField("lead_auditor").DueFrom("period_end").DueDays(120).Edit("additional").ReadOnlyRest();
                w.Completed("completed", "Completed");
                w.Voided();
                w.Forward("draft", "approval", label: "Submit for approval");
                w.Void("draft");
                w.Forward("approval", "active", label: "Approve plan", sign: "Approved");
                w.Back("approval", "draft", label: "Return to draft");
                w.Void("approval");
                w.Forward("active", "completed", label: "Complete plan");
                w.Back("active", "approval", label: "Back to approval");
            }));

        // ------------------------------------------------------------------ audits (AUD-020..028)
        app.Form("audit", "Audit", f => f
            .Icon("bi-clipboard2-check")
            .Description("An internal, supplier or external audit with a scored checklist, findings and a summary report.")
            .Numbering("AUD-")
            .Title("{subject}")
            .Section("audit_info", "Audit information", s =>
            {
                s.Text("subject", "Audit", 12).Required().MaxLength(200).Placeholder("For example: Final assembly – ISO 13485 §7.5 process audit");
                s.Keyword("audit_kind", "Kind", "aud_kind").Required().Header().Default("internal");
                s.Link("audit_type", "Audit type", "audit_type").Help("Its default checklists are copied in when the audit is created.");
                s.Text("auditee", "Auditee / area", 6).Required();
                s.Keyword("entity_kind", "Entity", "aud_entity", 3);
                s.Keyword("evaluation", "Evaluation", "aud_evaluation", 3).Header();
                s.User("lead_auditor", "Lead auditor").Required().Header();
                s.DateTime("start_at", "Start").Required();
                s.DateTime("end_at", "End");
                s.Text("criteria", "Audit criteria", 12).Placeholder("ISO 13485:2016, 21 CFR 820, SOP-QA-012");
                s.TextArea("scope", "Scope and objectives");
                s.Link("audit_plan", "Audit plan", "audit_plan").ReadOnly();
                s.Link("previous_audit", "Previous audit", "audit");
            }, icon: "bi-info-circle")
            .Subform("participants", "Participants", s =>
            {
                s.User("participant", "Internal participant", 3);
                s.Text("external_name", "Or external name", 3);
                s.Text("company", "Company", 3);
                s.Keyword("role", "Role", "aud_role", 3).Required();
            }, tab: "Team & documents", rowNoun: "participant", icon: "bi-people")
            .Subform("documents_reviewed", "Documents reviewed", s =>
            {
                s.Link("document", "Document");
                s.Text("doc_ref", "Number / title (external)", 4);
                s.Text("doc_revision", "Revision", 2);
            }, tab: "Team & documents", rowNoun: "document", icon: "bi-files")
            .Subform("checklist", "Checklist questions", s =>
            {
                s.Text("question", "Question", 4).Required();
                s.Text("clause", "Clause", 2);
                s.Number("out_of", "Out of", 1).Min(0).Max(100).Decimals(1);
                s.YesNo("required", "Req.", 1);
                s.Keyword("classification", "Classification", "aud_classification", 2);
                s.Number("score", "Score", 1).Min(0).Decimals(1);
                s.Text("comments", "Comments / objective evidence", 3);
            }, tab: "Checklist", rowNoun: "question", icon: "bi-ui-checks-grid",
               description: "Score ≤ out of (INV-067). Required questions must be answered before the audit leaves Scheduled (AUD-022). Questions can't be removed once the audit is scheduled: neutralize them by clearing Req. and Out of (AUD-023).")
            .Section("summary", "Audit summary", s =>
            {
                s.Number("total_score", "Total score", 3).ReadOnly();
                s.Number("max_score", "Maximum score", 3).ReadOnly();
                s.Number("score_pct", "Score %", 3).ReadOnly().Decimals(1);
                s.Text("answered", "Questions answered", 3).ReadOnly();
                s.Number("count_major", "Major NCs", 2).ReadOnly().Decimals(0);
                s.Number("count_minor", "Minor NCs", 2).ReadOnly().Decimals(0);
                s.Number("count_observation", "Observations", 2).ReadOnly().Decimals(0);
                s.Number("count_ofi", "OFIs", 2).ReadOnly().Decimals(0);
                s.Number("count_conforming", "Conforming", 2).ReadOnly().Decimals(0);
                s.YesNo("has_nc", "Nonconformances", 2).ReadOnly();
                s.Keyword("suggested_result", "Suggested result", "aud_result").ReadOnly();
                s.Keyword("result", "Audit result", "aud_result").RequiredIn("scheduled").Header();
                s.Keyword("frequency_rec", "Frequency recommendation", "aud_frequency").RequiredIn("scheduled");
                s.TextArea("conclusions", "Conclusions").RequiredIn("scheduled");
                s.TextArea("frequency_comments", "Frequency comments");
            }, tab: "Summary", icon: "bi-clipboard-data")
            .Section("report", "Report", s =>
            {
                s.User("report_by", "Report prepared by").Default("$me");
                s.Date("report_date", "Report date");
                s.Users("report_distribution", "Report distribution", 12);
            }, tab: "Summary", icon: "bi-file-earmark-text")
            .Section("audit_system", "Scheduling", s =>
            {
                s.Date("scheduled_due", "Scheduled due").ReadOnly();
            }, tab: "Summary")
            .Workflow("audit", "Audit", w =>
            {
                w.Start("draft", "Draft").Purpose("Plan the audit: scope, team, criteria and checklists.")
                    .DueDays(10).Edit("audit_info", "participants", "documents_reviewed", "checklist", "summary", "report").Hide("audit_system");
                w.Phase("scheduled", "Scheduled").Purpose("Conduct the audit: answer the checklist, classify findings, raise CAPAs and recommendations, and write the summary.")
                    .AssignField("lead_auditor").DueFrom("scheduled_due").DueDays(10)
                    .Edit("audit_info", "participants", "documents_reviewed", "checklist", "summary", "report").Hide("audit_system").Activities("recommendation", "action_item");
                w.Phase("report_approval", "Report Approval").Purpose("Quality reviews and approves the audit report.")
                    .AssignGroup("qa-approvers").DueDays(5).Edit("summary", "report").ReadOnlyRest().Hide("audit_system").Activities("recommendation");
                w.Completed("completed", "Completed").Hide("audit_system");
                w.Voided().Hide("audit_system");
                w.Forward("draft", "scheduled", label: "Schedule audit");
                w.Void("draft");
                w.Forward("scheduled", "report_approval", label: "Submit report for approval");
                w.Back("scheduled", "draft", label: "Back to draft");
                w.Void("scheduled");
                w.Forward("report_approval", "completed", label: "Approve report", sign: "Report approved");
                w.Back("report_approval", "scheduled", label: "Return to lead auditor");
            }));

        // ------------------------------------------------------------------ recommendations (AUD-025)
        app.Form("recommendation", "Recommendation", f => f
            .Icon("bi-lightbulb")
            .Description("A recommendation raised from an audit finding (observation or opportunity for improvement).")
            .Numbering("REC-")
            .Title("{subject}")
            .Activity("audit")
            .Section("rec_details", "Recommendation", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200);
                s.Text("clause", "Clause", 4);
                s.User("assignee", "Assigned to").Required().Header();
                s.Date("due_date", "Due date").Required().Header().Default("$today+30");
                s.TextArea("finding", "Finding");
                s.YesNo("verification_required", "Verification required?").Default("no");
                s.User("verifier", "Verification by").VisibleWhen("verification_required=yes").RequiredIn("draft");
            }, icon: "bi-lightbulb")
            .Section("rec_action", "Action taken", s =>
            {
                s.TextArea("action_taken", "Action taken").RequiredIn("open");
                s.Date("completed_on", "Completed on").Default("$today").RequiredIn("open");
            }, icon: "bi-tools")
            .Section("rec_verification", "Verification", s =>
            {
                s.Keyword("verification_result", "Result", "verification_result").RequiredIn("verification");
                s.TextArea("verification_comments", "Verification comments");
            }, icon: "bi-shield-check")
            .Section("rec_system", "Source", s => { s.Text("finding_row", "Finding row").ReadOnly(); })
            .Workflow("recommendation", "Recommendation", w =>
            {
                w.Start("draft", "Draft").Purpose("Describe the recommendation and who acts on it.").DueDays(3).Edit("rec_details").Hide("rec_action", "rec_verification", "rec_system");
                w.Phase("open", "Open").Purpose("Act on the recommendation and record what was done.")
                    .AssignField("assignee").DueFrom("due_date").Edit("rec_action").ReadOnly("rec_details").Hide("rec_verification", "rec_system");
                w.Phase("verification", "Verification").Purpose("Verify the recommendation was acted on effectively.")
                    .AssignField("verifier").DueDays(5).Edit("rec_verification").ReadOnly("rec_details", "rec_action").Hide("rec_system");
                w.Completed("completed", "Completed").Hide("rec_system");
                w.Voided().Hide("rec_system");
                w.Forward("draft", "open", label: "Assign recommendation");
                w.Forward("open", "verification", label: "Send for verification", when: "verification_required=yes");
                w.Forward("open", "completed", label: "Complete", sign: "Completed", when: "verification_required!=yes");
                w.Back("open", "draft");
                w.Void("open");
                w.Forward("verification", "completed", label: "Verify and close", sign: "Verified", when: "verification_result=verified");
                w.Back("verification", "open", label: "Not verified: back to assignee");
                w.Void("verification");
            }));

        // ------------------------------------------------------------------ views (AUD-032)
        app.View("mine", "My audits", v => v.Category("1. My work").Forms("audit").Mine()
            .Columns("number", "title", "phase", "due", "auditee", "start_at").Sort("due", false).Default());
        app.View("my_recs", "My recommendations", v => v.Category("1. My work").Forms("recommendation").Mine()
            .Columns("number", "title", "phase", "due", "assignee"));
        app.View("open_by_assignee", "Open audits by assignee", v => v.Category("2. Audits").Forms("audit").Open()
            .Columns("number", "title", "assignees", "phase", "due", "lead_auditor").Sort("phase", false));
        app.View("by_status", "Audits by status", v => v.Category("2. Audits").Forms("audit").All()
            .Columns("number", "phase", "title", "audit_kind", "auditee", "start_at", "result").Sort("phase", false));
        app.View("by_number", "Audits by number", v => v.Category("2. Audits").Forms("audit").All()
            .Columns("number", "title", "audit_kind", "auditee", "phase", "score_pct", "result"));
        app.View("scheduled", "Scheduled audits", v => v.Category("2. Audits").Forms("audit").Open().Phases("scheduled")
            .Columns("number", "title", "auditee", "start_at", "end_at", "lead_auditor", "due").Sort("start_at", false).Calendar("start_at"));
        app.View("with_nc", "Audits with nonconformances", v => v.Category("3. Findings").Forms("audit").All().Filter("has_nc", "eq", "yes")
            .Columns("number", "title", "auditee", "count_major", "count_minor", "result", "phase"));
        app.View("completed", "Completed audits", v => v.Category("2. Audits").Forms("audit").Completed()
            .Columns("number", "title", "auditee", "score_pct", "result", "frequency_rec", "modified").Sort("modified"));
        app.View("recs_open", "Open recommendations", v => v.Category("3. Findings").Forms("recommendation").Open()
            .Columns("number", "title", "phase", "due", "assignee", "clause"));
        app.View("recs_overdue", "Overdue findings: recommendations", v => v.Category("3. Findings").Forms("recommendation").Overdue()
            .Columns("number", "title", "due", "assignee", "clause").Sort("due", false));
        app.View("capas", "Open CAPAs from internal audits", v => v.Category("3. Findings").Forms("capa").Open().Filter("source", "eq", "internal_audit")
            .Columns("number", "title", "phase", "due", "priority", "owner"));
        app.View("capas_external", "Open CAPAs from external audits", v => v.Category("3. Findings").Forms("capa").Open().Filter("source", "eq", "external_audit")
            .Columns("number", "title", "phase", "due", "priority", "owner"));
        app.View("plans", "Audit plans", v => v.Category("4. Programs").Forms("audit_plan").All()
            .Columns("number", "title", "program", "phase", "period_start", "period_end", "lead_auditor"));
        app.View("plans_active", "Active audit plans", v => v.Category("4. Programs").Forms("audit_plan").Open().Phases("active")
            .Columns("number", "title", "program", "period_end", "lead_auditor"));
        app.View("checklists", "Checklist templates", v => v.Category("5. Setup").Forms("checklist_template").All().Filter("enabled", "eq", "yes")
            .Columns("number", "title", "standard", "program", "modified").Sort("number", false));
        app.View("checklists_disabled", "Disabled checklist templates", v => v.Category("5. Setup").Forms("checklist_template").All().Filter("enabled", "eq", "no")
            .Columns("number", "title", "standard", "modified"));
        app.View("types", "Audit types", v => v.Category("5. Setup").Forms("audit_type").All()
            .Columns("number", "title", "evaluation", "program", "frequency_months", "enabled").Sort("number", false));
    }

    public Task SeedDemoAsync(DemoContext d) => AuditsDemo.SeedAsync(d);
}
