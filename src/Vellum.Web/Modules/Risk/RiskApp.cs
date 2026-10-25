using Vellum.Domain;
using Vellum.Packaging;

namespace Vellum.Modules.Risk;

/// <summary>
/// Risk Register (§16.1): risk assessments on a 5 × 5 matrix with initial and residual risk (RSK-009), non-overlapping
/// bands (INV-175), mitigations that become action items, and completion of unacceptable risks only with a linked
/// mitigation or an e-signed acceptance by Quality (RSK-014, INV-176, SAF-045).
/// Workflow: Draft → Assessment → Mitigation → Review → Completed.
/// </summary>
public sealed class RiskApp : IPackagedApp
{
    public string Key => "risk";
    public int Order => 75;

    public void Define(AppBuilder app)
    {
        var issues = RiskMatrix.Validate(RiskMatrix.Bands);
        if (issues.Count > 0) throw new InvalidOperationException("The risk matrix bands are invalid (INV-175): " + string.Join(" ", issues));

        app.Name("Risk Register")
            .Description("Assess product, process and business risks on a 5 × 5 matrix, mitigate them and record e-signed risk acceptance.")
            .Icon("bi-grid-3x3-gap").Color("#b45309").Suite("Risk").Order(10).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@engineering", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        app.Keywords("risk_category", "Risk category", k => k
            .Add("product_safety", "Product safety (patient / user harm)").Add("process", "Manufacturing process").Add("regulatory", "Regulatory compliance")
            .Add("supply_chain", "Supply chain").Add("data_integrity", "Information security & data integrity").Add("ehs", "Workplace safety & environment")
            .Add("business", "Business continuity"));
        app.Keywords("risk_source", "Risk source", k => k
            .Add("design_review", "Design review").Add("process_change", "Process change").Add("complaint_trend", "Complaint trend")
            .Add("audit_finding", "Audit finding").Add("supplier_issue", "Supplier issue").Add("nonconformance", "Nonconformance")
            .Add("field_data", "Post-market surveillance").Add("management_review", "Management review"));
        app.Keywords("risk_severity", "Severity", k =>
        {
            for (var i = 0; i < RiskMatrix.Size; i++) k.Add($"s{i + 1}", $"{i + 1} · {RiskMatrix.Severity[i]}");
        });
        app.Keywords("risk_likelihood", "Likelihood", k =>
        {
            for (var i = 0; i < RiskMatrix.Size; i++) k.Add($"l{i + 1}", $"{i + 1} · {RiskMatrix.Likelihood[i]}");
        });
        app.Keywords("risk_level", "Risk level", k =>
        {
            foreach (var b in RiskMatrix.Bands)
                k.Add(b.Key, b.Label, b.Key switch { "low" => "success", "medium" => "warning", "high" => "danger", _ => "dark" });
        });
        app.Keywords("risk_control", "Risk control type", k => k
            .Add("design", "Inherent safety by design").Add("protective", "Protective measure / alarm")
            .Add("information", "Information for safety (labeling, IFU, training)").Add("procedural", "Procedural control")
            .Add("detection", "Detection / inspection").Add("supplier", "Supplier control"));
        app.Keywords("risk_acceptance", "Risk acceptance status", k => k
            .Add("not_required", "Not required", "secondary").Add("required", "Mitigation or acceptance required", "danger")
            .Add("mitigated", "Mitigated", "success").Add("accepted", "Accepted (e-signed)", "info")
            .Add("mitigation_linked", "Linked mitigation in progress", "warning"));
        app.Keywords("risk_review", "Review status", k => k
            .Add("current", "Current", "success").Add("due_soon", "Review due within 30 days", "warning").Add("overdue", "Review overdue", "danger"));

