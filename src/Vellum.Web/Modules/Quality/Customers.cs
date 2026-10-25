using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// Customer profiles and feedback §12.7 (CFB-001, CFB-002). Profiles are non-workflow records with contacts and an automatic
/// feedback history; feedback is created from a profile and runs Investigation → Approval → Completed.
/// </summary>
public sealed class CustomersApp : IPackagedApp
{
    public string Key => "customers";
    public int Order => 38;

    public void Define(AppBuilder app)
    {
        app.Name("Customers & Feedback")
            .Description("Customer profiles, contacts and feedback history: the lookup source for complaints, NCRs and returns.")
            .Icon("bi-person-badge").Color("#0f766e").Suite("Quality").Order(40).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@regulatory", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        app.Keywords("customer_type", "Customer type", k => k
            .Add("hospital", "Hospital / IDN").Add("clinic", "Clinic / ambulatory").Add("distributor", "Distributor").Add("oem", "OEM partner").Add("gpo", "Group purchasing org"));
        app.Keywords("customer_status", "Customer status", k => k
            .Add("active", "Active", "success").Add("on_hold", "On hold", "warning").Add("inactive", "Inactive", "secondary"));
        app.Keywords("feedback_type", "Feedback type", k => k
            .Add("compliment", "Compliment", "success").Add("suggestion", "Suggestion", "info").Add("concern", "Concern", "warning").Add("inquiry", "Inquiry", "secondary"));
        app.Keywords("feedback_channel", "Feedback channel", k => k
            .Add("email", "Email").Add("phone", "Phone").Add("visit", "Site visit").Add("survey", "Survey").Add("web", "Web form"));

        app.Form("customer", "Customer Profile", f => f
            .Icon("bi-person-badge")
            .Description("Account information, addresses and contacts. Lists every feedback record and complaint for the customer.")
            .Numbering("CUS-", 4)
            .Title("{customer_name}")
            .NonWorkflow()
            .Section("account", "Account", s =>
            {
                s.Text("customer_name", "Customer name", 8).Required().Unique().MaxLength(200);
                s.Text("account_number", "Account number", 4).Unique();
                s.Keyword("customer_type", "Type", "customer_type").Required().Header();
                s.Keyword("customer_status", "Status", "customer_status").Default("active").Header();
                s.User("account_manager", "Account manager").Header();
                s.Text("region", "Region / territory", 4);
                s.Url("website", "Website", 8);
            }, icon: "bi-building")
            .Section("address", "Address", s =>
            {
                s.Text("street", "Street", 12);
                s.Text("city", "City", 4);
                s.Text("state", "State / region", 3);
                s.Text("postal_code", "Postal code", 2);
                s.Text("country", "Country", 3);
            }, icon: "bi-geo-alt")
            .Subform("contacts", "Contacts", s =>
            {
                s.Text("contact_name", "Name", 3).Required();
                s.Text("contact_title", "Title", 3);
                s.Text("contact_email", "Email", 3);
                s.Text("contact_phone", "Phone", 2);
                s.YesNo("contact_primary", "Primary?", 1);
            }, rowNoun: "contact", icon: "bi-person-lines-fill", description: "Feedback contacts are limited to these people (CFB-002)."));

