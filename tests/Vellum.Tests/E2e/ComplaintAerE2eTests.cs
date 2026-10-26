using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Data;
using Vellum.Engine.Records;
using Vellum.Modules.AdverseEvents;
using static Vellum.Tests.E2e.QualitySteps;

namespace Vellum.Tests.E2e;

/// <summary>
/// Complaint Handling (§12.7) and Adverse Event Reporting (§12.8) end to end through the web pages: intake, investigation,
/// the MDR decision and its reporting clock, the MedWatch 3500A with eMDR validation, simulated gateway submission with
/// ACK1–ACK3, a supplemental report, and closure of the complaint.
/// </summary>
[Collection("vellum")]
public class ComplaintAerE2eTests(VellumFixture fx)
{
    private static Task<Page> NewComplaintAsync(VellumFixture fx, Browser b, string subject, string lot, bool injury, string site = AerSite) =>
        b.CreateAsync("complaint", f =>
        {
            f.Set("f.subject", subject).Pick("f.category", injury ? "adverse_event" : "product_quality").Set("f.owner", fx.Uid("jlee").ToString())
                .Set("f.awareness_date", Day(-2)).Pick("f.site_reported", site).Pick("f.channel", "phone")
                .Set("f.description", "The pump didn't alarm on a downstream occlusion; the infusion stopped for about 40 minutes.")
                .Set("f.product", "VX-200 infusion pump").Set("f.model", "VX-200-US").Set("f.lot_number", lot).Set("f.serial_number", $"SN-{lot}")
                .Pick("f.failure_mode", "occlusion_alarm").Pick("f.device_returned", "yes")
                .Set("f.reporter_name", "Tom Becker").Pick("f.reporter_type", "hcp").Set("f.reporter_organization", "St. Mary's Regional").Set("f.reporter_country", "USA");
            if (injury)
                f.Pick("f.patient_involved", "yes").Set("f.patient_age", "60").Pick("f.patient_sex", "female")
                    .Pick("f.injury", "yes").Pick("f.death", "no").Pick("f.medical_intervention", "yes").Set("f.patient_outcome", "Extra lab draw and a dose adjustment.");
            else
                f.Pick("f.patient_involved", "no");
        });

