using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Modules.Risk;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>Risk register and FMEA: matrix bands (INV-175), acceptance gate (INV-176, SAF-045), RPN (INV-178), thresholds (INV-179).</summary>
[Collection("vellum")]
public class RiskTests(VellumFixture fx)
{
    [Fact] // INV-175
    public void Risk_bands_dont_overlap_and_cover_every_score()
    {
        Assert.Empty(RiskMatrix.Validate(RiskMatrix.Bands));
        foreach (var score in RiskMatrix.Domain) Assert.NotNull(RiskMatrix.BandFor(score));
        Assert.Equal("low", RiskMatrix.BandFor(4)!.Key);
        Assert.Equal("medium", RiskMatrix.BandFor(5)!.Key);
        Assert.Equal("high", RiskMatrix.BandFor(15)!.Key);
        Assert.Equal("critical", RiskMatrix.BandFor(16)!.Key);
        Assert.Null(RiskMatrix.BandFor(26)); // "No matching results"

        var overlapping = new[] { new RiskBand("a", "A", 1, 10, true, ""), new RiskBand("b", "B", 8, 25, false, "") };
        Assert.Contains(RiskMatrix.Validate(overlapping), i => i.Contains("overlap"));
        var gap = new[] { new RiskBand("a", "A", 1, 4, true, ""), new RiskBand("b", "B", 10, 25, false, "") };
        var issues = RiskMatrix.Validate(gap);
        Assert.Contains(issues, i => i.Contains("5, 6, 8, 9"));
    }

    private static async Task<Record> AssessedRiskAsync(IServiceScope jlee, int s, int l, List<Dictionary<string, object?>>? mitigations = null)
    {
        var r = await jlee.CreateAsync("risk_assessment", new()
        {
            ["subject"] = "Test risk " + Guid.NewGuid().ToString("N")[..6], ["risk_category"] = "product_safety", ["owner"] = jlee.UserId("jlee"),
            ["hazard"] = "Something hazardous.",
        }, rows: mitigations == null ? null : new() { ["mitigations"] = mitigations });
        Assert.True((await jlee.RouteAsync(r.Id, "assessment")).Ok);
        Assert.True((await jlee.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(r.Id, new() { ["severity"] = $"s{s}", ["likelihood"] = $"l{l}" })).Ok);
        return r;
    }