        app.Form("feedback", "Customer Feedback", f => f
            .Icon("bi-chat-square-text")
            .Description("Compliments, suggestions, concerns and inquiries from a customer. Created from the customer profile.")
            .Numbering("FB-")
            .Title("{subject}")
            .HiddenFromNew()
            .Section("fb_info", "Feedback", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200);
                s.Link("customer", "Customer", "customer").Required().Header();
                s.Text("contact", "Customer contact", 4).Help("Must be one of the contacts on the customer profile (CFB-002).");
                s.Keyword("feedback_type", "Type", "feedback_type").Required().Header();
                s.Keyword("channel", "Received by", "feedback_channel");
                s.Date("received_on", "Received on").Default("$today");
                s.User("owner", "Owner").Required().Header().Default("$me");
                s.Text("product", "Product", 4);
                s.Text("lot_number", "Lot / serial", 4);
                s.TextArea("description", "Feedback").Required();
            }, icon: "bi-chat-square-text")
            .Section("fb_investigation", "Investigation and action", s =>
            {
                s.TextArea("investigation", "Investigation").RequiredIn("investigation");
                s.TextArea("action_taken", "Action taken").RequiredIn("investigation");
                s.TextArea("response", "Response to the customer");
            }, icon: "bi-search")
            .Section("fb_links", "Follow-up", s =>
            {
                s.Link("capa_record", "CAPA", "capa");
                s.Link("rma_record", "Customer return (RMA)", "rma");
            }, icon: "bi-arrow-repeat")
            .Workflow("feedback", "Feedback", w =>
            {
                w.Start("investigation", "Investigation").Purpose("Look into the feedback, decide what to do and draft the response.")
                    .AssignField("owner").DueDays(10).Edit("fb_info", "fb_investigation", "fb_links");
                w.Phase("approval", "Approval").Purpose("Quality approves the investigation and the response.")
                    .AssignGroup("qa-approvers").DueDays(3).Edit("fb_links").ReadOnlyRest();
                w.Completed("completed", "Completed");
                w.Voided();
                w.Forward("investigation", "approval", label: "Send for approval");
                w.Forward("approval", "completed", label: "Approve", sign: "Approved");
                w.Back("approval", "investigation");
                w.Void("approval");
            }));

        app.View("customers", "Customer profiles", v => v.Category("1. Customers").Forms("customer").All()
            .Columns("number", "customer_name", "customer_type", "customer_status", "region", "account_manager").Sort("customer_name", false).Default());
        app.View("customers_hold", "Customers on hold", v => v.Category("1. Customers").Forms("customer").All().Filter("customer_status", "eq", "on_hold")
            .Columns("number", "customer_name", "customer_type", "account_manager"));
        app.View("feedback_mine", "My feedback", v => v.Category("2. Feedback").Forms("feedback").Mine()
            .Columns("number", "title", "customer", "feedback_type", "phase", "due").Sort("due", false));
        app.View("feedback_open", "Open feedback", v => v.Category("2. Feedback").Forms("feedback").Open()
            .Columns("number", "title", "customer", "feedback_type", "phase", "due", "owner"));
        app.View("feedback_closed", "Closed feedback", v => v.Category("2. Feedback").Forms("feedback").Closed()
            .Columns("number", "title", "customer", "feedback_type", "phase", "modified").Sort("modified"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        var stMary = await d.CreateAsync("customer", new()
        {
            ["customer_name"] = "St. Mary's Regional Medical Center", ["account_number"] = "C-10422", ["customer_type"] = "hospital", ["customer_status"] = "active",
            ["account_manager"] = d.User("rnovak"), ["region"] = "Midwest", ["street"] = "1200 Oakland Drive", ["city"] = "Kalamazoo", ["state"] = "MI", ["postal_code"] = "49008", ["country"] = "USA",
        }, "sortiz", daysAgo: 300, rows: new()
        {
            ["contacts"] =
            [
                new() { ["contact_name"] = "Dr. Angela Ruiz", ["contact_title"] = "Director of Biomedical Engineering", ["contact_email"] = "a.ruiz@stmarys.example", ["contact_phone"] = "+1 269 555 0142", ["contact_primary"] = true },
                new() { ["contact_name"] = "Tom Becker", ["contact_title"] = "Nurse Manager, ICU", ["contact_email"] = "t.becker@stmarys.example", ["contact_phone"] = "+1 269 555 0177", ["contact_primary"] = false },
            ],
        });
        var bostonGen = await d.CreateAsync("customer", new()
        {
            ["customer_name"] = "Boston General Hospital", ["account_number"] = "C-10087", ["customer_type"] = "hospital", ["customer_status"] = "active",
            ["account_manager"] = d.User("rnovak"), ["region"] = "Northeast", ["street"] = "55 Fruit Street", ["city"] = "Boston", ["state"] = "MA", ["postal_code"] = "02114", ["country"] = "USA",
        }, "sortiz", daysAgo: 280, rows: new()
        {
            ["contacts"] = [new() { ["contact_name"] = "Priya Raman", ["contact_title"] = "Clinical Engineering Manager", ["contact_email"] = "praman@bostongeneral.example", ["contact_primary"] = true }],
        });
        await d.CreateAsync("customer", new()
        {
            ["customer_name"] = "MedSupply Europe B.V.", ["account_number"] = "D-20031", ["customer_type"] = "distributor", ["customer_status"] = "active",
            ["account_manager"] = d.User("sortiz"), ["region"] = "EMEA", ["street"] = "Keizersgracht 210", ["city"] = "Amsterdam", ["postal_code"] = "1016 DX", ["country"] = "Netherlands",
        }, "sortiz", daysAgo: 250, rows: new()
        {
            ["contacts"] = [new() { ["contact_name"] = "Joost de Vries", ["contact_title"] = "Quality & Regulatory Lead", ["contact_email"] = "j.devries@medsupply-eu.example", ["contact_primary"] = true }],
        });
        await d.CreateAsync("customer", new()
        {
            ["customer_name"] = "Lakeshore Surgery Center", ["account_number"] = "C-10533", ["customer_type"] = "clinic", ["customer_status"] = "on_hold",
            ["account_manager"] = d.User("rnovak"), ["region"] = "Midwest", ["city"] = "Chicago", ["state"] = "IL", ["country"] = "USA",
        }, "rnovak", daysAgo: 120);

        // Feedback: one completed (compliment), one open concern.
        var fb1 = await d.CreateAsync("feedback", new()
        {
            ["subject"] = "Positive feedback on VX-200 in-service training", ["customer"] = stMary.Id, ["contact"] = "Tom Becker",
            ["feedback_type"] = "compliment", ["channel"] = "email", ["received_on"] = DemoContext.Days(-40), ["owner"] = d.User("sortiz"), ["product"] = "VX-200",
            ["description"] = "ICU nursing staff found the new hands-on in-service very effective; requested the same for the night shift.",
            ["investigation"] = "Shared with the clinical education team.", ["action_taken"] = "Night-shift in-service scheduled.",
            ["response"] = "Thank you; Acme clinical education will run night-shift sessions on the 14th and 15th.",
        }, "sortiz", daysAgo: 40);
        await d.RouteAsync(fb1.Id, "approval", assignees: ["dokafor"]);
        await d.RouteAsync(fb1.Id, "completed");
        await d.CreateAsync("feedback", new()
        {
            ["subject"] = "Concern about alarm volume on VX-200 in recovery bays", ["customer"] = bostonGen.Id, ["contact"] = "Priya Raman",
            ["feedback_type"] = "concern", ["channel"] = "phone", ["received_on"] = DemoContext.Days(-4), ["owner"] = d.User("jlee"), ["product"] = "VX-200",
            ["description"] = "Clinical engineering says the maximum alarm volume is hard to hear in the post-op recovery bays with curtains closed.",
        }, "jlee", daysAgo: 4);
    }
}

