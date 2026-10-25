using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// Customer returns §12.9: Draft → Initial Approval → Receiving Inspection → Disposition → Verification → Completed (RMA-002).
/// Parts list with computed totals (RMA-001), "Load parts list" (RMA-003), received ≤ authorized unless overridden with a justification (INV-066).
/// </summary>
public sealed class RmaApp : IPackagedApp
{
    public string Key => "rma";
    public int Order => 44;

    public void Define(AppBuilder app)
    {
        app.Name("Customer Returns (RMA)")
            .Description("Authorize, receive, inspect and disposition returned product, with credit and replacement tracking.")
            .Icon("bi-box-arrow-in-left").Color("#65a30d").Suite("Quality").Order(70).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        app.Keywords("rma_reason", "Return reason", k => k
            .Add("defective", "Defective / complaint", "danger").Add("damaged", "Damaged in transit", "warning").Add("wrong_item", "Wrong item shipped")
            .Add("expired", "Expired / short-dated").Add("recall", "Field action / recall", "danger").Add("customer_error", "Ordered in error", "secondary"));
        app.Keywords("rma_inspection_result", "Inspection result", k => k
            .Add("confirmed", "Failure confirmed", "danger").Add("no_failure", "No failure found", "success").Add("damaged", "Shipping damage", "warning"));
        app.Keywords("rma_disposition", "Return disposition", k => k
            .Add("return_to_stock", "Return to stock").Add("scrap", "Scrap", "danger").Add("repair", "Repair and return", "info")
            .Add("return_to_customer", "Return to customer as is").Add("replace", "Replace", "primary"));
        app.Keywords("rma_accounting", "Accounting disposition", k => k
            .Add("credit", "Credit").Add("replacement", "Replacement").Add("repair_charge", "Repair charge").Add("no_charge", "No charge"));

        app.Form("rma", "Customer Return", f => f
            .Icon("bi-box-arrow-in-left")
            .Description("A return material authorization: parts and values, receiving, inspection, disposition and credit.")
            .Numbering("RMA-")
            .Title("{subject}")
            .Section("return_info", "Return information", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200);
                s.Link("customer", "Customer", "customer").Header();
                s.Text("customer_contact", "Customer contact", 4);
                s.User("owner", "RMA owner").Required().Header().Default("$me");
                s.Keyword("rma_reason", "Reason", "rma_reason").RequiredIn("draft").Header();
                s.Link("complaint", "Related complaint", "complaint");
                s.Date("requested_on", "Requested on").Default("$today");
                s.Text("return_method", "Carrier / tracking", 6);
                s.TextArea("description", "Problem description").Required();
            }, icon: "bi-info-circle")
            .Subform("rma_parts", "Parts list", s =>
            {
                s.Text("part_number", "Part", 3).Required();
                s.Text("part_description", "Description", 3);
                s.Number("qty_authorized", "Qty authorized", 2).Required().Min(1);
                s.Currency("unit_value", "Unit value", "USD", 2).Required().Min(0);
                s.Currency("line_total", "Total", "USD", 2).ReadOnly();
            }, rowNoun: "part", required: true, icon: "bi-list-ol", description: "Total = quantity × unit value (calculated).")
            .Section("rma_totals", "Authorization", s =>
            {
                s.Currency("authorized_total", "Authorized value", "USD", 4).ReadOnly().Header();
                s.Number("authorized_units", "Units authorized", 4).ReadOnly();
            }, icon: "bi-calculator")
            .Subform("rma_receipts", "Receiving", s =>
            {
                s.Text("rcv_part_number", "Part", 3).Required();
                s.Date("rcv_ship_date", "Shipped on", 2);
                s.Number("rcv_qty_received", "Received", 2).Required().Min(0);
                s.Number("rcv_qty_inspected", "Inspected", 2).Min(0);
                s.Number("rcv_qty_voided", "Voided", 2).Min(0);
            }, tab: "Receiving & inspection", rowNoun: "receipt", required: true, icon: "bi-box-seam",
               description: "\"Load parts list\" copies the authorized parts here. Received quantities can't exceed what was authorized without an override (INV-066).")
            .Section("receiving_override", "Receiving override", s =>
            {
                s.Date("received_on", "Received on");
                s.YesNo("receiving_override", "Accept more than authorized?").Default("no").Help("Needed only when a received quantity is more than authorized (INV-066).");
                s.TextArea("override_justification", "Override justification").VisibleWhen("receiving_override=yes");
            }, tab: "Receiving & inspection", icon: "bi-shield-exclamation")
            .Section("inspection", "Inspection", s =>
            {
                s.Keyword("inspection_result", "Inspection result", "rma_inspection_result").RequiredIn("receiving_inspection");
                s.TextArea("inspection_notes", "Inspection findings").RequiredIn("receiving_inspection");
                s.Link("ncr_record", "NCR", "ncr");
                s.Link("capa_record", "CAPA", "capa");
            }, tab: "Receiving & inspection", icon: "bi-search")
            .Section("disposition", "Disposition", s =>
            {
                s.Keyword("rma_disposition", "Disposition", "rma_disposition").RequiredIn("disposition");
                s.Keyword("accounting_disposition", "Accounting disposition", "rma_accounting").RequiredIn("disposition");
                s.Currency("credit_amount", "Credit / charge amount", "USD", 4).Min(0);
                s.TextArea("disposition_notes", "Disposition notes");
            }, tab: "Disposition", icon: "bi-signpost-split")
            .Section("verification", "Verification and effectiveness", s =>
            {
                s.YesNo("resolved", "Customer issue resolved?").RequiredIn("verification");
                s.TextArea("verification_notes", "Verification notes").RequiredIn("verification");
            }, tab: "Disposition", icon: "bi-patch-check")
            .Workflow("rma", "Customer return", w =>
            {
                w.Start("draft", "Draft").Purpose("Describe the return and list the parts and values to authorize.")
                    .DueDays(2).Edit("return_info", "rma_parts").ReadOnly("rma_totals").Hide("rma_receipts", "receiving_override", "inspection", "disposition", "verification");
                w.Phase("initial_approval", "Initial Approval").Purpose("Quality authorizes the return (e-signed).")
                    .AssignGroup("qa-approvers").DueDays(2).ReadOnlyRest().Hide("rma_receipts", "receiving_override", "inspection", "disposition", "verification");
                w.Phase("receiving_inspection", "Receiving Inspection").Purpose("Receive the parts against the authorization and inspect them. Return information is now read-only.")
                    .AssignField("owner").DueDays(10).Edit("rma_receipts", "receiving_override", "inspection").ReadOnly("return_info", "rma_parts", "rma_totals").Hide("disposition", "verification");
                w.Phase("disposition", "Disposition").Purpose("Decide what happens to the parts and the credit, replacement or charge.")
                    .AssignField("owner").DueDays(5).Edit("disposition").ReadOnlyRest().Hide("verification");
                w.Phase("verification", "Verification").Purpose("Confirm the disposition was carried out and the customer's issue is resolved.")
                    .AssignField("owner").DueDays(5).Edit("verification").ReadOnlyRest().MustComplete("action_item");
                w.Completed("completed", "Completed");
                w.Voided();

                w.Forward("draft", "initial_approval", label: "Submit for authorization");
                w.Void("draft");
                w.Forward("initial_approval", "receiving_inspection", label: "Authorize return", sign: "Return authorized");
                w.Back("initial_approval", "draft");
                w.Void("initial_approval");
                w.Forward("receiving_inspection", "disposition", label: "Inspection complete");
                w.Back("receiving_inspection", "initial_approval");
                w.Forward("disposition", "verification", label: "Send to verification");
                w.Back("disposition", "receiving_inspection");
                w.Forward("verification", "completed", label: "Verify and complete", sign: "Verified");
                w.Back("verification", "disposition");
            }));

        app.View("mine", "My returns", v => v.Category("1. My work").Forms("rma").Mine()
            .Columns("number", "title", "phase", "due", "customer", "rma_reason").Sort("due", false).Default());
        app.View("open", "Open returns", v => v.Category("2. Returns").Forms("rma").Open()
            .Columns("number", "title", "customer", "phase", "due", "authorized_total", "owner"));
        app.View("awaiting", "Awaiting receipt", v => v.Category("2. Returns").Forms("rma").Open().Phases("receiving_inspection")
            .Columns("number", "title", "customer", "authorized_units", "due", "owner"));
        app.View("overrides", "Received over authorization", v => v.Category("2. Returns").Forms("rma").All().Filter("receiving_override", "eq", "yes")
            .Columns("number", "title", "customer", "phase", "override_justification", "modified"));
        app.View("completed", "Completed returns", v => v.Category("3. Closed").Forms("rma").Completed()
            .Columns("number", "title", "customer", "rma_disposition", "accounting_disposition", "credit_amount", "modified").Sort("modified"));
        app.View("voided", "Voided returns", v => v.Category("3. Closed").Forms("rma").Voided().Columns("number", "title", "customer", "modified"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        var db = d.Db;
        async Task<Guid?> Find(string form, string field, string value)
        {
            if (!d.Config.FormsByKey.TryGetValue(form, out var f)) return null;
            return await db.RecordValues.Where(v => v.FieldKey == field && v.RowId == null && v.Text == value && db.Records.Any(r => r.Id == v.RecordId && r.FormId == f.Id))
                .Select(v => (Guid?)v.RecordId).FirstOrDefaultAsync();
        }
        var boston = await Find("customer", "customer_name", "Boston General Hospital");
        var stMary = await Find("customer", "customer_name", "St. Mary's Regional Medical Center");
        var luer = await Find("complaint", "subject", "Luer connector cracked on VX-200 administration set");
        var occlusion = await Find("complaint", "subject", "VX-200 did not alarm on downstream occlusion; patient received delayed therapy");

        // 1. In Receiving Inspection: one more set arrived than authorized, accepted with an override.
        var r1 = await d.CreateAsync("rma", new()
        {
            ["subject"] = "Return of cracked VX-200 administration sets (lot AS-2409)", ["customer"] = boston, ["customer_contact"] = "Priya Raman",
            ["owner"] = d.User("jlee"), ["rma_reason"] = "defective", ["complaint"] = luer, ["requested_on"] = DemoContext.Days(-6),
            ["return_method"] = "UPS 1Z 4F8 902 03 1188 7741",
            ["description"] = "Four administration sets with cracked female luers returned for failure investigation.",
        }, "jlee", daysAgo: 6, rows: new()
        {
            ["rma_parts"] = [new() { ["part_number"] = "AS-VX200", ["part_description"] = "VX-200 administration set", ["qty_authorized"] = 4m, ["unit_value"] = 18.5m }],
        });
        await d.RouteAsync(r1.Id, "initial_approval", assignees: ["sortiz"]);
        await d.RouteAsync(r1.Id, "receiving_inspection");
        await d.UpdateAsync(r1.Id, new()
        {
            ["received_on"] = DemoContext.Days(-1), ["receiving_override"] = true,
            ["override_justification"] = "Customer included a fifth set from the same lot that cracked during set-up; accepted for the failure investigation.",
        }, "jlee");
        await SetReceiptsAsync(d, r1.Id, ("AS-VX200", DemoContext.Days(-3), 5m, 5m));

        // 2. Completed with credit and signatures.
        var r2 = await d.CreateAsync("rma", new()
        {
            ["subject"] = "Return of VX-200 pump for occlusion alarm investigation", ["customer"] = stMary, ["customer_contact"] = "Dr. Angela Ruiz",
            ["owner"] = d.User("jlee"), ["rma_reason"] = "defective", ["complaint"] = occlusion, ["requested_on"] = DemoContext.Days(-24),
            ["return_method"] = "Acme courier pickup",
            ["description"] = "Pump VX2-24180-0412 returned for failure analysis; customer received a loaner and a replacement.",
        }, "jlee", daysAgo: 24, rows: new()
        {
            ["rma_parts"] =
            [
                new() { ["part_number"] = "VX-200-US", ["part_description"] = "VX-200 infusion pump", ["qty_authorized"] = 1m, ["unit_value"] = 2450m },
                new() { ["part_number"] = "AS-VX200", ["part_description"] = "Administration set in use at the time", ["qty_authorized"] = 1m, ["unit_value"] = 18.5m },
            ],
        });
        await d.RouteAsync(r2.Id, "initial_approval", assignees: ["dokafor"]);
        await d.RouteAsync(r2.Id, "receiving_inspection");
        await SetReceiptsAsync(d, r2.Id, ("VX-200-US", DemoContext.Days(-22), 1m, 1m), ("AS-VX200", DemoContext.Days(-22), 1m, 1m));
        await d.UpdateAsync(r2.Id, new()
        {
            ["received_on"] = DemoContext.Days(-20), ["inspection_result"] = "confirmed",
            ["inspection_notes"] = "Occlusion alarm latency 95 s at 15 psi (spec ≤ 30 s). Force sensor zero offset +0.58 N.",
        }, "jlee");
        await d.RouteAsync(r2.Id, "disposition");
        await d.UpdateAsync(r2.Id, new()
        {
            ["rma_disposition"] = "scrap", ["accounting_disposition"] = "replacement", ["credit_amount"] = 0m,
            ["disposition_notes"] = "Pump kept as a retained sample for the 8D, then scrapped; replacement pump shipped on loaner conversion.",
        }, "jlee");
        await d.RouteAsync(r2.Id, "verification");
        await d.UpdateAsync(r2.Id, new() { ["resolved"] = true, ["verification_notes"] = "Customer confirmed the replacement pump passed incoming biomed checks." }, "jlee");
        await d.RouteAsync(r2.Id, "completed");

        // 3. Draft for a short-dated return.
        await d.CreateAsync("rma", new()
        {
            ["subject"] = "Short-dated ST-40 stock returned by distributor", ["customer"] = await Find("customer", "customer_name", "MedSupply Europe B.V."),
            ["customer_contact"] = "Joost de Vries", ["owner"] = d.User("lgarcia"), ["rma_reason"] = "expired",
            ["description"] = "Distributor wants to return 2 cartons of ST-40 with less than 6 months of shelf life.",
        }, "lgarcia", daysAgo: 1, rows: new()
        {
            ["rma_parts"] = [new() { ["part_number"] = "ST-40", ["part_description"] = "Sterile tubing set (carton of 50)", ["qty_authorized"] = 2m, ["unit_value"] = 610m }],
        });
    }

    private static async Task SetReceiptsAsync(DemoContext d, Guid id, params (string Part, DateOnly Shipped, decimal Received, decimal Inspected)[] lines)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(id);
        var data = new RecordData();
        foreach (var (part, shipped, received, inspected) in lines)
        {
            var row = new RowBag { IsNew = true, Order = data.RowsOf("rma_receipts").Count };
            row.Values.Set("rcv_part_number", Val.Of(part));
            row.Values.Set("rcv_ship_date", Val.Of(shipped));
            row.Values.Set("rcv_qty_received", Val.Of(received));
            row.Values.Set("rcv_qty_inspected", Val.Of(inspected));
            data.RowsOf("rma_receipts").Add(row);
        }
        var res = await records.SaveAsync(snap, data, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException("Demo receipts failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }
}

public sealed class RmaBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["rma"];

    private static string Key(string? part) => (part ?? "").Trim().ToUpperInvariant();

    public override Task OnSavingAsync(SavingContext ctx)
    {
        // RMA-001: line total = quantity × unit value; authorized value and units.
        var parts = ctx.Proposed.RowsOf("rma_parts");
        foreach (var row in parts)
        {
            var qty = row.Values.Number("qty_authorized");
            var unit = row.Values.Number("unit_value");
            ctx.SetRow(row, "line_total", qty is { } q && unit is { } u ? Math.Round(q * u, 2) : null);
        }
        ctx.Set("authorized_total", parts.Count == 0 ? null : parts.Sum(r => r.Values.Number("line_total") ?? 0m));
        ctx.Set("authorized_units", parts.Count == 0 ? null : parts.Sum(r => r.Values.Number("qty_authorized") ?? 0m));

        // INV-066: received ≤ authorized per part, unless an override with a justification exists.
        var authorized = parts.GroupBy(r => Key(r.Values.Text("part_number"))).ToDictionary(g => g.Key, g => g.Sum(r => r.Values.Number("qty_authorized") ?? 0m));
        var receipts = ctx.Proposed.RowsOf("rma_receipts");
        var over = receipts.GroupBy(r => Key(r.Values.Text("rcv_part_number")))
            .Select(g => (Part: g.First().Values.Text("rcv_part_number"), Received: g.Sum(r => r.Values.Number("rcv_qty_received") ?? 0m), Authorized: authorized.GetValueOrDefault(g.Key)))
            .Where(x => x.Received > x.Authorized).ToList();
        var overridden = ctx.Proposed.Main.IsYes("receiving_override") && !string.IsNullOrWhiteSpace(ctx.Proposed.Main.Text("override_justification"));
        if (over.Count > 0 && !overridden)
            foreach (var (part, received, auth) in over)
                ctx.Error("receiving_override", $"{received:0.##} of part {part} received but only {auth:0.##} authorized. Accept the extra with an override and a justification, or correct the quantity (INV-066).");
        foreach (var r in receipts)
            if ((r.Values.Number("rcv_qty_inspected") ?? 0) > (r.Values.Number("rcv_qty_received") ?? 0))
                ctx.Error("rcv_qty_inspected", $"Receiving row {r.Order + 1}: more inspected than received.", r.Id);
        return Task.CompletedTask;
    }

    /// <summary>RMA-003: the parts list is copied to receiving when the RMA reaches Receiving Inspection.</summary>
    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key == "receiving_inspection" && ctx.Snapshot.Rows("rma_receipts").Count == 0) await LoadPartsAsync(ctx);
    }

    private static async Task<int> LoadPartsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var have = s.Rows("rma_receipts").Select(r => Key(r.Values.Text("rcv_part_number"))).ToHashSet();
        var data = new RecordData();
        var rows = data.RowsOf("rma_receipts");
        rows.AddRange(s.Rows("rma_receipts").Select(r => r.Clone()));
        var added = 0;
        foreach (var p in s.Rows("rma_parts").Where(p => !have.Contains(Key(p.Values.Text("part_number")))))
        {
            var row = new RowBag { IsNew = true, Order = rows.Count };
            row.Values.Set("rcv_part_number", Val.Of(p.Values.Text("part_number")));
            rows.Add(row);
            added++;
        }
        if (added == 0) return 0;
        var fresh = await ctx.Records.LoadRequiredAsync(s.Record.Id);
        var res = await ctx.Records.SaveAsync(fresh, data, new SaveOptions { System = true });
        return res.Ok ? added : 0;
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var list = new List<RecordAction>();
        if (!ctx.IsOpen() || !await ctx.CanActAsync()) return list;
        if (s.PhaseKey == "receiving_inspection")
            list.Add(new() { Key = "rma-load", Label = "Load parts list", Icon = "bi-list-check", Group = "Actions",
                Description = "Copy authorized parts that aren't listed yet into Receiving (RMA-003)." });
        if (s.PhaseKey is "receiving_inspection" or "disposition")
        {
            if (s.Links("ncr_record").Count == 0)
                list.Add(new() { Key = "rma-ncr", Label = "Create NCR", Icon = "bi-exclamation-octagon", Group = "Create", Description = "Raise a nonconformance for the returned product (RMA-005)." });
            if (s.Links("capa_record").Count == 0)
                list.Add(new() { Key = "rma-capa", Label = "Create CAPA", Icon = "bi-shield-check", Group = "Create", Description = "Raise a CAPA from this return (RMA-005)." });
        }
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if ((await ActionsAsync(ctx)).All(a => a.Key != actionKey)) return ActionOutcome.Fail("This action isn't available.");
        var s = ctx.Snapshot;
        if (actionKey == "rma-load")
        {
            var n = await LoadPartsAsync(ctx);
            return ActionOutcome.Ok(n == 0 ? "Every authorized part is already listed in Receiving." : $"Added {n} part{(n == 1 ? "" : "s")} to Receiving.");
        }
        var first = s.Rows("rma_parts").FirstOrDefault();
        var (rec, err) = actionKey switch
        {
            "rma-ncr" => await QualityLinks.CreateFromAsync(ctx, "ncr", new()
            {
                ["subject"] = $"Returned product: {s.Text("subject")}", ["nc_type"] = "customer_return", ["owner"] = ctx.Me.Id, ["source_record"] = s.Record.Id,
                ["customer"] = s.Data.Main.Link("customer"), ["customer_reference"] = s.Record.Number,
                ["part_number"] = first?.Values.Text("part_number"), ["part_description"] = first?.Values.Text("part_description"),
                ["quantity_affected"] = s.Rows("rma_receipts").Sum(r => r.Values.Number("rcv_qty_received") ?? 0m),
                ["description"] = $"{s.Text("description")}\n\nInspection: {s.Text("inspection_notes")}",
            }, "ncr_record"),
            "rma-capa" => await QualityLinks.CreateFromAsync(ctx, "capa", new()
            {
                ["subject"] = s.Text("subject"), ["capa_type"] = "corrective", ["source"] = "rma", ["priority"] = "medium", ["owner"] = ctx.Me.Id,
                ["source_record"] = s.Record.Id, ["description"] = s.Text("description"),
            }, "capa_record"),
            _ => ((Record?)null, (string?)"This action isn't available."),
        };
        return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"{rec.Number} created and linked to {s.Record.Number}.", rec.Id);
    }

    public override Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var received = s.Rows("rma_receipts").GroupBy(r => Key(r.Values.Text("rcv_part_number")))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Values.Number("rcv_qty_received") ?? 0m));
        var lines = s.Rows("rma_parts").GroupBy(r => Key(r.Values.Text("part_number")))
            .Select(g => new RmaLine(g.First().Values.Text("part_number") ?? "", g.Sum(r => r.Values.Number("qty_authorized") ?? 0m), received.GetValueOrDefault(g.Key), g.Sum(r => r.Values.Number("line_total") ?? 0m)))
            .ToList();
        foreach (var (k, qty) in received.Where(kv => lines.All(l => Key(l.Part) != kv.Key)))
            lines.Add(new RmaLine(s.Rows("rma_receipts").First(r => Key(r.Values.Text("rcv_part_number")) == k).Values.Text("rcv_part_number") ?? k, 0, qty, 0));
        if (lines.Count == 0) return Task.FromResult(Enumerable.Empty<RecordPanel>());
        var model = new RmaSummaryModel(lines, s.Number("authorized_total") ?? 0m, s.Bool("receiving_override") == true, s.Rows("rma_receipts").Count > 0);
        return Task.FromResult<IEnumerable<RecordPanel>>([new RecordPanel { Key = "rma-summary", Title = "Authorized vs received", Icon = "bi-box-seam", Placement = "side", Order = 10,
            Partial = "~/Modules/Quality/Views/_RmaSummary.cshtml", Model = model }]);
    }
}

public sealed record RmaLine(string Part, decimal Authorized, decimal Received, decimal Value)
{
    public bool Over => Received > Authorized;
}
public sealed record RmaSummaryModel(List<RmaLine> Lines, decimal Total, bool Override, bool Receiving);
