using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Modules.Assets;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>Assets and calibration: INV-022, INV-064 (reference table), INV-087, INV-115, INV-116, AST-025/027.</summary>
[Collection("vellum")]
public class AssetTests(VellumFixture fx)
{
    // ------------------------------------------------------------------ INV-064: AST-022 reference table

    // Nominal 10, minus tolerance 0.5, plus tolerance 0.3 (asymmetric on purpose).
    [Theory]
    [InlineData("10", "0", true)]          // R = M
    [InlineData("9.5", "-0.5", true)]      // R = M − T₋ (boundary passes)
    [InlineData("9.7", "-0.3", true)]      // M − T₋ ≤ R < M → R − M
    [InlineData("9.4", "-0.1", false)]     // R < M − T₋ → R − (M − T₋)
    [InlineData("8.25", "-1.25", false)]
    [InlineData("10.1", "0.1", true)]      // M < R ≤ M + T₊ → R − M
    [InlineData("10.3", "0.3", true)]      // R = M + T₊ (boundary passes)
    [InlineData("10.45", "0.15", false)]   // R > M + T₊ → R − (M + T₊)
    [InlineData("10.3000001", "0.0000001", false)]
    public void Reading_deviation_and_result_follow_AST_022(string reading, string deviation, bool pass)
    {
        var r = CalibrationMath.Evaluate(decimal.Parse(reading, System.Globalization.CultureInfo.InvariantCulture), 10m, 0.5m, 0.3m);
        Assert.Equal(decimal.Parse(deviation, System.Globalization.CultureInfo.InvariantCulture), r.Deviation);
        Assert.Equal(pass, r.Pass);
    }

