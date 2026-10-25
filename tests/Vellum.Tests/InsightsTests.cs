using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Modules.Insights;
using Vellum.Modules.Insights.Charts;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>Vellum Insights: chart rules (INV-124), relative dates (INV-125), c-chart limits (RPT-301), trimming (INV-081), alert failures (RPT-606).</summary>
[Collection("vellum")]
public class InsightsTests(VellumFixture fx)
{
    private static ChartDefinition Def(Action<ChartDefinition>? change = null)
    {
        var d = new ChartDefinition { App = "capa", View = "open", Type = "column", Category = "source" };
        change?.Invoke(d);
        return d;
    }

    private static List<ChartColumn> CapaColumns(IServiceScope s)
    {
        var cfg = s.ServiceProvider.GetRequiredService<Engine.ConfigCache>().Current;
        return ChartColumns.For([cfg.Form("capa")]);
    }

    private static async Task<Record> CapaAsync(IServiceScope s, string subject, string source = "supplier") =>
        await s.CreateAsync("capa", new()
        {
            ["subject"] = subject, ["capa_type"] = "corrective", ["source"] = source, ["priority"] = "high",
            ["owner"] = s.UserId("jlee"), ["description"] = "Insights test.",
        });

    // ------------------------------------------------------------------ INV-124 / RPT-105

    [Fact]
    public void Valid_definitions_pass_the_rule_set()
    {
        using var s = fx.As("jlee");
        var cols = CapaColumns(s);
        Assert.Empty(ChartValidator.Validate(Def(), cols));
        Assert.Empty(ChartValidator.Validate(Def(d => { d.Type = "stacked_column"; d.Compare = "priority"; }), cols));
        Assert.Empty(ChartValidator.Validate(Def(d => { d.Category = "created"; d.CategoryDates = "month"; d.Type = "line"; d.Limits = "calculated"; }), cols));
        Assert.Empty(ChartValidator.Validate(Def(d => { d.Type = "gauge"; d.Category = null; d.Ranges = [new() { Max = 70, Tone = "bad" }, new() { Min = 70, Max = 90, Tone = "warn" }, new() { Min = 90, Tone = "ok" }]; }), cols));
    }

