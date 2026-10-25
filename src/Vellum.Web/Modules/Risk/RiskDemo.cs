using Microsoft.EntityFrameworkCore;
using Vellum.Engine.Records;
using Vellum.Modules.Supplier;
using Vellum.Packaging;

namespace Vellum.Modules.Risk;

/// <summary>Demo risk register for Acme Medical Devices: nine assessments across every level and phase.</summary>
public static class RiskDemo
{
    /// <summary>Complete every open action item under a record as its assignee (verifying when required).</summary>
    public static async Task CompleteActionItemsAsync(DemoContext d, Guid parentId, string? only = null, int daysAgo = 3)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        foreach (var ai in await records.ChildrenAsync(parentId, "action_item"))
        {
            if (ai.PhaseType != Domain.PhaseType.Normal || (only != null && !ai.Title.Contains(only, StringComparison.OrdinalIgnoreCase))) continue;
            d.Db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(ai.Id);
            var doer = d.Directory.Get(snap.UserId("assignee"))!.Username;
            await d.UpdateAsync(ai.Id, new() { ["action_taken"] = "Implemented as planned; evidence filed with the record.", ["completed_on"] = DemoContext.Days(-daysAgo) }, doer);
            if (snap.Bool("verification_required") == true)
            {
                await d.RouteAsync(ai.Id, "verification");
                var verifier = d.Directory.Get(snap.UserId("verifier"))!.Username;
                await d.UpdateAsync(ai.Id, new() { ["verification_result"] = "verified", ["verification_comments"] = "Verified on the line; records reviewed." }, verifier);
            }
            await d.RouteAsync(ai.Id, "completed");
        }
    }

    public static async Task SeedAsync(DemoContext d)
    {
        Dictionary<string, object?> Mit(string measure, string type, string? owner, int days) =>
            new() { ["measure"] = measure, ["control_type"] = type, ["mit_owner"] = owner == null ? null : d.User(owner), ["days"] = (decimal)days };

        async Task<Guid> New(string subject, string category, string source, string owner, string product, string hazard, string harm, int daysAgo,
            List<Dictionary<string, object?>>? mitigations = null, Guid? sourceRecord = null)
        {
            var values = new Dictionary<string, object?>
            {
                ["subject"] = subject, ["risk_category"] = category, ["risk_source"] = source, ["owner"] = d.User(owner),
                ["product"] = product, ["hazard"] = hazard, ["harm"] = harm,
            };
            if (sourceRecord != null) values["source_record"] = sourceRecord;
            var r = await d.CreateAsync("risk_assessment", values, owner, daysAgo: daysAgo,
                rows: mitigations == null ? null : new() { ["mitigations"] = mitigations });
            return r.Id;
        }
        async Task Assess(Guid id, string owner, int s, int l, string rationale, string? controls = null)
        {
            await d.RouteAsync(id, "assessment");
            await d.UpdateAsync(id, new() { ["severity"] = $"s{s}", ["likelihood"] = $"l{l}", ["rationale"] = rationale, ["existing_controls"] = controls }, owner);
        }
        async Task Residual(Guid id, string owner, int s, int l, string verification) =>
            await d.UpdateAsync(id, new() { ["residual_severity"] = $"s{s}", ["residual_likelihood"] = $"l{l}", ["verification"] = verification }, owner);
        async Task Complete(Guid id, string notes, string reviewer, int reviewInDays = 365)
        {
            await d.UpdateAsync(id, new() { ["review_notes"] = notes, ["review_date"] = DemoContext.Days(reviewInDays) }, reviewer);
            await d.RouteAsync(id, "completed", asUser: reviewer);
        }

        Guid? galway = null;
        if (d.Config.FormsByKey.TryGetValue("supplier", out var supplierForm))
            galway = await d.Db.Records.AsNoTracking().Where(r => r.FormId == supplierForm.Id && r.Title == "Galway Electronics Ltd.").Select(r => (Guid?)r.Id).FirstOrDefaultAsync();

        // 1. Completed: high initial risk mitigated to low.
        var drop = await New("Housing crack on drop lets fluid into the electronics", "product_safety", "design_review", "jlee", "VX-200 infusion pump",
            "A 1 m drop onto a hard floor cracks the housing at the battery bay; IV fluid spilled later can reach the control board.",
            "Short circuit and pump shutdown during therapy: interruption of a critical infusion.", 140,
            [
                Mit("Add internal ribs and increase wall thickness to 2.2 mm at the battery bay (DCR-0412)", "design", "akim", 15),
                Mit("1.2 m drop test on 30 units per IEC 60601-1-11, followed by an IPX2 drip test", "detection", "jlee", 10),
            ]);
        await Assess(drop, "jlee", 4, 3, "Two field reports of cracked housings in 18 months (installed base 4,200); drop-test margin at 1 m was marginal on DV units.",
            "Drop test at 1.0 m in design verification; IPX1 rating.");
        await d.RouteAsync(drop, "mitigation");
        await CompleteActionItemsAsync(d, drop, daysAgo: 60);
        await Residual(drop, "jlee", 4, 1, "Re-run drop and drip testing on 30 units from tool rev D: no cracks, no ingress (TR-2026-118).");
        await d.RouteAsync(drop, "review");
        await Complete(drop, "Controls verified effective. Residual risk Low; monitor complaint trend quarterly.", "sortiz");

        // 2. Completed: catastrophic severity reduced to medium by design and alarm.
        var freeFlow = await New("Free flow of medication when the administration set is misloaded", "product_safety", "design_review", "jlee", "VX-200 infusion pump",
            "If the administration set isn't seated in the pumping channel and the door is opened, gravity free flow can occur.",
            "Over-infusion of a high-alert drug: serious injury or death.", 210,
            [
                Mit("Anti-free-flow clamp that closes automatically when the door opens", "design", "akim", 20),
                Mit("Set-misload sensor with an audible alarm before infusion starts", "protective", "akim", 25),
            ]);
        await Assess(freeFlow, "jlee", 5, 3, "Use-error found in formative usability study FUS-03 (2 of 15 nurses misloaded the set).");
        await d.RouteAsync(freeFlow, "mitigation");
        await CompleteActionItemsAsync(d, freeFlow, daysAgo: 120);
        await Residual(freeFlow, "jlee", 5, 1, "Summative usability study SUS-01: 0 of 30 free-flow events; clamp verified in 500 door cycles.");
        await d.RouteAsync(freeFlow, "review");
        await Complete(freeFlow, "Residual risk reduced as far as possible; benefit outweighs remaining risk. Medium, acceptable (ALARP).", "dokafor");

        // 3. In mitigation: critical supply-chain risk with open action items.
        var single = await New("Single source for the VX-200 control board (Galway Electronics)", "supply_chain", "supplier_issue", "cwu", "PCBA-200-CTL",
            "Galway Electronics is the only qualified PCBA source and is conditionally approved after audit SA-2026-02.",
            "Line stoppage and backorders for an infusion pump used in critical care.", 45,
            [
                Mit("Qualify a second PCBA source (shortlist of three EMS providers)", "supplier", "cwu", 90),
                Mit("Hold 12 weeks of safety stock of PCBA-200-CTL", "procedural", "lgarcia", 20),
            ], galway);
        await Assess(single, "cwu", 4, 4, "Supplier scored D in two quarters; one rejected lot in October. No alternative source qualified.");
        await d.RouteAsync(single, "mitigation");
        await CompleteActionItemsAsync(d, single, only: "safety stock", daysAgo: 5);

        // 4. In assessment: medium risk being planned.
        var eo = await New("EO sterilant residuals above ISO 10993-7 limits on accessory kits", "product_safety", "process_change", "jlee", "VX-200 accessory kit",
            "A shorter aeration cycle proposed by NorthStar could leave ethylene oxide residuals above the tolerable contact limit.",
            "Local irritation or systemic toxicity in patients.", 8,
            [Mit("Keep 72 h aeration until residual testing on 3 loads confirms the shorter cycle", "procedural", "jlee", 30)]);
        await Assess(eo, "jlee", 4, 2, "Residual results on the current cycle are at 40% of the limit; the change would roughly double them.");

        // 5. Critical, awaiting e-signed acceptance in Review.
        var latency = await New("Occlusion alarm latency at low flow rates (installed base, firmware 2.4)", "product_safety", "field_data", "jlee", "VX-200 infusion pump, firmware 2.4",
            "At flow rates below 1 mL/h the downstream occlusion alarm can take up to 2.5 h to sound on pumps running firmware 2.4.",
            "Delayed delivery of a critical drug (for example vasopressors in neonates): serious injury or death.", 30,
            [
                Mit("Customer advisory letter and IFU addendum: use the low-flow occlusion setting below 1 mL/h", "information", "rnovak", 10),
                Mit("Firmware 3.0 field update for all installed pumps (planned, tracked in the change program)", "design", null, 120),
            ]);
        await Assess(latency, "jlee", 5, 4, "Bench testing reproduced the latency; three complaints in 9 months, one with patient involvement (MDR filed).");
        await d.RouteAsync(latency, "mitigation");
        await CompleteActionItemsAsync(d, latency, daysAgo: 6);
        await Residual(latency, "jlee", 5, 3, "Advisory letter acknowledged by 94% of customer sites; the latency itself remains until firmware 3.0 is installed.");
        await d.RouteAsync(latency, "review");

        // 6. Completed long ago; its periodic review is now overdue.
        var hvac = await New("Cleanroom HVAC failure during the fill operation", "process", "management_review", "akim", "Cleanroom B fill line",
            "Loss of HVAC pressure cascade during filling lets particles into open product.", "Particulate contamination of sterile fluid path components.", 380);
        await Assess(hvac, "akim", 3, 2, "Two HVAC trips in five years; pressure alarms and line stop interlock in place.",
            "Differential pressure alarms; automatic line stop; EM monitoring per SOP-EM-004.");
        await d.RouteAsync(hvac, "review");
        await Complete(hvac, "Acceptable (Medium). Existing controls effective; re-review annually.", "sortiz");
        await d.UpdateAsync(hvac, new() { ["review_date"] = DemoContext.Days(-5) }, "sortiz");

        // 7. Draft.
        await New("Shared login on the label printing workstation", "data_integrity", "audit_finding", "pshah", "Labeling station LS-2",
            "Operators use a shared Windows account on the label printing PC, so label print events can't be attributed.",
            "Mislabeling can't be traced to a person; 21 CFR Part 11 noncompliance.", 3);

        // 8. Completed EHS risk; review due within 30 days.
        var ipa = await New("Operator exposure to IPA vapour at the manual cleaning station", "ehs", "audit_finding", "akim", "Cleaning station CL-2",
            "Manual wipe-down with 99.9% IPA in a small room without local exhaust ventilation.", "Dizziness and headaches; fire risk from vapour accumulation.", 300,
            [Mit("Install local exhaust ventilation at station CL-2", "design", "ebaker", 30)]);
        await Assess(ipa, "akim", 3, 3, "Air monitoring found 180 ppm (TWA limit 200 ppm) during peak cleaning.");
        await d.RouteAsync(ipa, "mitigation");
        await CompleteActionItemsAsync(d, ipa, daysAgo: 200);
        await Residual(ipa, "akim", 3, 2, "Follow-up air monitoring: 35 ppm TWA with LEV running.");
        await d.RouteAsync(ipa, "review");
        await Complete(ipa, "LEV effective. Re-check air monitoring at the annual review.", "sortiz");
        await d.UpdateAsync(ipa, new() { ["review_date"] = DemoContext.Days(20) }, "sortiz");

        // 9. Completed with an e-signed acceptance of a residual High risk.
        var battery = await New("Battery depletion stops the pump during transport", "product_safety", "field_data", "jlee", "VX-200 infusion pump",
            "During long intra-hospital transports the internal battery can run out before the pump is reconnected to mains power.",
            "Interruption of infusion; the clinician must switch to a manual alternative.", 95,
            [Mit("Low-battery alarm escalation at 30 and 10 minutes remaining", "protective", "akim", 20)]);
        await Assess(battery, "jlee", 5, 2, "Battery runtime is 6 h; 4 events reported in 2 years, all with alarm warnings ignored.");
        await d.RouteAsync(battery, "mitigation");
        await CompleteActionItemsAsync(d, battery, daysAgo: 40);
        await Residual(battery, "jlee", 5, 2, "Alarm escalation verified in V&V (TR-2026-077). Likelihood unchanged: depends on clinical practice.");
        await d.RouteAsync(battery, "review");
        await DemoActions.RunAsync(d, battery, "accept_risk", "dokafor", new()
        {
            ["rationale"] = "A larger battery would add 400 g and was rejected by clinicians in usability testing. Two-stage alarms and IFU warnings reduce the risk as far as practicable; the benefit of portable infusion outweighs the residual risk.",
        });
        await Complete(battery, "Residual High risk accepted by the VP Quality & Regulatory (e-signed). Re-assess when the next-generation battery is available.", "sortiz", 180);
    }
}