        app.Form("risk_assessment", "Risk Assessment", f => f
            .Icon("bi-grid-3x3-gap")
            .Description("A risk scored on the 5 × 5 matrix before and after mitigation, with acceptance and periodic review.")
            .Numbering("RA-")
            .Title("{subject}")
            .Section("identification", "Risk identification", s =>
            {
                s.Text("subject", "Risk title", 12).Required().MaxLength(200).Placeholder("The hazard and its consequence, in a few words");
                s.Keyword("risk_category", "Category", "risk_category").Required().Header();
                s.Keyword("risk_source", "Source", "risk_source");
                s.User("owner", "Risk owner").Required().Default("$me").Header().Help("Assesses the risk and carries out the mitigations.");
                s.Text("product", "Product / process", 6);
                s.Link("source_record", "Source record").Help("The NCR, complaint, audit finding, change or supplier the risk came from (RSK-012).");
                s.TextArea("hazard", "Hazard and hazardous situation").Required().Help("What can go wrong, and how someone or something is exposed to it.");
                s.TextArea("harm", "Potential harm or consequence");
                s.Link("previous_assessment", "Previous assessment", "risk_assessment").ReadOnly().Help("Set when this assessment re-assesses an earlier one (RSK-008).");
            }, icon: "bi-exclamation-triangle")
            .Section("summary", "Risk summary", s =>
            {
                s.Number("current_score", "Current score", 3).ReadOnly().Decimals(0).Help("Residual score when the residual risk has been assessed, otherwise the initial score.");
                s.Keyword("current_level", "Current risk level", "risk_level", 3).ReadOnly().Header();
                s.Keyword("acceptance_status", "Acceptance", "risk_acceptance", 6).ReadOnly().Header()
                    .Help("High and Critical risks need a linked mitigation or an e-signed acceptance before they can be completed (INV-176).");
            }, icon: "bi-speedometer")
            .Section("initial", "Initial risk", s =>
            {
                s.Prompt("matrix_prompt", "Score = severity × likelihood on the 5 × 5 matrix. Low 1–4 and Medium 5–9 are acceptable; High 10–15 and Critical 16–25 are not (RSK-014).");
                s.Keyword("severity", "Severity", "risk_severity").RequiredIn("assessment")
                    .Help("1 Negligible: no injury · 2 Minor: temporary injury · 3 Serious: needs medical intervention · 4 Critical: permanent impairment · 5 Catastrophic: death.");
                s.Keyword("likelihood", "Likelihood", "risk_likelihood").RequiredIn("assessment")
                    .Help("1 Improbable (< 1 in 10⁶) · 2 Remote · 3 Occasional · 4 Probable · 5 Frequent (> 1 in 1,000).");
                s.Number("initial_score", "Initial score", 2).ReadOnly().Decimals(0);
                s.Keyword("initial_level", "Initial level", "risk_level", 2).ReadOnly();
                s.TextArea("existing_controls", "Existing controls", 6);
                s.TextArea("rationale", "Assessment rationale", 6).Help("The evidence behind the ratings: data, field history, test results.");
            }, tab: "Assessment", icon: "bi-grid-3x3")
            .Subform("mitigations", "Mitigations", s =>
            {
                s.Text("measure", "Risk control measure", 4).Required();
                s.Keyword("control_type", "Type", "risk_control", 3);
                s.User("mit_owner", "Owner", 2);
                s.Number("days", "Days", 1).Min(1).Max(365).Decimals(0);
                s.Link("linked_record", "Linked CAPA / change").Width(2);
            }, tab: "Assessment", rowNoun: "mitigation", icon: "bi-shield-plus",
               description: "Rows with an owner become action items when the assessment enters Mitigation. Link an existing CAPA or change record instead when the work is already tracked there.")
            .Section("residual", "Residual risk", s =>
            {
                s.Keyword("residual_severity", "Residual severity", "risk_severity").RequiredIn("mitigation");
                s.Keyword("residual_likelihood", "Residual likelihood", "risk_likelihood").RequiredIn("mitigation");
                s.Number("residual_score", "Residual score", 2).ReadOnly().Decimals(0);
                s.Keyword("residual_level", "Residual level", "risk_level", 2).ReadOnly();
                s.TextArea("verification", "Verification of control effectiveness").RequiredIn("mitigation")
                    .Help("How you know the controls work: test reports, validation, monitoring data.");
            }, tab: "Assessment", icon: "bi-shield-check")
            .Section("acceptance", "Risk acceptance", s =>
            {
                s.Prompt("acceptance_prompt", "Quality approvers accept an unacceptable risk with the Accept risk action. The acceptance is e-signed and is cleared automatically if the risk score changes afterwards (SAF-045).");
                s.User("accepted_by", "Accepted by", 4).ReadOnly().Protected();
                s.Date("accepted_on", "Accepted on", 4).ReadOnly().Protected();
                s.Number("accepted_score", "Accepted score", 4).ReadOnly().Protected().Decimals(0);
                s.TextArea("acceptance_rationale", "Benefit-risk rationale").ReadOnly().Protected();
            }, tab: "Review", icon: "bi-pen")
            .Section("review", "Review", s =>
            {
                s.Date("review_date", "Next periodic review", 4).RequiredIn("review").Default("$today+365");
                s.Keyword("review_state", "Review status", "risk_review", 4).ReadOnly();
                s.TextArea("review_notes", "Review conclusion").RequiredIn("review");
            }, tab: "Review", icon: "bi-calendar-check")
            .Workflow("risk_assessment", "Risk assessment", w =>
            {
                w.Start("draft", "Draft").Purpose("Describe the hazard, its source and who owns the risk.")
                    .DueDays(5).Edit("identification").Hide("summary", "initial", "mitigations", "residual", "acceptance", "review");
                w.Phase("assessment", "Assessment").Purpose("Rate severity and likelihood on the 5 × 5 matrix and plan the mitigations.")
                    .AssignField("owner").DueDays(10).Edit("identification", "initial", "mitigations").ReadOnly("summary", "acceptance").Hide("residual", "review");
                w.Phase("mitigation", "Mitigation").Purpose("Carry out the mitigations (each owner's row is an action item), then rate the residual risk and record how the controls were verified.")
                    .AssignField("owner").DueDays(30).Edit("mitigations", "residual").ReadOnly("identification", "summary", "initial", "acceptance").Hide("review")
                    .MustComplete("action_item").Activities("action_item");
                w.Phase("review", "Review").Purpose("Quality reviews the residual risk, records the conclusion and next review date, and accepts any remaining unacceptable risk with an e-signature.")
                    .AssignGroup("qa-approvers").DueDays(10).Edit("review").ReadOnlyRest();
                w.Completed("completed", "Completed");
                w.Voided();

                w.Forward("draft", "assessment", label: "Submit for assessment");
                w.Void("draft");
                w.Forward("assessment", "mitigation", label: "Start mitigation");
                w.Forward("assessment", "review", label: "Send to review without mitigation");
                w.Back("assessment", "draft");
                w.Void("assessment");
                w.Forward("mitigation", "review", label: "Send to review");
                w.Back("mitigation", "assessment", label: "Back to assessment");
                w.Forward("review", "completed", label: "Approve and complete", sign: "Risk review approved");
                w.Back("review", "mitigation", label: "Reopen mitigation");
                w.Back("review", "assessment", label: "Reassess");
            }));

