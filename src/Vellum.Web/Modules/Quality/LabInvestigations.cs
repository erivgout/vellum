using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// Laboratory investigations §12.6 (OOS/OOT): Initiate → Lab Investigation → (lab error) Final Approval → Closed, or
/// (no lab error) Lab Investigation Approval → Full Investigation → Final Approval → Closed (LAB-002, INV-044).
/// Original results are invalidated only with an identified lab error (LAB-003). Testing activities: re-test by a different analyst,
/// re-sample by the same analyst (LAB-004, LAB-005, INV-045). Gates per LAB-006; e-signatures per LAB-007.
/// </summary>
public sealed class LabInvestigationsApp : IPackagedApp
{
    public string Key => "lab_investigations";
    public int Order => 42;

    private const string NoLabError = "lab_error_identified=no";

    public void Define(AppBuilder app)
    {
        app.Name("Lab Investigations (OOS/OOT)")
            .Description("Investigate out-of-specification and out-of-trend laboratory results, following FDA guidance on OOS investigations.")
            .Icon("bi-eyedropper").Color("#4f46e5").Suite("Quality").Order(60).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@qc-lab", AppRole.Author)
            .Grant("@quality", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        QualitySites.Keywords(app);
        app.Keywords("lab_result_type", "Result type", k => k
            .Add("oos", "OOS (out of specification)", "danger").Add("oot", "OOT (out of trend)", "warning"));
        app.Keywords("lab_test_kind", "Testing type", k => k
            .Add("retest", "Re-test (same sample, different analyst)", "info").Add("resample", "Re-sample (new sample, same analyst)", "secondary"));
        app.Keywords("lab_error_type", "Lab error type", k => k
            .Add("calculation", "Calculation / transcription error").Add("instrument", "Instrument malfunction").Add("sample_prep", "Sample preparation error")
            .Add("method", "Method not followed").Add("standard", "Standard / reagent problem").Add("glassware", "Glassware / contamination"));
        app.Keywords("lab_batch_disposition", "Batch disposition", k => k
            .Add("release", "Release", "success").Add("reject", "Reject", "danger").Add("pending", "Pending further data", "warning"));
        app.Keywords("lab_test_outcome", "Testing outcome", k => k
            .Add("pass", "Meets specification", "success").Add("fail", "Fails specification", "danger"));

        app.Form("lab_investigation", "Lab Investigation", f => f
            .Icon("bi-eyedropper")
            .Description("An OOS or OOT laboratory result: the lab investigation, any re-testing, and the full investigation when no lab error is found.")
            .Numbering("LAB-")
            .Title("{subject}")
            .Section("lab_general", "Result and batch", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("For example: EO residual above limit, ST-40 lot 24-131");
                s.Keyword("result_type", "Result type", "lab_result_type").Required().Header();
                s.Text("product", "Product / material", 4).Required();
                s.Text("batch_number", "Batch / lot", 4).Required().Header();
                s.Keyword("site", "Laboratory", "quality_site");
                s.Text("test_name", "Test", 4).Required();
                s.Text("specification", "Specification", 4).Required().Placeholder("≤ 4.0 mg/device");
                s.Text("original_result", "Reported result", 4).Required().Placeholder("5.8 mg/device");
                s.Date("test_date", "Tested on").Default("$today");
                s.User("analyst", "Original analyst").Required().Header().Default("$me").Help("Re-tests must go to a different analyst (INV-045).");
                s.User("lab_supervisor", "Lab supervisor").Required().Help("Leads the lab investigation phase.");
            }, icon: "bi-clipboard-data")
            .Section("lab_test_details", "Test details", s =>
            {
                s.Link("test_method", "Test method (approved SOP)").Help("Link the approved, effective test method.");
                s.Text("test_method_ref", "Method number and revision", 4);
                s.Text("instrument", "Instrument ID", 4);
                s.Date("instrument_calibrated_on", "Last calibrated").Help("From the instrument's asset record.");
                s.Text("reference_standard", "Reference standard / lot", 6);
            }, icon: "bi-cpu")
            .Section("lab_checklist", "Lab investigation checklist", s =>
            {
                s.Prompt("lab_checklist_prompt", "Phase I: look for an assignable laboratory error before anything else. Keep the original sample preparations.");
                s.YesNo("chk_calculations", "Calculations and transcription verified?", 6).RequiredIn("lab_investigation");
                s.YesNo("chk_method", "Method followed as written?", 6).RequiredIn("lab_investigation");
                s.YesNo("chk_instrument", "Instrument calibrated and system suitability met?", 6).RequiredIn("lab_investigation");
                s.YesNo("chk_standards", "Standards and reagents in date and correctly prepared?", 6).RequiredIn("lab_investigation");
                s.YesNo("chk_sample_prep", "Sample preparation correct (weights, dilutions)?", 6).RequiredIn("lab_investigation");
                s.YesNo("chk_analyst", "Analyst trained on the method?", 6).RequiredIn("lab_investigation");
            }, tab: "Lab investigation", icon: "bi-list-check")
            .Section("lab_error", "Lab error assessment", s =>
            {
                s.YesNo("additional_testing", "Is additional testing required?").Help("Add the re-tests or re-samples on the Testing tab.");
                s.YesNo("lab_error_identified", "Lab error identified and original results invalidated?").RequiredIn("lab_investigation")
                    .Help("Yes only with an assignable, documented lab error. No sends the investigation down the full-investigation path (INV-044).");
                s.Keyword("lab_error_type", "Type of lab error", "lab_error_type").VisibleWhen("lab_error_identified=yes").RequiredIn("lab_investigation");
                s.YesNo("results_invalidated", "Original results invalidated").ReadOnly().Header().Help("Set only when an assignable lab error is identified (LAB-003).");
                s.YesNo("full_investigation_required", "Full investigation required").ReadOnly();
                s.User("investigation_lead", "Full investigation lead").VisibleWhen(NoLabError).RequiredIn("lab_investigation");
                s.TextArea("lab_summary", "Lab investigation summary").RequiredIn("lab_investigation");
            }, tab: "Lab investigation", icon: "bi-search")
            .Subform("testing_requests", "Additional testing", s =>
            {
                s.Keyword("test_kind", "Type", "lab_test_kind", 3).Required();
                s.User("test_analyst", "Analyst", 3).Required();
                s.Number("replicates", "Replicates", 2).Required().Min(1).Max(12).Decimals(0);
                s.Text("test_reason", "Reason / hypothesis", 4);
            }, tab: "Testing", rowNoun: "test", icon: "bi-arrow-repeat",
               description: "Each row becomes a testing activity. A re-test goes to a different analyst; a re-sample goes to the original analyst automatically (INV-045).")
            .Section("full_investigation", "Full (manufacturing) investigation", s =>
            {
                s.Prompt("fi_prompt", "Phase II: no assignable lab error was found, so the result stands until the manufacturing investigation explains it (LAB-003).");
                s.TextArea("fi_reason", "Scope and reason").RequiredIn("full_investigation");
                s.YesNo("fi_batch_record", "Batch record reviewed?", 6).RequiredIn("full_investigation");
                s.YesNo("fi_deviations", "Deviations during manufacture reviewed?", 6).RequiredIn("full_investigation");
                s.YesNo("fi_equipment", "Equipment and maintenance logs reviewed?", 6).RequiredIn("full_investigation");
                s.YesNo("fi_materials", "Raw material records reviewed?", 6).RequiredIn("full_investigation");
                s.YesNo("fi_environment", "Environmental monitoring reviewed?", 6).RequiredIn("full_investigation");
                s.TextArea("fi_results", "Full investigation results").RequiredIn("full_investigation");
                s.Keyword("batch_disposition", "Batch disposition", "lab_batch_disposition").RequiredIn("full_investigation");
            }, tab: "Full investigation", icon: "bi-building-gear")
            .Section("conclusion", "Conclusion", s =>
            {
                s.Keyword("root_cause_category", "Root cause category", "root_cause_category");
                s.TextArea("root_cause", "Root cause");
                s.TextArea("final_conclusion", "Final conclusion").Help("Required before Final Approval.");
                s.Link("related_investigations", "Related lab investigations", "lab_investigation");
            }, tab: "Conclusion", icon: "bi-flag")
            .Section("lab_followup", "NCR and CAPA", s =>
            {
                s.YesNo("ncr_required", "Is an NCR required?");
                s.TextArea("ncr_justification", "Justification (no NCR)").VisibleWhen("ncr_required=no");
                s.Link("ncr_record", "Linked NCR", "ncr");
                s.YesNo("capa_required", "Is a CAPA required?");
                s.TextArea("capa_justification", "Justification (no CAPA)").VisibleWhen("capa_required=no");
                s.Link("capa_record", "Linked CAPA", "capa");
            }, tab: "Conclusion", icon: "bi-diagram-3")
            .Workflow("lab_investigation", "Lab investigation", w =>
            {
                w.Start("initiate", "Initiate").Purpose("Record the result, the batch and the test. Start the lab investigation the same day; don't discard the preparations.")
                    .DueDays(1).Edit("lab_general", "lab_test_details")
                    .Hide("lab_checklist", "lab_error", "testing_requests", "full_investigation", "conclusion", "lab_followup");
                w.Phase("lab_investigation", "Lab Investigation").Purpose("Phase I: check for an assignable laboratory error. Re-tests and re-samples run as testing activities.")
                    .AssignField("lab_supervisor").DueDays(5).Edit("lab_test_details", "lab_checklist", "lab_error", "testing_requests", "conclusion", "lab_followup")
                    .ReadOnly("lab_general").Hide("full_investigation").MustComplete("lab_test", "action_item").Activities("action_item");
                w.Phase("lab_approval", "Lab Investigation Approval").Purpose("Quality confirms no lab error was found and approves the full investigation (e-signed).")
                    .AssignGroup("qa-approvers").DueDays(2).ReadOnlyRest().Hide("full_investigation").MustComplete("action_item");
                w.Phase("full_investigation", "Full Investigation").Purpose("Phase II: investigate manufacturing. Testing activities and action items must be complete before you move on (LAB-006).")
                    .AssignField("investigation_lead").DueDays(20).Edit("full_investigation", "testing_requests", "conclusion", "lab_followup").ReadOnlyRest()
                    .MustComplete("lab_test", "action_item").Activities("action_item");
                w.Phase("final_approval", "Final Approval").Purpose("Quality approves the conclusion and the batch disposition (e-signed).")
                    .AssignGroup("qa-approvers").DueDays(3).ReadOnlyRest().MustComplete("lab_test", "action_item");
                w.Completed("closed", "Closed");
                w.Voided();

                w.Forward("initiate", "lab_investigation", label: "Start lab investigation");
                w.Void("initiate", sign: "Voided");
                w.Forward("lab_investigation", "final_approval", label: "Lab error confirmed: send to Final Approval", when: "lab_error_identified=yes");
                w.Forward("lab_investigation", "lab_approval", label: "No lab error: request full investigation", when: NoLabError);
                w.Back("lab_investigation", "initiate");
                w.Void("lab_investigation", sign: "Voided");
                w.Forward("lab_approval", "full_investigation", label: "Approve full investigation", sign: "Approved for full investigation");
                w.Back("lab_approval", "lab_investigation");
                w.Forward("full_investigation", "final_approval", label: "Send to Final Approval");
                w.Back("full_investigation", "lab_approval");
                w.Forward("final_approval", "closed", label: "Approve and close", sign: "Approved");
                w.Back("final_approval", "full_investigation", label: "Back to full investigation");
                w.Back("final_approval", "lab_investigation", label: "Back to lab investigation");
            }));

        // LAB-004: testing activity (re-test / re-sample).
        app.Form("lab_test", "Testing Activity", f => f
            .Icon("bi-arrow-repeat")
            .Description("A re-test or re-sample with one result row per replicate.")
            .Numbering("TST-")
            .Title("{subject}")
            .Activity("lab_investigation")
            .Section("test_info", "Testing activity", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200);
                s.Keyword("test_kind", "Type", "lab_test_kind").Required().Header();
                s.User("original_analyst", "Original analyst").ReadOnly();
                s.User("test_analyst", "Testing analyst").Required().Header()
                    .Help("Re-test: a different analyst from the original test. Re-sample: the original analyst (filled in automatically, INV-045).");
                s.Text("product", "Product / material", 4).ReadOnly();
                s.Text("batch_number", "Batch / lot", 4).ReadOnly();
                s.Text("test_name", "Test", 4).ReadOnly();
                s.Text("specification", "Specification", 4).ReadOnly();
                s.Number("replicates", "Replicates", 2).Required().Min(1).Max(12).Decimals(0);
                s.TextArea("test_reason", "Reason / hypothesis");
            }, icon: "bi-clipboard-data")
            .Subform("replicate_results", "Replicate results", s =>
            {
                s.Number("rep_no", "Replicate", 2).Required().Min(1).Decimals(0);
                s.Text("rep_result", "Result", 4).Required();
                s.YesNo("rep_meets_spec", "Meets spec?", 2).Required();
                s.Text("rep_notes", "Notes", 4);
            }, rowNoun: "replicate", required: true, icon: "bi-table", description: "One row per replicate.")
            .Section("test_outcome", "Outcome", s =>
            {
                s.Keyword("outcome", "Outcome", "lab_test_outcome").ReadOnly().Header().RequiredIn("testing")
                    .Help("Calculated: meets specification only if every replicate does.");
                s.TextArea("test_summary", "Summary").RequiredIn("testing");
            }, icon: "bi-flag")
            .Workflow("lab_test", "Testing activity", w =>
            {
                w.Start("initiate", "Initiate").Purpose("Confirm the testing type, analyst and number of replicates.").DueDays(1).Edit("test_info").Hide("replicate_results", "test_outcome");
                w.Phase("qc_approval", "QC Approval").Purpose("QC approves the testing plan before testing starts (e-signed).")
                    .AssignGroup("qa-approvers").DueDays(1).ReadOnlyRest().Hide("replicate_results", "test_outcome");
                w.Phase("testing", "Testing in Progress").Purpose("Run the replicates and record each result.")
                    .AssignField("test_analyst").DueDays(5).Edit("replicate_results", "test_outcome").ReadOnlyRest();
                w.Phase("final_approval", "Final Approval").Purpose("QC approves the results (e-signed).")
                    .AssignGroup("qa-approvers").DueDays(2).ReadOnlyRest();
                w.Completed("closed", "Closed");
                w.Voided();
                w.Forward("initiate", "qc_approval", label: "Submit for QC approval");
                w.Void("initiate", sign: "Voided");
                w.Forward("qc_approval", "testing", label: "QC approve: start testing", sign: "QC approved");
                w.Back("qc_approval", "initiate");
                w.Forward("testing", "final_approval", label: "Results complete");
                w.Back("testing", "qc_approval");
                w.Forward("final_approval", "closed", label: "Approve results", sign: "Approved");
                w.Back("final_approval", "testing");
            }));

        app.View("mine", "My lab investigations", v => v.Category("1. My work").Forms("lab_investigation").Mine()
            .Columns("number", "title", "phase", "due", "result_type", "batch_number").Sort("due", false).Default());
        app.View("my_tests", "My testing activities", v => v.Category("1. My work").Forms("lab_test").Mine()
            .Columns("number", "title", "phase", "due", "test_kind", "test_analyst").Sort("due", false));
        app.View("open", "Open investigations", v => v.Category("2. Investigations").Forms("lab_investigation").Open()
            .Columns("number", "title", "phase", "due", "result_type", "product", "batch_number", "analyst"));
        app.View("oos", "Open OOS", v => v.Category("2. Investigations").Forms("lab_investigation").Open().Filter("result_type", "eq", "oos")
            .Columns("number", "title", "phase", "due", "batch_number", "original_result", "specification"));
        app.View("full", "In full investigation", v => v.Category("2. Investigations").Forms("lab_investigation").Open().Phases("lab_approval", "full_investigation")
            .Columns("number", "title", "phase", "due", "investigation_lead", "batch_disposition"));
        app.View("invalidated", "Results invalidated (lab error)", v => v.Category("2. Investigations").Forms("lab_investigation").All().Filter("results_invalidated", "eq", "yes")
            .Columns("number", "title", "lab_error_type", "batch_number", "phase", "modified").Sort("modified"));
        app.View("tests", "Open testing activities", v => v.Category("3. Testing").Forms("lab_test").Open()
            .Columns("number", "title", "phase", "due", "test_kind", "test_analyst", "original_analyst"));
        app.View("closed", "Closed investigations", v => v.Category("4. Closed").Forms("lab_investigation").Completed()
            .Columns("number", "title", "result_type", "results_invalidated", "batch_disposition", "modified").Sort("modified"));
        app.View("voided", "Voided investigations", v => v.Category("4. Closed").Forms("lab_investigation").Voided().Columns("number", "title", "analyst", "modified"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        var records = d.Services.GetRequiredService<RecordService>();

        // 1. Open OOS in Full Investigation with a re-test by a second analyst underway.
        var l1 = await d.CreateAsync("lab_investigation", new()
        {
            ["subject"] = "EO residual above limit on ST-40 lot 24-131",
            ["result_type"] = "oos", ["product"] = "ST-40 sterile tubing set", ["batch_number"] = "24-131", ["site"] = "galway",
            ["test_name"] = "Ethylene oxide residual (ISO 10993-7, GC-FID)", ["specification"] = "≤ 4.0 mg/device", ["original_result"] = "5.8 mg/device",
            ["test_date"] = DemoContext.Days(-11), ["analyst"] = d.User("hmoreau"), ["lab_supervisor"] = d.User("sortiz"),
            ["test_method_ref"] = "TM-QC-031 rev F", ["instrument"] = "GC-07 (Agilent 8890)", ["instrument_calibrated_on"] = DemoContext.Days(-40), ["reference_standard"] = "EO-STD-2026-03",
        }, "hmoreau", daysAgo: 11);
        await d.RouteAsync(l1.Id, "lab_investigation");
        await d.UpdateAsync(l1.Id, new()
        {
            ["chk_calculations"] = true, ["chk_method"] = true, ["chk_instrument"] = true, ["chk_standards"] = true, ["chk_sample_prep"] = true, ["chk_analyst"] = true,
            ["additional_testing"] = true, ["lab_error_identified"] = false, ["investigation_lead"] = d.User("jlee"),
            ["lab_summary"] = "No assignable lab error: calculations verified, system suitability met (RSD 1.2%), standard in date, preparations re-injected with the same result (5.7 mg).",
        }, "sortiz");
        await d.RouteAsync(l1.Id, "lab_approval", assignees: ["dokafor"]);
        await d.RouteAsync(l1.Id, "full_investigation");
        await d.UpdateAsync(l1.Id, new()
        {
            ["fi_reason"] = "Lot 24-131 was aerated for 48 h instead of 72 h after EO chamber 2 was returned to service.",
            ["fi_batch_record"] = true, ["fi_deviations"] = true, ["fi_equipment"] = true,
        }, "jlee");
        await AddTestingRequestAsync(d, l1.Id, "retest", "jlee", 3, "Confirm result on retained extract with a second analyst");
        var snap1 = await records.LoadRequiredAsync(l1.Id);
        d.As("jlee");
        await LabTesting.CreateTestsAsync(new RecordContext(snap1, d.Services));
        var test1 = (await records.ChildrenAsync(l1.Id, "lab_test")).First();
        await d.RouteAsync(test1.Id, "qc_approval", assignees: ["sortiz"]);
        await d.RouteAsync(test1.Id, "testing");
        await SetReplicatesAsync(d, test1.Id, [("5.6 mg/device", false), ("5.9 mg/device", false)]);

        // 2. Closed OOT: calculation error identified, results invalidated; signatures throughout.
        var l2 = await d.CreateAsync("lab_investigation", new()
        {
            ["subject"] = "Tensile strength trending low on catheter hub bond lot CH-0802",
            ["result_type"] = "oot", ["product"] = "CH-1020 catheter hub assembly", ["batch_number"] = "CH-0802", ["site"] = "galway",
            ["test_name"] = "Bond tensile strength (n=10)", ["specification"] = "≥ 15 N; trend limit mean ≥ 18 N", ["original_result"] = "Mean 16.4 N",
            ["test_date"] = DemoContext.Days(-30), ["analyst"] = d.User("hmoreau"), ["lab_supervisor"] = d.User("sortiz"),
            ["test_method_ref"] = "TM-QC-014 rev C", ["instrument"] = "Instron 3345 (TT-02)", ["instrument_calibrated_on"] = DemoContext.Days(-90),
        }, "hmoreau", daysAgo: 30);
        await d.RouteAsync(l2.Id, "lab_investigation");
        await d.UpdateAsync(l2.Id, new()
        {
            ["chk_calculations"] = false, ["chk_method"] = true, ["chk_instrument"] = true, ["chk_standards"] = true, ["chk_sample_prep"] = true, ["chk_analyst"] = true,
            ["lab_error_identified"] = true, ["lab_error_type"] = "calculation",
            ["lab_summary"] = "The spreadsheet used a 5 kN load-cell conversion factor instead of 500 N. Recalculated mean is 19.1 N (within trend).",
            ["root_cause_category"] = "measurement", ["root_cause"] = "Unvalidated spreadsheet cell referenced the wrong load cell.",
            ["final_conclusion"] = "Original result invalidated (calculation error). Recalculated result 19.1 N meets the trend limit; lot released.",
            ["ncr_required"] = false, ["ncr_justification"] = "No product nonconformance.",
            ["capa_required"] = false, ["capa_justification"] = "Spreadsheet locked and validated under change CC-0405.",
        }, "sortiz");
        await d.RouteAsync(l2.Id, "final_approval", assignees: ["dokafor"]);
        await d.RouteAsync(l2.Id, "closed");

        // 3. Just raised.
        await d.CreateAsync("lab_investigation", new()
        {
            ["subject"] = "Bacterial endotoxin result at action level, AS-VX200 lot AS-2412",
            ["result_type"] = "oos", ["product"] = "VX-200 administration set", ["batch_number"] = "AS-2412", ["site"] = "galway",
            ["test_name"] = "Bacterial endotoxins (LAL kinetic chromogenic)", ["specification"] = "< 20 EU/device", ["original_result"] = "24.1 EU/device",
            ["test_date"] = DemoContext.Today, ["analyst"] = d.User("hmoreau"), ["lab_supervisor"] = d.User("sortiz"),
            ["test_method_ref"] = "TM-QC-008 rev H", ["instrument"] = "Plate reader PR-03",
        }, "hmoreau");
    }

    private static async Task AddTestingRequestAsync(DemoContext d, Guid id, string kind, string analyst, int replicates, string reason)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(id);
        var data = new RecordData();
        var rows = data.RowsOf("testing_requests");
        rows.AddRange(snap.Rows("testing_requests").Select(r => r.Clone()));
        var row = new RowBag { IsNew = true, Order = rows.Count };
        row.Values.Set("test_kind", FieldCodec.FromObject(snap.Field("test_kind")!, kind, snap.Config));
        row.Values.Set("test_analyst", Val.Ref(d.User(analyst)));
        row.Values.Set("replicates", Val.Of(replicates));
        row.Values.Set("test_reason", Val.Of(reason));
        rows.Add(row);
        var res = await records.SaveAsync(snap, data, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException("Demo testing request failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    private static async Task SetReplicatesAsync(DemoContext d, Guid id, (string Result, bool Meets)[] reps)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(id);
        var data = new RecordData();
        var rows = data.RowsOf("replicate_results");
        foreach (var (result, meets) in reps)
        {
            var row = new RowBag { IsNew = true, Order = rows.Count };
            row.Values.Set("rep_no", Val.Of(rows.Count + 1));
            row.Values.Set("rep_result", Val.Of(result));
            row.Values.Set("rep_meets_spec", Val.Bool(meets));
            rows.Add(row);
        }
        var res = await records.SaveAsync(snap, data, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException("Demo replicates failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }
}

/// <summary>LAB-004 / LAB-005: creates testing activities from the additional-testing rows, enforcing INV-045.</summary>
public static class LabTesting
{
    /// <summary>INV-045 for one request: re-test ≠ original analyst; re-sample = original analyst.</summary>
    public static string? Problem(string? kind, int? testAnalyst, int? originalAnalyst, Engine.Security.UserDirectory dir) => kind switch
    {
        "retest" when testAnalyst != null && testAnalyst == originalAnalyst =>
            $"A re-test must be done by a different analyst from the original test ({dir.Name(originalAnalyst)}) (INV-045).",
        "resample" when testAnalyst != null && originalAnalyst != null && testAnalyst != originalAnalyst =>
            $"A re-sample is tested by the original analyst ({dir.Name(originalAnalyst)}) (INV-045).",
        _ => null,
    };

    public static async Task<List<Record>> CreateTestsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var original = s.UserId("analyst");
        var pending = s.Rows("testing_requests").Where(r => r.CreatedRecordId == null).ToList();
        var created = new List<Record>();
        if (pending.Count == 0) return created;
        await Tx.RunAsync(ctx.Db, async () =>
        {
            var proposed = new RecordData();
            var rows = proposed.RowsOf("testing_requests");
            foreach (var row in s.Rows("testing_requests"))
            {
                var clone = row.Clone();
                if (row.CreatedRecordId == null)
                {
                    var kind = s.KeywordKey(row, "test_kind");
                    var analyst = kind == "resample" ? original : row.Values.Ref("test_analyst");
                    if (Problem(kind, analyst, original, ctx.Directory) is { } p) throw new WorkflowException(p);
                    var label = kind == "resample" ? "Re-sample" : "Re-test";
                    var res = await ctx.Records.CreateAsync(new CreateRequest
                    {
                        FormKey = "lab_test", ParentId = s.Record.Id, SkipAccessCheck = true, Assignees = [ctx.Me.IsSystem ? s.UserId("lab_supervisor") ?? s.Record.CreatedById : ctx.Me.Id],
                        Values = new()
                        {
                            ["subject"] = $"{label}: {s.Text("test_name")}, batch {s.Text("batch_number")}", ["test_kind"] = kind, ["original_analyst"] = original,
                            ["test_analyst"] = analyst, ["product"] = s.Text("product"), ["batch_number"] = s.Text("batch_number"),
                            ["test_name"] = s.Text("test_name"), ["specification"] = s.Text("specification"),
                            ["replicates"] = row.Values.Number("replicates") ?? 1m, ["test_reason"] = row.Values.Text("test_reason"),
                        },
                        CreateComment = $"Created from the additional testing of {s.Record.Number} (LAB-004)",
                    });
                    if (!res.Ok) throw new WorkflowException("Couldn't create the testing activity: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                    clone.CreatedRecordId = res.Record!.Id;
                    created.Add(res.Record);
                }
                rows.Add(clone);
            }
            var fresh = await ctx.Records.LoadRequiredAsync(s.Record.Id);
            var save = await ctx.Records.SaveAsync(fresh, proposed, new SaveOptions { System = true });
            if (!save.Ok) throw new WorkflowException(string.Join("; ", save.Errors.Select(e => e.Message)));
            return true;
        });
        return created;
    }
}

public sealed class LabInvestigationBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["lab_investigation"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        var labError = m.Bool("lab_error_identified");
        // LAB-003: original results are invalidated only with an identified, typed lab error.
        ctx.Set("results_invalidated", labError == true && ctx.KeywordKey("lab_error_type") != null);
        // INV-044: "No" switches to the full-investigation path.
        ctx.Set("full_investigation_required", labError switch { false => true, true => false, _ => null });
        if (labError != true && ctx.KeywordKey("lab_error_type") != null) ctx.Set("lab_error_type", null);

        // INV-045 on the additional-testing rows.
        var original = m.Ref("analyst");
        var dir = ctx.Services.GetRequiredService<Engine.Security.UserDirectory>();
        foreach (var row in ctx.Proposed.RowsOf("testing_requests").Where(r => r.CreatedRecordId == null))
        {
            var kind = ctx.KeywordKey(row, "test_kind");
            if (kind == "resample" && original != null) ctx.SetRow(row, "test_analyst", original);
            else if (LabTesting.Problem(kind, row.Values.Ref("test_analyst"), original, dir) is { } p) ctx.Error("test_analyst", $"Testing row {row.Order + 1}: {p}", row.Id);
        }
        return Task.CompletedTask;
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.Kind != RouteKind.Forward) return;
        if ((ctx.From.Key is "lab_investigation" or "full_investigation") && s.Rows("testing_requests").Any(r => r.CreatedRecordId == null))
            ctx.Error("Some additional-testing rows don't have testing activities yet. Use \"Create testing activities\" and complete them first (LAB-006).");
        if (ctx.To.Key == "final_approval")
        {
            if (string.IsNullOrWhiteSpace(s.Text("final_conclusion"))) ctx.Error("Write the final conclusion before Final Approval.");
            await QualityLinks.CheckRequiredGateAsync(ctx, "ncr_required", "ncr_justification", "ncr_record", "ncr", "NCR");
            await QualityLinks.CheckRequiredGateAsync(ctx, "capa_required", "capa_justification", "capa_record", "capa", "CAPA");
        }
        if (ctx.To.Key == "closed")
        {
            // INV-044: without a lab error, Closed is reachable only through a completed full investigation.
            if (s.Bool("lab_error_identified") != true)
            {
                var fi = s.Workflow!.Phases.First(p => p.Key == "full_investigation").Id;
                var through = await ctx.Db.PhaseEntries.AnyAsync(e => e.RecordId == s.Record.Id && e.PhaseId == fi && e.ExitedAt != null && e.ExitAction == "forward");
                if (!through || string.IsNullOrWhiteSpace(s.Text("fi_results")))
                    ctx.Error("No lab error was identified, so this investigation can close only after the full investigation is completed (INV-044).");
            }
            await QualityLinks.CheckRequiredGateAsync(ctx, "ncr_required", "ncr_justification", "ncr_record", "ncr", "NCR");
            await QualityLinks.CheckRequiredGateAsync(ctx, "capa_required", "capa_justification", "capa_record", "capa", "CAPA");
        }
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var list = new List<RecordAction>();
        if (!ctx.IsOpen() || !await ctx.CanActAsync()) return list;
        var pending = s.Rows("testing_requests").Count(r => r.CreatedRecordId == null);
        if (pending > 0 && s.PhaseKey is "lab_investigation" or "full_investigation")
            list.Add(new() { Key = "lab-tests", Label = $"Create testing activities ({pending})", Icon = "bi-arrow-repeat", Group = "Create",
                Description = "Turn the additional-testing rows into testing activities. Save your changes first." });
        if (s.Bool("ncr_required") == true && (await QualityLinks.LinkedAsync(ctx, "ncr_record", "ncr")).Count == 0)
            list.Add(new() { Key = "lab-ncr", Label = "Initiate NCR", Icon = "bi-exclamation-octagon", Group = "Create", Description = "Raise the NCR for the affected batch (QE-005)." });
        if (s.Bool("capa_required") == true && (await QualityLinks.LinkedAsync(ctx, "capa_record", "capa")).Count == 0)
            list.Add(new() { Key = "lab-capa", Label = "Initiate CAPA", Icon = "bi-shield-plus", Group = "Create", Description = "Raise the CAPA this investigation needs (QE-005)." });
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if ((await ActionsAsync(ctx)).All(a => a.Key != actionKey)) return ActionOutcome.Fail("This action isn't available.");
        var s = ctx.Snapshot;
        if (actionKey == "lab-tests")
        {
            try
            {
                var created = await LabTesting.CreateTestsAsync(ctx);
                return ActionOutcome.Ok($"Created {string.Join(", ", created.Select(c => c.Number))}.", created.Count == 1 ? created[0].Id : null);
            }
            catch (WorkflowException ex) { ctx.Db.ChangeTracker.Clear(); return ActionOutcome.Fail(ex.Message); }
        }
        var (rec, err) = actionKey switch
        {
            "lab-ncr" => await QualityLinks.CreateFromAsync(ctx, "ncr", new()
            {
                ["subject"] = $"{s.Keyword("result_type")?.Label}: {s.Text("test_name")} on {s.Text("product")} batch {s.Text("batch_number")}",
                ["nc_type"] = "finished_product", ["severity"] = "major", ["owner"] = ctx.Me.Id, ["source_record"] = s.Record.Id,
                ["part_number"] = s.Text("product"), ["lot_number"] = s.Text("batch_number"),
                ["description"] = $"{s.Text("test_name")}: {s.Text("original_result")} against {s.Text("specification")}. {s.Text("fi_results")}",
            }, "ncr_record"),
            "lab-capa" => await QualityLinks.CreateFromAsync(ctx, "capa", new()
            {
                ["subject"] = s.Text("subject"), ["capa_type"] = "corrective", ["source"] = "lab_investigation", ["priority"] = "high", ["owner"] = ctx.Me.Id,
                ["source_record"] = s.Record.Id, ["product"] = s.Text("product"), ["description"] = $"{s.Text("original_result")} vs {s.Text("specification")}. {s.Text("root_cause")}",
            }, "capa_record"),
            _ => ((Record?)null, (string?)"This action isn't available."),
        };
        return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"{rec.Number} created and linked to {s.Record.Number}.", rec.Id);
    }

    public override Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var model = new LabPathModel(s.Bool("lab_error_identified"), s.Bool("results_invalidated") == true, s.Keyword("lab_error_type")?.Label,
            s.Text("original_result"), s.Text("specification"), s.Keyword("result_type"), s.PhaseKey);
        return Task.FromResult<IEnumerable<RecordPanel>>([new RecordPanel { Key = "lab-path", Title = "Investigation path", Placement = "top", Partial = "~/Modules/Quality/Views/_LabPath.cshtml", Model = model }]);
    }
}

