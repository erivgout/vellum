using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Security;

namespace Vellum.Tests.E2e;

/// <summary>The people in a scenario, each with their own signed-in browser (opened on first use).</summary>
public sealed class Cast(VellumFixture fx) : IDisposable
{
    private readonly Dictionary<string, Browser> _open = [];

    public async Task<Browser> As(string username)
    {
        if (!_open.TryGetValue(username, out var b)) _open[username] = b = await fx.SignedInAsync(username);
        return b;
    }

    /// <summary>Open a page as the given person.</summary>
    public async Task<Page> Open(string username, string url) => await (await As(username)).GoAsync(url);

    public string Name(string username) => fx.Services.GetRequiredService<UserDirectory>().Get(username)!.DisplayName;

    public void Dispose()
    {
        foreach (var b in _open.Values) b.Dispose();
    }
}

/// <summary>Document Control as people use it: the approval chain, status banners, revision history, views and the review schedule.</summary>
public static class DocumentSteps
{
    /// <summary>
    /// Draft → Awaiting Release with every signature, each person on their own page: the author submits, Document Control
    /// checks the format, the owner approves, both reviewers sign and the owner approves for release.
    /// </summary>
    public static async Task<Page> ToAwaitingReleaseAsync(this Cast c, string url, string author = "jlee")
    {
        await (await c.Open(author, url)).RouteOkAsync("Submit to Document Control");
        await (await c.Open("pshah", url)).RouteOkAsync("Format checked: send for initial approval");
        await (await c.Open("sortiz", url)).RouteOkAsync("Approve: send to reviewers");
        await (await c.Open("dokafor", url)).RouteOkAsync("Reviewed");
        await (await c.Open("tbrooks", url)).RouteOkAsync("Reviewed");
        var page = await (await c.Open("sortiz", url)).RouteOkAsync("Approve for release");
        Assert.Equal("Awaiting Release", page.Phase());
        return page;
    }

    /// <summary>Document Control releases the revision; it becomes effective today.</summary>
    public static async Task<Page> ReleaseAsync(this Cast c, string url)
    {
        var page = await (await c.Open("pshah", url)).RouteOkAsync("Release: make effective");
        Assert.True(page.IsEffective(), $"{page.Number()} isn't effective after release.\n{page.Excerpt()}");
        return page;
    }

    /// <summary>A new SOP by jlee taken all the way to effective. Returns the effective record page (as Document Control).</summary>
    public static async Task<Page> EffectiveSopAsync(this Cast c, VellumFixture fx, string title)
    {
        var draft = await DocumentE2eTests.NewSopAsync(fx, await c.As("jlee"), title);
        await c.ToAwaitingReleaseAsync(draft.Path);
        return await c.ReleaseAsync(draft.Path);
    }

    /// <summary>
    /// Revision B of an effective document by the quickest legitimate path: Document Control raises a change request, the
    /// owner accepts it, jlee marks the change as minor and Document Control releases it. Returns revision B's URL.
    /// </summary>
    public static async Task<string> MinorRevisionAsync(this Cast c, VellumFixture fx, string urlA, byte[]? content = null)
    {
        var cr = await (await c.Open("pshah", urlA)).ActionAsync("request_change", f => f
            .Set("a.summary", "Editorial update").Set("a.reason", "Editorial update of the wording.").Set("a.assign_to", fx.Uid("jlee").ToString()));
        await (await c.Open("sortiz", cr.Path)).RouteOkAsync("Accept: create new revision");
        var urlB = (await c.Open("jlee", urlA)).Banner("Revision B in progress").Href!;
        var b = await c.Open("jlee", urlB);
        b = await b.SaveAsync(f =>
        {
            f.Pick("f.minor_change", "yes").Pick("f.training_required", "no").Set("f.training_justification", "Editorial only.");
            if (content != null)
                f.Set("remove", b.All("input[name=remove]").Select(e => e.GetAttribute("value")).ToArray()) // the only file is the primary one
                    .Attach("file.primary_file", "revision-b.pdf", content, "application/pdf");
        });
        await b.RouteOkAsync("Minor change: send to release", comment: "Editorial update.");
        await c.ReleaseAsync(urlB);
        return urlB;
    }

