using Vellum.Domain;
using Vellum.Packaging;

namespace Vellum.Modules.Risk;

/// <summary>
/// FMEA (§16.3): design, process and supplier FMEAs with a server-calculated worksheet (RPN = S × O × D, revised RPN
/// and reduction %, INV-178), thresholds that force a recommended action or a justification (FMEA-006, INV-179),
/// recommended actions turned into Action Items (FMEA-007) and a Pareto of the highest RPNs.
/// Workflow: Pending → Assigned → Completed (FMEA-003).
/// </summary>
public sealed class FmeaApp : IPackagedApp
{
    public string Key => "fmea";
    public int Order => 76;

    public void Define(AppBuilder app)
    {
        app.Name("FMEA")
            .Description("Design and process failure mode and effects analysis with calculated RPNs and tracked recommended actions.")
            .Icon("bi-diagram-3").Color("#9333ea").Suite("Risk").Order(20).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@engineering", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        app.Keywords("fmea_type", "FMEA type", k => k
            .Add("design", "Design FMEA (DFMEA)", "info").Add("process", "Process FMEA (PFMEA)", "violet").Add("supplier", "Supplier FMEA", "secondary"));

        app.Form("fmea", "FMEA", f => f
            .Icon("bi-diagram-3")
            .Description("Failure modes, effects and causes rated for severity, occurrence and detection, with an action plan.")
            .Numbering("FMEA-", 4)
            .Title("{subject}")
            .Section("scope", "FMEA scope", s =>
            {
                s.Text("subject", "FMEA title", 12).Required().MaxLength(200).Placeholder("e.g. VX-200 housing assembly process FMEA");
                s.Keyword("fmea_type", "Type", "fmea_type").Required().Header().Default("process");
                s.Text("product", "Product / assembly", 4).Required();
                s.User("owner", "FMEA owner", 4).Required().Default("$me").Header();
                s.Users("team", "Cross-functional team", 6);
                s.Link("related_risk", "Related risk assessment", "risk_assessment");
                s.TextArea("scope_text", "Scope and boundaries").Help("Which item, design or process steps are in scope, and the operating conditions assumed.");
                s.Link("related_record", "Related design project / change").Help("For example the design project, the change record or the process FMEA's control plan.");
                s.Number("severity_threshold", "Severity threshold", 3).Default("9").Min(1).Max(10).Decimals(0)
                    .Help("Rows with severity at or above this need a recommended action or a documented justification (FMEA-006).");
                s.Number("rpn_threshold", "RPN threshold", 3).Default("100").Min(1).Max(1000).Decimals(0)
                    .Help("Rows with an RPN at or above this need a recommended action or a documented justification (FMEA-006).");
            }, icon: "bi-bullseye")
            .Section("summary", "Worksheet summary", s =>
            {
                s.Number("rows_total", "Rows", 2).ReadOnly().Decimals(0);
                s.Number("max_rpn", "Highest RPN", 2).ReadOnly().Decimals(0).Header();
                s.Number("flagged_rows", "Rows over a threshold", 3).ReadOnly().Decimals(0);
                s.Number("open_flags", "Still need an action or justification", 3).ReadOnly().Decimals(0).Header();
                s.Number("avg_reduction", "Average RPN reduction (%)", 2).ReadOnly().Decimals(1);
            }, icon: "bi-clipboard-data")
            .Subform("worksheet", "FMEA worksheet", s =>
            {
                s.Text("item_function", "Item / function", 2).Required();
                s.Text("failure_mode", "Failure mode", 2).Required();
                s.Text("effect", "Effect", 2).Required().Help("State any safety or regulatory noncompliance (FMEA-004).");
                s.Number("severity", "S", 1).Required().Min(1).Max(10).Decimals(0);
                s.Text("cause", "Cause", 2).Required();
                s.Number("occurrence", "O", 1).Required().Min(1).Max(10).Decimals(0);
                s.Text("controls", "Current controls", 2);
                s.Number("detection", "D", 1).Required().Min(1).Max(10).Decimals(0);
                s.Number("rpn", "RPN", 1).ReadOnly().Decimals(0);
                s.Text("attention", "Flag", 1).ReadOnly();
                s.Text("recommended_action", "Recommended action", 2);
                s.Text("justification", "Justification (no action)", 2);
                s.User("responsible", "Responsible", 2);
                s.Text("action_taken", "Action taken", 2);
                s.Number("rev_severity", "S′", 1).Min(1).Max(10).Decimals(0);
                s.Number("rev_occurrence", "O′", 1).Min(1).Max(10).Decimals(0);
                s.Number("rev_detection", "D′", 1).Min(1).Max(10).Decimals(0);
                s.Number("rev_rpn", "RPN′", 1).ReadOnly().Decimals(0);
                s.Number("reduction", "Reduction %", 1).ReadOnly().Decimals(1);
            }, tab: "Worksheet", rowNoun: "failure mode", icon: "bi-table",
               description: "RPN = S × O × D, RPN′ = S′ × O′ × D′ and reduction % = (RPN − RPN′) ÷ RPN are calculated when you save and can't be typed in (INV-178). Rate S, O and D from 1 to 10.")
            .Section("conclusion", "Conclusion", s =>
            {
                s.TextArea("conclusion", "Summary and conclusion").RequiredIn("assigned")
                    .Help("The overall result: the highest risks, the actions committed and whether the design or process is acceptable.");
            }, tab: "Worksheet", icon: "bi-check2-square")
            .Workflow("fmea", "FMEA", w =>
            {
                w.Start("pending", "Pending").Purpose("Define the scope, the team and the thresholds, and start the worksheet.")
                    .DueDays(10).Edit("scope", "worksheet").ReadOnly("summary").Hide("conclusion");
                w.Phase("assigned", "Assigned").Purpose("Complete the worksheet: rate S, O and D, add recommended actions and turn them into action items. Rows at or over the severity or RPN threshold need a recommended action or a justification before the FMEA can be completed (INV-179).")
                    .AssignField("owner").DueDays(30).Edit("scope", "worksheet", "conclusion").ReadOnly("summary").Activities("action_item");
                w.Completed("completed", "Completed");
                w.Voided();

                w.Forward("pending", "assigned", label: "Assign for analysis");
                w.Void("pending");
                w.Forward("assigned", "completed", label: "Complete FMEA", sign: "FMEA approved");
                w.Back("assigned", "pending", label: "Send back to pending");
                w.Void("assigned");
            }));

