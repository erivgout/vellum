using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Change;

/// <summary>Change Management demo data: the packaged change templates (CHG-003) and changes in every phase.</summary>
internal static class ChangeDemo
{
    /// <summary>CHG-003: the packaged change templates. Baseline data, seeded at install even without demo data.</summary>
    public static async Task SeedTemplatesAsync(DemoContext d)
    {
        Dictionary<string, object?> Approval(string type, params string[] users) => new() { ["approval_type"] = type, ["approvers"] = users.Select(d.User).ToList() };
        Dictionary<string, object?> Impact(string area, string assessor) => new() { ["area"] = area, ["assessor"] = d.User(assessor) };
        Dictionary<string, object?> Step(string action, decimal days, bool verify) => new() { ["action"] = action, ["days"] = days, ["verify"] = verify };

        // ------------------------------------------------------------------ packaged templates (CHG-003)
        await d.CreateAsync("change_template", new()
        {
            ["name"] = "Engineering change", ["enabled"] = true, ["default_impact"] = "medium", ["all_approvers"] = false, ["ia_required"] = false,
            ["description"] = "Design changes to product, drawings, bills of material or tooling (legacy ECN). Feasibility is assessed in Review; ad-hoc action items can be added during implementation.",
            ["verification_required"] = true,
            ["verification_instructions"] = "Confirm the released drawing and BOM revisions are in use on the line and the first-article inspection passed.",
            ["default_reg_impact"] = "minor", ["default_filing"] = "annual",
        }, "sortiz", daysAgo: 160, rows: new()
        {
            ["template_approvals"] = [Approval("engineering", "akim"), Approval("quality", "sortiz", "dokafor"), Approval("regulatory", "rnovak"), Approval("manufacturing", "lgarcia")],
            ["template_impacts"] = [Impact("regulatory", "rnovak"), Impact("manufacturing", "lgarcia"), Impact("validation", "jlee")],
            ["template_plan"] =
            [
                Step("Release the updated drawing and BOM", 10, false),
                Step("Complete first-article inspection on the new configuration", 15, true),
                Step("Update affected work instructions and train operators", 10, false),
            ],
        });
        await d.CreateAsync("change_template", new()
        {
            ["name"] = "Process change", ["enabled"] = true, ["default_impact"] = "medium", ["all_approvers"] = false, ["ia_required"] = true,
            ["description"] = "Changes to validated manufacturing processes, parameters, equipment or cleaning. Impact assessments must be completed, and the change is verified after implementation.",
            ["verification_required"] = true,
            ["verification_instructions"] = "Review the process validation report and 3 consecutive production lots against the acceptance criteria.",
            ["default_reg_impact"] = "minor", ["default_filing"] = "annual",
        }, "sortiz", daysAgo: 160, rows: new()
        {
            ["template_approvals"] = [Approval("quality", "sortiz"), Approval("manufacturing", "lgarcia"), Approval("engineering", "akim"), Approval("validation", "jlee")],
            ["template_impacts"] = [Impact("manufacturing", "lgarcia"), Impact("validation", "jlee"), Impact("regulatory", "rnovak")],
            ["template_plan"] =
            [
                Step("Execute process validation (IQ/OQ/PQ) and approve the report", 20, true),
                Step("Update the PFMEA and control plan", 10, false),
                Step("Train operators on the revised process", 7, false),
            ],
        });
        await d.CreateAsync("change_template", new()
        {
            ["name"] = "Specification change", ["enabled"] = true, ["default_impact"] = "low", ["all_approvers"] = false, ["ia_required"] = false,
            ["description"] = "Changes to product, material or inspection specifications and acceptance criteria.",
            ["verification_required"] = false, ["default_reg_impact"] = "minor", ["default_filing"] = "annual",
        }, "sortiz", daysAgo: 160, rows: new()
        {
            ["template_approvals"] = [Approval("quality", "sortiz", "dokafor"), Approval("engineering", "akim")],
            ["template_impacts"] = [Impact("quality", "jlee"), Impact("supply_chain", "cwu")],
            ["template_plan"] = [Step("Update the specification and the inspection plan", 5, false), Step("Notify affected suppliers of the revised specification", 5, false)],
        });
        await d.CreateAsync("change_template", new()
        {
            ["name"] = "Supplier change", ["enabled"] = true, ["default_impact"] = "high", ["all_approvers"] = false, ["ia_required"] = false,
            ["description"] = "New, alternate or relocated suppliers and contract manufacturers, and supplier-initiated process changes.",
            ["verification_required"] = true,
            ["verification_instructions"] = "Confirm the supplier is on the approved supplier list with a signed quality agreement and the first lots passed incoming inspection.",
            ["default_reg_impact"] = "moderate",
        }, "sortiz", daysAgo: 160, rows: new()
        {
            ["template_approvals"] = [Approval("supply_chain", "cwu"), Approval("quality", "sortiz", "dokafor"), Approval("regulatory", "rnovak")],
            ["template_impacts"] = [Impact("supply_chain", "cwu"), Impact("regulatory", "rnovak"), Impact("quality", "jlee")],
            ["template_plan"] =
            [
                Step("Audit and qualify the supplier; sign the quality agreement", 20, false),
                Step("Approve first-article / PPAP samples", 15, false),
                Step("Add the supplier to the approved supplier list", 3, false),
            ],
        });
        await d.CreateAsync("change_template", new()
        {
            ["name"] = "Document change", ["enabled"] = true, ["default_impact"] = "low", ["all_approvers"] = false, ["ia_required"] = false,
            ["description"] = "Revisions to procedures, work instructions and forms that need change control but no product or process change.",
            ["verification_required"] = false, ["default_reg_impact"] = "none", ["default_filing"] = "none",
        }, "sortiz", daysAgo: 160, rows: new()
        {
            ["template_approvals"] = [Approval("document_control", "pshah"), Approval("quality", "sortiz", "dokafor")],
            ["template_plan"] = [Step("Train affected users on the revised documents", 10, false)],
        });
        await d.CreateAsync("change_template", new()
        {
            ["name"] = "Legacy ECN (pre-2025)", ["enabled"] = false, ["default_impact"] = "medium",
            ["description"] = "Retired: replaced by the Engineering change template. Kept for changes migrated from the legacy system.",
            ["verification_required"] = false,
        }, "sortiz", daysAgo: 400, rows: new() { ["template_approvals"] = [Approval("engineering", "akim")] });
    }

