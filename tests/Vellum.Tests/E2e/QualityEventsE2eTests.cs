using static Vellum.Tests.E2e.QualitySteps;

namespace Vellum.Tests.E2e;

/// <summary>
/// Nonconformance, planned deviations, quality events, lab investigations, customer returns, customers and feedback, and
/// change management end to end through the web pages, plus a sweep of the quality applications' views.
/// </summary>
[Collection("vellum")]
public class QualityEventsE2eTests(VellumFixture fx)
{
    private static async Task<Page> MrbVoteAsync(VellumFixture fx, string url, params string[] voters)
    {
        Page page = null!;
        foreach (var voter in voters)
        {
            using var b = await fx.SignedInAsync(voter);
            page = await (await b.GoAsync(url)).RouteOkAsync("Approve MRB disposition");
        }
        return page;
    }

    [Fact] // NC-001..003, NC-005, NC-007, INV-065
    public async Task An_ncr_is_contained_dispositioned_by_the_mrb_and_escalated_to_a_linked_8d_capa()
    {
        var tag = E2e.Tag();
        using var cwu = await fx.SignedInAsync("cwu");
        var ncr = await cwu.CreateAsync("ncr", f => f
            .Set("f.subject", $"Force sensor zero offset out of tolerance {tag}").Pick("f.nc_type", "in_process").Pick("f.severity", "major")
            .Set("f.owner", fx.Uid("cwu").ToString()).Set("f.description", "In-process test found sensors reading +0.6 N at zero load (limit ±0.2 N).")
            .Set("f.supplier_name", "SensaTek Components").Pick("f.supplier_fault", "no").Set("f.process_step", "Sensor calibration (OP-40)")
            .Set("f.part_number", $"FS-{tag}").Set("f.lot_number", "FS-0824").Set("f.quantity_affected", "50").Pick("f.defect_codes", "dimensional")
            .Set("f.defect_description", "Offset +0.45 to +0.62 N on 37 of 40 sampled.").Pick("f.material_hold", "yes").Set("f.hold_location", "Quarantine rack Q-2")
            .Set("f.containment_owner", fx.Uid("cwu").ToString()).Set("f.containment_actions", "All 50 sensors tagged and moved to Q-2."));
        var url = ncr.Path;
        var number = ncr.Number();
        Assert.StartsWith("NCR-", number);
        Assert.Equal("On hold", ncr.FieldText("hold_status"));
        Assert.Contains(number, (await cwu.GoAsync($"/apps/nonconformance?view=on_hold&q={tag}")).ViewNumbers());

        ncr = await ncr.RouteOkAsync("Submit to MRB");
        Assert.Equal("MRB Review", ncr.Phase());

        // The MRB agrees the disposition lines; each member signs and the NCR moves on at 75% (3 of 4).
        using var jlee = await fx.SignedInAsync("jlee");
        ncr = await (await jlee.GoAsync(url)).SaveOkAsync(f => f
            .Set("f.mrb_recommendation", "Rework what can be re-zeroed; scrap the rest.")
            .AddRow("dispositions", ("disposition_type", "rework"), ("disposition_qty", "30"), ("disposition_instructions", "Re-zero per WI-RW-07"))
            .AddRow("dispositions", ("disposition_type", "scrap"), ("disposition_qty", "20"), ("disposition_instructions", "Scrap per SOP-MAT-011")));
        // Saved quantities come back as typed, not as the stored decimal(38,10).
        Assert.Equal("30", ncr.Q("[name='r.dispositions.0.disposition_qty']")?.GetAttribute("value"));
        ncr = await ncr.RouteOkAsync("Approve MRB disposition");
        Assert.Equal("MRB Review", ncr.Phase());
        Assert.Equal("Approval recorded (1 of 3 needed).", ncr.Toast);

        using var sortiz = await fx.SignedInAsync("sortiz");
        var bad = await (await sortiz.GoAsync(url)).RouteAsync("Approve MRB disposition", password: "wrong-password");
        Assert.NotEmpty(bad.RouteErrors());
        ncr = await MrbVoteAsync(fx, url, "sortiz", "akim");
        Assert.Equal("Disposition", ncr.Phase());

        // INV-065: the lines must add up to the quantity affected before Disposition is left.
        ncr = await (await cwu.GoAsync(url)).SaveOkAsync(f => f.Set("r.dispositions.1.disposition_qty", "15"));
        var short5 = await ncr.RouteAsync("Send for final approval", assignees: [fx.Uid("sortiz")]);
        Assert.Contains(short5.RouteErrors(), e => e.Contains("add up to 45") && e.Contains("INV-065"));
        ncr = await (await cwu.GoAsync(url)).SaveOkAsync(f => f.Set("r.dispositions.1.disposition_qty", "20"));
        ncr = await ncr.RouteOkAsync("Send for final approval", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Final Approval", ncr.Phase());

        // NC-007: approving the disposition releases the material hold.
        ncr = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve disposition");
        Assert.Equal("Verification", ncr.Phase());
        Assert.Equal("Released", ncr.FieldText("hold_status"));

        // NC-005: escalate to an 8D CAPA, linked both ways.
        var capa = await (await cwu.GoAsync(url)).ActionOkAsync("ncr-capa");
        var capaNumber = capa.Number();
        Assert.StartsWith("CAPA-", capaNumber);
        Assert.Equal($"{capaNumber} created and linked to {number}.", capa.Toast);
        Assert.Equal("eight_d", capa.FieldValue("capa_type"));
        Assert.Equal("ncr", capa.FieldValue("source"));
        Assert.Contains(capa.Related(), r => r.Number == number && r.Text.Contains("Source"));
        ncr = await cwu.GoAsync(url);
        Assert.Contains(ncr.Related(), r => r.Number == capaNumber && r.Text.Contains("Created"));
        Assert.DoesNotContain("ncr-capa", ncr.Actions());

        ncr = await ncr.SaveOkAsync(f => f.Pick("f.disposition_verified", "yes").Set("f.verification_notes", "30 re-zeroed and retested; 20 scrapped (ticket ST-0912)."));
        ncr = await ncr.RouteOkAsync("Verified: complete NCR");
        Assert.Equal("Completed", ncr.Phase());
    }

    [Fact] // NC-004, DEV-001..004, INV-118, INV-119, SAF-044
    public async Task Use_as_is_needs_a_planned_deviation_that_is_approved_activated_and_later_expires()
    {
        var tag = E2e.Tag();
        var part = $"MF-{tag}";
        using var jlee = await fx.SignedInAsync("jlee");
        var ncr = await jlee.CreateAsync("ncr", f => f
            .Set("f.subject", $"Scratched mounting face {tag}").Pick("f.nc_type", "finished_product").Pick("f.severity", "minor")
            .Set("f.owner", fx.Uid("jlee").ToString()).Set("f.description", "Light scratches on the mounting face (zone C).")
            .Set("f.part_number", part).Set("f.lot_number", "M-2291").Set("f.quantity_affected", "10").Pick("f.defect_codes", "cosmetic"));
        var url = ncr.Path;
        ncr = await (await ncr.RouteOkAsync("Submit to MRB")).SaveOkAsync(f => f
            .Set("f.mrb_recommendation", "Zone C is covered by the housing: use as is under a planned deviation.")
            .AddRow("dispositions", ("disposition_type", "use_as_is"), ("disposition_qty", "10"), ("disposition_instructions", "Zone C only")));
        ncr = await MrbVoteAsync(fx, url, "jlee", "sortiz", "akim");
        Assert.Equal("Disposition", ncr.Phase());
        ncr = await jlee.GoAsync(url);
        ncr.ExpectText("Use As Is needs an active, approved planned deviation");

        // DEV-004: the deviation is created from the NCR with the part, defect and quantity filled in.
        var dev = await ncr.ActionOkAsync("ncr-deviation", f => f.Pick("a.defect", "cosmetic").Set("a.qty", "10"));
        var devUrl = dev.Path;
        var devNumber = dev.Number();
        Assert.StartsWith("DEV-", devNumber);
        Assert.Equal("Draft", dev.Phase());
        Assert.Equal(part, dev.FieldValue("part_number"));
        Assert.Equal("cosmetic", dev.FieldValue("defect_code"));

        // NC-004: the NCR can't complete while the deviation isn't active.
        using var sortiz = await fx.SignedInAsync("sortiz");
        ncr = await (await jlee.GoAsync(url)).RouteOkAsync("Send for final approval", assignees: [fx.Uid("sortiz")]);
        ncr = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve disposition");
        ncr = await (await jlee.GoAsync(url)).SaveOkAsync(f => f.Pick("f.disposition_verified", "yes").Set("f.verification_notes", "10 pcs issued under the deviation."));
        var early = await ncr.RouteAsync("Verified: complete NCR");
        Assert.Contains(early.RouteErrors(), e => e.Contains($"{devNumber} isn't active for part {part}"));

        // DEV-002: Draft → Review → Initial Approval → Final Approval → Active.
        var incomplete = await (await jlee.GoAsync(devUrl)).RouteAsync("Submit for review");
        Assert.Contains(incomplete.Errors, e => e.Contains("Technical reviewer is required"));
        dev = await (await jlee.GoAsync(devUrl)).RouteOkAsync("Submit for review", fill: f => f
            .Set("f.reviewer", fx.Uid("akim").ToString()).Set("f.requirement", "Drawing 40-1180 rev D note 4: no visible scratches").Pick("f.risk_level", "low"));
        Assert.Equal("Review", dev.Phase());
        using var akim = await fx.SignedInAsync("akim");
        dev = await (await akim.GoAsync(devUrl)).RouteOkAsync("Reviewed: send for approval", assignees: [fx.Uid("sortiz")], fill: f => f
            .Pick("f.fit_function_impact", "no").Pick("f.patient_safety_impact", "no").Set("f.risk_summary", "Cosmetic only; covered by the housing."));
        dev = await (await sortiz.GoAsync(devUrl)).RouteOkAsync("Approve", assignees: fx.Uids("sortiz", "dokafor").Select(int.Parse).ToArray());
        Assert.Equal("Final Approval", dev.Phase());

        // SAF-044: the final approver must be a different person from the initial approver.
        var same = await (await sortiz.GoAsync(devUrl)).RouteAsync("Approve and activate");
        Assert.Contains(same.RouteErrors(), e => e.Contains("different approver than initial approval"));
        using var dokafor = await fx.SignedInAsync("dokafor");
        dev = await (await dokafor.GoAsync(devUrl)).RouteOkAsync("Approve and activate");
        Assert.Equal("Active", dev.Phase());
        Assert.Contains($"Deviation {devNumber} is active", await jlee.NotificationsAsync());
        Assert.Contains(devNumber, (await jlee.GoAsync($"/apps/deviations?view=active&q={tag}")).ViewNumbers());

        // INV-118 satisfied: the NCR completes.
        ncr = await (await jlee.GoAsync(url)).RouteOkAsync("Verified: complete NCR");
        Assert.Equal("Completed", ncr.Phase());

        // DEV-002/003: using up the quantity expires the deviation on the next expiry run, and the owner is told.
        dev = await (await jlee.GoAsync(devUrl)).SaveOkAsync(f => f.Set("f.quantity_used", "10").Set("f.usage_notes", $"Lot M-2291: 10 pcs ({ncr.Number()})."));
        using var admin = await fx.SignedInAsync("admin");
        var task = await admin.GoAsync("/admin/tasks/quality-deviation-expiry");
        var ran = await task.Form("form[action*='handler=Run']").SubmitAsync();
        if (ran.Toast?.StartsWith("Planned deviation expiry succeeded") != true)
            throw new Xunit.Sdk.XunitException($"{ran.Toast}\n{(await admin.GoAsync("/admin/tasks/quality-deviation-expiry")).Text}");
        dev = await jlee.GoAsync(devUrl);
        Assert.Equal("Expired", dev.Phase());
        Assert.Equal("Quantity limit used up", dev.FieldText("closure_reason"));
        Assert.Contains($"Deviation {devNumber} has expired", await jlee.NotificationsAsync());
        Assert.DoesNotContain(devNumber, (await jlee.GoAsync($"/apps/deviations?view=active&q={tag}")).ViewNumbers());
    }

    [Fact] // QE-005, QE-101..103, QE-106, INV-041
    public async Task A_critical_quality_event_is_triaged_into_a_linked_capa_and_completed_on_track_and_trend()
    {
        var tag = E2e.Tag();
        using var nflores = await fx.SignedInAsync("nflores");
        Action<FormFill> details = f => f
            .Set("f.subject", $"Sterilizer load released before record review {tag}").Pick("f.event_type", "procedure").Pick("f.criticality", "critical")
            .Set("f.owner", fx.Uid("nflores").ToString()).Set("f.event_date", Today).Pick("f.site", "kalamazoo")
            .Set("f.description", "Load EO2-0918 was moved to finished goods before the cycle record was reviewed.")
            .Set("f.immediate_actions", "Remaining units placed on hold in FG-2.");

        // QE-103: a critical event needs its escalation list.
        var form = (await nflores.NewAsync("quality_event")).RecordForm();
        details(form);
        var refused = await form.SubmitAsync();
        Assert.Contains(refused.Errors, e => e.Contains("escalation notification list (QE-103)"));

        var qe = await nflores.CreateAsync("quality_event", f =>
        {
            details(f);
            f.Set("f.escalation_list", fx.Uids("dokafor", "rnovak")).Set("f.distribution_list", fx.Uid("jlee").ToString());
        });
        var url = qe.Path;
        var number = qe.Number();
        Assert.StartsWith("QE-", number);
        using var dokafor = await fx.SignedInAsync("dokafor");
        Assert.Contains($"Critical quality event {number}", await dokafor.NotificationsAsync());

        qe = await qe.RouteOkAsync("Submit for review", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Initial Review", qe.Phase());
        Assert.Contains($"Critical event {number} moved to Initial Review", await dokafor.NotificationsAsync());

        // Triage: Track & Trend with a CAPA. The gate waits for the CAPA (INV-041).
        using var sortiz = await fx.SignedInAsync("sortiz");
        qe = await (await sortiz.GoAsync(url)).SaveOkAsync(f => f
            .Pick("f.product_impact", "potential").Pick("f.patient_safety_impact", "no").Set("f.impact_summary", "Sterility of the held units not yet demonstrated.")
            .Pick("f.track_trend", "yes").Set("f.track_trend_justification", "Root cause is clear; the CAPA covers the systemic fix.").Pick("f.capa_required", "yes"));
        var gate = await qe.RouteAsync("Track & Trend: send to Final Approval", comment: "Track and trend", assignees: [fx.Uid("dokafor")]);
        Assert.Contains(gate.RouteErrors(), e => e.Contains("A CAPA is required") && e.Contains("INV-041"));

        var capa = await (await sortiz.GoAsync(url)).ActionOkAsync("qe-capa");
        var capaNumber = capa.Number();
        Assert.Equal("quality_event", capa.FieldValue("source"));
        Assert.Equal("critical", capa.FieldValue("priority"));
        Assert.Contains(capa.Related(), r => r.Number == number);

        qe = await sortiz.GoAsync(url);
        qe.ExpectText(capaNumber);
        Assert.DoesNotContain("qe-capa", qe.Actions());
        qe = await qe.RouteOkAsync("Track & Trend: send to Final Approval", comment: "Track and trend; CAPA raised.", assignees: [fx.Uid("dokafor")]);
        Assert.Equal("Final Approval", qe.Phase());
        qe = await (await dokafor.GoAsync(url)).RouteOkAsync("Approve and complete");
        Assert.Equal("Completed", qe.Phase());

        // QE-106: the distribution list hears about the completion.
        using var jlee = await fx.SignedInAsync("jlee");
        Assert.Contains($"Quality event {number} completed", await jlee.NotificationsAsync());
        Assert.Contains(number, (await jlee.GoAsync($"/apps/quality_events?view=track_trend&q={tag}")).ViewNumbers());
    }

    [Fact] // LAB-001..007, INV-044, INV-045
    public async Task An_oos_without_a_lab_error_needs_the_full_investigation_and_a_retest_by_another_analyst()
    {
        var tag = E2e.Tag();
        using var hmoreau = await fx.SignedInAsync("hmoreau");
        var lab = await hmoreau.CreateAsync("lab_investigation", f => f
            .Set("f.subject", $"EO residual above limit {tag}").Pick("f.result_type", "oos").Set("f.product", "ST-40").Set("f.batch_number", $"B-{tag}")
            .Set("f.test_name", "EO residual").Set("f.specification", "≤ 4.0 mg/device").Set("f.original_result", "5.8 mg/device")
            .Set("f.analyst", fx.Uid("hmoreau").ToString()).Set("f.lab_supervisor", fx.Uid("sortiz").ToString()));
        var url = lab.Path;
        Assert.StartsWith("LAB-", lab.Number());
        lab = await lab.RouteOkAsync("Start lab investigation");
        Assert.Equal("Lab Investigation", lab.Phase());

        // INV-045: a re-test can't go back to the original analyst.
        using var sortiz = await fx.SignedInAsync("sortiz");
        Action<FormFill> phase1 = f => f
            .Pick("f.chk_calculations", "yes").Pick("f.chk_method", "yes").Pick("f.chk_instrument", "yes").Pick("f.chk_standards", "yes")
            .Pick("f.chk_sample_prep", "yes").Pick("f.chk_analyst", "yes").Pick("f.additional_testing", "yes").Pick("f.lab_error_identified", "no")
            .Set("f.investigation_lead", fx.Uid("jlee").ToString()).Set("f.lab_summary", "No assignable laboratory error found.");
        var sameAnalyst = await (await sortiz.GoAsync(url)).SaveAsync(f =>
        {
            phase1(f);
            f.AddRow("testing_requests", ("test_kind", "retest"), ("test_analyst", fx.Uid("hmoreau").ToString()), ("replicates", "3"), ("test_reason", "Confirm the result"));
        });
        Assert.Contains(sameAnalyst.Errors, e => e.Contains("INV-045"));
        lab = await (await sortiz.GoAsync(url)).SaveOkAsync(f =>
        {
            phase1(f);
            f.AddRow("testing_requests", ("test_kind", "retest"), ("test_analyst", fx.Uid("jlee").ToString()), ("replicates", "3"), ("test_reason", "Confirm the result"));
        });
        // LAB-003, INV-044: no lab error means the result stands and the full investigation is required.
        Assert.Equal("No", lab.FieldText("results_invalidated"));
        Assert.Equal("Yes", lab.FieldText("full_investigation_required"));
        Assert.DoesNotContain("Lab error confirmed: send to Final Approval", lab.RouteOptions());

        // LAB-006: testing rows become testing activities, which must finish before the phase is left.
        var untested = await lab.RouteAsync("No lab error: request full investigation", assignees: [fx.Uid("dokafor")]);
        Assert.Contains(untested.RouteErrors(), e => e.Contains("don't have testing activities yet"));
        var test = await (await sortiz.GoAsync(url)).ActionOkAsync("lab-tests");
        Assert.StartsWith("TST-", test.Number());
        Assert.EndsWith(fx.Name("hmoreau"), test.FieldText("original_analyst"));
        Assert.Equal(fx.Uid("jlee").ToString(), test.Q("input[name='f.test_analyst']")?.GetAttribute("value"));
        var testUrl = test.Path;
        var blocked = await (await sortiz.GoAsync(url)).RouteAsync("No lab error: request full investigation", assignees: [fx.Uid("dokafor")]);
        Assert.Contains(blocked.RouteErrors(), e => e.Contains(test.Number()));

        // LAB-004: Initiate → QC Approval → Testing in Progress → Final Approval → Closed.
        test = await test.RouteOkAsync("Submit for QC approval", assignees: [fx.Uid("dokafor")]);
        using var dokafor = await fx.SignedInAsync("dokafor");
        test = await (await dokafor.GoAsync(testUrl)).RouteOkAsync("QC approve: start testing");
        Assert.Equal("Testing in Progress", test.Phase());
        using var jlee = await fx.SignedInAsync("jlee");
        test = await (await jlee.GoAsync(testUrl)).SaveOkAsync(f => f
            .AddRow("replicate_results", ("rep_no", "1"), ("rep_result", "5.9 mg"), ("rep_meets_spec", "no"))
            .AddRow("replicate_results", ("rep_no", "2"), ("rep_result", "6.1 mg"), ("rep_meets_spec", "no"))
            .Set("f.test_summary", "Re-test confirms the OOS result."));
        var two = await test.RouteAsync("Results complete", assignees: [fx.Uid("dokafor")]);
        Assert.Contains(two.RouteErrors(), e => e.Contains("each of the 3 replicates (2 recorded)"));
        test = await (await jlee.GoAsync(testUrl)).SaveOkAsync(f => f.AddRow("replicate_results", ("rep_no", "3"), ("rep_result", "5.7 mg"), ("rep_meets_spec", "no")));
        Assert.Equal("Fails specification", test.FieldText("outcome"));
        test = await test.RouteOkAsync("Results complete", assignees: [fx.Uid("dokafor")]);
        test = await (await dokafor.GoAsync(testUrl)).RouteOkAsync("Approve results");
        Assert.Equal("Closed", test.Phase());

        // LAB-002: Lab Investigation Approval (e-signed) → Full Investigation.
        lab = await (await sortiz.GoAsync(url)).RouteOkAsync("No lab error: request full investigation", assignees: [fx.Uid("dokafor")]);
        Assert.Equal("Lab Investigation Approval", lab.Phase());
        lab = await (await dokafor.GoAsync(url)).RouteOkAsync("Approve full investigation");
        Assert.Equal("Full Investigation", lab.Phase());

        // Full investigation by its lead, with an NCR for the batch (QE-005) and no CAPA (justified).
        lab = await (await jlee.GoAsync(url)).SaveOkAsync(f => f
            .Set("f.fi_reason", "OOS confirmed by re-test.").Pick("f.fi_batch_record", "yes").Pick("f.fi_deviations", "yes").Pick("f.fi_equipment", "yes")
            .Pick("f.fi_materials", "yes").Pick("f.fi_environment", "yes").Set("f.fi_results", "Aeration time shortened after a controller change.")
            .Pick("f.batch_disposition", "reject").Pick("f.root_cause_category", "method").Set("f.root_cause", "Aeration cycle shortened to 8 h.")
            .Set("f.final_conclusion", "Batch rejected; aeration restored to 12 h.").Pick("f.capa_required", "no")
            .Set("f.capa_justification", "Controller change is reverted under the change process.").Pick("f.ncr_required", "yes"));
        var noNcr = await lab.RouteAsync("Send to Final Approval", assignees: [fx.Uid("sortiz")]);
        Assert.Contains(noNcr.RouteErrors(), e => e.Contains("An NCR is required") || e.Contains("A NCR is required"));
        var ncr = await (await jlee.GoAsync(url)).ActionOkAsync("lab-ncr");
        Assert.StartsWith("NCR-", ncr.Number());
        lab = await (await jlee.GoAsync(url)).RouteOkAsync("Send to Final Approval", assignees: [fx.Uid("sortiz")]);
        lab = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve and close");
        Assert.Equal("Closed", lab.Phase());
        Assert.Equal("No", lab.FieldText("results_invalidated"));
    }

    [Fact] // RMA-001..005, INV-066
    public async Task A_customer_return_is_authorized_received_over_authorization_with_a_justification_and_verified()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var rma = await jlee.CreateAsync("rma", f => f
            .Set("f.subject", $"Cracked administration sets {tag}").Set("f.owner", fx.Uid("jlee").ToString()).Pick("f.rma_reason", "defective")
            .Set("f.customer_contact", "Chris Dalton").Set("f.description", "Four sets with cracked female luers.")
            .AddRow("rma_parts", ("part_number", $"AS-{tag}"), ("part_description", "Administration set"), ("qty_authorized", "4"), ("unit_value", "18.50"))
            .AddRow("rma_parts", ("part_number", $"TS-{tag}"), ("part_description", "Tubing set"), ("qty_authorized", "2"), ("unit_value", "7.25")));
        var url = rma.Path;
        var number = rma.Number();
        Assert.StartsWith("RMA-", number);
        Assert.Contains("88.50", rma.FieldText("authorized_total"));
        Assert.Equal("6", rma.FieldText("authorized_units"));

        rma = await rma.RouteOkAsync("Submit for authorization", assignees: [fx.Uid("sortiz")]);
        using var sortiz = await fx.SignedInAsync("sortiz");
        rma = await (await sortiz.GoAsync(url)).RouteOkAsync("Authorize return");
        Assert.Equal("Receiving Inspection", rma.Phase());

        // RMA-003: the parts list is copied into Receiving.
        rma = await jlee.GoAsync(url);
        Assert.Equal($"AS-{tag}", rma.Q("[name='r.rma_receipts.0.rcv_part_number']")?.GetAttribute("value"));
        Assert.Equal($"TS-{tag}", rma.Q("[name='r.rma_receipts.1.rcv_part_number']")?.GetAttribute("value"));

        // INV-066: more than authorized needs an override with a justification.
        Action<FormFill> receive = f => f.Set("r.rma_receipts.0.rcv_qty_received", "5").Set("r.rma_receipts.1.rcv_qty_received", "2")
            .Set("f.received_on", Today).Pick("f.inspection_result", "confirmed").Set("f.inspection_notes", "Cracks at the luer root on all units.");
        var over = await rma.SaveAsync(receive);
        Assert.Contains(over.Errors, e => e.Contains("INV-066"));
        var noReason = await (await jlee.GoAsync(url)).SaveAsync(f => { receive(f); f.Pick("f.receiving_override", "yes"); });
        Assert.Contains(noReason.Errors, e => e.Contains("INV-066"));
        rma = await (await jlee.GoAsync(url)).SaveOkAsync(f =>
        {
            receive(f);
            f.Pick("f.receiving_override", "yes").Set("f.override_justification", "The customer sent a fifth unit from the same lot.");
        });

        // RMA-005: an NCR for the returned product, linked to the RMA.
        var ncr = await rma.ActionOkAsync("rma-ncr");
        Assert.Equal("customer_return", ncr.FieldValue("nc_type"));
        Assert.Equal("7", ncr.FieldValue("quantity_affected"));
        rma = await (await jlee.GoAsync(url)).RouteOkAsync("Inspection complete");
        Assert.Equal("Disposition", rma.Phase());
        rma = await rma.RouteOkAsync("Send to verification", fill: f => f
            .Pick("f.rma_disposition", "replace").Pick("f.accounting_disposition", "credit").Set("f.credit_amount", "92.50").Set("f.disposition_notes", "Replace and credit."));
        Assert.Equal("Verification", rma.Phase());
        rma = await rma.RouteOkAsync("Verify and complete", fill: f => f.Pick("f.resolved", "yes").Set("f.verification_notes", "Replacements received by the customer."));
        Assert.Equal("Completed", rma.Phase());
        Assert.Contains(number, (await jlee.GoAsync($"/apps/rma?view=overrides&q={tag}")).ViewNumbers());
        Assert.Contains(ncr.Number(), rma.Text);
    }

    [Fact] // CFB-001, CFB-002, RMA-005
    public async Task Feedback_is_recorded_from_a_customer_profile_with_its_contacts_and_shows_in_its_history()
    {
        var tag = E2e.Tag();
        var name = $"Lakeside Surgery Center {tag}";
        using var jlee = await fx.SignedInAsync("jlee");
        var customer = await jlee.CreateAsync("customer", f => f
            .Set("f.customer_name", name).Set("f.account_number", $"C-{tag}").Pick("f.customer_type", "clinic").Pick("f.customer_status", "active")
            .Set("f.account_manager", fx.Uid("rnovak").ToString()).Set("f.city", "Chicago").Set("f.country", "USA")
            .AddRow("contacts", ("contact_name", "Ana Ruiz"), ("contact_title", "Biomed lead"), ("contact_email", "a.ruiz@lakeside.example"), ("contact_primary", "yes"))
            .AddRow("contacts", ("contact_name", "Ben Ortiz"), ("contact_title", "OR manager")));
        var customerUrl = customer.Path;
        Assert.StartsWith("CUS-", customer.Number());

        // Customer names are unique.
        var duplicate = await (await jlee.NewAsync("customer")).RecordForm().Set("f.customer_name", name).Pick("f.customer_type", "clinic").SubmitAsync();
        Assert.NotEmpty(duplicate.Errors);
        Assert.StartsWith("/records/new", duplicate.Path);

        // CFB-002: feedback comes from the profile, with the primary contact filled in; contacts are limited to the profile's.
        var fb = await customer.ActionOkAsync("cus-feedback");
        var fbUrl = fb.Path;
        Assert.StartsWith("FB-", fb.Number());
        Assert.Equal("Investigation", fb.Phase());
        Assert.Equal("Ana Ruiz", fb.FieldValue("contact"));
        var stranger = await fb.SaveAsync(f => f.Set("f.contact", "Zed Nobody"));
        Assert.Contains(stranger.Errors, e => e.Contains("Choose one of the customer's contacts") && e.Contains("Ana Ruiz") && e.Contains("Ben Ortiz"));

        fb = await (await jlee.GoAsync(fbUrl)).SaveOkAsync(f => f
            .Set("f.subject", $"Alarm volume hard to hear in recovery bays {tag}").Pick("f.feedback_type", "concern").Set("f.product", "VX-200")
            .Set("f.description", "Maximum alarm volume is hard to hear with curtains closed.").Set("f.investigation", "Measured 62 dB at 3 m.")
            .Set("f.action_taken", "Offered the remote alarm accessory.").Set("f.response", "We'll fit the remote alarm accessory at no charge."));
        fb = await fb.ActionOkAsync("fb-notify");
        Assert.Equal("Email queued to Ana Ruiz.", fb.Toast);
        var rma = await (await jlee.GoAsync(fbUrl)).ActionOkAsync("fb-rma");
        Assert.StartsWith("RMA-", rma.Number());

        fb = await (await jlee.GoAsync(fbUrl)).RouteOkAsync("Send for approval", assignees: [fx.Uid("sortiz")]);
        using var sortiz = await fx.SignedInAsync("sortiz");
        fb = await (await sortiz.GoAsync(fbUrl)).RouteOkAsync("Approve");
        Assert.Equal("Completed", fb.Phase());

        // CFB-001: the profile's feedback history lists the feedback and the return.
        customer = await jlee.GoAsync(customerUrl);
        customer.ExpectText("Feedback history").ExpectText(fb.Number()).ExpectText(rma.Number());
    }

    [Fact] // CHG-002, CHG-021..024, CHG-030, CHG-040, CHG-050, CHG-051, INV-147..149, INV-160, INV-161
    public async Task A_change_with_impact_assessments_a_new_document_and_a_pas_filing_goes_from_draft_to_completed()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var template = (await jlee.FindInViewAsync("change", "templates", "Specification change"))["/records/".Length..];
        var sop = await DocumentE2eTests.NewSopAsync(fx, jlee, $"Incoming inspection of force sensors {tag}");
        var sopUrl = sop.Path;

        // INV-147: the template's approvals, impact assessments and action plan are copied into the change.
        var change = await jlee.CreateAsync("change", f => f
            .Set("f.change_template", template).Set("f.subject", $"Tighten force-sensor offset limit {tag}").Set("f.owner", fx.Uid("jlee").ToString())
            .Set("f.planned_date", Day(30)).Pick("f.reason_code", "capa").Set("f.products", "VX-200 occlusion sensor")
            .Set("f.current_state", "Offset limit ±0.5 N.").Set("f.proposed_change", "Offset limit ±0.2 N with 100% incoming test.")
            .Set("f.justification", "Occlusion alarm complaints traced to sensor offset.")
            .Pick("f.verification_required", "yes").Set("f.verifier", fx.Uid("tbrooks").ToString())
            .Pick("f.reg_impact", "substantial").Set("f.agency", "FDA CDRH").Set("f.submission_number", $"P-{tag}").Set("f.submission_date", Day(-10)));
        var url = change.Path;
        var number = change.Number();
        Assert.StartsWith("CHG-", number);
        Assert.Equal("Specification change", change.FieldText("change_type"));
        Assert.Equal("pas", change.FieldValue("filing")); // CHG-050: the filing follows the impact
        Assert.Equal(2, change.All("[name^='r.approvals.'][name$='.__id']").Count);
        Assert.Equal(2, change.All("[name^='r.impacts.'][name$='.__id']").Count);
        Assert.Equal(2, change.All("[name^='r.plan.'][name$='.__id']").Count);

        change = await change.SaveOkAsync(f => f.AddRow("documents", ("doc_action", "new"), ("document", sop.RecordId.ToString()), ("doc_ref", sop.Number()),
            ("reviser", fx.Uid("jlee").ToString())));
        change = await change.ActionOkAsync("assign_ia");
        Assert.StartsWith("Assigned IA-", change.Toast);
        var assessments = change.Activities().Where(a => a.Number.StartsWith("IA-")).ToList();
        Assert.Equal(2, assessments.Count);

        change = await (await change.RouteOkAsync("Submit for review")).RouteOkAsync("Send for approval");
        Assert.Equal("Approval", change.Phase());

        // CHG-030: each assessor completes their impact assessment (e-signed).
        using var cwu = await fx.SignedInAsync("cwu");
        foreach (var (b, area) in new[] { (jlee, "Quality"), (cwu, "Supply chain") })
        {
            var ia = await b.GoAsync(assessments.Single(a => a.Text.Contains(area)).Href);
            ia = await ia.SaveOkAsync(f => f.Pick("f.impact", "low").Set("f.assessment", $"{area}: incoming inspection plan and supplier spec updated."));
            Assert.Equal("Completed", (await ia.RouteOkAsync("Complete assessment")).Phase());
        }

        // INV-148: one approval per approval type; the other quality approver is released.
        using var sortiz = await fx.SignedInAsync("sortiz");
        change = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve");
        Assert.Equal("Approval", change.Phase());
        using var dokafor = await fx.SignedInAsync("dokafor");
        Assert.Empty((await dokafor.GoAsync(url)).RouteOptions());
        using var akim = await fx.SignedInAsync("akim");
        change = await (await akim.GoAsync(url)).RouteOkAsync("Approve");
        Assert.Equal("Implementation", change.Phase());

        // CHG-040, INV-149: the action plan became action items, which must be done before Verification.
        change = await jlee.GoAsync(url);
        var items = change.Activities().Where(a => a.Number.StartsWith("AI-")).ToList();
        Assert.Equal(2, items.Count);
        var open = await change.RouteAsync("Send to verification");
        Assert.Contains(open.RouteErrors(), e => e.StartsWith("Complete the open action items first"));
        foreach (var item in items)
        {
            var ai = await (await jlee.GoAsync(item.Href)).SaveOkAsync(f => f.Set("f.action_taken", "Done and filed with the change."));
            Assert.Equal("Completed", (await ai.RouteOkAsync("Complete")).Phase());
        }
        change = await (await jlee.GoAsync(url)).RouteOkAsync("Send to verification");
        Assert.Equal("Verification", change.Phase());

        // INV-149, INV-161, INV-160: evidence, the agency approval and the new document must all be in place.
        using var tbrooks = await fx.SignedInAsync("tbrooks");
        var blocked = await (await tbrooks.GoAsync(url)).RouteAsync("Verify and complete");
        Assert.Contains(blocked.RouteErrors(), e => e.Contains("objective evidence"));
        Assert.Contains(blocked.RouteErrors(), e => e.Contains("approval date is recorded (CHG-051)"));
        Assert.Contains(blocked.RouteErrors(), e => e.Contains("approval letter"));
        Assert.Contains(blocked.RouteErrors(), e => e.Contains(sop.Number()) && e.Contains("INV-160"));
        change = await (await tbrooks.GoAsync(url)).SaveOkAsync(f => f
            .Set("f.verification_evidence", "Incoming inspection of 3 sensor lots used the ±0.2 N limit.").Set("f.approval_date", Day(-1))
            .Attach("file.approval_evidence", "fda-pas-approval.pdf", E2e.Pdf("FDA PAS approval"), "application/pdf"));
        var docOnly = await change.RouteAsync("Verify and complete");
        var docOnlyError = Assert.Single(docOnly.RouteErrors());
        Assert.Contains(sop.Number(), docOnlyError);
        Assert.Contains("new document not yet effective (INV-160)", docOnlyError);

        await ReleaseSopAsync(fx, sopUrl);
        change = await (await tbrooks.GoAsync(url)).RouteOkAsync("Verify and complete");
        Assert.Equal("Completed", change.Phase());
        Assert.Contains(number, (await jlee.GoAsync($"/apps/change?view=completed&q={tag}")).ViewNumbers());
    }

