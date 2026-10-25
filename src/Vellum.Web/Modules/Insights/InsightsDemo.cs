using Vellum.Data;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Packaging;

namespace Vellum.Modules.Insights;

/// <summary>
/// Demo data for Insights alerts (RPT-603): "3 or more open complaints about the same product in 30 days → open a CAPA".
/// Seeded at the end of the Complaint Handling demo (it watches complaints and creates CAPAs). Two recent VX-200 pump
/// complaints join the one already under investigation, so the first run creates a CAPA linked to all three.
/// </summary>
public static class InsightsDemo
{
    public static async Task SeedAsync(DemoContext d)
    {
        if (!d.Config.FormsByKey.ContainsKey("capa") || !d.Config.FormsByKey.ContainsKey("complaint")) return;
        var log = d.Services.GetRequiredService<ILogger<AlertEngine>>();
        try
        {
            await d.CreateAsync("complaint", new()
            {
                ["subject"] = "VX-200 occlusion alarm sounded about 90 seconds late during a saline flush",
                ["category"] = "product_quality", ["owner"] = d.User("jlee"), ["awareness_date"] = DemoContext.Days(-12),
                ["site_reported"] = "kalamazoo", ["channel"] = "phone",
                ["description"] = "Infusion nurse clamped the line to test the alarm after a flush; the occlusion alarm took about 90 seconds. No patient was connected.",
                ["product"] = "VX-200 infusion pump", ["model"] = "VX-200-US", ["lot_number"] = "VX2-24180", ["serial_number"] = "VX2-24180-0519",
                ["failure_mode"] = "occlusion_alarm", ["reporter_type"] = "hcp", ["reporter_organization"] = "Bronson Methodist Hospital", ["patient_involved"] = false,
            }, "jlee", daysAgo: 12);
            await d.CreateAsync("complaint", new()
            {
                ["subject"] = "VX-200 did not alarm with a clamped downstream line during a biomed check",
                ["category"] = "product_quality", ["owner"] = d.User("jlee"), ["awareness_date"] = DemoContext.Days(-4),
                ["site_reported"] = "boston", ["channel"] = "field_service",
                ["description"] = "Preventive maintenance check: no occlusion alarm within 2 minutes with the downstream line clamped at 25 mL/h. Pump removed from service.",
                ["product"] = "VX-200 infusion pump", ["model"] = "VX-200-US", ["serial_number"] = "VX2-24311-0042", ["failure_mode"] = "occlusion_alarm",
                ["device_returned"] = true, ["reporter_type"] = "biomed", ["reporter_organization"] = "Boston General Hospital", ["patient_involved"] = false,
            }, "jlee", daysAgo: 4);

            var db = d.Db;
            db.ChangeTracker.Clear();
            await InsightsSchema.EnsureAsync(db);
            var numbers = d.Services.GetRequiredService<NumberingService>();
            var create = new AlertCreateConfig
            {
                App = "capa", Form = "capa", Phase = "capa/initiate", Per = "group", DueDays = 5, SkipWhileOpen = true,
                Users = [d.User("jlee")], NotifyUsers = [d.User("sortiz")],
                Fields =
                [
                    new() { Field = "subject", Value = "Recurring complaints: {group} ({count} in 30 days)" },
                    new() { Field = "capa_type", Value = "corrective" },
                    new() { Field = "source", Value = "customer_complaint" },
                    new() { Field = "priority", Value = "high" },
                    new() { Field = "owner", Value = "jlee" },
                    new() { Field = "discovered_on", Value = "{today}" },
                    new() { Field = "product", Value = "{group.product}" },
                    new() { Field = "source_record", Value = "{records}" },
                    new()
                    {
                        Field = "description",
                        Value = "{count} open complaints about the {group.product} were received in the last 30 days ({records}). The most recent is {record.number}: {record.title}. " +
                                "Investigate whether they share a root cause. Raised automatically by alert {alert.number} (report {report.number}).",
                    },
                ],
            };
            var now = DateTime.UtcNow;
            var t = new InsightsAlertTemplate
            {
                Number = $"ALT-{await numbers.NextAsync("insights-alert"):D5}",
                Name = "Repeat complaints about one product → CAPA",
                Description = "3 or more open complaints about the same product in 30 days open a CAPA for that product, linked to the complaints.",
                OwnerId = d.User("sortiz"), AppKey = "complaints", ViewKey = "open_by_assignee",
                MatchColumnsJson = "[\"product\"]", ReportColumnsJson = "[\"phase\",\"failure_mode\",\"created\"]",
                MinMatches = 3, LookbackDays = 30, FrequencyDays = 1, Enabled = true, OnlyWhenChanged = false,
                Action = "create", CreateJson = create.ToJson(), Subject = AlertEngine.DefaultSubject, Body = AlertEngine.DefaultBody,
                RecipientsJson = "{}", CcJson = "{}", CreatedAt = now, ModifiedAt = now,
            };
            db.Set<InsightsAlertTemplate>().Add(t);
            await db.SaveChangesAsync();

            // The first scheduled run, so the demo's report shows the CAPA it created.
            d.Services.GetRequiredService<CurrentUser>().UseSystem("Insights threshold alerts");
            var report = await d.Services.GetRequiredService<AlertEngine>().RunAsync(t.Id, manual: false, test: false);
            log.LogInformation("Demo alert {Template}: report {Report} {Status}: {Detail}", t.Number, report.Number, report.ActionStatus, report.ActionDetail);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Insights demo alert failed");
        }
    }
}
