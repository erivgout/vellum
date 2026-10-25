using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Modules.Ehs;

namespace Vellum.Tests;

/// <summary>EHS: OSHA clocks (EHS-114, SAF-020/023/024), OSHA rates (RPT-702), JSA residual risk (INV-078), sensitive fields (SEC-520).</summary>
[Collection("vellum")]
public class EhsTests(VellumFixture fx)
{
    // ------------------------------------------------------------------ OSHA clock computation

    [Theory]
    [InlineData("fatality", 8)]
    [InlineData("inpatient", 24)]
    [InlineData("amputation", 24)]
    [InlineData("eye_loss", 24)]
    public void Osha_clock_hours_follow_29_CFR_1904_39(string severe, int hours)
    {
        var rule = OshaClock.RuleFor(severe)!;
        var occurred = new DateTime(2026, 6, 10, 13, 45, 0, DateTimeKind.Utc);
        Assert.Equal(hours, rule.Hours);
        Assert.Equal(occurred.AddHours(hours), OshaClock.Deadline(occurred, rule));
        Assert.Contains("1904.39", rule.Description); // SAF-024: the deadline shows its rule
    }

    [Fact] // SAF-024: calendar hours across a DST change (US DST ends 1 Nov 2026)
    public void Osha_clock_is_exact_across_daylight_saving_change()
    {
        var detroit = Vellum.Modules.Kit.Sites.Tz("kzo");
        var local = new DateTime(2026, 10, 31, 22, 0, 0, DateTimeKind.Unspecified); // EDT, UTC-4
        var occurredUtc = TimeZoneInfo.ConvertTimeToUtc(local, detroit);
        var deadline = OshaClock.Deadline(occurredUtc, OshaClock.RuleFor("inpatient")!);
        Assert.Equal(TimeSpan.FromHours(24), deadline - occurredUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 21, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(deadline, detroit)); // 21:00 EST, not 22:00
    }

    [Fact]
    public void Osha_clock_states()
    {
        var d = new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(OshaClockState.Running, OshaClock.State(d, null, d.AddHours(-3)));
        Assert.Equal(OshaClockState.DueSoon, OshaClock.State(d, null, d.AddMinutes(-90)));
        Assert.Equal(OshaClockState.Missed, OshaClock.State(d, null, d.AddMinutes(1)));
        Assert.Equal(OshaClockState.ReportedOnTime, OshaClock.State(d, d.AddMinutes(-1), d.AddHours(5)));
        Assert.Equal(OshaClockState.ReportedLate, OshaClock.State(d, d.AddMinutes(1), d.AddHours(5)));
        Assert.Equal(OshaClockState.None, OshaClock.State(null, null, d));
    }

    // ------------------------------------------------------------------ RPT-702 rates

    [Fact]
    public void Trir_and_dart_rates_use_exact_decimals()
    {
        Assert.Equal(600_000m / 412_500m, OshaRates.Trir(3, 412_500m));
        Assert.Equal(1.4545454545454545454545454545m, OshaRates.Trir(3, 412_500m));
        Assert.Equal("1.45", OshaRates.Format(OshaRates.Trir(3, 412_500m)));
        Assert.Equal(0.8m, OshaRates.Dart(2, 500_000m));
        Assert.Equal(2m, OshaRates.Trir(1, 100_000m));
        Assert.Equal(0m, OshaRates.Trir(0, 250_000m));
        Assert.Null(OshaRates.Trir(1, 0m)); // undefined without hours
        Assert.Equal("–", OshaRates.Format(null));
        Assert.Equal("0.13", OshaRates.Format(0.125m)); // display rounding away from zero only
    }

    [Theory]
    [InlineData(true, "days_away", 0, 0, true)]
    [InlineData(true, "restricted", 0, 0, true)]
    [InlineData(true, "other_recordable", 0, 0, false)]
    [InlineData(true, "death", 0, 0, false)]
    [InlineData(true, null, 3, 0, true)]
    [InlineData(true, null, 0, 0, false)]
    [InlineData(false, "days_away", 9, 5, false)]
    public void Dart_case_definition(bool recordable, string? cls, int away, int restricted, bool expected) =>
        Assert.Equal(expected, OshaRates.IsDart(recordable, cls, away, restricted));

