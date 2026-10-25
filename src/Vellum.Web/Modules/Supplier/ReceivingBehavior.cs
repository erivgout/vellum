using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;

namespace Vellum.Modules.Supplier;

/// <summary>Skip-lot inputs read from the material, the supplier and the earlier lots (INV-170).</summary>
public static class ReceivingRules
{
    public static SkipLotProfile ProfileOf(LiteRecord material) => new(
        material.Bool("skip_lot_allowed") != false,
        (int)(material.Number("skip_initial") ?? SkipLotProfile.Default.InitialPasses),
        (int)(material.Number("skip_count") ?? SkipLotProfile.Default.LotsToSkip),
        (int)(material.Number("skip_requalify") ?? SkipLotProfile.Default.RequalifyPasses));

    /// <summary>Why a supplier's lots can't be skipped, or null when it is eligible.</summary>
    public static string? IneligibleReason(LiteRecord supplier) =>
        supplier.KeywordKey("approval_status") switch
        {
            "conditional" => "The supplier is conditionally approved: every lot is inspected until it is fully approved.",
            "approved" when supplier.KeywordKey("current_grade") == "d" => "The supplier's latest scorecard grade is D (probation): every lot is inspected.",
            "approved" => null,
            _ => "The supplier isn't approved: every lot is inspected.",
        };

    public static SkipLotDecision Decide(LiteRecord material, LiteRecord supplier, IReadOnlyList<LotOutcome> history, bool coaReceived) =>
        SkipLot.Evaluate(ProfileOf(material), history, IneligibleReason(supplier), coaMissing: material.Bool("coa_required") != false && !coaReceived);

    /// <summary>
    /// Closed lots of the same supplier and material received before this one, in receipt order (received date, then
    /// record sequence). Voided lots don't count. Lots still open have no outcome yet and are left out.
    /// </summary>
    public static async Task<List<LotOutcome>> HistoryAsync(IServiceProvider sp, RecordReader reader, Guid supplierId, Guid materialId,
        DateOnly? receivedOn, Record? self)
    {
        var db = sp.GetRequiredService<VellumDbContext>();
        var cfg = sp.GetRequiredService<ConfigCache>().Current;
        if (!cfg.FormsByKey.TryGetValue("receiving", out var form)) return [];
        var fid = form.Id;
        var selfId = self?.Id;
        var ids = await db.Records.AsNoTracking()
            .Where(r => r.FormId == fid && r.PhaseType == PhaseType.Completed && r.Id != selfId &&
                        db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "supplier" && v.RefGuid == supplierId) &&
                        db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "material" && v.RefGuid == materialId))
            .Select(r => r.Id).ToListAsync();
        var lots = await reader.LoadManyAsync(ids, rows: false);
        var mine = receivedOn ?? DateOnly.MaxValue;
        var mySeq = self?.Sequence ?? long.MaxValue;
        return lots
            .Select(l => (Lot: l, On: l.Date("received_on") ?? DateOnly.FromDateTime(l.Record.CreatedAt)))
            .Where(x => x.On < mine || (x.On == mine && x.Lot.Record.Sequence < mySeq))
            .OrderBy(x => x.On).ThenBy(x => x.Lot.Record.Sequence)
            .Select(x => x.Lot.PhaseKey switch
            {
                "accepted" => LotOutcome.Passed,
                "rejected" => LotOutcome.Failed,
                "released" => LotOutcome.Skipped,
                _ => (LotOutcome?)null,
            })
            .OfType<LotOutcome>().ToList();
    }
}

