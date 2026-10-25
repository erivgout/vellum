using Microsoft.EntityFrameworkCore;
using Vellum.Engine.Records;
using Vellum.Modules.Supplier;
using Vellum.Packaging;

namespace Vellum.Modules.Risk;

/// <summary>Demo FMEAs: the VX-200 housing assembly PFMEA (in progress) and the sterile pouch sealing PFMEA (completed).</summary>
public static class FmeaDemo
{
    public static async Task SeedAsync(DemoContext d)
    {
        Dictionary<string, object?> Row(string item, string mode, string effect, int s, string cause, int o, string controls, int det,
            string? action = null, string? responsible = null, string? justification = null) => new()
        {
            ["item_function"] = item, ["failure_mode"] = mode, ["effect"] = effect, ["severity"] = (decimal)s, ["cause"] = cause,
            ["occurrence"] = (decimal)o, ["controls"] = controls, ["detection"] = (decimal)det, ["recommended_action"] = action,
            ["responsible"] = responsible == null ? null : d.User(responsible), ["justification"] = justification,
        };

        // Record what was done and the revised ratings on worksheet rows (as the FMEA owner would after the actions close).
        async Task Revise(Guid id, string asUser, params (string Mode, string Taken, int S, int O, int D)[] revisions)
        {
            d.As(asUser);
            d.Db.ChangeTracker.Clear();
            var records = d.Services.GetRequiredService<RecordService>();
            var snap = await records.LoadRequiredAsync(id);
            var proposed = new RecordData();
            var rows = proposed.RowsOf("worksheet");
            foreach (var row in snap.Rows("worksheet"))
            {
                var clone = row.Clone();
                var rev = revisions.FirstOrDefault(r => r.Mode == row.Values.Text("failure_mode"));
                if (rev.Mode != null)
                {
                    clone.Values.Set("action_taken", Val.Of(rev.Taken));
                    clone.Values.Set("rev_severity", Val.Of(rev.S));
                    clone.Values.Set("rev_occurrence", Val.Of(rev.O));
                    clone.Values.Set("rev_detection", Val.Of(rev.D));
                }
                rows.Add(clone);
            }
            var res = await records.SaveAsync(snap, proposed, new SaveOptions { System = true });
            if (!res.Ok) throw new InvalidOperationException("Demo FMEA revision failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        }

        Guid? dropRisk = null;
        if (d.Config.FormsByKey.TryGetValue("risk_assessment", out var raForm))
            dropRisk = await d.Db.Records.AsNoTracking().Where(r => r.FormId == raForm.Id && r.Title.StartsWith("Housing crack")).Select(r => (Guid?)r.Id).FirstOrDefaultAsync();

        // 1. VX-200 housing assembly PFMEA: actions in progress, one high-severity row still without an action (INV-179 blocks completion).
        var housingValues = new Dictionary<string, object?>
        {
            ["subject"] = "VX-200 infusion pump housing assembly", ["fmea_type"] = "process", ["product"] = "VX-200 infusion pump (HSG-200 assembly)",
            ["owner"] = d.User("akim"), ["team"] = new[] { d.User("jlee"), d.User("cwu"), d.User("ebaker"), d.User("nflores") },
            ["scope_text"] = "Final assembly line 3, operations 10 to 80: ultrasonic welding of the housing halves through packaging. Incoming component quality is covered by the supplier PFMEAs.",
        };
        if (dropRisk != null) housingValues["related_risk"] = dropRisk;
        var housing = await d.CreateAsync("fmea", housingValues, "akim", daysAgo: 50, rows: new()
        {
            ["worksheet"] =
            [
                Row("Op 10 Ultrasonic weld housing halves", "Incomplete weld along the seam", "Fluid ingress to the control board; possible shutdown during therapy (safety)", 9,
                    "Weld horn wear causes amplitude drift", 4, "Weld energy monitoring; 100% pressure-decay leak test", 3,
                    "Add horn-wear PM every 50,000 cycles and SPC on weld energy", "ebaker"),
                Row("Op 20 Drive housing screws", "Screws under-torqued", "Housing separates when dropped; exposed electronics", 7,
                    "Torque driver drift (no calibration interval)", 3, "Torque audit 40 units/week", 4,
                    "Put the torque driver on a 30-day calibration interval (see CAPA on torque drift)", "ebaker"),
                Row("Op 30 Install door gasket", "Gasket missing or twisted", "IP22 ingress protection lost: fluid ingress", 8,
                    "Manual placement; gasket can twist in the groove", 3, "Visual check by the operator", 5,
                    "Add a vision system presence/position check after Op 30", "akim"),
                Row("Op 35 Seat pressure sensor", "Occlusion pressure sensor not fully seated", "Downstream occlusion not detected: delayed therapy (patient harm)", 10,
                    "Sensor clip not fully engaged", 2, "Functional occlusion test at Op 70", 4),
                Row("Op 40 Apply device label", "Label misaligned or UDI unreadable", "UDI can't be scanned; traceability lost (regulatory)", 6,
                    "Label applicator guide worn", 3, "UDI barcode verification at Op 80", 3),
                Row("Op 50 Bond display window", "Adhesive void at the window edge", "Cosmetic defect; possible fluid path into the display", 5,
                    "Inconsistent dispense pattern", 4, "Visual inspection", 6,
                    "Optimise the dispense pattern and add UV fluorescence inspection", "akim"),
                Row("Op 60 Fit battery door latch", "Latch fractures in use", "Battery dislodged: pump shuts down during therapy (safety)", 9,
                    "Latch material brittle at low temperature", 2, "Latch pull test 5 per lot", 6,
                    "Change latch material to PC/ABS 30% GF and repeat cold-drop testing", "akim"),
                Row("Op 70 Final functional test", "Alarm speaker not tested", "Silent alarm: occlusion or air-in-line not noticed (patient harm)", 10,
                    "Test step skipped", 2, "Automated audio test with interlock", 2,
                    justification: "Speaker output is 100% tested by the automated audio test; the station can't pass a unit without it (interlock validated in TMV-0045)."),
                Row("Op 80 Pack and label shipper", "Wrong IFU revision packed", "User follows outdated instructions", 6,
                    "Superseded IFU stock at the line", 2, "Line clearance checklist", 4),
            ],
        });
        await d.RouteAsync(housing.Id, "assigned");
        await DemoActions.RunAsync(d, housing.Id, "create_actions", "akim", new() { ["days"] = "30", ["verify"] = "yes" });
        await RiskDemo.CompleteActionItemsAsync(d, housing.Id, only: "calibration interval", daysAgo: 12);
        await RiskDemo.CompleteActionItemsAsync(d, housing.Id, only: "horn-wear", daysAgo: 4);
        await Revise(housing.Id, "akim",
            ("Screws under-torqued", "30-day calibration interval in place since August; weekly torque audits clean.", 7, 1, 4),
            ("Incomplete weld along the seam", "Horn PM added to the CMMS; SPC chart on weld energy live on line 3.", 9, 2, 3));

        // 2. Sterile pouch sealing PFMEA: every action closed and verified, completed and signed.
        var pouch = await d.CreateAsync("fmea", new()
        {
            ["subject"] = "Sterile barrier pouch sealing (SteriPack 1073B pouch, sealer HS-2)", ["fmea_type"] = "process",
            ["product"] = "VX-200 accessory kit sterile barrier", ["owner"] = d.User("jlee"),
            ["team"] = new[] { d.User("hmoreau"), d.User("ebaker"), d.User("cwu") },
            ["scope_text"] = "Heat sealing of the Tyvek 1073B peel pouch on band sealer HS-2 through to the seal integrity checks (ISO 11607-2).",
        }, "jlee", daysAgo: 160, rows: new()
        {
            ["worksheet"] =
            [
                Row("Heat seal pouch", "Seal temperature too low", "Weak seal or channel: loss of sterility (patient infection)", 9,
                    "Heater band degradation", 3, "Temperature display; hourly peel test", 4,
                    "Validate the seal window (OQ/PQ) and add a temperature alarm with line stop", "ebaker"),
                Row("Heat seal pouch", "Seal temperature too high", "Brittle seal, Tyvek delamination, particulates on opening", 7,
                    "Controller overshoot after a stop", 2, "Visual inspection of every pouch", 4),
                Row("Heat seal pouch", "Contamination on the seal area", "Seal channel: loss of sterility", 9,
                    "Powder or debris from gloves on the seal area", 2, "Visual inspection", 5,
                    "Glove and seal-area wipe procedure; dye penetration test (ASTM F1929) every shift", "hmoreau"),
                Row("Load pouch", "Pouch wrinkled in the seal zone", "Seal channel: loss of sterility", 8,
                    "Operator loading technique", 3, "Visual inspection of every pouch", 3,
                    "Add a loading fixture with tensioning guides", "ebaker"),
                Row("Heat seal pouch", "Seal width below 6 mm", "Reduced seal strength", 6, "Pouch offset in the sealer", 2, "Seal width gauge each hour", 3),
                Row("Select pouch", "Wrong pouch size used", "Kit damages the pouch; seal stressed", 5, "Similar-looking pouches at the station", 2, "Line clearance; kit BOM check", 2),
                Row("Heat seal pouch", "Dwell timer failure", "Weak seal: loss of sterility", 9, "Timer relay failure", 2, "PLC-controlled dwell with hourly peel test", 3,
                    justification: "Dwell is PLC-controlled with a redundant timer and fault alarm; hourly peel tests detect any drift (validated in PQ-HS2-03)."),
            ],
        });
        await d.RouteAsync(pouch.Id, "assigned");
        await DemoActions.RunAsync(d, pouch.Id, "create_actions", "jlee", new() { ["days"] = "30", ["verify"] = "yes" });
        await RiskDemo.CompleteActionItemsAsync(d, pouch.Id, daysAgo: 90);
        await Revise(pouch.Id, "jlee",
            ("Seal temperature too low", "Seal window validated (PQ-HS2-03); temperature alarm with line stop installed.", 9, 2, 2),
            ("Contamination on the seal area", "Wipe procedure in WI-PK-011; dye penetration test every shift since March.", 9, 1, 3),
            ("Pouch wrinkled in the seal zone", "Loading fixture LF-07 in use; zero wrinkle rejects in 3 months.", 8, 1, 3));
        await d.UpdateAsync(pouch.Id, new()
        {
            ["conclusion"] = "Every failure mode with severity ≥ 9 has a validated control or a documented justification. The highest RPN (108, low seal temperature) was reduced to 36, and no row is now at or over the RPN threshold. The sealing process is acceptable for release.",
        }, "jlee");
        await d.RouteAsync(pouch.Id, "completed");
    }
}
