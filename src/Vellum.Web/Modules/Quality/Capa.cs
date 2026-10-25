using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// CAPA §12.4: Initiate → Initial Review → Root Cause &amp; Corrective Action Plan → Plan Approval → Implementation →
/// Effectiveness Check → Closed. Planned actions become Action Items on entering Implementation (CAPA-003, QE-002);
/// Effectiveness Check needs every Action Item closed (CAPA-004, INV-043); "not effective" reopens (INV-042).
/// 8D discipline sections show for 8D CAPAs (CAPA-006); risk-based priority sets phase due dates (CAPA-007);
/// cost tracking (QE-012). SCAR is its own form with the supplier workflow (CAPA-010).
/// </summary>
public sealed class CapaApp : IPackagedApp
{
    public string Key => "capa";
    public int Order => 30;

    private const string EightD = "capa_type=eight_d";

    public void Define(AppBuilder app)
    {
        app.Name("Corrective & Preventive Action")
            .Description("Investigate root causes, plan and verify corrective and preventive actions (CAPA, 8D, SCAR).")
            .Icon("bi-shield-check").Color("#7c3aed").Suite("Quality").Order(10).Version("1.1.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("@engineering", AppRole.Author)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        app.Keywords("capa_type", "CAPA type", k => k
            .Add("corrective", "Corrective", "primary").Add("preventive", "Preventive", "info")
            .Add("eight_d", "8D", "secondary").Add("scar", "SCAR", "warning"));
        app.Keywords("capa_source", "CAPA source", k => k
            .Add("internal_audit", "Internal audit").Add("external_audit", "External audit").Add("customer_complaint", "Customer complaint")
            .Add("ncr", "Nonconformance").Add("quality_event", "Quality event").Add("lab_investigation", "Lab investigation")
            .Add("incident", "EHS incident").Add("risk_assessment", "Risk assessment").Add("management_review", "Management review")
            .Add("supplier", "Supplier issue").Add("capa", "Related CAPA").Add("rma", "Customer return"));
        app.Keywords("priority", "Priority", k => k
            .Add("low", "Low", "secondary").Add("medium", "Medium", "info").Add("high", "High", "warning").Add("critical", "Critical", "danger"));
        app.Keywords("root_cause_category", "Root cause category (6M)", k => k
            .Add("man", "People").Add("machine", "Machine / equipment").Add("method", "Method / process").Add("material", "Material")
            .Add("measurement", "Measurement").Add("environment", "Environment").Add("design", "Design").Add("supplier", "Supplier"));
        app.Keywords("effectiveness_result", "Effectiveness result", k => k
            .Add("effective", "Effective", "success").Add("not_effective", "Not effective", "danger"));
        RiskPriority.Keywords(app);
        QualityCosts.Keywords(app);

        app.Form("capa", "CAPA", f =>
        {
            f.Icon("bi-shield-check")
                .Description("Corrective or preventive action with root cause analysis and effectiveness check.")
                .Numbering("CAPA-")
                .Title("{subject}")
                .Section("problem", "Problem identification", s =>
                {
                    s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("A short, specific description of the problem");
                    s.Keyword("capa_type", "Type", "capa_type").Required().Header().Default("corrective")
                        .Help("8D shows the eight-discipline sections (D0–D8) on the 8D tab.");
                    s.Keyword("source", "Source", "capa_source").Required();
                    s.Keyword("priority", "Priority", "priority").Required().Header().Default("medium")
                        .Help("Sets the default due date of each phase: critical work gets half the standard time, high 75%, low 150% (CAPA-007).");
                    s.User("owner", "CAPA owner").Required().Header().Default("$me");
                    s.Date("discovered_on", "Discovered on").Default("$today");
                    s.Text("product", "Product / process affected", 8);
                    s.Link("source_record", "Source record").Help("The audit finding, complaint, NCR or event that raised this CAPA.");
                    s.TextArea("description", "Problem description").Required();
                }, icon: "bi-search")
                .Section("risk_priority", "Risk-based priority", s =>
                {
                    s.Prompt("risk_prompt", "Rate the risk to get a suggested priority (severity × occurrence). If you choose a different priority, explain why: the justification is kept in the audit trail.");
                    s.Keyword("risk_severity", "Severity of harm", "quality_severity");
                    s.Keyword("risk_occurrence", "Probability of occurrence", "quality_occurrence");
                    s.Keyword("suggested_priority", "Risk-based priority", "priority").ReadOnly().Help("Calculated from severity × occurrence: 15+ or catastrophic = critical, 8+ = high, 4+ = medium.");
                    s.Link("risk_assessment", "Linked risk assessment").Help("A risk assessment record that supports the rating, if one exists.");
                    s.TextArea("priority_justification", "Priority justification").Help("Required when the priority differs from the risk-based priority.");
                }, icon: "bi-speedometer2", collapsed: false)
                .Section("containment", "Containment", s =>
                {
                    s.YesNo("containment_required", "Containment required?").Default("no");
                    s.User("containment_owner", "Containment owner").VisibleWhen("containment_required=yes");
                    s.Date("containment_date", "Contained on").VisibleWhen("containment_required=yes");
                    s.TextArea("containment_actions", "Immediate actions taken (quantity and location of suspect product)").VisibleWhen("containment_required=yes");
                }, icon: "bi-bandaid")
                .Section("closure", "Close without action", s =>
                {
                    s.Prompt("closure_prompt", "Use this only when Initial Review finds no action is needed. A justification is mandatory and kept in the audit trail.");
                    s.TextArea("closure_justification", "Justification");
                }, icon: "bi-x-octagon", optional: true)
                .Section("root_cause", "Root cause analysis", s =>
                {
                    s.Keyword("root_cause_category", "Root cause category", "root_cause_category").RequiredIn("root_cause_plan");
                    s.TextArea("root_cause", "Root cause statement").RequiredIn("root_cause_plan");
                }, tab: "Investigation", icon: "bi-diagram-3")
                .Subform("whys", "5 Whys", s =>
                {
                    s.Text("why", "Why?", 6);
                    s.Text("because", "Because…", 6);
                }, tab: "Investigation", rowNoun: "why", maxRows: 7, icon: "bi-question-circle")
                .Section("plan", "Corrective action plan", s =>
                {
                    s.TextArea("solution", "Develop solution").RequiredIn("root_cause_plan");
                }, tab: "Investigation", icon: "bi-signpost-split")
                .Subform("planned_actions", "Planned actions", s =>
                {
                    s.Text("action", "Action", 5).Required();
                    s.User("assignee", "Assign to", 3).Required();
                    s.Number("days", "Days to complete", 2).Required().Min(1).Max(365).Decimals(0);
                    s.YesNo("verify", "Verify?", 2);
                }, tab: "Investigation", rowNoun: "action", required: true, icon: "bi-list-check",
                   description: "Each row becomes an Action Item when the CAPA enters Implementation.")
                .Section("effectiveness", "Effectiveness check", s =>
                {
                    s.TextArea("effectiveness_criteria", "Success criteria").RequiredIn("root_cause_plan");
                    s.Date("effectiveness_due", "Check on").Help("For example 90 days after implementation.");
                    s.User("effectiveness_verifier", "Verified by").RequiredIn("implementation");
                    s.Keyword("effectiveness_result", "Result", "effectiveness_result").RequiredIn("effectiveness_check");
                    s.TextArea("effectiveness_evidence", "Evidence and conclusion").RequiredIn("effectiveness_check");
                }, tab: "Effectiveness", icon: "bi-graph-up-arrow");

            // CAPA-006: the eight disciplines, shown when the CAPA type is 8D.
            f.Section("d0", "D0 · Prepare", s =>
                {
                    s.Prompt("d0_prompt", "Assess whether the problem needs an 8D and take emergency response actions to protect the customer.").VisibleWhen(EightD);
                    s.TextArea("d0_symptoms", "Symptoms observed").VisibleWhen(EightD);
                    s.TextArea("d0_era", "Emergency response actions").VisibleWhen(EightD);
                }, tab: "8D", icon: "bi-flag")
                .Section("d1", "D1 · Team", s =>
                {
                    s.User("d1_champion", "Champion").VisibleWhen(EightD);
                    s.User("d1_leader", "Team leader").VisibleWhen(EightD);
                    s.Users("d1_team", "Team members", 12).VisibleWhen(EightD).Help("A cross-functional team with process and product knowledge.");
                }, tab: "8D", icon: "bi-people")
                .Section("d2", "D2 · Describe the problem (Is / Is not)", s =>
                {
                    s.TextArea("d2_what", "What is the problem? (and what is it not)", 6).VisibleWhen(EightD);
                    s.TextArea("d2_where", "Where was it seen? (and where not)", 6).VisibleWhen(EightD);
                    s.TextArea("d2_when", "When did it start? (and when not)", 6).VisibleWhen(EightD);
                    s.TextArea("d2_how_many", "How many / how big? (and how not)", 6).VisibleWhen(EightD);
                }, tab: "8D", icon: "bi-search")
                .Section("d3", "D3 · Interim containment", s =>
                {
                    s.TextArea("d3_actions", "Interim containment actions").VisibleWhen(EightD);
                    s.Date("d3_date", "Contained on").VisibleWhen(EightD);
                    s.YesNo("d3_verified", "Containment verified effective?").VisibleWhen(EightD);
                }, tab: "8D", icon: "bi-bandaid")
                .Section("d4", "D4 · Root cause (occurrence and escape)", s =>
                {
                    s.TextArea("d4_occurrence", "Why it occurred (occurrence root cause)", 6).VisibleWhen(EightD).RequiredIn("root_cause_plan");
                    s.TextArea("d4_escape", "Why it escaped detection (escape point)", 6).VisibleWhen(EightD).RequiredIn("root_cause_plan");
                    s.YesNo("d4_verified", "Root cause verified (turned on and off)?").VisibleWhen(EightD);
                }, tab: "8D", icon: "bi-diagram-3")
                .Section("d5", "D5 · Permanent corrective actions", s =>
                {
                    s.TextArea("d5_actions", "Chosen permanent corrective actions", 6).VisibleWhen(EightD).RequiredIn("root_cause_plan");
                    s.TextArea("d5_verification", "How they were verified before implementation", 6).VisibleWhen(EightD);
                }, tab: "8D", icon: "bi-wrench-adjustable")
                .Section("d6", "D6 · Implement and validate", s =>
                {
                    s.TextArea("d6_implementation", "Implementation and validation results", 8).VisibleWhen(EightD).RequiredIn("implementation");
                    s.Date("d6_date", "Implemented on").VisibleWhen(EightD);
                    s.YesNo("d6_containment_removed", "Interim containment removed?").VisibleWhen(EightD);
                }, tab: "8D", icon: "bi-check2-circle")
                .Section("d7", "D7 · Prevent recurrence", s =>
                {
                    s.TextArea("d7_systemic", "Systemic preventive actions (similar products and processes)", 6).VisibleWhen(EightD).RequiredIn("implementation");
                    s.Text("d7_documents", "Documents updated (PFMEA, control plan, work instructions)", 6).VisibleWhen(EightD);
                }, tab: "8D", icon: "bi-shield-plus")
                .Section("d8", "D8 · Recognize the team", s =>
                {
                    s.TextArea("d8_recognition", "Team recognition and lessons learned").VisibleWhen(EightD);
                    s.Date("d8_closed_on", "8D closed on").VisibleWhen(EightD);
                }, tab: "8D", icon: "bi-trophy");

            QualityCosts.Define(f);

            string[] eightDEarly = ["d0", "d1", "d2", "d3"];
            f.Workflow("capa", "CAPA", w =>
            {
                w.Start("initiate", "Initiate").Purpose("Describe the problem, its source and any containment. Rate the risk to set the priority.")
                    .DueDays(3).Edit(["problem", "risk_priority", "containment", .. eightDEarly])
                    .Hide("closure", "root_cause", "whys", "plan", "planned_actions", "effectiveness", "costs", "cost_summary");
                w.Phase("initial_review", "Initial Review").Purpose("Confirm the CAPA is valid, set priority and owner, or close it without action.")
                    .AssignField("owner").DueDays(5).Edit(["problem", "risk_priority", "containment", "closure", "costs", .. eightDEarly])
                    .ReadOnly("cost_summary").Hide("root_cause", "whys", "plan", "planned_actions", "effectiveness");
                w.Phase("root_cause_plan", "Root Cause & Plan").Purpose("Find the root cause and plan the corrective actions. For an 8D, complete D4 and D5.")
                    .AssignField("owner").DueDays(15)
                    .Edit("root_cause", "whys", "plan", "planned_actions", "effectiveness", "containment", "risk_priority", "d3", "d4", "d5", "costs")
                    .ReadOnly("problem", "d0", "d1", "d2", "cost_summary").Hide("closure");
                w.Phase("plan_approval", "Plan Approval").Purpose("Quality approves the root cause and action plan.")
                    .AssignGroup("qa-approvers").DueDays(5).ReadOnlyRest().Hide("closure");
                w.Phase("implementation", "Implementation").Purpose("Carry out the planned actions. Every action item must be closed before the effectiveness check.")
                    .AssignField("owner").DueDays(30).Edit("effectiveness", "d6", "d7", "costs").ReadOnlyRest().Hide("closure").MustComplete("action_item").Activities("action_item");
                w.Phase("effectiveness_check", "Effectiveness Check").Purpose("Verify the actions worked against the success criteria.")
                    .AssignField("effectiveness_verifier").DueFrom("effectiveness_due").DueDays(10).Edit("effectiveness", "d8", "costs").ReadOnlyRest().Hide("closure");
                w.Completed("closed", "Closed");
                w.Completed("closed_no_action", "Closed Without Action");
                w.Voided();

                w.Forward("initiate", "initial_review", label: "Submit for review");
                w.Void("initiate");
                w.Forward("initial_review", "root_cause_plan", label: "Accept: investigate");
                w.Forward("initial_review", "closed_no_action", label: "Close without action", sign: "Closed without action", comment: true);
                w.Back("initial_review", "initiate");
                w.Void("initial_review");
                w.Forward("root_cause_plan", "plan_approval", label: "Submit plan for approval");
                w.Back("root_cause_plan", "initial_review");
                w.Forward("plan_approval", "implementation", label: "Approve plan", sign: "Approved");
                w.Back("plan_approval", "root_cause_plan", label: "Reject: revise plan");
                w.Forward("implementation", "effectiveness_check", label: "Send to effectiveness check");
                w.Back("implementation", "root_cause_plan", label: "Back to planning");
                w.Forward("effectiveness_check", "closed", label: "Effective: close CAPA", sign: "Verified effective", when: "effectiveness_result=effective");
                w.Back("effectiveness_check", "root_cause_plan", label: "Not effective: reopen investigation");
                w.Back("effectiveness_check", "implementation");
            });
        });

        // CAPA-010: supplier corrective action request.
        app.Form("scar", "SCAR", f =>
        {
            f.Icon("bi-truck")
                .Description("Supplier corrective action request: issue the problem to a supplier, track their response and verify it.")
                .Numbering("SCAR-")
                .Title("{subject}")
                .Section("scar_problem", "Problem identification", s =>
                {
                    s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("For example: Resin lots shipped without certificates of analysis");
                    s.User("owner", "Supplier quality engineer").Required().Header().Default("$me");
                    s.Keyword("priority", "Priority", "priority").Required().Header().Default("medium");
                    s.Link("source_record", "Source record", "ncr", "capa", "complaint", "rma").Help("The NCR, complaint or CAPA that raised this SCAR.");
                    s.TextArea("description", "Problem description").Required().Help("What was found, how many parts, and the requirement that wasn't met.");
                    s.Text("requirement", "Requirement not met", 12).Placeholder("Drawing, specification or purchase-order clause");
                }, icon: "bi-search")
                .Section("scar_supplier", "Supplier and product", s =>
                {
                    s.Text("supplier_name", "Supplier", 6).Required().Header();
                    s.Text("supplier_code", "Supplier code", 3);
                    s.Text("supplier_contact", "Supplier contact", 3);
                    s.Text("supplier_email", "Contact email", 4).Placeholder("quality@supplier.example");
                    s.Text("part_number", "Part number", 4).Required();
                    s.Text("part_description", "Part description", 4);
                    s.Text("lot_number", "Lot / batch", 3);
                    s.Text("po_number", "Purchase order", 3);
                    s.Number("quantity_affected", "Quantity affected", 3).Min(0);
                }, icon: "bi-building")
                .Section("scar_containment", "Containment", s =>
                {
                    s.YesNo("containment_required", "Supplier containment required?").Default("yes");
                    s.Number("stock_quarantined", "Suspect stock quarantined (units)").VisibleWhen("containment_required=yes").Min(0);
                    s.TextArea("containment_actions", "Containment requested (at Acme, in transit and at the supplier)").VisibleWhen("containment_required=yes");
                }, icon: "bi-bandaid")
                .Section("scar_request", "Request to supplier", s =>
                {
                    s.Date("response_due", "Supplier response due").Help("Sets the due date while the SCAR is with the supplier.").RequiredIn("initial_approval");
                    s.Date("sent_on", "Sent to supplier on").ReadOnly().Help("Recorded by \"Send to supplier\".");
                    s.TextArea("request_message", "Message to the supplier");
                }, icon: "bi-envelope-paper")
                .Section("supplier_response", "Supplier response", s =>
                {
                    s.Prompt("sr_prompt", "Enter the supplier's response from their 8D or SCAR response form, and attach the returned file.");
                    s.TextArea("sr_containment", "Supplier containment actions");
                    s.TextArea("sr_root_cause", "Supplier root cause").RequiredIn("assign_to_supplier");
                    s.TextArea("sr_corrective_action", "Corrective action").RequiredIn("assign_to_supplier");
                    s.TextArea("sr_preventive_action", "Preventive action (systemic)");
                    s.Date("sr_implementation_date", "Implemented on").RequiredIn("assign_to_supplier");
                    s.Text("sr_responder", "Responded by (name, title)", 6);
                    s.Date("sr_received_on", "Response received on").RequiredIn("assign_to_supplier");
                    s.Attachment("sr_file", "Supplier response file");
                }, tab: "Supplier response", icon: "bi-reply")
                .Section("scar_verification", "Verification and effectiveness", s =>
                {
                    s.User("scar_verifier", "Verified by").RequiredIn("assign_to_supplier").Help("Someone other than the SCAR owner (SAF-044).");
                    s.TextArea("verification_method", "Verification performed (receiving inspection, supplier audit, data review)").RequiredIn("verification");
                    s.Keyword("scar_result", "Result", "effectiveness_result").RequiredIn("verification");
                    s.TextArea("verification_evidence", "Evidence and conclusion").RequiredIn("verification");
                }, tab: "Supplier response", icon: "bi-patch-check");
            f.Workflow("scar", "SCAR", w =>
            {
                w.Start("draft", "Draft").Purpose("Describe the problem, the supplier and the part, and what you need the supplier to do.")
                    .DueDays(3).Edit("scar_problem", "scar_supplier", "scar_containment", "scar_request").Hide("supplier_response", "scar_verification");
                w.Phase("initial_approval", "Initial Approval").Purpose("Supplier quality management approves issuing the SCAR to the supplier.")
                    .AssignGroup("qa-approvers").DueDays(3).Edit("scar_request").ReadOnlyRest().Hide("supplier_response", "scar_verification");
                w.Phase("assign_to_supplier", "Assign to Supplier").Purpose("The supplier investigates and responds. Send the request, then record their response and choose the verifier.")
                    .AssignField("owner").DueFrom("response_due").DueDays(20).Edit("supplier_response", "scar_verification", "scar_containment").ReadOnlyRest();
                w.Phase("verification", "Verification & Effectiveness").Purpose("Verify the supplier's actions were implemented and are effective.")
                    .AssignField("scar_verifier").DueDays(10).Edit("scar_verification").ReadOnlyRest();
                w.Completed("completed", "Completed");
                w.Voided();

                w.Forward("draft", "initial_approval", label: "Submit for approval");
                w.Void("draft");
                w.Forward("initial_approval", "assign_to_supplier", label: "Approve and issue to supplier", sign: "Approved for issue");
                w.Back("initial_approval", "draft");
                w.Void("initial_approval");
                w.Forward("assign_to_supplier", "verification", label: "Supplier responded: verify");
                w.Back("assign_to_supplier", "initial_approval");
                w.Forward("verification", "completed", label: "Verified effective: complete", sign: "Verified effective", when: "scar_result=effective");
                w.Back("verification", "assign_to_supplier", label: "Not effective: back to supplier");
            });
        });

        app.View("mine", "My CAPAs", v => v.Category("1. My work").Forms("capa").Mine()
            .Columns("number", "title", "phase", "due", "priority", "capa_type").Sort("due", false).Default());
        app.View("mine_scar", "My SCARs", v => v.Category("1. My work").Forms("scar").Mine()
            .Columns("number", "title", "phase", "due", "supplier_name", "priority").Sort("due", false));
        app.View("open", "Open CAPAs", v => v.Category("2. CAPA").Forms("capa").Open()
            .Columns("number", "title", "phase", "due", "priority", "source", "owner"));
        app.View("by_phase", "Open by phase", v => v.Category("2. CAPA").Forms("capa").Open()
            .Columns("number", "phase", "title", "due", "owner").Sort("phase", false));
        app.View("by_owner", "Open by owner", v => v.Category("2. CAPA").Forms("capa").Open()
            .Columns("number", "owner", "title", "phase", "due", "priority").Sort("owner", false));
        app.View("by_source", "Open by source", v => v.Category("2. CAPA").Forms("capa").Open()
            .Columns("number", "source", "title", "phase", "due", "owner").Sort("source", false));
        app.View("overdue", "Overdue CAPAs", v => v.Category("2. CAPA").Forms("capa").Overdue()
            .Columns("number", "title", "phase", "due", "priority", "owner").Sort("due", false));
        app.View("critical", "High and critical", v => v.Category("2. CAPA").Forms("capa").Open().Filter("priority", "ne", "low").Filter("priority", "ne", "medium")
            .Columns("number", "title", "phase", "due", "priority", "owner"));
        app.View("eight_d", "8D investigations", v => v.Category("2. CAPA").Forms("capa").Open().Filter("capa_type", "eq", "eight_d")
            .Columns("number", "title", "phase", "due", "d1_leader", "owner"));
        app.View("effectiveness_due", "Effectiveness checks due", v => v.Category("2. CAPA").Forms("capa").Open().Phases("effectiveness_check")
            .Columns("number", "title", "effectiveness_due", "effectiveness_verifier", "due"));
        app.View("scar_open", "Open SCARs", v => v.Category("3. SCAR").Forms("scar").Open()
            .Columns("number", "title", "supplier_name", "phase", "due", "response_due", "owner").Sort("due", false));
        app.View("scar_waiting", "Awaiting supplier response", v => v.Category("3. SCAR").Forms("scar").Open().Phases("assign_to_supplier")
            .Columns("number", "title", "supplier_name", "sent_on", "response_due", "owner").Sort("response_due", false));
        app.View("scar_closed", "Closed SCARs", v => v.Category("3. SCAR").Forms("scar").Closed()
            .Columns("number", "title", "supplier_name", "phase", "scar_result", "modified").Sort("modified"));
        app.View("closed", "Closed CAPAs", v => v.Category("4. Closed").Forms("capa").Completed().Filter("effectiveness_result", "set")
            .Columns("number", "title", "phase", "priority", "owner", "modified").Sort("modified"));
        app.View("closed_no_action", "Closed without action", v => v.Category("4. Closed").Forms("capa").Phases("closed_no_action")
            .Columns("number", "title", "source", "owner", "modified").Sort("modified"));
        app.View("voided", "Voided CAPAs", v => v.Category("4. Closed").Forms("capa").Voided().Columns("number", "title", "owner", "modified"));
        app.View("actions", "CAPA action items", v => v.Category("5. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee").Sort("assignee", false));
        app.View("actions_closed", "Closed action items", v => v.Category("5. Action items").Forms("action_item").Closed()
            .Columns("number", "title", "assignee", "completed_on", "modified").Sort("completed_on"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        var records = d.Services.GetRequiredService<RecordService>();

        // 1. Closed CAPA with signatures and completed action items.
        var c1 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Torque drift on final assembly station 3",
            ["capa_type"] = "corrective", ["source"] = "internal_audit", ["priority"] = "high", ["owner"] = d.User("jlee"),
            ["risk_severity"] = "serious", ["risk_occurrence"] = "occasional",
            ["product"] = "VX-200 infusion pump housing",
            ["description"] = "Audit IA-2026-04 found 6 of 40 sampled housings torqued below the 2.4 N·m minimum. The station's torque driver had drifted.",
            ["effectiveness_criteria"] = "Zero under-torque findings across 3 consecutive weekly audits of 40 units.",
            ["effectiveness_verifier"] = d.User("tbrooks"),
        }, "tbrooks", daysAgo: 64, rows: new()
        {
            ["planned_actions"] =
            [
                new() { ["action"] = "Recalibrate torque driver and add to 30-day calibration interval", ["assignee"] = d.User("ebaker"), ["days"] = 5m, ["verify"] = false },
                new() { ["action"] = "Add torque verification step to WI-ASM-014", ["assignee"] = d.User("akim"), ["days"] = 10m, ["verify"] = true },
            ],
            ["costs"] =
            [
                new() { ["cost_category"] = "labor", ["cost_date"] = DemoContext.Days(-50), ["cost_amount"] = 1840m, ["cost_note"] = "Re-inspection of 1,150 housings (2 inspectors × 23 h)" },
                new() { ["cost_category"] = "rework", ["cost_date"] = DemoContext.Days(-48), ["cost_amount"] = 960m, ["cost_note"] = "Re-torque of 64 housings" },
                new() { ["cost_category"] = "equipment", ["cost_date"] = DemoContext.Days(-45), ["cost_amount"] = 420m, ["cost_note"] = "Torque driver calibration (external lab)" },
            ],
        });
        await d.RouteAsync(c1.Id, "initial_review");
        await d.RouteAsync(c1.Id, "root_cause_plan");
        await d.UpdateAsync(c1.Id, new()
        {
            ["root_cause_category"] = "machine",
            ["root_cause"] = "The torque driver had no defined calibration interval, so drift went undetected for 4 months.",
            ["solution"] = "Put the driver on a 30-day calibration cycle and add a daily torque check to the work instruction.",
        }, "jlee");
        await d.RouteAsync(c1.Id, "plan_approval");
        await d.RouteAsync(c1.Id, "implementation", asUser: "sortiz", comment: "Plan approved. Good root cause work.");
        foreach (var ai in await records.ChildrenAsync(c1.Id, "action_item"))
            await CompleteActionItemAsync(d, records, ai.Id, "Done as planned; records attached to the calibration log.");
        await d.RouteAsync(c1.Id, "effectiveness_check");
        await d.UpdateAsync(c1.Id, new() { ["effectiveness_result"] = "effective", ["effectiveness_evidence"] = "Three weekly audits (120 units) found no under-torque housings." }, "tbrooks");
        await d.RouteAsync(c1.Id, "closed");

        // 2. In implementation with open action items (one overdue).
        var c2 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Mislabeled lot shipped to distributor (wrong expiry date)",
            ["capa_type"] = "corrective", ["source"] = "customer_complaint", ["priority"] = "critical", ["owner"] = d.User("jlee"),
            ["risk_severity"] = "critical", ["risk_occurrence"] = "probable",
            ["product"] = "Sterile tubing set ST-40",
            ["description"] = "Distributor reported lot 24-118 labeled with a 2029 expiry instead of 2027. Label template variable was not updated after the shelf-life change.",
            ["containment_required"] = true, ["containment_owner"] = d.User("lgarcia"), ["containment_date"] = DemoContext.Days(-30),
            ["containment_actions"] = "Quarantined 1,240 units at the Kalamazoo warehouse; distributor placed 300 units on hold.",
            ["effectiveness_criteria"] = "No label/expiry mismatches in 100% label verification for 60 days.",
            ["effectiveness_verifier"] = d.User("sortiz"),
        }, "jlee", daysAgo: 34, rows: new()
        {
            ["planned_actions"] =
            [
                new() { ["action"] = "Lock expiry field in label template to the shelf-life master data", ["assignee"] = d.User("akim"), ["days"] = 7m, ["verify"] = true },
                new() { ["action"] = "Add second-person label verification to packaging SOP", ["assignee"] = d.User("pshah"), ["days"] = 10m, ["verify"] = false },
                new() { ["action"] = "Notify affected customers and reconcile returned stock", ["assignee"] = d.User("rnovak"), ["days"] = 5m, ["verify"] = false },
            ],
            ["costs"] =
            [
                new() { ["cost_category"] = "transportation", ["cost_date"] = DemoContext.Days(-25), ["cost_amount"] = 2350m, ["cost_note"] = "Return freight from 4 distributors" },
                new() { ["cost_category"] = "scrap", ["cost_date"] = DemoContext.Days(-20), ["cost_amount"] = 11160m, ["cost_note"] = "Scrapped 1,240 units (relabeling not validated)" },
            ],
        });
        await d.RouteAsync(c2.Id, "initial_review");
        await d.RouteAsync(c2.Id, "root_cause_plan");
        await d.UpdateAsync(c2.Id, new()
        {
            ["root_cause_category"] = "method",
            ["root_cause"] = "The shelf-life change was made in the product master but the label template used a hard-coded expiry offset.",
            ["solution"] = "Drive the expiry from master data, add a second-person check and notify customers.",
        }, "jlee");
        await d.RouteAsync(c2.Id, "plan_approval");
        await d.RouteAsync(c2.Id, "implementation", asUser: "dokafor");
        var items = await records.ChildrenAsync(c2.Id, "action_item");
        if (items.Count > 0) await d.SetDueAsync(items[0].Id, DemoContext.Days(-3));
        if (items.Count > 2)
        {
            await d.UpdateAsync(items[2].Id, new() { ["action_taken"] = "All 4 distributors notified; 300 units returned and scrapped.", ["completed_on"] = DemoContext.Days(-2) }, "rnovak");
            await d.RouteAsync(items[2].Id, "completed");
        }

        // 3. Awaiting plan approval (assigned to the approvers).
        var c3 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Particulate contamination in cleanroom B fill line",
            ["capa_type"] = "corrective", ["source"] = "quality_event", ["priority"] = "high", ["owner"] = d.User("akim"),
            ["risk_severity"] = "critical", ["risk_occurrence"] = "unlikely",
            ["description"] = "Environmental monitoring showed ISO 7 excursions on 3 consecutive days at the fill line.",
            ["effectiveness_criteria"] = "Particle counts within ISO 7 limits for 30 consecutive days.",
        }, "akim", daysAgo: 12, rows: new()
        {
            ["planned_actions"] = [new() { ["action"] = "Replace HEPA filter bank FB-2", ["assignee"] = d.User("ebaker"), ["days"] = 3m, ["verify"] = true }],
            ["whys"] =
            [
                new() { ["why"] = "Why were particle counts high?", ["because"] = "Airflow at the fill line dropped 22%." },
                new() { ["why"] = "Why did airflow drop?", ["because"] = "HEPA filter bank FB-2 was loaded beyond its rating." },
                new() { ["why"] = "Why wasn't it replaced?", ["because"] = "The PM task used pressure differential limits from the old filter model." },
            ],
        });
        await d.RouteAsync(c3.Id, "initial_review");
        await d.RouteAsync(c3.Id, "root_cause_plan");
        await d.UpdateAsync(c3.Id, new()
        {
            ["root_cause_category"] = "machine", ["root_cause"] = "Preventive maintenance limits weren't updated for the new filter model.",
            ["solution"] = "Replace the filter bank and correct the PM differential-pressure limit.",
        }, "akim");
        await d.RouteAsync(c3.Id, "plan_approval", assignees: ["sortiz", "dokafor"]);

        // 4. New CAPA in initial review, overdue.
        var c4 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Supplier certificate of analysis missing for resin lot",
            ["capa_type"] = "scar", ["source"] = "supplier", ["priority"] = "medium", ["owner"] = d.User("cwu"),
            ["description"] = "Three resin deliveries from Polymera arrived without CoAs. Receiving released them under deviation.",
        }, "cwu", daysAgo: 9);
        await d.RouteAsync(c4.Id, "initial_review");
        await d.SetDueAsync(c4.Id, DemoContext.Days(-2));

        // 5. Draft preventive CAPA.
        await d.CreateAsync("capa", new()
        {
            ["subject"] = "Preventive: extend operator training on new labeling system",
            ["capa_type"] = "preventive", ["source"] = "management_review", ["priority"] = "low", ["owner"] = d.User("kpatel"),
            ["description"] = "Management review identified the labeling system rollout as a training risk for second-shift operators.",
        }, "kpatel", daysAgo: 2);

        // 6. 8D in Root Cause & Plan (customer escalation), with D0–D3 filled in.
        var c6 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "8D: Occlusion alarm failures on VX-200 pumps at St. Mary's Hospital",
            ["capa_type"] = "eight_d", ["source"] = "customer_complaint", ["priority"] = "critical", ["owner"] = d.User("jlee"),
            ["risk_severity"] = "critical", ["risk_occurrence"] = "occasional",
            ["priority_justification"] = "Alarm function protects patients from under-infusion; treated as critical regardless of the occurrence rating.",
            ["product"] = "VX-200 infusion pump, firmware 4.2",
            ["description"] = "Customer reported 3 pumps that did not alarm on downstream occlusion during bench checks. Customer requested a formal 8D.",
            ["d0_symptoms"] = "Downstream occlusion alarm did not trigger within 60 s at 15 psi on 3 of 22 pumps.",
            ["d0_era"] = "Field safety notice drafted; affected serial range quarantined at the Kalamazoo DC; customer supplied 6 loaner pumps.",
            ["d1_champion"] = d.User("dokafor"), ["d1_leader"] = d.User("jlee"),
            ["d1_team"] = new List<int> { d.User("akim"), d.User("hmoreau"), d.User("cwu"), d.User("rnovak") },
            ["d2_what"] = "Occlusion alarm latency > 60 s. Is not: upstream occlusion alarm (works).",
            ["d2_where"] = "Pumps built on line 2 in August. Is not: line 1 builds.",
            ["d2_when"] = "Serials VX2-24080 to VX2-24311 (built 05–29 Aug). Is not: later builds.",
            ["d2_how_many"] = "3 confirmed of 22 tested; up to 231 units at risk.",
            ["d3_actions"] = "100% bench test of quarantined stock; customer units swapped; line 2 force-sensor lot held.",
            ["d3_date"] = DemoContext.Days(-6), ["d3_verified"] = true,
            ["effectiveness_criteria"] = "No occlusion alarm latency failures in 100% end-of-line testing for 3 months; zero field reports.",
        }, "jlee", daysAgo: 8, rows: new()
        {
            ["planned_actions"] =
            [
                new() { ["action"] = "Add occlusion alarm latency test to end-of-line tester", ["assignee"] = d.User("akim"), ["days"] = 10m, ["verify"] = true },
                new() { ["action"] = "Tighten force-sensor incoming inspection (offset ≤ 0.2 N)", ["assignee"] = d.User("cwu"), ["days"] = 7m, ["verify"] = true },
            ],
        });
        await d.RouteAsync(c6.Id, "initial_review");
        await d.RouteAsync(c6.Id, "root_cause_plan");
        await d.UpdateAsync(c6.Id, new()
        {
            ["d4_occurrence"] = "Force-sensor lot FS-0824 had a +0.6 N zero offset, shifting the alarm threshold.",
            ["d4_escape"] = "End-of-line test checks upstream occlusion only; downstream latency was never tested.",
        }, "jlee");

        // SCAR 1: with the supplier, response overdue.
        var s1 = await d.CreateAsync("scar", new()
        {
            ["subject"] = "Resin lots shipped without certificates of analysis",
            ["owner"] = d.User("cwu"), ["priority"] = "medium",
            ["description"] = "Three consecutive deliveries of PC-2405 polycarbonate resin (lots R-5521, R-5530, R-5544) arrived without CoAs, contrary to the quality agreement.",
            ["requirement"] = "Quality agreement QA-POL-02 §4.3: CoA with every lot",
            ["supplier_name"] = "Polymera Resins GmbH", ["supplier_code"] = "SUP-0142", ["supplier_contact"] = "Katrin Vogel", ["supplier_email"] = "k.vogel@polymera.example",
            ["part_number"] = "RM-PC-2405", ["part_description"] = "Medical-grade polycarbonate resin", ["lot_number"] = "R-5521, R-5530, R-5544",
            ["po_number"] = "PO-778120", ["quantity_affected"] = 3000m,
            ["containment_required"] = true, ["stock_quarantined"] = 1200m, ["containment_actions"] = "Remaining 1,200 kg held in receiving until CoAs are provided.",
            ["response_due"] = DemoContext.Days(10),
        }, "cwu", daysAgo: 24);
        await d.RouteAsync(s1.Id, "initial_approval", assignees: ["sortiz"]);
        await d.RouteAsync(s1.Id, "assign_to_supplier");
        await d.UpdateAsync(s1.Id, new() { ["sent_on"] = DemoContext.Days(-20) }, "cwu");
        await d.SetDueAsync(s1.Id, DemoContext.Days(-4));

        // SCAR 2: completed with signatures.
        var s2 = await d.CreateAsync("scar", new()
        {
            ["subject"] = "Burrs on machined manifold ports",
            ["owner"] = d.User("cwu"), ["priority"] = "high",
            ["description"] = "Incoming inspection found burrs in the luer ports of 46 of 500 manifolds (lot M-2207), a particulate risk.",
            ["requirement"] = "Drawing 40-1180 rev D note 7: ports free of burrs at 10× magnification",
            ["supplier_name"] = "Precision Micro Machining LLC", ["supplier_code"] = "SUP-0088", ["supplier_contact"] = "Dan Reyes", ["supplier_email"] = "dreyes@pmm.example",
            ["part_number"] = "40-1180", ["part_description"] = "3-port manifold, PC", ["lot_number"] = "M-2207", ["quantity_affected"] = 500m,
            ["containment_required"] = true, ["stock_quarantined"] = 500m, ["containment_actions"] = "Lot returned; supplier sorting finished goods at their site.",
            ["response_due"] = DemoContext.Days(5),
        }, "cwu", daysAgo: 70);
        await d.RouteAsync(s2.Id, "initial_approval", assignees: ["sortiz"]);
        await d.RouteAsync(s2.Id, "assign_to_supplier");
        await d.UpdateAsync(s2.Id, new()
        {
            ["sent_on"] = DemoContext.Days(-66), ["response_due"] = DemoContext.Days(-38),
            ["sr_containment"] = "Sorted 2,100 manifolds at PMM; 112 rejected.",
            ["sr_root_cause"] = "Worn deburring tool; tool life was not tracked on the new 5-axis cell.",
            ["sr_corrective_action"] = "Tool-life counter set at 800 parts; deburr verification at 10× added to the in-process check.",
            ["sr_preventive_action"] = "Tool-life tracking rolled out to all CNC cells.",
            ["sr_implementation_date"] = DemoContext.Days(-45), ["sr_responder"] = "Dan Reyes, Quality Manager", ["sr_received_on"] = DemoContext.Days(-44),
            ["scar_verifier"] = d.User("jlee"),
        }, "cwu");
        await d.RouteAsync(s2.Id, "verification");
        await d.UpdateAsync(s2.Id, new()
        {
            ["verification_method"] = "Tightened inspection of the next 5 lots (2,500 parts) at 10×.",
            ["scar_result"] = "effective", ["verification_evidence"] = "Zero burrs found in 5 consecutive lots.",
        }, "jlee");
        await d.RouteAsync(s2.Id, "completed");
    }

    internal static async Task CompleteActionItemAsync(DemoContext d, RecordService records, Guid id, string actionTaken)
    {
        var snap = await records.LoadRequiredAsync(id);
        var doer = d.Directory.Get(snap.UserId("assignee"))!.Username;
        await d.UpdateAsync(id, new() { ["action_taken"] = actionTaken, ["completed_on"] = DemoContext.Days(-3) }, doer);
        if (snap.Bool("verification_required") == true)
        {
            await d.RouteAsync(id, "verification");
            var verifier = d.Directory.Get(snap.UserId("verifier"))!.Username;
            await d.UpdateAsync(id, new() { ["verification_result"] = "verified", ["verification_comments"] = "Checked objective evidence; action is effective." }, verifier);
        }
        await d.RouteAsync(id, "completed");
    }
}

