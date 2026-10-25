using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Modules.Change;
using Vellum.Modules.Inspections;

namespace Vellum.Tests;

/// <summary>Inspections: INV-068 (limit order), INV-069 (evaluation reference table), INV-166..169 (records, samples, results, forced review).</summary>
public class InspectionEvaluatorTests
{
    private static readonly NumericLimits Range = new(NumericType.Range, Lsl: 3.90m, Lcl: 3.95m, Target: 4.00m, Ucl: 4.05m, Usl: 4.10m);
    private static readonly NumericLimits Target = new(NumericType.Target, Lcl: 0.47m, Target: 0.50m, Ucl: 0.53m, Tolerance: 0.05m);
    private static readonly NumericLimits Min = new(NumericType.Min, Lsl: 15m, Lcl: 16.5m);
    private static readonly NumericLimits Max = new(NumericType.Max, Ucl: 20m, Usl: 25m);

    public static TheoryData<string, decimal?, InspectionResult> Reference => new()
    {
        // RANGE 3.90 … 4.10, control 3.95 … 4.05
        { "range", 3.89m, InspectionResult.Fail }, { "range", 3.90m, InspectionResult.Warn }, { "range", 3.94m, InspectionResult.Warn },
        { "range", 3.95m, InspectionResult.Pass }, { "range", 4.00m, InspectionResult.Pass }, { "range", 4.05m, InspectionResult.Pass },
        { "range", 4.06m, InspectionResult.Warn }, { "range", 4.10m, InspectionResult.Warn }, { "range", 4.11m, InspectionResult.Fail },
        { "range", null, InspectionResult.Pending },
        // TARGET 0.50 ± 0.05, control 0.47 … 0.53
        { "target", 0.44m, InspectionResult.Fail }, { "target", 0.45m, InspectionResult.Warn }, { "target", 0.47m, InspectionResult.Pass },
        { "target", 0.50m, InspectionResult.Pass }, { "target", 0.54m, InspectionResult.Warn }, { "target", 0.55m, InspectionResult.Warn },
        { "target", 0.56m, InspectionResult.Fail },
        // MIN ≥ 15, warn below 16.5 (no upper limits apply)
        { "min", 14.9m, InspectionResult.Fail }, { "min", 15m, InspectionResult.Warn }, { "min", 16.4m, InspectionResult.Warn },
        { "min", 16.5m, InspectionResult.Pass }, { "min", 1000m, InspectionResult.Pass },
        // MAX ≤ 25, warn above 20 (no lower limits apply)
        { "max", -5m, InspectionResult.Pass }, { "max", 20m, InspectionResult.Pass }, { "max", 20.1m, InspectionResult.Warn },
        { "max", 25m, InspectionResult.Warn }, { "max", 25.01m, InspectionResult.Fail },
    };

    [Theory] // INV-069 / INS-003 reference table
    [MemberData(nameof(Reference))]
    public void Numeric_evaluation_reference_table(string type, decimal? x, InspectionResult expected)
    {
        var limits = type switch { "range" => Range, "target" => Target, "min" => Min, _ => Max };
        Assert.Equal(expected, InspectionEvaluator.Evaluate(limits, x));
    }

    [Fact] // INV-069: limits that don't apply to the type are ignored
    public void Limits_that_dont_apply_are_ignored()
    {
        var min = new NumericLimits(NumericType.Min, Lsl: 10m, Usl: 11m, Ucl: 10.5m);
        Assert.Equal(InspectionResult.Pass, InspectionEvaluator.Evaluate(min, 50m));
        var max = new NumericLimits(NumericType.Max, Lsl: 10m, Usl: 11m);
        Assert.Equal(InspectionResult.Pass, InspectionEvaluator.Evaluate(max, 1m));
    }

    [Fact] // INV-068 / INS-002
    public void Limits_must_follow_the_conventional_order()
    {
        Assert.Empty(InspectionEvaluator.ValidateLimits(Range));
        Assert.Empty(InspectionEvaluator.ValidateLimits(Target));
        Assert.Empty(InspectionEvaluator.ValidateLimits(Min));
        Assert.Empty(InspectionEvaluator.ValidateLimits(Max));
        Assert.NotEmpty(InspectionEvaluator.ValidateLimits(Range with { Lcl = 3.85m }));      // LCL < LSL
        Assert.NotEmpty(InspectionEvaluator.ValidateLimits(Range with { Target = 4.06m }));   // Target > UCL
        Assert.NotEmpty(InspectionEvaluator.ValidateLimits(Range with { Usl = null }));       // RANGE needs USL
        Assert.NotEmpty(InspectionEvaluator.ValidateLimits(Target with { Ucl = 0.56m }));     // UCL > target + tolerance
        Assert.NotEmpty(InspectionEvaluator.ValidateLimits(Target with { Tolerance = -1m }));
        Assert.NotEmpty(InspectionEvaluator.ValidateLimits(Min with { Lsl = null }));
        Assert.NotEmpty(InspectionEvaluator.ValidateLimits(Max with { Ucl = 26m }));
    }