        app.View("by_status", "FMEAs by status", v => v.Category("1. FMEAs").Forms("fmea").All()
            .Columns("number", "title", "fmea_type", "product", "phase", "max_rpn", "open_flags", "owner").Sort("phase", false).Default());
        app.View("open", "Open FMEAs", v => v.Category("1. FMEAs").Forms("fmea").Open()
            .Columns("number", "title", "fmea_type", "phase", "due", "max_rpn", "open_flags", "owner").Sort("due", false));
        app.View("mine", "My FMEAs", v => v.Category("1. FMEAs").Forms("fmea").Mine()
            .Columns("number", "title", "phase", "due", "open_flags"));
        app.View("design", "Design FMEAs", v => v.Category("2. By type").Forms("fmea").All().Filter("fmea_type", "eq", "design")
            .Columns("number", "title", "product", "phase", "max_rpn", "owner"));
        app.View("process", "Process FMEAs", v => v.Category("2. By type").Forms("fmea").All().Filter("fmea_type", "eq", "process")
            .Columns("number", "title", "product", "phase", "max_rpn", "owner"));
        app.View("completed", "Completed FMEAs", v => v.Category("3. Closed").Forms("fmea").Completed()
            .Columns("number", "title", "fmea_type", "max_rpn", "avg_reduction", "modified").Sort("modified"));
        app.View("voided", "Voided FMEAs", v => v.Category("3. Closed").Forms("fmea").Voided().Columns("number", "title", "owner", "modified"));
        app.View("actions", "FMEA action items", v => v.Category("4. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee"));
    }

    public Task SeedDemoAsync(DemoContext demo) => FmeaDemo.SeedAsync(demo);
}