    [Fact]
    public void Zero_tolerance_and_overall_results()
    {
        Assert.Equal(new ReadingResult(0m, true), CalibrationMath.Evaluate(2.40m, 2.40m, 0m, 0m));
        Assert.Equal(new ReadingResult(-0.01m, false), CalibrationMath.Evaluate(2.39m, 2.40m, 0m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => CalibrationMath.Evaluate(1m, 1m, -0.1m, 0m));
        Assert.True(CalibrationMath.Overall([true, true]));
        Assert.False(CalibrationMath.Overall([true, false, null])); // AST-023: any failure fails
        Assert.Null(CalibrationMath.Overall([true, null]));
        Assert.Null(CalibrationMath.Overall([]));
    }

    [Fact] // AST-005 / AST-027 status rules
    public void Asset_status_from_schedule()
    {
        var today = new DateOnly(2026, 6, 1);
        Assert.Equal("overdue", AssetStatus.Compute("in_service", true, today.AddDays(-1), 30, today));
        Assert.Equal("due", AssetStatus.Compute("in_service", true, today.AddDays(7), 30, today)); // lead = 30 / 4 = 7 days
        Assert.Equal("in_service", AssetStatus.Compute("due", true, today.AddDays(8), 30, today));
        Assert.Equal("due", AssetStatus.Compute(null, true, null, 30, today)); // never calibrated
        Assert.Equal("quarantined", AssetStatus.Compute("quarantined", true, today.AddDays(100), 365, today)); // sticky
        Assert.Equal("in_service", AssetStatus.Compute("overdue", false, null, 0, today));
    }

    // ------------------------------------------------------------------ helpers

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static Task<Record> AssetAsync(Microsoft.Extensions.DependencyInjection.IServiceScope s, string id, bool standard = false, int interval = 365) =>
        s.CreateAsync("asset", new()
        {
            ["asset_id"] = id, ["description"] = "Test asset", ["asset_type"] = standard ? "torque_standard" : "torque_driver", ["site"] = "kzo",
            ["owner"] = s.UserId("akim"), ["technician"] = s.UserId("ebaker"), ["calibration_required"] = true, ["cal_interval_days"] = (decimal)interval,
            ["use_as_standard"] = standard,
        }, rows: new() { ["characteristics"] = [new() { ["characteristic"] = "Torque", ["unit"] = "N·m", ["nominal"] = 2.40m, ["tol_minus"] = 0.12m, ["tol_plus"] = 0.12m }] });

    private static async Task<CreateResult> CalibrationAsync(Microsoft.Extensions.DependencyInjection.IServiceScope s, Guid asset, DateOnly date, decimal found, decimal left,
        Guid? standard = null, Dictionary<string, object?>? extra = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["asset_ref"] = "Test", ["cal_date"] = date, ["technician"] = s.UserId("ebaker"),
            ["cal_type"] = standard == null ? "external" : "internal", ["certificate_no"] = standard == null ? "CERT-1" : null,
            ["standards"] = standard is { } g ? new List<Guid> { g } : null,
        };
        if (extra != null) foreach (var (k, v) in extra) values[k] = v;
        s.Db().ChangeTracker.Clear();
        return await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = "calibration", ParentId = asset, Values = values,
            RowValues = new() { ["readings"] = [new() { ["characteristic"] = "Torque", ["nominal"] = 2.40m, ["tol_minus"] = 0.12m, ["tol_plus"] = 0.12m, ["found"] = found, ["left"] = left }] },
        });
    }

    // ------------------------------------------------------------------ INV-022

    [Fact]
    public async Task Asset_ids_are_unique_in_validation_and_in_the_database()
    {
        using var s = fx.As("ebaker");
        var id = NewId("T22");
        var a = await AssetAsync(s, id);
        var records = s.ServiceProvider.GetRequiredService<RecordService>();

        var dup = await records.CreateAsync(new CreateRequest { FormKey = "asset", Values = new() { ["asset_id"] = id.ToLowerInvariant(), ["description"] = "Dup", ["asset_type"] = "caliper", ["site"] = "bos", ["owner"] = s.UserId("akim") } });
        Assert.False(dup.Ok); // normalized to upper case, then rejected as a duplicate

        // The database itself refuses a second claim on the same tag (primary key).
        Assert.True(await s.Db().Set<AssetTagClaim>().AnyAsync(c => c.Tag == id && c.RecordId == a.Id));
        s.Db().ChangeTracker.Clear();
        s.Db().Set<AssetTagClaim>().Add(new AssetTagClaim { Tag = id, RecordId = Guid.NewGuid(), ClaimedAt = DateTime.UtcNow });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => s.Db().SaveChangesAsync());
        s.Db().ChangeTracker.Clear();

        // And an asset ID never changes.
        var change = await records.UpdateAsync(a.Id, new() { ["asset_id"] = NewId("T22") });
        Assert.False(change.Ok);
    }

    // ------------------------------------------------------------------ INV-064 on a record + AST-027

    [Fact]
    public async Task Calibration_computes_results_and_updates_the_asset_on_completion()
    {
        using var s = fx.As("ebaker");
        var asset = await AssetAsync(s, NewId("T64"), interval: 90);
        var res = await CalibrationAsync(s, asset.Id, BusinessCalendar.Today.AddDays(-3), 2.30m, 2.53m);
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
        var cal = await s.LoadAsync(res.Record!.Id);
        var row = cal.Rows("readings")[0];
        Assert.Equal(-0.10m, row.Values.Number("found_dev"));
        Assert.Equal("pass", cal.KeywordKey(row, "found_result"));
        Assert.Equal(0.01m, row.Values.Number("left_dev")); // 2.53 − (2.40 + 0.12)
        Assert.Equal("fail", cal.KeywordKey(row, "left_result"));
        Assert.Equal("pass", cal.KeywordKey("as_found_result"));
        Assert.Equal("fail", cal.KeywordKey("as_left_result"));

        Assert.True((await s.RouteAsync(cal.Record.Id, "completed")).Ok);
        var after = await s.LoadAsync(asset.Id);
        Assert.Equal(BusinessCalendar.Today.AddDays(-3), after.Date("last_cal_date"));
        Assert.Equal(BusinessCalendar.Today.AddDays(87), after.Date("next_cal_date"));
        Assert.Equal("quarantined", after.KeywordKey("status")); // AST-025: as-left failure quarantines

        // A passing calibration releases the quarantine.
        var pass = await CalibrationAsync(s, asset.Id, BusinessCalendar.Today, 2.41m, 2.41m);
        Assert.True((await s.RouteAsync(pass.Record!.Id, "completed")).Ok);
        Assert.Equal("in_service", (await s.LoadAsync(asset.Id)).KeywordKey("status"));
    }

    // ------------------------------------------------------------------ INV-115

    [Fact]
    public async Task Standards_must_be_in_calibration_on_the_calibration_date()
    {
        using var s = fx.As("ebaker");
        var std = await AssetAsync(s, NewId("T115S"), standard: true, interval: 365);
        var gauge = await AssetAsync(s, NewId("T115G"));
        var today = BusinessCalendar.Today;

        var noHistory = await CalibrationAsync(s, gauge.Id, today, 2.40m, 2.40m, std.Id);
        Assert.False(noHistory.Ok);
        Assert.Contains(noHistory.Errors, e => e.Message.Contains("INV-115"));

        var stdCal = await CalibrationAsync(s, std.Id, today.AddDays(-400), 2.40m, 2.40m); // external, passed, but expired 35 days ago
        Assert.True((await s.RouteAsync(stdCal.Record!.Id, "completed")).Ok);
        Assert.False((await CalibrationAsync(s, gauge.Id, today, 2.40m, 2.40m, std.Id)).Ok);           // overdue on the date
        Assert.True((await CalibrationAsync(s, gauge.Id, today.AddDays(-100), 2.40m, 2.40m, std.Id)).Ok); // was in calibration then

        var renewed = await CalibrationAsync(s, std.Id, today.AddDays(-10), 2.40m, 2.40m);
        Assert.True((await s.RouteAsync(renewed.Record!.Id, "completed")).Ok);
        Assert.True((await CalibrationAsync(s, gauge.Id, today, 2.40m, 2.40m, std.Id)).Ok);

        // A quarantined standard can't be used (SAF-040).
        await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(std.Id, new() { ["status"] = "quarantined", ["quarantine_reason"] = "Dropped" });
        Assert.False((await CalibrationAsync(s, gauge.Id, today, 2.40m, 2.40m, std.Id)).Ok);
    }

    // ------------------------------------------------------------------ INV-116

    [Fact]
    public async Task As_found_failure_completes_only_with_a_capa_or_a_justified_bypass()
    {
        using var s = fx.As("ebaker");
        var asset = await AssetAsync(s, NewId("T116"));
        var res = await CalibrationAsync(s, asset.Id, BusinessCalendar.Today, 2.20m, 2.41m, extra: new() { ["impact_assessment"] = "Reviewed: no product impact." });
        Assert.True(res.Ok);
        var id = res.Record!.Id;
        Assert.Equal("fail", (await s.LoadAsync(id)).KeywordKey("as_found_result"));

        var blocked = await s.RouteAsync(id, "completed");
        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Errors, e => e.Message.Contains("INV-116"));

        var records = s.ServiceProvider.GetRequiredService<RecordService>();
        await records.UpdateAsync(id, new() { ["bypass_capa"] = true }); // a bypass without a reason isn't enough
        Assert.False((await s.RouteAsync(id, "completed")).Ok);
        await records.UpdateAsync(id, new() { ["bypass_reason"] = "Utility gauge; no product decisions rely on it." });
        Assert.True((await s.RouteAsync(id, "completed")).Ok);
        Assert.Equal("pass", (await s.LoadAsync(asset.Id)).KeywordKey("last_cal_result"));
    }

    // ------------------------------------------------------------------ INV-087

    [Fact]
    public async Task Only_the_schedule_assignee_or_a_manager_can_record_a_calibration()
    {
        Guid id;
        using (var s = fx.As("ebaker")) id = (await AssetAsync(s, NewId("T087"))).Id;
        async Task<RecordAction> ActionFor(string user)
        {
            using var u = fx.As(user);
            var snap = await u.LoadAsync(id);
            var behavior = u.ServiceProvider.GetServices<RecordBehavior>().OfType<AssetBehavior>().Single();
            return (await behavior.ActionsAsync(new RecordContext(snap, u.ServiceProvider))).Single(a => a.Key == "record_calibration");
        }
        Assert.Null((await ActionFor("ebaker")).DisabledReason);
        Assert.Contains("INV-087", (await ActionFor("jlee")).DisabledReason);
        Assert.Null((await ActionFor("lgarcia")).DisabledReason); // application manager
    }
}