    [Fact] // INV-167 / INS-014
    public void Sample_fails_only_on_its_failure_classes()
    {
        string[] classes = ["critical", "major"];
        Assert.Equal(InspectionResult.Fail, InspectionEvaluator.SampleResult([new("major", InspectionResult.Fail), new("minor", InspectionResult.Pass)], classes));
        Assert.Equal(InspectionResult.Pass, InspectionEvaluator.SampleResult([new("minor", InspectionResult.Fail), new("major", InspectionResult.Warn)], classes));
        Assert.Equal(InspectionResult.Pending, InspectionEvaluator.SampleResult([new("major", InspectionResult.Pass), new("minor", InspectionResult.Pending)], classes));
        Assert.Equal(InspectionResult.Fail, InspectionEvaluator.SampleResult([new("critical", InspectionResult.Fail), new("minor", InspectionResult.Pending)], classes));
    }

    [Theory] // INV-169 / INS-016
    [InlineData("never", InspectionResult.Fail, InspectionResult.Fail, false)]
    [InlineData("warnings", InspectionResult.Warn, InspectionResult.Pass, true)]
    [InlineData("failures", InspectionResult.Warn, InspectionResult.Pass, false)]
    [InlineData("failures", InspectionResult.Fail, InspectionResult.Pass, true)]
    [InlineData("sample_failures", InspectionResult.Fail, InspectionResult.Pass, false)]
    [InlineData("sample_failures", InspectionResult.Fail, InspectionResult.Fail, true)]
    [InlineData("always", InspectionResult.Pass, InspectionResult.Pass, true)]
    public void Review_required(string rule, InspectionResult criterion, InspectionResult sample, bool expected) =>
        Assert.Equal(expected, InspectionEvaluator.ReviewRequired(rule, [criterion], sample));

    [Fact] // INS-010, INV-168
    public void Sample_counts_and_record_result()
    {
        Assert.Equal(10, InspectionEvaluator.TotalSamples("quantity", 5, 500, null, 1000));
        Assert.Equal(5, InspectionEvaluator.TotalSamples("quantity", 5, 500, null, 120));
        Assert.Equal(2, InspectionEvaluator.TotalSamples("quantity", 3, 1000, null, 2)); // never more than the lot
        Assert.Equal(40, InspectionEvaluator.TotalSamples("full", null, null, null, 40));
        Assert.Equal(0, InspectionEvaluator.TotalSamples("none", 4, null, null, 40));
        Assert.Equal(4, InspectionEvaluator.TotalSamples("time", 2, null, 60, 500, new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 1, 1, 9, 30, 0)));
        Assert.Equal(InspectionResult.Pending, InspectionEvaluator.RecordResult([new("A", 2, 1)], [InspectionResult.Fail]));
        Assert.Equal(InspectionResult.Fail, InspectionEvaluator.RecordResult([new("A", 2, 2)], [InspectionResult.Pass, InspectionResult.Fail]));
        Assert.Equal(InspectionResult.Pass, InspectionEvaluator.RecordResult([new("A", 2, 2), new("B", 0, 0)], [InspectionResult.Pass, InspectionResult.Pass]));
    }
}

[Collection("vellum")]
public class InspectionTests(VellumFixture fx)
{
    private static async Task<ActionOutcome> ActAsync(IServiceScope s, Guid id, string action, Dictionary<string, string> values)
    {
        s.Db().ChangeTracker.Clear();
        var snap = await s.ServiceProvider.GetRequiredService<RecordService>().LoadRequiredAsync(id);
        var ctx = new RecordContext(snap, s.ServiceProvider);
        var behavior = s.ServiceProvider.GetRequiredService<BehaviorRegistry>().For(snap.Form).First();
        return await behavior.ExecuteAsync(ctx, action, new ActionInput { Values = values.ToDictionary(kv => kv.Key, kv => new[] { kv.Value }) });
    }

