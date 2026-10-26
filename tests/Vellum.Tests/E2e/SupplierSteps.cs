using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Records;

namespace Vellum.Tests.E2e;

/// <summary>
/// Supplier quality and supplier portal steps through the pages: approved materials and supplier profiles, inviting a portal
/// contact (the email really goes out through the outbox to the pickup folder) and answering requests in the portal.
/// </summary>
public static partial class SupplierSteps
{
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    /// <summary>The supplier quality engineer registers a material and approves it as its owner (signed).</summary>
    public static async Task<Page> ApprovedMaterialAsync(this Browser sqe, string partNumber)
    {
        var m = await sqe.CreateAsync("material", f => f
            .Set("f.part_number", partNumber).Set("f.description", "Polycarbonate housing").Pick("f.commodity", "molded")
            .Pick("f.sampling_plan", "z14_l2_10"));
        m = await m.RouteOkAsync("Submit for approval");
        m = await m.RouteOkAsync("Approve material");
        Assert.Equal("Approved", m.Phase());
        return m;
    }

    /// <summary>A prospective supplier profile with one contact, qualification evidence and one supplied material.</summary>
    public static Task<Page> NewSupplierAsync(this Browser sqe, string name, string risk, string contactName, string contactEmail, Guid material) =>
        sqe.CreateAsync("supplier", f => f
            .Set("f.name", name).Set("f.code", "V-" + E2e.Tag()).Pick("f.risk_class", risk).Pick("f.supplier_type", "component")
            .Set("f.scope", "Molded housings")
            .AddRow("contacts", ("contact_name", contactName), ("contact_role", "Quality Manager"), ("email", contactEmail))
            .Pick("f.qual_methods", "questionnaire", "cert_review")
            .Set("f.qual_date", Iso(BusinessCalendar.Today.AddDays(-10)))
            .Set("f.qual_summary", "Questionnaire and ISO 13485 certificate reviewed; no open issues.")
            .AddRow("supplied_materials", ("material", material.ToString()), ("supplier_part", "PX-" + E2e.Tag())));

    /// <summary>A Quality approver approves the supplier with an e-signature.</summary>
    public static async Task<Page> ApproveSupplierAsync(this Browser approver, string supplierUrl)
    {
        var page = await (await approver.GoAsync(supplierUrl)).ActionAsync("approve", f => f.Set("a.scope", "Molded housings to drawing DWG-200"),
            password: TestHelpers.Password);
        Assert.Empty(page.Errors);
        Assert.Contains("approved supplier list", page.Toast);
        Assert.Equal("Approved", page.FieldText("approval_status"));
        return page;
    }

    /// <summary>
    /// An approved supplier whose contact has been invited and has set a password. Returns the supplier page URL, its id
    /// and the contact's username.
    /// </summary>
    public static async Task<(string Url, Guid Id, string Name, string Username)> SupplierWithPortalContactAsync(VellumFixture fx, Browser cwu, Browser sortiz, string contact)
    {
        var tag = E2e.Tag();
        var material = await cwu.ApprovedMaterialAsync("H-" + tag);
        var name = $"Lakeside Molding {tag}";
        var email = $"{contact.Split(' ')[0].ToLowerInvariant()}.{tag.ToLowerInvariant()}@lakeside.example";
        var supplier = await cwu.NewSupplierAsync(name, "medium", contact, email, material.RecordId);
        await sortiz.ApproveSupplierAsync(supplier.Path);
        var username = await InviteAndActivateAsync(fx, cwu, supplier.Path, email);
        return (supplier.Path, supplier.RecordId, name, username);
    }