    [Fact] // CMP-002..005, CMP-009, SAF-020, SAF-022, AER-003..011, INV-046..048
    public async Task A_reportable_complaint_is_reported_to_the_fda_acknowledged_followed_up_and_closed()
    {
        await fx.EnsureAerSetupAsync();
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var complaint = await NewComplaintAsync(fx, jlee, $"VX-200 no occlusion alarm, patient harmed {tag}", $"L-{tag}", injury: true);
        var url = complaint.Path;
        var number = complaint.Number();
        Assert.StartsWith("CMP-", number);
        Assert.Equal("Draft", complaint.Phase());
        complaint.ExpectText("Regulatory reporting clock").ExpectText("Provisional until the decision tree is completed");

        // SAF-020: an injury alerts regulatory affairs straight away.
        using var rnovak = await fx.SignedInAsync("rnovak");
        Assert.Contains($"Potentially reportable complaint {number}", await rnovak.NotificationsAsync());

        complaint = await complaint.RouteOkAsync("Start investigation");
        Assert.Equal("Investigation", complaint.Phase());
        var tree = Assert.Single(complaint.Activities(), a => a.Number.StartsWith("MDR-"));

        // INV-046: the complaint can't leave Investigation before its MDR decision tree is completed.
        var early = await complaint.RouteAsync("Send to review", assignees: [fx.Uid("sortiz")]);
        Assert.Contains(early.Errors, e => e.Contains(tree.Number));
        Assert.Contains(early.Errors, e => e.Contains("Investigation summary is required"));

        // CMP-003: the decision tree works out the conclusion from the answers.
        var mdr = await (await jlee.GoAsync(tree.Href)).SaveOkAsync(f => f
            .Pick("f.q_death", "no").Pick("f.q_serious_injury", "yes").Pick("f.q_remedial", "no")
            .Set("f.mdr_rationale", "Interrupted anticoagulant therapy needed intervention to prevent impairment: serious injury (21 CFR 803.3)."));
        Assert.Equal("Yes", mdr.FieldText("mdr_reportable"));
        Assert.Equal("30-day report", mdr.FieldText("mdr_report_type"));
        mdr = await mdr.RouteOkAsync("Send for regulatory approval");
        Assert.Equal("Regulatory Approval", mdr.Phase());
        mdr = await (await rnovak.GoAsync(tree.Href)).RouteOkAsync("Approve decision");
        Assert.Equal("Completed", mdr.Phase());

        // CMP-009: the 30-day FDA clock runs from the awareness date and shows on the complaint.
        var due = BusinessCalendar.Today.AddDays(28);
        complaint = await rnovak.GoAsync(url);
        Assert.Equal("Reportable: 30-day", complaint.FieldText("mdr_decision"));
        Assert.Equal("Clock running", complaint.FieldText("clock_status"));
        complaint.ExpectText($"Due {due:ddd dd MMM yyyy}").ExpectText("28d left");
        Assert.Contains($"Reportable event: {number}", await rnovak.NotificationsAsync());

        // CMP-005: Create MedWatch from the complaint carries the mapped data into Adverse Event Reporting.
        var create = complaint.Actions().Single(a => a.StartsWith("aer-create-"));
        var mw = await complaint.ActionOkAsync(create);
        var mwUrl = mw.Path;
        var mwNumber = mw.Number();
        Assert.StartsWith("MW-", mwNumber);
        Assert.Equal("Draft", mw.Phase());
        Assert.StartsWith($"MedWatch {mwNumber} created", mw.Toast);
        mw.ExpectText("Assigned on submission").ExpectText($"Due {due:ddd dd MMM yyyy}");
        Assert.Contains(mw.All(".vla-links a"), a => a.TextContent.Trim() == number);
        Assert.Equal(AerSite, mw.FieldValue("location"));
        Assert.Equal(fx.Uid("sortiz").ToString(), Assert.Single(mw.All("input[name='f.approvers']")).GetAttribute("value"));
        Assert.Contains("block sending for approval", mw.One(".vla-validation").TextContent);

        // The FDA 3500A print layout of the draft.
        var print = await rnovak.GoAsync($"/quality/aer/{mw.RecordId}/3500a");
        print.ExpectText("St. Mary's Regional").ExpectText("VX-200 infusion pump");

        // AER-006: eMDR validation blocks the route with field-level errors…
        var invalid = await mw.RouteAsync("Send for approval");
        Assert.Contains(invalid.Errors, e => e.Contains("(AER-R"));
        Assert.Equal("Draft", (await rnovak.GoAsync(mwUrl)).Phase());

        // …which are fixed on the report.
        mw = await (await rnovak.GoAsync(mwUrl)).SaveOkAsync(f => f
            .Set("f.event_date", Day(-3)).Set("f.product_code", "FRN").Pick("f.device_operator", "hcp").Pick("f.usage", "reuse")
            .Pick("f.evaluated", "yes").Set("f.evaluation_summary", "Returned pump reproduced a 95 s alarm latency; force sensor offset found."));
        Assert.Contains("eMDR validation passed", mw.One(".vla-validation").TextContent);
        mw = await mw.RouteOkAsync("Send for approval");
        Assert.Equal("Approval", mw.Phase());
        mw.ExpectText("0 of 1 MedWatch approver approved");

        // Not before every approver has approved.
        var unapproved = await mw.RouteAsync("Submit to FDA");
        Assert.Contains(unapproved.RouteErrors(), e => e.Contains("No MedWatch approver has approved this report yet"));

        using var sortiz = await fx.SignedInAsync("sortiz");
        var approval = Assert.Single((await rnovak.GoAsync(mwUrl)).Activities(), a => a.Number.StartsWith("MWA-"));
        (await sortiz.GoAsync("/work")).ExpectText(approval.Number);
        Assert.Equal("Approved", (await (await sortiz.GoAsync(approval.Href)).RouteOkAsync("Approve report")).Phase());

        // AER-005, AER-008: submission assigns the next report number and sends the file through the gateway.
        mw = await (await rnovak.GoAsync(mwUrl)).RouteOkAsync("Submit to FDA");
        Assert.Equal("Submitted to FDA", mw.Phase());
        var reportNumber = mw.FieldText("report_number");
        Assert.Matches($@"^{AerCode}-{BusinessCalendar.Today.Year}-\d{{5}}$", reportNumber);
        Assert.Equal("Awaiting acknowledgements", mw.FieldText("submission_status"));
        Assert.Contains(mw.Actions(), a => a == "aer-poll");

        var board = await rnovak.GoAsync("/quality/aer");
        var row = board.All("tbody tr").Single(tr => tr.TextContent.Contains(mwNumber));
        Assert.Contains(reportNumber, row.TextContent);
        Assert.Contains("ACK1 not received", Page.Squash(row.TextContent));

        // The simulated FDA answers about 1, 2 and 3 minutes after sending: let four minutes pass, then check.
        using (var scope = fx.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<VellumDbContext>().Set<EmdrSubmission>()
                .Where(s => s.MedWatchId == mw.RecordId && !s.Superseded)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SentAt, x => x.SentAt!.Value.AddMinutes(-4)));
        mw = await mw.ActionOkAsync("aer-poll");
        Assert.Contains("ACK1 accepted", mw.Toast);
        Assert.Contains("ACK3 accepted", mw.Toast);
        Assert.Equal("Accepted by FDA", mw.FieldText("submission_status"));
        mw.ExpectText("The FDA accepted the report");
        Assert.Equal(3, mw.Activities().Count(a => a.Number.StartsWith("ACK-")));
        row = (await rnovak.GoAsync("/quality/aer?show=all")).All("tbody tr").Single(tr => tr.TextContent.Contains(mwNumber));
        Assert.Contains("ACK3 received", Page.Squash(row.TextContent));

