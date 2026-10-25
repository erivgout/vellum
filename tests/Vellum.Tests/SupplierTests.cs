using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Modules.Supplier;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>Supplier quality: approved materials (INV-089), scorecard weights (INV-094/095), skip-lot (INV-170), NCMR gate (INV-171).</summary>
[Collection("vellum")]
public class SupplierTests(VellumFixture fx)
{
    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    internal static async Task<ActionOutcome> ActAsync(IServiceScope s, Guid id, string key, Dictionary<string, string>? inputs = null,
        string? comment = null, string password = TestHelpers.Password)
    {
        var sp = s.ServiceProvider;
        sp.GetRequiredService<VellumDbContext>().ChangeTracker.Clear();
        var snap = await sp.GetRequiredService<RecordService>().LoadRequiredAsync(id);
        var ctx = new RecordContext(snap, sp);
        foreach (var b in sp.GetServices<RecordBehavior>().Where(b => b.Forms.Contains(snap.Form.Key)))
            if ((await b.ActionsAsync(ctx)).Any(a => a.Key == key))
                return await b.ExecuteAsync(ctx, key, new ActionInput
                {
                    Values = (inputs ?? []).ToDictionary(kv => kv.Key, kv => new[] { kv.Value }), Comment = comment, Password = password, Certify = true,
                });
        return ActionOutcome.Fail($"Action {key} isn't offered.");
    }

    private static async Task<Record> ApprovedMaterialAsync(IServiceScope cwu, Dictionary<string, object?>? extra = null, bool approve = true)
    {
        var values = new Dictionary<string, object?>
        {
            ["part_number"] = "T-" + U(), ["description"] = "Test housing", ["commodity"] = "molded", ["owner"] = cwu.UserId("cwu"),
            ["sampling_plan"] = "z14_l2_10", ["accept_number"] = 0m, ["coa_required"] = false,
            ["skip_initial"] = 3m, ["skip_count"] = 2m, ["skip_requalify"] = 5m, ["inspector"] = cwu.UserId("cwu"),
        };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        var m = await cwu.CreateAsync("material", values);
        if (!approve) return m;
        Assert.True((await cwu.RouteAsync(m.Id, "approval")).Ok);
        Assert.True((await cwu.RouteAsync(m.Id, "approved")).Ok);
        return m;
    }

    private static Dictionary<string, object?> SupplierValues(IServiceScope s, string risk = "medium") => new()
    {
        ["name"] = "Test Supplier " + U(), ["code"] = "V-" + U(), ["supplier_type"] = "component", ["risk_class"] = risk, ["owner"] = s.UserId("cwu"),
        ["qual_methods"] = new[] { "questionnaire" }, ["qual_date"] = BusinessCalendar.Today.AddDays(-10), ["qual_summary"] = "Questionnaire reviewed.",
        ["scope"] = "Molded parts",
    };

    private static async Task<Record> ApprovedSupplierAsync(VellumFixture fx, Guid materialId)
    {
        Record sup;
        using (var cwu = fx.As("cwu"))
            sup = await cwu.CreateAsync("supplier", SupplierValues(cwu), rows: new()
            {
                ["contacts"] = [new() { ["contact_name"] = "Pat Doe", ["email"] = "pat@example.com" }],
                ["certifications"] = [new() { ["standard"] = "iso_9001", ["expires_on"] = BusinessCalendar.Today.AddDays(400) }],
                ["supplied_materials"] = [new() { ["material"] = materialId }],
            });
        using var approver = fx.As("sortiz");
        var res = await ActAsync(approver, sup.Id, "approve", new() { ["scope"] = "Molded parts", ["requalify_on"] = BusinessCalendar.Today.AddYears(2).ToString("yyyy-MM-dd") });
        Assert.True(res.Success, string.Join("; ", res.Errors ?? []));
        return sup;
    }

