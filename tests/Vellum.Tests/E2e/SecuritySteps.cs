using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;

namespace Vellum.Tests.E2e;

/// <summary>A user created through Administration › Users › New.</summary>
public sealed record NewUser(int Id, string Username, string DisplayName, string Password);

/// <summary>
/// Sign-in, account and Administration Center steps for the security and administration end-to-end tests. Each step uses
/// the page a person would use; the few database reads are for values a person can't see (session cookies, settings to restore).
/// </summary>
public static class SecuritySteps
{
    /// <summary>Strong temporary password that passes the default policy.</summary>
    public const string TempPassword = "Kestrel-Temp-2026!";
    public const string NewPassword = "Harbor-Lights-77#";

    /// <summary>Kind of the toast queued by the last post ("ok" or "bad").</summary>
    public static string? ToastKind(this Page page) => page.Q("[data-toast]")?.GetAttribute("data-kind");

    /// <summary>Records listed on the search page (the page also repeats the search words, so look at the result rows).</summary>
    public static IReadOnlyList<string> SearchHits(this Page page) =>
        page.All("main a.vl-list-item[href^='/records/']").Select(a => Page.Squash(a.TextContent)).ToList();

    /// <summary>The message on the sign-in page (error banner), if any.</summary>
    public static string? LoginError(this Page page) => page.Q(".vl-login-card .vl-banner-bad") is { } e ? Page.Squash(e.TextContent) : null;

    public static string? LoginInfo(this Page page) => page.Q(".vl-login-card .vl-banner-info") is { } e ? Page.Squash(e.TextContent) : null;

    /// <summary>Identifier-first sign-in that returns whatever page results (no assertion), for failure paths.</summary>
    public static async Task<Page> TrySignInAsync(this Browser b, string username, string password, string? returnUrl = null)
    {
        var login = await b.GoAsync("/login" + (returnUrl != null ? "?returnUrl=" + Uri.EscapeDataString(returnUrl) : ""));
        var pw = await login.Form("form[action*='handler=Identify']").Set("Username", username).SubmitAsync();
        return await pw.Form("form[action*='handler=Password']").Set("Password", password).SubmitAsync();
    }

    /// <summary>Administration › Users › New. Returns the new user (lands on their profile page).</summary>
    public static async Task<NewUser> CreateUserAsync(this Browser admin, string first, bool mustChange = true, int? reportsTo = null,
        string password = TempPassword)
    {
        var tag = E2e.Tag().ToLowerInvariant();
        var username = $"e2e.{first.ToLowerInvariant()}.{tag}";
        var last = "Tester" + tag;
        var form = (await admin.GoAsync("/admin/users/new")).FormOf("#u-username")
            .Set("Input.Username", username).Set("Input.FirstName", first).Set("Input.LastName", last)
            .Set("Input.Email", $"{username}@acme-medical.example").Set("Input.JobTitle", "Validation Specialist").Set("Password", password);
        if (!mustChange) form.Set("MustChange", "false");
        if (reportsTo != null) form.Set("Input.ReportsToId", reportsTo.Value.ToString());
        var page = await form.SubmitAsync();
        if (!page.Url.AbsolutePath.StartsWith("/admin/users/") || page.Url.AbsolutePath.EndsWith("/new"))
            throw new Xunit.Sdk.XunitException($"Creating {username} failed: {page.Alert}\n{page.Excerpt()}");
        Assert.Equal($"Created {first} {last}.", page.Toast);
        return new NewUser(int.Parse(page.Url.AbsolutePath.Split('/').Last()), username, $"{first} {last}", password);
    }

    /// <summary>Sign in as a new user and replace the temporary password on the forced-change page. Lands on the profile.</summary>
    public static async Task<Page> SignInAndSetPasswordAsync(this Browser b, NewUser u, string newPassword = NewPassword)
    {
        var page = await b.SignInAsync(u.Username, u.Password);
        Assert.StartsWith("/account/password", page.Path);
        return await page.FormOf("#new").Set("Current", u.Password).Set("New", newPassword).Set("Confirm", newPassword).SubmitAsync();
    }

    /// <summary>Press a button on an admin user's page (Unlock, Logout…) by handler name.</summary>
    public static async Task<Page> AdminUserActionAsync(this Browser admin, int userId, string handler, Action<FormFill>? fill = null)
    {
        var page = await admin.GoAsync($"/admin/users/{userId}");
        var form = page.Form($"form[action*='handler={handler}']");
        fill?.Invoke(form);
        return await form.SubmitAsync();
    }

    /// <summary>The current TOTP code for a base32 secret, as an authenticator app shows it.</summary>
    public static string Totp(string secret) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

    /// <summary>A code that is certainly not valid now (an authenticator would never show it).</summary>
    public static string WrongTotp(string secret)
    {
        var t = new Totp(Base32Encoding.ToBytes(secret));
        var valid = new[] { -1, 0, 1 }.Select(d => t.ComputeTotp(DateTime.UtcNow.AddSeconds(30 * d))).ToHashSet();
        return Enumerable.Range(0, 1000).Select(i => ((123456 + i * 7919) % 1000000).ToString("D6")).First(c => !valid.Contains(c));
    }

    /// <summary>Open sessions of a user (ids are the cookie values: only for checking that pages never show them).</summary>
    public static async Task<List<string>> OpenSessionIdsAsync(this VellumFixture fx, int userId)
    {
        using var scope = fx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<VellumDbContext>().Sessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null).Select(s => s.Id).ToListAsync();
    }

    /// <summary>
    /// Replay a session cookie from a different client, the way someone who copied it would. Returns the status of a
    /// protected API call: 200 while the session is alive, 401 once it has ended on the server.
    /// </summary>
    public static async Task<HttpStatusCode> ReplaySessionAsync(this VellumFixture fx, string sessionId)
    {
        using var client = fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/notifications");
        req.Headers.Add("Cookie", $"{Vellum.Infrastructure.Auth.SessionDefaults.Cookie}={sessionId}");
        return (await client.SendAsync(req)).StatusCode;
    }

    public static UserInfo Person(this VellumFixture fx, string username) =>
        fx.Services.GetRequiredService<UserDirectory>().Get(username) ?? throw new Xunit.Sdk.XunitException($"No user '{username}'.");

    /// <summary>Whether this browser is still signed in: a protected page answers instead of sending it to /login.</summary>
    public static async Task<bool> IsSignedInAsync(this Browser b)
    {
        var res = await b.RawGetAsync("/api/notifications");
        return res.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>Current system settings (to restore them after a test that changes them).</summary>
    public static async Task<SystemSettings> SettingsAsync(this VellumFixture fx)
    {
        using var scope = fx.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<VellumDbContext>().Settings.AsNoTracking().FirstAsync();
    }

    /// <summary>Put system settings back as they were (the admin pages need a password and reason for some of them).</summary>
    public static async Task RestoreSettingsAsync(this VellumFixture fx, SystemSettings original)
    {
        using var scope = fx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VellumDbContext>();
        var cur = await db.Settings.FirstAsync();
        Vellum.Modules.Admin.SettingsInput.From(original).ApplyTo(cur);
        cur.LoginBlocked = original.LoginBlocked;
        cur.LoginBlockAllowList = original.LoginBlockAllowList;
        cur.LoginBlockMessage = original.LoginBlockMessage;
        await db.SaveChangesAsync();
    }
}