    /// <summary>Document Control: the drafted SOP from Submit to Document Control through Release (as in DocumentE2eTests).</summary>
    private static async Task ReleaseSopAsync(VellumFixture fx, string url)
    {
        foreach (var (user, route) in new[]
        {
            ("jlee", "Submit to Document Control"), ("pshah", "Format checked: send for initial approval"), ("sortiz", "Approve: send to reviewers"),
            ("dokafor", "Reviewed"), ("tbrooks", "Reviewed"), ("sortiz", "Approve for release"), ("pshah", "Release"),
        })
        {
            using var b = await fx.SignedInAsync(user);
            await (await b.GoAsync(url)).RouteOkAsync(route);
        }
    }

    [Theory] // PLT-1103, CAPA-020, CMP-012, QE-107, AER-013
    [InlineData("capa")]
    [InlineData("actions")]
    [InlineData("nonconformance")]
    [InlineData("quality_events")]
    [InlineData("deviations")]
    [InlineData("customers")]
    [InlineData("complaints")]
    [InlineData("adverse_events")]
    [InlineData("lab_investigations")]
    [InlineData("rma")]
    [InlineData("change")]
    public async Task Every_view_of_a_quality_application_renders_filters_and_exports(string app)
    {
        using var sortiz = await fx.SignedInAsync("sortiz");
        var home = await sortiz.GoAsync($"/apps/{app}");
        var views = home.All($"a[href^='/apps/{app}?view=']").Select(a => a.GetAttribute("href")!).Distinct().ToList();
        Assert.NotEmpty(views);
        foreach (var view in views)
        {
            var page = await sortiz.GoAsync(view);
            var key = view.Split("view=")[1].Split('&')[0];
            // The first quick filter, with its first choice, and the search box.
            if (page.Q("form.vl-toolbar select[name^='f.']") is { } select && select.QuerySelectorAll("option").Skip(1).FirstOrDefault()?.GetAttribute("value") is { } choice)
                await sortiz.GoAsync($"/apps/{app}?view={key}&{select.GetAttribute("name")}={Uri.EscapeDataString(choice)}");
            await sortiz.GoAsync($"/apps/{app}?view={key}&q=zz-no-such-record");
            var csv = await sortiz.RawGetAsync($"/apps/{app}/export.csv?view={key}");
            Assert.Equal(System.Net.HttpStatusCode.OK, csv.StatusCode);
            Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        }
    }
}
