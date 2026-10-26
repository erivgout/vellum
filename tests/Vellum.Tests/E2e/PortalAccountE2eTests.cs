using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Data;

namespace Vellum.Tests.E2e;

/// <summary>The supplier contact's own account page in the portal.</summary>
[Collection("vellum")]
public class PortalAccountE2eTests(VellumFixture fx)
{
    [Fact] // SEC-402: a contact ends another of their sessions, and the page never carries session IDs (the cookie values)
    public async Task A_portal_contact_ends_another_session_and_the_page_never_shows_session_ids()
    {
        using var cwu = await fx.SignedInAsync("cwu");
        using var sortiz = await fx.SignedInAsync("sortiz");
        var supplier = await SupplierSteps.SupplierWithPortalContactAsync(fx, cwu, sortiz, "Robin Hale");
        int userId;
        using (var scope = fx.Services.CreateScope())
            userId = await scope.ServiceProvider.GetRequiredService<VellumDbContext>().Users.Where(u => u.Username == supplier.Username).Select(u => u.Id).SingleAsync();

        using var laptop = new Browser(fx);
        await laptop.SignInAsync(supplier.Username);
        using var phone = new Browser(fx);
        await phone.SignInAsync(supplier.Username);
        var sessions = await fx.OpenSessionIdsAsync(userId);
        Assert.Equal(2, sessions.Count);

        var account = await laptop.GoAsync("/portal/account");
        Assert.All(sessions, sid => Assert.DoesNotContain(sid, account.Html));
        var ended = await account.Form("form[action*='handler=EndSession']").SubmitAsync();
        Assert.Equal("Session ended.", ended.Toast);

        Assert.Single(await fx.OpenSessionIdsAsync(userId));
        Assert.Equal(HttpStatusCode.OK, (await laptop.RawGetAsync("/portal")).StatusCode);
        var phoneHome = await phone.RawGetAsync("/portal");
        Assert.Equal(HttpStatusCode.Redirect, phoneHome.StatusCode);
        Assert.StartsWith("/login", phoneHome.Headers.Location!.OriginalString);
    }
}
