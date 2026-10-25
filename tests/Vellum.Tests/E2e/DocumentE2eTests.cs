using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Security;

namespace Vellum.Tests.E2e;

/// <summary>Document Control end to end through the web pages: author, review, sign, release, read.</summary>
[Collection("vellum")]
public class DocumentE2eTests(VellumFixture fx)
{
    /// <summary>jlee writes a new SOP with a PDF and sends it to Document Control. Returns the record page.</summary>
    internal static async Task<Page> NewSopAsync(VellumFixture fx, Browser author, string title)
    {
        var page = await author.CreateAsync("document", f => f
            .Pick("f.doc_type", "procedure")
            .Set("f.title", title)
            .Set("f.owner", fx.Uid("sortiz").ToString())
            .Set("f.reviewers", fx.Uids("dokafor", "tbrooks"))
            .Pick("f.department", "operations")
            .Set("f.revision_reason", "New procedure written for the end-to-end test.")
            .Pick("f.training_required", "no")
            .Set("f.training_justification", "Administrative content only.")
            .Attach("file.primary_file", $"{title}.pdf", E2e.Pdf(title), "application/pdf"));
        Assert.StartsWith("SOP-", page.Number());
        Assert.Equal("Draft", page.Phase());
        return page;
    }

    [Fact]
    public async Task A_new_SOP_goes_from_draft_to_effective_through_every_signed_approval()
    {
        var title = $"Line clearance {E2e.Tag()}";
        using var jlee = await fx.SignedInAsync("jlee");
        var doc = await NewSopAsync(fx, jlee, title);
        var url = doc.Path;
        var number = doc.Number();

        doc = await doc.RouteOkAsync("Submit to Document Control");
        Assert.Equal("Document Control Review", doc.Phase());

        // Document Control sees it in their work queue and checks the format.
        using var pshah = await fx.SignedInAsync("pshah");
        (await pshah.GoAsync("/work")).ExpectText(number);
        doc = await (await pshah.GoAsync(url)).RouteOkAsync("Format checked: send for initial approval");
        Assert.Equal("Initial Approval", doc.Phase());

        // The owner signs. A wrong password is refused and nothing moves.
        using var sortiz = await fx.SignedInAsync("sortiz");
        var bad = await (await sortiz.GoAsync(url)).RouteAsync("Approve: send to reviewers", password: "not-my-password");
        Assert.NotEmpty(bad.RouteErrors());
        Assert.Equal("Initial Approval", (await sortiz.GoAsync(url)).Phase());
        doc = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve: send to reviewers");
        Assert.Equal("Review", doc.Phase());

        // Both reviewers must sign (voting 100%): after the first the document stays in Review.
        using var dokafor = await fx.SignedInAsync("dokafor");
        doc = await (await dokafor.GoAsync(url)).RouteOkAsync("Reviewed");
        Assert.Equal("Review", doc.Phase());
        using var tbrooks = await fx.SignedInAsync("tbrooks");
        doc = await (await tbrooks.GoAsync(url)).RouteOkAsync("Reviewed");
        Assert.Equal("Final Approval", doc.Phase());

        doc = await (await sortiz.GoAsync(url)).RouteOkAsync("Approve for release");
        Assert.Equal("Awaiting Release", doc.Phase());
        doc = await (await pshah.GoAsync(url)).RouteOkAsync("Release");
        doc.ExpectText("Effective");
        Assert.Contains("signed", doc.One(".vl-sig-badge").TextContent);

        // A production operator can read the effective revision and its print view with the signature manifest.
        using var nflores = await fx.SignedInAsync("nflores");
        (await nflores.GoAsync(url)).ExpectText(title);
        var print = await nflores.GoAsync(url + "/print");
        print.ExpectText(fx.Services.GetRequiredService<UserDirectory>().Get("sortiz")!.DisplayName).ExpectText("Approved").ExpectText("Released");
        var search = await nflores.GoAsync($"/search?q={Uri.EscapeDataString(number)}");
        search.ExpectText(title);
    }

    [Fact] // PLT-501/502: a draft can be saved incomplete, but it can't be sent on until required fields are filled
    public async Task An_incomplete_draft_saves_but_cannot_be_sent_until_required_fields_are_filled()
    {
        using var jlee = await fx.SignedInAsync("jlee");
        var draft = await jlee.CreateAsync("document", f => f.Pick("f.doc_type", "procedure").Set("f.title", $"Half-written {E2e.Tag()}"));
        Assert.Equal("Draft", draft.Phase());
        Assert.Contains("Reviewers is required", draft.One("[data-route-panel] .vl-banner-warn").TextContent);

        var refused = await draft.RouteAsync("Submit to Document Control");
        Assert.Contains(refused.Errors, e => e.Contains("Reviewers is required"));
        Assert.Contains(refused.Errors, e => e.Contains("Primary controlled file is required"));
        Assert.Equal("Draft", (await jlee.GoAsync(draft.Path)).Phase());

        // What the author typed before the refused send is kept.
        var saved = await (await jlee.GoAsync(draft.Path)).SaveAsync(f => f
            .Set("f.reviewers", fx.Uids("dokafor", "tbrooks")).Set("f.revision_reason", "Filled in after the first attempt.")
            .Pick("f.training_required", "no").Set("f.training_justification", "No new tasks.")
            .Attach("file.primary_file", "content.pdf", E2e.Pdf(), "application/pdf"));
        Assert.Equal("Saved.", saved.Toast);
        Assert.Equal("Document Control Review", (await saved.RouteOkAsync("Submit to Document Control")).Phase());
    }

    [Fact]
    public async Task Signed_out_visitors_are_sent_to_sign_in_and_unknown_records_are_not_found()
    {
        using var anon = new Browser(fx);
        var login = await anon.GoAsync("/apps/documents");
        Assert.StartsWith("/login", login.Url.AbsolutePath);
        using var jlee = await fx.SignedInAsync("jlee");
        await jlee.GoAsync($"/records/{Guid.NewGuid()}", HttpStatusCode.NotFound);
    }
}