/// <summary>
/// Receiving inspection: approved-supplier-list enforcement (SUP-003, INV-089), the skip-lot inspection status (INV-170),
/// the calculated accept/reject result, and closing a rejected lot only with a linked NCMR (INV-171).
/// </summary>
public sealed class ReceivingBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["receiving"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        var reader = ctx.Services.GetRequiredService<RecordReader>();
        var current = ctx.Current;
        var phaseKey = current?.PhaseKey ?? "receipt";
        var supplierId = main.Link("supplier");
        var materialId = main.Link("material");
        var supplier = supplierId is { } sid ? await reader.LoadAsync(sid) : null;
        var material = materialId is { } mid ? await reader.LoadAsync(mid, rows: false) : null;
        if (supplier != null && supplier.Form.Key != "supplier") { ctx.Error("supplier", "Choose a supplier profile."); supplier = null; }
        if (material != null && material.Form.Key != "material") { ctx.Error("material", "Choose a material profile."); material = null; }

        var changed = ctx.IsNew || current!.Data.Main.Link("supplier") != supplierId || current.Data.Main.Link("material") != materialId;
        if (changed && supplier != null && material != null)
        {
            var supplierName = supplier.Text("name") ?? supplier.Record.Title;
            var part = material.Text("part_number") ?? material.Record.Number;
            if (supplier.KeywordKey("approval_status") is not ("approved" or "conditional"))
                ctx.Error("supplier", $"{supplierName} is {supplier.Keyword("approval_status")?.Label ?? "not approved"}: receiving from it is blocked until it is approved (SUP-003).");
            if (material.Record.PhaseType != PhaseType.Completed || material.PhaseKey != "approved")
                ctx.Error("material", $"{part} isn't an approved material ({material.Phase?.Name}). Only approved materials can be received (INV-089).");
            else if (material.Bool("inactive") == true)
                ctx.Error("material", $"{part} is inactive and can't be received.");
            else if (!supplier.Rows("supplied_materials").Any(r => r.Values.Link("material") == material.Id))
                ctx.Error("material", $"{part} isn't on {supplierName}'s approved supplier list. Add it to the supplier's supplied materials first (SUP-003).");
        }

        ctx.Set("supplier_name", supplier?.Text("name"));
        ctx.Set("material_name", material == null ? null : $"{material.Text("part_number")} {material.Text("description")}".Trim());
        ctx.Set("sampling_plan_text", material?.Keyword("sampling_plan")?.Label);

        // INV-170: decided while the lot is in Receipt, then frozen with the record.
        if (phaseKey == "receipt" && supplier != null && material != null)
        {
            var history = await ReceivingRules.HistoryAsync(ctx.Services, reader, supplier.Id, material.Id, main.Date("received_on"), current?.Record);
            var decision = ReceivingRules.Decide(material, supplier, history, main.Bool("coa_received") == true);
            ctx.Set("inspection_status", decision.Skip ? "skip" : "required");
            ctx.Set("status_basis", decision.Basis);
            if (!decision.Skip && main.Ref("inspector") == null && material.UserId("inspector") is { } inspector) ctx.Set("inspector", inspector);
        }

        // Accept / reject is calculated, never typed in.
        var sample = main.Number("sample_size");
        var defects = main.Number("defects_found");
        if (sample != null && defects != null && defects > sample) ctx.Error("defects_found", "Defects found can't be more than the sample size.");
        if (main.Number("quantity") is { } qty && main.Number("qty_rejected") is { } rej && rej > qty)
            ctx.Error("qty_rejected", "The rejected quantity can't be more than the quantity received.");
        var acceptNumber = material?.Number("accept_number") ?? 0m;
        string? result = null;
        if (main.Bool("doc_nonconforming") == true && phaseKey == "inspection") result = "reject";
        else if (sample != null && defects != null) result = defects > acceptNumber || main.Bool("doc_nonconforming") == true ? "reject" : "accept";
        ctx.Set("inspection_result", result);
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        switch (ctx.To.Key)
        {
            case "inspection" when s.KeywordKey("inspection_status") != "required":
                ctx.Error("This lot qualifies for skip-lot. Release it, or void it.");
                break;
            case "released" when s.KeywordKey("inspection_status") != "skip":
                ctx.Error("This lot must be inspected; it can't be released under skip-lot (INV-170).");
                break;
            case "accepted" when s.KeywordKey("inspection_result") != "accept":
                ctx.Error("Only a lot whose inspection result is Accept can be accepted.");
                break;
            case "rejected":
                // INV-171: a failed inspection closes only with a linked NCMR (or is voided with a reason).
                if (s.KeywordKey("inspection_result") != "reject") ctx.Error("Only a lot whose inspection result is Reject can be rejected.");
                if (s.Data.Main.Link("ncr_link") is not { } ncr)
                    ctx.Error("A rejected lot can only be closed with a linked NCMR (INV-171). Create the NCMR or link an existing one. If the inspection itself was invalid, void it with a reason instead.");
                else if (!await ctx.Db.Records.AnyAsync(r => r.Id == ncr && r.PhaseType != PhaseType.Voided))
                    ctx.Error("The linked NCMR doesn't exist or has been voided. Link a valid NCMR (INV-171).");
                break;
        }
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        if (s.PhaseKey != "inspection" || s.KeywordKey("inspection_result") != "reject" || s.Links("ncr_link").Count > 0 || ctx.Me.IsSystem)
            return [];
        var acting = await ctx.Access.ActingAssignmentAsync(ctx.Record.Id);
        if (acting == null && ctx.Access.RoleFor(ctx.Record.AppId) < AppRole.Manager) return [];
        var installed = ctx.Config.FormsByKey.ContainsKey("ncr");
        return
        [
            new RecordAction
            {
                Key = "create_ncmr", Label = "Create NCMR", Icon = "bi-exclamation-diamond", Group = "Create", Variant = "danger",
                Description = "Raise a nonconforming material report for this lot and link it here (RCV-006).",
                DisabledReason = installed ? null : "The Nonconformance (NCR) application isn't installed. Link an existing NCMR in the NCMR field instead.",
                Confirm = "The NCMR is created with the lot, material, supplier and defect details from this inspection.",
            },
        ];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "create_ncmr") return ActionOutcome.Fail("This action isn't available.");
        var s = ctx.Snapshot;
        if (!ctx.Config.FormsByKey.TryGetValue("ncr", out var ncrForm)) return ActionOutcome.Fail("The Nonconformance application isn't installed.");
        if (s.PhaseKey != "inspection" || s.KeywordKey("inspection_result") != "reject") return ActionOutcome.Fail("Only a rejected lot in Inspection can raise an NCMR.");
        if (s.Links("ncr_link").Count > 0) return ActionOutcome.Fail("An NCMR is already linked.");
        if (await ctx.Access.ActingAssignmentAsync(ctx.Record.Id) == null && ctx.Access.RoleFor(ctx.Record.AppId) < AppRole.Manager)
            return ActionOutcome.Fail("Only the inspector (or an application manager) can raise the NCMR.");

        var values = NcrValues(ncrForm, s, ctx);
        string? number = null;
        var outcome = await Tx.RunAsync(ctx.Db, async () =>
        {
            var res = await ctx.Records.CreateAsync(new CreateRequest
            {
                FormKey = "ncr", Values = values, SkipAccessCheck = true,
                CreateComment = $"Raised from rejected receiving inspection {s.Record.Number} (RCV-006)",
            });
            if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            number = res.Record!.Number;
            var ncrId = res.Record.Id;
            var proposed = new RecordData();
            proposed.Main.Set("ncr_link", Val.Link(ncrId));
            var save = await ctx.Records.SaveAsync(await ctx.Records.LoadRequiredAsync(s.Record.Id), proposed, new SaveOptions { System = true });
            if (!save.Ok) return ActionOutcome.Fail(save.Errors.Select(e => e.Message).ToArray());
            await ctx.Records.LinkAsync(s.Record.Id, ncrId, LinkKind.Source, "ncr_link");
            await ctx.Db.SaveChangesAsync();
            return ActionOutcome.Ok($"NCMR {number} created and linked. You can now reject the lot.");
        }, o => o.Success);
        return outcome;
    }

    /// <summary>Pre-fill whatever the installed NCR form has room for (field keys and types are checked, nothing is assumed).</summary>
    public static Dictionary<string, object?> NcrValues(FormDefinition form, RecordSnapshot s, RecordContext ctx)
    {
        var values = new Dictionary<string, object?>();
        var cfg = ctx.Config;
        var lot = s.Text("lot_number");
        var material = s.Text("material_name");
        var supplier = s.Text("supplier_name");
        var detail = string.Join("\n", new[]
        {
            s.Text("defect_description"),
            $"Receiving inspection {s.Record.Number}: {s.Number("defects_found"):0} defect(s) in a sample of {s.Number("sample_size"):0}.",
            s.Text("inspection_notes"),
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        void Try(object? value, params string[] keys)
        {
            if (value == null) return;
            foreach (var key in keys)
            {
                if (values.ContainsKey(key)) continue;
                var f = form.Fields.FirstOrDefault(x => x.Key == key && FieldCodec.StoresValues(x.Type) && !x.ReadOnly);
                if (f == null) continue;
                object? v = f.Type switch
                {
                    FieldType.Text or FieldType.TextArea => value is string str ? (f.MaxLength is { } m && str.Length > m ? str[..m] : str) : value.ToString(),
                    FieldType.Number or FieldType.Currency => value is decimal d ? d : null,
                    FieldType.Date => value is DateOnly dt ? dt : null,
                    FieldType.User or FieldType.UserMulti => value is int u ? u : null,
                    FieldType.Link => value is Guid g && (f.LinkForms.Count == 0 || f.LinkForms.Any(lf => cfg.FormsByKey.TryGetValue(lf, out var lfForm)
                        && ctx.Db.Records.Any(r => r.Id == g && r.FormId == lfForm.Id))) ? g : null,
                    FieldType.Keyword => value is string[] options ? options.FirstOrDefault(o => cfg.Keyword(f.KeywordList ?? "", o) != null) : null,
                    _ => null,
                };
                if (v == null) continue;
                values[key] = v;
                return;
            }
        }

        Try($"Rejected lot {lot} of {material} from {supplier}", "subject", "title", "name", "summary");
        Try(detail, "description", "details", "problem_description", "nonconformance", "issue_description");
        Try(lot, "lot_number", "lot", "lot_numbers", "batch", "batch_number");
        Try(s.Number("qty_rejected") ?? s.Number("quantity"), "quantity", "qty", "quantity_affected", "qty_affected", "quantity_nonconforming");
        Try(material, "part_number", "material_name", "product", "item", "part");
        Try(supplier, "supplier_name");
        Try(s.Data.Main.Link("supplier"), "supplier");
        Try(s.Data.Main.Link("material"), "material");
        Try(s.Record.Id, "source_record", "related_record", "receiving_record", "receiving");
        Try(s.Text("po_number"), "po_number", "purchase_order");
        Try(ctx.Me.IsSystem ? null : ctx.Me.Id, "owner", "originator", "reported_by", "initiator");
        Try(BusinessCalendar.Today, "discovered_on", "detected_on", "date_detected", "occurred_on", "date");
        Try(new[] { "supplier", "material", "ncmr", "incoming", "purchased", "receiving" }, "nc_type", "ncr_type", "type", "category");
        Try(new[] { "receiving_inspection", "incoming_inspection", "receiving", "incoming", "supplier" }, "source", "nc_source", "origin", "detected_at", "detection_point");
        return values;
    }

    public override Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx) =>
        Task.FromResult<IEnumerable<RecordPanel>>(
        [
            new RecordPanel
            {
                Key = "rcv-status", Title = "Inspection status", Placement = "top", Order = 1,
                Partial = "~/Modules/Supplier/Views/_ReceivingBanner.cshtml", Model = ctx.Snapshot,
            },
        ]);
}