        app.View("open_by_level", "Open risks by level", v => v.Category("1. Risks").Forms("risk_assessment").Open()
            .Columns("number", "title", "risk_category", "current_level", "current_score", "acceptance_status", "phase", "due", "owner").Sort("current_score").Default());
        app.View("mine", "My risk assessments", v => v.Category("1. Risks").Forms("risk_assessment").Mine()
            .Columns("number", "title", "current_level", "phase", "due").Sort("due", false));
        app.View("unacceptable", "High and critical risks", v => v.Category("1. Risks").Forms("risk_assessment").Open()
            .Filter("current_level", "set").Filter("current_level", "ne", "low").Filter("current_level", "ne", "medium")
            .Columns("number", "title", "current_level", "current_score", "acceptance_status", "phase", "owner").Sort("current_score"));
        app.View("awaiting_acceptance", "Awaiting mitigation or acceptance", v => v.Category("1. Risks").Forms("risk_assessment").Open()
            .Filter("acceptance_status", "eq", "required")
            .Columns("number", "title", "current_level", "current_score", "phase", "due", "owner").Sort("current_score"));
        app.View("register", "Risk register", v => v.Category("2. Register").Forms("risk_assessment").All().Filter("current_level", "set")
            .Columns("number", "title", "risk_category", "initial_level", "current_level", "acceptance_status", "phase", "owner", "review_date").Sort("current_score"));
        app.View("due_review", "Risks due for review", v => v.Category("2. Register").Forms("risk_assessment").Completed()
            .Filter("review_state", "set").Filter("review_state", "ne", "current")
            .Columns("number", "title", "current_level", "review_date", "review_state", "owner").Sort("review_date", false));
        app.View("completed", "Completed assessments", v => v.Category("2. Register").Forms("risk_assessment").Completed()
            .Columns("number", "title", "risk_category", "current_level", "owner", "modified").Sort("modified"));
        app.View("voided", "Voided assessments", v => v.Category("2. Register").Forms("risk_assessment").Voided()
            .Columns("number", "title", "owner", "modified"));
        app.View("actions", "Risk mitigation action items", v => v.Category("3. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee"));
    }

    public Task SeedDemoAsync(DemoContext demo) => RiskDemo.SeedAsync(demo);
}
