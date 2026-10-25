using Vellum.Engine.Records;
using Vellum.Modules.Change;
using Vellum.Packaging;

namespace Vellum.Modules.Audits;

/// <summary>Audits demo data: checklists, audit types, an active program with a completed audit (findings, CAPAs, recommendations), a scheduled supplier audit and more.</summary>
internal static class AuditsDemo
{
    public static async Task SeedAsync(DemoContext d)
    {
        Dictionary<string, object?> Q(string question, string clause, string hint, decimal outOf = 5, bool required = true) =>
            new() { ["question"] = question, ["clause"] = clause, ["hint"] = hint, ["out_of"] = outOf, ["required"] = required };

        // ------------------------------------------------------------------ checklist templates
        var iso75 = await d.CreateAsync("checklist_template", new()
        {
            ["name"] = "ISO 13485:2016 – Production and service provision (§7.5)", ["enabled"] = true, ["standard"] = "ISO 13485:2016", ["program"] = "quality",
            ["description"] = "Process audit of production areas. Sample at least 10 device history records per line.",
        }, "tbrooks", daysAgo: 300, rows: new()
        {
            ["questions"] =
            [
                Q("Are production activities planned, carried out and monitored under controlled conditions?", "ISO 13485:2016 §7.5.1", "Work instructions at stations, qualified equipment, monitoring records"),
                Q("Is product cleanliness controlled where required and are cleaning processes validated?", "ISO 13485:2016 §7.5.2", "Cleaning logs, validation report"),
                Q("Are installation and servicing activities documented and verified?", "ISO 13485:2016 §7.5.3", "Only if applicable", required: false),
                Q("Are special processes (welding, molding, sealing) validated and revalidated after changes?", "ISO 13485:2016 §7.5.6", "Validation master plan, revalidation triggers, change records"),
                Q("Is the sterilization process validated, with records retained for each sterilization batch?", "ISO 13485:2016 §7.5.7", "EO cycle records, parametric release"),
                Q("Is product identified throughout realization, with inspection and test status evident?", "ISO 13485:2016 §7.5.8", "Travelers, status tags, WIP labels"),
                Q("Is traceability maintained for components, materials and conditions as required?", "ISO 13485:2016 §7.5.9", "DHR component lot numbers"),
                Q("Is customer property identified, verified, protected and safeguarded?", "ISO 13485:2016 §7.5.10", "Customer-owned fixtures and tooling", required: false),
                Q("Is product preserved during handling, packaging, storage and delivery?", "ISO 13485:2016 §7.5.11", "Storage conditions, packaging validation"),
            ],
        });
        var supplierChecklist = await d.CreateAsync("checklist_template", new()
        {
            ["name"] = "Supplier quality system audit (ISO 13485 §7.4 / 21 CFR 820.50)", ["enabled"] = true, ["standard"] = "ISO 13485:2016, 21 CFR 820.50", ["program"] = "supplier",
            ["description"] = "On-site audit of component and service suppliers. Verify the quality agreement is being followed.",
        }, "tbrooks", daysAgo: 300, rows: new()
        {
            ["questions"] =
            [
                Q("Does the supplier maintain a certified QMS with current certificates?", "ISO 13485:2016 §4.1", "ISO 13485 / ISO 9001 certificate and scope"),
                Q("Are purchasing controls applied to the supplier's own sub-tier suppliers?", "ISO 13485:2016 §7.4.1", "Approved supplier list, sub-tier evaluations"),
                Q("Are incoming materials inspected and released against documented criteria?", "ISO 13485:2016 §7.4.3", "Receiving inspection records"),
                Q("Are product and process changes notified to Acme before implementation?", "Quality agreement §5", "Change notification log"),
                Q("Is nonconforming product identified, segregated and dispositioned?", "ISO 13485:2016 §8.3", "Quarantine area, MRB records"),
                Q("Are measuring instruments calibrated and traceable to national standards?", "ISO 13485:2016 §7.6", "Calibration certificates"),
                Q("Is a certificate of analysis provided and reviewed for each lot?", "Quality agreement §6", "CoA review records", required: false),
            ],
        });
        var capaChecklist = await d.CreateAsync("checklist_template", new()
        {
            ["name"] = "21 CFR 820 – CAPA subsystem (QSIT)", ["enabled"] = true, ["standard"] = "21 CFR 820.100", ["program"] = "quality",
            ["description"] = "FDA QSIT-style review of the CAPA subsystem.",
        }, "tbrooks", daysAgo: 300, rows: new()
        {
            ["questions"] =
            [
                Q("Are quality data sources analyzed to identify existing and potential causes of nonconformities?", "21 CFR 820.100(a)(1)", "Trend reports, data sources list"),
                Q("Are root-cause investigations documented and proportionate to risk?", "21 CFR 820.100(a)(2)", "Sample 5 closed CAPAs"),
                Q("Are corrective actions verified or validated for effectiveness before closure?", "21 CFR 820.100(a)(4)", "Effectiveness criteria and evidence"),
                Q("Is CAPA information disseminated and submitted for management review?", "21 CFR 820.100(a)(6)–(7)", "Management review minutes"),
            ],
        });
        var calChecklist = await d.CreateAsync("checklist_template", new()
        {
            ["name"] = "Calibration and measurement control", ["enabled"] = true, ["standard"] = "ISO 13485:2016 §7.6", ["program"] = "quality",
        }, "jlee", daysAgo: 280, rows: new()
        {
            ["questions"] =
            [
                Q("Are all measuring instruments in the calibration program with a unique ID?", "ISO 13485:2016 §7.6", "Asset register vs. instruments on the floor"),
                Q("Are calibrations performed at defined intervals against traceable standards?", "ISO 13485:2016 §7.6(a)", "Certificates, intervals"),
                Q("Are out-of-tolerance results assessed for impact on accepted product?", "ISO 13485:2016 §7.6", "OOT investigations"),
                Q("Are instruments safeguarded from adjustments that would invalidate calibration?", "ISO 13485:2016 §7.6(c)", "Seals, locked settings", required: false),
            ],
        });
        await d.CreateAsync("checklist_template", new()
        {
            ["name"] = "ISO 9001:2008 general QMS checklist (retired)", ["enabled"] = false, ["standard"] = "ISO 9001:2008", ["program"] = "quality",
            ["description"] = "Superseded by the ISO 13485:2016 checklists.",
        }, "tbrooks", daysAgo: 900, rows: new() { ["questions"] = [Q("Is there a documented quality policy?", "ISO 9001:2008 §5.3", "")] });

        // ------------------------------------------------------------------ audit types
        var internalType = await d.CreateAsync("audit_type", new()
        {
            ["name"] = "Internal QMS process audit", ["enabled"] = true, ["evaluation"] = "scoring", ["program"] = "quality", ["frequency_months"] = 12m,
            ["checklists"] = new List<Guid> { iso75.Id, capaChecklist.Id }, ["acceptable_pct"] = 85m, ["marginal_pct"] = 70m,
            ["introduction"] = "Internal audits verify that the QMS conforms to ISO 13485:2016 and 21 CFR 820 and is effectively implemented.",
            ["scoring_guidelines"] = "5 = fully effective · 4 = minor lapses · 2–3 = systematic weakness · 0–1 = requirement not met.",
        }, "tbrooks", daysAgo: 300);
        var supplierType = await d.CreateAsync("audit_type", new()
        {
            ["name"] = "Supplier audit", ["enabled"] = true, ["evaluation"] = "scoring", ["program"] = "supplier", ["frequency_months"] = 24m,
            ["checklists"] = new List<Guid> { supplierChecklist.Id }, ["acceptable_pct"] = 85m, ["marginal_pct"] = 70m,
            ["introduction"] = "Supplier audits confirm critical suppliers can consistently meet Acme's requirements and the quality agreement.",
        }, "tbrooks", daysAgo: 300);
        var calType = await d.CreateAsync("audit_type", new()
        {
            ["name"] = "Calibration program audit", ["enabled"] = true, ["evaluation"] = "normal", ["program"] = "quality", ["frequency_months"] = 12m,
            ["checklists"] = new List<Guid> { calChecklist.Id },
        }, "jlee", daysAgo: 280);

        // ------------------------------------------------------------------ previous supplier audit (2 years ago, completed)
        var past = await d.CreateAsync("audit", new()
        {
            ["subject"] = "Polymera Inc. (resin supplier) – Supplier audit 2024", ["audit_kind"] = "supplier", ["audit_type"] = supplierType.Id,
            ["auditee"] = "Polymera Inc. (resin supplier)", ["entity_kind"] = "supplier", ["lead_auditor"] = d.User("tbrooks"),
            ["start_at"] = DateTime.UtcNow.Date.AddDays(-705).AddHours(13), ["end_at"] = DateTime.UtcNow.Date.AddDays(-704).AddHours(21),
            ["criteria"] = "ISO 13485:2016 §7.4, quality agreement QA-POL-01", ["scope"] = "Resin compounding and lot release for VX-200 housings.",
        }, "tbrooks", daysAgo: 720);
        await d.RouteAsync(past.Id, "scheduled", asUser: "tbrooks");
        await DemoKit.UpdateRowsAsync(d, past.Id, "checklist", "tbrooks", (s, r) =>
        {
            var obs = r.Order == 6;
            r.SetRow(s.Form, s.Config, "classification", obs ? "observation" : "conforming");
            r.SetRow(s.Form, s.Config, "score", obs ? 4m : 5m);
            r.SetRow(s.Form, s.Config, "comments", obs ? "CoA review is by exception only; recommend 100 % review for medical-grade lots." : "Verified; records complete.");
        }, values: new()
        {
            ["result"] = "acceptable", ["frequency_rec"] = "keep",
            ["conclusions"] = "Polymera's QMS is effective. One observation on CoA review.", ["report_date"] = DemoContext.Days(-700),
        });
        await d.RouteAsync(past.Id, "report_approval", asUser: "tbrooks");
        await d.RouteAsync(past.Id, "completed", asUser: "sortiz");

        // ------------------------------------------------------------------ active audit program
        var year = DemoContext.Today.Year;
        var plan = await d.CreateAsync("audit_plan", new()
        {
            ["name"] = $"{year} internal and supplier audit program", ["program"] = "quality",
            ["period_start"] = new DateOnly(year, 1, 1), ["period_end"] = new DateOnly(year, 12, 31), ["lead_auditor"] = d.User("tbrooks"),
            ["effective_date"] = new DateOnly(year, 1, 15), ["review_date"] = new DateOnly(year, 7, 1),
            ["distribution"] = new List<int> { d.User("sortiz"), d.User("dokafor"), d.User("lgarcia") },
            ["scope"] = "All ISO 13485:2016 clauses at Kalamazoo and Boston at least once a year; critical suppliers per their risk rating.",
        }, "tbrooks", daysAgo: 290, rows: new()
        {
            ["planned_audits"] =
            [
                new() { ["auditee"] = "Final assembly – Kalamazoo", ["entity_kind"] = "process", ["audit_type"] = internalType.Id, ["planned_start"] = DemoContext.Days(-40), ["planned_end"] = DemoContext.Days(-39), ["planned_lead"] = d.User("tbrooks") },
                new() { ["auditee"] = "Calibration program – Kalamazoo", ["entity_kind"] = "department", ["audit_type"] = calType.Id, ["planned_start"] = DemoContext.Days(-12), ["planned_end"] = DemoContext.Days(-12), ["planned_lead"] = d.User("jlee") },
                new() { ["auditee"] = "EO sterilization – Steri-Pro Galway", ["entity_kind"] = "supplier", ["audit_type"] = supplierType.Id, ["planned_start"] = DemoContext.Days(-5), ["planned_end"] = DemoContext.Days(-4), ["planned_lead"] = d.User("tbrooks") },
                new() { ["auditee"] = "Polymera Inc. (resin supplier)", ["entity_kind"] = "supplier", ["audit_type"] = supplierType.Id, ["planned_start"] = DemoContext.Days(12), ["planned_end"] = DemoContext.Days(13), ["planned_lead"] = d.User("tbrooks") },
                new() { ["auditee"] = "CAPA process – Boston", ["entity_kind"] = "process", ["audit_type"] = internalType.Id, ["planned_start"] = DemoContext.Days(45), ["planned_end"] = DemoContext.Days(46), ["planned_lead"] = d.User("tbrooks") },
            ],
        });
        await d.RouteAsync(plan.Id, "approval", asUser: "tbrooks");
        await d.RouteAsync(plan.Id, "active", asUser: "sortiz", comment: "Program approved; supplier coverage matches the risk ratings.");

        var records = d.Services.GetRequiredService<RecordService>();
        async Task<Guid> ScheduleAsync(string auditee)
        {
            d.Db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(plan.Id);
            var row = snap.Rows("planned_audits").First(r => r.Values.Text("auditee") == auditee);
            await DemoKit.ActAsync(d, plan.Id, "schedule", "tbrooks", new() { ["row"] = row.Id.ToString() });
            d.Db.ChangeTracker.Clear();
            return (await records.LoadRequiredAsync(plan.Id)).Rows("planned_audits").First(r => r.Id == row.Id).CreatedRecordId!.Value;
        }

        // 1. Completed internal audit with findings, CAPAs and recommendations.
        var a1 = await ScheduleAsync("Final assembly – Kalamazoo");
        await DemoKit.UpdateRowsAsync(d, a1, "participants", "tbrooks", null, add:
        [
            new() { ["participant"] = d.User("tbrooks"), ["role"] = "lead_auditor" },
            new() { ["participant"] = d.User("jlee"), ["role"] = "auditor" },
            new() { ["participant"] = d.User("lgarcia"), ["role"] = "auditee" },
            new() { ["participant"] = d.User("akim"), ["role"] = "auditee" },
        ], values: new() { ["criteria"] = "ISO 13485:2016 §7.5, 21 CFR 820.100, SOP-PRD-002" });
        await DemoKit.UpdateRowsAsync(d, a1, "documents_reviewed", "tbrooks", null, add:
        [
            new() { ["doc_ref"] = "WI-ASM-014 Final assembly – VX-200", ["doc_revision"] = "C" },
            new() { ["doc_ref"] = "SOP-PRD-002 Device history records", ["doc_revision"] = "F" },
            new() { ["doc_ref"] = "VR-2025-019 Ultrasonic weld validation (WLD-02)", ["doc_revision"] = "A" },
        ]);
        (string Cls, decimal Score, string Comment)[] answers =
        [
            ("conforming", 5, "WI-ASM-014 rev C posted at all stations; torque drivers calibrated."),
            ("conforming", 4, "Cleaning log complete; cleaning validation CV-2025-02 current."),
            ("conforming", 5, "Not applicable: no installation or servicing at this site."),
            ("minor_nc", 2, "Weld process WLD-02 not revalidated after the horn was replaced in May (maintenance record MR-2026-118)."),
            ("conforming", 5, "EO records for lots 26-0301 to 26-0340 reviewed; parametric release complete."),
            ("conforming", 4, "Status tags in use; two WIP carts without travelers corrected during the audit."),
            ("major_nc", 1, "3 of 10 DHRs sampled (lots 26-0301, -0302, -0303) lack the Y-connector component lot number: traceability can't be shown."),
            ("conforming", 5, "Customer-owned test fixtures labeled and logged."),
            ("observation", 4, "Finished goods stored next to dock door 3; warehouse temperature logged but not trended."),
            ("conforming", 5, "Quarterly quality data review QDR-2026-Q2 covers all sources."),
            ("conforming", 4, "5-why and fishbone used; depth proportionate to risk."),
            ("ofi", 4, "Effectiveness checks use a fixed 30-day window; consider risk-based durations."),
            ("conforming", 5, "CAPA metrics presented in management review MR-2026-01."),
        ];
        await DemoKit.UpdateRowsAsync(d, a1, "checklist", "tbrooks", (s, r) =>
        {
            if (r.Order >= answers.Length) return;
            var (cls, score, comment) = answers[r.Order];
            r.SetRow(s.Form, s.Config, "classification", cls);
            r.SetRow(s.Form, s.Config, "score", score);
            r.SetRow(s.Form, s.Config, "comments", comment);
        });
        d.Db.ChangeTracker.Clear();
        var a1snap = await records.LoadRequiredAsync(a1);
        Guid RowOf(string cls) => a1snap.Rows("checklist").First(r => a1snap.KeywordKey(r, "classification") == cls).Id;
        await DemoKit.ActAsync(d, a1, "raise_capa", "tbrooks", new() { ["row"] = RowOf("major_nc").ToString(), ["owner"] = d.User("lgarcia").ToString(), ["priority"] = "high" });
        await DemoKit.ActAsync(d, a1, "raise_capa", "tbrooks", new() { ["row"] = RowOf("minor_nc").ToString(), ["owner"] = d.User("akim").ToString(), ["priority"] = "medium" });
        await DemoKit.ActAsync(d, a1, "raise_rec", "tbrooks", new() { ["row"] = RowOf("observation").ToString(), ["assignee"] = d.User("lgarcia").ToString(), ["due"] = DemoContext.Days(20).ToString("yyyy-MM-dd") });
        await DemoKit.ActAsync(d, a1, "raise_rec", "tbrooks", new() { ["row"] = RowOf("ofi").ToString(), ["assignee"] = d.User("sortiz").ToString(), ["due"] = DemoContext.Days(45).ToString("yyyy-MM-dd") });
        await d.UpdateAsync(a1, new()
        {
            ["result"] = "marginal", ["frequency_rec"] = "increase", ["report_date"] = DemoContext.Days(-36),
            ["conclusions"] = "Production controls are generally effective, but DHR traceability for the Y-connector is not assured (major) and the weld process was not revalidated after maintenance (minor). Re-audit final assembly in 6 months.",
            ["frequency_comments"] = "Re-audit in Q2 to verify CAPA effectiveness.",
        }, "tbrooks");
        await d.RouteAsync(a1, "report_approval", asUser: "tbrooks");
        await d.RouteAsync(a1, "completed", asUser: "sortiz", comment: "Report approved. CAPAs raised for both nonconformances.");
        // The observation's recommendation is already done; the OFI stays open.
        var recs = await records.ChildrenAsync(a1, "recommendation");
        if (recs.Count > 0)
        {
            await d.UpdateAsync(recs[0].Id, new() { ["action_taken"] = "Moved finished goods away from dock door 3 and added monthly temperature trending to the warehouse report.", ["completed_on"] = DemoContext.Days(-3) }, "lgarcia");
            await d.RouteAsync(recs[0].Id, "completed", asUser: "lgarcia");
        }
        // The CAPA for the major nonconformance moves on to initial review.
        var capas = d.Db.Links.Where(l => l.FromId == a1 && l.FieldKey != null && l.FieldKey.StartsWith("finding:")).Select(l => l.ToId).ToList();
        if (capas.Count > 0) await d.RouteAsync(capas[0], "initial_review", asUser: "tbrooks");

        // 2. Calibration audit (normal evaluation) waiting for report approval.
        var a2 = await ScheduleAsync("Calibration program – Kalamazoo");
        await DemoKit.UpdateRowsAsync(d, a2, "participants", "jlee", null, add:
        [
            new() { ["participant"] = d.User("jlee"), ["role"] = "lead_auditor" },
            new() { ["participant"] = d.User("ebaker"), ["role"] = "auditee" },
        ]);
        await DemoKit.UpdateRowsAsync(d, a2, "checklist", "jlee", (s, r) =>
        {
            var obs = r.Order == 3;
            r.SetRow(s.Form, s.Config, "classification", obs ? "observation" : "conforming");
            r.SetRow(s.Form, s.Config, "comments", obs ? "Tamper seals missing on 2 of 40 torque drivers; replaced during the audit." : "Sampled 40 instruments: all in the register, calibrated and traceable.");
        }, values: new()
        {
            ["result"] = "acceptable", ["frequency_rec"] = "keep", ["report_date"] = DemoContext.Days(-8),
            ["conclusions"] = "The calibration program is effective. One observation on tamper seals.",
        });
        d.Db.ChangeTracker.Clear();
        var a2snap = await records.LoadRequiredAsync(a2);
        var obsRow = a2snap.Rows("checklist").First(r => a2snap.KeywordKey(r, "classification") == "observation").Id;
        await DemoKit.ActAsync(d, a2, "raise_rec", "jlee", new() { ["row"] = obsRow.ToString(), ["assignee"] = d.User("ebaker").ToString(), ["due"] = DemoContext.Days(14).ToString("yyyy-MM-dd") });
        await d.RouteAsync(a2, "report_approval", asUser: "jlee");

        // 3. Scheduled supplier audit (Polymera), with the team and documents set up.
        var a3 = await ScheduleAsync("Polymera Inc. (resin supplier)");
        await DemoKit.UpdateRowsAsync(d, a3, "participants", "tbrooks", null, add:
        [
            new() { ["participant"] = d.User("tbrooks"), ["role"] = "lead_auditor" },
            new() { ["participant"] = d.User("cwu"), ["role"] = "auditor" },
            new() { ["external_name"] = "Dan Whitfield", ["company"] = "Polymera Inc.", ["role"] = "auditee" },
        ], values: new()
        {
            ["previous_audit"] = past.Id, ["criteria"] = "ISO 13485:2016 §7.4, quality agreement QA-POL-01 rev 3",
            ["scope"] = "Resin compounding, lot release and change notification for the Lexan HPX4 grade proposed in the VX-200 housing change.",
        });
        await DemoKit.UpdateRowsAsync(d, a3, "documents_reviewed", "tbrooks", null, add:
        [
            new() { ["doc_ref"] = "Polymera Quality Manual QM-01", ["doc_revision"] = "7" },
            new() { ["doc_ref"] = "Quality agreement QA-POL-01", ["doc_revision"] = "3" },
        ]);

        // 4. An unplanned audit in Draft, linked to the plan as an additional audit.
        var a4 = await d.CreateAsync("audit", new()
        {
            ["subject"] = "Document control process – Boston", ["audit_kind"] = "internal", ["audit_type"] = internalType.Id,
            ["auditee"] = "Document control – Boston", ["entity_kind"] = "process", ["lead_auditor"] = d.User("tbrooks"),
            ["start_at"] = DateTime.UtcNow.Date.AddDays(20).AddHours(13), ["end_at"] = DateTime.UtcNow.Date.AddDays(20).AddHours(21),
            ["scope"] = "Unplanned audit requested after FDA observation 483-2 on document change records.",
        }, "tbrooks", daysAgo: 2);
        await d.UpdateAsync(plan.Id, new() { ["additional_audits"] = new List<Guid> { a4.Id } }, "tbrooks");
    }
}