public sealed class CapaBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["capa"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        // CAPA-007: suggested priority from the risk rating; a different priority needs a justification.
        var suggested = RiskPriority.Priority(ctx.KeywordKey("risk_severity"), ctx.KeywordKey("risk_occurrence"));
        ctx.Set("suggested_priority", suggested);
        var chosen = ctx.KeywordKey("priority");
        if (suggested != null && chosen != null && chosen != suggested && string.IsNullOrWhiteSpace(ctx.Proposed.Main.Text("priority_justification")))
            ctx.Error("priority_justification",
                $"The risk rating suggests {ctx.Config.Keyword("priority", suggested)?.Label} priority. Explain why this CAPA is {ctx.Config.Keyword("priority", chosen)?.Label} (CAPA-007).");
        QualityCosts.Compute(ctx);
        return Task.CompletedTask;
    }

    public override Task OnCreatedAsync(RecordContext ctx) => PriorityDue.ApplyAsync(ctx, ctx.Snapshot.Phase, "priority");

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        // CAPA-002: closing without action needs a justification (kept in the audit trail).
        if (ctx.To.Key == "closed_no_action" && string.IsNullOrWhiteSpace(ctx.Snapshot.Text("closure_justification")))
            ctx.Error("Add a justification in \"Close without action\" before closing this CAPA without action.");
        // INV-042: a CAPA that isn't effective never closes.
        if (ctx.To.Key == "closed" && ctx.Snapshot.KeywordKey("effectiveness_result") != "effective")
            ctx.Error("A CAPA can only close when the effectiveness check result is Effective.");
        // SAF-044: implementer and effectiveness verifier must differ.
        if (ctx.To.Key == "effectiveness_check" && ctx.Snapshot.UserId("effectiveness_verifier") is { } v && v == ctx.Snapshot.UserId("owner"))
            ctx.Error("The effectiveness verifier must be someone other than the CAPA owner (segregation of duties).");
        return Task.CompletedTask;
    }

    /// <summary>CAPA-003 / QE-002: planned actions become Action Items (in Open) when the CAPA enters Implementation.</summary>
    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key == "implementation")
            await PlannedActions.CreateAsync(ctx, "planned_actions", ctx.Snapshot.UserId("owner"));
        await PriorityDue.ApplyAsync(ctx, ctx.To, "priority");
    }

    /// <summary>CAPA-005: linked CAPAs, deviations and NCRs can be created from Initiate through Effectiveness Check.</summary>
    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (!ctx.IsOpen() || !await ctx.CanActAsync()) return [];
        return
        [
            new RecordAction { Key = "capa-new-ncr", Label = "Create linked NCR", Icon = "bi-exclamation-octagon", Group = "Create",
                Description = "Raise a nonconformance for affected product, linked to this CAPA." },
            new RecordAction { Key = "capa-new-deviation", Label = "Create planned deviation", Icon = "bi-sign-turn-right", Group = "Create",
                Description = "Request a temporary, controlled departure from a requirement." },
            new RecordAction { Key = "capa-new-capa", Label = "Create linked CAPA", Icon = "bi-shield-plus", Group = "Create",
                Description = "Raise a related corrective or preventive action." },
        ];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (!ctx.IsOpen() || !await ctx.CanActAsync()) return ActionOutcome.Fail("Only the current assignee or an application manager can do this.");
        var s = ctx.Snapshot;
        var me = ctx.Me.Id;
        var (rec, err) = actionKey switch
        {
            "capa-new-ncr" => await QualityLinks.CreateFromAsync(ctx, "ncr", new()
            {
                ["subject"] = s.Text("subject"), ["owner"] = me, ["description"] = $"Raised from {s.Record.Number}: {s.Text("description")}",
                ["source_record"] = s.Record.Id,
            }),
            "capa-new-deviation" => await QualityLinks.CreateFromAsync(ctx, "deviation", new()
            {
                ["subject"] = $"Deviation for {s.Record.Number}: {s.Text("subject")}", ["owner"] = me, ["source_record"] = s.Record.Id,
            }),
            "capa-new-capa" => await QualityLinks.CreateFromAsync(ctx, "capa", new()
            {
                ["subject"] = s.Text("subject"), ["capa_type"] = "preventive", ["source"] = "capa", ["priority"] = s.KeywordKey("priority") ?? "medium",
                ["owner"] = me, ["source_record"] = s.Record.Id, ["description"] = $"Related to {s.Record.Number}: {s.Text("description")}",
            }),
            _ => ((Record?)null, (string?)"This action isn't available."),
        };
        return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"{rec.Number} created and linked to {s.Record.Number}.", rec.Id);
    }

    public override Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx) =>
        Task.FromResult<IEnumerable<RecordPanel>>(QualityCosts.Panel(ctx) is { } p ? [p] : []);
}

