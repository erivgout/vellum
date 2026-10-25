using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Security;

namespace Vellum.Tests.E2e;

/// <summary>Shared helpers for end-to-end tests.</summary>
public static class E2e
{
    /// <summary>A browser signed in as the given demo user.</summary>
    public static async Task<Browser> SignedInAsync(this VellumFixture fx, string username)
    {
        var b = new Browser(fx);
        await b.SignInAsync(username);
        return b;
    }

    /// <summary>A demo user's id (what the people picker posts).</summary>
    public static int Uid(this VellumFixture fx, string username) =>
        fx.Services.GetRequiredService<UserDirectory>().Get(username)?.Id ?? throw new Xunit.Sdk.XunitException($"No user '{username}'.");

    public static string[] Uids(this VellumFixture fx, params string[] usernames) => usernames.Select(u => fx.Uid(u).ToString()).ToArray();

    /// <summary>A small valid PDF.</summary>
    public static byte[] Pdf(string title = "Controlled content") =>
        Vellum.Modules.Pdf.PdfLayout.Render(new Vellum.Modules.Pdf.PdfReport { Title = title }, "Acme");

    /// <summary>A unique suffix so records created by different tests don't collide in searches and views.</summary>
    public static string Tag() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
}