        // CMP-009: the ACK2 receipt stops the complaint's FDA clock.
        complaint = await jlee.GoAsync(url);
        Assert.Equal("Submitted", complaint.FieldText("clock_status"));
        Assert.Contains(reportNumber, complaint.FieldText("report_reference"));
        complaint.ExpectText(mwNumber);

        // INV-048: completion needs the final acknowledgement confirmed.
        var unconfirmed = await (await rnovak.GoAsync(mwUrl)).RouteAsync("Complete: final acknowledgement confirmed");
        Assert.Contains(unconfirmed.RouteErrors(), e => e.Contains("Confirm final acknowledgement") && e.Contains("INV-048"));
        mw = await (await rnovak.GoAsync(mwUrl)).SaveOkAsync(f => f.Pick("f.confirm_final_ack", "yes").Set("f.completion_notes", "ACK3 loaded without errors."));
        mw = await mw.RouteOkAsync("Complete: final acknowledgement confirmed");
        Assert.Equal("Completed", mw.Phase());
        Assert.Contains(mwNumber, (await rnovak.GoAsync("/apps/adverse_events?view=completed")).ViewNumbers());

        // AER-011: a supplemental report keeps the report number and counts the follow-up.
        var sup = await mw.ActionOkAsync("aer-supplemental", f => f.Pick("a.followup", "device_evaluation").Set("a.note", "Root cause confirmed: force sensor lot FS-0824."));
        Assert.NotEqual(mwUrl, sup.Path);
        Assert.Equal(reportNumber, sup.FieldText("report_number"));
        Assert.Equal("supplemental", sup.FieldValue("report_type"));
        Assert.Equal("1", sup.FieldText("followup_number"));
        sup.ExpectText("Carried from the original report");
        Assert.Contains(sup.Number(), (await rnovak.GoAsync("/apps/adverse_events?view=supplementals")).ViewNumbers());