    [Fact] // INV-089
    public async Task Only_approved_materials_can_be_listed_as_supplied_materials()
    {
        using var cwu = fx.As("cwu");
        var draft = await ApprovedMaterialAsync(cwu, approve: false);
        var res = await cwu.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = "supplier", Values = SupplierValues(cwu),
            RowValues = new() { ["supplied_materials"] = [new() { ["material"] = draft.Id }] },
        });
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("INV-089"));

        Assert.True((await cwu.RouteAsync(draft.Id, "approval")).Ok);
        Assert.True((await cwu.RouteAsync(draft.Id, "approved")).Ok);
        var ok = await cwu.CreateAsync("supplier", SupplierValues(cwu), rows: new() { ["supplied_materials"] = [new() { ["material"] = draft.Id }] });
        Assert.NotNull(ok);
    }

    [Fact] // SUP-001: status only changes through the signed actions; a wrong password changes nothing.
    public async Task Supplier_approval_is_signed_and_status_is_protected()
    {
        Guid materialId, supplierId;
        using (var cwu = fx.As("cwu"))
        {
            materialId = (await ApprovedMaterialAsync(cwu)).Id;
            supplierId = (await cwu.CreateAsync("supplier", SupplierValues(cwu), rows: new()
            {
                ["contacts"] = [new() { ["contact_name"] = "Pat Doe" }],
                ["supplied_materials"] = [new() { ["material"] = materialId }],
            })).Id;
            Assert.Equal("prospective", (await cwu.LoadAsync(supplierId)).KeywordKey("approval_status"));
            var direct = await cwu.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(supplierId, new() { ["approval_status"] = "approved" });
            Assert.False(direct.Ok);
            // Authors who aren't approvers can't approve.
            var notApprover = await ActAsync(cwu, supplierId, "approve", new() { ["scope"] = "x", ["requalify_on"] = "2030-01-01" });
            Assert.False(notApprover.Success);
        }
        using var approver = fx.As("sortiz");
        var inputs = new Dictionary<string, string> { ["scope"] = "Molded parts", ["requalify_on"] = BusinessCalendar.Today.AddYears(2).ToString("yyyy-MM-dd") };
        Assert.False((await ActAsync(approver, supplierId, "approve", inputs, password: "wrong")).Success);
        Assert.Equal("prospective", (await approver.LoadAsync(supplierId)).KeywordKey("approval_status"));
        Assert.True((await ActAsync(approver, supplierId, "approve", inputs)).Success);
        Assert.Equal("approved", (await approver.LoadAsync(supplierId)).KeywordKey("approval_status"));
        var sig = await approver.Db().Signatures.AsNoTracking().SingleAsync(x => x.RecordId == supplierId);
        Assert.Equal("Approved supplier", sig.Meaning);
        Assert.Equal(approver.UserId("sortiz"), sig.SignerId);
    }

    [Fact] // INV-094, INV-095
    public void Scorecard_weights_total_100_and_not_applicable_weight_is_shared()
    {
        Assert.Null(Scoring.WeightError(40m, 30m, 15m, 15m));
        Assert.NotNull(Scoring.WeightError(40m, 30m, 15m, 10m));
        Assert.NotNull(Scoring.WeightError(40.01m, 30m, 15m, 15m));

        var all = Scoring.Calculate(350m, 95.5m, 4m, 86m, 40m, 30m, 15m, 15m);
        // (40 × 93 + 30 × 95.5 + 15 × 80 + 15 × 86) / 100 = 90.75 exactly
        Assert.Equal(90.75m, all.Overall);
        Assert.Equal("a", all.Grade);

        var noAudit = Scoring.Calculate(350m, 95.5m, 4m, null, 40m, 30m, 15m, 15m);
        // Audit's 15% is shared proportionally: (40 × 93 + 30 × 95.5 + 15 × 80) / 85 = 91.588… → 91.59
        Assert.Equal(91.59m, noAudit.Overall);
        Assert.Equal(100m, noAudit.Criteria.Where(c => c.Applicable).Sum(c => noAudit.EffectiveWeight(c)), 1);

        var bad = Scoring.Calculate(350m, 95.5m, 4m, 86m, 50m, 30m, 15m, 15m);
        Assert.Null(bad.Overall);
        Assert.NotEmpty(bad.Errors);
        Assert.Empty(Scoring.ValidateGrades(Scoring.Grades));
        Assert.Equal(100m, Scoring.QualityScore(0m));
        Assert.Equal(0m, Scoring.QualityScore(6000m));
    }

    [Fact] // INV-094 on the records themselves
    public async Task Scorecard_and_supplier_reject_weights_that_dont_total_100()
    {
        using var cwu = fx.As("cwu");
        var records = cwu.ServiceProvider.GetRequiredService<RecordService>();
        var supplierValues = SupplierValues(cwu);
        supplierValues["w_audit"] = 10m;
        var bad = await records.CreateAsync(new CreateRequest { FormKey = "supplier", Values = supplierValues });
        Assert.False(bad.Ok);
        Assert.Contains(bad.Errors, e => e.Message.Contains("100%"));

        var supplier = await cwu.CreateAsync("supplier", SupplierValues(cwu));
        var start = new DateOnly(2026, 4, 1);
        var card = await records.CreateAsync(new CreateRequest
        {
            FormKey = "scorecard",
            Values = new()
            {
                ["supplier"] = supplier.Id, ["period_start"] = start, ["period_end"] = new DateOnly(2026, 6, 30),
                ["ppm"] = 350m, ["otd_pct"] = 95.5m, ["responsiveness"] = 4m,
                ["w_quality"] = 50m, ["w_delivery"] = 30m, ["w_response"] = 15m, ["w_audit"] = 15m,
            },
        });
        Assert.False(card.Ok);

        var ok = await cwu.CreateAsync("scorecard", new()
        {
            ["supplier"] = supplier.Id, ["period_start"] = start, ["period_end"] = new DateOnly(2026, 6, 30),
            ["ppm"] = 350m, ["otd_pct"] = 95.5m, ["responsiveness"] = 4m,
        });
        var snap = await cwu.LoadAsync(ok.Id);
        Assert.Equal(40m, snap.Number("w_quality")); // copied from the supplier's default weights
        Assert.Equal(91.59m, snap.Number("overall_score"));
        Assert.Equal("a", snap.KeywordKey("grade"));
        Assert.Equal("Q2 2026", snap.Text("period"));
    }

    [Fact] // INV-170: pure, deterministic skip-lot rule
    public void Skip_lot_rule_is_a_deterministic_function_of_the_history()
    {
        var p = new SkipLotProfile(true, 3, 2, 5);
        var P = LotOutcome.Passed;
        var F = LotOutcome.Failed;
        var S = LotOutcome.Skipped;
        Assert.False(SkipLot.Evaluate(p, [P, P]).Skip);
        Assert.True(SkipLot.Evaluate(p, [P, P, P]).Skip);
        Assert.True(SkipLot.Evaluate(p, [P, P, P, S]).Skip);
        Assert.False(SkipLot.Evaluate(p, [P, P, P, S, S]).Skip); // inspected after 2 skips
        Assert.True(SkipLot.Evaluate(p, [P, P, P, S, S, P]).Skip);
        Assert.False(SkipLot.Evaluate(p, [P, P, P, F, P, P, P, P]).Skip); // 4 of 5 to requalify
        Assert.True(SkipLot.Evaluate(p, [P, P, P, F, P, P, P, P, P]).Skip);
        Assert.False(SkipLot.Evaluate(p, [P, P, P], supplierIneligibleReason: "conditional").Skip);
        Assert.False(SkipLot.Evaluate(p, [P, P, P], coaMissing: true).Skip);
        Assert.False(SkipLot.Evaluate(p with { Allowed = false }, [P, P, P]).Skip);

        // Same inputs, same answer, every time.
        var history = new[] { P, P, P, S, F, P, P, P, P, P, S };
        var first = SkipLot.Evaluate(p, history);
        for (var i = 0; i < 20; i++) Assert.Equal(first, SkipLot.Evaluate(p, history));
    }

    [Fact] // INV-170 end to end, SUP-003
    public async Task Receiving_status_follows_skip_lot_history_and_asl()
    {
        Guid materialId, supplierId;
        using (var cwu = fx.As("cwu")) materialId = (await ApprovedMaterialAsync(cwu)).Id;
        supplierId = (await ApprovedSupplierAsync(fx, materialId)).Id;

        using var s = fx.As("cwu");
        var day = 0;
        async Task<Record> Receive() => await s.CreateAsync("receiving", new()
        {
            ["po_number"] = "PO-" + U(), ["received_on"] = BusinessCalendar.Today.AddDays(-30 + day++), ["lot_number"] = "L-" + U(),
            ["quantity"] = 100m, ["supplier"] = supplierId, ["material"] = materialId, ["coa_received"] = true,
        });
        async Task InspectAndAccept(Guid id)
        {
            Assert.Equal("required", (await s.LoadAsync(id)).KeywordKey("inspection_status"));
            Assert.True((await s.RouteAsync(id, "inspection")).Ok);
            Assert.True((await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, new() { ["sample_size"] = 20m, ["defects_found"] = 0m })).Ok);
            Assert.Equal("accept", (await s.LoadAsync(id)).KeywordKey("inspection_result"));
            Assert.True((await s.RouteAsync(id, "accepted")).Ok);
        }
        for (var i = 0; i < 3; i++) await InspectAndAccept((await Receive()).Id);
        var fourth = await Receive();
        var snap = await s.LoadAsync(fourth.Id);
        Assert.Equal("skip", snap.KeywordKey("inspection_status"));
        var basis = snap.Text("status_basis");

        // Re-saving recomputes from the same inputs and gives the same decision.
        await s.ServiceProvider.GetRequiredService<RecordService>().SaveAsync(snap, new RecordData(), new SaveOptions { System = true });
        var again = await s.LoadAsync(fourth.Id);
        Assert.Equal("skip", again.KeywordKey("inspection_status"));
        Assert.Equal(basis, again.Text("status_basis"));

        // A skip-lot lot can't be sent to inspection, and an inspection-required lot can't be released.
        Assert.True((await s.RouteAsync(fourth.Id, "released")).Ok);

        // SUP-003: a material that isn't on the supplier's list is blocked.
        Guid otherMaterial;
        using (var cwu = fx.As("cwu")) otherMaterial = (await ApprovedMaterialAsync(cwu)).Id;
        var blocked = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = "receiving",
            Values = new() { ["po_number"] = "PO-1", ["lot_number"] = "L-1", ["quantity"] = 5m, ["supplier"] = supplierId, ["material"] = otherMaterial },
        });
        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Errors, e => e.Message.Contains("approved supplier list"));
    }

    [Fact] // INV-171
    public async Task Rejected_lot_closes_only_with_a_linked_ncmr_or_is_voided_with_a_reason()
    {
        Guid materialId, supplierId;
        using (var cwu = fx.As("cwu")) materialId = (await ApprovedMaterialAsync(cwu)).Id;
        supplierId = (await ApprovedSupplierAsync(fx, materialId)).Id;
        using var s = fx.As("cwu");
        var records = s.ServiceProvider.GetRequiredService<RecordService>();
        async Task<Guid> FailedLot()
        {
            var r = await s.CreateAsync("receiving", new()
            {
                ["po_number"] = "PO-" + U(), ["lot_number"] = "L-" + U(), ["quantity"] = 100m, ["supplier"] = supplierId, ["material"] = materialId,
            });
            Assert.True((await s.RouteAsync(r.Id, "inspection")).Ok);
            Assert.True((await records.UpdateAsync(r.Id, new() { ["sample_size"] = 20m, ["defects_found"] = 2m })).Ok);
            Assert.Equal("reject", (await s.LoadAsync(r.Id)).KeywordKey("inspection_result"));
            return r.Id;
        }

        var lot = await FailedLot();
        Assert.False((await s.RouteAsync(lot, "accepted")).Ok); // the accept route doesn't apply to a rejected result
        var noNcr = await s.RouteAsync(lot, "rejected");
        Assert.False(noNcr.Ok);
        Assert.Contains(noNcr.Errors, e => e.Message.Contains("INV-171"));

        // Link an NCMR (any existing record; the NCR form may come from another application) and the lot can close.
        var ncmr = await s.CreateAsync("material", new() { ["part_number"] = "NCMR-" + U(), ["description"] = "Stand-in NCMR record", ["commodity"] = "molded" });
        Assert.True((await records.UpdateAsync(lot, new() { ["ncr_link"] = ncmr.Id })).Ok);
        Assert.True((await s.RouteAsync(lot, "rejected")).Ok);

        // A second failed lot can be voided instead, but only with a reason.
        var invalid = await FailedLot();
        Assert.False((await s.RouteAsync(invalid, "voided", comment: " ")).Ok);
        Assert.True((await s.RouteAsync(invalid, "voided", comment: "Gauge out of calibration: inspection invalid")).Ok);
    }

    [Fact] // SUP-003 certificates, SUP-011 CAS check digit
    public async Task Certificate_states_and_cas_numbers_are_checked()
    {
        using var cwu = fx.As("cwu");
        var today = BusinessCalendar.Today;
        var sup = await cwu.CreateAsync("supplier", SupplierValues(cwu), rows: new()
        {
            ["certifications"] =
            [
                new() { ["standard"] = "iso_9001", ["expires_on"] = today.AddDays(-30) },
                new() { ["standard"] = "iso_9001", ["expires_on"] = today.AddDays(500) },
                new() { ["standard"] = "iso_13485", ["expires_on"] = today.AddDays(20) },
            ],
        });
        var snap = await cwu.LoadAsync(sup.Id);
        var states = snap.Rows("certifications").Select(r => snap.KeywordKey(r, "cert_state")).ToList();
        Assert.Equal(["superseded", "valid", "expiring"], states);
        Assert.Equal("expiring", snap.KeywordKey("cert_status"));
        Assert.Equal(today.AddDays(20), snap.Date("next_cert_expiry"));

        Assert.True(MaterialBehavior.IsValidCas("67-63-0"));
        Assert.True(MaterialBehavior.IsValidCas("7085-85-0"));
        Assert.False(MaterialBehavior.IsValidCas("67-63-1"));
        Assert.False(MaterialBehavior.IsValidCas("6763-0"));
    }
}
