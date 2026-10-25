using Vellum.Domain;
using Vellum.Packaging;

namespace Vellum.Modules.Inspections;

/// <summary>
/// Inspection management §14.4: criteria profiles (INV-068) → inspection points → inspection plans (Define → Approve → Active → Close),
/// inspection records for Active plans only (INV-166) with samples evaluated per INS-003 (INV-069), sample results (INV-167),
/// forced review (INV-169) and the record result once every point's samples are taken (INV-168).
/// The evaluation math lives in <see cref="InspectionEvaluator"/>.
/// </summary>
public sealed class InspectionsApp : IPackagedApp
{
    public string Key => "inspections";
    public int Order => 60;

    public void Define(AppBuilder app)
    {
        app.Name("Inspections")
            .Description("Define criteria, inspection points and plans; inspect lots sample by sample with automatic Pass / Warn / Fail results.")
            .Icon("bi-rulers").Color("#15803d").Suite("Quality").Order(40).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@qc-lab", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("jlee", AppRole.Manager, designer: true)
            .Grant("sortiz", AppRole.Manager);

        app.Keywords("ins_data_type", "Criterion data type", k => k.Add("numeric", "Numeric").Add("keyword", "Keyword list"));
        app.Keywords("ins_numeric_type", "Numeric criterion type", k => k
            .Add("range", "RANGE (LSL … USL)").Add("target", "TARGET (± tolerance)").Add("min", "MIN (≥ LSL)").Add("max", "MAX (≤ USL)"));
        app.Keywords("ins_result", "Inspection result", k => k
            .Add("pass", "Pass", "success").Add("warn", "Warn", "warning").Add("fail", "Fail", "danger").Add("pending", "Pending", "secondary"));
        app.Keywords("ins_class", "Criterion class", k => k
            .Add("critical", "Critical", "danger").Add("major", "Major", "warning").Add("minor", "Minor", "info"));
        app.Keywords("ins_sampling", "Sampling", k => k
            .Add("quantity", "Quantity-based (n per lot quantity)").Add("time", "Time-based (n every N minutes)").Add("full", "100 %").Add("none", "None"));
        app.Keywords("ins_review_for", "Review required for", k => k
            .Add("never", "Never").Add("warnings", "Criteria warnings").Add("failures", "Criteria failures").Add("sample_failures", "Sample failures").Add("always", "Always"));
        app.Keywords("ins_point_status", "Inspection point status", k => k
            .Add("active", "Active", "success").Add("inactive", "Inactive", "secondary").Add("voided", "Voided", "dark"));

        // ------------------------------------------------------------------ criteria profiles (INS-001, INS-002)
        app.Form("criteria_profile", "Criteria profile", f => f
            .Icon("bi-sliders")
            .Description("One inspection criterion: numeric limits or a keyword list with Pass / Warn / Fail results.")
            .Numbering("CRT-", 4)
            .Title("{name}")
            .NonWorkflow()
            .Section("criterion", "Criterion", s =>
            {
                s.Text("name", "Name", 8).Required().MaxLength(120).Placeholder("For example: Tube outside diameter");
                s.YesNo("enabled", "Enabled", 4).Default("yes").Help("Disabled profiles can't be picked for inspection points.");
                s.Keyword("data_type", "Data type", "ins_data_type").Required().Default("numeric");
                s.Keyword("numeric_type", "Numeric type", "ins_numeric_type").VisibleWhen("data_type=numeric")
                    .Help("Controls which limits apply (INS-001).");
                s.Text("unit", "Unit", 4).VisibleWhen("data_type=numeric").Placeholder("mm, N, psi…");
                s.Text("method", "Measurement method", 12).Placeholder("For example: laser micrometer, 3 readings averaged");
                s.Text("description", "Composite description", 12).ReadOnly();
            }, icon: "bi-sliders")
            .Section("limits", "Numeric limits", s =>
            {
                s.Prompt("limits_prompt", "Limits must satisfy LSL ≤ LCL ≤ Target ≤ UCL ≤ USL (INV-068). Outside the spec limits is Fail; within spec but outside the control limits is Warn; otherwise Pass (INS-003).");
                s.Number("lsl", "Lower spec limit (LSL)", 4).VisibleWhen("numeric_type=range||numeric_type=min");
                s.Number("lcl", "Lower control limit (LCL)", 4).VisibleWhen("numeric_type=range||numeric_type=min||numeric_type=target");
                s.Number("target", "Target", 4).VisibleWhen("numeric_type");
                s.Number("ucl", "Upper control limit (UCL)", 4).VisibleWhen("numeric_type=range||numeric_type=max||numeric_type=target");
                s.Number("usl", "Upper spec limit (USL)", 4).VisibleWhen("numeric_type=range||numeric_type=max");
                s.Number("tolerance", "Tolerance (±)", 4).Min(0).VisibleWhen("numeric_type=target");
            }, icon: "bi-rulers")
            .Subform("keywords", "Keyword results", s =>
            {
                s.Text("keyword", "Keyword", 6).Required();
                s.Keyword("kw_result", "Result", "ins_result", 4).Required();
                s.YesNo("kw_disabled", "Disabled", 2);
            }, rowNoun: "keyword", icon: "bi-list-ul", description: "For keyword-list criteria: each observed keyword maps to Pass, Warn or Fail. Disabled keywords aren't offered."));

        // ------------------------------------------------------------------ inspection points (INS-004)
        app.Form("inspection_point", "Inspection point", f => f
            .Icon("bi-crosshair")
            .Description("What to inspect at one step: criteria with classes, the sampling rule and when a sample needs review.")
            .Numbering("IPT-", 4)
            .Title("{name}")
            .NonWorkflow()
            .Section("point", "Inspection point", s =>
            {
                s.Text("name", "Name", 8).Required().MaxLength(150);
                s.Keyword("status", "Status", "ins_point_status", 4).Required().Default("active").Header()
                    .Help("Inactive or voided points can't be added to plans (INS-005).");
                s.TextArea("description", "Instructions");
                s.Keyword("sampling", "Sampling", "ins_sampling", 4).Required().Default("quantity");
                s.Number("sample_qty", "Samples", 2).Min(1).Decimals(0).Default("1").VisibleWhen("sampling=quantity||sampling=time");
                s.Number("per_qty", "Per lot quantity", 3).Min(1).Decimals(0).VisibleWhen("sampling=quantity").Help("For example 5 samples per 500 units.");
                s.Number("per_minutes", "Every N minutes", 3).Min(1).Decimals(0).VisibleWhen("sampling=time");
                s.Keywords("failure_classes", "Sample failure classes", "ins_class", 6)
                    .Help("A sample fails if a criterion of one of these classes fails (INV-167). Defaults to Critical and Major.");
                s.Keyword("review_for", "Review required for", "ins_review_for", 6).Required().Default("failures")
                    .Help("Samples meeting this condition must pass through Review (INV-169).");
            }, icon: "bi-crosshair")
            .Subform("criteria", "Inspection criteria", s =>
            {
                s.Link("profile", "Criteria profile", "criteria_profile").Width(5);
                s.Text("reference", "Reference (drawing, spec)", 3);
                s.Keyword("class", "Class", "ins_class", 2).Required();
                s.Text("cid", "CID", 2).Help("Defaults to ⟨point number⟩-⟨row⟩.");
            }, rowNoun: "criterion", required: true, icon: "bi-list-check"));

        // ------------------------------------------------------------------ inspection plans (INS-005)
        app.Form("inspection_plan", "Inspection plan", f => f
            .Icon("bi-diagram-3")
            .Description("An ordered set of inspection points for a product or part. Records can only be created for Active plans.")
            .Numbering("IPL-", 4)
            .Title("{name}")
            .Section("plan", "Plan", s =>
            {
                s.Text("name", "Plan name", 8).Required().MaxLength(150);
                s.User("owner", "Plan owner", 4).Required().Default("$me").Header();
                s.Text("product", "Product / part", 6).Header();
                s.Text("part_revision", "Part revision", 3);
                s.Link("parent_plan", "Parent plan", "inspection_plan");
                s.TextArea("description", "Scope and instructions");
            }, icon: "bi-diagram-3")
            .Subform("points", "Inspection points", s =>
            {
                s.Number("step", "Step", 1).Min(1).Decimals(0);
                s.Link("point", "Inspection point", "inspection_point").Width(6);
                s.Text("notes", "Notes", 5);
            }, rowNoun: "point", required: true, icon: "bi-list-ol", description: "Inactive or voided points can't be added. Steps default to the row order.")
            .Workflow("inspection_plan", "Inspection plan", w =>
            {
                w.Start("define", "Define").Purpose("List the inspection points in order and describe the plan.").DueDays(10).Edit("plan", "points");
                w.Phase("approve", "Approve").Purpose("Quality approves the inspection plan.").AssignGroup("qa-approvers").DueDays(5).ReadOnlyRest();
                w.Phase("active", "Active").Purpose("Inspection records can be created for this plan. Close it when the plan is retired.")
                    .AssignField("owner").DueDays(250).ReadOnlyRest();
                w.Completed("closed", "Closed");
                w.Voided();
                w.Forward("define", "approve", label: "Submit for approval");
                w.Void("define");
                w.Forward("approve", "active", label: "Approve plan", sign: "Approved");
                w.Back("approve", "define", label: "Return to define");
                w.Forward("active", "closed", label: "Close plan", comment: true);
            }));

        // ------------------------------------------------------------------ inspection records (INS-010, INS-011, INS-015)
        app.Form("inspection_record", "Inspection record", f => f
            .Icon("bi-clipboard2-data")
            .Description("The inspection of one lot against an Active inspection plan, sample by sample.")
            .Numbering("INR-")
            .Title("Lot {lot_number} – {plan_name}")
            .Section("record", "Lot", s =>
            {
                s.Link("plan", "Inspection plan", "inspection_plan").Required().Help("Only Active plans can be used (INV-166).");
                s.Text("plan_name", "Plan", 6).ReadOnly();
                s.Text("record_name", "Record name", 6);
                s.Text("lot_number", "Lot number", 4).Required().Header();
                s.Number("lot_qty", "Lot quantity", 4).Required().Min(1).Decimals(0).Header();
                s.User("inspector", "Inspector", 4).Required().Default("$me").Header();
                s.DateTime("lot_start", "Lot start");
                s.DateTime("lot_end", "Lot end");
            }, icon: "bi-box-seam")
            .Section("outcome", "Result", s =>
            {
                s.Keyword("result", "Result", "ins_result", 3).ReadOnly().Header();
                s.Number("total_samples", "Total samples", 3).ReadOnly().Decimals(0);
                s.Number("remaining_samples", "Remaining samples", 3).ReadOnly().Decimals(0);
                s.Number("failed_samples", "Failed samples", 3).ReadOnly().Decimals(0);
                s.Text("result_note", "Status", 12).ReadOnly();
                s.TextArea("remarks", "Remarks");
            }, icon: "bi-clipboard-check")
            .Workflow("inspection_record", "Inspection record", w =>
            {
                w.Start("inspect", "Inspect").Purpose("Take the samples each point needs (Actions › Create sample). The result is set once no samples remain (INV-168).")
                    .AssignField("inspector").DueDays(5).Edit("record", "outcome").MustComplete("inspection_sample").Activities("action_item");
                w.Phase("review", "Review").Purpose("Quality reviews the inspection, its failures and follow-up actions.")
                    .AssignGroup("qa-approvers").DueDays(3).Edit("outcome").ReadOnlyRest().Activities("action_item");
                w.Completed("closed", "Closed");
                w.Voided();
                w.Forward("inspect", "closed", label: "Close record", when: "result=pass");
                w.Forward("inspect", "review", label: "Send to review");
                w.Void("inspect");
                w.Forward("review", "closed", label: "Accept and close", sign: "Reviewed");
                w.Back("review", "inspect", label: "Back to inspection");
                w.Void("review");
            }));

        // ------------------------------------------------------------------ inspection samples (INS-012, INS-014, INS-016, INS-017)
        app.Form("inspection_sample", "Inspection sample", f => f
            .Icon("bi-eyedropper")
            .Description("One sample of a lot: measured values per criterion, evaluated Pass / Warn / Fail.")
            .Numbering("SMP-")
            .Title("{serial} – {point_name}")
            .Activity("inspection_record")
            .Section("sample", "Sample", s =>
            {
                s.Text("serial", "Serial / sample ID", 4).Required().Header();
                s.Link("point", "Inspection point", "inspection_point").Required();
                s.Text("point_name", "Point", 6).ReadOnly();
                s.User("taken_by", "Taken by", 4).Default("$me");
                s.DateTime("taken_on", "Taken on", 4).Default("$now");
            }, icon: "bi-eyedropper")
            .Subform("measurements", "Measurements", s =>
            {
                s.Text("cid", "CID", 1).ReadOnly();
                s.Text("criterion", "Criterion", 2).ReadOnly();
                s.Keyword("class", "Class", "ins_class", 1).ReadOnly();
                s.Text("spec", "Specification", 3).ReadOnly();
                s.Number("measured", "Measured", 1);
                s.Text("observed", "Observed", 1).Help("For keyword criteria: one of the allowed keywords.");
                s.Text("tool", "Tool", 1);
                s.Keyword("result", "Result", "ins_result", 1).ReadOnly();
                s.Text("remarks", "Defect / remarks", 1);
            }, rowNoun: "measurement", icon: "bi-rulers",
               description: "Loaded from the inspection point. Enter a measured value (numeric) or an observed keyword; the result is calculated on save (INV-069).")
            .Section("sample_outcome", "Sample result", s =>
            {
                s.Keyword("sample_result", "Sample result", "ins_result", 4).ReadOnly().Header();
                s.YesNo("review_needed", "Review required", 4).ReadOnly();
                s.TextArea("summary", "Warn / fail summary").ReadOnly();
                s.TextArea("corrections", "Remarks and corrections");
            }, icon: "bi-clipboard-check")
            .Workflow("inspection_sample", "Inspection sample", w =>
            {
                w.Start("inspect", "Inspect").Purpose("Enter each measurement and save to calculate the results.")
                    .DueDays(2).Edit("sample", "measurements", "sample_outcome");
                w.Phase("review", "Review").Purpose("Review the sample's warnings and failures and decide on follow-up.")
                    .AssignGroup("qa-approvers").DueDays(3).Edit("sample_outcome").ReadOnlyRest();
                w.Completed("closed", "Closed");
                w.Voided();
                w.Forward("inspect", "closed", label: "Close sample", when: "review_needed!=yes");
                w.Forward("inspect", "review", label: "Send to review");
                w.Void("inspect");
                w.Forward("review", "closed", label: "Accept and close", sign: "Reviewed");
                w.Back("review", "inspect", label: "Back to inspection");
            }));

        // ------------------------------------------------------------------ views (INS-024)
        app.View("mine", "My inspections", v => v.Category("1. My work").Forms("inspection_record", "inspection_sample").Mine()
            .Columns("number", "title", "form", "phase", "due").Sort("due", false).Default());
        app.View("records_open", "Open inspection records", v => v.Category("2. Inspect").Forms("inspection_record").Open()
            .Columns("number", "title", "phase", "lot_qty", "remaining_samples", "result", "inspector"));
        app.View("records_all", "Inspection records", v => v.Category("2. Inspect").Forms("inspection_record").All()
            .Columns("number", "title", "phase", "result", "total_samples", "failed_samples", "modified"));
        app.View("records_failed", "Failed inspections", v => v.Category("2. Inspect").Forms("inspection_record").All().Filter("result", "eq", "fail")
            .Columns("number", "title", "phase", "failed_samples", "inspector", "modified"));
        app.View("samples_open", "Open samples", v => v.Category("2. Inspect").Forms("inspection_sample").Open()
            .Columns("number", "title", "phase", "sample_result", "review_needed", "taken_by"));
        app.View("samples_review", "Samples awaiting review", v => v.Category("2. Inspect").Forms("inspection_sample").Open().Phases("review")
            .Columns("number", "title", "sample_result", "taken_by", "due"));
        app.View("samples_all", "Inspection samples", v => v.Category("2. Inspect").Forms("inspection_sample").All()
            .Columns("number", "title", "phase", "sample_result", "taken_by", "taken_on"));
        app.View("plans", "Inspection plans", v => v.Category("3. Setup").Forms("inspection_plan").All()
            .Columns("number", "title", "phase", "product", "owner"));
        app.View("points", "Inspection points", v => v.Category("3. Setup").Forms("inspection_point").All()
            .Columns("number", "title", "status", "sampling", "review_for"));
        app.View("profiles", "Criteria profiles", v => v.Category("3. Setup").Forms("criteria_profile").All()
            .Columns("number", "title", "data_type", "description", "enabled"));
        app.View("actions", "Inspection action items", v => v.Category("4. Activity").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee"));
    }

    public Task SeedDemoAsync(DemoContext d) => InspectionsDemo.SeedAsync(d);
}
