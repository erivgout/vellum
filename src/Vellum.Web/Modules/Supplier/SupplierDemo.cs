using System.Globalization;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Supplier;

/// <summary>Demo data for Acme Medical Devices' supply base: materials, suppliers, scorecards over four quarters and receiving lots.</summary>
public static class SupplierDemo
{
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static async Task SeedAsync(DemoContext d)
    {
        // ------------------------------------------------------------- materials (SUP-010..012)
        async Task<Guid> Material(Dictionary<string, object?> v, string owner, int daysAgo, bool approve = true)
        {
            v["owner"] = d.User(owner);
            var r = await d.CreateAsync("material", v, owner, daysAgo: daysAgo);
            if (!approve) return r.Id;
            await d.RouteAsync(r.Id, "approval");
            await d.RouteAsync(r.Id, "approved");
            return r.Id;
        }

        var resin = await Material(new()
        {
            ["part_number"] = "RES-PC-2458", ["description"] = "Medical-grade polycarbonate resin, natural (USP Class VI)", ["commodity"] = "resin",
            ["spec_ref"] = "SPEC-RM-0021 rev D", ["uom"] = "KG", ["sampling_plan"] = "coa_review", ["accept_number"] = 0m, ["coa_required"] = true,
            ["inspector"] = d.User("cwu"), ["inspection_criteria"] = "Verify CoA against SPEC-RM-0021: melt flow rate 18–22 g/10 min (300 °C/1.2 kg), moisture ≤ 0.02%, USP Class VI statement. Check lot labels and seal integrity of each gaylord.",
        }, "cwu", 400);
        var housingTop = await Material(new()
        {
            ["part_number"] = "HSG-200-T", ["description"] = "VX-200 pump housing, top (PC, light gray)", ["commodity"] = "molded",
            ["spec_ref"] = "DWG-200-114 rev C", ["sampling_plan"] = "z14_l2_10", ["accept_number"] = 1m, ["coa_required"] = false,
            ["inspector"] = d.User("cwu"), ["skip_initial"] = 3m, ["skip_count"] = 2m, ["skip_requalify"] = 5m,
            ["inspection_criteria"] = "Critical dimensions A–F per DWG-200-114 (CMM on 5 pcs). Visual: no short shots, flash ≤ 0.1 mm, no sink on the display bezel. Snap-fit engagement check with HSG-200-B.",
        }, "akim", 380);
        var housingBottom = await Material(new()
        {
            ["part_number"] = "HSG-200-B", ["description"] = "VX-200 pump housing, bottom (PC, light gray)", ["commodity"] = "molded",
            ["spec_ref"] = "DWG-200-115 rev C", ["sampling_plan"] = "z14_l2_10", ["accept_number"] = 1m, ["coa_required"] = false,
            ["inspector"] = d.User("cwu"), ["inspection_criteria"] = "Critical dimensions per DWG-200-115, gasket groove depth 1.20 ± 0.05 mm, boss inner diameter go/no-go.",
        }, "akim", 380);
        var pouch = await Material(new()
        {
            ["part_number"] = "PKG-1073-812", ["description"] = "Tyvek 1073B / PET-PE peel pouch, 8 × 12 in", ["commodity"] = "packaging",
            ["spec_ref"] = "SPEC-PK-0007 rev B", ["sampling_plan"] = "z14_l2_065", ["accept_number"] = 0m, ["coa_required"] = true,
            ["inspector"] = d.User("hmoreau"), ["skip_initial"] = 3m, ["skip_count"] = 3m, ["skip_requalify"] = 5m,
            ["inspection_criteria"] = "Seal width ≥ 6 mm, no channels or wrinkles in the seal, peel strength per ASTM F88 on 5 pouches, print legibility.",
        }, "jlee", 360);
        var pcba = await Material(new()
        {
            ["part_number"] = "PCBA-200-CTL", ["description"] = "VX-200 control board assembly (rev F)", ["commodity"] = "electronic",
            ["spec_ref"] = "ASSY-200-300 rev F", ["sampling_plan"] = "full", ["accept_number"] = 0m, ["coa_required"] = true, ["skip_lot_allowed"] = false,
            ["inspector"] = d.User("cwu"), ["inspection_criteria"] = "100% functional test on fixture FT-200 (occlusion sensor channel, alarm driver, battery management). Visual per IPC-A-610 Class 3 on 50 boards.",
        }, "akim", 350);
        var shaft = await Material(new()
        {
            ["part_number"] = "SFT-316-04", ["description"] = "316L stainless drive shaft, Ø 4 mm × 38 mm", ["commodity"] = "machined",
            ["spec_ref"] = "DWG-200-131 rev A", ["sampling_plan"] = "z14_l2_065", ["accept_number"] = 0m, ["coa_required"] = true,
            ["inspector"] = d.User("cwu"), ["inspection_criteria"] = "Diameter 4.000 +0/−0.008 mm, Ra ≤ 0.4 µm, material cert (316L, passivated per ASTM A967).",
        }, "akim", 300);
        var ipa = await Material(new()
        {
            ["part_number"] = "CHM-IPA-999", ["description"] = "Isopropyl alcohol 99.9%, USP grade", ["commodity"] = "chemical", ["uom"] = "L",
            ["cas_registered"] = true, ["cas_number"] = "67-63-0", ["ca_index_name"] = "2-Propanol", ["synonyms"] = "IPA; isopropanol; 2-propanol",
            ["ghs"] = new[] { "flammable", "irritant" },
            ["hazard_summary"] = "Highly flammable liquid and vapour (H225). Causes serious eye irritation (H319). May cause drowsiness or dizziness (H336).",
            ["osha_hazcom"] = "Flammable liquid Cat. 2; eye irritation Cat. 2A; STOT SE Cat. 3. Ground and bond containers, use with local exhaust; safety glasses and nitrile gloves.",
            ["sds_link"] = "https://sds.chemsource.example.com/ipa-999.pdf", ["sampling_plan"] = "coa_review", ["coa_required"] = true, ["inspector"] = d.User("cwu"),
        }, "mchen", 280);
        var adhesive = await Material(new()
        {
            ["part_number"] = "CHM-CA-4011", ["description"] = "Medical-grade ethyl cyanoacrylate adhesive, low viscosity", ["commodity"] = "chemical", ["uom"] = "EA",
            ["cas_registered"] = true, ["cas_number"] = "7085-85-0", ["ca_index_name"] = "2-Propenoic acid, 2-cyano-, ethyl ester", ["synonyms"] = "Ethyl 2-cyanoacrylate; ECA",
            ["ghs"] = new[] { "irritant" },
            ["hazard_summary"] = "Bonds skin and eyes in seconds (EUH202). Causes skin and serious eye irritation (H315, H319). May cause respiratory irritation (H335).",
            ["osha_hazcom"] = "Skin irritation Cat. 2; eye irritation Cat. 2; STOT SE Cat. 3. Dispense with the automated dispenser only; acetone available for skin bonding.",
            ["sds_link"] = "https://sds.chemsource.example.com/ca-4011.pdf", ["sampling_plan"] = "coa_review", ["coa_required"] = true, ["inspector"] = d.User("cwu"),
        }, "mchen", 260);
        var eo = await Material(new()
        {
            ["part_number"] = "SVC-EO-STD", ["description"] = "EO sterilization, validated standard cycle (SAL 10⁻⁶)", ["commodity"] = "service",
            ["sampling_plan"] = "coa_review", ["coa_required"] = true, ["skip_lot_allowed"] = false, ["inspector"] = d.User("jlee"),
            ["inspection_criteria"] = "Certificate of sterilization, BI results (all negative), cycle parameters within validated limits, EO residuals per ISO 10993-7.",
        }, "jlee", 250);
        await Material(new()
        {
            ["part_number"] = "LBL-200-SET", ["description"] = "VX-200 device label and UDI set", ["commodity"] = "labeling",
            ["spec_ref"] = "ART-200-020 rev B", ["sampling_plan"] = "z14_s3_25", ["inspector"] = d.User("cwu"),
        }, "cwu", 6, approve: false);

        // ------------------------------------------------------------- suppliers (SUP-001)
        Dictionary<string, object?> Contact(string name, string role, string email, string phone, bool notify) =>
            new() { ["contact_name"] = name, ["contact_role"] = role, ["email"] = email, ["phone"] = phone, ["notify_ratings"] = notify };
        Dictionary<string, object?> Cert(string std, string no, string body, int days) =>
            new() { ["standard"] = std, ["cert_number"] = no, ["registrar"] = body, ["expires_on"] = DemoContext.Days(days) };
        Dictionary<string, object?> Supplied(Guid material, string part, int lead) =>
            new() { ["material"] = material, ["supplier_part"] = part, ["lead_time"] = (decimal)lead };

        async Task<Guid> Supplier(Dictionary<string, object?> v, int daysAgo, List<Dictionary<string, object?>> contacts,
            List<Dictionary<string, object?>> certs, List<Dictionary<string, object?>> supplied)
        {
            v.TryAdd("owner", d.User("cwu"));
            var r = await d.CreateAsync("supplier", v, "cwu", daysAgo: daysAgo, rows: new()
            {
                ["contacts"] = contacts, ["certifications"] = certs, ["supplied_materials"] = supplied,
            });
            return r.Id;
        }
        async Task Approve(Guid id, string scope, int requalifyDays, string by = "sortiz", bool conditional = false, string? conditions = null)
        {
            var inputs = new Dictionary<string, string> { ["scope"] = scope, ["requalify_on"] = Iso(DemoContext.Days(requalifyDays)) };
            if (conditions != null) inputs["conditions"] = conditions;
            await DemoActions.RunAsync(d, id, conditional ? "approve_conditional" : "approve", by, inputs);
        }

        const string polymeraScope = "Medical-grade polycarbonate and PC/ABS resins (USP Class VI, ISO 10993 tested) for injection-molded device housings.";
        var polymera = await Supplier(new()
        {
            ["name"] = "Polymera Resins Inc.", ["code"] = "V-10421", ["supplier_type"] = "raw_material", ["risk_class"] = "high",
            ["address"] = "4120 Patterson Ave SE\nGrand Rapids, MI 49512", ["country"] = "USA", ["website"] = "https://polymera.example.com", ["scope"] = polymeraScope,
            ["qual_methods"] = new[] { "questionnaire", "cert_review", "history" }, ["qual_date"] = DemoContext.Days(-900), ["qual_audit_score"] = 88m,
            ["quality_agreement"] = true, ["quality_agreement_on"] = DemoContext.Days(-880),
            ["qual_summary"] = "Long-standing resin source. Questionnaire QS-114 complete, ISO 9001 certificate verified with BSI, three years of CoA history with no out-of-specification lots before 2026.",
        }, 900,
        [
            Contact("Dana Whitfield", "Quality Manager", "d.whitfield@polymera.example.com", "+1 616 555 0142", true),
            Contact("Marco Ruiz", "Account Manager", "m.ruiz@polymera.example.com", "+1 616 555 0177", false),
        ],
        [
            Cert("iso_9001", "US-QMS-44120", "BSI", -410),
            Cert("iso_9001", "US-QMS-44120-R1", "BSI", 690),
            Cert("iso_14001", "US-EMS-2231", "BSI", 300),
        ],
        [Supplied(resin, "Makroblend M-2458 NT", 21)]);
        await Approve(polymera, polymeraScope, 640);

        const string pmcScope = "Precision injection molding of VX-200 housing components in an ISO class 8 cleanroom, including insert molding and ultrasonic welding.";
        var precision = await Supplier(new()
        {
            ["name"] = "Precision Molding Co.", ["code"] = "V-10388", ["supplier_type"] = "component", ["risk_class"] = "high",
            ["address"] = "770 Industrial Pkwy\nPortage, MI 49002", ["country"] = "USA", ["website"] = "https://precisionmolding.example.com", ["scope"] = pmcScope,
            ["qual_methods"] = new[] { "onsite_audit", "first_article" }, ["qual_date"] = DemoContext.Days(-620), ["qual_audit_score"] = 92m,
            ["quality_agreement"] = true, ["quality_agreement_on"] = DemoContext.Days(-600),
            ["qual_summary"] = "On-site audit SA-2025-03 (score 92%, two minor findings closed). First article inspection of HSG-200-T/B passed (FAI-0412, FAI-0413). Process validation IQ/OQ/PQ reviewed for tools T-55120-1 and -2.",
        }, 620,
        [
            Contact("Greg Halvorsen", "Plant Quality Lead", "g.halvorsen@precisionmolding.example.com", "+1 269 555 0110", true),
            Contact("Aisha Bello", "Customer Service", "a.bello@precisionmolding.example.com", "+1 269 555 0131", false),
        ],
        [Cert("iso_13485", "MD 612044", "TÜV SÜD", 540), Cert("iso_9001", "Q 612045", "TÜV SÜD", 540)],
        [Supplied(housingTop, "PMC-55120-T", 28), Supplied(housingBottom, "PMC-55120-B", 28)]);
        await Approve(precision, pmcScope, 475);

        const string steriScope = "Medical sterile barrier systems: Tyvek and paper peel pouches, header bags and thermoformed trays (ISO 11607-1).";
        var steripack = await Supplier(new()
        {
            ["name"] = "SteriPack Ltd.", ["code"] = "V-20117", ["supplier_type"] = "packaging", ["risk_class"] = "high",
            ["address"] = "Unit 4, Mervue Business Park\nGalway H91 R2TX", ["country"] = "Ireland", ["website"] = "https://steripack.example.com", ["scope"] = steriScope,
            ["qual_methods"] = new[] { "remote_audit", "cert_review" }, ["qual_date"] = DemoContext.Days(-540), ["qual_audit_score"] = 95m,
            ["quality_agreement"] = true, ["quality_agreement_on"] = DemoContext.Days(-530),
            ["qual_summary"] = "Remote audit RA-2025-01 (95%). Packaging validation (ISO 11607-2) data reviewed for the 8 × 12 in pouch; seal strength and dye penetration results within limits.",
        }, 540,
        [Contact("Niamh O'Connell", "Regulatory & Quality Director", "n.oconnell@steripack.example.com", "+353 91 555 210", true)],
        [Cert("iso_13485", "IE-MD-3391", "NSAI", 21), Cert("iso_14001", "IE-EMS-1180", "NSAI", 460)],
        [Supplied(pouch, "SP-1073-0812", 35)]);
        await Approve(steripack, steriScope, 400);

        const string galwayScope = "PCBA manufacture and functional test of VX-200 control boards to IPC-A-610 Class 3.";
        var galway = await Supplier(new()
        {
            ["name"] = "Galway Electronics Ltd.", ["code"] = "V-20140", ["supplier_type"] = "contract_mfg", ["risk_class"] = "critical",
            ["address"] = "Ballybrit Business Park\nGalway H91 X2P8", ["country"] = "Ireland", ["scope"] = galwayScope,
            ["qual_methods"] = new[] { "onsite_audit" }, ["qual_date"] = DemoContext.Days(-150), ["qual_audit_score"] = 74m, ["quality_agreement"] = false,
            ["qual_summary"] = "On-site audit SA-2026-02 scored 74%: major finding on solder joint workmanship training records, minor on ESD controls. SCAR-2026-07 issued. Sole qualified source for PCBA-200-CTL, so approved conditionally.",
        }, 160,
        [
            Contact("Eoin Byrne", "Quality Engineer", "e.byrne@galwayelectronics.example.com", "+353 91 555 487", true),
            Contact("Siobhán Kelly", "Operations Director", "s.kelly@galwayelectronics.example.com", "+353 91 555 400", false),
        ],
        [Cert("iso_13485", "IE-MD-4410", "NSAI", 300), Cert("iso_9001", "IE-QMS-4411", "NSAI", 300)],
        [Supplied(pcba, "GE-CTL-200F", 42)]);
        await Approve(galway, galwayScope, 140, conditional: true,
            conditions: "100% functional test and IPC-A-610 Class 3 visual inspection at receiving until SCAR-2026-07 is closed and the quality agreement is signed.");

        const string midwestScope = "CNC machining of 316L stainless shafts and pins, with passivation per ASTM A967.";
        var midwest = await Supplier(new()
        {
            ["name"] = "Midwest Precision Machining", ["code"] = "V-10502", ["supplier_type"] = "component", ["risk_class"] = "medium",
            ["address"] = "2300 E Kilgore Rd\nKalamazoo, MI 49001", ["country"] = "USA", ["scope"] = midwestScope,
            ["qual_methods"] = new[] { "questionnaire", "first_article" }, ["qual_date"] = DemoContext.Days(-330),
            ["qual_summary"] = "Questionnaire QS-131 and FAI-0450 of SFT-316-04 passed. Medium risk: no on-site audit required.",
        }, 330,
        [Contact("Tom Kowalski", "Quality Manager", "t.kowalski@midwestpm.example.com", "+1 269 555 0190", true)],
        [Cert("iso_9001", "C-88731", "Intertek", 250)],
        [Supplied(shaft, "MPM-316-0438", 15)]);
        await Approve(midwest, midwestScope, 760);

        const string northScope = "Ethylene oxide sterilization of finished VX-200 accessory kits, validated per ISO 11135 (overkill half-cycle).";
        var northstar = await Supplier(new()
        {
            ["name"] = "NorthStar Sterilization Services", ["code"] = "V-10611", ["supplier_type"] = "service", ["risk_class"] = "critical",
            ["address"] = "1800 Northstar Dr\nCoon Rapids, MN 55433", ["country"] = "USA", ["scope"] = northScope,
            ["qual_methods"] = new[] { "onsite_audit", "cert_review" }, ["qual_date"] = DemoContext.Days(-300), ["qual_audit_score"] = 96m,
            ["quality_agreement"] = true, ["quality_agreement_on"] = DemoContext.Days(-290),
            ["w_quality"] = 50m, ["w_delivery"] = 20m, ["w_response"] = 10m, ["w_audit"] = 20m,
            ["qual_summary"] = "On-site audit SA-2025-09 (96%). Sterilization validation report SVR-0088 reviewed; annual requalification and dose audits current.",
        }, 300,
        [Contact("Rachel Lindqvist", "Validation Manager", "r.lindqvist@northstar-steri.example.com", "+1 763 555 0122", true)],
        [Cert("iso_13485", "US-MD-77310", "BSI", 420), Cert("iso_11135", "US-EO-77311", "BSI", 420)],
        [Supplied(eo, "NS-EO-STD-12", 7)]);
        await Approve(northstar, northScope, 65 + 300);

        const string chemScope = "Process chemicals: USP-grade solvents, cleaning agents and medical-grade adhesives, each lot with a CoA.";
        var chemsource = await Supplier(new()
        {
            ["name"] = "ChemSource Solutions", ["code"] = "V-10650", ["supplier_type"] = "distributor", ["risk_class"] = "medium",
            ["address"] = "55 Commerce Dr\nBattle Creek, MI 49037", ["country"] = "USA", ["scope"] = chemScope,
            ["qual_methods"] = new[] { "questionnaire", "cert_review" }, ["qual_date"] = DemoContext.Days(-260),
            ["qual_summary"] = "Distributor of record for IPA and cyanoacrylate. Manufacturer CoAs and SDSs verified; storage and FIFO controls reviewed in questionnaire QS-140.",
        }, 260,
        [Contact("Priya Raman", "Technical Sales", "p.raman@chemsource.example.com", "+1 269 555 0155", false)],
        [Cert("iso_9001", "US-QMS-55001", "NQA", 600)],
        [Supplied(ipa, "CS-IPA-USP-20L", 5), Supplied(adhesive, "CS-ECA-4011-20G", 10)]);
        await Approve(chemsource, chemScope, 800);

        await Supplier(new()
        {
            ["name"] = "Apex Labels & Print", ["code"] = "V-10715", ["supplier_type"] = "component", ["risk_class"] = "medium",
            ["address"] = "910 Gull Rd\nKalamazoo, MI 49048", ["country"] = "USA",
            ["scope"] = "Printed device labels, UDI labels and IFU booklets.",
            ["qual_methods"] = new[] { "questionnaire" },
            ["qual_summary"] = "Questionnaire QS-152 returned; print sample evaluation of the VX-200 label set is pending artwork approval.",
        }, 12,
        [Contact("Jenna Morales", "Customer Quality", "j.morales@apexlabels.example.com", "+1 269 555 0168", true)],
        [Cert("iso_9001", "US-QMS-60212", "SGS", 480)],
        []);

        const string lakeScope = "Injection molding of VX-200 housing components (former source).";
        var lakeshore = await Supplier(new()
        {
            ["name"] = "Lakeshore Plastics LLC", ["code"] = "V-09877", ["supplier_type"] = "component", ["risk_class"] = "high",
            ["address"] = "120 Harbor Dr\nSt. Joseph, MI 49085", ["country"] = "USA", ["scope"] = lakeScope,
            ["qual_methods"] = new[] { "onsite_audit" }, ["qual_date"] = DemoContext.Days(-1100), ["qual_audit_score"] = 81m,
            ["quality_agreement"] = true, ["quality_agreement_on"] = DemoContext.Days(-1090),
            ["qual_summary"] = "Qualified in 2023 as the original VX-200 housing molder.",
        }, 1100,
        [Contact("Bill Hendricks", "General Manager", "b.hendricks@lakeshoreplastics.example.com", "+1 269 555 0101", false)],
        [Cert("iso_9001", "US-QMS-30098", "NQA", 200)],
        [Supplied(housingTop, "LP-200-TOP", 30)]);
        await Approve(lakeshore, lakeScope, 365);
        await DemoActions.RunAsync(d, lakeshore, "disqualify", "dokafor",
            comment: "Three rejected housing lots in Q1 2026 (short shots and flash) and SCAR-2026-02 unanswered after 60 days. Molding moved to Precision Molding Co.");

        // ------------------------------------------------------------- scorecards: four quarters (SUP §15.5)
        var (lastStart, _) = SupplierBehavior.LastQuarter(DemoContext.Today);
        var quarters = Enumerable.Range(0, 4).Select(i => lastStart.AddMonths(-3 * (3 - i))).ToList();
        async Task Scores(Guid supplier, string by, params (decimal Ppm, decimal Otd, decimal Resp, decimal? Audit, string? Comment)[] q)
        {
            for (var i = 0; i < q.Length; i++)
            {
                var start = quarters[i];
                var end = start.AddMonths(3).AddDays(-1);
                var r = await d.CreateAsync("scorecard", new()
                {
                    ["supplier"] = supplier, ["period_start"] = start, ["period_end"] = end, ["ppm"] = q[i].Ppm, ["otd_pct"] = q[i].Otd,
                    ["responsiveness"] = q[i].Resp, ["audit_score"] = q[i].Audit, ["comments"] = q[i].Comment,
                }, by, daysAgo: Math.Max(1, DemoContext.Today.DayNumber - end.DayNumber - 10));
                var draftLast = i == q.Length - 1 && supplier == midwest;
                if (!draftLast) await d.RouteAsync(r.Id, "published");
            }
        }
        await Scores(polymera, "cwu",
            (380m, 95.0m, 4m, null, null), (310m, 96.5m, 4m, 86m, "Desk audit in March: two CoA template findings closed."),
            (1450m, 88.0m, 3m, null, "Three resin lots arrived without CoAs; one lot outside moisture specification. See CAPA on missing CoAs."),
            (350m, 95.5m, 4m, null, "Back on track after CoA process change."));
        await Scores(precision, "cwu",
            (150m, 97.0m, 5m, null, null), (120m, 98.0m, 5m, 92m, "Surveillance audit SA-2026-01: no findings."),
            (260m, 95.0m, 4m, null, "One lot with flash above limit, contained at the supplier."), (90m, 98.5m, 5m, null, null));
        await Scores(steripack, "cwu",
            (60m, 99.0m, 5m, null, null), (40m, 98.5m, 5m, 95m, null), (80m, 97.0m, 4m, null, null),
            (110m, 96.0m, 4m, null, "ISO 13485 certificate renewal due next month: renewed certificate requested."));
        await Scores(galway, "cwu",
            (2600m, 85.0m, 3m, null, "Solder bridging escapes on two lots."), (3100m, 82.0m, 2m, 74m, "Audit SA-2026-02: major finding; SCAR-2026-07 issued."),
            (1800m, 88.0m, 3m, null, "Containment (100% AOI) effective; root cause analysis accepted."), (1200m, 91.0m, 4m, null, "Improving: corrective actions verified on two lots."));
        await Scores(midwest, "cwu",
            (500m, 92.0m, 4m, null, null), (420m, 93.0m, 4m, null, null), (380m, 95.0m, 4m, null, null), (300m, 94.0m, 4m, null, "Draft: waiting for the Q3 delivery report."));
        await Scores(northstar, "jlee",
            (0m, 99.0m, 5m, null, null), (0m, 100m, 5m, 96m, "Annual audit SA-2026-03 (96%)."), (0m, 98.0m, 4m, null, null), (0m, 100m, 5m, null, null));

        // ------------------------------------------------------------- receiving (RCV-003..006), oldest first per supplier and material
        var lotSeq = 400;
        async Task<Guid> Lot(Guid supplier, Guid material, int daysAgo, decimal qty, string prefix, bool coa = true, string by = "nflores")
        {
            var r = await d.CreateAsync("receiving", new()
            {
                ["po_number"] = $"PO-26{4100 + lotSeq:0000}", ["received_on"] = DemoContext.Days(-daysAgo), ["lot_number"] = $"{prefix}-{lotSeq++:0000}",
                ["quantity"] = qty, ["supplier"] = supplier, ["material"] = material, ["coa_received"] = coa,
            }, by, daysAgo: daysAgo);
            return r.Id;
        }
        async Task Inspect(Guid lot, decimal sample, decimal defects, string? notes = null, bool close = true)
        {
            var records = d.Services.GetRequiredService<RecordService>();
            d.Db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(lot);
            if (snap.KeywordKey("inspection_status") != "required") throw new InvalidOperationException($"{snap.Record.Number} expected an inspection: {snap.Text("status_basis")}");
            await d.RouteAsync(lot, "inspection");
            d.Db.ChangeTracker.Clear();
            var inspector = d.Directory.Get((await records.LoadRequiredAsync(lot)).UserId("inspector"))!.Username;
            await d.UpdateAsync(lot, new() { ["sample_size"] = sample, ["defects_found"] = defects, ["inspection_notes"] = notes }, inspector);
            if (close) await d.RouteAsync(lot, "accepted");
        }
        async Task Release(Guid lot)
        {
            d.Db.ChangeTracker.Clear();
            var snap = await d.Services.GetRequiredService<RecordService>().LoadRequiredAsync(lot);
            if (snap.KeywordKey("inspection_status") != "skip") throw new InvalidOperationException($"{snap.Record.Number} expected skip-lot: {snap.Text("status_basis")}");
            await d.RouteAsync(lot, "released");
        }

        // Precision Molding, housing top: qualify (3 passes), skip 2, inspect, skip, and one waiting for release.
        foreach (var days in new[] { 95, 80, 66 }) await Inspect(await Lot(precision, housingTop, days, 1200m, "PM26"), 80m, 0m, "All dimensions within tolerance; no cosmetic defects.");
        foreach (var days in new[] { 52, 40 }) await Release(await Lot(precision, housingTop, days, 1200m, "PM26"));
        await Inspect(await Lot(precision, housingTop, 30, 1500m, "PM26"), 80m, 1m, "One part with flash 0.12 mm at the parting line (accept number 1). Supplier informed.");
        await Release(await Lot(precision, housingTop, 12, 1200m, "PM26"));
        await Lot(precision, housingTop, 3, 1200m, "PM26");
        // Precision Molding, housing bottom: two passes, the third in inspection.
        foreach (var days in new[] { 60, 45 }) await Inspect(await Lot(precision, housingBottom, days, 1200m, "PM26"), 80m, 0m);
        var bottomOpen = await Lot(precision, housingBottom, 4, 1200m, "PM26");
        await d.RouteAsync(bottomOpen, "inspection");
        // Polymera resin: two passes, then a lot without a CoA waiting in receipt.
        foreach (var days in new[] { 70, 50 }) await Inspect(await Lot(polymera, resin, days, 2000m, "PR"), 1m, 0m, "CoA reviewed: MFR 20.1 g/10 min, moisture 0.012%.");
        await Lot(polymera, resin, 2, 2000m, "PR", coa: false);
        // SteriPack pouches: qualified, three skipped, the next one in inspection with QC.
        foreach (var days in new[] { 88, 74, 61 }) await Inspect(await Lot(steripack, pouch, days, 10000m, "SPK"), 125m, 0m, "Seal width 7.1–7.6 mm; peel strength 2.8–3.4 N/15 mm.");
        foreach (var days in new[] { 47, 33, 15 }) await Release(await Lot(steripack, pouch, days, 10000m, "SPK"));
        var pouchOpen = await Lot(steripack, pouch, 5, 10000m, "SPK");
        await d.RouteAsync(pouchOpen, "inspection");
        // Galway Electronics PCBA (conditional: always inspected): one pass, one failure.
        await Inspect(await Lot(galway, pcba, 42, 250m, "GE"), 250m, 0m, "100% functional test passed; visual on 50 boards per IPC-A-610 Class 3.");
        var failed = await Lot(galway, pcba, 9, 250m, "GE");
        await Inspect(failed, 250m, 3m, "Functional test: 3 boards fail occlusion-sensor channel self-test.", close: false);
        await d.UpdateAsync(failed, new()
        {
            ["defect_description"] = "Solder bridging between pins 14–15 of U7 (QFN-48) on 3 of 250 boards; boards fail the occlusion-sensor self-test. IPC-A-610 Class 3 reject.",
            ["qty_rejected"] = 3m,
        }, "cwu");
        if (d.Config.FormsByKey.ContainsKey("ncr"))
        {
            try
            {
                await DemoActions.RunAsync(d, failed, "create_ncmr", "cwu");
                await d.RouteAsync(failed, "rejected");
            }
            catch (InvalidOperationException)
            {
                // The NCR form may need data this module can't know about; the lot then stays in Inspection awaiting its NCMR.
            }
        }
        // Midwest shafts and NorthStar EO cycles.
        await Inspect(await Lot(midwest, shaft, 30, 500m, "MPM"), 50m, 0m, "Diameter 3.995–3.999 mm; passivation cert attached.");
        await Inspect(await Lot(northstar, eo, 20, 48m, "NS-EO"), 10m, 0m, "Certificate of sterilization CS-26-1187; all 10 BIs negative; residuals within ISO 10993-7.");
    }
}
