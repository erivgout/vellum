using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// Nonconformance §12.3: Create → MRB Review (voting, e-signed) → Disposition → Final Approval → Verification → Completed (NC-003).
/// The NC type controls which sections show (NC-001). Disposition quantities must add up to the quantity affected (INV-065).
/// Use As Is needs an active, approved planned deviation (NC-004, INV-118). Supplier fault reveals Create SCAR (NC-005).
/// Material holds are released only by an approved disposition (NC-007).
/// </summary>
public sealed class NonconformanceApp : IPackagedApp
{
    public string Key => "nonconformance";
    public int Order => 34;

    private const string SupplierTypes = "nc_type=incoming||nc_type=in_process||nc_type=stock_rejection";
    private const string ProcessTypes = "nc_type=in_process||nc_type=finished_product";

    public void Define(AppBuilder app)
    {
        app.Name("Nonconformance (NCR)")
            .Description("Document, contain and disposition nonconforming material through the Material Review Board.")
            .Icon("bi-exclamation-octagon").Color("#dc2626").Suite("Quality").Order(20).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("@engineering", AppRole.Author)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        DefectCodes.Keywords(app);
        QualityCosts.Keywords(app);
        QualitySites.Keywords(app);
        app.Keywords("ncr_type", "Nonconformance type", k => k
            .Add("incoming", "Incoming / supplier material").Add("in_process", "In-process").Add("finished_product", "Finished product")
            .Add("customer_return", "Customer return").Add("stock_rejection", "Stock rejection"));
        app.Keywords("ncr_severity", "NCR severity", k => k
            .Add("minor", "Minor", "info").Add("major", "Major", "warning").Add("critical", "Critical", "danger"));
        app.Keywords("ncr_disposition", "Disposition", k => k
            .Add("use_as_is", "Use as is", "warning").Add("return_to_supplier", "Return to supplier", "info").Add("scrap", "Scrap", "danger")
            .Add("rework", "Rework", "primary").Add("regrade", "Re-grade", "secondary").Add("repair", "Repair", "primary").Add("sort", "Sort / screen", "secondary"));
        app.Keywords("ncr_hold_status", "Material hold", k => k
            .Add("none", "No hold", "secondary").Add("on_hold", "On hold", "danger").Add("released", "Released", "success"));

        app.Form("ncr", "Nonconformance", f =>
        {
            f.Icon("bi-exclamation-octagon")
                .Description("Nonconforming material or product: containment, MRB review, disposition and verification.")
                .Numbering("NCR-")
                .Title("{subject}")
                .Section("origination", "Origination", s =>
                {
                    s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("For example: Scratched mounting face on manifold 40-1180 lot M-2291");
                    s.Keyword("nc_type", "Nonconformance type", "ncr_type").Required().Header()
                        .Help("Controls which information sections show: supplier details are hidden for finished product; audit details show only for stock rejections.");
                    s.Keyword("severity", "Severity", "ncr_severity").Required().Header().Default("minor");
                    s.User("owner", "NCR owner").Required().Header().Default("$me");
                    s.Date("discovered_on", "Discovered on").Default("$today");
                    s.Keyword("site", "Site", "quality_site");
                    s.Link("source_record", "Source record", "complaint", "lab_investigation", "capa", "rma", "quality_event")
                        .Help("The complaint, lab investigation, inspection or CAPA this NCR came from (NC-006).");
                    s.TextArea("description", "Nonconformance description").Required();
                }, icon: "bi-flag")
                .Section("supplier_info", "Supplier information", s =>
                {
                    s.Text("supplier_name", "Supplier", 6).VisibleWhen(SupplierTypes).RequiredIn("create");
                    s.Text("supplier_code", "Supplier code", 3).VisibleWhen(SupplierTypes);
                    s.Text("po_number", "Purchase order", 3).VisibleWhen(SupplierTypes);
                    s.YesNo("supplier_fault", "Supplier fault?").VisibleWhen(SupplierTypes).Help("Yes reveals Create SCAR (NC-005).");
                }, icon: "bi-building")
                .Section("customer_info", "Customer information", s =>
                {
                    s.Link("customer", "Customer", "customer").VisibleWhen("nc_type=customer_return");
                    s.Text("customer_reference", "Customer reference (RMA, complaint)", 6).VisibleWhen("nc_type=customer_return");
                }, icon: "bi-person-badge")
                .Section("process_info", "Process information", s =>
                {
                    s.Text("process_step", "Process step", 4).VisibleWhen(ProcessTypes);
                    s.Text("work_center", "Work center / line", 4).VisibleWhen(ProcessTypes);
                    s.Text("work_order", "Work order", 4).VisibleWhen(ProcessTypes);
                    s.Text("equipment", "Equipment", 6).VisibleWhen(ProcessTypes);
                }, icon: "bi-gear-wide-connected")
                .Section("audit_info", "Audit information", s =>
                {
                    s.Text("audit_reference", "Stock audit reference", 6).VisibleWhen("nc_type=stock_rejection");
                    s.TextArea("audit_finding", "Audit finding").VisibleWhen("nc_type=stock_rejection");
                }, icon: "bi-clipboard-check")
                .Section("product", "Product information", s =>
                {
                    s.Text("part_number", "Part number", 4).Required().Header();
                    s.Text("part_description", "Part description", 5);
                    s.Text("part_revision", "Revision", 3);
                    s.Text("lot_number", "Lot / batch", 4).Required();
                    s.Number("quantity_affected", "Quantity affected", 3).Required().Min(0);
                    s.Text("uom", "Unit", 2).Default("ea");
                    s.Keywords("defect_codes", "Defect codes", "ncr_defect_code", 12).Required();
                    s.TextArea("defect_description", "Defect details (measurements, location, sample size)");
                }, icon: "bi-box-seam")
                .Section("containment", "Containment and material hold", s =>
                {
                    s.YesNo("material_hold", "Place material on hold?").Default("no")
                        .Help("Sends a hold to ERP/MES. The hold is released only when the disposition is approved (NC-007).");
                    s.Keyword("hold_status", "Hold status", "ncr_hold_status").ReadOnly().Header();
                    s.Text("hold_location", "Hold location", 6).VisibleWhen("material_hold=yes");
                    s.User("containment_owner", "Containment owner");
                    s.TextArea("containment_actions", "Containment actions (quantity and location of suspect product)");
                }, icon: "bi-bandaid")
                .Section("mrb", "MRB review", s =>
                {
                    s.Prompt("mrb_prompt", "Material Review Board members each approve with an e-signature. Agree the disposition lines here; the owner records how they were carried out in Disposition.");
                    s.Date("mrb_meeting", "MRB meeting date");
                    s.TextArea("mrb_recommendation", "MRB assessment and recommendation").RequiredIn("mrb_review");
                }, tab: "Disposition", icon: "bi-people")
                .Subform("dispositions", "Disposition lines", s =>
                {
                    s.Keyword("disposition_type", "Disposition", "ncr_disposition", 3).Required();
                    s.Number("disposition_qty", "Quantity", 2).Required().Min(0);
                    s.Text("disposition_instructions", "Instructions (scrap method, rework WI, return RMA)", 5);
                    s.YesNo("disposition_done", "Done?", 2);
                }, tab: "Disposition", rowNoun: "disposition", required: true, icon: "bi-signpost-split",
                   description: "One line per disposition. The quantities must add up to the quantity affected (INV-065).")
                .Section("disposition_summary", "Disposition summary", s =>
                {
                    s.Number("disposed_total", "Quantity dispositioned", 4).ReadOnly().Help("Sum of the disposition lines (calculated).");
                    s.Link("deviation_record", "Planned deviation (Use As Is)", "deviation")
                        .Help("Use As Is needs an active, approved planned deviation for this part and defect code before the NCR can complete (NC-004, INV-118).");
                    s.Text("return_authorization", "Supplier return authorization", 4);
                }, tab: "Disposition", icon: "bi-calculator")
                .Section("follow_up", "Follow-up", s =>
                {
                    s.Link("scar_record", "SCAR", "scar").Help("Created with \"Create SCAR\" when the supplier is at fault (NC-005).");
                    s.Link("capa_record", "CAPA", "capa");
                }, tab: "Disposition", icon: "bi-arrow-repeat")
                .Section("verification", "Verification and effectiveness", s =>
                {
                    s.YesNo("disposition_verified", "All dispositions carried out as approved?").RequiredIn("verification");
                    s.TextArea("verification_notes", "Verification evidence").RequiredIn("verification");
                }, tab: "Disposition", icon: "bi-patch-check");
            QualityCosts.Define(f);
            f.Workflow("ncr", "Nonconformance", w =>
            {
                w.Start("create", "Create").Purpose("Describe the nonconformance, the product and quantity affected, and contain the suspect material.")
                    .DueDays(2).Edit("origination", "supplier_info", "customer_info", "process_info", "audit_info", "product", "containment")
                    .Hide("mrb", "dispositions", "disposition_summary", "follow_up", "verification", "costs", "cost_summary");
                w.Phase("mrb_review", "MRB Review").Purpose("Material Review Board members review the nonconformance and agree the disposition. Each approval is e-signed; the NCR moves on when 75% have approved.")
                    .AssignGroup("mrb", all: true).Voting(75).DueDays(5).Edit("mrb", "dispositions", "containment", "follow_up").ReadOnlyRest().Hide("verification");
                w.Phase("disposition", "Disposition").Purpose("Carry out and record each disposition. The quantities must add up to the quantity affected (INV-065).")
                    .AssignField("owner").DueDays(10).Edit("dispositions", "disposition_summary", "containment", "follow_up", "costs").ReadOnlyRest().Hide("verification");
                w.Phase("final_approval", "Final Approval").Purpose("Quality approves the disposition. Approval releases any material hold (NC-007).")
                    .AssignGroup("qa-approvers").DueDays(3).ReadOnlyRest().Hide("verification");
                w.Phase("verification", "Verification").Purpose("Confirm every disposition was carried out and is effective. Use As Is needs an active planned deviation linked (NC-004).")
                    .AssignField("owner").DueDays(10).Edit("verification", "disposition_summary", "follow_up", "costs").ReadOnlyRest().MustComplete("action_item");
                w.Completed("completed", "Completed");
                w.Voided();

                w.Forward("create", "mrb_review", label: "Submit to MRB");
                w.Void("create");
                w.Forward("mrb_review", "disposition", label: "Approve MRB disposition", sign: "MRB approved");
                w.Back("mrb_review", "create");
                w.Void("mrb_review", sign: "Voided");
                w.Forward("disposition", "final_approval", label: "Send for final approval");
                w.Back("disposition", "mrb_review");
                w.Forward("final_approval", "verification", label: "Approve disposition", sign: "Disposition approved");
                w.Back("final_approval", "disposition");
                w.Forward("verification", "completed", label: "Verified: complete NCR", sign: "Verified");
                w.Back("verification", "final_approval");
            });
        });

        app.View("mine", "My NCRs", v => v.Category("1. My work").Forms("ncr").Mine()
            .Columns("number", "title", "phase", "due", "part_number", "severity").Sort("due", false).Default());
        app.View("open", "Open NCRs", v => v.Category("2. NCRs").Forms("ncr").Open()
            .Columns("number", "title", "phase", "due", "nc_type", "part_number", "severity", "owner"));
        app.View("mrb", "In MRB review", v => v.Category("2. NCRs").Forms("ncr").Open().Phases("mrb_review")
            .Columns("number", "title", "part_number", "quantity_affected", "due", "assignees"));
        app.View("on_hold", "Material on hold", v => v.Category("2. NCRs").Forms("ncr").Open().Filter("hold_status", "eq", "on_hold")
            .Columns("number", "title", "part_number", "lot_number", "quantity_affected", "hold_location", "phase"));
        app.View("supplier", "Supplier NCRs", v => v.Category("2. NCRs").Forms("ncr").All().Filter("nc_type", "eq", "incoming")
            .Columns("number", "title", "supplier_name", "part_number", "supplier_fault", "phase", "created").Sort("created"));
        app.View("overdue", "Overdue NCRs", v => v.Category("2. NCRs").Forms("ncr").Overdue()
            .Columns("number", "title", "phase", "due", "owner", "severity").Sort("due", false));
        app.View("by_part", "Open by part number", v => v.Category("2. NCRs").Forms("ncr").Open()
            .Columns("part_number", "number", "title", "lot_number", "quantity_affected", "phase").Sort("part_number", false));
        app.View("completed", "Completed NCRs", v => v.Category("3. Closed").Forms("ncr").Completed()
            .Columns("number", "title", "part_number", "severity", "cost_total", "modified").Sort("modified"));
        app.View("voided", "Voided NCRs", v => v.Category("3. Closed").Forms("ncr").Voided().Columns("number", "title", "owner", "modified"));
        app.View("actions", "NCR action items", v => v.Category("4. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        var deviations = d.Services.GetRequiredService<ActiveDeviations>();
        var dev = (await deviations.ForPartAsync("40-1180", "cosmetic")).FirstOrDefault();

        // 1. Completed with Use As Is under an active deviation, plus scrap; signatures throughout.
        var n1 = await d.CreateAsync("ncr", new()
        {
            ["subject"] = "Scratched mounting face on manifold 40-1180 lot M-2291",
            ["nc_type"] = "incoming", ["severity"] = "minor", ["owner"] = d.User("cwu"), ["discovered_on"] = DemoContext.Days(-20), ["site"] = "kalamazoo",
            ["description"] = "Receiving inspection found light scratches on the mounting face (zone C) of 140 of 500 manifolds. 20 parts also have scratches in the luer port area.",
            ["supplier_name"] = "Precision Micro Machining LLC", ["supplier_code"] = "SUP-0088", ["po_number"] = "PO-781455", ["supplier_fault"] = false,
            ["part_number"] = "40-1180", ["part_description"] = "3-port manifold, PC", ["part_revision"] = "D", ["lot_number"] = "M-2291",
            ["quantity_affected"] = 140m, ["defect_codes"] = new[] { "cosmetic" },
            ["defect_description"] = "Scratches 0.5–3 mm on zone C (120 pcs); scratches near luer port (20 pcs).",
            ["material_hold"] = true, ["hold_location"] = "Receiving cage R-3", ["containment_owner"] = d.User("cwu"),
            ["containment_actions"] = "140 parts segregated and tagged in receiving cage R-3; remaining 360 released after 100% inspection.",
        }, "cwu", daysAgo: 20);
        await d.RouteAsync(n1.Id, "mrb_review");
        await d.UpdateAsync(n1.Id, new() { ["mrb_meeting"] = DemoContext.Days(-17), ["mrb_recommendation"] = "Zone C scratches are cosmetic and covered by the housing: use as is under a planned deviation. Port-area scratches: scrap." }, "jlee");
        await SetDispositionsAsync(d, n1.Id, [("use_as_is", 120m, "Use under the planned deviation; zone C only.", false), ("scrap", 20m, "Scrap per SOP-MAT-011; crush and dispose.", false)]);
        foreach (var voter in new[] { "jlee", "sortiz", "akim" }) await d.RouteAsync(n1.Id, "disposition", asUser: voter);
        await d.UpdateAsync(n1.Id, new() { ["deviation_record"] = dev != null ? new List<Guid> { dev.Id } : null }, "cwu");
        await SetDispositionsAsync(d, n1.Id, [("use_as_is", 120m, "Use under the planned deviation; zone C only.", true), ("scrap", 20m, "Scrap per SOP-MAT-011; crush and dispose.", true)]);
        await d.RouteAsync(n1.Id, "final_approval");
        await d.RouteAsync(n1.Id, "verification", asUser: "sortiz");
        await d.UpdateAsync(n1.Id, new() { ["disposition_verified"] = true, ["verification_notes"] = "120 pcs issued to WO-55102 under the deviation; 20 pcs scrapped (scrap ticket ST-0912)." }, "cwu");
        await d.RouteAsync(n1.Id, "completed");

        // 2. In MRB voting: 2 of 4 members have approved (needs 3).
        var n2 = await d.CreateAsync("ncr", new()
        {
            ["subject"] = "Occlusion force sensor zero offset out of tolerance (lot FS-0824)",
            ["nc_type"] = "in_process", ["severity"] = "critical", ["owner"] = d.User("akim"), ["site"] = "kalamazoo",
            ["description"] = "In-process test on line 2 found force sensors reading +0.6 N at zero load (limit ±0.2 N). Linked to occlusion alarm complaints.",
            ["supplier_name"] = "SensaTek Components", ["supplier_code"] = "SUP-0201", ["po_number"] = "PO-779902", ["supplier_fault"] = true,
            ["process_step"] = "Sensor calibration (OP-40)", ["work_center"] = "Line 2", ["work_order"] = "WO-55087", ["equipment"] = "Force test fixture FT-2",
            ["part_number"] = "FS-200", ["part_description"] = "Occlusion force sensor", ["lot_number"] = "FS-0824", ["quantity_affected"] = 600m,
            ["defect_codes"] = new[] { "functional", "dimensional" },
            ["defect_description"] = "Zero offset +0.45 to +0.62 N on 37 of 40 sampled sensors.",
            ["material_hold"] = true, ["hold_location"] = "Line 2 quarantine rack Q-2", ["containment_owner"] = d.User("lgarcia"),
            ["containment_actions"] = "All 600 sensors of lot FS-0824 on hold; 231 built pumps quarantined at the Kalamazoo DC.",
        }, "akim", daysAgo: 6, rows: new()
        {
            ["dispositions"] =
            [
                new() { ["disposition_type"] = "return_to_supplier", ["disposition_qty"] = 520m, ["disposition_instructions"] = "Return to SensaTek under RA pending" },
                new() { ["disposition_type"] = "scrap", ["disposition_qty"] = 80m, ["disposition_instructions"] = "Scrap sensors removed from pumps during rework" },
            ],
        });
        await d.RouteAsync(n2.Id, "mrb_review");
        await d.UpdateAsync(n2.Id, new() { ["mrb_meeting"] = DemoContext.Days(-1), ["mrb_recommendation"] = "Return the unused lot to the supplier; scrap sensors removed from built pumps. Issue a SCAR to SensaTek." }, "jlee");
        await d.RouteAsync(n2.Id, "disposition", asUser: "jlee", comment: "Agree: supplier defect.");
        await d.RouteAsync(n2.Id, "disposition", asUser: "cwu", comment: "Agree. SCAR to follow.");

        // 3. In Disposition, quantities don't add up yet; Use As Is without a deviation (blocks completion).
        var n3 = await d.CreateAsync("ncr", new()
        {
            ["subject"] = "Label smudging on ST-40 pouch labels (lot 24-131)",
            ["nc_type"] = "finished_product", ["severity"] = "major", ["owner"] = d.User("jlee"), ["site"] = "kalamazoo",
            ["description"] = "Final inspection found smudged lot numbers on pouch labels. Barcode still scans; human-readable text partly illegible.",
            ["process_step"] = "Pouch labeling (OP-70)", ["work_center"] = "Packaging line P1", ["work_order"] = "WO-55120", ["equipment"] = "Thermal printer TP-3",
            ["part_number"] = "ST-40", ["part_description"] = "Sterile tubing set", ["lot_number"] = "24-131", ["quantity_affected"] = 1800m,
            ["defect_codes"] = new[] { "labeling" }, ["material_hold"] = true, ["hold_location"] = "Finished goods FG-4",
            ["containment_actions"] = "Lot 24-131 on hold in FG-4; printer TP-3 taken out of service.",
        }, "jlee", daysAgo: 10, rows: new()
        {
            ["dispositions"] =
            [
                new() { ["disposition_type"] = "sort", ["disposition_qty"] = 1800m, ["disposition_instructions"] = "100% visual inspection of human-readable lot number" },
                new() { ["disposition_type"] = "rework", ["disposition_qty"] = 260m, ["disposition_instructions"] = "Over-label per WI-PKG-022" },
            ],
        });
        await d.RouteAsync(n3.Id, "mrb_review");
        await d.UpdateAsync(n3.Id, new() { ["mrb_recommendation"] = "Sort 100%; over-label illegible pouches. Legible-but-smudged pouches may be used as is under a deviation." }, "sortiz");
        foreach (var voter in new[] { "sortiz", "jlee", "akim" }) await d.RouteAsync(n3.Id, "disposition", asUser: voter);
        await SetDispositionsAsync(d, n3.Id, [("rework", 260m, "Over-label per WI-PKG-022", false), ("use_as_is", 1480m, "Barcode verified; text legible at 30 cm", false)]);
        await d.SetDueAsync(n3.Id, DemoContext.Days(-1));

        // 4. New NCR from a customer return, just created.
        await d.CreateAsync("ncr", new()
        {
            ["subject"] = "Cracked luer connector on returned VX-200 administration set",
            ["nc_type"] = "customer_return", ["severity"] = "major", ["owner"] = d.User("jlee"), ["site"] = "boston",
            ["description"] = "Returned set from Boston General shows a cracked female luer at the pump inlet. Cracking consistent with over-tightening or solvent exposure.",
            ["customer_reference"] = "RMA-00002",
            ["part_number"] = "AS-VX200", ["part_description"] = "VX-200 administration set", ["lot_number"] = "AS-2409", ["quantity_affected"] = 4m,
            ["defect_codes"] = new[] { "functional" },
        }, "jlee", daysAgo: 1);
    }

    private static async Task SetDispositionsAsync(DemoContext d, Guid id, (string Type, decimal Qty, string Note, bool Done)[] lines)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(id);
        var form = snap.Form;
        var data = new RecordData();
        var rows = data.RowsOf("dispositions");
        var i = 0;
        foreach (var (type, qty, note, done) in lines)
        {
            var row = new RowBag { IsNew = true, Order = i++ };
            row.Values.Set("disposition_type", FieldCodec.FromObject(form.Fields.First(f => f.Key == "disposition_type"), type, snap.Config));
            row.Values.Set("disposition_qty", Val.Of(qty));
            row.Values.Set("disposition_instructions", Val.Of(note));
            row.Values.Set("disposition_done", Val.Bool(done));
            rows.Add(row);
        }
        var res = await records.SaveAsync(snap, data, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException("Demo dispositions failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }
}

public sealed class NcrBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["ncr"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var lines = ctx.Proposed.RowsOf("dispositions");
        ctx.Set("disposed_total", lines.Count == 0 ? null : lines.Sum(r => r.Values.Number("disposition_qty") ?? 0m));
        QualityCosts.Compute(ctx);

        // NC-007: the hold is placed by the owner but released only by an approved disposition.
        var status = ctx.Current?.KeywordKey("hold_status");
        var wasHeld = ctx.Current?.Data.Main.IsYes("material_hold") == true;
        var held = ctx.Proposed.Main.IsYes("material_hold");
        if (held && status is null or "none") ctx.Set("hold_status", "on_hold");
        else if (!held && wasHeld && status == "on_hold")
            ctx.Error("material_hold", "The material hold can only be released by approving the disposition (NC-007).");
        else if (!held && status is null) ctx.Set("hold_status", "none");
        return Task.CompletedTask;
    }

    public override Task OnCreatedAsync(RecordContext ctx) => SendHoldMessageAsync(ctx);

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.Kind != RouteKind.Forward) return;
        var lines = s.Rows("dispositions");

        // INV-065: disposition quantities add up to the quantity affected before leaving Disposition.
        if (ctx.From.Key == "disposition")
        {
            var affected = s.Number("quantity_affected") ?? 0m;
            var total = lines.Sum(r => r.Values.Number("disposition_qty") ?? 0m);
            if (total != affected)
                ctx.Error($"The disposition quantities add up to {total:0.##} but the quantity affected is {affected:0.##}. They must be equal before the NCR leaves Disposition (INV-065).");
            foreach (var r in lines)
            {
                var type = s.KeywordKey(r, "disposition_type");
                if (type is "return_to_supplier" or "scrap" or "rework" or "repair" && string.IsNullOrWhiteSpace(r.Values.Text("disposition_instructions")))
                    ctx.Error($"Disposition line {r.Order + 1}: {s.Config.Keyword("ncr_disposition", type)?.Label} needs instructions (return information, scrap method or work instruction, NC-002).");
            }
        }

        // NC-004 / INV-118: Use As Is completes only with a linked, active (approved) deviation for this part and defect.
        if (ctx.To.Key == "completed" && lines.Any(r => s.KeywordKey(r, "disposition_type") == "use_as_is"))
        {
            var problem = await UseAsIsProblemAsync(ctx);
            if (problem != null) ctx.Error(problem);
        }
    }

    internal static async Task<string?> UseAsIsProblemAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var linked = await QualityLinks.LinkedAsync(ctx, "deviation_record", "deviation");
        if (linked.Count == 0)
            return "Use As Is needs an active, approved planned deviation. Use \"Create planned deviation\" or link one in Disposition summary (NC-004).";
        var part = s.Text("part_number")?.Trim();
        var codes = s.KeywordKeys("defect_codes").ToList();
        var active = await ctx.Get<ActiveDeviations>().ForPartAsync(part);
        var match = active.FirstOrDefault(a => linked.Any(l => l.Id == a.Id) && (a.DefectKey == null || codes.Contains(a.DefectKey)));
        if (match != null) return null;
        var first = linked[0];
        return active.Any(a => linked.Any(l => l.Id == a.Id))
            ? $"{first.Number} is active but for a defect code that isn't on this NCR. Link a deviation for part {part} and one of this NCR's defect codes (INV-118)."
            : $"{first.Number} isn't active for part {part}. Use As Is needs the deviation approved and active before the NCR can complete (NC-004, INV-118).";
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        await SendHoldMessageAsync(ctx);
        // NC-007: approving the disposition releases the material hold.
        if (ctx.To.Key == "verification" && ctx.From.Key == "final_approval" && ctx.Snapshot.KeywordKey("hold_status") == "on_hold")
        {
            await ctx.Records.UpdateAsync(ctx.Record.Id, new() { ["hold_status"] = "released" }, "Disposition approved (NC-007)");
            QueueHoldMessage(ctx, "released");
            ctx.Get<AuditService>().Record(ctx.Record.Id, "Material hold released", $"Hold on {ctx.Snapshot.Text("part_number")} lot {ctx.Snapshot.Text("lot_number")} released after disposition approval (NC-007)");
        }
    }