    [Fact] // EHS-113 / RPT-702 end to end: hours records and recordable cases feed the rates
    public async Task Rates_are_calculated_from_hours_worked_and_recordable_cases()
    {
        using var s = fx.As("mchen");
        var year = 2019; // a year no other test uses
        await s.CreateAsync("hours_worked", new() { ["site"] = "gal", ["period"] = new DateOnly(year, 3, 14), ["hours"] = 150_000m });
        await s.CreateAsync("hours_worked", new() { ["site"] = "gal", ["period"] = new DateOnly(year, 4, 1), ["hours"] = 250_000m });
        var dup = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = "hours_worked", Values = new() { ["site"] = "gal", ["period"] = new DateOnly(year, 4, 20), ["hours"] = 1m },
        });
        Assert.False(dup.Ok); // one record per site and month
        foreach (var (cls, away) in new[] { ("days_away", 4m), ("other_recordable", 0m), ("restricted", 0m) })
            await s.CreateAsync("incident", new()
            {
                ["subject"] = "Rate test " + cls, ["incident_type"] = "injury", ["occurred_at"] = new DateTime(year, 5, 2, 10, 0, 0, DateTimeKind.Utc),
                ["site"] = "gal", ["outcome"] = "medical", ["description"] = "Test", ["recordable"] = true, ["osha_classification"] = cls,
                ["days_away"] = away, ["days_restricted"] = cls == "restricted" ? 3m : 0m,
            });

        var reports = s.ServiceProvider.GetRequiredService<EhsReports>();
        var rates = EhsReports.Rates("Galway", await reports.CasesAsync(year, "gal"), await reports.HoursAsync(year, "gal"), "gal");
        Assert.Equal(400_000m, rates.Hours);
        Assert.Equal(3, rates.Recordable);
        Assert.Equal(2, rates.DartCases);
        Assert.Equal(1.5m, rates.Trir);
        Assert.Equal(1m, rates.DartRate);
    }

    // ------------------------------------------------------------------ severe-injury clock on a record

    private static Dictionary<string, object?> Severe(DateTime occurredUtc, int supervisor) => new()
    {
        ["subject"] = "Clock test: arm caught in press", ["incident_type"] = "injury", ["occurred_at"] = occurredUtc, ["site"] = "kzo",
        ["outcome"] = "serious", ["osha_severe_event"] = "inpatient", ["description"] = "Test", ["supervisor"] = supervisor,
    };

    [Fact] // EHS-114 / SAF-020 / SAF-023
    public async Task Severe_injury_starts_a_24_hour_clock_alerts_and_escalates_until_acknowledged()
    {
        using var s = fx.As("nflores");
        var occurred = DateTime.UtcNow.AddHours(-2);
        occurred = new DateTime(occurred.Year, occurred.Month, occurred.Day, occurred.Hour, occurred.Minute, 0, DateTimeKind.Utc);
        var r = await s.CreateAsync("incident", Severe(occurred, s.UserId("akim")));
        var snap = await s.LoadAsync(r.Id);
        Assert.Equal(occurred.AddHours(24), snap.DateTime("osha_deadline"));
        Assert.Contains("1904.39(a)(2)", snap.Text("osha_rule"));

        // Safety-critical alerts: EHS, the supervisor and the reports-to chains (nflores → lgarcia → dokafor).
        var alerted = await s.Db().Notifications.AsNoTracking().Where(n => n.RecordId == r.Id && n.Kind == NotificationKind.SafetyCritical)
            .Select(n => n.UserId).ToListAsync();
        foreach (var u in new[] { "mchen", "akim", "lgarcia", "dokafor" }) Assert.Contains(s.UserId(u), alerted);

        var job = s.ServiceProvider.GetRequiredService<IEnumerable<Vellum.Infrastructure.Jobs.IScheduledJob>>().OfType<OshaClockJob>().Single();
        Assert.Equal(0, await job.EvaluateAsync(await s.LoadAsync(r.Id), DateTime.UtcNow.AddMinutes(10))); // already alerted, not yet an hour
        Assert.True(await job.EvaluateAsync(await s.LoadAsync(r.Id), DateTime.UtcNow.AddMinutes(65)) > 0);
        await s.Db().SaveChangesAsync();
        Assert.True(await s.Db().Notifications.AnyAsync(n => n.RecordId == r.Id && n.Title.StartsWith(IncidentAlerts.EscalationPrefix)));

        // Only EHS can acknowledge; acknowledging stops the hourly escalation.
        Assert.False((await RunActionAsync(s, r.Id, "osha_ack")).Success);
        using var ehs = fx.As("mchen");
        Assert.True((await RunActionAsync(ehs, r.Id, "osha_ack")).Success);
        var acked = await ehs.LoadAsync(r.Id);
        Assert.Equal(ehs.UserId("mchen"), acked.UserId("osha_ack_by"));
        Assert.Equal(0, await job.EvaluateAsync(acked, DateTime.UtcNow.AddHours(3)));

        // Missing the deadline still raises an alert, and the incident can't close without the OSHA report.
        Assert.True(await job.EvaluateAsync(acked, occurred.AddHours(24).AddMinutes(1)) > 0);
        var report = await RunActionAsync(ehs, r.Id, "osha_report", new() { ["reported_at"] = [DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")], ["reference"] = ["OSHA-1234"], ["method"] = ["phone"] }, tzUtc: true);
        Assert.True(report.Success, string.Join("; ", report.Errors ?? []));
        Assert.NotNull((await ehs.LoadAsync(r.Id)).DateTime("osha_reported_at"));
    }

    [Fact] // EHS-114: an incident with a severe outcome can't close until the OSHA report is recorded
    public async Task Severe_incident_cannot_close_without_osha_report()
    {
        using var s = fx.As("mchen");
        var r = await s.CreateAsync("incident", Severe(DateTime.UtcNow.AddHours(-1), s.UserId("akim")));
        await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(r.Id, new()
        {
            ["root_cause_category"] = "method", ["root_cause"] = "Test", ["corrective_action_required"] = false, ["reviewer"] = s.UserId("mchen"),
            ["recordable"] = false, ["review_comments"] = "Test",
        });
        Assert.True((await s.RouteAsync(r.Id, "investigation")).Ok);
        Assert.True((await s.RouteAsync(r.Id, "review")).Ok);
        var close = await s.RouteAsync(r.Id, "closed");
        Assert.False(close.Ok);
        Assert.Contains(close.Errors, e => e.Message.Contains("OSHA report"));
    }

    private static async Task<ActionOutcome> RunActionAsync(Microsoft.Extensions.DependencyInjection.IServiceScope s, Guid id, string action,
        Dictionary<string, string[]>? values = null, bool tzUtc = false)
    {
        var sp = s.ServiceProvider;
        s.Db().ChangeTracker.Clear();
        var snap = await sp.GetRequiredService<RecordService>().LoadRequiredAsync(id);
        var behavior = sp.GetServices<RecordBehavior>().OfType<IncidentBehavior>().Single();
        var ctx = new RecordContext(snap, sp);
        if (tzUtc && values != null && values.TryGetValue("reported_at", out var raw))
        {
            // The action parses times in the user's zone: convert the UTC test value into it.
            var utc = DateTime.SpecifyKind(DateTime.ParseExact(raw[0], "yyyy-MM-dd HH:mm", null), DateTimeKind.Utc);
            values["reported_at"] = [TimeZoneInfo.ConvertTimeFromUtc(utc, ctx.Records.TimeZone).ToString("yyyy-MM-dd HH:mm")];
        }
        return await behavior.ExecuteAsync(ctx, action, new ActionInput { Values = values ?? [] });
    }

    // ------------------------------------------------------------------ SEC-520 and INV-078

    [Fact] // SEC-520: people who can't see sensitive fields can't change or clear them
    public async Task Sensitive_fields_are_protected_from_non_managers()
    {
        Guid id;
        using (var ehs = fx.As("mchen"))
            id = (await ehs.CreateAsync("incident", new()
            {
                ["subject"] = "Sensitive test", ["incident_type"] = "injury", ["occurred_at"] = DateTime.UtcNow.AddHours(-1), ["site"] = "kzo",
                ["outcome"] = "first_aid", ["description"] = "Test", ["injured_name"] = "Pat Example", ["medical_treatment"] = "first_aid",
            })).Id;
        using var op = fx.As("nflores");
        Assert.False(op.ServiceProvider.GetRequiredService<Vellum.Engine.Security.AccessService>().CanSeeSensitive((await op.LoadAsync(id)).Record.AppId));
        await op.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, new() { ["injured_name"] = null, ["area"] = "Line 1" });
        var after = await op.LoadAsync(id);
        Assert.Equal("Pat Example", after.Text("injured_name"));
        Assert.Equal("Line 1", after.Text("area"));
    }

    private static Dictionary<string, object?> Step(int s1, int l1, int s2, int l2, string? justification = null) => new()
    {
        ["step"] = "Step", ["hazard"] = "Hazard", ["sev_i"] = $"sev{s1}", ["lik_i"] = $"lik{l1}", ["controls"] = "Controls",
        ["sev_r"] = $"sev{s2}", ["lik_r"] = $"lik{l2}", ["justification"] = justification,
    };

    [Fact] // INV-078 / EHS-304
    public async Task Jsa_residual_risk_must_not_exceed_initial_without_justification()
    {
        Assert.Equal(12, JsaRisk.Score("sev4", "lik3"));
        Assert.Equal("high", JsaRisk.Band(12));
        Assert.Equal("extreme", JsaRisk.Band(15));
        Assert.True(JsaRisk.ResidualAcceptable(6, 6, null));
        Assert.False(JsaRisk.ResidualAcceptable(4, 6, " "));

        using var s = fx.As("akim");
        var records = s.ServiceProvider.GetRequiredService<RecordService>();
        var info = new Dictionary<string, object?> { ["job_title"] = "JSA test", ["site"] = "kzo", ["owner"] = s.UserId("akim"), ["reviewer"] = s.UserId("mchen") };
        var bad = await records.CreateAsync(new CreateRequest { FormKey = "jsa", Values = info, RowValues = new() { ["steps"] = [Step(2, 2, 3, 2)] } });
        Assert.False(bad.Ok);
        Assert.Contains(bad.Errors, e => e.Message.Contains("INV-078"));

        var ok = await s.CreateAsync("jsa", info, rows: new() { ["steps"] = [Step(2, 2, 3, 2, "The hoist removes repeated lifts."), Step(5, 3, 5, 1)] });
        var snap = await s.LoadAsync(ok.Id);
        Assert.Equal(15m, snap.Rows("steps")[1].Values.Number("risk_i"));
        Assert.Equal(5m, snap.Rows("steps")[1].Values.Number("risk_r"));
        Assert.True((await s.RouteAsync(ok.Id, "review")).Ok);
        using var reviewer = fx.As("mchen");
        Assert.True((await reviewer.RouteAsync(ok.Id, "approved")).Ok);

        var extreme = await s.CreateAsync("jsa", info, rows: new() { ["steps"] = [Step(5, 4, 5, 3)] }); // residual 15 = Extreme
        Assert.True((await s.RouteAsync(extreme.Id, "review")).Ok);
        Assert.False((await reviewer.RouteAsync(extreme.Id, "approved")).Ok); // EHS-304: Extreme residual blocks approval
    }

    [Fact] // EHS-406: validity window and expiry warning lead time
    public void Permit_validity_status()
    {
        var from = new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(8);
        Assert.Equal(TimeSpan.FromHours(2), PermitValidity.WarningLead(from, to));
        Assert.Equal(TimeSpan.FromHours(24), PermitValidity.WarningLead(from, from.AddDays(7)));
        Assert.Equal("pending", PermitValidity.Status(from, to, from.AddMinutes(-1)));
        Assert.Equal("valid", PermitValidity.Status(from, to, from.AddHours(5)));
        Assert.Equal("expiring", PermitValidity.Status(from, to, from.AddHours(6.5)));
        Assert.Equal("expired", PermitValidity.Status(from, to, to));
        Assert.False(PermitValidity.GasOk(18.9m, 0m, 0m, 0m));
        Assert.True(PermitValidity.GasOk(20.9m, 2m, 0m, 5m));
    }
}
