using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Tests.E2e;

/// <summary>
/// EHS, assets and calibration, and projects end to end through the web pages: an injury from report to an OSHA-recordable,
/// closed incident on the OSHA log; an anonymous near miss that EHS has to accept; a JSA and the work permit that relies on
/// it; calibrations with as-found failures and quarantine; and a stage-gate project whose tasks are done by others.
/// </summary>
[Collection("vellum")]
public class EhsAssetsE2eTests(VellumFixture fx)
{
    private static string Iso(DateOnly d) => EhsAssetsSteps.Iso(d);
    private string Name(string username) => fx.Services.GetRequiredService<UserDirectory>().Get(username)!.DisplayName;

    [Fact] // EHS-101..113, SEC-520: injury → OSHA recordable (DART) → corrective action → management review → closed, on the OSHA log
    public async Task An_injury_becomes_an_osha_recordable_case_on_the_log_and_is_closed_after_review()
    {
        var tag = E2e.Tag();
        var injured = $"Jamie Ortega {tag}";

        // Anyone can report it (EHS-101).
        using var nflores = await fx.SignedInAsync("nflores");
        var blank = await nflores.NewAsync("incident");
        var form = blank.RecordForm()
            .Set("f.subject", $"Hand cut on deburring tool {tag}").Pick("f.incident_type", "injury")
            .Set("f.occurred_at", blank.LocalInput(DateTime.UtcNow.AddHours(-3))).Pick("f.site", "bos").Set("f.area", "Machining cell 4")
            .Pick("f.outcome", "restricted").Set("f.description", "Glove caught on the deburring tool while changing parts; cut to the left hand.")
            .Set("f.supervisor", fx.Uid("lgarcia").ToString()).Set("f.immediate_actions", "First aid given; tool guarded.").Pick("f.area_made_safe", "yes");
        var incident = await form.SubmitAsync();
        Assert.Equal("Report", incident.Phase());
        var url = incident.Path;
        var number = incident.Number();
        incident = await incident.RouteOkAsync("Submit to EHS");
        Assert.Equal("Investigation", incident.Phase());

        // EHS records the injured person (sensitive), the OSHA determination and the investigation.
        using var mchen = await fx.SignedInAsync("mchen");
        (await mchen.GoAsync("/work")).ExpectText(number);
        var inv = await mchen.GoAsync(url);
        var wrongClass = await inv.SaveAsync(f => f.Pick("f.recordable", "yes").Pick("f.osha_classification", "other_recordable").Set("f.days_restricted", "5"));
        Assert.Contains(wrongClass.Errors, e => e.Contains("isn't \"Other recordable\""));
        inv = await (await mchen.GoAsync(url)).SaveAsync(f => f
            .Pick("f.injured_category", "employee").Set("f.injured_name", injured).Set("f.injured_job_title", "Machine operator")
            .Pick("f.body_part", "hand").Pick("f.injury_nature", "laceration").Pick("f.medical_treatment", "clinic").Pick("f.privacy_case", "no")
            .Pick("f.recordable", "yes").Pick("f.osha_classification", "restricted").Pick("f.osha_case_type", "injury")
            .Set("f.days_away", "0").Set("f.days_restricted", "5").Set("f.recordability_notes", "Stitches and five days of restricted duty.")
            .Pick("f.root_cause_category", "machine").Set("f.root_cause", "No guard over the deburring tool's exposed edge.")
            .Pick("f.corrective_action_required", "yes").Set("f.reviewer", fx.Uid("lgarcia").ToString())
            .AddRow("corrective_actions", ("action", $"Fit a fixed guard to the deburring tool {tag}"), ("assignee", fx.Uid("akim").ToString()), ("days", "10"), ("verify", "no"))
            .AddRow("costs", ("item", "Clinic visit"), ("amount", "240")));
        Assert.Equal("Saved.", inv.Toast);
        Assert.Equal("Yes", inv.FieldText("dart"));
        Assert.Equal("USD 240.00", inv.FieldText("total_cost"));

        // The reporter can read the incident but not the injured person's details (SEC-520).
        var asReporter = await nflores.GoAsync(url);
        asReporter.ExpectNoText(injured).ExpectText("Restricted");

        // It's on the OSHA log at once, with the name for EHS only, and counted in the rates (EHS-110, RPT-702).
        var year = BusinessCalendar.Today.Year;
        var hours = await mchen.CreateAsync("hours_worked", f => f.Pick("f.site", "bos").Set("f.period", Iso(BusinessCalendar.Today))
            .Set("f.employees", "50").Set("f.hours", "100000"));
        Assert.StartsWith("HRS-", hours.Number());
        var osha = await mchen.GoAsync($"/ehs/osha?year={year}&site=bos");
        var logRow = Page.Squash(osha.All("table.vl-osha-log tbody tr").Single(r => r.TextContent.Contains(number)).TextContent);
        Assert.Contains(injured, logRow);
        Assert.Contains("Machine operator", logRow);
        Assert.Contains("✓", logRow); // job transfer or restriction
        var boston = Page.Squash(osha.All("tr").First(r => r.QuerySelector("td")?.TextContent.Trim() == "Boston").TextContent);
        Assert.Contains("100,000", boston);
        var csv = await mchen.RawGetAsync($"/ehs/osha?year={year}&site=bos&handler=Csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains(number, text);
        Assert.Contains(injured, text);
        var reporterLog = await nflores.GoAsync($"/ehs/osha?year={year}&site=bos");
        Assert.Contains("Restricted", reporterLog.All("table.vl-osha-log tbody tr").Single(r => r.TextContent.Contains(number)).TextContent);
        reporterLog.ExpectNoText(injured);

        // Corrective actions become action items; the review waits for them (EHS-104).
        inv = await (await mchen.GoAsync(url)).RouteOkAsync("Start corrective actions");
        Assert.Equal("Corrective Actions", inv.Phase());
        var early = await inv.RouteAsync("Send to management review");
        Assert.NotEmpty(early.RouteErrors().Concat(early.Errors));
        using var akim = await fx.SignedInAsync("akim");
        await akim.CompleteActionItemAsync($"Fit a fixed guard to the deburring tool {tag}", "Fixed guard fitted and checked.");
        inv = await (await mchen.GoAsync(url)).RouteOkAsync("Send to management review");
        Assert.Equal("Review", inv.Phase());

        // The area manager reviews and closes it with a signature.
        using var lgarcia = await fx.SignedInAsync("lgarcia");
        var review = await (await lgarcia.FromWorkAsync(number)).SaveAsync(f => f
            .Set("f.review_comments", "Guarding gap closed; checked the other two cells.").Set("f.lessons_learned", "Check guards after tool changes."));
        review = await review.RouteOkAsync("Close incident");
        Assert.Equal("Closed", review.Phase());
        (await mchen.GoAsync($"/ehs/osha?year={year}&site=bos")).ExpectText(number);
    }

    [Fact] // EHS-101/102: a near miss reported anonymously has no reporter, and a named EHS team member must accept it
    public async Task An_anonymous_near_miss_has_no_reporter_and_must_be_accepted_by_ehs_before_it_moves_on()
    {
        var tag = E2e.Tag();

        // The quick-report page needs a sign-in (there is no kiosk mode): anonymity is about what's stored, not who can post.
        using var visitor = new Browser(fx);
        Assert.StartsWith("/login", (await visitor.GoAsync("/ehs/near-miss")).Url.AbsolutePath);

        using var nflores = await fx.SignedInAsync("nflores");
        var page = await nflores.GoAsync("/ehs/near-miss");
        var empty = await page.Form("form.card").Set("Subject", "").Set("Description", "").SubmitAsync();
        Assert.Contains(empty.Errors, e => e.Contains("Say in a few words what nearly happened"));
        Assert.Contains(empty.Errors, e => e.Contains("Describe what happened"));

        var subject = $"Pallet slipped off forklift forks {tag}";
        var done = await page.Form("form.card")
            .Set("Subject", subject).Pick("Site", "kzo").Set("Area", "Warehouse aisle 3").Pick("Potential", "high")
            .Set("Description", "A pallet of boxes slid off the forks next to the pedestrian walkway; nobody was in the walkway.")
            .Set("Immediate", "Aisle closed and the load restacked.").Set("Suggestion", "Shrink-wrap mixed loads.")
            .Set("Anonymous", "true").SubmitAsync();
        done.ExpectText("Thank you for speaking up").ExpectText("submitted anonymously");
        var number = Regex.Match(done.Text, @"INC-\d+").Value;

        // EHS sees it in their queue; the record names nobody as reporter.
        using var mchen = await fx.SignedInAsync("mchen");
        var rec = await mchen.FromWorkAsync(number);
        var url = rec.Path;
        Assert.Equal("Investigation", rec.Phase());
        rec.ExpectText("Anonymous report: waiting for EHS to accept it").ExpectNoText(Name("nflores"));
        Assert.Empty(rec.RecordForm().Values("f.reported_by"));
        var history = await mchen.GoAsync(url + "/print");
        history.ExpectNoText(Name("nflores"));

        // Nobody else can accept it, and it can't move on (or be returned to a reporter) until EHS does.
        Assert.Equal("Only the EHS team can accept an anonymous report.", (await nflores.GoAsync(url)).DisabledAction("Accept anonymous report"));
        rec = await rec.SaveAsync(f => f.Pick("f.root_cause_category", "method").Set("f.root_cause", "Mixed loads aren't wrapped.")
            .Pick("f.corrective_action_required", "no").Set("f.reviewer", fx.Uid("lgarcia").ToString()));
        var refused = await rec.RouteAsync("No actions needed: send to review");
        Assert.Contains(refused.RouteErrors(), e => e.Contains("Accept this anonymous report first"));
        var back = await (await mchen.GoAsync(url)).RouteAsync("Return to reporter for details", comment: "Need more detail.");
        Assert.Contains(back.RouteErrors(), e => e.Contains("there's no reporter to return it to"));

        rec = await (await mchen.GoAsync(url)).ActionAsync("anon_accept", f => f.Set("a.note", "Credible; aisle inspected."));
        Assert.Contains("You're recorded as its EHS owner", rec.Toast);
        rec.ExpectText("Anonymous report, accepted by " + Name("mchen"));
        rec = await rec.RouteOkAsync("No actions needed: send to review");
        Assert.Equal("Review", rec.Phase());
    }

    [Fact] // EHS §17.3/17.4, INV-078, SAF-044: a JSA approved with a signature, then a hot work permit authorized and closed out
    public async Task A_job_safety_analysis_is_approved_and_a_hot_work_permit_is_authorized_and_closed_out()
    {
        var tag = E2e.Tag();
        using var akim = await fx.SignedInAsync("akim");
        var page = await akim.NewAsync("jsa");
        var jsa = await page.RecordForm()
            .Set("f.job_title", $"Weld bracket to mezzanine handrail {tag}").Pick("f.site", "kzo").Set("f.area", "Bay 3")
            .Set("f.reviewer", fx.Uid("mchen").ToString()).Set("f.required_ppe", "Welding helmet, gloves, FR jacket")
            .AddRow("steps", ("step", "Weld the bracket"), ("hazard", "Sparks igniting stored cartons"),
                ("sev_i", page.RowOption("steps", "sev_i", "sev4")), ("lik_i", page.RowOption("steps", "lik_i", "lik3")), ("controls", "Clear 10 m, fire blanket, fire watch"),
                ("sev_r", page.RowOption("steps", "sev_r", "sev4")), ("lik_r", page.RowOption("steps", "lik_r", "lik1")))
            .AddRow("steps", ("step", "Grind the weld"), ("hazard", "Eye injury from fragments"),
                ("sev_i", page.RowOption("steps", "sev_i", "sev2")), ("lik_i", page.RowOption("steps", "lik_i", "lik2")), ("controls", "Face shield"),
                ("sev_r", page.RowOption("steps", "sev_r", "sev3")), ("lik_r", page.RowOption("steps", "lik_r", "lik2")))
            .SubmitAsync();
        // It can't be saved while a step's residual risk is higher than its initial risk without a justification (INV-078).
        Assert.Contains(jsa.Errors, e => e.Contains("Step 2: the residual risk (6) is higher than the initial risk (4)"));
        jsa = await jsa.RecordForm().Set("r.steps.1.justification", "Grinding is done in a new position with a higher fall risk for fragments.").SubmitAsync();
        Assert.StartsWith("JSA-", jsa.Number());
        Assert.Equal("12", jsa.RowText("steps", 0, "risk_i"));
        Assert.Equal("Low", jsa.RowText("steps", 0, "band_r"));
        var jsaUrl = jsa.Path;
        jsa = await jsa.RouteOkAsync("Submit for review");
        using var mchen = await fx.SignedInAsync("mchen");
        jsa = await (await mchen.GoAsync(jsaUrl)).RouteOkAsync("Approve JSA");
        Assert.Equal("Approved", jsa.Phase());

        // The permit: one shift at most for hot work, issuer and receiver are different people.
        var blank = await akim.NewAsync("permit");
        var tooLong = await blank.RecordForm()
            .Set("f.work_title", $"Weld bracket, bay 3 {tag}").Pick("f.permit_type", "hot_work").Pick("f.site", "kzo").Set("f.location", "Mezzanine handrail, bay 3")
            .Set("f.receiver", fx.Uid("akim").ToString()).Set("f.issuer", fx.Uid("akim").ToString())
            .Set("f.valid_from", blank.LocalInput(DateTime.UtcNow.AddMinutes(-30))).Set("f.valid_to", blank.LocalInput(DateTime.UtcNow.AddHours(14)))
            .SubmitAsync();
        Assert.Contains(tooLong.Errors, e => e.Contains("one shift at most (12 hours)"));
        Assert.Contains(tooLong.Errors, e => e.Contains("issuer can't also be the permit receiver"));
        var permit = await tooLong.RecordForm()
            .Set("f.issuer", fx.Uid("mchen").ToString()).Set("f.valid_to", blank.LocalInput(DateTime.UtcNow.AddHours(8)))
            .Set("f.jsa", jsa.RecordId.ToString())
            .AddRow("conditions", ("condition", "Combustibles cleared within 10 m"), ("confirmed", "yes"))
            .AddRow("conditions", ("condition", "Fire watch posted for 60 minutes after"), ("confirmed", "no"))
            .SubmitAsync();
        Assert.StartsWith("PTW-", permit.Number());
        var permitUrl = permit.Path;
        permit = await permit.RouteOkAsync("Submit for authorization");
        Assert.Equal("Authorization", permit.Phase());

        // The issuer can't authorize while a precaution isn't confirmed.
        var notYet = await (await mchen.GoAsync(permitUrl)).RouteAsync("Authorize permit");
        Assert.Contains(notYet.RouteErrors(), e => e.Contains("Confirm every precaution is in place before authorizing: Fire watch posted"));
        permit = await (await mchen.GoAsync(permitUrl)).SaveAsync(f => f.Pick("r.conditions.1.confirmed", "yes"));
        permit = await permit.RouteOkAsync("Authorize permit");
        Assert.Equal("Active", permit.Phase());
        Assert.Equal("Valid", permit.FieldText("validity_status"));
        (await akim.GoAsync("/apps/ehs?view=permits_active")).ExpectText(permit.Number());

        // The receiver closes it out once the area is safe.
        var close = await (await akim.GoAsync(permitUrl)).SaveAsync(f => f.Pick("f.work_completed", "yes").Pick("f.area_left_safe", "no"));
        var refused = await close.RouteAsync("Close out permit");
        Assert.Contains(refused.RouteErrors(), e => e.Contains("area is left safe"));
        close = await (await akim.GoAsync(permitUrl)).SaveAsync(f => f.Pick("f.area_left_safe", "yes").Set("f.closeout_notes", "Fire watch completed, no hot spots."));
        close = await close.RouteOkAsync("Close out permit");
        Assert.Equal("Closed", close.Phase());
        Assert.Equal("Closed out", close.FieldText("validity_status"));
    }

    [Fact] // AST-020..029, INV-064, INV-115, INV-116, SAF-040: calibrations against a standard, an as-found failure and a quarantine
    public async Task An_asset_is_calibrated_against_a_standard_and_quarantined_when_it_is_still_out_of_tolerance()
    {
        var tag = E2e.Tag();
        using var ebaker = await fx.SignedInAsync("ebaker");
        var standard = await ebaker.CreateAsync("asset", f => f
            .Set("f.asset_id", $"kzo-ts-{tag}").Set("f.description", "Torque transducer standard 0–5 N·m").Pick("f.asset_type", "torque_standard")
            .Pick("f.site", "kzo").Set("f.owner", fx.Uid("lgarcia").ToString()).Pick("f.calibration_required", "yes").Set("f.cal_interval_days", "365")
            .Set("f.technician", fx.Uid("ebaker").ToString()).Set("f.cal_vendor", "Precision Cal Labs").Pick("f.use_as_standard", "yes")
            .Set("f.traceability", "NIST-traceable, cert PCL-2291")
            .AddRow("characteristics", ("characteristic", "Torque at 2.0 N·m"), ("unit", "N·m"), ("nominal", "2.000"), ("tol_minus", "0.004"), ("tol_plus", "0.004")));
        Assert.Equal($"KZO-TS-{tag}", standard.FieldText("asset_id")); // tags are stored upper-case (INV-022)
        Assert.Equal("Calibration due", standard.FieldText("status"));
        var driver = await ebaker.CreateAsync("asset", f => f
            .Set("f.asset_id", $"KZO-TD-{tag}").Set("f.description", "Electronic torque driver, station 3").Pick("f.asset_type", "torque_driver")
            .Pick("f.site", "kzo").Set("f.owner", fx.Uid("lgarcia").ToString()).Pick("f.calibration_required", "yes").Set("f.cal_interval_days", "180")
            .Set("f.technician", fx.Uid("ebaker").ToString())
            .AddRow("characteristics", ("characteristic", "Torque at 1.0 N·m"), ("unit", "N·m"), ("nominal", "1.00"), ("tol_minus", "0.05"), ("tol_plus", "0.05")));
        var driverUrl = driver.Path;

        // Asset IDs are unique across sites (INV-022).
        var dup = await (await ebaker.NewAsync("asset")).RecordForm().Set("f.asset_id", $"KZO-TD-{tag}").Set("f.description", "Duplicate")
            .Pick("f.asset_type", "torque_driver").Pick("f.site", "bos").Set("f.owner", fx.Uid("lgarcia").ToString()).Set("f.cal_interval_days", "180").SubmitAsync();
        Assert.NotEmpty(dup.Errors);

        // Only the responsible technician records calibrations (INV-087).
        using var nflores = await fx.SignedInAsync("nflores");
        Assert.Contains("Only the responsible technician", (await nflores.GoAsync(driverUrl)).DisabledAction("Record calibration"));

        // The standard can't be used until it has a passing calibration of its own (INV-115).
        var cal = await (await ebaker.GoAsync(driverUrl)).ActionAsync("record_calibration");
        Assert.Contains("Calibration CAL-", cal.Toast);
        var calUrl = cal.Path;
        var noStandard = await cal.SaveAsync(f => f.Set("f.standards", standard.RecordId.ToString()).Set("r.readings.0.found", "1.07").Set("r.readings.0.left", "1.01"));
        Assert.Contains(noStandard.Errors, e => e.Contains("had no passing calibration") && e.Contains("INV-115"));

        var stdCal = await (await ebaker.GoAsync(standard.Path)).ActionAsync("record_calibration");
        Assert.Equal("external", stdCal.FieldValue("cal_type")); // the standard has a calibration vendor
        var noCert = await stdCal.SaveAsync(f => f.Set("r.readings.0.found", "2.001").Set("r.readings.0.left", "2.001"));
        var refused = await noCert.RouteAsync("Complete calibration");
        Assert.Contains(refused.RouteErrors(), e => e.Contains("certificate number"));
        stdCal = await (await ebaker.GoAsync(stdCal.Path)).SaveAsync(f => f.Set("f.certificate_no", "PCL-" + tag));
        Assert.Equal("Pass", stdCal.FieldText("as_found_result"));
        stdCal = await stdCal.RouteOkAsync("Complete calibration");
        var std = await ebaker.GoAsync(standard.Path);
        Assert.Equal("In service", std.FieldText("status"));
        Assert.Equal(BusinessCalendar.Today.AddDays(365).ToString("dd MMM yyyy", CultureInfo.InvariantCulture), std.FieldText("next_cal_date"));

        // As found out of tolerance (AST-022: 1.07 is 0.02 above the upper limit), adjusted back in.
        cal = await (await ebaker.GoAsync(calUrl)).SaveAsync(f => f.Set("f.standards", standard.RecordId.ToString())
            .Set("f.procedure", "CP-012 rev D").Set("r.readings.0.found", "1.07").Set("r.readings.0.left", "1.01"));
        Assert.Equal("0.02", cal.RowText("readings", 0, "found_dev"));
        Assert.Equal("Fail", cal.FieldText("as_found_result"));
        Assert.Equal("Pass", cal.FieldText("as_left_result"));
        var blocked = await cal.RouteAsync("Complete calibration");
        Assert.Contains(blocked.RouteErrors(), e => e.Contains("measurement impact assessment"));
        Assert.Contains(blocked.RouteErrors(), e => e.Contains("link a corrective action (CAPA) or choose \"Bypass corrective action\""));
        cal = await (await ebaker.GoAsync(calUrl)).SaveAsync(f => f
            .Set("f.impact_assessment", "Station 3 torque checks since the last calibration were re-verified with a second driver; all within limits.")
            .Pick("f.bypass_capa", "yes").Set("f.bypass_reason", "Impact assessed: no product affected."));
        cal = await cal.RouteOkAsync("Complete calibration");
        Assert.Equal("Completed", cal.Phase());
        var asset = await ebaker.GoAsync(driverUrl);
        Assert.Equal("In service", asset.FieldText("status"));
        Assert.Equal(BusinessCalendar.Today.AddDays(180).ToString("dd MMM yyyy", CultureInfo.InvariantCulture), asset.FieldText("next_cal_date"));
        using var lgarcia = await fx.SignedInAsync("lgarcia");
        (await lgarcia.GoAsync("/notifications")).ExpectText($"As-found failure on KZO-TD-{tag}");

        // Next time it can't be adjusted back in: it's quarantined (AST-025, SAF-040) and shows on the due/overdue panel.
        var cal2 = await asset.ActionAsync("record_calibration");
        cal2 = await cal2.SaveAsync(f => f.Set("f.standards", standard.RecordId.ToString()).Set("r.readings.0.found", "1.10").Set("r.readings.0.left", "1.08")
            .Set("f.impact_assessment", "Driver drifting; parts since the last calibration re-checked.").Pick("f.bypass_capa", "yes").Set("f.bypass_reason", "Covered by the re-check."));
        Assert.Equal("Fail", cal2.FieldText("as_left_result"));
        cal2 = await cal2.RouteOkAsync("Complete calibration");
        asset = await ebaker.GoAsync(driverUrl);
        Assert.Equal("Quarantined", asset.FieldText("status"));
        asset.ExpectText("Out of tolerance after adjustment");
        Assert.Null(asset.DisabledAction("Quarantine asset")); // not offered at all once quarantined
        Assert.DoesNotContain("quarantine", asset.Actions());

        // The maintenance team's "Calibration due" panel counts it, and its link lists it (AST-050).
        var home = await ebaker.GoAsync("/");
        var widget = home.All(".card").Single(c => c.QuerySelector(".card-title-sm")?.TextContent.Contains("Calibration due") == true);
        var quarantined = widget.QuerySelector("a[href='/apps/assets?view=quarantined'] .n")!;
        Assert.True(int.Parse(quarantined.TextContent.Trim()) >= 1, widget.TextContent);
        (await ebaker.GoAsync("/apps/assets?view=quarantined")).ExpectText($"KZO-TD-{tag}");
        (await nflores.GoAsync("/")).ExpectNoText("Every asset is in calibration"); // not shown outside maintenance
    }

    [Fact] // PRJ-007/008/020: a project's tasks are assigned to others, who complete them; the gate needs a Go review and every approver
    public async Task A_project_gate_passes_once_others_complete_their_tasks_and_every_gate_approver_signs()
    {
        var tag = E2e.Tag();
        using var akim = await fx.SignedInAsync("akim");
        var blank = await akim.NewAsync("project");
        var oneApprover = await blank.RecordForm()
            .Set("f.project_name", $"VX-300 pump {tag}").Pick("f.project_type", "npi").Set("f.sponsor", fx.Uid("lgarcia").ToString())
            .Set("f.gate_approvers", fx.Uids("lgarcia")).Set("f.target_launch", Iso(BusinessCalendar.Today.AddDays(200)))
            .Set("f.objectives", "Launch the VX-300 infusion pump.").SubmitAsync();
        Assert.Contains(oneApprover.Errors, e => e.Contains("at least two gate approvers"));
        Assert.Contains(oneApprover.Errors, e => e.Contains("Include the project manager"));
        var project = await oneApprover.RecordForm().Set("f.gate_approvers", fx.Uids("akim", "lgarcia")).SubmitAsync();
        Assert.StartsWith("PRJ-", project.Number());
        Assert.Equal("Concept", project.Phase());
        var url = project.Path;

        // Tasks for the Concept stage, assigned to other people.
        var t1 = await akim.CreateAsync("task", f => f.Set("f.subject", $"Market requirements {tag}").Set("f.assignee", fx.Uid("jlee").ToString())
            .Pick("f.stage", "concept").Set("f.due_date", Iso(BusinessCalendar.Today.AddDays(20))), parent: project.RecordId);
        var t2 = await akim.CreateAsync("task", f => f.Set("f.subject", $"Preliminary hazard analysis {tag}").Set("f.assignee", fx.Uid("mchen").ToString())
            .Pick("f.stage", "concept").Set("f.due_date", Iso(BusinessCalendar.Today.AddDays(25))), parent: project.RecordId);
        Assert.Equal("Open", t1.Phase());
        project = await akim.GoAsync(url);
        Assert.Equal("2", project.FieldText("open_tasks"));
        Assert.Equal("0", project.FieldText("percent_complete"));

        // The gate can't be signed with open tasks or without a Go decision.
        var early = await project.RouteAsync("Approve Concept gate");
        var errors = early.RouteErrors();
        Assert.Contains(errors, e => e.Contains("Record the Concept gate review with a Go"));
        Assert.Contains(errors, e => e.Contains("Close the open Concept tasks first") && e.Contains(t1.Number()) && e.Contains(t2.Number()));

        // The assignees find their tasks and complete them.
        using var jlee = await fx.SignedInAsync("jlee");
        var task = await jlee.FromWorkAsync(t1.Number());
        var noOutcome = await task.RouteAsync("Complete task");
        Assert.NotEmpty(noOutcome.RouteErrors().Concat(noOutcome.Errors));
        task = await (await jlee.GoAsync(task.Path)).SaveAsync(f => f.Set("f.outcome", "MRD-0042 approved by marketing.").Set("f.deliverable", "MRD-0042"));
        task = await task.RouteOkAsync("Complete task");
        Assert.Equal("Completed", task.Phase());
        Assert.Equal("100", task.FieldText("percent_complete"));
        using var mchen = await fx.SignedInAsync("mchen");
        task = await (await mchen.FromWorkAsync(t2.Number())).SaveAsync(f => f.Set("f.outcome", "PHA-17 complete; 4 hazards to the risk register."));
        task = await task.RouteOkAsync("Complete task");
        project = await akim.GoAsync(url);
        Assert.Equal("0", project.FieldText("open_tasks"));
        Assert.Equal("100", project.FieldText("percent_complete"));

        // A Go gate review, then every gate approver signs (voting 100%).
        project = await project.SaveAsync((f) => f.AddRow("gate_reviews",
            ("gate", project.RowOption("gate_reviews", "gate", "concept")), ("review_date", Iso(BusinessCalendar.Today)),
            ("decision", project.RowOption("gate_reviews", "decision", "go")), ("summary", "MRD and PHA reviewed")));
        project = await project.RouteOkAsync("Approve Concept gate");
        Assert.Equal("Concept", project.Phase()); // 1 of 2 approvals
        using var lgarcia = await fx.SignedInAsync("lgarcia");
        project = await (await lgarcia.GoAsync(url)).RouteOkAsync("Approve Concept gate");
        Assert.Equal("Feasibility", project.Phase());
        (await akim.GoAsync(url + "/print")).ExpectText("Gate approved: Concept").ExpectText(Name("akim")).ExpectText(Name("lgarcia"));
    }
}
