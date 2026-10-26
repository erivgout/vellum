using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Infrastructure.Auth;

namespace Vellum.Pages.Account;

/// <summary>
/// Identifier-first sign-in (SEC-101) with password, TOTP (SEC-301), enrollment when MFA is required (SEC-302) and deep-link
/// return (SEC-106).
/// </summary>
public class LoginModel(LoginService login, SessionService sessions, VellumDbContext db, IDataProtectionProvider protection) : PageModel
{
    private IDataProtector Protector => protection.CreateProtector("vellum.login.mfa");
    private IDataProtector EnrollProtector => protection.CreateProtector("vellum.login.mfa-enroll");

    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? Ticket { get; set; }

    public string Step { get; private set; } = "identify";
    public string? Error { get; private set; }
    public string? Info { get; private set; }
    public string? EnrollSecret { get; private set; }
    public SystemSettings Settings { get; private set; } = null!;
    public bool ShowDemo => Settings.EnvironmentType != EnvironmentType.Production;

    private async Task LoadAsync() => Settings = await db.Settings.AsNoTracking().FirstAsync();

    // The sign-out redirect and site.js send "?signedout=1" and "?expired=1", which don't bind to bool.
    public async Task<IActionResult> OnGetAsync(string? expired = null, string? signedout = null, string? u = null)
    {
        await LoadAsync();
        var isExpired = Flag(expired);
        if (User.Identity?.IsAuthenticated == true && !isExpired) return LocalRedirect(SafeReturn());
        if (isExpired) Info = "Your session ended. Sign in again to continue.";
        if (Flag(signedout)) Info = "You've signed out.";
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
            case LoginOutcome.PasswordExpired:
                return await SignInAsync(result.User!);
            case LoginOutcome.MfaEnrollRequired:
                return await StartEnrollmentAsync(result.User!);
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

    /// <summary>SEC-302: MFA is required and this person has no factor yet, so they register one before any session exists.</summary>
    private async Task<IActionResult> StartEnrollmentAsync(User user)
    {
        Step = "enroll";
        EnrollSecret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        Ticket = EnrollProtector.Protect($"{user.Id}|{DateTime.UtcNow.AddMinutes(10):O}|{EnrollSecret}");
        db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "MFA enrollment started", Success = true, UserId = user.Id, Username = user.Username, Ip = Ip });
        await db.SaveChangesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostEnrollAsync()
    {
        await LoadAsync();
        string[] parts;
        try { parts = EnrollProtector.Unprotect(Ticket ?? "").Split('|'); }
        catch (System.Security.Cryptography.CryptographicException) { parts = []; }
        if (parts.Length != 3 || DateTime.Parse(parts[1]).ToUniversalTime() < DateTime.UtcNow)
        {
            Error = parts.Length == 3 ? "That took too long. Sign in again." : "Sign in again.";
            return Page();
        }
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == int.Parse(parts[0]) && x.Status == UserStatus.Active);
        if (user == null) { Error = "Sign in again."; return Page(); }
        Step = "enroll";
        EnrollSecret = parts[2];
        if (Code == null || !LoginService.VerifyTotp(EnrollSecret, Code))
        {
            Error = "That code didn't match. Make sure your device's clock is correct and try the newest code.";
            return Page();
        }
        user.TotpSecret = EnrollSecret;
        user.MfaEnrolled = true;
        db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "MFA enrollment completed", Success = true, UserId = user.Id, Username = user.Username, Ip = Ip });
        await db.SaveChangesAsync();
        return await SignInAsync(user);
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static bool Flag(string? value) => value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public string OtpUri(string secret) =>
        $"otpauth://totp/Vellum:{Uri.EscapeDataString(Username ?? "")}?secret={secret}&issuer=Vellum&digits=6&period=30";

    private async Task<IActionResult> SignInAsync(User user)
    {
        // SEC-201: an expired password has to be changed first, however the person signed in (password, TOTP or enrollment).
        if (LoginService.PasswordExpired(user, Settings) && !user.MustChangePassword)
            await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MustChangePassword, true));
        await sessions.StartAsync(user);
        await login.CompleteLoginAsync(user);
        return LocalRedirect(SafeReturn());
    }

    private string SafeReturn() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && !ReturnUrl.StartsWith("/login") ? ReturnUrl : "/";
}