    private static async Task MeasureAsync(IServiceScope s, Guid sampleId, decimal value)
    {
        var records = s.ServiceProvider.GetRequiredService<RecordService>();
        var snap = await s.LoadAsync(sampleId);
        var proposed = new RecordData();
        foreach (var r in snap.Rows("measurements"))
        {
            var c = r.Clone();
            c.SetRow(snap.Form, snap.Config, "measured", value);
            proposed.RowsOf("measurements").Add(c);
        }
        var res = await records.SaveAsync(snap, proposed, new SaveOptions { System = true });
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    [Fact] // INV-068 enforced on save
    public async Task Criteria_profile_rejects_limits_out_of_order()
    {
        using var s = fx.As("jlee");
        var res = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = "criteria_profile",
            Values = new() { ["name"] = "Bad", ["data_type"] = "numeric", ["numeric_type"] = "range", ["lsl"] = 5m, ["usl"] = 4m },
        });
        Assert.False(res.Ok);
        var ok = await s.CreateAsync("criteria_profile", new() { ["name"] = "OD", ["data_type"] = "numeric", ["numeric_type"] = "range", ["lsl"] = 1m, ["lcl"] = 1.5m, ["ucl"] = 2.5m, ["usl"] = 3m, ["unit"] = "mm" });
        Assert.StartsWith("RANGE 1 … 3 mm", (await s.LoadAsync(ok.Id)).Text("description"));
    }

    [Fact] // INV-166, INV-167, INV-168, INV-169
    public async Task Records_samples_results_and_forced_review()
    {
        Guid planId, pointId;
        using (var s = fx.As("jlee"))
        {
            var profile = await s.CreateAsync("criteria_profile", new() { ["name"] = "Length", ["data_type"] = "numeric", ["numeric_type"] = "range", ["lsl"] = 1m, ["lcl"] = 1.5m, ["ucl"] = 2.5m, ["usl"] = 3m });
            var point = await s.CreateAsync("inspection_point", new()
            {
                ["name"] = "Length check", ["status"] = "active", ["sampling"] = "quantity", ["sample_qty"] = 1m, ["per_qty"] = 100m,
                ["failure_classes"] = new List<string> { "major" }, ["review_for"] = "failures",
            }, rows: new() { ["criteria"] = [new() { ["profile"] = profile.Id, ["class"] = "major" }] });
            pointId = point.Id;
            Assert.Equal($"{point.Number}-1", (await s.LoadAsync(pointId)).Rows("criteria")[0].Values.Text("cid")); // CID default
            planId = (await s.CreateAsync("inspection_plan", new() { ["name"] = "Test plan", ["owner"] = s.UserId("jlee") },
                rows: new() { ["points"] = [new() { ["point"] = pointId }] })).Id;

            // INV-166: no records for a plan that isn't Active.
            var early = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
            {
                FormKey = "inspection_record", Values = new() { ["plan"] = planId, ["lot_number"] = "L-1", ["lot_qty"] = 200m },
            });
            Assert.False(early.Ok);
            Assert.True((await s.RouteAsync(planId, "approve", assignees: [s.UserId("sortiz")])).Ok);
        }
        using (var q = fx.As("sortiz")) Assert.True((await q.RouteAsync(planId, "active")).Ok);

        using var i = fx.As("jlee");
        var rec = await i.CreateAsync("inspection_record", new() { ["plan"] = planId, ["lot_number"] = "L-1", ["lot_qty"] = 200m });
        var r = await i.LoadAsync(rec.Id);
        Assert.Equal(2m, r.Number("total_samples"));
        Assert.Equal("pending", r.KeywordKey("result"));

        async Task<Guid> SampleAsync(string serial, decimal value)
        {
            var o = await ActAsync(i, rec.Id, "create_sample", new() { ["point"] = pointId.ToString(), ["serial"] = serial });
            Assert.True(o.Success, string.Join("; ", o.Errors ?? []));
            await MeasureAsync(i, o.OpenRecordId!.Value, value);
            return o.OpenRecordId.Value;
        }

        var s1 = await SampleAsync("S1", 2.0m);
        Assert.Equal("pass", (await i.LoadAsync(s1)).KeywordKey("sample_result"));
        Assert.True((await i.RouteAsync(s1, "closed")).Ok);
        r = await i.LoadAsync(rec.Id);
        Assert.Equal(1m, r.Number("remaining_samples"));
        Assert.Equal("pending", r.KeywordKey("result")); // INV-168: undefined while samples remain

        // INV-167 + INV-169: a failing major criterion fails the sample, which must go through Review.
        var s2 = await SampleAsync("S2", 3.5m);
        var s2snap = await i.LoadAsync(s2);
        Assert.Equal("fail", s2snap.KeywordKey("sample_result"));
        Assert.True(s2snap.Bool("review_needed"));
        Assert.False((await i.RouteAsync(s2, "closed")).Ok);
        Assert.True((await i.RouteAsync(s2, "voided", comment: "Measured the wrong part")).Ok);

        var s3 = await SampleAsync("S3", 1.8m);
        Assert.True((await i.RouteAsync(s3, "closed")).Ok);
        r = await i.LoadAsync(rec.Id);
        Assert.Equal(0m, r.Number("remaining_samples"));
        Assert.Equal("pass", r.KeywordKey("result")); // the voided failure is excluded (INV-168)

        // A further failing sample (non-voided) makes the record fail; it reaches Close only through Review.
        var s4 = await SampleAsync("S4", 0.5m);
        Assert.False((await i.RouteAsync(s4, "closed")).Ok);
        Assert.True((await i.RouteAsync(s4, "review", assignees: [i.UserId("sortiz")])).Ok);
        Assert.Equal("fail", (await i.LoadAsync(rec.Id)).KeywordKey("result"));
        using (var q = fx.As("sortiz")) Assert.True((await q.RouteAsync(s4, "closed")).Ok);
        Assert.False((await i.RouteAsync(rec.Id, "closed")).Ok); // the direct close route only applies to a Pass
        Assert.True((await i.RouteAsync(rec.Id, "review", assignees: [i.UserId("sortiz")])).Ok);
    }
}