    [Theory]
    [InlineData("stacked_needs_compare", "Compare")]
    [InlineData("pareto_no_compare", "Compare")]
    [InlineData("gauge_no_compare", "Compare")]
    [InlineData("average_with_count", "Aggregate")]
    [InlineData("sort_with_compare", "Sort")]
    [InlineData("sort_on_pareto", "Sort")]
    [InlineData("date_group_on_keyword", "CategoryDates")]
    [InlineData("compare_with_limits", "Compare")]
    [InlineData("manual_lcl_above_ucl", "Lcl")]
    [InlineData("calculated_limits_need_count", "Limits")]
    [InlineData("limits_not_on_donut", "Limits")]
    [InlineData("gauge_ranges_overlap", "Ranges")]
    [InlineData("gauge_inner_blank_min", "Ranges")]
    [InlineData("gauge_min_above_max", "Ranges")]
    [InlineData("last_n_days_needs_n", "RangeDays")]
    [InlineData("custom_range_reversed", "To")]
    [InlineData("number_field_as_category", "Category")]
    [InlineData("sum_of_text_field", "ValueField")]
    public void Invalid_definitions_are_rejected_with_a_clear_message(string rule, string field)
    {
        using var s = fx.As("jlee");
        var d = rule switch
        {
            "stacked_needs_compare" => Def(d => d.Type = "stacked_bar"),
            "pareto_no_compare" => Def(d => { d.Type = "pareto"; d.Compare = "priority"; }),
            "gauge_no_compare" => Def(d => { d.Type = "gauge"; d.Compare = "priority"; }),
            "average_with_count" => Def(d => d.Aggregate = "avg"),
            "sort_with_compare" => Def(d => { d.Compare = "priority"; d.Sort = "desc"; }),
            "sort_on_pareto" => Def(d => { d.Type = "pareto"; d.Sort = "asc"; }),
            "date_group_on_keyword" => Def(d => d.CategoryDates = "month"),
            "compare_with_limits" => Def(d => { d.Compare = "priority"; d.Limits = "manual"; d.Ucl = 5; }),
            "manual_lcl_above_ucl" => Def(d => { d.Limits = "manual"; d.Lcl = 9; d.Ucl = 3; }),
            "calculated_limits_need_count" => Def(d => { d.Limits = "calculated"; d.Aggregate = "sum"; d.ValueField = "subject"; }),
            "limits_not_on_donut" => Def(d => { d.Type = "donut"; d.Limits = "manual"; d.Ucl = 3; }),
            "gauge_ranges_overlap" => Def(d => { d.Type = "gauge"; d.Ranges = [new() { Min = 0, Max = 60 }, new() { Min = 50, Max = 100 }]; }),
            "gauge_inner_blank_min" => Def(d => { d.Type = "gauge"; d.Ranges = [new() { Max = 50 }, new() { Max = 80 }, new() { Min = 80 }]; }),
            "gauge_min_above_max" => Def(d => { d.Type = "gauge"; d.Ranges = [new() { Min = 80, Max = 20 }]; }),
            "last_n_days_needs_n" => Def(d => { d.Range = "last_n_days"; d.RangeDays = 0; }),
            "custom_range_reversed" => Def(d => { d.Range = "custom"; d.From = new DateOnly(2026, 5, 1); d.To = new DateOnly(2026, 4, 1); }),
            "number_field_as_category" => new ChartDefinition { App = "actions", View = "open", Category = "days" },
            "sum_of_text_field" => Def(d => { d.Aggregate = "sum"; d.ValueField = "subject"; }),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        var cols = rule == "number_field_as_category"
            ? [new ChartColumn("days", "Days", "number")]
            : CapaColumns(s);
        var issues = ChartValidator.Validate(d, cols);
        Assert.Contains(issues, i => i.Field == field && i.Message.Length > 10);
    }

    [Fact]
    public void Donut_charts_reject_negative_values()
    {
        Assert.NotEmpty(ChartValidator.ValidateData(Def(d => d.Type = "donut"), [3, -1]));
        Assert.Empty(ChartValidator.ValidateData(Def(d => d.Type = "column"), [3, -1]));
    }

    [Fact] // INV-124: the store refuses to save an invalid chart
    public async Task Invalid_charts_are_never_saved()
    {
        using var s = fx.As("jlee");
        var store = s.ServiceProvider.GetRequiredService<ChartStore>();
        var (chart, issues) = await store.SaveAsync(null, "Bad", null, Def(d => d.Type = "stacked_column"), false, null);
        Assert.Null(chart);
        Assert.Contains(issues, i => i.Field == "Compare");
        var (ok, none) = await store.SaveAsync(null, "Good", null, Def(), false, null);
        Assert.NotNull(ok);
        Assert.Empty(none);
        Assert.StartsWith("CHT-", ok!.Number);
    }

    // ------------------------------------------------------------------ INV-125 / RPT-202

    [Theory]
    [InlineData("ytd", "2026-01-01", "2026-03-15")]
    [InlineData("qtd", "2026-01-01", "2026-03-15")]
    [InlineData("mtd", "2026-03-01", "2026-03-15")]
    [InlineData("wtd", "2026-03-09", "2026-03-15")]
    [InlineData("last_week", "2026-03-02", "2026-03-08")]
    [InlineData("last_month", "2026-02-01", "2026-02-28")]
    [InlineData("last_quarter", "2025-10-01", "2025-12-31")]
    [InlineData("last_year", "2025-01-01", "2025-12-31")]
    [InlineData("last_n_days", "2026-03-09", "2026-03-15")]
    public void Relative_ranges_follow_the_definitions(string range, string from, string to)
    {
        var today = new DateOnly(2026, 3, 15); // a Sunday
        var (f, t) = RelativeDates.Resolve(range, 7, null, null, today);
        Assert.Equal(DateOnly.Parse(from), f);
        Assert.Equal(DateOnly.Parse(to), t);
    }

    [Fact] // INV-125: relative ranges are evaluated at generation time, never at creation time
    public async Task Relative_ranges_are_evaluated_when_the_chart_is_generated()
    {
        using var s = fx.As("jlee");
        await CapaAsync(s, "Created now for INV-125");
        var engine = s.ServiceProvider.GetRequiredService<ChartEngine>();
        var def = Def(d => { d.Range = "last_n_days"; d.RangeDays = 7; d.DateField = "created"; });

        var now = await engine.EvaluateAsync(def, "t", "t", now: DateTime.UtcNow);
        var later = await engine.EvaluateAsync(def, "t", "t", now: DateTime.UtcNow.AddDays(60));
        Assert.True(now.Ok);
        Assert.True(now.Records >= 1);
        Assert.Equal(0, later.Records);
        Assert.NotEqual(now.RangeText, later.RangeText);
    }

    // ------------------------------------------------------------------ RPT-301

    [Fact]
    public void C_chart_limits_are_c_bar_plus_or_minus_three_root_c_with_lcl_floored_at_zero()
    {
        var (c1, u1, l1) = ControlLimits.CChart([4, 4, 4, 4]);
        Assert.Equal(4, c1);
        Assert.Equal(10, u1, 6);
        Assert.Equal(0, l1); // 4 − 6 < 0 → 0

        var (c2, u2, l2) = ControlLimits.CChart([10, 22, 16, 16]);
        Assert.Equal(16, c2);
        Assert.Equal(28, u2, 6);
        Assert.Equal(4, l2, 6);

        Assert.Equal((0d, 0d, 0d), ControlLimits.CChart([]));
    }

    // ------------------------------------------------------------------ INV-081

    [Fact]
    public async Task Chart_data_and_drill_through_are_security_trimmed()
    {
        Guid open, restricted;
        using (var s = fx.As("jlee"))
        {
            open = (await CapaAsync(s, "Visible supplier CAPA", "supplier")).Id;
            restricted = (await CapaAsync(s, "Restricted supplier CAPA", "supplier")).Id;
            s.Db().RecordAccess.Add(new RecordAccess { RecordId = restricted, PrincipalKind = PrincipalKind.User, PrincipalId = s.UserId("sortiz"), Level = RecordAccessLevel.Reader });
            await s.Db().SaveChangesAsync();
        }
        var def = Def(d => d.View = "open");

        using (var pshah = fx.As("pshah"))
        {
            var engine = pshah.ServiceProvider.GetRequiredService<ChartEngine>();
            var (ids, _, issues) = await engine.DrillAsync(def, "supplier", null);
            Assert.Empty(issues);
            Assert.Contains(open, ids);
            Assert.DoesNotContain(restricted, ids);
            var eval = await engine.EvaluateAsync(def, "t", "t");
            var supplier = eval.Chart!.Categories.FindIndex(c => c.Key == "supplier");
            Assert.Equal(ids.Count, (int)eval.Chart.Series[0].Values[supplier]!.Value);

            var listed = await pshah.ServiceProvider.GetRequiredService<InsightsQueries>().Apply(new RecordFilter { App = "capa", Status = "all" }).Select(r => r.Id).ToListAsync();
            Assert.DoesNotContain(restricted, listed);
        }
        using (var sortiz = fx.As("sortiz"))
        {
            var (ids, _, _) = await sortiz.ServiceProvider.GetRequiredService<ChartEngine>().DrillAsync(def, "supplier", null);
            Assert.Contains(restricted, ids);
        }
        using (var anonymous = fx.As(null))
        {
            var engine = anonymous.ServiceProvider.GetRequiredService<ChartEngine>();
            var eval = await engine.EvaluateAsync(def, "t", "t");
            Assert.Null(eval.Chart);
            Assert.Contains(eval.Issues, i => i.Field == "App");
            Assert.Empty((await engine.DrillAsync(def, "supplier", null)).Ids);
        }
    }

    // ------------------------------------------------------------------ RPT-604..606

    private static async Task<InsightsAlertTemplate> TemplateAsync(IServiceScope s, string subject)
    {
        await InsightsSchema.EnsureAsync(s.Db());
        var t = new InsightsAlertTemplate
        {
            Number = "ALT-T" + Guid.NewGuid().ToString("N")[..6], Name = "Repeat supplier CAPAs", OwnerId = s.UserId("jlee"), AppKey = "capa", ViewKey = "open",
            MatchColumnsJson = "[\"source\"]", ReportColumnsJson = "[\"priority\"]", MinMatches = 2, FrequencyDays = 1,
            RecipientsJson = new AlertRecipients { Users = [s.UserId("sortiz")] }.ToJson(), Subject = subject, Body = AlertEngine.DefaultBody,
            CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
        };
        s.Db().Set<InsightsAlertTemplate>().Add(t);
        await s.Db().SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task Alert_reports_group_matches_and_notify_recipients()
    {
        using var s = fx.As("jlee");
        await CapaAsync(s, "Supplier CAPA A", "supplier");
        await CapaAsync(s, "Supplier CAPA B", "supplier");
        var t = await TemplateAsync(s, AlertEngine.DefaultSubject);
        var report = await s.ServiceProvider.GetRequiredService<AlertEngine>().RunAsync(t.Id, manual: false, test: false);

        Assert.Equal("Notified", report.ActionStatus);
        var result = JsonSerializer.Deserialize<AlertResult>(report.ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains(result.Groups, g => g.Values.SequenceEqual(["Supplier issue"]) && g.Count >= 2);
        Assert.True(await s.Db().Notifications.AnyAsync(n => n.UserId == s.UserId("sortiz") && n.Url == $"/insights/alerts/reports/{report.Id}"));
    }

    [Fact] // RPT-606 / SAF-020: a failed action still produces the report and is raised to the owner and administrators
    public async Task Failed_alert_actions_are_raised_never_silent()
    {
        using var s = fx.As("jlee");
        await CapaAsync(s, "Supplier CAPA C", "supplier");
        await CapaAsync(s, "Supplier CAPA D", "supplier");
        var t = await TemplateAsync(s, "Broken {nonsense} subject");
        var report = await s.ServiceProvider.GetRequiredService<AlertEngine>().RunAsync(t.Id, manual: false, test: false);

        Assert.Equal("Failed", report.ActionStatus);
        Assert.Contains("{nonsense}", report.ActionDetail);
        Assert.True(await s.Db().Set<InsightsAlertReport>().AnyAsync(r => r.Id == report.Id));
        var raised = await s.Db().Notifications.Where(n => n.Kind == NotificationKind.SafetyCritical && n.Url == $"/insights/alerts/reports/{report.Id}").Select(n => n.UserId).ToListAsync();
        Assert.Contains(s.UserId("jlee"), raised);
        Assert.Contains(s.UserId("admin"), raised);
    }

    [Fact] // RPT-605: a test report runs a disabled template and sends nothing
    public async Task Test_reports_run_disabled_templates_without_sending()
    {
        using var s = fx.As("jlee");
        var t = await TemplateAsync(s, AlertEngine.DefaultSubject);
        t.Enabled = false;
        await s.Db().SaveChangesAsync();
        var before = await s.Db().Notifications.CountAsync(n => n.UserId == s.UserId("sortiz"));
        var report = await s.ServiceProvider.GetRequiredService<AlertEngine>().RunAsync(t.Id, manual: true, test: true);
        Assert.True(report.IsTest);
        Assert.Equal("Test", report.ActionStatus);
        Assert.Equal(before, await s.Db().Notifications.CountAsync(n => n.UserId == s.UserId("sortiz")));
        Assert.False(AlertEngine.IsDue(t, DateTime.UtcNow));
    }

    // ------------------------------------------------------------------ renderer

    [Fact]
    public void Charts_render_accessible_svg_with_drill_links_and_a_report_table()
    {
        var d = new ChartData
        {
            Id = "t", Title = "Open by phase", Kind = ChartKind.Column,
            Categories = [new("a", "Initiate"), new("b", "Review")],
            Series = [new ChartSeries { Key = "n", Name = "Records", Values = [3, 1], Hrefs = ["/insights/records?x=1", null] }],
        };
        var html = ChartRenderer.Figure(d, TableMode.Open).ToString()!;
        Assert.Contains("role=\"img\"", html);
        Assert.Contains("<title id=", html);
        Assert.Contains("<desc id=", html);
        Assert.Contains("href=\"/insights/records?x=1\"", html);
        Assert.Contains("<title>Initiate: 3</title>", html);
        Assert.Contains("75%", html); // share of total in the report table
        Assert.Contains("<tfoot>", html);
        Assert.Contains("No data for this period", ChartRenderer.Figure(new ChartData { Id = "e", Title = "Empty" }).ToString());
        Assert.StartsWith("Category,Records,Share %", ChartRenderer.Csv(d));
    }
}