    /// <summary>NC-007 / SAF-041: the hold is sent to ERP/MES through the transactional outbox (INV-040).</summary>
    private static async Task SendHoldMessageAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.KeywordKey("hold_status") != "on_hold") return;
        if (await ctx.Db.Audit.AnyAsync(a => a.RecordId == ctx.Record.Id && a.Action == "Material hold placed")) return;
        QueueHoldMessage(ctx, "on_hold");
        ctx.Get<AuditService>().Record(ctx.Record.Id, "Material hold placed",
            $"{ctx.Snapshot.Number("quantity_affected"):0.##} {ctx.Snapshot.Text("uom")} of {ctx.Snapshot.Text("part_number")} lot {ctx.Snapshot.Text("lot_number")} on hold at {ctx.Snapshot.Text("hold_location") ?? "the containment location"}");
        await ctx.Db.SaveChangesAsync();
    }

    private static void QueueHoldMessage(RecordContext ctx, string status)
    {
        var s = ctx.Snapshot;
        ctx.Db.Outbox.Add(new OutboxMessage
        {
            Type = "erp.material-hold", RecordId = s.Record.Id, CreatedAt = DateTime.UtcNow,
            Payload = JsonSerializer.Serialize(new { ncr = s.Record.Number, status, part = s.Text("part_number"), lot = s.Text("lot_number"), quantity = s.Number("quantity_affected"), location = s.Text("hold_location") }),
        });
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (!ctx.IsOpen() || !await ctx.CanActAsync()) return [];
        var s = ctx.Snapshot;
        var list = new List<RecordAction>();
        if (s.Bool("supplier_fault") == true && s.Links("scar_record").Count == 0 && FormRules.Matches("nc_type=incoming||nc_type=in_process||nc_type=stock_rejection", s.Data.Main, s.Form, s.Config))
            list.Add(new RecordAction { Key = "ncr-scar", Label = "Create SCAR", Icon = "bi-truck", Group = "Create",
                Description = "Supplier fault: raise a supplier corrective action request with this NCR's details (NC-005)." });
        var useAsIs = s.Rows("dispositions").Where(r => s.KeywordKey(r, "disposition_type") == "use_as_is").Sum(r => r.Values.Number("disposition_qty") ?? 0m);
        if (useAsIs > 0 && s.Links("deviation_record").Count == 0)
        {
            var codes = s.KeywordKeys("defect_codes").Select(k => (k, s.Config.Keyword("ncr_defect_code", k)?.Label ?? k)).ToList();
            list.Add(new RecordAction
            {
                Key = "ncr-deviation", Label = "Create planned deviation", Icon = "bi-sign-turn-right", Group = "Create",
                Description = "Use As Is needs an approved, active deviation for this part and defect code (NC-004, DEV-004).",
                Inputs =
                [
                    new ActionField("defect", "Defect code", "select", true, Options: codes, Default: codes.FirstOrDefault().k),
                    new ActionField("qty", "Maximum quantity", "number", true, Default: useAsIs.ToString("0.##")),
                    new ActionField("until", "Valid until", "date", true, Default: BusinessCalendar.Today.AddDays(60).ToString("yyyy-MM-dd")),
                ],
            });
        }
        if (s.Links("capa_record").Count == 0)
            list.Add(new RecordAction { Key = "ncr-capa", Label = "Create 8D CAPA", Icon = "bi-shield-check", Group = "Create",
                Description = "Start an 8D corrective action linked to this NCR (NC-005)." });
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (!ctx.IsOpen() || !await ctx.CanActAsync()) return ActionOutcome.Fail("Only the current assignee or an application manager can do this.");
        var s = ctx.Snapshot;
        var me = ctx.Me.Id;
        (Record? Record, string? Error) result;
        switch (actionKey)
        {
            case "ncr-scar":
                result = await QualityLinks.CreateFromAsync(ctx, "scar", new()
                {
                    ["subject"] = s.Text("subject"), ["owner"] = me, ["priority"] = s.KeywordKey("severity") switch { "critical" => "critical", "major" => "high", _ => "medium" },
                    ["source_record"] = s.Record.Id, ["description"] = $"{s.Text("description")}\n\nDefect details: {s.Text("defect_description")}",
                    ["supplier_name"] = s.Text("supplier_name"), ["supplier_code"] = s.Text("supplier_code"), ["po_number"] = s.Text("po_number"),
                    ["part_number"] = s.Text("part_number"), ["part_description"] = s.Text("part_description"), ["lot_number"] = s.Text("lot_number"),
                    ["quantity_affected"] = s.Number("quantity_affected"), ["containment_actions"] = s.Text("containment_actions"),
                }, "scar_record");
                break;
            case "ncr-deviation":
                var defect = input.Get("defect");
                if (defect == null || !s.KeywordKeys("defect_codes").Contains(defect)) return ActionOutcome.Fail("Choose one of this NCR's defect codes.");
                if (!decimal.TryParse(input.Get("qty"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var qty) || qty <= 0)
                    return ActionOutcome.Fail("Enter the maximum quantity the deviation covers.");
                if (!DateOnly.TryParse(input.Get("until"), out var until) || until < BusinessCalendar.Today) return ActionOutcome.Fail("Choose an end date that isn't in the past.");
                var existing = await ctx.Get<ActiveDeviations>().ForPartAsync(s.Text("part_number"), defect);
                if (existing.Count > 0)
                    return ActionOutcome.Fail($"{existing[0].Number} is already active for this part and defect code. Link it in Disposition summary instead (INV-118).");
                result = await QualityLinks.CreateFromAsync(ctx, "deviation", new()
                {
                    ["subject"] = $"Use as is: {s.Text("subject")}", ["owner"] = me, ["deviation_type"] = "product",
                    ["part_number"] = s.Text("part_number"), ["part_description"] = s.Text("part_description"), ["defect_code"] = defect,
                    ["lot_numbers"] = s.Text("lot_number"), ["source_record"] = s.Record.Id, ["max_quantity"] = qty, ["max_lots"] = 1m,
                    ["valid_from"] = BusinessCalendar.Today, ["valid_to"] = until,
                    ["scope"] = $"Use as is {qty:0.##} {s.Text("uom")} of {s.Text("part_number")} lot {s.Text("lot_number")} ({s.Record.Number}).",
                    ["justification"] = s.Text("mrb_recommendation"),
                }, "deviation_record");
                break;
            case "ncr-capa":
                result = await QualityLinks.CreateFromAsync(ctx, "capa", new()
                {
                    ["subject"] = s.Text("subject"), ["capa_type"] = "eight_d", ["source"] = "ncr", ["owner"] = me,
                    ["priority"] = s.KeywordKey("severity") switch { "critical" => "critical", "major" => "high", _ => "medium" },
                    ["product"] = $"{s.Text("part_number")} {s.Text("part_description")}", ["source_record"] = s.Record.Id,
                    ["description"] = s.Text("description"), ["d2_what"] = s.Text("defect_description"), ["d3_actions"] = s.Text("containment_actions"),
                }, "capa_record");
                break;
            default:
                return ActionOutcome.Fail("This action isn't available.");
        }
        return result.Record == null ? ActionOutcome.Fail(result.Error!) : ActionOutcome.Ok($"{result.Record.Number} created and linked to {s.Record.Number}.", result.Record.Id);
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var lines = s.Rows("dispositions").Select(r => new DispositionLine(
            s.Keyword(r, "disposition_type")?.Label ?? "–", s.Keyword(r, "disposition_type")?.Color, r.Values.Number("disposition_qty") ?? 0m, r.Values.Bool("disposition_done") == true)).ToList();
        var useAsIs = s.Rows("dispositions").Any(r => s.KeywordKey(r, "disposition_type") == "use_as_is");
        var active = await ctx.Get<ActiveDeviations>().ForPartAsync(s.Text("part_number"));
        var model = new NcrSummaryModel(s.Number("quantity_affected") ?? 0m, s.Text("uom") ?? "ea", lines, s.KeywordKey("hold_status"), s.Text("hold_location"),
            useAsIs, useAsIs ? await UseAsIsProblemAsync(ctx) : null, active, s.PhaseKey);
        var panels = new List<RecordPanel>
        {
            new() { Key = "ncr-alerts", Title = "Alerts", Placement = "top", Partial = "~/Modules/Quality/Views/_NcrAlerts.cshtml", Model = model },
            new() { Key = "ncr-disposition", Title = "Disposition", Icon = "bi-signpost-split", Placement = "side", Order = 10, Partial = "~/Modules/Quality/Views/_NcrDisposition.cshtml", Model = model },
        };
        if (QualityCosts.Panel(ctx) is { } costs) panels.Add(costs);
        return panels;
    }
}

public sealed record DispositionLine(string Label, string? Color, decimal Qty, bool Done);

public sealed record NcrSummaryModel(decimal Affected, string Uom, List<DispositionLine> Lines, string? HoldStatus, string? HoldLocation,
    bool UseAsIs, string? UseAsIsProblem, List<ActiveDeviationInfo> PartDeviations, string PhaseKey)
{
    public decimal Disposed => Lines.Sum(l => l.Qty);
    public decimal Remaining => Affected - Disposed;
    public int Percent => Affected > 0 ? (int)Math.Min(100, Math.Round(Disposed / Affected * 100)) : 0;
}

internal static class SnapshotRowExtensions
{
    public static Keyword? Keyword(this RecordSnapshot s, RowBag row, string key) => s.Config.Keyword(row.Values.Ref(key));
}
