using System.Net;
using static Vellum.Tests.E2e.QualitySteps;

namespace Vellum.Tests.E2e;

/// <summary>CAPA §12.4 end to end through the web pages: CAPA with action items done by their assignees, 8D and SCAR.</summary>
[Collection("vellum")]
public class CapaE2eTests(VellumFixture fx)
{
    [Fact] // CAPA-002..004, CAPA-007, QE-001, QE-002, INV-042, INV-043, SAF-044
    public async Task A_capa_goes_from_initiate_to_closed_with_action_items_completed_by_their_assignees()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");

        // CAPA-007: a priority that contradicts the risk rating needs a justification.
        var refused = await (await jlee.NewAsync("capa")).RecordForm()
            .Set("f.subject", $"Torque drift on station 3 {tag}").Pick("f.capa_type", "corrective").Pick("f.source", "internal_audit")
            .Pick("f.priority", "low").Set("f.owner", fx.Uid("jlee").ToString()).Set("f.description", "6 of 40 housings under-torqued.")
            .Pick("f.risk_severity", "serious").Pick("f.risk_occurrence", "occasional").SubmitAsync();
        Assert.Contains(refused.Errors, e => e.Contains("suggests High priority") && e.Contains("CAPA-007"));

        var capa = await jlee.CreateAsync("capa", f => f
            .Set("f.subject", $"Torque drift on station 3 {tag}").Pick("f.capa_type", "corrective").Pick("f.source", "internal_audit")
            .Pick("f.priority", "high").Set("f.owner", fx.Uid("jlee").ToString()).Set("f.product", "VX-200 housing")
            .Set("f.description", "Audit found 6 of 40 sampled housings torqued below the 2.4 N·m minimum.")
            .Pick("f.risk_severity", "serious").Pick("f.risk_occurrence", "occasional"));
        var url = capa.Path;
        var number = capa.Number();
        Assert.StartsWith("CAPA-", number);
        Assert.Equal("Initiate", capa.Phase());
        Assert.Equal("High", capa.FieldText("suggested_priority"));

        capa = await capa.RouteOkAsync("Submit for review");
        Assert.Equal("Initial Review", capa.Phase());
        capa = await capa.RouteOkAsync("Accept: investigate");
        Assert.Equal("Root Cause & Plan", capa.Phase());

        // PLT-502: the plan can't go for approval until the investigation is written up.
        var incomplete = await capa.RouteAsync("Submit plan for approval");
        Assert.Contains(incomplete.Errors, e => e.Contains("Root cause statement is required"));
        Assert.Contains(incomplete.Errors, e => e.Contains("Add at least one action to Planned actions"));
        Assert.Equal("Root Cause & Plan", (await jlee.GoAsync(url)).Phase());

        capa = await (await jlee.GoAsync(url)).SaveOkAsync(f => f
            .Pick("f.root_cause_category", "machine")
            .Set("f.root_cause", "The torque driver had no calibration interval, so drift went undetected for 4 months.")
            .AddRow("whys", ("why", "Why were housings under-torqued?"), ("because", "The driver delivered 12% low."))
            .AddRow("whys", ("why", "Why did it deliver low?"), ("because", "It was never recalibrated."))
            .Set("f.solution", "Put the driver on a 30-day calibration cycle and add a torque check to the work instruction.")
            .AddRow("planned_actions", ("action", $"Recalibrate the torque driver {tag}"), ("assignee", fx.Uid("akim").ToString()), ("days", "5"), ("verify", "no"))
            .AddRow("planned_actions", ("action", $"Add a torque check to WI-ASM-014 {tag}"), ("assignee", fx.Uid("pshah").ToString()), ("days", "10"), ("verify", "yes"))
            .Set("f.effectiveness_criteria", "Zero under-torque findings in 3 weekly audits of 40 units.")
            .Set("f.effectiveness_due", Day(90)).Set("f.effectiveness_verifier", fx.Uid("tbrooks").ToString()));
        Assert.Equal(2, capa.All("[name^='r.planned_actions.'][name$='.__id']").Count);

