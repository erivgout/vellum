using Vellum.Engine.Records;
using Vellum.Modules.Change;
using Vellum.Packaging;

namespace Vellum.Modules.Inspections;

/// <summary>Inspections demo data: criteria profiles, points, an Active ST-40 final inspection plan and lots with passing, warning and failing samples.</summary>
internal static class InspectionsDemo
{
    public static async Task SeedAsync(DemoContext d)
    {
        // ------------------------------------------------------------------ criteria profiles
        var od = await d.CreateAsync("criteria_profile", new()
        {
            ["name"] = "Tube outside diameter", ["enabled"] = true, ["data_type"] = "numeric", ["numeric_type"] = "range", ["unit"] = "mm",
            ["lsl"] = 3.90m, ["lcl"] = 3.95m, ["target"] = 4.00m, ["ucl"] = 4.05m, ["usl"] = 4.10m,
            ["method"] = "Laser micrometer LM-2, 3 readings 120° apart, averaged",
        }, "jlee", daysAgo: 200);
        var wall = await d.CreateAsync("criteria_profile", new()
        {
            ["name"] = "Tube wall thickness", ["enabled"] = true, ["data_type"] = "numeric", ["numeric_type"] = "target", ["unit"] = "mm",
            ["target"] = 0.50m, ["tolerance"] = 0.05m, ["lcl"] = 0.47m, ["ucl"] = 0.53m, ["method"] = "Ultrasonic thickness gauge UTG-11",
        }, "jlee", daysAgo: 200);
        var bond = await d.CreateAsync("criteria_profile", new()
        {
            ["name"] = "Connector bond strength", ["enabled"] = true, ["data_type"] = "numeric", ["numeric_type"] = "min", ["unit"] = "N",
            ["lsl"] = 15m, ["lcl"] = 16.5m, ["target"] = 20m, ["method"] = "Pull tester PT-500 at 50 mm/min (SPEC-ST40-003 §4)",
        }, "jlee", daysAgo: 200);
        var particles = await d.CreateAsync("criteria_profile", new()
        {
            ["name"] = "Particulate ≥ 10 µm per device", ["enabled"] = true, ["data_type"] = "numeric", ["numeric_type"] = "max", ["unit"] = "particles",
            ["target"] = 5m, ["ucl"] = 20m, ["usl"] = 25m, ["method"] = "Liquid particle counter LPC-3, USP <788> method 1",
        }, "hmoreau", daysAgo: 190);
        var visual = await d.CreateAsync("criteria_profile", new()
        {
            ["name"] = "Visual inspection – tubing set", ["enabled"] = true, ["data_type"] = "keyword", ["method"] = "Under 1,000 lux, against the boundary samples BS-ST40",
        }, "jlee", daysAgo: 200, rows: new()
        {
            ["keywords"] =
            [
                new() { ["keyword"] = "No defects", ["kw_result"] = "pass" },
                new() { ["keyword"] = "Minor cosmetic", ["kw_result"] = "warn" },
                new() { ["keyword"] = "Kink", ["kw_result"] = "fail" },
                new() { ["keyword"] = "Foreign matter", ["kw_result"] = "fail" },
                new() { ["keyword"] = "Scratch (legacy)", ["kw_result"] = "warn", ["kw_disabled"] = true },
            ],
        });
        var label = await d.CreateAsync("criteria_profile", new()
        {
            ["name"] = "Label legibility", ["enabled"] = true, ["data_type"] = "keyword", ["method"] = "Read at 30 cm; verify UDI barcode scans",
        }, "jlee", daysAgo: 200, rows: new()
        {
            ["keywords"] =
            [
                new() { ["keyword"] = "Legible", ["kw_result"] = "pass" },
                new() { ["keyword"] = "Smudged but readable", ["kw_result"] = "warn" },
                new() { ["keyword"] = "Illegible", ["kw_result"] = "fail" },
            ],
        });
        await d.CreateAsync("criteria_profile", new()
        {
            ["name"] = "Tube outside diameter (old gauge)", ["enabled"] = false, ["data_type"] = "numeric", ["numeric_type"] = "range", ["unit"] = "mm",
            ["lsl"] = 3.85m, ["usl"] = 4.15m, ["method"] = "Retired: replaced by laser micrometer method",
        }, "jlee", daysAgo: 400);

        // ------------------------------------------------------------------ inspection points
        Dictionary<string, object?> Crit(Guid profile, string cls, string reference) => new() { ["profile"] = profile, ["class"] = cls, ["reference"] = reference };
        var pDim = await d.CreateAsync("inspection_point", new()
        {
            ["name"] = "ST-40 dimensional check", ["status"] = "active", ["sampling"] = "quantity", ["sample_qty"] = 5m, ["per_qty"] = 500m,
            ["failure_classes"] = new List<string> { "critical", "major" }, ["review_for"] = "failures",
            ["description"] = "Measure at the patient end, 20 mm from the connector.",
        }, "jlee", daysAgo: 190, rows: new() { ["criteria"] = [Crit(od.Id, "major", "DWG-ST40-001 dim A"), Crit(wall.Id, "major", "DWG-ST40-001 dim B")] });
        var pBond = await d.CreateAsync("inspection_point", new()
        {
            ["name"] = "ST-40 connector bond test", ["status"] = "active", ["sampling"] = "quantity", ["sample_qty"] = 3m, ["per_qty"] = 1000m,
            ["failure_classes"] = new List<string> { "critical" }, ["review_for"] = "sample_failures",
            ["description"] = "Destructive test: scrap tested samples.",
        }, "jlee", daysAgo: 190, rows: new() { ["criteria"] = [Crit(bond.Id, "critical", "SPEC-ST40-003 §4")] });
        var pVis = await d.CreateAsync("inspection_point", new()
        {
            ["name"] = "ST-40 visual, particulate and labeling", ["status"] = "active", ["sampling"] = "quantity", ["sample_qty"] = 2m, ["per_qty"] = 500m,
            ["failure_classes"] = new List<string> { "critical", "major" }, ["review_for"] = "warnings",
        }, "jlee", daysAgo: 190, rows: new()
        {
            ["criteria"] = [Crit(visual.Id, "major", "BS-ST40 boundary samples"), Crit(label.Id, "minor", "LBL-ST40 rev G"), Crit(particles.Id, "critical", "USP <788>")],
        });
        await d.CreateAsync("inspection_point", new()
        {
            ["name"] = "ST-40 burst pressure (retired)", ["status"] = "inactive", ["sampling"] = "quantity", ["sample_qty"] = 1m, ["per_qty"] = 2000m,
            ["review_for"] = "failures", ["description"] = "Moved to design verification testing in 2025.",
        }, "jlee", daysAgo: 400, rows: new() { ["criteria"] = [Crit(bond.Id, "critical", "Legacy")] });

        // ------------------------------------------------------------------ inspection plans
        var plan = await d.CreateAsync("inspection_plan", new()
        {
            ["name"] = "ST-40 sterile tubing set – final inspection", ["owner"] = d.User("jlee"), ["product"] = "ST-40 sterile tubing set", ["part_revision"] = "D",
            ["description"] = "Final release inspection before sterilization. Sampling per SOP-QC-021 (c = 0 for critical and major criteria).",
        }, "jlee", daysAgo: 180, rows: new()
        {
            ["points"] =
            [
                new() { ["step"] = 1m, ["point"] = pDim.Id, ["notes"] = "Before packaging" },
                new() { ["step"] = 2m, ["point"] = pBond.Id, ["notes"] = "Destructive" },
                new() { ["step"] = 3m, ["point"] = pVis.Id, ["notes"] = "After labeling" },
            ],
        });
        await d.RouteAsync(plan.Id, "approve", asUser: "jlee");
        await d.RouteAsync(plan.Id, "active", asUser: "sortiz", comment: "Sampling plan reviewed against SOP-QC-021.");

        await d.CreateAsync("inspection_plan", new()
        {
            ["name"] = "VX-200 housing – incoming inspection", ["owner"] = d.User("cwu"), ["product"] = "VX-200 front housing 200-1101", ["part_revision"] = "E",
            ["description"] = "Incoming inspection of molded housings from the molder (draft for the HPX4 resin change).",
        }, "cwu", daysAgo: 4, rows: new() { ["points"] = [new() { ["point"] = pVis.Id, ["notes"] = "Cosmetic and label check" }] });

        // ------------------------------------------------------------------ inspection records
        var records = d.Services.GetRequiredService<RecordService>();
        string Tool(string criterion) => criterion switch
        {
            "Tube outside diameter" => "LM-2 #0457",
            "Tube wall thickness" => "UTG-11 #0210",
            "Connector bond strength" => "PT-500 #0033",
            "Particulate ≥ 10 µm per device" => "LPC-3 #0008",
            _ => "",
        };
        async Task<Guid> SampleAsync(Guid recordId, Guid point, string serial, string user, Dictionary<string, object> values, string? remarks = null)
        {
            var outcome = await DemoKit.ActAsync(d, recordId, "create_sample", user, new() { ["point"] = point.ToString(), ["serial"] = serial });
            var id = outcome.OpenRecordId!.Value;
            await DemoKit.UpdateRowsAsync(d, id, "measurements", user, (s, r) =>
            {
                if (r.Values.Text("criterion") is not { } name || !values.TryGetValue(name, out var v)) return;
                if (v is decimal dv)
                {
                    r.SetRow(s.Form, s.Config, "measured", dv);
                    r.SetRow(s.Form, s.Config, "tool", Tool(name));
                }
                else r.SetRow(s.Form, s.Config, "observed", (string)v);
            }, values: remarks != null ? new() { ["corrections"] = remarks } : null);
            return id;
        }
        async Task CloseOrReviewAsync(Guid sampleId, string user)
        {
            d.Db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(sampleId);
            await d.RouteAsync(sampleId, snap.Bool("review_needed") == true ? "review" : "closed", asUser: user);
        }

        // Lot 26-0412: every sample passes (one warning) → Pass, closed.
        var r1 = await d.CreateAsync("inspection_record", new()
        {
            ["plan"] = plan.Id, ["record_name"] = "Final inspection lot 26-0412", ["lot_number"] = "26-0412", ["lot_qty"] = 500m, ["inspector"] = d.User("hmoreau"),
            ["lot_start"] = DateTime.UtcNow.Date.AddDays(-9).AddHours(7), ["lot_end"] = DateTime.UtcNow.Date.AddDays(-9).AddHours(15),
        }, "hmoreau", daysAgo: 9);
        decimal[] ods = [4.00m, 4.02m, 3.98m, 4.07m, 4.01m];
        decimal[] walls = [0.50m, 0.51m, 0.49m, 0.50m, 0.52m];
        for (var i = 0; i < 5; i++)
            await CloseOrReviewAsync(await SampleAsync(r1.Id, pDim.Id, $"26-0412-D{i + 1:00}", "hmoreau",
                new() { ["Tube outside diameter"] = ods[i], ["Tube wall thickness"] = walls[i] }, i == 3 ? "OD near the upper control limit; extruder die checked, within spec." : null), "hmoreau");
        foreach (var (n, f) in new[] { (1, 21.4m), (2, 19.8m), (3, 22.1m) })
            await CloseOrReviewAsync(await SampleAsync(r1.Id, pBond.Id, $"26-0412-B{n:00}", "hmoreau", new() { ["Connector bond strength"] = f }), "hmoreau");
        for (var i = 0; i < 2; i++)
            await CloseOrReviewAsync(await SampleAsync(r1.Id, pVis.Id, $"26-0412-V{i + 1:00}", "hmoreau",
                new() { ["Visual inspection – tubing set"] = "No defects", ["Label legibility"] = "Legible", ["Particulate ≥ 10 µm per device"] = 6m + i }), "hmoreau");
        await d.UpdateAsync(r1.Id, new() { ["remarks"] = "Lot released to sterilization." }, "hmoreau");
        await d.RouteAsync(r1.Id, "closed", asUser: "hmoreau");

        // Lot 26-0419: a failed wall thickness (reviewed and closed), a failed bond test in Review with a follow-up, a visual warning in Review.
        var r2 = await d.CreateAsync("inspection_record", new()
        {
            ["plan"] = plan.Id, ["record_name"] = "Final inspection lot 26-0419", ["lot_number"] = "26-0419", ["lot_qty"] = 500m, ["inspector"] = d.User("hmoreau"),
            ["lot_start"] = DateTime.UtcNow.Date.AddDays(-3).AddHours(7), ["lot_end"] = DateTime.UtcNow.Date.AddDays(-3).AddHours(15),
        }, "hmoreau", daysAgo: 3);
        decimal[] ods2 = [4.01m, 3.99m, 4.03m, 4.00m, 3.97m];
        decimal[] walls2 = [0.50m, 0.49m, 0.56m, 0.51m, 0.50m];
        Guid wallFail = Guid.Empty;
        for (var i = 0; i < 5; i++)
        {
            var id = await SampleAsync(r2.Id, pDim.Id, $"26-0419-D{i + 1:00}", "hmoreau", new() { ["Tube outside diameter"] = ods2[i], ["Tube wall thickness"] = walls2[i] },
                i == 2 ? "Wall 0.56 mm: above the 0.55 mm spec limit. Extruder puller speed drifted; adjusted and re-measured the next 20 units (all within spec)." : null);
            if (i == 2) wallFail = id;
            await CloseOrReviewAsync(id, "hmoreau");
        }
        await d.RouteAsync(wallFail, "closed", asUser: "sortiz", comment: "Reviewed: isolated drift, containment verified by the extra 20-unit check.");
        await CloseOrReviewAsync(await SampleAsync(r2.Id, pBond.Id, "26-0419-B01", "hmoreau", new() { ["Connector bond strength"] = 19.5m }), "hmoreau");
        await CloseOrReviewAsync(await SampleAsync(r2.Id, pBond.Id, "26-0419-B02", "hmoreau", new() { ["Connector bond strength"] = 16.0m },
            "Below the 16.5 N warning limit; within spec."), "hmoreau");
        var bondFail = await SampleAsync(r2.Id, pBond.Id, "26-0419-B03", "hmoreau", new() { ["Connector bond strength"] = 14.2m },
            "Separated at 14.2 N, below the 15 N minimum. Lot placed on hold pending investigation.");
        await CloseOrReviewAsync(bondFail, "hmoreau");
        var kind = d.Config.FormsByKey.ContainsKey("ncr") ? "ncr" : "capa";
        try
        {
            await DemoKit.ActAsync(d, bondFail, "follow_up", "hmoreau", new() { ["kind"] = kind, ["subject"] = "Lot 26-0419 Y-connector bond strength below 15 N", ["owner"] = d.User("jlee").ToString() });
        }
        catch (InvalidOperationException) when (kind == "ncr")
        {
            await DemoKit.ActAsync(d, bondFail, "follow_up", "hmoreau", new() { ["kind"] = "capa", ["subject"] = "Lot 26-0419 Y-connector bond strength below 15 N", ["owner"] = d.User("jlee").ToString() });
        }
        await CloseOrReviewAsync(await SampleAsync(r2.Id, pVis.Id, "26-0419-V01", "hmoreau",
            new() { ["Visual inspection – tubing set"] = "Minor cosmetic", ["Label legibility"] = "Legible", ["Particulate ≥ 10 µm per device"] = 9m },
            "Light scuff on the drip chamber; within boundary sample BS-ST40-04."), "hmoreau");

        // Lot 26-0425: just started.
        var r3 = await d.CreateAsync("inspection_record", new()
        {
            ["plan"] = plan.Id, ["record_name"] = "Final inspection lot 26-0425", ["lot_number"] = "26-0425", ["lot_qty"] = 1000m, ["inspector"] = d.User("hmoreau"),
            ["lot_start"] = DateTime.UtcNow.Date.AddHours(7),
        }, "hmoreau", daysAgo: 0);
        await CloseOrReviewAsync(await SampleAsync(r3.Id, pDim.Id, "26-0425-D01", "hmoreau", new() { ["Tube outside diameter"] = 4.00m, ["Tube wall thickness"] = 0.50m }), "hmoreau");
        await CloseOrReviewAsync(await SampleAsync(r3.Id, pDim.Id, "26-0425-D02", "hmoreau", new() { ["Tube outside diameter"] = 4.02m, ["Tube wall thickness"] = 0.51m }), "hmoreau");
    }
}
