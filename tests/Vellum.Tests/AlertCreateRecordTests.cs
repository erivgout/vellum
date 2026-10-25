using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Modules.Insights;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>
/// Alert create-record actions (RPT-603): creation through the engine as the system identity with links and audit,
/// required-field failures that create nothing (INV-127, RPT-606), the test-report dry run (RPT-605), idempotent
/// retries, and the safe token template language.
/// </summary>
[Collection("vellum")]
public class AlertCreateRecordTests(VellumFixture fx)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A unique product and reporter organization: groups are keyed on both, so other tests' complaints never match.</summary>
    private sealed record Scenario(string Product, string Org);

    private static Scenario NewScenario() => new("TP-" + Guid.NewGuid().ToString("N")[..8], "Org " + Guid.NewGuid().ToString("N")[..8]);

    private static async Task<List<Record>> ComplaintsAsync(IServiceScope s, Scenario sc, int n)
    {
        var list = new List<Record>();
        for (var i = 0; i < n; i++)
            list.Add(await s.CreateAsync("complaint", new()
            {
                ["subject"] = $"{sc.Product} complaint {i + 1}", ["category"] = "product_quality", ["site_reported"] = "boston",
                ["description"] = "Alert create-record test.", ["product"] = sc.Product, ["reporter_organization"] = sc.Org,
                ["lot_number"] = i == 0 ? "LOT-" + i : null, ["patient_involved"] = false,
            }));
        return list;
    }

    private static List<AlertFieldValue> CapaFields(params (string Field, string Value)[] extra)
    {
        var list = new List<AlertFieldValue>
        {
            new() { Field = "subject", Value = "Recurring complaints: {group.product} ({count})" },
            new() { Field = "capa_type", Value = "corrective" },
            new() { Field = "source", Value = "Customer complaint" },
            new() { Field = "priority", Value = "high" },
            new() { Field = "owner", Value = "jlee" },
            new() { Field = "product", Value = "{group.product}" },
            new() { Field = "source_record", Value = "{records}" },
            new() { Field = "description", Value = "{count} complaints about {group.product} from {group.reporter_organization}: {records}." },
        };
        foreach (var (f, v) in extra)
        {
            list.RemoveAll(x => x.Field == f);
            if (v != "") list.Add(new AlertFieldValue { Field = f, Value = v });
        }
        return list;
    }

    private static async Task<InsightsAlertTemplate> TemplateAsync(IServiceScope s, Action<AlertCreateConfig>? change = null, bool enabled = true)
    {
        await InsightsSchema.EnsureAsync(s.Db());
        var c = new AlertCreateConfig
        {
            App = "capa", Form = "capa", Phase = "capa/initiate", Per = "group", DueDays = 5,
            Users = [s.UserId("jlee")], NotifyUsers = [s.UserId("sortiz")], Fields = CapaFields(),
        };
        change?.Invoke(c);
        var t = new InsightsAlertTemplate
        {
            Number = "ALT-R" + Guid.NewGuid().ToString("N")[..6], Name = "Repeat complaints → CAPA", OwnerId = s.UserId("jlee"),
            AppKey = "complaints", ViewKey = "open_by_assignee", MatchColumnsJson = "[\"product\",\"reporter_organization\"]", ReportColumnsJson = "[\"category\"]",
            MinMatches = 3, LookbackDays = 30, FrequencyDays = 1, Enabled = enabled, Action = "create", CreateJson = c.ToJson(),
            Subject = AlertEngine.DefaultSubject, Body = AlertEngine.DefaultBody, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
        };
        s.Db().Set<InsightsAlertTemplate>().Add(t);
        await s.Db().SaveChangesAsync();
        return t;
    }

    private static Task<List<Record>> CapasForAsync(IServiceScope s, Scenario sc)
    {
        var capa = s.ServiceProvider.GetRequiredService<ConfigCache>().Current.Form("capa");
        return s.Db().Records.AsNoTracking()
            .Where(r => r.FormId == capa.Id && s.Db().RecordValues.Any(v => v.RecordId == r.Id && v.FieldKey == "product" && v.Text == sc.Product)).ToListAsync();
    }

    private static Task<List<InsightsAlertCreation>> RowsAsync(IServiceScope s, long reportId) =>
        s.Db().Set<InsightsAlertCreation>().AsNoTracking().Where(c => c.ReportId == reportId).ToListAsync();

    private static AlertEngine Engine(IServiceScope s) => s.ServiceProvider.GetRequiredService<AlertEngine>();

    // ------------------------------------------------------------------ RPT-603: creation

    [Fact]
    public async Task Creates_one_record_per_group_as_the_system_linked_to_the_matches()
    {
        using var s = fx.As("jlee");
        var sc = NewScenario();
        var complaints = await ComplaintsAsync(s, sc, 3);
        var t = await TemplateAsync(s);

        var report = await Engine(s).RunAsync(t.Id, manual: false, test: false);

        Assert.Equal("Created", report.ActionStatus);
        var row = Assert.Single(await RowsAsync(s, report.Id), r => r.ItemLabel.StartsWith(sc.Product));
        Assert.Equal("Created", row.Status);
        var capa = Assert.Single(await CapasForAsync(s, sc));
        Assert.Equal(capa.Id, row.RecordId);
        Assert.Equal(capa.Number, row.RecordNumber);
        Assert.Contains(capa.Number, report.ActionDetail);

        var snap = await s.LoadAsync(capa.Id);
        Assert.Equal("initiate", snap.PhaseKey);
        Assert.Equal($"Recurring complaints: {sc.Product} (3)", snap.Record.Title);
        Assert.Equal("high", snap.KeywordKey("priority"));
        Assert.Equal("customer_complaint", snap.KeywordKey("source"));
        Assert.Equal(s.UserId("jlee"), snap.UserId("owner"));
        Assert.Equal(complaints.Select(c => c.Id).Order(), snap.Data.Main.Links("source_record").Order());
        Assert.Contains(complaints[0].Number, snap.Text("description"));

        // Created as the system identity, with the audit naming the alert template and the report.
        var created = await s.Db().Audit.AsNoTracking().FirstAsync(a => a.RecordId == capa.Id && a.Action == "Created");
        Assert.Equal(ActorKind.System, created.ActorKind);
        Assert.Contains(t.Number, created.UserName);
        Assert.Contains(t.Number, created.Reason);
        Assert.Contains(report.Number, created.Reason);

        // Due in 5 business days, assigned to the configured assignee, the notify list told.
        var expectedDue = await s.ServiceProvider.GetRequiredService<BusinessCalendar>().AddAsync(BusinessCalendar.Today, 5, businessDays: true);
        Assert.Equal(expectedDue, snap.Record.DueDate);
        Assert.True(await s.Db().Assignments.AnyAsync(a => a.RecordId == capa.Id && a.UserId == s.UserId("jlee") && a.Status == AssignmentStatus.Active));
        Assert.True(await s.Db().Notifications.AnyAsync(n => n.RecordId == capa.Id && n.UserId == s.UserId("sortiz")));

        // Record links to every matched source record, and a system comment pointing back to the alert report.
        foreach (var c in complaints)
            Assert.True(await s.Db().Links.AnyAsync(l => l.FromId == capa.Id && l.ToId == c.Id && l.Kind == LinkKind.Related));
        Assert.True(await s.Db().Comments.AnyAsync(c => c.RecordId == capa.Id && c.IsSystem && c.Text.Contains(report.Number)));
        Assert.True(await s.Db().Audit.AnyAsync(a => a.RecordId == null && a.Action == "Alert report generated" && a.Detail!.Contains(report.Number)));
    }

    // ------------------------------------------------------------------ INV-127 / RPT-606

    [Fact]
    public async Task Missing_required_fields_create_nothing_and_fail_visibly()
    {
        using var s = fx.As("jlee");
        var sc = NewScenario();
        await ComplaintsAsync(s, sc, 3);
        // The CAPA owner is required and its "$me" default never applies to alerts; the description renders empty from a token.
        var t = await TemplateAsync(s, c => c.Fields = CapaFields(("owner", ""), ("description", "{record.serial_number}")));
        var capaCount = await s.Db().Records.CountAsync(r => r.FormId == s.ServiceProvider.GetRequiredService<ConfigCache>().Current.Form("capa").Id);

        var report = await Engine(s).RunAsync(t.Id, manual: false, test: false);

        Assert.Equal("Failed", report.ActionStatus);
        Assert.Contains("CAPA owner", report.ActionDetail);
        Assert.Contains("Problem description", report.ActionDetail);
        Assert.Contains("INV-127", report.ActionDetail);
        Assert.Empty(await CapasForAsync(s, sc));
        Assert.Equal(capaCount, await s.Db().Records.CountAsync(r => r.FormId == s.ServiceProvider.GetRequiredService<ConfigCache>().Current.Form("capa").Id));
        var row = Assert.Single(await RowsAsync(s, report.Id), r => r.ItemLabel.StartsWith(sc.Product));
        Assert.Equal("Failed", row.Status);
        Assert.Null(row.RecordId);

        // Raised to the owner and administrators, and written to the system log.
        var raised = await s.Db().Notifications.Where(n => n.Kind == NotificationKind.SafetyCritical && n.Url == $"/insights/alerts/reports/{report.Id}").Select(n => n.UserId).ToListAsync();
        Assert.Contains(s.UserId("jlee"), raised);
        Assert.Contains(s.UserId("admin"), raised);
        Assert.True(await s.Db().Audit.AnyAsync(a => a.RecordId == null && a.Action == "Alert action failed" && a.Detail!.Contains(report.Number)));
    }

    [Fact] // INV-127 at save time: the editor refuses an action that leaves a required field unset, with the field named
    public void Saving_an_action_without_every_required_field_is_refused()
    {
        using var s = fx.As("jlee");
        var store = s.ServiceProvider.GetRequiredService<AlertStore>();
        var input = new AlertInput
        {
            Name = "x", App = "complaints", View = "open_by_assignee", MatchColumns = ["product", "reporter_organization"], Action = "create",
            Create = new AlertCreateConfig { App = "capa", Form = "capa", Phase = "capa/initiate", Users = [s.UserId("jlee")], Fields = CapaFields(("owner", ""), ("subject", "{nonsense}")) },
        };
        var issues = store.Validate(input);
        Assert.Contains(issues, i => i.Field == "Create.Fields" && i.Message.Contains("CAPA owner") && i.Message.Contains("INV-127"));
        Assert.Contains(issues, i => i.Field == "Create.Fields" && i.Message.Contains("{nonsense}"));

        input.Create.Fields = CapaFields();
        Assert.Empty(store.Validate(input));

        // Only forms the owner may create records in can be targeted (kpatel only reads CAPAs).
        using var p = fx.As("kpatel");
        Assert.Contains(p.ServiceProvider.GetRequiredService<AlertStore>().Validate(input), i => i.Field == "Create.Form");
    }

    // ------------------------------------------------------------------ RPT-605: dry run

    [Fact]
    public async Task Test_reports_show_what_would_be_created_without_creating_it()
    {
        using var s = fx.As("jlee");
        var sc = NewScenario();
        await ComplaintsAsync(s, sc, 3);
        var t = await TemplateAsync(s, enabled: false);
        var links = await s.Db().Links.CountAsync();
        var notes = await s.Db().Notifications.CountAsync();
        var audit = await s.Db().Audit.CountAsync(a => a.RecordId != null);

        var report = await Engine(s).RunAsync(t.Id, manual: true, test: true);

        Assert.True(report.IsTest);
        Assert.Equal("Test", report.ActionStatus);
        Assert.Contains("would create", report.ActionDetail);
        var row = Assert.Single(await RowsAsync(s, report.Id), r => r.ItemLabel.StartsWith(sc.Product));
        Assert.Equal("Preview", row.Status);
        Assert.Null(row.RecordId);
        var detail = JsonSerializer.Deserialize<AlertCreationDetail>(row.DetailJson!, Web)!;
        Assert.Equal($"Recurring complaints: {sc.Product} (3)", detail.Title);
        Assert.Equal("Initiate", detail.Phase);
        Assert.Contains("Jordan Lee", detail.Assignees.Single());
        Assert.Contains(detail.Fields, f => f.Key == "owner" && f.Required && f.Origin == "alert");
        Assert.Contains(detail.Fields, f => f.Key == "capa_type" && f.Value == "Corrective");
        Assert.Equal(3, detail.Sources.Count);

        // Nothing stored, linked, sent or audited on any record.
        Assert.Empty(await CapasForAsync(s, sc));
        Assert.Equal(links, await s.Db().Links.CountAsync());
        Assert.Equal(notes, await s.Db().Notifications.CountAsync());
        Assert.Equal(audit, await s.Db().Audit.CountAsync(a => a.RecordId != null));
    }

    [Fact] // the dry run also reports what would fail, and a test report never raises the failure
    public async Task Test_reports_show_failures_that_a_real_run_would_hit()
    {
        using var s = fx.As("jlee");
        var sc = NewScenario();
        await ComplaintsAsync(s, sc, 3);
        var t = await TemplateAsync(s, c => c.Fields = CapaFields(("priority", "urgent-ish")));
        var report = await Engine(s).RunAsync(t.Id, manual: true, test: true);
        Assert.Equal("Failed", report.ActionStatus);
        var row = Assert.Single(await RowsAsync(s, report.Id), r => r.ItemLabel.StartsWith(sc.Product));
        Assert.Equal("PreviewFailed", row.Status);
        Assert.Contains("urgent-ish", row.Message);
        Assert.False(await s.Db().Notifications.AnyAsync(n => n.Url == $"/insights/alerts/reports/{report.Id}"));
    }

    [Fact] // the "create one record per" option
    public async Task One_record_per_report_or_per_matched_record()
    {
        using var s = fx.As("jlee");
        var a = NewScenario();
        var b = a with { Product = a.Product + "-B" };
        await ComplaintsAsync(s, a, 3);
        await ComplaintsAsync(s, b, 3);

        var perReport = await Engine(s).RunAsync((await TemplateAsync(s, c => c.Per = "report")).Id, manual: true, test: true);
        var reportRows = await RowsAsync(s, perReport.Id);
        var only = Assert.Single(reportRows);
        Assert.Equal("report", only.ItemKey);
        Assert.Contains(a.Product, only.ItemLabel);
        Assert.Contains(b.Product, only.ItemLabel);

        var perRecord = await Engine(s).RunAsync((await TemplateAsync(s, c => c.Per = "record")).Id, manual: true, test: true);
        var recordRows = await RowsAsync(s, perRecord.Id);
        Assert.True(recordRows.Count >= 6);
        Assert.All(recordRows, r => Assert.StartsWith("r:", r.ItemKey));
    }

    // ------------------------------------------------------------------ idempotency

    [Fact]
    public async Task Retried_and_resumed_runs_never_create_duplicates()
    {
        using var s = fx.As("jlee");
        var sc = NewScenario();
        await ComplaintsAsync(s, sc, 3);
        var t = await TemplateAsync(s, c => c.SkipWhileOpen = false);
        var engine = Engine(s);
        var first = await engine.RunAsync(t.Id, manual: false, test: false);
        Assert.Equal("Created", first.ActionStatus);
        Assert.Single(await CapasForAsync(s, sc));

        // The process "died" after creating the record but before finishing the report: the next run resumes that report.
        await s.Db().Set<InsightsAlertReport>().Where(r => r.Id == first.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.ActionStatus, "Pending").SetProperty(r => r.ActionDetail, (string?)null));
        s.Db().ChangeTracker.Clear();
        var resumed = await engine.RunAsync(t.Id, manual: false, test: false);
        Assert.Equal(first.Id, resumed.Id);
        Assert.Equal("Created", resumed.ActionStatus);
        Assert.Single(await CapasForAsync(s, sc));

        // Running the action for the same report again creates nothing new.
        s.Db().ChangeTracker.Clear();
        var report = await s.Db().Set<InsightsAlertReport>().FirstAsync(r => r.Id == first.Id);
        var result = JsonSerializer.Deserialize<AlertResult>(report.ResultJson, Web)!;
        var again = await s.ServiceProvider.GetRequiredService<AlertCreateAction>().ExecuteAsync(await s.Db().Set<InsightsAlertTemplate>().FirstAsync(x => x.Id == t.Id), report, result, dryRun: false);
        Assert.Equal(0, again.Created);
        Assert.True(again.Already >= 1);
        Assert.Single(await CapasForAsync(s, sc));

        // The database itself refuses a second "Created" row for the same report and item.
        var row = (await RowsAsync(s, first.Id)).First(r => r.Status == "Created");
        s.Db().Set<InsightsAlertCreation>().Add(new InsightsAlertCreation { ReportId = row.ReportId, TemplateId = row.TemplateId, ItemKey = row.ItemKey, ItemLabel = "dup", Status = "Created", At = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db().SaveChangesAsync());
        s.Db().ChangeTracker.Clear();
    }

    [Fact] // a later report doesn't open a second record while the first one for the same group is still open
    public async Task Later_reports_skip_groups_whose_record_is_still_open()
    {
        using var s = fx.As("jlee");
        var sc = NewScenario();
        await ComplaintsAsync(s, sc, 3);
        var t = await TemplateAsync(s);
        var engine = Engine(s);
        Assert.Equal("Created", (await engine.RunAsync(t.Id, manual: true, test: false)).ActionStatus);
        s.Db().ChangeTracker.Clear();
        var second = await engine.RunAsync(t.Id, manual: true, test: false);
        Assert.NotEqual("Failed", second.ActionStatus);
        var row = Assert.Single(await RowsAsync(s, second.Id), r => r.ItemLabel.StartsWith(sc.Product));
        Assert.Equal("Skipped", row.Status);
        Assert.Single(await CapasForAsync(s, sc));
    }

    [Fact] // a failed report can be retried after the template is fixed; only the missing records are created
    public async Task Failed_reports_can_be_retried_once_fixed()
    {
        using var s = fx.As("jlee");
        var sc = NewScenario();
        await ComplaintsAsync(s, sc, 3);
        var t = await TemplateAsync(s, c => c.Fields = CapaFields(("owner", "")));
        var engine = Engine(s);
        var report = await engine.RunAsync(t.Id, manual: false, test: false);
        Assert.Equal("Failed", report.ActionStatus);

        var tracked = await s.Db().Set<InsightsAlertTemplate>().FirstAsync(x => x.Id == t.Id);
        tracked.CreateJson = AlertCreateConfig.Parse(tracked.CreateJson) is var c ? Fix(c) : null;
        await s.Db().SaveChangesAsync();
        s.Db().ChangeTracker.Clear();

        var retried = await engine.RetryAsync(report.Id);
        Assert.Equal("Created", retried.ActionStatus);
        Assert.Single(await CapasForAsync(s, sc));
        await Assert.ThrowsAsync<AlertFailure>(() => engine.RetryAsync(report.Id));

        static string Fix(AlertCreateConfig c) { c.Fields = CapaFields(); return c.ToJson(); }
    }

    // ------------------------------------------------------------------ token templates

    [Fact]
    public void Token_templates_parse_a_closed_language_and_never_run_code()
    {
        Assert.Null(AlertTemplate.Parse("Recurring: {group} ({count}) {{literal}}").Error);
        Assert.NotNull(AlertTemplate.Parse("Broken {group").Error);
        Assert.NotNull(AlertTemplate.Parse("{a{b}}").Error);
        Assert.NotNull(AlertTemplate.Parse("{group|shell:rm}").Error);
        Assert.NotNull(AlertTemplate.Parse("{group|truncate:x}").Error);
        Assert.NotNull(AlertTemplate.Parse("{}").Error);

        string[] match = ["product"];
        string[] fields = ["lot_number", "owner"];
        Assert.Null(AlertTemplate.CheckToken("group.product", match, fields));
        Assert.Null(AlertTemplate.CheckToken("record.lot_number", match, fields));
        Assert.Null(AlertTemplate.CheckToken("today+30", match, fields));
        Assert.NotNull(AlertTemplate.CheckToken("group.supplier", match, fields));
        Assert.NotNull(AlertTemplate.CheckToken("record.secret", match, fields));
        Assert.NotNull(AlertTemplate.CheckToken("System.IO.File.Delete", match, fields));
        Assert.NotNull(AlertTemplate.CheckToken("today+99999", match, fields));
    }

    [Fact]
    public void Token_templates_render_typed_values_and_filters()
    {
        using var s = fx.As("jlee");
        var cfg = s.ServiceProvider.GetRequiredService<ConfigCache>().Current;
        var dir = s.ServiceProvider.GetRequiredService<Engine.Security.UserDirectory>();
        var capa = cfg.Form("capa");
        FieldDefinition F(string k) => capa.Fields.First(f => f.Key == k);
        var parser = new ValueParser(cfg, dir);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var jlee = s.UserId("jlee");
        AlertTemplate.Value Resolve(string token) => token switch
        {
            "group" => new("VX-200 pump"),
            "count" => new("3", FieldType.Number, [Val.Of(3m)]),
            "records" => new("CMP-1, CMP-2", FieldType.Link, ids.Select(Val.Link).ToList()),
            "record.owner" => new("Jordan Lee", FieldType.User, [Val.Ref(jlee)]),
            "record.mode" => new("", null),
            _ when AlertTemplate.TodayOffset(token) is { } off => new("x", FieldType.Date, [Val.Of(BusinessCalendar.Today.AddDays(off))]),
            _ => AlertTemplate.Value.Empty,
        };

        // Text with tokens, filters and escaped braces.
        var (subject, e1) = AlertTemplate.Render("{group|upper|truncate:6}: {count} {{x}}", F("subject"), Resolve, parser);
        Assert.Null(e1);
        Assert.Equal("VX-200: 3 {x}", subject.Single().Text);
        Assert.Equal("n/a", AlertTemplate.Render("{record.mode|default:n/a}", F("subject"), Resolve, parser).Values.Single().Text);

        // A single token keeps its type: a person stays a person, links stay links, dates stay dates.
        Assert.Equal(jlee, AlertTemplate.Render("{record.owner}", F("owner"), Resolve, parser).Values.Single().RefId);
        Assert.Equal(ids.Order(), AlertTemplate.Render(" {records} ", F("source_record"), Resolve, parser).Values.Select(v => v.RefGuid!.Value).Order());
        Assert.Equal(BusinessCalendar.Today.AddDays(30), DateOnly.FromDateTime(AlertTemplate.Render("{today+30}", F("discovered_on"), Resolve, parser).Values.Single().Date!.Value));

        // Literal text is parsed for the field: keywords by key or label, people by username or name; bad values are errors.
        Assert.Equal(cfg.Keyword("priority", "high")!.Id, AlertTemplate.Render("High", F("priority"), Resolve, parser).Values.Single().RefId);
        Assert.Equal(cfg.Keyword("capa_source", "customer_complaint")!.Id, AlertTemplate.Render("customer_complaint", F("source"), Resolve, parser).Values.Single().RefId);
        Assert.Equal(jlee, AlertTemplate.Render("Jordan Lee", F("owner"), Resolve, parser).Values.Single().RefId);
        Assert.NotNull(AlertTemplate.Render("Nobody Here", F("owner"), Resolve, parser).Error);
        Assert.NotNull(AlertTemplate.Render("someday", F("discovered_on"), Resolve, parser).Error);
        Assert.Empty(AlertTemplate.Render("{record.mode}", F("product"), Resolve, parser).Values);

        // Code-looking text is just text.
        Assert.Equal("@(System.IO.File.Delete(\"x\")) <script>", AlertTemplate.Render("@(System.IO.File.Delete(\"x\")) <script>", F("subject"), Resolve, parser).Values.Single().Text);
    }
}
