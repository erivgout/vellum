using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Tests.E2e;

/// <summary>
/// Steps shared by the quality-event end-to-end tests: activities and related records as the record page lists them,
/// behavior actions that must succeed, view lookups, and the Adverse Event Reporting setup done through its pages.
/// </summary>
public static class QualitySteps
{
    public sealed record ListedRecord(string Number, string Href, string Text);

    public static string Today => BusinessCalendar.Today.ToString("yyyy-MM-dd");
    public static string Day(int offset) => BusinessCalendar.Today.AddDays(offset).ToString("yyyy-MM-dd");

    /// <summary>A demo user's display name, as pages show it.</summary>
    public static string Name(this VellumFixture fx, string username) =>
        fx.Services.GetRequiredService<UserDirectory>().Get(username)!.DisplayName;

    /// <summary>Activities on the record's Activities tab.</summary>
    public static IReadOnlyList<ListedRecord> Activities(this Page page) =>
        page.All("#tab-activities tbody tr").Select(tr => new ListedRecord(Page.Squash(tr.QuerySelector(".col-number a")!.TextContent),
            tr.QuerySelector(".col-number a")!.GetAttribute("href")!, Page.Squash(tr.TextContent))).ToList();

    /// <summary>Records in the rail's Related records card.</summary>
    public static IReadOnlyList<ListedRecord> Related(this Page page) =>
        page.All(".vl-rail a.vl-list-item").Select(a => new ListedRecord(Page.Squash(a.QuerySelector(".num")?.TextContent ?? ""),
            a.GetAttribute("href")!, Page.Squash(a.TextContent))).ToList();