        capa = await capa.RouteOkAsync("Submit plan for approval", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Plan Approval", capa.Phase());

        // The approver signs; a wrong password is refused and nothing moves.
        using var sortiz = await fx.SignedInAsync("sortiz");
        (await sortiz.GoAsync("/work")).ExpectText(number);
        var bad = await (await sortiz.GoAsync(url)).RouteAsync("Approve plan", password: "not-my-password");
        Assert.NotEmpty(bad.RouteErrors());
        Assert.Equal("Plan Approval", (await sortiz.GoAsync(url)).Phase());
        capa = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve plan");
        Assert.Equal("Implementation", capa.Phase());

        // QE-002: each planned action is now an Action Item assigned to its person.
        capa = await jlee.GoAsync(url);
        var items = capa.Activities().Where(a => a.Number.StartsWith("AI-")).ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, a => Assert.Contains("Open", a.Text));

        // INV-043: the effectiveness check waits for every action item.
        var early = await capa.RouteAsync("Send to effectiveness check");
        Assert.Contains(early.RouteErrors(), e => e.StartsWith("Complete the open action items first"));
        Assert.Equal("Implementation", (await jlee.GoAsync(url)).Phase());

        // Only the assignee can complete an action item: someone else gets no route to take.
        var akimItem = items.Single(a => a.Text.Contains("Recalibrate"));
        using var nflores = await fx.SignedInAsync("nflores");
        Assert.Empty((await nflores.GoAsync(akimItem.Href)).RouteOptions());

        using var akim = await fx.SignedInAsync("akim");
        (await akim.GoAsync("/work")).ExpectText(akimItem.Number);
        var ai = await akim.GoAsync(akimItem.Href);
        Assert.Equal("Open", ai.Phase());
        ai = await ai.SaveOkAsync(f => f.Set("f.action_taken", "Driver recalibrated (cert CAL-2291) and put on a 30-day interval."));
        ai = await ai.RouteOkAsync("Complete");
        Assert.Equal("Completed", ai.Phase());

        // pshah's item needs independent verification by the CAPA owner (QE-001).
        var pshahItem = items.Single(a => a.Text.Contains("WI-ASM-014"));
        using var pshah = await fx.SignedInAsync("pshah");
        ai = await (await pshah.GoAsync(pshahItem.Href)).SaveOkAsync(f => f.Set("f.action_taken", "WI-ASM-014 rev F adds a daily torque check."));
        Assert.DoesNotContain("Complete", ai.RouteOptions());
        ai = await ai.RouteOkAsync("Send for verification");
        Assert.Equal("Verification", ai.Phase());
        ai = await (await jlee.GoAsync(pshahItem.Href)).SaveOkAsync(f => f.Pick("f.verification_result", "verified").Set("f.verification_comments", "Checked rev F on the line."));
        ai = await ai.RouteOkAsync("Verify and close");
        Assert.Equal("Completed", ai.Phase());

        capa = await (await jlee.GoAsync(url)).RouteOkAsync("Send to effectiveness check");
        Assert.Equal("Effectiveness Check", capa.Phase());

        // The effectiveness verifier works the check. "Not effective" never offers closure (INV-042).
        using var tbrooks = await fx.SignedInAsync("tbrooks");
        (await tbrooks.GoAsync("/work")).ExpectText(number);
        capa = await (await tbrooks.GoAsync(url)).SaveOkAsync(f => f.Pick("f.effectiveness_result", "not_effective").Set("f.effectiveness_evidence", "One finding in week 2."));
        Assert.DoesNotContain("Effective: close CAPA", capa.RouteOptions());
        Assert.Contains("Not effective: reopen investigation", capa.RouteOptions());