    public static bool IsEffective(this Page page) => page.Q(".vl-record-id .vl-chip-effective") != null;

    /// <summary>Titles of the document status banners at the top of the record page.</summary>
    public static IReadOnlyList<string> Banners(this Page page) => page.All(".vl-doc-banner .ttl").Select(e => Page.Squash(e.TextContent)).ToList();

    /// <summary>The status banner whose title starts with the given text (title, text and link).</summary>
    public static (string Title, string Text, string? Href) Banner(this Page page, string titleStart)
    {
        var el = page.All(".vl-doc-banner").FirstOrDefault(b => Page.Squash(b.QuerySelector(".ttl")!.TextContent).StartsWith(titleStart, StringComparison.OrdinalIgnoreCase))
            ?? throw new Xunit.Sdk.XunitException($"No banner '{titleStart}' on {page.Path}. Banners: {string.Join(" | ", page.Banners())}");
        return (Page.Squash(el.QuerySelector(".ttl")!.TextContent), Page.Squash(el.QuerySelector(".small")?.TextContent ?? ""), el.QuerySelector("a[href]")?.GetAttribute("href"));
    }

    /// <summary>Rows of the Revision history tab: revision → (status, reason, approvals, effective date, related).</summary>
    public static IReadOnlyList<RevisionLine> RevisionHistory(this Page page) =>
        page.All(".vl-revhist tbody tr[data-paginate-row]").Select(tr =>
        {
            var td = tr.QuerySelectorAll("td").Select(x => Page.Squash(x.TextContent)).ToList();
            return new RevisionLine(Page.Squash(tr.QuerySelector("td .fw-semibold")!.TextContent), td[1], td[2], td[3], td[4], td[5],
                tr.ClassList.Contains("is-current"), tr.QuerySelector("td a[href^='/records/']")?.GetAttribute("href"));
        }).ToList();

    /// <summary>Disabled entries of the record's Actions menus (label → reason).</summary>
    public static IReadOnlyDictionary<string, string> DisabledActions(this Page page) =>
        page.All(".vl-actionbar .dropdown-item.disabled").ToDictionary(
            e => Page.Squash(e.TextContent.Replace(e.GetAttribute("title") ?? "\u0000", "")), e => e.GetAttribute("title") ?? "");

    /// <summary>Errors shown in an action dialog after a refused action.</summary>
    public static IReadOnlyList<string> ActionErrors(this Page page, string actionKey) =>
        page.All($"#action-{actionKey} .vl-banner-bad li").Select(e => Page.Squash(e.TextContent)).ToList();

    /// <summary>Rows of the list view on an application page, as visible text.</summary>
    public static IReadOnlyList<string> ViewRows(this Page page) =>
        page.All(".vl-table tbody tr[data-href]").Select(tr => Page.Squash(tr.TextContent)).ToList();

    /// <summary>Rows of a list view that mention the given text (for example a document number).</summary>
    public static IReadOnlyList<string> RowsWith(this Page page, string text) =>
        page.ViewRows().Where(r => r.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Open a Document Control view filtered by a search term, the way the toolbar's filter box does.</summary>
    public static Task<Page> DocumentViewAsync(this Browser b, string view, string q) =>
        b.GoAsync($"/apps/documents?view={view}&q={Uri.EscapeDataString(q)}");

    /// <summary>A date as the pages show it.</summary>
    public static string Day(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The History › Field changes entries of one field (old and new value per row).</summary>
    public static IReadOnlyList<string> FieldChanges(this Page page, string fieldLabel) =>
        page.All("#h-fields .card").Where(card => Page.Squash(card.QuerySelector(".card-title-sm")?.TextContent ?? "") == fieldLabel)
            .SelectMany(card => card.QuerySelectorAll("tbody tr")).Select(tr => Page.Squash(tr.TextContent)).ToList();

    /// <summary>Every comment of the record (the Comments card and its "View all" history), newest first.</summary>
    public static IReadOnlyList<string> Comments(this Page page) =>
        page.All(".vl-comment .txt").Select(e => Page.Squash(e.TextContent)).Where(t => t.Length > 0).Distinct().ToList();
}

public sealed record RevisionLine(string Revision, string Status, string Reason, string Approvals, string Effective, string Related, bool Current, string? Href);