public sealed class ScarBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["scar"];

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key == "verification" && ctx.Snapshot.UserId("scar_verifier") is { } v && v == ctx.Snapshot.UserId("owner"))
            ctx.Error("The SCAR verifier must be someone other than the SCAR owner (segregation of duties, SAF-044).");
        if (ctx.To.Key == "completed" && ctx.Snapshot.KeywordKey("scar_result") != "effective")
            ctx.Error("A SCAR can only be completed when the supplier's actions are verified effective.");
        return Task.CompletedTask;
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.PhaseKey != "assign_to_supplier" || !await ctx.CanActAsync()) return [];
        var s = ctx.Snapshot;
        return
        [
            new RecordAction
            {
                Key = "scar-send", Label = s.Date("sent_on") == null ? "Send to supplier" : "Resend to supplier", Icon = "bi-envelope-arrow-up",
                Description = "Email the SCAR and the response template to the supplier contact. The message is kept in this record's email log (CAPA-011).",
                Inputs =
                [
                    new ActionField("email", "Supplier contact email", Required: true, Default: s.Text("supplier_email")),
                    new ActionField("due", "Response due", "date", true, Default: (s.Date("response_due") ?? BusinessCalendar.Today.AddDays(20)).ToString("yyyy-MM-dd")),
                    new ActionField("message", "Message", "textarea", Default: s.Text("request_message") ??
                        $"Please investigate {s.Record.Number} ({s.Text("subject")}) and return the completed SCAR response form with your containment, root cause and corrective actions."),
                ],
            },
        ];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "scar-send" || ctx.Snapshot.PhaseKey != "assign_to_supplier" || !await ctx.CanActAsync())
            return ActionOutcome.Fail("This action isn't available.");
        var email = input.Get("email")?.Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@')) return ActionOutcome.Fail("Enter the supplier contact's email address.");
        if (!DateOnly.TryParse(input.Get("due"), out var due) || due < BusinessCalendar.Today) return ActionOutcome.Fail("Choose a response due date that isn't in the past.");
        var s = ctx.Snapshot;
        var subject = $"Supplier corrective action request {s.Record.Number}: {s.Text("subject")}";
        var body = (input.Get("message") ?? "") + $"\n\nPart {s.Text("part_number")} · lot {s.Text("lot_number")} · response due {due:dd MMM yyyy}.\nAttached: SCAR response template (8D format).";
        return await Tx.RunAsync(ctx.Db, async () =>
        {
            ctx.Db.EmailLog.Add(new EmailLogEntry { RecordId = s.Record.Id, To = email, Subject = subject, Body = body, At = DateTime.UtcNow, Status = "Queued" });
            ctx.Db.Outbox.Add(new OutboxMessage
            {
                Type = "email", RecordId = s.Record.Id, CreatedAt = DateTime.UtcNow,
                Payload = JsonSerializer.Serialize(new { to = email, subject, body, url = (string?)null }),
            });
            ctx.Get<AuditService>().Record(s.Record.Id, "Sent to supplier", $"SCAR sent to {email}, response due {due:dd MMM yyyy}", phaseName: s.Phase?.Name);
            var res = await ctx.Records.UpdateAsync(s.Record.Id, new() { ["sent_on"] = BusinessCalendar.Today, ["response_due"] = due, ["supplier_email"] = email });
            if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            await ctx.Db.SaveChangesAsync();
            return ActionOutcome.Ok($"SCAR sent to {email}.");
        }, o => o.Success);
    }
}