    /// <summary>
    /// The SQE invites the supplier's first contact from the Supplier portal tab, the email is delivered, and the contact
    /// follows the link and sets a password. Returns the username to sign in with.
    /// </summary>
    public static async Task<string> InviteAndActivateAsync(VellumFixture fx, Browser cwu, string supplierUrl, string email)
    {
        var supplier = await cwu.GoAsync(supplierUrl);
        var invited = await supplier.ActionAsync("portal-invite");
        Assert.Empty(invited.Errors);
        Assert.Contains("Invitation sent", invited.Toast);
        var mail = await DeliveredMailAsync(fx, email, "invites you to its supplier portal");
        var link = WelcomeLink().Match(mail);
        Assert.True(link.Success, "The invitation email has no set-password link:\n" + mail);
        var username = Regex.Match(mail, @"Your username: (\S+)").Groups[1].Value;

        using var contact = new Browser(fx);
        var welcome = await contact.GoAsync(link.Groups[1].Value);
        welcome.ExpectText("Choose a password to activate your account");
        var done = await welcome.Form("form[method=post]").Set("password", TestHelpers.Password).Set("confirm", TestHelpers.Password).SubmitAsync();
        done.ExpectText("Your password is set").ExpectText(username);
        return username;
    }

    /// <summary>
    /// An administrator runs outbound email delivery (Administration › Scheduled tasks › Run now) until the outbox is empty.
    /// Each run delivers the oldest batch, as the every-minute schedule would, and other tests leave mail queued too.
    /// </summary>
    public static async Task DeliverMailAsync(VellumFixture fx)
    {
        using var admin = await fx.SignedInAsync("admin");
        for (var runs = 0; runs < 20 && await PendingMailAsync(fx) > 0; runs++)
        {
            var task = await admin.GoAsync("/admin/tasks/outbox-dispatch");
            var ran = await task.Form("form[action*='handler=Run']").SubmitAsync();
            Assert.True(ran.Toast?.Contains("succeeded") == true, $"Running email delivery didn't succeed: {ran.Toast}");
        }
        Assert.Equal(0, await PendingMailAsync(fx));
    }

    private static async Task<int> PendingMailAsync(VellumFixture fx)
    {
        using var scope = fx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Vellum.Data.VellumDbContext>().Outbox.CountAsync(o => o.ProcessedAt == null && o.Attempts < 10);
    }

    /// <summary>Deliver the queued email and return the newest message to <paramref name="to"/> whose subject contains the text.</summary>
    public static async Task<string> DeliveredMailAsync(VellumFixture fx, string to, string subject)
    {
        await DeliverMailAsync(fx);
        var mail = Directory.Exists(fx.MailPickup)
            ? Directory.GetFiles(fx.MailPickup, "*.eml").OrderByDescending(File.GetLastWriteTimeUtc).Select(File.ReadAllText)
                .FirstOrDefault(m => m.Contains($"To: {to}\r\n", StringComparison.OrdinalIgnoreCase) && Regex.Match(m, @"Subject: (.*)\r\n").Groups[1].Value.Contains(subject))
            : null;
        return mail ?? throw new Xunit.Sdk.XunitException($"No email to {to} about \"{subject}\" in {fx.MailPickup}.");
    }

    /// <summary>The supplier's response form on a portal request page.</summary>
    public static FormFill PortalForm(this Page page) => page.Form("form[data-portal-form]");

    /// <summary>Press "Submit response" (or "Save draft") on a portal request.</summary>
    public static Task<Page> SubmitResponseAsync(this FormFill form, bool submit = true) =>
        form.SubmitAsync(submit ? "button[name=submit][value='1']" : "button[name=submit][value='0']");

    /// <summary>The share id of a portal request page.</summary>
    public static int ShareId(this Page page) => int.Parse(Regex.Match(page.Url.AbsolutePath, @"/portal/requests/(\d+)").Groups[1].Value);

    /// <summary>Open the request numbered <paramref name="number"/> from the portal home page, as the contact would.</summary>
    public static async Task<Page> OpenPortalRequestAsync(this Browser contact, string number)
    {
        var home = await contact.GoAsync("/portal");
        var item = home.All("a.vp-item").FirstOrDefault(a => a.TextContent.Contains(number))
            ?? throw new Xunit.Sdk.XunitException($"{number} isn't on the portal home page.\n{home.Excerpt()}");
        return await contact.GoAsync(item.GetAttribute("href")!);
    }

    [GeneratedRegex(@"https?://[^/\s]+(/portal/welcome\?token=\S+)")]
    private static partial Regex WelcomeLink();
}