    private static async Task ReviewFieldsAsync(IServiceScope s, Guid id) =>
        Assert.True((await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, new()
        {
            ["review_notes"] = "Reviewed.", ["review_date"] = BusinessCalendar.Today.AddDays(200),
        })).Ok);

    [Fact] // INV-176, SAF-045
    public async Task Unacceptable_risk_completes_only_with_mitigation_or_signed_acceptance()
    {
        Guid id;
        using (var jlee = fx.As("jlee"))
        {
            id = (await AssessedRiskAsync(jlee, 5, 4)).Id;
            var snap = await jlee.LoadAsync(id);
            Assert.Equal(20m, snap.Number("current_score"));
            Assert.Equal("critical", snap.KeywordKey("current_level"));
            Assert.Equal("required", snap.KeywordKey("acceptance_status"));
            Assert.True((await jlee.RouteAsync(id, "review", assignees: [jlee.UserId("sortiz")])).Ok);

            // Acceptance can't be written directly.
            var direct = await jlee.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, new() { ["accepted_by"] = jlee.UserId("sortiz"), ["accepted_score"] = 20m });
            Assert.False(direct.Ok);
        }

        using var sortiz = fx.As("sortiz");
        await ReviewFieldsAsync(sortiz, id);
        var blocked = await sortiz.RouteAsync(id, "completed");
        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Errors, e => e.Message.Contains("INV-176"));

        // Someone without acceptance authority can't accept.
        using (var pshah = fx.As("pshah"))
            Assert.False((await SupplierTests.ActAsync(pshah, id, "accept_risk", new() { ["rationale"] = "No." })).Success);
        // Wrong password: nothing recorded.
        Assert.False((await SupplierTests.ActAsync(sortiz, id, "accept_risk", new() { ["rationale"] = "Benefit outweighs risk." }, password: "nope")).Success);
        Assert.Null((await sortiz.LoadAsync(id)).UserId("accepted_by"));

        var accepted = await SupplierTests.ActAsync(sortiz, id, "accept_risk", new() { ["rationale"] = "No practicable further reduction; clinical benefit outweighs the residual risk." });
        Assert.True(accepted.Success, string.Join("; ", accepted.Errors ?? []));
        var after = await sortiz.LoadAsync(id);
        Assert.Equal("accepted", after.KeywordKey("acceptance_status"));
        Assert.Equal(20m, after.Number("accepted_score"));
        Assert.True(await sortiz.Db().Signatures.AnyAsync(x => x.RecordId == id && x.Meaning == RiskRules.AcceptanceMeaning && !x.Superseded));
        Assert.True((await sortiz.RouteAsync(id, "completed")).Ok);
    }

    [Fact] // INV-176: residual still unacceptable needs acceptance; a changed score clears an acceptance
    public async Task Residual_unacceptable_risk_needs_acceptance_and_score_changes_clear_it()
    {
        using var jlee = fx.As("jlee");
        var records = jlee.ServiceProvider.GetRequiredService<RecordService>();
        var risk = await AssessedRiskAsync(jlee, 4, 4, [new() { ["measure"] = "Add an interlock", ["mit_owner"] = jlee.UserId("akim"), ["days"] = 10m }]);
        Assert.True((await jlee.RouteAsync(risk.Id, "mitigation")).Ok);
        var items = await jlee.Db().Records.AsNoTracking().Where(r => r.ParentId == risk.Id).ToListAsync();
        Assert.Single(items); // the mitigation became an action item
        Assert.Equal("mitigation_linked", (await jlee.LoadAsync(risk.Id)).KeywordKey("acceptance_status"));

        using (var akim = fx.As("akim"))
        {
            await akim.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(items[0].Id, new() { ["action_taken"] = "Done", ["completed_on"] = BusinessCalendar.Today });
            Assert.True((await akim.RouteAsync(items[0].Id, "completed")).Ok);
        }
        Assert.True((await records.UpdateAsync(risk.Id, new() { ["residual_severity"] = "s4", ["residual_likelihood"] = "l3", ["verification"] = "Tested." })).Ok);
        Assert.Equal("required", (await jlee.LoadAsync(risk.Id)).KeywordKey("acceptance_status"));
        Assert.True((await jlee.RouteAsync(risk.Id, "review", assignees: [jlee.UserId("dokafor")])).Ok);

        using var dokafor = fx.As("dokafor");
        await ReviewFieldsAsync(dokafor, risk.Id);
        Assert.False((await dokafor.RouteAsync(risk.Id, "completed")).Ok); // residual 12 (High) with mitigation: still needs acceptance
        Assert.True((await SupplierTests.ActAsync(dokafor, risk.Id, "accept_risk", new() { ["rationale"] = "Accepted pending redesign." })).Success);
        Assert.Equal("accepted", (await dokafor.LoadAsync(risk.Id)).KeywordKey("acceptance_status"));

        // Routing back supersedes the acceptance signature and clears the acceptance (SAF-045).
        Assert.True((await dokafor.RouteAsync(risk.Id, "mitigation", comment: "Re-rate after the field data review")).Ok);
        var back = await dokafor.LoadAsync(risk.Id);
        Assert.Null(back.UserId("accepted_by"));
        Assert.Equal("required", back.KeywordKey("acceptance_status"));
    }

    [Fact] // INV-176: acceptable risks complete normally; mitigated-to-acceptable completes
    public async Task Acceptable_risk_completes_without_acceptance()
    {
        Guid id;
        using (var jlee = fx.As("jlee"))
        {
            id = (await AssessedRiskAsync(jlee, 2, 3)).Id;
            Assert.Equal("not_required", (await jlee.LoadAsync(id)).KeywordKey("acceptance_status"));
            Assert.True((await jlee.RouteAsync(id, "review", assignees: [jlee.UserId("sortiz")])).Ok);
        }
        using var sortiz = fx.As("sortiz");
        await ReviewFieldsAsync(sortiz, id);
        Assert.True((await sortiz.RouteAsync(id, "completed")).Ok);
        Assert.Equal("current", (await sortiz.LoadAsync(id)).KeywordKey("review_state"));
    }

    private static Dictionary<string, object?> Row(string mode, int s, int o, int d, string? action = null, string? justification = null,
        int? rs = null, int? ro = null, int? rd = null, int? responsible = null) => new()
    {
        ["item_function"] = "Op 10", ["failure_mode"] = mode, ["effect"] = "Effect", ["cause"] = "Cause",
        ["severity"] = (decimal)s, ["occurrence"] = (decimal)o, ["detection"] = (decimal)d,
        ["recommended_action"] = action, ["justification"] = justification, ["responsible"] = responsible,
        ["rev_severity"] = (decimal?)rs, ["rev_occurrence"] = (decimal?)ro, ["rev_detection"] = (decimal?)rd,
    };

    [Fact] // INV-178
    public async Task Fmea_rpn_revised_rpn_and_reduction_are_calculated_and_not_editable()
    {
        using var jlee = fx.As("jlee");
        var f = await jlee.CreateAsync("fmea", new() { ["subject"] = "Test FMEA", ["fmea_type"] = "design", ["product"] = "Widget", ["owner"] = jlee.UserId("jlee") },
            rows: new() { ["worksheet"] = [Row("Crack", 8, 3, 5, "Add rib", rs: 8, ro: 1, rd: 3)] });
        var snap = await jlee.LoadAsync(f.Id);
        var row = snap.Rows("worksheet").Single().Values;
        Assert.Equal(120m, row.Number("rpn"));
        Assert.Equal(24m, row.Number("rev_rpn"));
        Assert.Equal(80.0m, row.Number("reduction"));
        Assert.Equal(1m, snap.Number("flagged_rows"));
        Assert.Equal(0m, snap.Number("open_flags"));

        // A forged RPN (even through a system write) is recalculated from S, O and D.
        var records = jlee.ServiceProvider.GetRequiredService<RecordService>();
        var proposed = new RecordData();
        var forged = snap.Rows("worksheet").Single().Clone();
        forged.Values.Set("rpn", Val.Of(1m));
        forged.Values.Set("occurrence", Val.Of(2m));
        proposed.RowsOf("worksheet").Add(forged);
        Assert.True((await records.SaveAsync(snap, proposed, new SaveOptions { System = true })).Ok);
        var after = (await jlee.LoadAsync(f.Id)).Rows("worksheet").Single().Values;
        Assert.Equal(80m, after.Number("rpn"));
        Assert.Equal(70.0m, after.Number("reduction"));

        // The RPN columns are read-only fields, so the UI never posts them.
        var form = snap.Form;
        Assert.All(new[] { "rpn", "rev_rpn", "reduction" }, k => Assert.True(form.Fields.Single(x => x.Key == k).ReadOnly));

        var calc = FmeaRules.Calculate(9m, 3m, 4m, 9m, 2m, 2m, 9, 100);
        Assert.Equal(108, calc.Rpn);
        Assert.Equal(36, calc.RevisedRpn);
        Assert.Equal(66.7m, calc.Reduction);
        Assert.True(calc.SeverityFlag && calc.RpnFlag);
    }

    [Fact] // INV-179, FMEA-007
    public async Task Fmea_completes_only_when_flagged_rows_have_an_action_or_justification()
    {
        using var akim = fx.As("akim");
        var records = akim.ServiceProvider.GetRequiredService<RecordService>();
        var f = await akim.CreateAsync("fmea", new() { ["subject"] = "Threshold FMEA", ["fmea_type"] = "process", ["product"] = "Widget", ["owner"] = akim.UserId("akim") },
            rows: new()
            {
                ["worksheet"] =
                [
                    Row("Severe but rare", 9, 1, 2),                       // severity ≥ 9 → flagged
                    Row("High RPN", 6, 5, 4, "Add a vision check", responsible: akim.UserId("ebaker")), // RPN 120 → flagged, has action
                    Row("Minor", 3, 2, 2),                                 // not flagged
                ],
            });
        Assert.True((await akim.RouteAsync(f.Id, "assigned")).Ok);
        Assert.True((await records.UpdateAsync(f.Id, new() { ["conclusion"] = "Done." })).Ok);
        var blocked = await akim.RouteAsync(f.Id, "completed");
        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Errors, e => e.Message.Contains("INV-179") && e.Message.Contains("Severe but rare"));
        Assert.Equal(1m, (await akim.LoadAsync(f.Id)).Number("open_flags"));

        // FMEA-007: recommended actions become action items linked to their rows.
        var made = await SupplierTests.ActAsync(akim, f.Id, "create_actions", new() { ["days"] = "20", ["verify"] = "yes" });
        Assert.True(made.Success, string.Join("; ", made.Errors ?? []));
        var snap = await akim.LoadAsync(f.Id);
        var actionRow = snap.Rows("worksheet")[1];
        Assert.NotNull(actionRow.CreatedRecordId);
        var item = await akim.LoadAsync(actionRow.CreatedRecordId!.Value);
        Assert.Equal("action_item", item.Form.Key);
        Assert.Equal(akim.UserId("ebaker"), item.UserId("assignee"));
        Assert.Equal(f.Id, item.Record.ParentId);

        // Add a justification to the severe row; now it completes.
        var proposed = new RecordData();
        foreach (var r in snap.Rows("worksheet"))
        {
            var c = r.Clone();
            if (r.Values.Text("failure_mode") == "Severe but rare") c.Values.Set("justification", Val.Of("Detected 100% by the end-of-line test (validated)."));
            proposed.RowsOf("worksheet").Add(c);
        }
        Assert.True((await records.SaveAsync(snap, proposed, new SaveOptions { System = true })).Ok);
        Assert.Equal(0m, (await akim.LoadAsync(f.Id)).Number("open_flags"));
        Assert.True((await akim.RouteAsync(f.Id, "completed")).Ok);
    }
}
