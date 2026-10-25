using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Assets;

/// <summary>Demo data: standards, measuring instruments and equipment at Kalamazoo, Boston and Galway with calibration history.</summary>
public static class AssetsDemo
{
    private sealed record Char(string Name, string Unit, decimal Nominal, decimal Minus, decimal Plus);

    private static async Task<Record> AssetAsync(DemoContext d, string id, string description, string type, string site, string location,
        string owner, string technician, int? interval, Char[] chars, string? manufacturer = null, string? model = null, string? serial = null,
        bool standard = false, string? traceability = null, string? accuracy = null, string? vendor = null, int? pmInterval = null, int daysAgo = 400)
    {
        return await d.CreateAsync("asset", new()
        {
            ["asset_id"] = id, ["description"] = description, ["asset_type"] = type, ["site"] = site, ["location"] = location,
            ["manufacturer"] = manufacturer, ["model"] = model, ["serial_number"] = serial,
            ["owner"] = d.User(owner), ["technician"] = d.User(technician),
            ["calibration_required"] = interval != null, ["cal_interval_days"] = interval, ["cal_vendor"] = vendor,
            ["use_as_standard"] = standard, ["traceability"] = traceability, ["accuracy"] = accuracy,
            ["pm_required"] = pmInterval != null, ["pm_interval_days"] = pmInterval,
        }, technician, daysAgo: daysAgo, rows: new()
        {
            ["characteristics"] = chars.Select(c => new Dictionary<string, object?>
            {
                ["characteristic"] = c.Name, ["unit"] = c.Unit, ["nominal"] = c.Nominal, ["tol_minus"] = c.Minus, ["tol_plus"] = c.Plus,
            }).ToList(),
        });
    }

