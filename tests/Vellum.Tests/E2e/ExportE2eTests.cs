namespace Vellum.Tests.E2e;

/// <summary>View exports and search results as people receive them.</summary>
[Collection("vellum")]
public class ExportE2eTests(VellumFixture fx)
{
    [Fact] // PLT-1103: the export is safe to open in a spreadsheet and keeps the order the list was sorted in
    public async Task A_view_export_neutralises_formulas_and_follows_the_list_sort()
    {
        var tag = E2e.Tag();
        using var sortiz = await fx.SignedInAsync("sortiz");
        await sortiz.CreateAsync("capa", f => f.Set("f.subject", $"=HYPERLINK(\"http://evil.example\",\"Open\") {tag}"));
        await sortiz.CreateAsync("capa", f => f.Set("f.subject", $"Alpha label check {tag}"));
        await sortiz.CreateAsync("capa", f => f.Set("f.subject", $"Zulu label check {tag}"));

        var list = await sortiz.GoAsync($"/apps/capa?view=open&q={tag}&sort=title&desc=true");
        var export = list.One("a[href*='/export.csv']").GetAttribute("href")!;
        Assert.Contains("sort=title", export);
        Assert.Contains("desc=true", export);

        var csv = await (await sortiz.RawGetAsync(export)).Content.ReadAsStringAsync();
        Assert.Contains($"\"'=HYPERLINK(\"\"http://evil.example\"\",\"\"Open\"\") {tag}\"", csv);
        Assert.DoesNotContain($"\"=HYPERLINK", csv);
        Assert.True(csv.IndexOf($"Zulu label check {tag}") < csv.IndexOf($"Alpha label check {tag}"), "The export should follow the descending title sort.");
    }

    [Fact] // a search for "amp" must not break the HTML entity of "R&D"
    public async Task Search_results_highlight_the_term_without_garbling_the_title()
    {
        var tag = E2e.Tag();
        using var sortiz = await fx.SignedInAsync("sortiz");
        await sortiz.CreateAsync("capa", f => f.Set("f.subject", $"R&D bench {tag}"));
        var results = await sortiz.GoAsync($"/search?q={Uri.EscapeDataString("R&D bench " + tag)}");
        var title = results.All(".ttl").First(t => t.TextContent.Contains(tag));
        Assert.Equal($"R&D bench {tag}", Page.Squash(title.TextContent));
        Assert.Equal($"R&D bench {tag}", Page.Squash(title.QuerySelector("mark")!.TextContent));
    }
}