    public static async Task SeedAsync(DemoContext d)
    {
        Dictionary<string, object?> Impact(string area, string assessor) => new() { ["area"] = area, ["assessor"] = d.User(assessor) };
        var engineering = await FindTemplateAsync(d, "Engineering change");
        var process = await FindTemplateAsync(d, "Process change");
        var supplier = await FindTemplateAsync(d, "Supplier change");
        var document = await FindTemplateAsync(d, "Document change");

        // ------------------------------------------------------------------ 1. Engineering change in Approval with partial signatures
        var c1 = await d.CreateAsync("change", new()
        {
            ["change_template"] = engineering,
            ["subject"] = "Change VX-200 pump housing resin from Makrolon 2458 to Lexan HPX4",
            ["owner"] = d.User("akim"), ["planned_date"] = DemoContext.Days(35), ["impact_level"] = "high", ["reason_code"] = "obsolescence",
            ["products"] = "VX-200 infusion pump – front and rear housings (P/N 200-1101, 200-1102)",
            ["current_state"] = "Housings are molded from Makrolon 2458 polycarbonate. The resin supplier has announced end of life for this grade in Q2 2027.",
            ["proposed_change"] = "Mold both housings in Lexan HPX4 (medical-grade polycarbonate, ISO 10993 certified) using the existing tools, with a revised molding window.",
            ["justification"] = "Secures supply beyond 2027. HPX4 has better chemical resistance to hospital disinfectants, addressing 3 complaints of housing crazing in 2026.",
            ["verifier"] = d.User("jlee"), ["reg_impact"] = "moderate", ["filing"] = "cbe30", ["agency"] = "FDA CDRH",
            ["reg_rationale"] = "Material change of a patient-contacting enclosure; biocompatibility re-established by testing, no change to intended use.",
            ["estimated_cost"] = 48500m, ["cost_notes"] = "Biocompatibility testing, mold trials, validation.",
        }, "akim", daysAgo: 24, rows: new()
        {
            ["plan"] =
            [
                new() { ["action"] = "Release housing drawings 200-1101 rev F and 200-1102 rev E", ["assignee"] = d.User("akim"), ["days"] = 10m, ["verify"] = false },
                new() { ["action"] = "Run mold trials and dimensional first-article on HPX4", ["assignee"] = d.User("lgarcia"), ["days"] = 15m, ["verify"] = true, ["plan_verifier"] = d.User("jlee") },
                new() { ["action"] = "Complete ISO 10993 cytotoxicity and sensitization testing", ["assignee"] = d.User("rnovak"), ["days"] = 25m, ["verify"] = false },
            ],
            ["documents"] =
            [
                new() { ["doc_action"] = "change", ["doc_ref"] = "SPEC-MAT-012 Housing resin specification", ["reviser"] = d.User("akim"), ["draft_due"] = DemoContext.Days(10) },
                new() { ["doc_action"] = "change", ["doc_ref"] = "WI-MLD-004 Housing molding parameters", ["reviser"] = d.User("lgarcia"), ["draft_due"] = DemoContext.Days(14) },
            ],
        });
        await d.RouteAsync(c1.Id, "review", asUser: "akim");
        await DemoKit.ActAsync(d, c1.Id, "assign_ia", "akim");
        await CompleteAssessmentsAsync(d, c1.Id, new()
        {
            ["regulatory"] = ("medium", "Moderate change: material of a patient-contacting enclosure. File a CBE-30 with the biocompatibility summary."),
            ["manufacturing"] = ("low", "Existing tools run HPX4 with a 10 °C higher melt temperature. No new equipment."),
        });
        await d.RouteAsync(c1.Id, "approval", asUser: "akim");
        await d.RouteAsync(c1.Id, "implementation", asUser: "akim", comment: "Design is sound; tooling unchanged.");
        await d.RouteAsync(c1.Id, "implementation", asUser: "sortiz", comment: "Quality approves, subject to validation results.");

        // ------------------------------------------------------------------ 2. Completed process change
        var c2 = await d.CreateAsync("change", new()
        {
            ["change_template"] = process,
            ["subject"] = "Increase ultrasonic weld time on ST-40 Y-connector from 0.8 s to 1.1 s",
            ["owner"] = d.User("jlee"), ["planned_date"] = DemoContext.Days(-20), ["impact_level"] = "medium", ["reason_code"] = "capa",
            ["products"] = "ST-40 sterile tubing set – Y-connector weld (line 2, Kalamazoo)",
            ["current_state"] = "Weld time 0.8 s at 40 kHz. Pull tests show a Cpk of 0.92 on joint strength.",
            ["proposed_change"] = "Increase weld time to 1.1 s and amplitude to 85 %; update the validated parameter window.",
            ["justification"] = "Raises joint strength Cpk above 1.33 (CAPA for weld separation complaints).",
            ["verifier"] = d.User("tbrooks"), ["reg_impact"] = "minor", ["filing"] = "annual", ["annual_report_period"] = "FY2026 annual report",
            ["reg_rationale"] = "Parameter change within the validated process; no impact on specifications or labeling.",
            ["estimated_cost"] = 12000m, ["actual_cost"] = 10850m,
        }, "jlee", daysAgo: 82, rows: new()
        {
            ["plan"] =
            [
                new() { ["action"] = "Execute OQ/PQ at the new weld parameters", ["assignee"] = d.User("akim"), ["days"] = 15m, ["verify"] = true, ["plan_verifier"] = d.User("jlee") },
                new() { ["action"] = "Update PFMEA-ST40 and the control plan", ["assignee"] = d.User("jlee"), ["days"] = 10m, ["verify"] = false },
                new() { ["action"] = "Train line 2 operators on WI-WLD-002 rev H", ["assignee"] = d.User("kpatel"), ["days"] = 7m, ["verify"] = false },
            ],
        });
        await d.RouteAsync(c2.Id, "review", asUser: "jlee");
        await DemoKit.ActAsync(d, c2.Id, "assign_ia", "jlee");
        await CompleteAssessmentsAsync(d, c2.Id, new()
        {
            ["manufacturing"] = ("low", "Cycle time +0.3 s per part; line 2 capacity is sufficient."),
            ["validation"] = ("medium", "OQ/PQ at the new window is needed; existing IQ remains valid."),
            ["regulatory"] = ("low", "Within the validated process. Document in the annual report."),
        });
        await d.RouteAsync(c2.Id, "approval", asUser: "jlee");
        foreach (var u in new[] { "sortiz", "lgarcia", "akim", "jlee" })
            await d.RouteAsync(c2.Id, "implementation", asUser: u);
        await DemoKit.CloseActionItemsAsync(d, c2.Id, note: "Done; OQ/PQ report VR-2026-031 approved, 3 lots passed.");
        await d.RouteAsync(c2.Id, "verification", asUser: "jlee");
        await d.UpdateAsync(c2.Id, new()
        {
            ["verification_evidence"] = "Reviewed validation report VR-2026-031 and lots 26-0388, 26-0391 and 26-0397: pull strength Cpk 1.62, no weld separations in 3,600 units.",
            ["verified_on"] = DemoContext.Days(-2),
        }, "tbrooks");
        await d.RouteAsync(c2.Id, "completed", asUser: "tbrooks", comment: "Verified effective.");

        // ------------------------------------------------------------------ 3. PAS change blocked awaiting FDA approval
        var c3 = await d.CreateAsync("change", new()
        {
            ["change_template"] = supplier,
            ["subject"] = "Add Steri-Pro Galway as a second EO sterilization site for ST-40",
            ["owner"] = d.User("cwu"), ["planned_date"] = DemoContext.Days(10), ["impact_level"] = "high", ["reason_code"] = "capacity",
            ["products"] = "ST-40 sterile tubing set (all configurations)",
            ["current_state"] = "All ST-40 lots are ethylene-oxide sterilized at a single contract sterilizer in Michigan.",
            ["proposed_change"] = "Qualify Steri-Pro (Galway, Ireland) as a second EO site with an equivalent validated cycle, for EU supply and business continuity.",
            ["justification"] = "Single-source sterilization is the top supply risk in the 2026 risk register.",
            ["verifier"] = d.User("sortiz"), ["reg_impact"] = "substantial", ["agency"] = "FDA CDRH",
            ["submission_number"] = "P180032/S024", ["submission_date"] = DemoContext.Days(-40),
            ["reg_rationale"] = "New sterilization site for a PMA device: a PMA supplement needs approval before distribution.",
            ["estimated_cost"] = 96000m,
        }, "cwu", daysAgo: 95, rows: new()
        {
            ["plan"] =
            [
                new() { ["action"] = "Audit Steri-Pro Galway and sign the quality agreement", ["assignee"] = d.User("tbrooks"), ["days"] = 20m, ["verify"] = false },
                new() { ["action"] = "Execute EO sterilization validation (ISO 11135) at Steri-Pro", ["assignee"] = d.User("hmoreau"), ["days"] = 30m, ["verify"] = true, ["plan_verifier"] = d.User("jlee") },
                new() { ["action"] = "Add Steri-Pro to the approved supplier list", ["assignee"] = d.User("cwu"), ["days"] = 3m, ["verify"] = false },
            ],
        });
        await d.RouteAsync(c3.Id, "review", asUser: "cwu");
        await DemoKit.ActAsync(d, c3.Id, "assign_ia", "cwu");
        await CompleteAssessmentsAsync(d, c3.Id, new()
        {
            ["supply_chain"] = ("medium", "Lead time +4 days for EU-bound lots; dual sourcing removes the single point of failure."),
            ["regulatory"] = ("high", "New sterilization site for a PMA device: PMA supplement (PAS) required. No distribution of Galway-sterilized lots before FDA approval."),
            ["quality"] = ("medium", "Supplier audit and validation required; incoming inspection plan unchanged."),
        });
        await d.RouteAsync(c3.Id, "approval", asUser: "cwu");
        foreach (var u in new[] { "cwu", "sortiz", "rnovak" })
            await d.RouteAsync(c3.Id, "implementation", asUser: u);
        await DemoKit.CloseActionItemsAsync(d, c3.Id, note: "Completed; reports filed in the PMA supplement package.");
        await d.RouteAsync(c3.Id, "verification", asUser: "cwu");
        await d.UpdateAsync(c3.Id, new()
        {
            ["verification_evidence"] = "Steri-Pro audit SA-2026-014 closed; validation report EO-VAL-0412 approved; three PQ lots sterile (SAL 10⁻⁶).",
        }, "sortiz");
        await d.CommentAsync(c3.Id, "Verification done. Holding completion until FDA approves supplement S024 – Galway-sterilized lots stay quarantined.", "sortiz");

        // ------------------------------------------------------------------ 4. Document change in Review
        var c4 = await d.CreateAsync("change", new()
        {
            ["change_template"] = document,
            ["subject"] = "Revise SOP-QA-007 Complaint handling to add the MDR reportability decision tree",
            ["owner"] = d.User("pshah"), ["planned_date"] = DemoContext.Days(21), ["impact_level"] = "low", ["reason_code"] = "regulatory",
            ["products"] = "Quality system – complaint handling (all products)",
            ["current_state"] = "SOP-QA-007 rev D leaves MDR reportability to the complaint reviewer's judgment without a documented decision path.",
            ["proposed_change"] = "Add a reportability decision tree (new WI-QA-031) referenced from SOP-QA-007 rev E, with 30-day and 5-day reporting clocks.",
            ["justification"] = "FDA inspection observation 483-2 (March 2026) on inconsistent MDR decisions.",
        }, "pshah", daysAgo: 8, rows: new()
        {
            ["documents"] =
            [
                new() { ["doc_action"] = "change", ["doc_ref"] = "SOP-QA-007 Complaint handling (rev D → E)", ["reviser"] = d.User("pshah"), ["draft_due"] = DemoContext.Days(7) },
                new() { ["doc_action"] = "new", ["doc_ref"] = "WI-QA-031 MDR reportability decision tree", ["reviser"] = d.User("rnovak"), ["draft_due"] = DemoContext.Days(7) },
            ],
            ["impacts"] = [new() { ["area"] = "regulatory", ["assessor"] = d.User("rnovak"), ["ia_due"] = DemoContext.Days(6) }, new() { ["area"] = "training", ["assessor"] = d.User("kpatel"), ["ia_due"] = DemoContext.Days(6) }],
        });
        await d.RouteAsync(c4.Id, "review", asUser: "pshah");
        await DemoKit.ActAsync(d, c4.Id, "assign_ia", "pshah");

        // ------------------------------------------------------------------ 5. Specification change in Draft
        await d.CreateAsync("change", new()
        {
            ["change_template"] = (await FindTemplateAsync(d, "Specification change")),
            ["subject"] = "Tighten ST-40 connector bond strength acceptance from ≥ 15 N to ≥ 18 N",
            ["owner"] = d.User("jlee"), ["planned_date"] = DemoContext.Days(40), ["reason_code"] = "quality_improvement",
            ["products"] = "ST-40 sterile tubing set – SPEC-ST40-003",
            ["current_state"] = "Bond strength acceptance is ≥ 15 N; field returns show separations between 15 and 17 N under clinical handling.",
            ["proposed_change"] = "Raise the acceptance limit to ≥ 18 N and the warning limit to 19.5 N in the inspection plan.",
            ["justification"] = "Aligns the specification with the clinical load study CLS-2026-07.",
        }, "jlee", daysAgo: 3);

        // ------------------------------------------------------------------ 6. Engineering change in Implementation (one action item overdue)
        var c6 = await d.CreateAsync("change", new()
        {
            ["change_template"] = engineering,
            ["subject"] = "Add molded strain relief to the VX-200 power cord entry",
            ["owner"] = d.User("akim"), ["planned_date"] = DemoContext.Days(12), ["impact_level"] = "medium", ["reason_code"] = "quality_improvement",
            ["products"] = "VX-200 infusion pump – rear housing, power cord assembly 200-3300",
            ["current_state"] = "The power cord enters the rear housing through a plain grommet; two returns showed conductor fatigue at the entry.",
            ["proposed_change"] = "Overmold a strain relief boot on cord assembly 200-3300 and add a mating boss to rear housing 200-1102.",
            ["justification"] = "Eliminates the fatigue failure mode found in complaint investigations.",
            ["verifier"] = d.User("jlee"), ["reg_impact"] = "minor", ["filing"] = "annual", ["annual_report_period"] = "FY2026 annual report",
        }, "akim", daysAgo: 46, rows: new()
        {
            ["plan"] =
            [
                new() { ["action"] = "Release cord assembly drawing 200-3300 rev C", ["assignee"] = d.User("akim"), ["days"] = 5m, ["verify"] = false },
                new() { ["action"] = "Qualify the overmold supplier tool (T-3300-01)", ["assignee"] = d.User("cwu"), ["days"] = 15m, ["verify"] = true, ["plan_verifier"] = d.User("akim") },
                new() { ["action"] = "Flex-life test 10,000 cycles on 30 cords", ["assignee"] = d.User("hmoreau"), ["days"] = 12m, ["verify"] = false },
            ],
            ["post_impl"] =
            [
                new() { ["post_action"] = "Purge grommet-style cords from line-side stock", ["post_assignee"] = d.User("lgarcia"), ["post_days"] = 3m, ["post_verify"] = false },
            ],
        });
        await d.RouteAsync(c6.Id, "review", asUser: "akim");
        await d.RouteAsync(c6.Id, "approval", asUser: "akim");
        foreach (var u in new[] { "akim", "dokafor", "rnovak", "lgarcia" })
            await d.RouteAsync(c6.Id, "implementation", asUser: u);
        await DemoKit.CloseActionItemsAsync(d, c6.Id, leaveOpen: 2);
        var records = d.Services.GetRequiredService<RecordService>();
        var open6 = (await records.ChildrenAsync(c6.Id, "action_item")).Where(i => i.PhaseType == Domain.PhaseType.Normal).ToList();
        if (open6.Count > 0) await d.SetDueAsync(open6[0].Id, DemoContext.Days(-3));

        // ------------------------------------------------------------------ 7. Completed CBE-30 change
        var c7 = await d.CreateAsync("change", new()
        {
            ["change_template"] = engineering,
            ["subject"] = "VX-200 firmware 3.2.1: revised downstream occlusion alarm threshold",
            ["owner"] = d.User("akim"), ["planned_date"] = DemoContext.Days(-5), ["impact_level"] = "high", ["reason_code"] = "safety",
            ["products"] = "VX-200 infusion pump – firmware 3.2.0 → 3.2.1",
            ["current_state"] = "Downstream occlusion alarm triggers at 18 psi; field data show delayed alarms with small-bore catheters.",
            ["proposed_change"] = "Lower the default threshold to 12 psi and add a configurable 8–15 psi range for neonatal profiles.",
            ["justification"] = "Health hazard evaluation HHE-2026-03; reduces time-to-alarm by 40 % at 1 mL/h.",
            ["verifier"] = d.User("jlee"), ["reg_impact"] = "moderate", ["filing"] = "cbe30", ["agency"] = "FDA CDRH",
            ["submission_number"] = "P180032/S022", ["submission_date"] = DemoContext.Days(-48),
            ["reg_rationale"] = "Change to an alarm setting affecting safety; labeling unchanged apart from the operator's manual table 4-2.",
        }, "akim", daysAgo: 120, rows: new()
        {
            ["plan"] =
            [
                new() { ["action"] = "Software verification of 3.2.1 (SVP-200-3.2.1)", ["assignee"] = d.User("akim"), ["days"] = 10m, ["verify"] = true, ["plan_verifier"] = d.User("jlee") },
                new() { ["action"] = "Update operator's manual table 4-2", ["assignee"] = d.User("pshah"), ["days"] = 5m, ["verify"] = false },
            ],
        });
        await d.RouteAsync(c7.Id, "review", asUser: "akim");
        await d.RouteAsync(c7.Id, "approval", asUser: "akim");
        foreach (var u in new[] { "akim", "sortiz", "rnovak", "lgarcia" })
            await d.RouteAsync(c7.Id, "implementation", asUser: u);
        await DemoKit.CloseActionItemsAsync(d, c7.Id, note: "Verification report SVR-0321 approved; manual rev K released.");
        await d.RouteAsync(c7.Id, "verification", asUser: "akim");
        await d.UpdateAsync(c7.Id, new()
        {
            ["verification_evidence"] = "Firmware 3.2.1 confirmed on 12 production units; occlusion alarm at 12.1 ± 0.4 psi. CBE-30 waiting period elapsed without agency response.",
            ["verified_on"] = DemoContext.Days(-1),
        }, "jlee");
        await d.RouteAsync(c7.Id, "completed", asUser: "jlee");

        // ------------------------------------------------------------------ 8. Voided change
        var c8 = await d.CreateAsync("change", new()
        {
            ["change_template"] = (await FindTemplateAsync(d, "Specification change")),
            ["subject"] = "Switch VX-200 carton labels to thermal-transfer label stock",
            ["owner"] = d.User("lgarcia"), ["planned_date"] = DemoContext.Days(15), ["impact_level"] = "low", ["reason_code"] = "cost_reduction",
            ["current_state"] = "Carton labels are laser-printed on 3-up sheets.",
            ["proposed_change"] = "Print carton labels on thermal-transfer roll stock at the packing station.",
            ["justification"] = "Saves about $9,000 a year in label stock.",
        }, "lgarcia", daysAgo: 30);
        await d.RouteAsync(c8.Id, "review", asUser: "lgarcia");
        await d.RouteAsync(c8.Id, "voided", asUser: "lgarcia", comment: "Superseded: label stock moves to the new labeling system project.");
    }

    private static async Task<Guid> FindTemplateAsync(DemoContext d, string name)
    {
        var form = d.Config.Form("change_template");
        var id = d.Db.RecordValues.Where(v => v.FieldKey == "name" && v.Text == name && v.RowId == null &&
            d.Db.Records.Any(r => r.Id == v.RecordId && r.FormId == form.Id)).Select(v => v.RecordId).First();
        return await Task.FromResult(id);
    }

    /// <summary>Complete the open impact assessments of a change by area; areas not listed stay open.</summary>
    private static async Task CompleteAssessmentsAsync(DemoContext d, Guid changeId, Dictionary<string, (string Impact, string Text)> results)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        foreach (var ia in await records.ChildrenAsync(changeId, "impact_assessment"))
        {
            d.Db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(ia.Id);
            if (snap.KeywordKey("area") is not { } area || !results.TryGetValue(area, out var r)) continue;
            var assessor = d.Directory.Get(snap.UserId("assessor"))!.Username;
            await d.UpdateAsync(ia.Id, new() { ["impact"] = r.Impact, ["assessment"] = r.Text }, assessor);
            await d.RouteAsync(ia.Id, "completed", asUser: assessor);
        }
    }
}