        // INV-043: an action item raised during the check keeps the CAPA open until it's done.
        var late = await tbrooks.CreateAsync("action_item", f => f
            .Set("f.subject", $"Re-audit station 3 after the week-2 finding {tag}").Set("f.assignee", fx.Uid("akim").ToString()).Set("f.due_date", Day(7)), capa.RecordId);
        late = await late.RouteOkAsync("Assign action");
        Assert.Equal("Open", late.Phase());
        capa = await (await tbrooks.GoAsync(url)).SaveOkAsync(f => f.Pick("f.effectiveness_result", "effective")
            .Set("f.effectiveness_evidence", "Three weekly audits (120 units) found no under-torque housings."));
        var blocked = await capa.RouteAsync("Effective: close CAPA");
        Assert.Contains(blocked.RouteErrors(), e => e.Contains(late.Number()));
        Assert.Equal("Effectiveness Check", (await tbrooks.GoAsync(url)).Phase());
        ai = await (await akim.GoAsync(late.Path)).SaveOkAsync(f => f.Set("f.action_taken", "Re-audited 40 units: all within torque."));
        Assert.Equal("Completed", (await ai.RouteOkAsync("Complete")).Phase());

        capa = await (await tbrooks.GoAsync(url)).RouteOkAsync("Effective: close CAPA");
        Assert.Equal("Closed", capa.Phase());
        Assert.Contains("signed", capa.One(".vl-sig-badge").TextContent);
        Assert.Empty(capa.RouteOptions());

        // INV-043 holds after closure too: no new action items can be added to a closed CAPA, even from a saved link.
        Assert.Empty(capa.All("a[href^='/records/new?form=action_item']"));
        await jlee.GoAsync($"/records/new?form=action_item&parent={capa.RecordId}", HttpStatusCode.BadRequest);

