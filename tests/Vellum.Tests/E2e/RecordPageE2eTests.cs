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