        // SAF-022 is satisfied by the submission, so the complaint can now be approved and closed.
        complaint = await (await jlee.GoAsync(url)).SaveOkAsync(f => f
            .Set("f.investigation_summary", "Returned pump reproduced a 95 s alarm latency.").Pick("f.conclusion", "confirmed")
            .Pick("f.capa_required", "no").Set("f.capa_justification", "Covered by the existing 8D on force sensor lot FS-0824.")
            .Pick("f.ncr_required", "no").Set("f.ncr_justification", "Covered by the force sensor NCR.")
            .Set("f.response_text", "We confirmed the problem and are replacing the pump.").Set("f.response_sent_on", Today));
        complaint = await complaint.RouteOkAsync("Send to review", assignees: [fx.Uid("sortiz")]);
        complaint = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve: awaiting closure");
        Assert.Equal("Awaiting Closure", complaint.Phase());
        complaint = await (await jlee.GoAsync(url)).RouteOkAsync("Close complaint");
        Assert.Equal("Closed", complaint.Phase());
        Assert.Contains(number, (await jlee.GoAsync($"/apps/complaints?view=closed&q={tag}")).ViewNumbers());
    }

    [Fact] // CMP-003, CMP-006, CMP-009, QE-005, INV-041
    public async Task A_non_reportable_decision_needs_its_rationale_and_no_clock_runs()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var complaint = await NewComplaintAsync(fx, jlee, $"Backlight flicker at low brightness {tag}", $"D-{tag}", injury: false, site: "boston");
        var url = complaint.Path;
        complaint = await complaint.RouteOkAsync("Start investigation");
        var tree = Assert.Single(complaint.Activities(), a => a.Number.StartsWith("MDR-"));

        var mdr = await (await jlee.GoAsync(tree.Href)).SaveOkAsync(f => f.Pick("f.q_death", "no").Pick("f.q_serious_injury", "no").Pick("f.q_malfunction", "no"));
        Assert.Equal("No", mdr.FieldText("mdr_reportable"));
        Assert.Equal("Not reportable", mdr.FieldText("mdr_report_type"));
        Assert.DoesNotContain(mdr.Actions(), a => a.StartsWith("aer-create-"));
        var noRationale = await mdr.RouteAsync("Send for regulatory approval");
        Assert.Contains(noRationale.Errors, e => e.Contains("Rationale is required"));
        mdr = await (await jlee.GoAsync(tree.Href)).RouteOkAsync("Send for regulatory approval",
            fill: f => f.Set("f.mdr_rationale", "Cosmetic display malfunction; infusion unaffected, recurrence can't cause serious injury."));
        using var rnovak = await fx.SignedInAsync("rnovak");
        Assert.Equal("Completed", (await (await rnovak.GoAsync(tree.Href)).RouteOkAsync("Approve decision")).Phase());

        complaint = await rnovak.GoAsync(url);
        Assert.Equal("Not reportable", complaint.FieldText("mdr_decision"));
        Assert.Equal("No report due", complaint.FieldText("clock_status"));
        complaint.ExpectText("No regulatory report is due");
        Assert.DoesNotContain(complaint.Actions(), a => a.StartsWith("aer-create-"));

        // INV-041: "no CAPA" needs a justification before approval.
        complaint = await (await jlee.GoAsync(url)).SaveOkAsync(f => f
            .Set("f.investigation_summary", "Reproduced at 5% PWM duty cycle; firmware 4.3 fixes it.").Pick("f.conclusion", "confirmed")
            .Pick("f.capa_required", "no").Pick("f.ncr_required", "no").Set("f.ncr_justification", "Within display specification.")
            .Set("f.response_text", "Firmware 4.3 fixes the flicker.").Set("f.response_sent_on", Today));
        complaint = await complaint.RouteOkAsync("Send to review", assignees: [fx.Uid("sortiz")]);
        using var sortiz = await fx.SignedInAsync("sortiz");
        var refused = await (await sortiz.GoAsync(url)).RouteAsync("Approve: awaiting closure");
        Assert.Contains(refused.RouteErrors(), e => e.Contains("Explain why no CAPA is required"));
        Assert.Equal("Review", (await sortiz.GoAsync(url)).Phase());
        complaint = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve: awaiting closure",
            fill: f => f.Set("f.capa_justification", "Already addressed by firmware 4.3 (change CC-0398)."));
        Assert.Equal("Awaiting Closure", complaint.Phase());

        // CMP-006: in Awaiting Closure only the diligence log can still change.
        complaint = await jlee.GoAsync(url);
        Assert.Null(complaint.Q("[name='f.description']"));
        complaint = await complaint.SaveOkAsync(f => f.AddRow("diligence_log",
            ("dl_date", Today), ("dl_method", "phone"), ("dl_contact", "Priya Raman (biomed)"), ("dl_outcome", "Confirmed firmware 4.3 installed"), ("dl_info_obtained", "yes")));
        Assert.NotNull(complaint.Q("input[value='Confirmed firmware 4.3 installed']"));
        Assert.Equal("Closed", (await complaint.RouteOkAsync("Close complaint")).Phase());
    }

    [Fact] // CMP-003, CMP-004, CMP-009, SAF-022, INV-113
    public async Task An_eu_vigilance_clock_stops_only_when_regulatory_affairs_records_the_submission_with_a_valid_signature()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var complaint = await jlee.CreateAsync("complaint", f => f
            .Set("f.subject", $"Particulate in ST-40 fluid path {tag}").Pick("f.category", "packaging").Set("f.owner", fx.Uid("jlee").ToString())
            .Set("f.awareness_date", Day(-2)).Pick("f.site_reported", "galway").Set("f.description", "Black particles inside sealed pouches of lot 24-127.")
            .Set("f.product", "ST-40 sterile tubing set").Set("f.lot_number", $"EU-{tag}").Pick("f.failure_mode", "contamination")
            .Pick("f.patient_involved", "no").Pick("f.eu_market", "yes"));
        var url = complaint.Path;
        complaint = await complaint.RouteOkAsync("Start investigation");

        // CMP-003: a device on the EU market gets the vigilance tree as well; both must be completed (INV-046).
        var mdrTree = Assert.Single(complaint.Activities(), a => a.Number.StartsWith("MDR-"));
        var vigTree = Assert.Single(complaint.Activities(), a => a.Number.StartsWith("VIG-"));
        var mdr = await (await jlee.GoAsync(mdrTree.Href)).RouteOkAsync("Send for regulatory approval", fill: f => f
            .Pick("f.q_death", "no").Pick("f.q_serious_injury", "no").Pick("f.q_malfunction", "no").Set("f.mdr_rationale", "No malfunction of the device itself."));
        var vig = await (await jlee.GoAsync(vigTree.Href)).SaveOkAsync(f => f
            .Pick("f.q_incident", "yes").Pick("f.q_causal", "yes").Pick("f.q_serious", "yes").Pick("f.vig_severity", "other_serious")
            .Set("f.vig_rationale", "Particulate could have led to a serious deterioration in health."));
        Assert.Equal("Yes", vig.FieldText("vig_reportable"));
        Assert.Equal("15", vig.FieldText("vig_days"));
        vig = await vig.RouteOkAsync("Send for regulatory approval");
        using var rnovak = await fx.SignedInAsync("rnovak");
        Assert.Equal("Completed", (await (await rnovak.GoAsync(mdrTree.Href)).RouteOkAsync("Approve decision")).Phase());
        Assert.Equal("Completed", (await (await rnovak.GoAsync(vigTree.Href)).RouteOkAsync("Approve decision")).Phase());

        // CMP-009: 15 calendar days from awareness for an EU serious incident; no FDA report.
        complaint = await rnovak.GoAsync(url);
        Assert.Equal("Not reportable", complaint.FieldText("mdr_decision"));
        Assert.Equal("Reportable: 15 days", complaint.FieldText("vigilance_decision"));
        complaint.ExpectText("EU MDR serious incident (Art. 87(3))").ExpectText($"Due {BusinessCalendar.Today.AddDays(13):ddd dd MMM yyyy}").ExpectText("13d left");

        // SAF-022: the complaint can't be approved for closure before the report is filed.
        complaint = await (await jlee.GoAsync(url)).SaveOkAsync(f => f
            .Set("f.investigation_summary", "Particles are pouch-seal debris from sealer SB-2.").Pick("f.conclusion", "confirmed")
            .Pick("f.capa_required", "no").Set("f.capa_justification", "Covered by the sealer CAPA.").Pick("f.ncr_required", "no")
            .Set("f.ncr_justification", "Lot already quarantined under its NCR.").Set("f.response_text", "Lot recalled; replacement shipped.").Set("f.response_sent_on", Today));
        complaint = await complaint.RouteOkAsync("Send to review", assignees: [fx.Uid("sortiz")]);
        using var sortiz = await fx.SignedInAsync("sortiz");
        var unreported = await (await sortiz.GoAsync(url)).RouteAsync("Approve: awaiting closure");
        Assert.Contains(unreported.RouteErrors(), e => e.Contains("hasn't been submitted") && e.Contains("SAF-022"));

        // The submission is recorded with an e-signature: a wrong password records nothing.
        complaint = await rnovak.GoAsync(url);
        var wrong = await complaint.ActionAsync("cmp-submit", f => f.Pick("a.agency", "EU").Set("a.reference", $"MIR-IE-{tag}"), password: "not-my-password");
        Assert.Contains(wrong.ActionErrors("cmp-submit"), e => e.Contains("password isn't right"));
        Assert.Equal("Clock running", (await rnovak.GoAsync(url)).FieldText("clock_status"));
        complaint = await (await rnovak.GoAsync(url)).ActionOkAsync("cmp-submit", f => f.Pick("a.agency", "EU").Set("a.reference", $"MIR-IE-{tag}"),
            password: TestHelpers.Password);
        Assert.Equal("EU submission recorded.", complaint.Toast);
        Assert.Equal("Submitted", complaint.FieldText("clock_status"));
        Assert.Contains($"MIR-IE-{tag}", complaint.FieldText("report_reference"));
        Assert.Equal("Awaiting Closure", (await (await sortiz.GoAsync(url)).RouteOkAsync("Approve: awaiting closure")).Phase());
    }

    [Fact] // CMP-010, CMP-012, PLT-1103
    public async Task Complaint_trends_and_views_find_a_repeat_lot_and_export_it()
    {
        var tag = E2e.Tag();
        var lot = $"LOT-{tag}";
        using var jlee = await fx.SignedInAsync("jlee");
        var numbers = new List<string>();
        for (var i = 0; i < 3; i++)
            numbers.Add((await NewComplaintAsync(fx, jlee, $"Repeat lot complaint {i + 1} {tag}", lot, injury: false)).Number());

        // CMP-010: three complaints on one lot within 90 days is a signal, linked to the filtered list.
        var trends = await jlee.GoAsync("/quality/complaint-trends?months=3");
        var signal = trends.All("a[href]").FirstOrDefault(a => a.TextContent.Contains(lot))
            ?? throw new Xunit.Sdk.XunitException($"No trend link for {lot}.\n{trends.Excerpt()}");
        var filtered = await jlee.GoAsync(signal.GetAttribute("href")!);
        Assert.Equal(numbers.Order(), filtered.ViewNumbers().Order());

        // The similar-complaints panel on the record sees the other two.
        var record = await jlee.GoAsync(await jlee.FindInViewAsync("complaints", "all", numbers[0]));
        record.ExpectText("Similar complaints").ExpectText(numbers[1]).ExpectText(numbers[2]);

        // Views: search, keyword filter and CSV export.
        var mine = await jlee.GoAsync($"/apps/complaints?view=mine&q={tag}");
        Assert.Equal(3, mine.ViewNumbers().Count);
        var byCategory = await jlee.GoAsync($"/apps/complaints?view=all&q={tag}&f.category=adverse_event");
        Assert.Empty(byCategory.ViewNumbers());
        var csv = await jlee.RawGetAsync($"/apps/complaints/export.csv?view=all&q={tag}");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.StartsWith("\"Number\",\"Title\"", text.TrimStart('﻿'));
        Assert.All(numbers, n => Assert.Contains(n, text));
    }
}