public sealed class CustomerBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["customer"];

    public override Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (ctx.Access.RoleFor(ctx.Record.AppId) < AppRole.Author) return Task.FromResult(Enumerable.Empty<RecordAction>());
        var list = new List<RecordAction>
        {
            new() { Key = "cus-feedback", Label = "New feedback", Icon = "bi-chat-square-text", Group = "Create", Description = "Record feedback from this customer (CFB-002)." },
        };
        if (ctx.Config.FormsByKey.ContainsKey("complaint"))
            list.Add(new() { Key = "cus-complaint", Label = "New complaint", Icon = "bi-chat-left-dots", Group = "Create", Description = "Log a product complaint from this customer." });
        return Task.FromResult<IEnumerable<RecordAction>>(list);
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (ctx.Access.RoleFor(ctx.Record.AppId) < AppRole.Author) return ActionOutcome.Fail("You need author access to customers.");
        var s = ctx.Snapshot;
        var primary = s.Rows("contacts").FirstOrDefault(r => r.Values.Bool("contact_primary") == true) ?? s.Rows("contacts").FirstOrDefault();
        var (rec, err) = actionKey switch
        {
            "cus-feedback" => await QualityLinks.CreateFromAsync(ctx, "feedback", new()
            {
                ["subject"] = $"Feedback from {s.Text("customer_name")}", ["customer"] = s.Record.Id, ["contact"] = primary?.Values.Text("contact_name"),
                ["feedback_type"] = "inquiry", ["owner"] = ctx.Me.Id,
            }),
            "cus-complaint" => await QualityLinks.CreateFromAsync(ctx, "complaint", new()
            {
                ["subject"] = $"Complaint from {s.Text("customer_name")}", ["customer"] = s.Record.Id, ["owner"] = ctx.Me.Id,
                ["reporter_name"] = primary?.Values.Text("contact_name"), ["reporter_organization"] = s.Text("customer_name"),
                ["reporter_email"] = primary?.Values.Text("contact_email"), ["reporter_phone"] = primary?.Values.Text("contact_phone"), ["reporter_country"] = s.Text("country"),
            }),
            _ => ((Record?)null, (string?)"This action isn't available."),
        };
        return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"{rec.Number} created for {s.Text("customer_name")}.", rec.Id);
    }

    /// <summary>CFB-001: the feedback history lists every feedback record (and complaint) for the customer automatically.</summary>
    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var related = await CustomerHistory.ForCustomerAsync(ctx, ctx.Record.Id);
        return
        [
            new RecordPanel { Key = "cus-history", Title = "Feedback history", Icon = "bi-clock-history", Placement = "tab", Count = related.Count,
                Partial = "~/Modules/Quality/Views/_CustomerHistory.cshtml", Model = related },
        ];
    }
}