    /// <summary>Open the (single) activity whose number starts with <paramref name="prefix"/> and whose row contains <paramref name="text"/>.</summary>
    public static Task<Page> OpenActivityAsync(this Browser b, Page parent, string prefix, string? text = null)
    {
        var rows = parent.Activities().Where(a => a.Number.StartsWith(prefix) && (text == null || a.Text.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        if (rows.Count != 1)
            throw new Xunit.Sdk.XunitException($"Expected one {prefix} activity{(text != null ? $" with '{text}'" : "")} on {parent.Path}, found: {string.Join(" | ", parent.Activities().Select(a => a.Text))}");
        return b.GoAsync(rows[0].Href);
    }

    /// <summary>Save and require the "Saved." toast (fails with the page's messages otherwise).</summary>
    public static async Task<Page> SaveOkAsync(this Page page, Action<FormFill> fill)
    {
        var result = await page.SaveAsync(fill);
        if (result.Toast != "Saved.")
            throw new Xunit.Sdk.XunitException($"Saving {page.Path} failed: {string.Join(" | ", result.Errors)}\n{result.Excerpt()}");
        return result;
    }

    /// <summary>Run a behavior action and require success: a toast and no errors in its dialog.</summary>
    public static async Task<Page> ActionOkAsync(this Page page, string actionKey, Action<FormFill>? fill = null, string? password = null, string? comment = null)
    {
        if (!page.Actions().Contains(actionKey))
            throw new Xunit.Sdk.XunitException($"No action '{actionKey}' on {page.Path}. Offered: {string.Join(", ", page.Actions())}");
        var result = await page.ActionAsync(actionKey, fill, password, comment);
        var errors = result.ActionErrors(actionKey);
        if (result.Toast == null)
            throw new Xunit.Sdk.XunitException($"Action '{actionKey}' failed: {string.Join(" | ", errors)}\n{result.Excerpt()}");
        return result;
    }

    /// <summary>The record page linked from a view row whose text contains <paramref name="text"/>.</summary>
    public static async Task<string> FindInViewAsync(this Browser b, string app, string view, string text)
    {
        var page = await b.GoAsync($"/apps/{app}?view={view}&q={Uri.EscapeDataString(text)}");
        var row = page.All("tbody tr[data-href]").FirstOrDefault(tr => Page.Squash(tr.TextContent).Contains(text, StringComparison.OrdinalIgnoreCase))
            ?? throw new Xunit.Sdk.XunitException($"No row with '{text}' in {app} › {view}.\n{page.Excerpt()}");
        return row.GetAttribute("data-href")!;
    }

    /// <summary>The numbers listed in an application view (first column links).</summary>
    public static IReadOnlyList<string> ViewNumbers(this Page page) =>
        page.All("tbody tr[data-href] .col-number a").Select(a => Page.Squash(a.TextContent)).ToList();

    /// <summary>Unread and read notification titles of the signed-in user.</summary>
    public static async Task<string> NotificationsAsync(this Browser b) => (await b.GoAsync("/notifications")).Text;

    // ------------------------------------------------------------------ Adverse Event Reporting setup

    public const string AerSite = "kalamazoo";
    public const string AerCode = "1834567";
    private static readonly SemaphoreSlim AerLock = new(1, 1);

    /// <summary>
    /// AER-001/002 and INT-902 through the pages, idempotently: the application profile and product codes on
    /// Adverse Event Reporting › Setup (as rnovak), a sequential manufacturer mapping for Kalamazoo, and the simulated
    /// eMDR gateway in Administration › eMDR gateway (as admin). Uses the same profile and gateway settings as
    /// AdverseEventTests, so whichever test runs first sets up what the other expects.
    /// </summary>
    public static async Task EnsureAerSetupAsync(this VellumFixture fx)
    {
        await AerLock.WaitAsync();
        try
        {
            using var ra = await fx.SignedInAsync("rnovak");
            var setup = await ra.GoAsync("/quality/aer/setup");
            if (setup.Contains("Create application profile"))
            {
                var saved = await setup.Form("form[action*='handler=Profile']")
                    .Set("MappingEnabled", "true", "false").Set("ReceiptDelayMinutes", "60")
                    .Set("SignSubmit", "Submitted to FDA").Set("SignComplete", "Final acknowledgement confirmed").Set("SignVoid", "Voided")
                    .Set("Approvers", fx.Uid("sortiz").ToString()).SubmitAsync();
                Assert.Equal("Application profile saved.", saved.Toast);
                setup = saved;
            }
            if (!Page.Squash(setup.One("section[aria-labelledby='aer-map-title']").TextContent).Contains("Kalamazoo, MI"))
                await ra.CreateAsync("aer_mapping", f => f
                    .Pick("f.map_location", AerSite).Pick("f.map_reporter_type", "manufacturer").Pick("f.map_mfr_code_mode", "basic")
                    .Set("f.map_mfr_code", AerCode).Pick("f.map_mfr_seq_format", "sequential").Set("f.map_mfr_seq_initial", "1")
                    .Set("f.map_mfr_name", "Acme Medical Devices, Inc.").Set("f.map_mfr_address", "4100 Portage Rd, Kalamazoo, MI, USA")
                    .Set("f.map_contact_name", "Regulatory Affairs").Set("f.map_contact_address", "4100 Portage Rd, Kalamazoo, MI, USA")
                    .Set("f.map_contact_phone", "+1 269 555 0100"));
            setup = await ra.GoAsync("/quality/aer/setup");
            if (setup.Q("#product-codes") is not { } codes || !Page.Squash(codes.TextContent).Contains("FRN"))
            {
                var imported = await setup.Form("form[action*='handler=Import']")
                    .Set("Csv", "code,device name,class,regulation,panel,brand names\nFRN,\"Pump, Infusion\",2,880.5725,General Hospital,VX-200").SubmitAsync();
                Assert.StartsWith("Product codes imported", imported.Toast);
            }

            using var admin = await fx.SignedInAsync("admin");
            var emdr = await admin.GoAsync("/admin/emdr");
            var res = await emdr.Form("form[action*='handler=Save']")
                .Pick("AccountType", "test").Set("SenderOrganization", "Acme Medical Devices").Set("SenderRoutingId", "ACMETEST")
                .Set("SenderEmail", "emdr@acme.example").Pick("Gateway", "simulated").Set("SimulatedOutcome", "accept").SubmitAsync();
            Assert.Empty(res.Errors);
            Assert.NotNull(res.Toast);
        }
        finally { AerLock.Release(); }
    }
}
