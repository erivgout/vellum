using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Infrastructure.Auth;

namespace Vellum.Pages.Account;

/// <summary>Identifier-first sign-in (SEC-101) with password, optional TOTP (SEC-301) and deep-link return (SEC-106).</summary>
public class LoginModel(LoginService login, SessionService sessions, VellumDbContext db, IDataProtectionProvider protection) : PageModel
{
    private IDataProtector Protector => protection.CreateProtector("vellum.login.mfa");

    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? Ticket { get; set; }

    public string Step { get; private set; } = "identify";
    public string? Error { get; private set; }
    public string? Info { get; private set; }
    public SystemSettings Settings { get; private set; } = null!;
    public bool ShowDemo => Settings.EnvironmentType != EnvironmentType.Production;

    private async Task LoadAsync() => Settings = await db.Settings.AsNoTracking().FirstAsync();

    public async Task<IActionResult> OnGetAsync(bool expired = false, bool signedout = false, string? u = null)
    {
        await LoadAsync();
        if (User.Identity?.IsAuthenticated == true && !expired) return LocalRedirect(SafeReturn());
        if (expired) Info = "Your session ended. Sign in again to continue.";
        if (signedout) Info = "You've signed out.";
        if (!string.IsNullOrWhiteSpace(u)) { Username = u; Step = "password"; }
        return Page();
    }

    public async Task<IActionResult> OnPostIdentifyAsync()
    {
        await LoadAsync();
        if (string.IsNullOrWhiteSpace(Username)) { Error = "Enter your username."; return Page(); }
        // Unknown and known usernames get the same next step (INV-150).
        Step = "password";
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        await LoadAsync();
        Step = "password";
        if (string.IsNullOrWhiteSpace(Username)) { Step = "identify"; return Page(); }
        if (string.IsNullOrEmpty(Password)) { Error = "Enter your password."; return Page(); }
        var result = await login.VerifyPasswordAsync(Username, Password);
        switch (result.Outcome)
        {
            case LoginOutcome.Success:
            case LoginOutcome.MfaEnrollRequired:
                return await SignInAsync(result.User!);
            case LoginOutcome.PasswordExpired:
                await db.Users.Where(x => x.Id == result.User!.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MustChangePassword, true));
                return await SignInAsync(result.User!);
            case LoginOutcome.MfaRequired:
                Step = "mfa";
                Ticket = Protector.Protect($"{result.User!.Id}|{DateTime.UtcNow.AddMinutes(5):O}");
                return Page();
            default:
                Error = result.Message;
                return Page();
        }
    }

    public async Task<IActionResult> OnPostMfaAsync()
    {
        await LoadAsync();
        Step = "mfa";
        try
        {
            var parts = Protector.Unprotect(Ticket ?? "").Split('|');
            if (DateTime.Parse(parts[1]).ToUniversalTime() < DateTime.UtcNow) { Step = "identify"; Error = "That took too long. Sign in again."; return Page(); }
            var user = await db.Users.FirstAsync(x => x.Id == int.Parse(parts[0]));
            if (user.TotpSecret != null && Code != null && LoginService.VerifyTotp(user.TotpSecret, Code))
                return await SignInAsync(user);
            db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "MFA verification failed", Success = false, UserId = user.Id, Username = user.Username });
            await db.SaveChangesAsync();
            await Task.Delay(800); // SEC-306: throttle
            Error = "That code isn't right. Check your authenticator app and try again.";
            return Page();
        }
        catch (Exception) when (Ticket != null)
        {
            Step = "identify";
            Error = "Sign in again.";
            return Page();
        }
    }

    private async Task<IActionResult> SignInAsync(User user)
    {
        await sessions.StartAsync(user);
        await login.CompleteLoginAsync(user);
        return LocalRedirect(SafeReturn());
    }

    private string SafeReturn() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && !ReturnUrl.StartsWith("/login") ? ReturnUrl : "/";
}