public sealed record CustomerHistoryItem(Guid Id, string Number, string Title, string FormName, string FormIcon, string AppColor, string? PhaseName, PhaseType PhaseType, DateOnly? Due, DateTime Created);

public static class CustomerHistory
{
    /// <summary>Feedback, complaints and returns that reference the customer (security-trimmed).</summary>
    public static async Task<List<CustomerHistoryItem>> ForCustomerAsync(RecordContext ctx, Guid customerId)
    {
        var formIds = new[] { "feedback", "complaint", "rma" }.Select(k => ctx.Config.FormsByKey.GetValueOrDefault(k)?.Id).OfType<int>().ToList();
        var recs = await ctx.Access.Readable().AsNoTracking()
            .Where(r => formIds.Contains(r.FormId) && ctx.Db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "customer" && v.RefGuid == customerId))
            .OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync();
        return recs.Select(r =>
        {
            var f = ctx.Config.FormsById[r.FormId];
            return new CustomerHistoryItem(r.Id, r.Number, r.Title, f.Name, f.Icon, f.App.Color, ctx.Config.Phase(r.PhaseId)?.Name, r.PhaseType, r.DueDate, r.CreatedAt);
        }).ToList();
    }
}

public sealed class FeedbackBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["feedback"];

    /// <summary>CFB-002: the contact must be one of the customer profile's contacts.</summary>
    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var contact = ctx.Proposed.Main.Text("contact")?.Trim();
        if (string.IsNullOrEmpty(contact) || ctx.Proposed.Main.Link("customer") is not { } customerId) return;
        var names = await ctx.Services.GetRequiredService<VellumDbContext>().RecordValues.AsNoTracking()
            .Where(v => v.RecordId == customerId && v.RowId != null && v.FieldKey == "contact_name").Select(v => v.Text).ToListAsync();
        if (names.Count > 0 && !names.Any(n => string.Equals(n, contact, StringComparison.OrdinalIgnoreCase)))
            ctx.Error("contact", $"Choose one of the customer's contacts: {string.Join(", ", names)}. Add new contacts on the customer profile first.");
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (!ctx.IsOpen() || !await ctx.CanActAsync()) return [];
        var s = ctx.Snapshot;
        var list = new List<RecordAction>();
        if (s.Links("capa_record").Count == 0)
            list.Add(new() { Key = "fb-capa", Label = "Create CAPA", Icon = "bi-shield-check", Group = "Create", Description = "Raise a CAPA linked to this feedback." });
        if (s.Links("rma_record").Count == 0 && ctx.Config.FormsByKey.ContainsKey("rma"))
            list.Add(new() { Key = "fb-rma", Label = "Create RMA", Icon = "bi-box-arrow-in-left", Group = "Create", Description = "Authorize a customer return linked to this feedback." });
        list.Add(new()
        {
            Key = "fb-notify", Label = "Notify customer", Icon = "bi-envelope", Group = "Actions",
            Description = "Email the response to the customer contact. The message is kept in this record's email log (CFB-002).",
            Inputs = [new ActionField("message", "Message", "textarea", true, Default: s.Text("response"))],
        });
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (!ctx.IsOpen() || !await ctx.CanActAsync()) return ActionOutcome.Fail("Only the current assignee or an application manager can do this.");
        var s = ctx.Snapshot;
        if (actionKey == "fb-notify")
        {
            var customerId = s.Data.Main.Link("customer");
            var contact = s.Text("contact");
            var email = customerId == null ? null : await ctx.Db.RecordValues.AsNoTracking()
                .Where(v => v.RecordId == customerId && v.FieldKey == "contact_email" && v.RowId != null &&
                            ctx.Db.RecordValues.Any(n => n.RowId == v.RowId && n.FieldKey == "contact_name" && n.Text == contact))
                .Select(v => v.Text).FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(email)) return ActionOutcome.Fail("The contact has no email address on the customer profile.");
            var message = input.Get("message");
            if (string.IsNullOrWhiteSpace(message)) return ActionOutcome.Fail("Write the message to send.");
            var subject = $"Acme Medical Devices: your feedback {s.Record.Number}";
            ctx.Db.EmailLog.Add(new EmailLogEntry { RecordId = s.Record.Id, To = email, Subject = subject, Body = message, At = DateTime.UtcNow, Status = "Queued" });
            ctx.Db.Outbox.Add(new OutboxMessage { Type = "email", RecordId = s.Record.Id, CreatedAt = DateTime.UtcNow,
                Payload = JsonSerializer.Serialize(new { to = email, subject, body = message, url = (string?)null }) });
            ctx.Get<AuditService>().Record(s.Record.Id, "Customer notified", $"Email sent to {contact} <{email}>", phaseName: s.Phase?.Name);
            await ctx.Db.SaveChangesAsync();
            return ActionOutcome.Ok($"Email queued to {contact}.");
        }
        var (rec, err) = actionKey switch
        {
            "fb-capa" => await QualityLinks.CreateFromAsync(ctx, "capa", new()
            {
                ["subject"] = s.Text("subject"), ["capa_type"] = "corrective", ["source"] = "customer_complaint", ["priority"] = "medium",
                ["owner"] = ctx.Me.Id, ["source_record"] = s.Record.Id, ["product"] = s.Text("product"), ["description"] = s.Text("description"),
            }, "capa_record"),
            "fb-rma" => await QualityLinks.CreateFromAsync(ctx, "rma", new()
            {
                ["subject"] = $"Return for {s.Record.Number}: {s.Text("subject")}", ["customer"] = s.Data.Main.Link("customer"), ["owner"] = ctx.Me.Id,
                ["customer_contact"] = s.Text("contact"), ["description"] = s.Text("description"),
            }, "rma_record"),
            _ => ((Record?)null, (string?)"This action isn't available."),
        };
        return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"{rec.Number} created and linked to {s.Record.Number}.", rec.Id);
    }
}
