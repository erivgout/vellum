using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Tests.E2e;

/// <summary>
/// Audits, inspections, the risk register and FMEA end to end through the web pages: an audit program from plan to approved
/// report with findings, CAPA and recommendation; an inspection lot sample by sample; a risk scored on the heat map,
/// mitigated and accepted with an e-signature; and an FMEA worksheet with RPN thresholds.
/// </summary>
[Collection("vellum")]
public class AuditRiskE2eTests(VellumFixture fx)
{
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Day(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    private string Name(string username) => fx.Services.GetRequiredService<UserDirectory>().Get(username)!.DisplayName;

    [Fact] // AUD-010..015, AUD-022, AUD-025, AUD-028, AUD-032, INV-067
    public async Task An_audit_program_runs_from_plan_to_an_approved_report_with_findings_a_capa_and_a_recommendation()
    {
        var tag = E2e.Tag();
        var auditee = $"Final assembly {tag}";
        using var tb = await fx.SignedInAsync("tbrooks");

        // Setup: a checklist template and an audit type that uses it.
        var checklist = await tb.CreateAsync("checklist_template", f => f
            .Set("f.name", $"Production controls {tag}").Set("f.standard", "ISO 13485:2016")
            .AddRow("questions", ("question", "Are work instructions at the station current?"), ("clause", $"§4.2.4-{tag}"), ("out_of", "5"), ("required", "yes"))
            .AddRow("questions", ("question", "Are torque drivers in calibration?"), ("clause", $"§7.6-{tag}"), ("out_of", "5"), ("required", "yes"))
            .AddRow("questions", ("question", "Is line clearance recorded?"), ("clause", $"§7.5.1-{tag}"), ("out_of", "5"), ("required", "yes")));
        var type = await tb.CreateAsync("audit_type", f => f
            .Set("f.name", $"Process audit {tag}").Pick("f.evaluation", "scoring").Set("f.frequency_months", "12")
            .Set("f.checklists", checklist.RecordId.ToString()));

        // The audit program for the year, approved by Quality with a signature.
        var start = BusinessCalendar.Today.AddDays(3);
        var plan = await tb.CreateAsync("audit_plan", (page, f) => f
            .Set("f.name", $"Internal audit program {tag}").Pick("f.program", "quality")
            .Set("f.period_start", Iso(BusinessCalendar.Today.AddDays(-5))).Set("f.period_end", Iso(BusinessCalendar.Today.AddDays(300)))
            .Set("f.lead_auditor", fx.Uid("tbrooks").ToString()).Set("f.scope", "Production and process controls.")
            .AddRow("planned_audits", ("auditee", auditee), ("entity_kind", page.RowOption("planned_audits", "entity_kind", "process")),
                ("audit_type", type.RecordId.ToString()), ("planned_start", Iso(start)), ("planned_end", Iso(start.AddDays(1))),
                ("planned_lead", fx.Uid("tbrooks").ToString())));
        var planUrl = plan.Path;
        (await tb.GoAsync("/audits/program")).ExpectText(auditee).ExpectText("Planned " + Day(start));
        plan = await plan.RouteOkAsync("Submit for approval", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Approval", plan.Phase());

        using var sortiz = await fx.SignedInAsync("sortiz");
        var wrong = await (await sortiz.GoAsync(planUrl)).RouteAsync("Approve plan", password: "wrong-password");
        Assert.NotEmpty(wrong.RouteErrors());
        plan = await (await sortiz.GoAsync(planUrl)).RouteOkAsync("Approve plan");
        Assert.Equal("Active", plan.Phase());

        // The lead auditor schedules the planned audit (AUD-012): it opens in Scheduled with the checklist copied in.
        plan = await (await tb.GoAsync(planUrl)).ActionAsync("schedule");
        var auditNumber = Regex.Match(plan.Toast ?? "", @"AUD-\d+").Value;
        Assert.False(string.IsNullOrEmpty(auditNumber), plan.Toast);
        Assert.Contains($"for {auditee}, assigned to", plan.Toast);
        Assert.Equal("Every planned audit is already scheduled.", (await tb.GoAsync(planUrl)).DisabledAction("Schedule audit"));
        var audit = await plan.ClickAsync(auditNumber);
        var auditUrl = audit.Path;
        Assert.Equal("Scheduled", audit.Phase());
        Assert.Equal("Are work instructions at the station current?", audit.RecordForm().Values("r.checklist.0.question").Single());
        Assert.Equal(3, audit.All("[data-subform='checklist'] tbody tr[data-row]").Count);
        (await tb.GoAsync("/work")).ExpectText(auditNumber);
        var program = await tb.GoAsync("/audits/program?status=Scheduled");
        Assert.Contains(auditNumber, program.All("tr").Single(r => r.TextContent.Contains(auditee)).TextContent);

        // A score above "out of" is refused (INV-067).
        var bad = await audit.SaveAsync(f => f.Pick("r.checklist.0.classification", "conforming").Set("r.checklist.0.score", "6"));
        Assert.Contains(bad.Errors, e => e.Contains("can't be more than the question's \"out of\" value (5)"));

        // The checklist answered: one conforming, one minor nonconformance, one observation.
        audit = await (await tb.GoAsync(auditUrl)).SaveAsync(f => f
            .Pick("r.checklist.0.classification", "conforming").Set("r.checklist.0.score", "5")
            .Pick("r.checklist.1.classification", "minor_nc").Set("r.checklist.1.score", "2")
            .Set("r.checklist.1.comments", "Torque driver TD-014 at station 3 overdue for calibration since August.")
            .Pick("r.checklist.2.classification", "observation").Set("r.checklist.2.score", "4")
            .Set("r.checklist.2.comments", "Clearance recorded but not signed by the second operator."));
        Assert.Equal("Saved.", audit.Toast);
        Assert.Equal("11", audit.FieldText("total_score"));
        Assert.Equal("73.3", audit.FieldText("score_pct"));
        Assert.Equal("Marginal", audit.FieldText("suggested_result"));

        // The report can't be submitted without the conclusions (AUD-028).
        var incomplete = await audit.RouteAsync("Submit report for approval", assignees: [fx.Uid("sortiz")]);
        Assert.Contains(incomplete.Errors.Concat(incomplete.RouteErrors()), e => e.Contains("Audit result is required"));

        // Follow-ups from the findings (AUD-025): a CAPA for the nonconformance, a recommendation for the observation.
        audit = await (await tb.GoAsync(auditUrl)).ActionAsync("raise_capa", f => f.Set("a.owner", fx.Uid("lgarcia").ToString()).Pick("a.priority", "high"));
        var capaNumber = Regex.Match(audit.Toast ?? "", @"CAPA-\d+").Value;
        Assert.Contains("raised and linked to the finding", audit.Toast);
        Assert.Equal("No minor or major nonconformances without a CAPA.", audit.DisabledAction("Raise CAPA"));
        audit = await audit.ActionAsync("raise_rec", f => f.Pick("a.row", "Q3").Set("a.assignee", fx.Uid("jlee").ToString()));
        var recNumber = Regex.Match(audit.Toast ?? "", @"REC-\d+").Value;
        Assert.Contains("assigned to", audit.Toast);
        audit.ExpectText(capaNumber).ExpectText(recNumber); // the Findings tab

        // The CAPA starts with the auditor who raised it, pre-filled from the finding, owned by the process owner and linked back.
        var capa = await (await tb.GoAsync("/work")).ClickAsync(capaNumber);
        Assert.Equal("Initiate", capa.Phase());
        capa.ExpectText("Torque driver TD-014 at station 3").ExpectText(auditNumber);
        Assert.Equal(fx.Uid("lgarcia").ToString(), capa.RecordForm().Values("f.owner").Single());

        // The findings report across audits (AUD-032), filtered by classification and searched by clause.
        var findings = await tb.GoAsync($"/audits/findings?q={tag}");
        findings.ExpectText(auditee).ExpectText(capaNumber).ExpectText(recNumber).ExpectText($"§7.6-{tag}");
        var minors = await tb.GoAsync($"/audits/findings?c=minor_nc&q={tag}");
        minors.ExpectText("Torque driver TD-014").ExpectNoText("not signed by the second operator");

        // Report submitted and approved with a signature.
        audit = await (await tb.GoAsync(auditUrl)).SaveAsync(f => f.Pick("f.result", "marginal").Pick("f.frequency_rec", "increase")
            .Set("f.conclusions", "Calibration control at the line needs attention; otherwise effective."));
        audit = await audit.RouteOkAsync("Submit report for approval", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Report Approval", audit.Phase());
        audit = await (await sortiz.GoAsync(auditUrl)).RouteOkAsync("Approve report");
        Assert.Equal("Completed", audit.Phase());

        // The program now shows the result and the next audit, brought forward by "Increase frequency" (AUD-015).
        program = await tb.GoAsync("/audits/program");
        var row = Page.Squash(program.All("tr").Single(r => r.TextContent.Contains(auditee)).TextContent);
        Assert.Contains(auditNumber, row);
        Assert.Contains("Marginal", row);
        Assert.Contains("Increase frequency", row);
        Assert.Contains(Day(start.AddMonths(6)), row);

        // The recommendation is closed by its assignee.
        using var jlee = await fx.SignedInAsync("jlee");
        var rec = await (await jlee.GoAsync("/work")).ClickAsync(recNumber);
        rec = await rec.SaveAsync(f => f.Set("f.action_taken", "Second-operator signature added to the clearance form.")
            .Set("f.completed_on", Iso(BusinessCalendar.Today)));
        rec = await rec.RouteOkAsync("Complete");
        Assert.Equal("Completed", rec.Phase());
        (await tb.GoAsync($"/audits/findings?q={tag}")).ExpectText(recNumber).ExpectText("Completed");
    }

    [Fact] // CHG-061: change timeliness for change records the user can read
    public async Task The_change_timeliness_page_shows_open_changes_by_type_department_and_reason()
    {
        var tag = E2e.Tag();
        using var sortiz = await fx.SignedInAsync("sortiz");
        var template = await sortiz.CreateAsync("change_template", (page, f) => f
            .Set("f.name", $"Line change {tag}")
            .AddRow("template_approvals", ("approval_type", page.RowOption("template_approvals", "approval_type", "quality")))
            .Set("r.template_approvals.0.approvers", fx.Uids("sortiz", "dokafor")));
        var change = await sortiz.CreateAsync("change", f => f
            .Set("f.change_template", template.RecordId.ToString()).Set("f.subject", $"Move final pack to line 4 {tag}")
            .Set("f.planned_date", Iso(BusinessCalendar.Today.AddDays(30))).Pick("f.impact_level", "medium").Pick("f.reason_code", "capacity")
            .Set("f.current_state", "Final pack on line 2.").Set("f.proposed_change", "Final pack on line 4.").Set("f.justification", "Line 2 capacity."));
        Assert.Equal($"Line change {tag}", change.FieldText("change_type"));

        var byType = await sortiz.GoAsync("/change/timeliness");
        var row = byType.All("tr").Single(r => r.QuerySelector("td")?.TextContent.Trim() == $"Line change {tag}"); // the aging table
        Assert.Equal("1", Page.Squash(row.QuerySelectorAll("td")[1].TextContent)); // 0–30 days
        Assert.Contains(byType.All("tr[data-href]"), r => r.TextContent.Contains(change.Number())); // the oldest open changes
        (await sortiz.GoAsync("/change/timeliness?by=department")).ExpectText("Quality");
        (await sortiz.GoAsync("/change/timeliness?by=reason")).ExpectText("Capacity / second source");
        var bogus = await sortiz.GoAsync("/change/timeliness?by=nonsense"); // falls back to the change type
        Assert.Contains(bogus.All("tr"), r => r.QuerySelector("td")?.TextContent.Trim() == $"Line change {tag}");
    }

    [Fact] // INS-001..018, INV-068/069, INV-166..169: an inspection lot from criteria to a reviewed, failed record
    public async Task An_inspection_lot_is_sampled_point_by_point_and_a_failed_sample_is_reviewed()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");

        // Criteria: a numeric range (limits must be in order, INV-068) and a keyword list.
        var badLimits = await (await jlee.NewAsync("criteria_profile")).RecordForm()
            .Set("f.name", $"Tube OD {tag}").Pick("f.data_type", "numeric").Pick("f.numeric_type", "range").Set("f.unit", "mm")
            .Set("f.lsl", "5.10").Set("f.lcl", "4.95").Set("f.target", "5.00").Set("f.ucl", "5.05").Set("f.usl", "4.90").SubmitAsync();
        Assert.NotEmpty(badLimits.Errors);
        var od = await jlee.CreateAsync("criteria_profile", f => f
            .Set("f.name", $"Tube OD {tag}").Pick("f.data_type", "numeric").Pick("f.numeric_type", "range").Set("f.unit", "mm")
            .Set("f.lsl", "4.90").Set("f.lcl", "4.95").Set("f.target", "5.00").Set("f.ucl", "5.05").Set("f.usl", "5.10"));
        var visual = await jlee.CreateAsync("criteria_profile", (page, f) => f
            .Set("f.name", $"Surface {tag}").Pick("f.data_type", "keyword")
            .AddRow("keywords", ("keyword", "Clean"), ("kw_result", page.RowOption("keywords", "kw_result", "pass")))
            .AddRow("keywords", ("keyword", "Scratched"), ("kw_result", page.RowOption("keywords", "kw_result", "warn")))
            .AddRow("keywords", ("keyword", "Cracked"), ("kw_result", page.RowOption("keywords", "kw_result", "fail"))));
        var point = await jlee.CreateAsync("inspection_point", (page, f) => f
            .Set("f.name", $"Extrusion final check {tag}").Pick("f.sampling", "quantity").Set("f.sample_qty", "2").Set("f.per_qty", "500")
            .Pick("f.review_for", "failures")
            .AddRow("criteria", ("profile", od.RecordId.ToString()), ("reference", "DWG-310"), ("class", page.RowOption("criteria", "class", "major")))
            .AddRow("criteria", ("profile", visual.RecordId.ToString()), ("class", page.RowOption("criteria", "class", "minor"))));
        var plan = await jlee.CreateAsync("inspection_plan", f => f
            .Set("f.name", $"Tubing lot release {tag}").Set("f.product", "PVC tubing 5 mm")
            .AddRow("points", ("point", point.RecordId.ToString())));
        var planUrl = plan.Path;

        // Records only for Active plans (INV-166).
        var early = await (await jlee.NewAsync("inspection_record")).RecordForm()
            .Set("f.plan", plan.RecordId.ToString()).Set("f.lot_number", "L-" + tag).Set("f.lot_qty", "500").SubmitAsync();
        Assert.Contains(early.Errors, e => e.Contains("INV-166"));

        plan = await plan.RouteOkAsync("Submit for approval", assignees: [fx.Uid("sortiz")]);
        using var sortiz = await fx.SignedInAsync("sortiz");
        plan = await (await sortiz.GoAsync(planUrl)).RouteOkAsync("Approve plan");
        Assert.Equal("Active", plan.Phase());

        var record = await jlee.CreateAsync("inspection_record", f => f
            .Set("f.plan", plan.RecordId.ToString()).Set("f.lot_number", "L-" + tag).Set("f.lot_qty", "500"));
        var recordUrl = record.Path;
        Assert.Equal("Pending", record.FieldText("result"));
        Assert.Equal("2", record.FieldText("remaining_samples")); // 2 per 500

        // Sample 1 passes and closes straight away.
        var s1 = await record.ActionAsync("create_sample");
        Assert.Contains("Sample SMP-", s1.Toast);
        Assert.Equal($"Tube OD {tag}", s1.RowText("measurements", 0, "criterion"));
        s1 = await s1.SaveAsync(f => f.Set("r.measurements.0.measured", "5.02").Set("r.measurements.1.observed", "Clean"));
        Assert.Equal("Pass", s1.FieldText("sample_result"));
        s1 = await s1.RouteOkAsync("Close sample");
        Assert.Equal("Closed", s1.Phase());

        // Sample 2 is over the upper spec limit on a major criterion: it fails and must be reviewed (INV-167, INV-169).
        var s2 = await (await jlee.GoAsync(recordUrl)).ActionAsync("create_sample");
        var unknown = await s2.SaveAsync(f => f.Set("r.measurements.0.measured", "5.12").Set("r.measurements.1.observed", "Dented"));
        Assert.Contains(unknown.Errors, e => e.Contains("\"Dented\" isn't one of Clean, Scratched, Cracked"));
        s2 = await (await jlee.GoAsync(s2.Path)).SaveAsync(f => f.Set("r.measurements.0.measured", "5.12").Set("r.measurements.1.observed", "Scratched")
            .Set("r.measurements.0.remarks", "Die wear suspected."));
        Assert.Equal("Fail", s2.FieldText("sample_result"));
        Assert.Equal("Fail", s2.RowText("measurements", 0, "result"));
        Assert.Equal("Warn", s2.RowText("measurements", 1, "result"));
        Assert.DoesNotContain("Close sample", s2.RouteOptions());
        var s2Url = s2.Path;
        s2 = await s2.RouteOkAsync("Send to review", assignees: [fx.Uid("sortiz")]);

        // The reviewer raises a follow-up action and accepts the sample with a signature.
        var review = await (await sortiz.GoAsync(s2Url)).ActionAsync("follow_up", f => f.Pick("a.kind", "action_item")
            .Set("a.subject", "Replace extrusion die " + tag).Set("a.owner", fx.Uid("akim").ToString()));
        Assert.Contains("Action item AI-", review.Toast);
        review = await review.RouteOkAsync("Accept and close");
        Assert.Equal("Closed", review.Phase());

        // No samples remain: the record result is Fail (INV-168), so it goes to review rather than closing.
        record = await jlee.GoAsync(recordUrl);
        Assert.Equal("Fail", record.FieldText("result"));
        Assert.Equal("0", record.FieldText("remaining_samples"));
        Assert.Equal("1", record.FieldText("failed_samples"));
        Assert.DoesNotContain("Close record", record.RouteOptions());
        record = await record.RouteOkAsync("Send to review", assignees: [fx.Uid("sortiz")]);
        record = await (await sortiz.GoAsync(recordUrl)).RouteOkAsync("Accept and close");
        Assert.Equal("Closed", record.Phase());
        (await jlee.GoAsync(planUrl)).ExpectText(record.Number()); // the plan's inspection records card
    }

    [Fact] // RSK-009, RSK-014, RSK-015, INV-176, SAF-045: score, heat map, mitigation, residual risk and an e-signed acceptance
    public async Task A_critical_risk_is_plotted_mitigated_and_accepted_with_a_signature_before_it_completes()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var risk = await jlee.CreateAsync("risk_assessment", f => f
            .Set("f.subject", $"Occlusion alarm fails to sound {tag}").Pick("f.risk_category", "product_safety").Pick("f.risk_source", "design_review")
            .Set("f.product", "VX-200 infusion pump").Set("f.hazard", "Under-infusion goes unnoticed when the occlusion alarm doesn't sound.")
            .Set("f.harm", "Delayed therapy."));
        var url = risk.Path;
        var number = risk.Number();
        risk = await risk.RouteOkAsync("Submit for assessment");
        Assert.Equal("Assessment", risk.Phase());

        // Scored critical on the 5 × 5 matrix: the banner says it's unacceptable.
        risk = await risk.SaveAsync(f => f.Pick("f.severity", "s4").Pick("f.likelihood", "l4").Set("f.rationale", "Field data: 3 reports in 18 months."));
        Assert.Equal("16", risk.FieldText("current_score"));
        Assert.Equal("Critical", risk.FieldText("current_level"));
        risk.ExpectText("Unacceptable risk: Critical (score 16)");
        Assert.Equal("Only Quality approvers have risk acceptance authority (SAF-045).", risk.DisabledAction("Accept risk"));

        // The heat map cell shows it, and its link lists exactly that cell.
        var map = await jlee.GoAsync("/risk/heatmap");
        var cell = map.One("a.rk-link[href*='s=4&l=4']");
        Assert.Matches(@"Severity 4 × likelihood 4 = 16 \(Critical\): [1-9]\d* open risks?", cell.GetAttribute("aria-label")!);
        var filtered = await jlee.GoAsync(cell.GetAttribute("href")!);
        filtered.ExpectText("Severity 4 (Critical) × likelihood 4 (Probable)").ExpectText(number);
        Assert.All(filtered.All("#risks tbody tr"), r => Assert.Contains("S4 × L4", r.TextContent));
        (await jlee.GoAsync("/risk/heatmap?s=2&l=2")).ExpectNoText(number);
        (await jlee.GoAsync("/risk/heatmap?level=critical")).ExpectText(number);

        // Mitigation needs a control measure; its owner gets an action item.
        var none = await risk.RouteAsync("Start mitigation");
        Assert.Contains(none.RouteErrors(), e => e.Contains("Add at least one mitigation"));
        var measure = $"Add redundant alarm buzzer {tag}";
        risk = await (await jlee.GoAsync(url)).SaveAsync(f => f
            .AddRow("mitigations", ("measure", measure), ("mit_owner", fx.Uid("akim").ToString()), ("days", "15")));
        risk = await risk.RouteOkAsync("Start mitigation");
        Assert.Equal("Mitigation", risk.Phase());

        var blocked = await risk.RouteAsync("Send to review", assignees: [fx.Uid("sortiz")]);
        Assert.NotEmpty(blocked.RouteErrors().Concat(blocked.Errors)); // open action item, residual risk not rated
        using var akim = await fx.SignedInAsync("akim");
        await akim.CompleteActionItemAsync(measure, "Second buzzer added in design change DC-881; verified on 30 units.");

        // Residual risk is still High: unacceptable until accepted.
        risk = await (await jlee.GoAsync(url)).SaveAsync(f => f.Pick("f.residual_severity", "s4").Pick("f.residual_likelihood", "l3")
            .Set("f.verification", "Alarm test protocol TP-221 passed on 30 units."));
        Assert.Equal("12", risk.FieldText("current_score"));
        risk.ExpectText("Unacceptable risk: High (score 12)").ExpectText("The residual risk is still unacceptable");

        // A Quality approver accepts it with a signature; a wrong password changes nothing.
        using var sortiz = await fx.SignedInAsync("sortiz");
        var wrong = await (await sortiz.GoAsync(url)).ActionAsync("accept_risk", f => f.Set("a.rationale", "Benefit outweighs residual risk."), password: "not-it");
        Assert.NotEmpty(wrong.Errors);
        (await sortiz.GoAsync(url)).ExpectText("Unacceptable risk: High (score 12)");
        var accepted = await (await sortiz.GoAsync(url)).ActionAsync("accept_risk",
            f => f.Set("a.rationale", "Further reduction isn't practicable; therapy benefit outweighs the residual risk."), password: TestHelpers.Password);
        Assert.Contains("Risk accepted at score 12", accepted.Toast);
        accepted.ExpectText($"Risk accepted by {Name("sortiz")} on {Day(BusinessCalendar.Today)} at score 12 (High)");

        // Changing the score afterwards clears the acceptance (SAF-045).
        risk = await (await jlee.GoAsync(url)).SaveAsync(f => f.Pick("f.residual_likelihood", "l4"));
        risk.ExpectText("Unacceptable risk: Critical (score 16)").ExpectNoText("Risk accepted by");
        risk = await risk.SaveAsync(f => f.Pick("f.residual_likelihood", "l3"));
        risk.ExpectText("Unacceptable risk: High (score 12)");
        risk = await risk.RouteOkAsync("Send to review", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Review", risk.Phase());

        // Review can't complete an unaccepted unacceptable risk (INV-176).
        var review = await (await sortiz.GoAsync(url)).SaveAsync(f => f.Set("f.review_date", Iso(BusinessCalendar.Today.AddYears(1)))
            .Set("f.review_notes", "Residual risk reviewed with design and clinical."));
        var refused = await review.RouteAsync("Approve and complete");
        Assert.Contains(refused.RouteErrors(), e => e.Contains("can be completed only after Quality accepts it with an e-signature"));
        review = await (await sortiz.GoAsync(url)).ActionAsync("accept_risk", f => f.Set("a.rationale", "Accepted at review."), password: TestHelpers.Password);
        review = await review.RouteOkAsync("Approve and complete");
        Assert.Equal("Completed", review.Phase());

        // Completed risks leave the heat map of open risks.
        (await jlee.GoAsync("/risk/heatmap?level=high")).ExpectNoText(number);
        (await jlee.GoAsync("/apps/risk?view=register")).ExpectText(number);
    }

    [Fact] // FMEA-006/007, INV-178/179: RPNs calculated, thresholds force an action or a justification before completion
    public async Task An_fmea_flags_rows_over_the_thresholds_until_each_has_an_action_or_a_justification()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var fmea = await jlee.CreateAsync("fmea", f => f
            .Set("f.subject", $"Bag sealing PFMEA {tag}").Pick("f.fmea_type", "process").Set("f.product", "IV bag 500 ml")
            .Set("f.team", fx.Uids("jlee", "akim"))
            .AddRow("worksheet", ("item_function", "Seal bag"), ("failure_mode", "Incomplete seal"), ("effect", "Leak: patient exposure"),
                ("severity", "9"), ("cause", "Low seal temperature"), ("occurrence", "3"), ("controls", "Daily peel test"), ("detection", "4"))
            .AddRow("worksheet", ("item_function", "Print lot"), ("failure_mode", "Smudged print"), ("effect", "Cosmetic"),
                ("severity", "3"), ("cause", "Ink viscosity"), ("occurrence", "4"), ("detection", "5"))
            .AddRow("worksheet", ("item_function", "Fit cap"), ("failure_mode", "Loose cap"), ("effect", "Leak in transit"),
                ("severity", "6"), ("cause", "Torque drift"), ("occurrence", "5"), ("detection", "4"))
            .Set("r.worksheet.0.rpn", "1")); // a calculated column can't be typed in (INV-178)
        var url = fmea.Path;
        Assert.Equal("108", fmea.RowText("worksheet", 0, "rpn"));
        Assert.Equal("Severity + RPN", fmea.RowText("worksheet", 0, "attention"));
        Assert.Equal("60", fmea.RowText("worksheet", 1, "rpn"));
        Assert.Equal("–", fmea.RowText("worksheet", 1, "attention"));
        Assert.Equal("RPN ≥ 100", fmea.RowText("worksheet", 2, "attention"));
        Assert.Equal("120", fmea.FieldText("max_rpn"));
        Assert.Equal("2", fmea.FieldText("open_flags"));

        fmea = await fmea.RouteOkAsync("Assign for analysis");
        Assert.Equal("Assigned", fmea.Phase());
        var blocked = await fmea.RouteAsync("Complete FMEA");
        var errors = blocked.RouteErrors().Concat(blocked.Errors).ToList();
        Assert.Contains(errors, e => e.Contains("Worksheet row 1 (Incomplete seal)") && e.Contains("INV-179"));
        Assert.Contains(errors, e => e.Contains("Worksheet row 3 (Loose cap): RPN 120 ≥ 100"));
        Assert.DoesNotContain(errors, e => e.Contains("row 2"));

        // A partial revised rating is refused; a recommended action with a responsible person, and a justification, clear the flags.
        var partial = await (await jlee.GoAsync(url)).SaveAsync(f => f.Set("r.worksheet.0.rev_severity", "9"));
        Assert.Contains(partial.Errors, e => e.Contains("rate all of S′, O′ and D′"));
        fmea = await (await jlee.GoAsync(url)).SaveAsync(f => f
            .Set("r.worksheet.0.recommended_action", "Add seal temperature interlock " + tag).Set("r.worksheet.0.responsible", fx.Uid("akim").ToString())
            .Set("r.worksheet.2.justification", "Cap torque is 100% verified downstream; transit leak is detectable."));
        Assert.Equal("0", fmea.FieldText("open_flags"));

        fmea = await fmea.ActionAsync("create_actions");
        Assert.Contains("Created 1 action item", fmea.Toast);
        using var akim = await fx.SignedInAsync("akim");
        (await akim.GoAsync("/work")).ExpectText("Add seal temperature interlock " + tag);

        // Re-rated after the action: RPN′ and the reduction are calculated.
        fmea = await (await jlee.GoAsync(url)).SaveAsync(f => f
            .Set("r.worksheet.0.action_taken", "Interlock installed and validated.")
            .Set("r.worksheet.0.rev_severity", "9").Set("r.worksheet.0.rev_occurrence", "1").Set("r.worksheet.0.rev_detection", "3")
            .Set("f.conclusion", "Seal process acceptable with the interlock; cap torque covered downstream."));
        Assert.Equal("27", fmea.RowText("worksheet", 0, "rev_rpn"));
        Assert.Equal("75", fmea.RowText("worksheet", 0, "reduction").TrimEnd('0').TrimEnd('.'));
        fmea = await fmea.RouteOkAsync("Complete FMEA");
        Assert.Equal("Completed", fmea.Phase());
    }
}