/// <summary>CAPA-007: a record's priority scales the default due date of the phase it enters.</summary>
public static class PriorityDue
{
    public static async Task ApplyAsync(RecordContext ctx, PhaseDefinition? phase, string priorityField)
    {
        if (phase is not { Type: PhaseType.Normal, DueDays: { } days } || ctx.Record.DueDate is not { } current) return;
        if (phase.DueDateField != null && ctx.Snapshot.Date(phase.DueDateField) != null) return;
        var priority = ctx.Snapshot.KeywordKey(priorityField);
        var factor = RiskPriority.Factor(priority);
        if (factor == 1m) return;
        var cal = ctx.Get<BusinessCalendar>();
        var standard = await cal.AddAsync(BusinessCalendar.Today, days, phase.BusinessDays);
        if (current != standard) return; // the router chose a date: keep it
        var scaledDays = Math.Max(1, (int)Math.Ceiling(days * factor));
        var scaled = await cal.AddAsync(BusinessCalendar.Today, scaledDays, phase.BusinessDays);
        if (scaled == current) return;
        ctx.Record.DueDate = scaled;
        var entry = await ctx.Db.PhaseEntries.Where(e => e.RecordId == ctx.Record.Id && e.ExitedAt == null).OrderByDescending(e => e.Id).FirstOrDefaultAsync();
        if (entry != null) entry.DueDate = scaled;
        var label = ctx.Config.Keyword("priority", priority!)?.Label ?? priority;
        ctx.Get<AuditService>().Record(ctx.Record.Id, "Due date set by priority",
            $"{phase.Name}: {label} priority allows {scaledDays} instead of {days} {(phase.BusinessDays ? "business " : "")}days (CAPA-007)",
            oldValue: standard.ToString("dd MMM yyyy"), newValue: scaled.ToString("dd MMM yyyy"), phaseName: phase.Name);
        await ctx.Db.SaveChangesAsync();
    }
}