        // The closed CAPA is in the Closed view and its print shows who approved and verified it.
        Assert.Contains(number, (await jlee.GoAsync($"/apps/capa?view=closed&q={tag}")).ViewNumbers());
        var print = await jlee.GoAsync(url + "/print");
        print.ExpectText(fx.Name("sortiz")).ExpectText("Approved").ExpectText(fx.Name("tbrooks")).ExpectText("Verified effective");
    }

    [Fact] // a route sent from a page that's out of date is refused with a message the person can see
    public async Task An_approver_working_from_an_out_of_date_page_is_told_the_record_has_moved_on()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var capa = await jlee.CreateAsync("capa", f => f
            .Set("f.subject", $"Stale approval {tag}").Pick("f.capa_type", "preventive").Pick("f.source", "management_review").Pick("f.priority", "medium")
            .Set("f.owner", fx.Uid("jlee").ToString()).Set("f.description", "Training risk on the new labeling system."));
        capa = await (await capa.RouteOkAsync("Submit for review")).RouteOkAsync("Accept: investigate");
        capa = await capa.SaveOkAsync(f => f
            .Pick("f.root_cause_category", "man").Set("f.root_cause", "Second shift wasn't trained.").Set("f.solution", "Extend the training.")
            .AddRow("planned_actions", ("action", "Train second shift"), ("assignee", fx.Uid("jlee").ToString()), ("days", "5"), ("verify", "no"))
            .Set("f.effectiveness_criteria", "No labeling errors for 60 days."));
        capa = await capa.RouteOkAsync("Submit plan for approval", assignees: fx.Uids("sortiz", "dokafor").Select(int.Parse).ToArray());

        // Both approvers open the record; Sam approves first, then Dana sends from the page still open.
        using var dokafor = await fx.SignedInAsync("dokafor");
        var stale = await dokafor.GoAsync(capa.Path);
        using var sortiz = await fx.SignedInAsync("sortiz");
        Assert.Equal("Implementation", (await (await sortiz.GoAsync(capa.Path)).RouteOkAsync("Approve plan")).Phase());

        var result = await stale.RouteAsync("Approve plan");
        Assert.Null(result.Toast);
        Assert.Equal("Implementation", result.Phase());
        Assert.Contains(result.Errors.Concat(result.RouteErrors()), e => e.Contains("isn't available any more"));
    }

    [Fact] // CAPA-006
    public async Task An_8d_capa_carries_the_disciplines_and_needs_d4_to_d7_before_it_moves_on()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var capa = await jlee.CreateAsync("capa", f => f
            .Set("f.subject", $"8D: occlusion alarm failures {tag}").Pick("f.capa_type", "eight_d").Pick("f.source", "customer_complaint")
            .Pick("f.priority", "critical").Set("f.owner", fx.Uid("jlee").ToString()).Set("f.description", "3 pumps didn't alarm on downstream occlusion.")
            .Set("f.d0_symptoms", "Alarm latency over 60 s at 15 psi.").Set("f.d0_era", "Serial range quarantined.")
            .Set("f.d1_champion", fx.Uid("dokafor").ToString()).Set("f.d1_leader", fx.Uid("jlee").ToString()).Set("f.d1_team", fx.Uids("akim", "cwu"))
            .Set("f.d2_what", "Downstream occlusion alarm late. Is not: upstream.").Set("f.d3_actions", "100% bench test of quarantined stock."));
        capa = await (await capa.RouteOkAsync("Submit for review")).RouteOkAsync("Accept: investigate");
        // D0–D2 are read-only from Root Cause & Plan on.
        Assert.EndsWith(fx.Name("dokafor"), capa.FieldText("d1_champion"));
        Assert.Contains("Downstream occlusion alarm late", capa.FieldText("d2_what"));

        capa = await capa.SaveOkAsync(f => f
            .Pick("f.root_cause_category", "material").Set("f.root_cause", "Force-sensor lot with a +0.6 N zero offset.")
            .Set("f.solution", "Tighten incoming inspection and add a latency test.")
            .AddRow("planned_actions", ("action", $"Add a latency test to the end-of-line tester {tag}"), ("assignee", fx.Uid("jlee").ToString()), ("days", "10"), ("verify", "no"))
            .Set("f.effectiveness_criteria", "No latency failures for 3 months."));
        var refused = await capa.RouteAsync("Submit plan for approval");
        Assert.Contains(refused.Errors, e => e.Contains("Why it occurred (occurrence root cause) is required"));
        Assert.Contains(refused.Errors, e => e.Contains("Chosen permanent corrective actions is required"));

        capa = await (await jlee.GoAsync(capa.Path)).SaveOkAsync(f => f
            .Set("f.d4_occurrence", "Sensor lot FS-0824 zero offset shifted the threshold.").Set("f.d4_escape", "End-of-line test checked upstream only.")
            .Set("f.d5_actions", "Incoming offset limit ±0.2 N; latency test at end of line."));
        capa = await capa.RouteOkAsync("Submit plan for approval", assignees: [fx.Uid("sortiz")]);
        using var sortiz = await fx.SignedInAsync("sortiz");
        capa = await (await sortiz.GoAsync(capa.Path)).RouteOkAsync("Approve plan");
        Assert.Equal("Implementation", capa.Phase());

        var ai = await jlee.OpenActivityAsync(await jlee.GoAsync(capa.Path), "AI-");
        ai = await ai.SaveOkAsync(f => f.Set("f.action_taken", "Latency test added to EOL-2."));
        Assert.Equal("Completed", (await ai.RouteOkAsync("Complete")).Phase());

        capa = await jlee.GoAsync(capa.Path);
        var noD6 = await capa.RouteAsync("Send to effectiveness check", fill: f => f.Set("f.effectiveness_verifier", fx.Uid("tbrooks").ToString()));
        Assert.Contains(noD6.Errors, e => e.Contains("Implementation and validation results is required"));
        Assert.Contains(noD6.Errors, e => e.Contains("Systemic preventive actions (similar products and processes) is required"));
        capa = await (await jlee.GoAsync(capa.Path)).RouteOkAsync("Send to effectiveness check", fill: f => f
            .Set("f.effectiveness_verifier", fx.Uid("tbrooks").ToString())
            .Set("f.d6_implementation", "Latency test live on EOL-2; 300 pumps passed.").Set("f.d7_systemic", "Offset limit added to all force-sensor specs."));
        Assert.Equal("Effectiveness Check", capa.Phase());

        // The 8D view lists it with its team leader.
        var view = await jlee.GoAsync($"/apps/capa?view=eight_d&q={tag}");
        Assert.Contains(capa.Number(), view.ViewNumbers());
    }

    [Fact] // CAPA-010, CAPA-011, SAF-044
    public async Task A_scar_is_issued_to_the_supplier_answered_and_verified_by_someone_else()
    {
        var tag = E2e.Tag();
        using var cwu = await fx.SignedInAsync("cwu");
        var scar = await cwu.CreateAsync("scar", f => f
            .Set("f.subject", $"Resin lots shipped without CoAs {tag}").Set("f.owner", fx.Uid("cwu").ToString()).Pick("f.priority", "medium")
            .Set("f.description", "Three PC-2405 deliveries arrived without certificates of analysis.").Set("f.requirement", "Quality agreement §4.3")
            .Set("f.supplier_name", "Polymera Resins GmbH").Set("f.supplier_email", "quality@polymera.example").Set("f.part_number", "RM-PC-2405")
            .Set("f.lot_number", "R-5521").Pick("f.containment_required", "yes").Set("f.stock_quarantined", "1200"));
        Assert.StartsWith("SCAR-", scar.Number());
        scar = await scar.RouteOkAsync("Submit for approval", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Initial Approval", scar.Phase());

        using var sortiz = await fx.SignedInAsync("sortiz");
        var noDue = await (await sortiz.GoAsync(scar.Path)).RouteAsync("Approve and issue to supplier");
        Assert.Contains(noDue.Errors, e => e.Contains("Supplier response due is required"));
        scar = await (await sortiz.GoAsync(scar.Path)).RouteOkAsync("Approve and issue to supplier", fill: f => f.Set("f.response_due", Day(20)));
        Assert.Equal("Assign to Supplier", scar.Phase());

        // CAPA-011: the SCAR goes to the supplier contact by email (kept in the email log).
        scar = await (await cwu.GoAsync(scar.Path)).ActionOkAsync("scar-send", f => f.Set("a.email", "k.vogel@polymera.example"));
        Assert.Equal("SCAR sent to k.vogel@polymera.example.", scar.Toast);
        Assert.Contains("Resend to supplier", scar.Text);

        // SAF-044: the owner can't verify their own SCAR.
        scar = await scar.SaveOkAsync(f => f
            .Set("f.sr_containment", "Remaining stock sorted.").Set("f.sr_root_cause", "CoA step skipped after an ERP change.")
            .Set("f.sr_corrective_action", "CoA printing moved into the shipping release.").Set("f.sr_implementation_date", Day(-2))
            .Set("f.sr_received_on", Today).Set("f.sr_responder", "Katrin Vogel, QA").Set("f.scar_verifier", fx.Uid("cwu").ToString()));
        var self = await scar.RouteAsync("Supplier responded: verify");
        Assert.Contains(self.RouteErrors(), e => e.Contains("someone other than the SCAR owner"));
        scar = await (await cwu.GoAsync(scar.Path)).RouteOkAsync("Supplier responded: verify", fill: f => f.Set("f.scar_verifier", fx.Uid("jlee").ToString()));
        Assert.Equal("Verification & Effectiveness", scar.Phase());

        using var jlee = await fx.SignedInAsync("jlee");
        scar = await (await jlee.GoAsync(scar.Path)).SaveOkAsync(f => f
            .Set("f.verification_method", "Checked CoAs on the next 5 lots.").Pick("f.scar_result", "effective").Set("f.verification_evidence", "All 5 lots arrived with CoAs."));
        scar = await scar.RouteOkAsync("Verified effective: complete");
        Assert.Equal("Completed", scar.Phase());
        Assert.Contains(scar.Number(), (await jlee.GoAsync($"/apps/capa?view=scar_closed&q={tag}")).ViewNumbers());
        await cwu.GoAsync(scar.Path + "/print");
    }
}
