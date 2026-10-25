using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Records;

namespace Vellum.Tests.E2e;

/// <summary>
/// The designer end to end (DSN §3): an administrator builds and changes an application in a draft, validates and publishes
/// it, and people see the change only after publishing. Every test designs its own application, so the packaged ones other
/// tests rely on are never changed.
/// </summary>
[Collection("vellum")]
public class DesignerE2eTests(VellumFixture fx)
{
    /// <summary>A new application (DSN-001) everyone can author in, published once. Its form and workflow share the app's key.</summary>
    internal static async Task<string> PublishedAppAsync(Browser admin, string name)
    {
        var key = "e2e" + E2e.Tag().ToLowerInvariant();
        var app = await admin.NewAppAsync($"{name} {key[3..]}", key);
        app.ExpectText("This application isn't published yet");
        await admin.SetDefaultAccessAsync(key, "Author");
        await admin.PublishAsync(key, "First version");
        return key;
    }

    [Fact] // DSN-001, DSN-003, DSN-101, DSN-1201, DSN-1203, PLT-501
    public async Task A_form_change_reaches_users_only_after_it_is_published()
    {
        using var admin = await fx.SignedInAsync("admin");
        var key = "e2e" + E2e.Tag().ToLowerInvariant();
        var name = $"Equipment Requests {key[3..]}";
        await admin.NewAppAsync(name, key);

        // Until it's published, nobody else can see the application.
        using var jlee = await fx.SignedInAsync("jlee");
        await jlee.GoAsync($"/apps/{key}", HttpStatusCode.NotFound);
        await admin.SetDefaultAccessAsync(key, "Author");
        var v1 = await admin.PublishAsync(key, "First version");
        Assert.Contains("Published", v1.Toast);
        var first = await jlee.CreateAsync(key, f => f.Set("f.title", "Calibrate the torque driver"));
        Assert.Equal("Draft", first.Phase());

        // The designer adds an optional field, renames a label and makes it required in Draft (DSN-302, PLT-502).
        await admin.SaveFormAsync(key, key, form =>
        {
            form.Section("details")["fields"]!.AsArray().Add(DesignerSteps.NewField("serial_number", "Serial number"));
            var description = form.Field("description");
            description["label"] = "Problem description";
            description["requiredInPhases"] = DesignerSteps.Strings("draft");
        });
        var overview = await admin.GoAsync($"/designer/apps/{key}");
        overview.ExpectText("Unpublished changes").ExpectText($"Users still see version {fx.Services.GetRequiredService<Vellum.Engine.ConfigCache>().Current.Version}");

        // DSN-003: users keep working on the live version while the draft is open.
        var unchanged = await jlee.NewAsync(key);
        Assert.Null(unchanged.Q("[name='f.serial_number']"));
        unchanged.ExpectText("Description").ExpectNoText("Problem description");

        // Validate: the publish page lists the changes and passes (DSN-1201), with the impact on records (SAF-071).
        var plan = await admin.GoAsync($"/designer/apps/{key}/publish");
        Assert.Equal("0", Page.Squash(plan.One(".dz-count.tone-ok b").TextContent));
        plan.ExpectText("Serial number").ExpectText("Problem description").ExpectText("becomes required");
        Assert.Null(plan.Q("form[action$='/publish'] button[disabled]"));

        var published = await admin.PublishAsync(key, "Track serial numbers (CR-1042)");
        var version = int.Parse(published.Url.Query.Split("v=")[1], CultureInfo.InvariantCulture);
        published.ExpectText($"Version {version}").ExpectText("Serial number");

        // DSN-1203: the history lists the version with its reason and change count.
        var history = await admin.GoAsync($"/designer/versions?app={key}");
        var row = history.All("tbody tr").First(r => r.TextContent.Contains($"v{version}"));
        Assert.Contains("Track serial numbers (CR-1042)", row.TextContent);
        Assert.Contains("Live", row.TextContent);
        Assert.Equal("2", Page.Squash(row.QuerySelector("td.numeric")!.TextContent));

        // Users now get the new field and the new label, can save a value and see it on the record.
        var created = await jlee.CreateAsync(key, f => f.Set("f.title", "Replace the bench scale").Set("f.serial_number", "SN-20931"));
        Assert.Equal("SN-20931", created.FieldValue("serial_number"));
        created.ExpectText("Problem description");

        // Problem description is now required to leave Draft, also for the record that existed before the change.
        var refused = await created.RouteAsync("Submit for review");
        Assert.Contains(refused.Errors, e => e.Contains("Problem description is required"));
        var refusedOld = await (await jlee.GoAsync(first.Path)).RouteAsync("Submit for review");
        Assert.Contains(refusedOld.Errors, e => e.Contains("Problem description is required"));

        var saved = await (await jlee.GoAsync(created.Path)).SaveAsync(f => f.Set("f.description", "Reads 3 g high after the drop."));
        Assert.Equal("Saved.", saved.Toast);
        var review = await saved.RouteOkAsync("Submit for review", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Review", review.Phase());
        Assert.Equal("SN-20931", review.FieldText("serial_number"));
        review.ExpectText("Reads 3 g high after the drop.");
    }

    [Fact] // DSN-003: discarding throws the draft away and leaves the live configuration as it was.
    public async Task Discarding_a_draft_leaves_the_live_application_unchanged()
    {
        using var admin = await fx.SignedInAsync("admin");
        var key = await PublishedAppAsync(admin, "Tool Loans");
        await admin.SaveFormAsync(key, key, form => form.Field("title")["label"] = "Headline");
        var publish = await admin.GoAsync($"/designer/apps/{key}/publish");
        publish.ExpectText("Headline");

        var app = await admin.GoAsync($"/designer/apps/{key}");
        var discarded = await app.Form($"form[action='/designer/apps/{key}?handler=Discard']").SubmitAsync();
        Assert.Equal($"/designer/apps/{key}", discarded.Url.AbsolutePath);
        Assert.Equal("Draft discarded. The live configuration is unchanged.", discarded.Toast);
        discarded.ExpectNoText("Unpublished changes");

        (await admin.GoAsync($"/designer/apps/{key}/publish")).ExpectText("Nothing to publish");
        var designer = await admin.GoAsync($"/designer/apps/{key}/forms/{key}");
        Assert.Equal("Title", designer.Island("dz-form-data")["form"]!.AsObject().Field("title")["label"]!.GetValue<string>());
        using var jlee = await fx.SignedInAsync("jlee");
        (await jlee.NewAsync(key)).ExpectText("Title").ExpectNoText("Headline");
    }

    [Fact] // DSN-1201, SAF-070, INV-135, DSN-612: errors block publishing; nothing reaches users.
    public async Task Validation_blocks_a_broken_workflow_from_being_published()
    {
        using var admin = await fx.SignedInAsync("admin");
        var key = await PublishedAppAsync(admin, "Gauge Repairs");
        var cfg = fx.Services.GetRequiredService<Vellum.Engine.ConfigCache>();
        var versionBefore = cfg.Current.Version;

        // Review edits and shows Details read-only at once, and a new phase nothing routes to.
        var saved = await admin.SaveWorkflowAsync(key, key, key, wf =>
        {
            var review = wf.Phase("review");
            review["editableSections"] = DesignerSteps.Strings("details");
            review["readOnlySections"] = DesignerSteps.Strings("details");
            wf["phases"]!.AsArray().Insert(2, new JsonObject
            {
                ["id"] = null, ["key"] = "rework", ["name"] = "Rework", ["type"] = "Normal", ["dueDays"] = 3, ["assignment"] = "Any",
                ["editableSections"] = new JsonArray(), ["readOnlySections"] = DesignerSteps.Strings("*"), ["hiddenSections"] = new JsonArray(),
            });
        });
        Assert.Contains(saved["issues"]!.AsArray(), i => ((string?)i!["message"])!.Contains("both editable and read-only"));

        var plan = await admin.GoAsync($"/designer/apps/{key}/publish");
        plan.ExpectText("Fix these errors to publish").ExpectText("can't be reached").ExpectText("both editable and read-only");
        Assert.NotNull(plan.Q($"form[action='/designer/apps/{key}/publish'] button[disabled]"));
        plan.ExpectText("Blocked by");

        // Pressing Publish anyway (a stale page, a crafted post) is refused on the server.
        var refused = await plan.Form($"form[action='/designer/apps/{key}/publish']").Set("Note", "Try anyway").Set("AcceptWarnings", "true").SubmitAsync();
        refused.ExpectText("Nothing was published");
        Assert.Contains(refused.Errors, e => e.Contains("can't be reached"));
        Assert.Equal(versionBefore, cfg.Current.Version);

        // The live workflow is untouched: a new record still goes Draft → Review.
        using var jlee = await fx.SignedInAsync("jlee");
        var rec = await jlee.CreateAsync(key, f => f.Set("f.title", "Broken gauge"));
        Assert.Equal("Review", (await rec.RouteOkAsync("Submit for review", assignees: [fx.Uid("sortiz")])).Phase());

        // Fixing the draft makes it publishable again.
        await admin.SaveWorkflowAsync(key, key, key, wf =>
        {
            wf.Phase("review")["editableSections"] = new JsonArray();
            wf.Phase("review")["readOnlySections"] = DesignerSteps.Strings("*");
            wf["phases"]!.AsArray().Remove(wf.Phase("rework"));
        });
        (await admin.GoAsync($"/designer/apps/{key}/publish")).ExpectText("Nothing to publish");
    }

    [Fact] // DSN-601..612, INV-071: route labels and due-date rules take effect when published.
    public async Task Workflow_changes_take_effect_after_publishing()
    {
        using var admin = await fx.SignedInAsync("admin");
        var key = await PublishedAppAsync(admin, "Fixture Builds");
        using var jlee = await fx.SignedInAsync("jlee");
        var before = await jlee.CreateAsync(key, f => f.Set("f.title", "Before the change"));
        Assert.Contains("Submit for review", before.RouteOptions());

        await admin.SaveWorkflowAsync(key, key, key, wf =>
        {
            wf.Route("draft", "review")["label"] = "Send to the fixture engineer";
            wf.Phase("review")["dueDays"] = 9;
            wf.Phase("review")["name"] = "Engineering review";
        });
        // Not live yet.
        Assert.Contains("Submit for review", (await jlee.GoAsync(before.Path)).RouteOptions());

        var publish = await admin.GoAsync($"/designer/apps/{key}/publish");
        publish.ExpectText("Engineering review").ExpectText("Send to the fixture engineer");
        await admin.PublishAsync(key, "Longer engineering review");

        var after = await jlee.GoAsync(before.Path);
        Assert.Equal(["Send to the fixture engineer"], after.RouteOptions().Where(o => o.StartsWith("Send")).ToList());
        Assert.DoesNotContain("Submit for review", after.RouteOptions());
        var routed = await after.RouteOkAsync("Send to the fixture engineer", assignees: [fx.Uid("sortiz")]);
        Assert.Equal("Engineering review", routed.Phase());
        var expected = await fx.Services.CreateScope().ServiceProvider.GetRequiredService<BusinessCalendar>().AddAsync(BusinessCalendar.Today, 9, businessDays: true);
        Assert.Equal($"Due {expected.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}", routed.One(".vl-record-id .vl-due").GetAttribute("title"));
    }

    [Fact] // DSN-1205: the data dictionary, where-used and Platform Engine pages.
    public async Task Dictionary_usage_and_engine_pages_describe_the_configuration()
    {
        using var admin = await fx.SignedInAsync("admin");
        var key = await PublishedAppAsync(admin, "Spare Parts");
        await admin.SaveFormAsync(key, key, form =>
        {
            var lot = DesignerSteps.NewField("bin", "Storage bin", "Keyword");
            lot["keywordList"] = "quality_site";
            form.Section("details")["fields"]!.AsArray().Add(lot);
        });
        await admin.PublishAsync(key, "Add storage bin");

        var dictionary = await admin.GoAsync($"/designer/engine/forms/{key}");
        dictionary.ExpectText("Storage bin").ExpectText("Title").ExpectText("quality_site");

        var usage = await admin.GoAsync($"/designer/engine/usage?kind=list&key=quality_site");
        usage.ExpectText("Storage bin");
        var fieldUsage = await admin.GoAsync($"/designer/engine/usage?kind=field&key={key}.title");
        fieldUsage.ExpectText("Title");

        var engine = await admin.GoAsync($"/designer/engine?q={key}");
        engine.ExpectText(key);
        await admin.GoAsync("/designer/engine?tab=orphans");
        await admin.GoAsync($"/designer/apps/{key}/settings");
        (await admin.GoAsync("/designer")).ExpectText($"Spare Parts {key[3..]}");
    }

    [Fact] // DSN-001, INV-082: designer rights are checked on the server for every page and handler.
    public async Task People_without_designer_rights_cannot_reach_the_designer()
    {
        using var admin = await fx.SignedInAsync("admin");
        var key = await PublishedAppAsync(admin, "Badge Requests");

        using var nflores = await fx.SignedInAsync("nflores");
        Assert.StartsWith("/forbidden", (await nflores.GoAsync("/designer")).Url.AbsolutePath);
        Assert.StartsWith("/forbidden", (await nflores.GoAsync("/designer/versions")).Url.AbsolutePath);
        Assert.StartsWith("/forbidden", (await nflores.GoAsync("/designer/engine")).Url.AbsolutePath);
        Assert.StartsWith("/forbidden", (await nflores.GoAsync($"/designer/apps/{key}")).Url.AbsolutePath);
        Assert.StartsWith("/forbidden", (await nflores.GoAsync($"/designer/apps/{key}/forms/{key}")).Url.AbsolutePath);
        Assert.StartsWith("/forbidden", (await nflores.GoAsync($"/designer/engine/forms/{key}")).Url.AbsolutePath);
        var home = await nflores.GoAsync("/");
        Assert.Null(home.Q("a[href='/designer']"));

        // The save handler a script posts to refuses them too, and nothing reaches the draft.
        var reply = await nflores.PostJsonAsync($"/designer/apps/{key}/forms/{key}?handler=Save", new { form = new { key, name = "Hijacked" } });
        Assert.Equal(HttpStatusCode.Forbidden, reply.Status);
        var publish = await home.Form("form[action='/logout']").Set("Note", "Not allowed").SubmitToAsync($"/designer/apps/{key}/publish");
        Assert.StartsWith("/forbidden", publish.Url.AbsolutePath);
        (await admin.GoAsync($"/designer/apps/{key}/publish")).ExpectText("Nothing to publish");

        // A designer of one application can't open another one (sortiz designs CAPA, not this app).
        using var sortiz = await fx.SignedInAsync("sortiz");
        await sortiz.GoAsync("/designer/apps/capa");
        Assert.StartsWith("/forbidden", (await sortiz.GoAsync($"/designer/apps/{key}")).Url.AbsolutePath);
        (await sortiz.GoAsync("/designer")).ExpectNoText($"Badge Requests {key[3..]}");

        using var anon = new Browser(fx);
        Assert.StartsWith("/login", (await anon.GoAsync("/designer")).Url.AbsolutePath);
    }
}