public sealed record LabPathModel(bool? LabError, bool Invalidated, string? ErrorType, string? Result, string? Spec, Keyword? ResultType, string PhaseKey);

public sealed class LabTestBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["lab_test"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        var kind = ctx.KeywordKey("test_kind");
        var original = m.Ref("original_analyst");
        if (kind == "resample" && original != null && m.Ref("test_analyst") != original) ctx.Set("test_analyst", original);
        else if (LabTesting.Problem(kind, m.Ref("test_analyst"), original, ctx.Services.GetRequiredService<Engine.Security.UserDirectory>()) is { } p)
            ctx.Error("test_analyst", p);

        // Outcome: meets specification only if every replicate does.
        var reps = ctx.Proposed.RowsOf("replicate_results");
        var answered = reps.Where(r => r.Values.Bool("rep_meets_spec") != null).ToList();
        ctx.Set("outcome", answered.Count == 0 ? null : answered.All(r => r.Values.Bool("rep_meets_spec") == true) ? "pass" : "fail");
        return Task.CompletedTask;
    }

    /// <summary>Inherits batch and analyst information from the investigation when created by hand (LAB-004).</summary>
    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        if (ctx.Record.ParentId is not { } pid || ctx.Snapshot.UserId("original_analyst") != null) return;
        var parent = await ctx.Records.LoadAsync(pid);
        if (parent == null || parent.Form.Key != "lab_investigation") return;
        var values = new Dictionary<string, object?>
        {
            ["original_analyst"] = parent.UserId("analyst"), ["product"] = parent.Text("product"), ["batch_number"] = parent.Text("batch_number"),
            ["test_name"] = parent.Text("test_name"), ["specification"] = parent.Text("specification"),
        };
        if (ctx.Snapshot.KeywordKey("test_kind") == "resample") values["test_analyst"] = parent.UserId("analyst");
        await ctx.Records.UpdateAsync(ctx.Record.Id, values, "Inherited from the lab investigation (LAB-004)");
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.Kind != RouteKind.Forward) return;
        // INV-045, checked against the investigation's own analyst (authoritative).
        if (s.Record.ParentId is { } pid && await ctx.Records.LoadAsync(pid) is { } parent)
            if (LabTesting.Problem(s.KeywordKey("test_kind"), s.UserId("test_analyst"), parent.UserId("analyst"), ctx.Directory) is { } p) ctx.Error(p);
        if (ctx.From.Key == "testing")
        {
            var expected = (int)(s.Number("replicates") ?? 0);
            var rows = s.Rows("replicate_results").Count;
            if (rows != expected) ctx.Error($"Record a result for each of the {expected} replicates ({rows} recorded).");
        }
    }
}