    /// <summary>Record (and by default complete) a calibration with one as-found / as-left pair per characteristic.</summary>
    private static async Task<Record> CalibrateAsync(DemoContext d, Record asset, string technician, int daysAgo, decimal[] found, decimal[] left,
        Record[]? standards = null, string? certificate = null, string? vendor = null, Dictionary<string, object?>? extra = null, bool complete = true,
        double temperature = 20.4, int humidity = 42, string? procedure = null)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(asset.Id);
        var chars = snap.Rows("characteristics");
        var rows = chars.Select((c, i) => new Dictionary<string, object?>
        {
            ["characteristic"] = c.Values.Text("characteristic"), ["unit"] = c.Values.Text("unit"), ["nominal"] = c.Values.Number("nominal"),
            ["tol_minus"] = c.Values.Number("tol_minus"), ["tol_plus"] = c.Values.Number("tol_plus"),
            ["found"] = i < found.Length ? found[i] : null, ["left"] = i < left.Length ? left[i] : null,
        }).ToList();
        var values = new Dictionary<string, object?>
        {
            ["asset_ref"] = AssetQueries.AssetRef(snap), ["cal_date"] = DemoContext.Days(-daysAgo), ["technician"] = d.User(technician),
            ["cal_type"] = standards == null ? "external" : "internal", ["vendor"] = vendor, ["certificate_no"] = certificate,
            ["standards"] = standards?.Select(s => s.Id).ToList(), ["temperature_c"] = (decimal)temperature, ["humidity_pct"] = (decimal)humidity,
            ["procedure"] = procedure,
        };
        if (extra != null) foreach (var (k, v) in extra) values[k] = v;
        var cal = await d.CreateAsync("calibration", values, technician, parent: asset.Id, assignees: [technician], rows: new() { ["readings"] = rows }, daysAgo: daysAgo);
        if (complete) await d.RouteAsync(cal.Id, "completed", asUser: technician);
        return cal;
    }

    public static async Task SeedAsync(DemoContext d)
    {
        // ---- Standards (calibrated externally, traceable to national standards).
        var torqueStd = await AssetAsync(d, "KZO-STD-001", "Torque transducer standard 0.2–5 N·m", "torque_standard", "kzo", "Metrology lab", "ebaker", "ebaker", 365,
            [new("Torque at 2.000 N·m", "N·m", 2.000m, 0.010m, 0.010m)], "Mountz", "EZ-TorQ III", "EZ3-118204", standard: true,
            traceability: "NIST-traceable, Transcat accredited (ISO/IEC 17025) certificate", accuracy: "±0.5 % of reading", vendor: "Transcat Kalamazoo", daysAgo: 800);
        var blocks = await AssetAsync(d, "KZO-STD-002", "Gauge block set grade 0, 0.5–100 mm (112 pieces)", "gauge_blocks", "kzo", "Metrology lab", "ebaker", "ebaker", 365,
            [new("25 mm block length", "mm", 25.00000m, 0.00014m, 0.00014m)], "Mitutoyo", "516-940-26", "MG-0094417", standard: true,
            traceability: "NIST-traceable, Mitutoyo America A2LA certificate", accuracy: "Grade 0 (ISO 3650)", vendor: "Mitutoyo America calibration lab", daysAgo: 800);
        var pressureStd = await AssetAsync(d, "KZO-STD-003", "Reference pressure calibrator 0–1,400 kPa", "pressure_calibrator", "kzo", "Metrology lab", "ebaker", "ebaker", 365,
            [new("Pressure at 689.48 kPa (100 psi)", "kPa", 689.48m, 0.14m, 0.14m)], "Fluke", "2700G-BG200K", "4419021", standard: true,
            traceability: "NIST-traceable, Fluke Calibration accredited certificate", accuracy: "±0.02 % FS", vendor: "Fluke Calibration, Everett", daysAgo: 800);
        var manometer = await AssetAsync(d, "BOS-STD-005", "Digital micromanometer 0–3,735 Pa", "pressure_calibrator", "bos", "Facilities shop", "ebaker", "ebaker", 365,
            [new("Differential pressure at 125 Pa", "Pa", 125.0m, 1.0m, 1.0m)], "TSI", "5825", "58251942008", standard: true,
            traceability: "NIST-traceable, TSI factory calibration", accuracy: "±1 % of reading ±1 Pa", vendor: "TSI Service Center", daysAgo: 800);
        var weights = await AssetAsync(d, "GAL-STD-004", "E2 calibration weight set 1 mg–200 g", "weights", "gal", "QC laboratory", "hmoreau", "hmoreau", 365,
            [new("200 g weight", "g", 200.0000m, 0.0003m, 0.0003m)], "Sartorius", "YCW522-AC-02", "37106622", standard: true,
            traceability: "Traceable to NSAI National Metrology Laboratory", accuracy: "OIML class E2", vendor: "NSAI National Metrology Laboratory", daysAgo: 800);

        await CalibrateAsync(d, torqueStd, "ebaker", 400, [2.003m], [2.003m], certificate: "TC-25-110482", vendor: "Transcat Kalamazoo");
        await CalibrateAsync(d, torqueStd, "ebaker", 40, [2.004m], [2.002m], certificate: "TC-26-020417", vendor: "Transcat Kalamazoo");
        await CalibrateAsync(d, blocks, "ebaker", 400, [25.00004m], [25.00004m], certificate: "MAC-25-55871", vendor: "Mitutoyo America calibration lab");
        await CalibrateAsync(d, blocks, "ebaker", 40, [25.00006m], [25.00006m], certificate: "MAC-26-06120", vendor: "Mitutoyo America calibration lab");
        await CalibrateAsync(d, pressureStd, "ebaker", 400, [689.51m], [689.51m], certificate: "FC-25-77310", vendor: "Fluke Calibration, Everett");
        await CalibrateAsync(d, pressureStd, "ebaker", 40, [689.52m], [689.49m], certificate: "FC-26-08802", vendor: "Fluke Calibration, Everett");
        await CalibrateAsync(d, manometer, "ebaker", 400, [125.3m], [125.3m], certificate: "TSI-25-301877", vendor: "TSI Service Center");
        await CalibrateAsync(d, manometer, "ebaker", 40, [125.4m], [125.1m], certificate: "TSI-26-044012", vendor: "TSI Service Center");
        await CalibrateAsync(d, weights, "hmoreau", 400, [200.0001m], [200.0001m], certificate: "NML-25-1288", vendor: "NSAI National Metrology Laboratory");
        await CalibrateAsync(d, weights, "hmoreau", 40, [200.0001m], [200.0001m], certificate: "NML-26-0219", vendor: "NSAI National Metrology Laboratory");

        // ---- Kalamazoo instruments.
        var td14 = await AssetAsync(d, "KZO-TD-014", "Electronic torque driver 0.5–3.0 N·m, final assembly station 3", "torque_driver", "kzo", "Final assembly, station 3", "akim", "ebaker", 30,
            [new("Torque at 2.40 N·m", "N·m", 2.40m, 0.12m, 0.12m)], "Mountz", "MTD 30", "MZ2291847");
        var capa = await d.Db.Records.AsNoTracking().Where(r => r.Title == "Torque drift on final assembly station 3").Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
        await CalibrateAsync(d, td14, "ebaker", 64, [2.18m], [2.41m], [torqueStd], procedure: "CP-012 Torque driver calibration rev D", extra: new()
        {
            ["adjustments"] = "Clutch spring re-set and output re-zeroed to the transducer.",
            ["impact_assessment"] = "Station 3 housings torqued since the last good calibration were held: 6 of 40 sampled were below 2.28 N·m. Reworked under CAPA; no product shipped below minimum.",
            ["capa_link"] = capa is { } c ? new List<Guid> { c } : null,
            ["bypass_capa"] = capa == null,
            ["bypass_reason"] = capa == null ? "Covered by the station 3 torque audit; product contained and reworked." : null,
            ["comments"] = "As-found 2.18 N·m, 0.10 N·m below the lower limit. Interval reduced to 30 days.",
        });
        await CalibrateAsync(d, td14, "ebaker", 34, [2.43m], [2.43m], [torqueStd], procedure: "CP-012 Torque driver calibration rev D");
        await CalibrateAsync(d, td14, "ebaker", 4, [2.38m], [2.38m], [torqueStd], procedure: "CP-012 Torque driver calibration rev D");

        var td15 = await AssetAsync(d, "KZO-TD-015", "Electronic torque driver 1.0–5.0 N·m, final assembly station 5", "torque_driver", "kzo", "Final assembly, station 5", "akim", "ebaker", 90,
            [new("Torque at 3.50 N·m", "N·m", 3.50m, 0.175m, 0.175m)], "Mountz", "MTD 50", "MZ2291902");
        await CalibrateAsync(d, td15, "ebaker", 41, [3.53m], [3.53m], [torqueStd], procedure: "CP-012 Torque driver calibration rev D");

        var cal3 = await AssetAsync(d, "KZO-CAL-003", "Digital caliper 0–150 mm, incoming inspection", "caliper", "kzo", "Receiving inspection", "cwu", "ebaker", 180,
            [new("Outside jaws at 25 mm", "mm", 25.00m, 0.02m, 0.02m), new("Outside jaws at 100 mm", "mm", 100.00m, 0.02m, 0.02m)], "Mitutoyo", "500-196-30", "A2093315");
        await CalibrateAsync(d, cal3, "ebaker", 118, [25.01m, 100.01m], [25.01m, 100.01m], [blocks], procedure: "CP-004 Caliper calibration rev B");

        var cal7 = await AssetAsync(d, "KZO-CAL-007", "Digital caliper 0–200 mm, machining", "caliper", "kzo", "Machining, CNC cell", "akim", "ebaker", 180,
            [new("Outside jaws at 50 mm", "mm", 50.00m, 0.03m, 0.03m), new("Outside jaws at 150 mm", "mm", 150.00m, 0.03m, 0.03m)], "Mitutoyo", "500-197-30", "A2101187");
        await CalibrateAsync(d, cal7, "ebaker", 205, [50.01m, 150.02m], [50.01m, 150.02m], [blocks], procedure: "CP-004 Caliper calibration rev B");

        var mic = await AssetAsync(d, "KZO-MIC-002", "Outside micrometer 0–25 mm, machining", "micrometer", "kzo", "Machining, CNC cell", "akim", "ebaker", 365,
            [new("At 12.5 mm", "mm", 12.500m, 0.002m, 0.002m)], "Mitutoyo", "293-340-30", "M0811402");
        await CalibrateAsync(d, mic, "ebaker", 90, [12.501m], [12.501m], [blocks], procedure: "CP-005 Micrometer calibration rev A");

        var bal1 = await AssetAsync(d, "KZO-BAL-001", "Analytical balance 220 g, incoming QC", "balance", "kzo", "Receiving inspection", "cwu", "ebaker", 90,
            [new("Check at 100 g", "g", 100.0000m, 0.0005m, 0.0005m)], "Mettler Toledo", "XPR205", "B934017122", vendor: "Mettler-Toledo Service");
        await CalibrateAsync(d, bal1, "ebaker", 72, [100.0002m], [100.0001m], certificate: "MT-26-4471820", vendor: "Mettler-Toledo Service", temperature: 21.1, humidity: 38);

        var pg21 = await AssetAsync(d, "KZO-PG-021", "Pressure gauge 0–160 psi, compressed air header", "pressure_gauge", "kzo", "Compressor room, header", "ebaker", "ebaker", 365,
            [new("At 100 psi", "psi", 100.0m, 1.6m, 1.6m)], "Ashcroft", "1279 Duragauge", "AS-77-18822");
        await CalibrateAsync(d, pg21, "ebaker", 350, [100.8m], [100.8m], [pressureStd], procedure: "CP-020 Pressure gauge calibration rev C");

        var pg22 = await AssetAsync(d, "KZO-PG-022", "Pressure gauge 0–160 psi, molding chiller loop", "pressure_gauge", "kzo", "Molding hall, chiller skid", "lgarcia", "ebaker", 365,
            [new("At 100 psi", "psi", 100.0m, 1.6m, 1.6m)], "Ashcroft", "1279 Duragauge", "AS-77-18907");
        await CalibrateAsync(d, pg22, "ebaker", 375, [100.4m], [100.4m], [pressureStd], procedure: "CP-020 Pressure gauge calibration rev C");
        await CalibrateAsync(d, pg22, "ebaker", 9, [106.0m], [104.9m], [pressureStd], procedure: "CP-020 Pressure gauge calibration rev C", extra: new()
        {
            ["adjustments"] = "Pointer reset attempted; the Bourdon tube is deformed (likely a pressure spike) and the gauge can't be brought back within tolerance.",
            ["impact_assessment"] = "Utility gauge on the chiller loop, not used for product or process acceptance. Chiller pressure is also logged by the PLC transmitter PT-114 (calibrated, in tolerance). No product impact.",
            ["bypass_capa"] = true,
            ["bypass_reason"] = "No product or process decision relies on this gauge; it is being replaced like for like. Spike source tracked in maintenance MNT work order.",
            ["comments"] = "Gauge quarantined and tagged DO NOT USE pending replacement.",
        });

        var press = await AssetAsync(d, "KZO-PRS-007", "Injection molding press 7, Engel victory 220 t", "production", "kzo", "Molding hall, Press 7", "lgarcia", "ebaker", null,
            [], "Engel", "victory 220", "EN-220-40871", pmInterval: 90);
        var pm = await d.CreateAsync("maintenance", new()
        {
            ["asset_ref"] = "KZO-PRS-007 · Injection molding press 7, Engel victory 220 t", ["maintenance_type"] = "preventive",
            ["performed_on"] = DemoContext.Days(-40), ["technician"] = d.User("ebaker"),
            ["work_performed"] = "Quarterly PM per Engel schedule: hydraulic oil sample, filter change, tie-bar lubrication, safety gate and light curtain test.",
            ["downtime_hours"] = 6.5m, ["returned_to_service"] = DemoContext.Days(-40), ["result"] = "ok",
        }, "ebaker", parent: press.Id, assignees: ["ebaker"], daysAgo: 40, rows: new()
        {
            ["tasks"] =
            [
                new() { ["task"] = "Hydraulic oil sample sent for analysis", ["task_result"] = "ok" },
                new() { ["task"] = "Replace return-line filter", ["task_result"] = "replaced" },
                new() { ["task"] = "Lubricate tie bars and toggle", ["task_result"] = "ok" },
                new() { ["task"] = "Test safety gate interlocks and light curtain", ["task_result"] = "ok", ["notes"] = "Stopping time 182 ms (limit 250 ms)" },
            ],
            ["parts"] = [new() { ["part"] = "Return-line filter element", ["part_no"] = "EN-FE-10-220", ["qty"] = 1m }],
        });
        await d.RouteAsync(pm.Id, "completed", asUser: "ebaker");
        await d.CreateAsync("maintenance", new()
        {
            ["asset_ref"] = "KZO-PRS-007 · Injection molding press 7, Engel victory 220 t", ["maintenance_type"] = "corrective",
            ["performed_on"] = DemoContext.Today, ["technician"] = d.User("ebaker"),
            ["work_performed"] = "Press locked out after today's injury. Inspect the rear guard door interlock and the light curtain muting before any restart.",
        }, "ebaker", parent: press.Id, assignees: ["ebaker"], rows: new()
        {
            ["tasks"] =
            [
                new() { ["task"] = "Test rear guard door interlock (both channels)" },
                new() { ["task"] = "Check light curtain muting configuration against the validated settings" },
                new() { ["task"] = "Measure stopping time" },
            ],
        });

        // ---- Boston.
        var dp4 = await AssetAsync(d, "BOS-DP-004", "HEPA differential pressure gauge 0–250 Pa, cleanroom B fill line", "dp_gauge", "bos", "Cleanroom B, fill line", "akim", "ebaker", 180,
            [new("At 125 Pa", "Pa", 125m, 5m, 5m)], "Dwyer", "Magnehelic 2000-250PA", "DW-2000-80455");
        await CalibrateAsync(d, dp4, "ebaker", 45, [127m], [126m], [manometer], procedure: "CP-022 Differential pressure gauge calibration rev A", temperature: 20.1, humidity: 45);

        var th10 = await AssetAsync(d, "BOS-TH-010", "Thermohygrometer, cleanroom B", "thermohygrometer", "bos", "Cleanroom B, gowning", "akim", "ebaker", 365,
            [new("Temperature at 20 °C", "°C", 20.0m, 0.3m, 0.3m), new("Relative humidity at 45 %", "%RH", 45.0m, 2.0m, 2.0m)], "Vaisala", "HMT330", "V3940018",
            vendor: "Vaisala calibration service");
        await CalibrateAsync(d, th10, "ebaker", 200, [20.1m, 45.8m], [20.1m, 45.8m], certificate: "VAI-26-118733", vendor: "Vaisala calibration service");

        var ahu = await AssetAsync(d, "BOS-AHU-02", "Cleanroom B air handler AHU-02 with HEPA bank FB-2", "hvac", "bos", "Plant room, AHU-02", "akim", "ebaker", null, [],
            "Trane", "CSAA021", "TR-K19G33021", pmInterval: 180);
        var fb2 = await d.CreateAsync("maintenance", new()
        {
            ["asset_ref"] = "BOS-AHU-02 · Cleanroom B air handler AHU-02 with HEPA bank FB-2", ["maintenance_type"] = "corrective",
            ["performed_on"] = DemoContext.Days(-8), ["technician"] = d.User("ebaker"),
            ["work_performed"] = "Replaced HEPA filter bank FB-2 (loaded beyond rating, see CAPA on cleanroom B particulates). Integrity tested with PAO; airflow re-balanced.",
            ["downtime_hours"] = 10m, ["returned_to_service"] = DemoContext.Days(-8), ["result"] = "ok",
        }, "ebaker", parent: ahu.Id, assignees: ["ebaker"], daysAgo: 8, rows: new()
        {
            ["tasks"] =
            [
                new() { ["task"] = "Bag-out and replace 4 HEPA cells", ["task_result"] = "replaced" },
                new() { ["task"] = "PAO integrity scan", ["task_result"] = "ok", ["notes"] = "Max penetration 0.003 %" },
                new() { ["task"] = "Airflow and room pressure re-balance", ["task_result"] = "adjusted", ["notes"] = "Fill line +18 Pa to corridor" },
            ],
            ["parts"] = [new() { ["part"] = "HEPA filter H14 610 × 610 × 292 mm", ["part_no"] = "CAM-H14-610-292", ["qty"] = 4m }],
        });
        await d.RouteAsync(fb2.Id, "completed", asUser: "ebaker");

        // ---- Galway.
        var bal3 = await AssetAsync(d, "GAL-BAL-003", "Analytical balance 120 g, QC laboratory", "balance", "gal", "QC laboratory, weighing room", "hmoreau", "hmoreau", 180,
            [new("Check at 100 g", "g", 100.0000m, 0.0010m, 0.0010m), new("Check at 10 g", "g", 10.0000m, 0.0005m, 0.0005m)], "Sartorius", "Cubis II MCA125P", "0038817264");
        await CalibrateAsync(d, bal3, "hmoreau", 179, [100.0003m, 10.0001m], [100.0003m, 10.0001m], [weights], procedure: "LAB-CP-002 Balance calibration rev F", temperature: 20.8, humidity: 47);
        await CalibrateAsync(d, bal3, "hmoreau", 0, [100.0004m], [], [weights], procedure: "LAB-CP-002 Balance calibration rev F", complete: false, temperature: 20.6, humidity: 49);

        var gtd = await AssetAsync(d, "GAL-TD-001", "Torque driver 0.2–1.2 N·m, line 2 cap tightening", "torque_driver", "gal", "Line 2, capping station", "hmoreau", "hmoreau", 60,
            [new("Torque at 0.80 N·m", "N·m", 0.80m, 0.04m, 0.04m)], "Kilews", "SKD-BE120L", "KW-1128874", vendor: "Calibration Ireland Ltd");
        await CalibrateAsync(d, gtd, "hmoreau", 31, [0.81m], [0.81m], certificate: "CIL-26-03318", vendor: "Calibration Ireland Ltd");
    }
}
