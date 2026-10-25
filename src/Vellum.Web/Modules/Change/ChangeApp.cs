using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Change;

/// <summary>
/// Change Management §13 (template-driven model, parity with Reliance Change Management 2.0.0):
/// Draft → Review → Approval (every approver at once, one approval per approval type, INV-148) → Implementation
/// (action plan → Action Items, INV-149) → Verification (only when required, CHG-023) → Completed, with the regulatory
/// filing gate (PAS / CBE-30, INV-161, SAF-022). Change templates are copied on create, so editing them never changes
/// existing changes (INV-147). Impact assessments are linked activities (CHG-030).
/// </summary>
public sealed class ChangeApp : IPackagedApp
{
    public string Key => "change";
    public int Order => 40;

    public void Define(AppBuilder app)
    {
        app.Name("Change Management")
            .Description("Propose, assess, approve, implement and verify changes to products, processes, specifications, suppliers and documents, with regulatory filing control.")
            .Icon("bi-arrow-repeat").Color("#0369a1").Suite("Quality").Order(20).Version("2.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@engineering", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("@regulatory", AppRole.Author)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("@change-board", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        app.Keywords("chg_impact_level", "Change impact level", k => k
            .Add("low", "Low", "secondary").Add("medium", "Medium", "warning").Add("high", "High", "danger"));
        app.Keywords("chg_approval_type", "Change approval type", k => k
            .Add("quality", "Quality").Add("regulatory", "Regulatory").Add("engineering", "Engineering").Add("manufacturing", "Manufacturing")
            .Add("supply_chain", "Supply chain").Add("validation", "Validation").Add("ehs", "EHS").Add("document_control", "Document control"));
        app.Keywords("chg_reason", "Change reason", k => k
            .Add("quality_improvement", "Quality improvement").Add("cost_reduction", "Cost reduction").Add("regulatory", "Regulatory requirement")
            .Add("capa", "CAPA / nonconformance").Add("supplier", "Supplier change").Add("customer", "Customer request")
            .Add("capacity", "Capacity / second source").Add("obsolescence", "Obsolescence").Add("safety", "Safety"));
        app.Keywords("chg_reg_impact", "Regulatory impact", k => k
            .Add("substantial", "Substantial", "danger").Add("moderate", "Moderate", "warning").Add("minor", "Minor", "info")
            .Add("none", "No regulatory impact", "secondary"));
        app.Keywords("chg_filing", "Regulatory filing", k => k
            .Add("pas", "Prior Approval Supplement (PAS)", "danger").Add("cbe30", "CBE-30 (effected in 30 days)", "warning")
            .Add("cbe0", "CBE-0 (effected immediately)", "info").Add("annual", "Annual report", "secondary").Add("none", "No filing", "secondary"));
        app.Keywords("chg_doc_action", "Affected document action", k => k
            .Add("change", "Change", "info").Add("obsolete", "Obsolete", "secondary").Add("new", "New", "success"));
        app.Keywords("chg_ia_impact", "Assessed impact", k => k
            .Add("high", "High", "danger").Add("medium", "Medium", "warning").Add("low", "Low", "info").Add("na", "Not applicable", "secondary"));
        app.Keywords("chg_area", "Impacted area", k => k
            .Add("quality", "Quality system").Add("regulatory", "Regulatory").Add("manufacturing", "Manufacturing").Add("engineering", "Design / engineering")
            .Add("validation", "Validation").Add("labeling", "Labeling").Add("supply_chain", "Supply chain").Add("ehs", "EHS")
            .Add("training", "Training").Add("it", "IT / software").Add("customer", "Customers"));

        // ------------------------------------------------------------------ change templates (CHG-001..003)
        app.Form("change_template", "Change template", f => f
            .Icon("bi-file-earmark-ruled")
            .Description("A reusable blueprint shown as the change type. Its defaults are copied into each new change (INV-147).")
            .Numbering("CHT-", 3)
            .Title("{name}")
            .NonWorkflow()
            .Section("template", "Change type", s =>
            {
                s.Text("name", "Change type name", 8).Required().MaxLength(80).Placeholder("For example: Engineering change");
                s.YesNo("enabled", "Enabled", 4).Default("yes").Help("Disabled templates aren't offered for new changes. Existing changes never change (INV-147).");
                s.TextArea("description", "When to use this template");
                s.Keyword("default_impact", "Default impact level", "chg_impact_level");
                s.YesNo("all_approvers", "Every approver of a type must approve").Default("no")
                    .Help("Off: one approval per approval type is enough (CHG-021).");
                s.YesNo("ia_required", "Impact assessments must be completed").Default("no")
                    .Help("On: the change can't be completed while impact assessments are open (CHG-031).");
            }, icon: "bi-ui-checks")
            .Section("template_verification", "Verification", s =>
            {
                s.YesNo("verification_required", "Verification of the implemented change required").Default("yes")
                    .Help("Adds the Verification phase after Implementation (CHG-023).");
                s.TextArea("verification_instructions", "Default verification instructions");
            }, icon: "bi-patch-check")
            .Section("template_regulatory", "Regulatory reporting defaults", s =>
            {
                s.Keyword("default_reg_impact", "Default regulatory impact", "chg_reg_impact");
                s.Keyword("default_filing", "Default filing", "chg_filing");
            }, icon: "bi-bank")
            .Subform("template_approvals", "Required approvals", s =>
            {
                s.Keyword("approval_type", "Approval type", "chg_approval_type", 4).Required();
                s.Users("approvers", "Approvers", 8).Required();
            }, rowNoun: "approval", required: true, icon: "bi-people",
               description: "Copied into each change. Every approver is assigned at once in Approval.")
            .Subform("template_impacts", "Impact assessments", s =>
            {
                s.Keyword("area", "Impacted area", "chg_area", 6).Required();
                s.User("assessor", "Assessor", 6);
            }, rowNoun: "assessment", icon: "bi-bullseye")
            .Subform("template_plan", "Default action plan", s =>
            {
                s.Text("action", "Action", 7).Required();
                s.Number("days", "Days to complete", 3).Min(1).Max(365).Decimals(0).Default("10");
                s.YesNo("verify", "Verify?", 2);
            }, rowNoun: "action", icon: "bi-list-check"));

        // ------------------------------------------------------------------ change record (CHG-010)
        const string infoSections = "change_info";
        string[] planning = [infoSections, "approvals", "impacts", "plan", "documents", "post_impl", "verification_plan", "regulatory", "costs"];
        app.Form("change", "Change", f => f
            .Icon("bi-arrow-repeat")
            .Description("A controlled change from proposal through impact assessment, approval, implementation and verification.")
            .Numbering("CHG-")
            .Title("{subject}")
            .Section("change_info", "Change information", s =>
            {
                s.Link("change_template", "Change template", "change_template").Required()
                    .Help("Pre-fills approvals, impact assessments, the action plan, verification and regulatory defaults. Values are copied, so later template edits never change this change (INV-147).");
                s.Text("change_type", "Change type", 6).ReadOnly().Header();
                s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("A short, specific description of the change");
                s.User("owner", "Change owner").Required().Header().Default("$me");
                s.Date("planned_date", "Planned implementation date").Required().Header();
                s.Keyword("impact_level", "Change impact level", "chg_impact_level").Required().Header();
                s.Keyword("reason_code", "Reason for change", "chg_reason").Required();
                s.Text("products", "Products / processes affected", 8).Placeholder("For example: VX-200 infusion pump, housing assembly");
                s.TextArea("current_state", "Current state").Required();
                s.TextArea("proposed_change", "Proposed change").Required();
                s.TextArea("justification", "Reason / justification").Required();
            }, icon: "bi-info-circle")
            .Section("costs", "Costs", s =>
            {
                s.Currency("estimated_cost", "Estimated cost");
                s.Currency("actual_cost", "Actual cost");
                s.Text("cost_notes", "Cost notes", 6);
            }, icon: "bi-cash-coin", collapsed: true)
            .Subform("approvals", "Approvals", s =>
            {
                s.Keyword("approval_type", "Approval type", "chg_approval_type", 4).Required();
                s.Users("approvers", "Approvers", 8).Required();
            }, tab: "Approvals", rowNoun: "approval", required: true, icon: "bi-people",
               description: "Everyone listed is assigned at once in Approval. The change moves on when every approval type has at least one approval (INV-148).")
            .Subform("impacts", "Impact assessments", s =>
            {
                s.Keyword("area", "Impacted area", "chg_area", 5).Required();
                s.User("assessor", "Assessor", 4).Required();
                s.Date("ia_due", "Due", 3);
            }, tab: "Impact", rowNoun: "assessment", icon: "bi-bullseye",
               description: "Actions › Assign impact assessments creates a linked assessment record for each row, straight in Assessment (CHG-030).")
            .Subform("plan", "Action plan", s =>
            {
                s.Text("action", "Action", 4).Required();
                s.User("assignee", "Assign to", 3).Help("Leave empty to assign the change owner.");
                s.Number("days", "Days", 1).Required().Min(1).Max(365).Decimals(0);
                s.YesNo("verify", "Verify?", 2);
                s.User("plan_verifier", "Verifier", 2);
            }, tab: "Implementation", rowNoun: "action", icon: "bi-list-check",
               description: "Each row becomes an Action Item when the change enters Implementation; all must be complete before the change moves on (CHG-040, INV-149).")
            .Subform("documents", "Affected documents", s =>
            {
                s.Keyword("doc_action", "Action", "chg_doc_action", 2).Required();
                s.Link("document", "Document").Help("The current revision, or the new draft once it exists.");
                s.Text("doc_ref", "Number / title", 3);
                s.User("reviser", "Reviser", 2);
                s.Date("draft_due", "Draft due", 2);
            }, tab: "Implementation", rowNoun: "document", icon: "bi-files",
               description: "Linked documents must reach their target state (new revision effective, or obsolete) before the change is completed (INV-160).")
            .Subform("post_impl", "Post-implementation actions", s =>
            {
                s.Text("post_action", "Action", 4).Required();
                s.User("post_assignee", "Assign to", 3);
                s.Number("post_days", "Days", 1).Required().Min(1).Max(365).Decimals(0);
                s.YesNo("post_verify", "Verify?", 2);
                s.User("post_verifier", "Verifier", 2);
            }, tab: "Implementation", rowNoun: "action", icon: "bi-check2-all",
               description: "Follow-up actions (training, monitoring, stock purge). They become Action Items and must be complete before Verification (CHG-044).")
            .Section("verification_plan", "Verification plan", s =>
            {
                s.YesNo("verification_required", "Verification of implemented change required")
                    .Help("Adds the Verification phase after Implementation (CHG-023).");
                s.User("verifier", "Verification by").VisibleWhen("verification_required=yes").RequiredIn("review")
                    .Help("Someone other than the change owner.");
                s.TextArea("verification_instructions", "Verification to be performed").VisibleWhen("verification_required=yes");
            }, tab: "Verification", icon: "bi-clipboard2-check")
            .Section("verification", "Verification of implemented change", s =>
            {
                s.TextArea("verification_evidence", "Objective evidence").RequiredIn("verification")
                    .Help("What was checked, where, and the result (INV-149).");
                s.Date("verified_on", "Verified on").Default("$today");
                s.Attachment("verification_files", "Evidence files");
            }, tab: "Verification", icon: "bi-patch-check")
            .Section("regulatory", "Regulatory reporting", s =>
            {
                s.Prompt("reg_prompt", "Classify the change by its impact on safety and effectiveness: Substantial → Prior Approval Supplement · Moderate → CBE-30 or CBE-0 · Minor → Annual report (CHG-050).");
                s.Keyword("reg_impact", "Regulatory impact", "chg_reg_impact", 6);
                s.Keyword("filing", "Filing", "chg_filing", 6);
                s.Text("agency", "Agency", 4).Placeholder("FDA CDRH").VisibleWhen("filing=pas||filing=cbe30||filing=cbe0");
                s.Text("submission_number", "Submission number", 4).VisibleWhen("filing=pas||filing=cbe30||filing=cbe0");
                s.Date("submission_date", "Submitted on", 4).VisibleWhen("filing=pas||filing=cbe30||filing=cbe0");
                s.Date("agency_response_date", "Agency response received", 4).VisibleWhen("filing=pas||filing=cbe30")
                    .Help("For CBE-30, a response before day 30 lets the change complete early.");
                s.Date("approval_date", "Agency approval date", 4).VisibleWhen("filing=pas")
                    .Help("A PAS change can't be completed until this and the approval letter are recorded (INV-161).");
                s.Text("annual_report_period", "Annual report period", 4).VisibleWhen("filing=annual").Placeholder("For example: FY2026 annual report");
                s.TextArea("reg_rationale", "Classification rationale");
                s.Attachment("approval_evidence", "Agency approval letter (evidence)").VisibleWhen("filing=pas");
            }, tab: "Regulatory", icon: "bi-bank")
            .Section("routing", "Routing", s =>
            {
                s.Users("approver_pool", "All approvers").ReadOnly();
                s.YesNo("all_approvers", "Every approver of a type must approve").ReadOnly();
                s.YesNo("ia_required", "Impact assessments must be completed").ReadOnly();
            }, tab: "Approvals", icon: "bi-signpost")
            .Workflow("change", "Change", w =>
            {
                w.Start("draft", "Draft").Purpose("Describe the change, choose approvers, plan impact assessments, the action plan and the regulatory classification.")
                    .DueDays(10).Edit(planning).Hide("verification", "routing").Activities("impact_assessment");
                w.Phase("review", "Review").Purpose("The change owner reviews the proposal, assigns impact assessments and confirms the approvers.")
                    .AssignField("owner").DueDays(5).Edit(planning).Hide("verification", "routing").Activities("impact_assessment");
                w.Phase("approval", "Approval").Purpose("Every approver is assigned at once. The change moves on when each approval type has at least one approval; any approver can reject it back to Review.")
                    .AssignField("approver_pool").Voting(100).DueDays(7).ReadOnlyRest().Hide("verification", "routing").Activities("impact_assessment");
                w.Phase("implementation", "Implementation").Purpose("Carry out the action plan. Every action item must be complete, and regulatory preconditions met, before the change moves on.")
                    .AssignField("owner").DueDays(20).Edit("post_impl", "documents", "regulatory", "costs").ReadOnlyRest().Hide("verification", "routing")
                    .MustComplete("action_item").Activities("action_item", "impact_assessment");
                w.Phase("verification", "Verification").Purpose("Independently verify the implemented change and record objective evidence.")
                    .AssignField("verifier").DueDays(10).Edit("verification", "regulatory").ReadOnlyRest().Hide("routing").Activities("action_item");
                w.Completed("completed", "Completed").Hide("routing");
                w.Voided().Hide("routing");

                w.Forward("draft", "review", label: "Submit for review");
                w.Void("draft");
                w.Forward("review", "approval", label: "Send for approval");
                w.Back("review", "draft", label: "Back to draft");
                w.Void("review");
                w.Forward("approval", "implementation", label: "Approve", sign: "Approved");
                w.Back("approval", "review", label: "Reject: back to review");
                w.Forward("implementation", "verification", label: "Send to verification", when: "verification_required=yes");
                w.Forward("implementation", "completed", label: "Complete change", sign: "Change implemented", when: "verification_required!=yes");
                w.Back("implementation", "review", label: "Back to review");
                w.Back("implementation", "draft", label: "Back to draft");
                w.Forward("verification", "completed", label: "Verify and complete", sign: "Verified effective");
                w.Back("verification", "implementation", label: "Not verified: back to implementation");
            }));

        // ------------------------------------------------------------------ impact assessment (CHG-030)
        app.Form("impact_assessment", "Impact Assessment", f => f
            .Icon("bi-bullseye")
            .Description("An assessment of a change's impact on one area (quality, regulatory, manufacturing…).")
            .Numbering("IA-")
            .Title("{area} impact – {change_ref}")
            .Activity("change")
            .Section("ia_request", "Assessment request", s =>
            {
                s.Keyword("area", "Impacted area", "chg_area").Required().Header();
                s.User("assessor", "Assessor").Required().Header();
                s.Date("due_date", "Due date").Default("$today+10").Header();
                s.Text("change_ref", "Change", 12).ReadOnly();
                s.TextArea("instructions", "What to assess");
            }, icon: "bi-bullseye")
            .Section("ia_result", "Assessment", s =>
            {
                s.Keyword("impact", "Impact", "chg_ia_impact").RequiredIn("assessment").Header();
                s.TextArea("assessment", "Impact description").RequiredIn("assessment");
                s.TextArea("actions_needed", "Actions needed").Help("Add them to the change's action plan or post-implementation actions.");
            }, icon: "bi-clipboard-data")
            .Workflow("impact_assessment", "Impact assessment", w =>
            {
                w.Start("draft", "Draft").Purpose("Describe what needs assessing and who assesses it.").DueDays(3).Edit("ia_request").Hide("ia_result");
                w.Phase("assessment", "Assessment").Purpose("Assess the impact of the change on your area and describe it.")
                    .AssignField("assessor").DueFrom("due_date").DueDays(5).Edit("ia_result").ReadOnly("ia_request");
                w.Completed("completed", "Completed");
                w.Voided();
                w.Forward("draft", "assessment", label: "Assign assessment");
                w.Void("draft");
                w.Forward("assessment", "completed", label: "Complete assessment", sign: "Assessed");
                w.Back("assessment", "draft");
                w.Void("assessment");
            }));

        // ------------------------------------------------------------------ views (CHG-060, CHG-061)
        app.View("mine", "My changes", v => v.Category("1. My work").Forms("change").Mine()
            .Columns("number", "title", "change_type", "phase", "due", "impact_level").Sort("due", false).Default());
        app.View("my_assessments", "My impact assessments", v => v.Category("1. My work").Forms("impact_assessment").Mine()
            .Columns("number", "title", "phase", "due", "area"));
        app.View("open", "Open changes", v => v.Category("2. Changes").Forms("change").Open()
            .Columns("number", "title", "change_type", "phase", "due", "impact_level", "owner"));
        app.View("approval", "Awaiting approval", v => v.Category("2. Changes").Forms("change").Open().Phases("approval")
            .Columns("number", "title", "change_type", "assignees", "due", "impact_level"));
        app.View("regulatory", "Regulatory filings", v => v.Category("2. Changes").Forms("change").All().Filter("filing", "set")
            .Columns("number", "title", "phase", "filing", "submission_date", "approval_date", "owner"));
        app.View("pas", "PAS awaiting agency approval", v => v.Category("2. Changes").Forms("change").Open().Filter("filing", "eq", "pas").Filter("approval_date", "empty")
            .Columns("number", "title", "phase", "submission_number", "submission_date", "owner"));
        app.View("aging", "Aging: open changes by age", v => v.Category("3. Timeliness").Forms("change").Open()
            .Columns("number", "title", "change_type", "phase", "created", "due", "owner").Sort("created", false));
        app.View("overdue", "Overdue changes", v => v.Category("3. Timeliness").Forms("change").Overdue()
            .Columns("number", "title", "phase", "due", "owner", "change_type").Sort("due", false));
        app.View("planned", "Planned implementation calendar", v => v.Category("3. Timeliness").Forms("change").Open()
            .Columns("number", "title", "planned_date", "phase", "owner").Sort("planned_date", false).Calendar("planned_date"));
        app.View("completed", "Completed changes", v => v.Category("4. Closed").Forms("change").Completed()
            .Columns("number", "title", "change_type", "impact_level", "owner", "modified").Sort("modified"));
        app.View("voided", "Voided changes", v => v.Category("4. Closed").Forms("change").Voided()
            .Columns("number", "title", "change_type", "owner", "modified"));
        app.View("ia_open", "Open impact assessments", v => v.Category("5. Impact assessments").Forms("impact_assessment").Open()
            .Columns("number", "title", "phase", "due", "assessor", "area"));
        app.View("ia_completed", "Completed impact assessments", v => v.Category("5. Impact assessments").Forms("impact_assessment").Completed()
            .Columns("number", "title", "impact", "assessor", "modified"));
        app.View("ia_voided", "Voided impact assessments", v => v.Category("5. Impact assessments").Forms("impact_assessment").Voided()
            .Columns("number", "title", "assessor", "modified"));
        app.View("actions", "Change action items", v => v.Category("6. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee"));
        app.View("templates", "Enabled change templates", v => v.Category("7. Setup").Forms("change_template").All().Filter("enabled", "eq", "yes")
            .Columns("number", "title", "default_impact", "verification_required", "modified").Sort("number", false));
        app.View("templates_disabled", "Disabled change templates", v => v.Category("7. Setup").Forms("change_template").All().Filter("enabled", "eq", "no")
            .Columns("number", "title", "modified"));
    }

    public Task SeedBaselineAsync(DemoContext d) => ChangeDemo.SeedTemplatesAsync(d);
    public Task SeedDemoAsync(DemoContext d) => ChangeDemo.SeedAsync(d);
}
