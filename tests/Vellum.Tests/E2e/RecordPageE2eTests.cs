namespace Vellum.Tests.E2e;

/// <summary>Record page behavior shared by every application.</summary>
[Collection("vellum")]
public class RecordPageE2eTests(VellumFixture fx)
{
    [Fact] // PLT-205: activities are created from their parent; a bare link explains that instead of failing
    public async Task An_activity_form_opened_without_its_parent_explains_where_to_create_it()
    {
        using var sortiz = await fx.SignedInAsync("sortiz");
        var page = await sortiz.GoAsync("/records/new?form=action_item");
        Assert.Equal("/apps/actions", page.Url.AbsolutePath);
        Assert.Contains("created from their parent record", page.Toast);
    }

    [Fact] // INV-081: favorites never show a record or application the person can't open (for example after losing access)
    public async Task Favorites_drop_records_and_apps_the_person_can_no_longer_open()
    {
        var tag = E2e.Tag();
        // An application with no default access: only the administrator who built it can open it.
        using var admin = await fx.SignedInAsync("admin");
        var key = await DesignerE2eTests.PublishedAppAsync(admin, "Restricted favorites", access: null);
        var record = await admin.CreateAsync(key, f => f.Set("f.title", $"Favorite trimming {tag}"));

        // Stars saved while nflores could still open them (for example before an access change).
        using var nflores = await fx.SignedInAsync("nflores");
        var home = await nflores.GoAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.OK, (await nflores.PostJsonAsync("/api/favorites/toggle", new { kind = "record", refKey = record.RecordId.ToString(), label = $"{record.Number()} · Favorite trimming {tag}" })).Status);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await nflores.PostJsonAsync("/api/favorites/toggle", new { kind = "app", refKey = key, label = $"Restricted app {tag}" })).Status);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await nflores.PostJsonAsync("/api/favorites/toggle", new { kind = "app", refKey = "documents", label = $"Documents {tag}" })).Status);

        home = await nflores.GoAsync("/");
        var favorites = home.All(".vl-side-link .vl-side-text").Select(e => e.TextContent).ToList();
        Assert.Contains($"Documents {tag}", favorites);
        Assert.DoesNotContain(favorites, f => f.Contains($"Favorite trimming {tag}") || f.Contains($"Restricted app {tag}"));
    }

    [Fact]
    public async Task A_parent_record_offers_its_activities_under_New_activity()
    {
        using var sortiz = await fx.SignedInAsync("sortiz");
        var capa = await sortiz.CreateAsync("capa", f => f.Set("f.subject", $"Activity menu {E2e.Tag()}"));
        var menu = capa.One("[data-bs-toggle=dropdown]:has(.bi-plus-square)");
        Assert.Equal("New activity", Page.Squash(menu.TextContent));
        var item = menu.ParentElement!.QuerySelector("a.dropdown-item[href*='form=action_item']")!.GetAttribute("href")!;
        Assert.Equal($"/records/new?form=action_item&parent={capa.RecordId}", item);
        (await sortiz.GoAsync(item)).ExpectText($"Activity of {capa.Number()}");
    }
}
