using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Infrastructure.Auth;

namespace Vellum.Areas.Portal.Pages.Account;

/// <summary>The supplier contact's own profile, time zone, two-step verification (SEC §4.3, optional TOTP) and sessions.</summary>
public class IndexModel(VellumDbContext db, CurrentUser me, UserDirectory dir) : PageModel
{
    public User Me { get; private set; } = null!;
    public SystemSettings Settings { get; private set; } = null!;
    public List<UserSession> Sessions { get; private set; } = [];
    public string? PendingSecret { get; private set; }
    public string? Error { get; private set; }

    public static readonly string[] TimeZones = global::Vellum.Pages.Account.ProfileModel.TimeZones;

    private async Task LoadAsync()
    {
        Me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.Id);
        Settings = await db.Settings.AsNoTracking().FirstAsync();
        Sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == me.Id && s.RevokedAt == null).OrderByDescending(s => s.LastSeenAt).ToListAsync();
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostPreferencesAsync(string timeZone)
    {
        var u = await db.Users.FirstAsync(x => x.Id == me.Id);
        if (TimeZones.Contains(timeZone)) u.TimeZone = timeZone;
        await db.SaveChangesAsync();
        dir.Invalidate();
        TempData["Toast"] = "Preferences saved.";
        return Redirect("/portal/account");
    }

    public async Task<IActionResult> OnPostMfaStartAsync()
    {
        await LoadAsync();
        PendingSecret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "MFA enrollment started", Success = true, UserId = me.Id, Username = Me.Username, Ip = me.Ip });
        await db.SaveChangesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostMfaConfirmAsync(string secret, string code)
    {
        await LoadAsync();
        if (string.IsNullOrEmpty(secret) || !LoginService.VerifyTotp(secret, code ?? ""))
        {
            PendingSecret = secret;
            Error = "That code didn't match. Check your device's clock and try the newest code.";
            await Task.Delay(800); // SEC-306
            return Page();
        }
        var u = await db.Users.FirstAsync(x => x.Id == me.Id);
        u.TotpSecret = secret;
        u.MfaEnrolled = true;
        db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "MFA enrollment completed", Success = true, UserId = u.Id, Username = u.Username, Ip = me.Ip });
        await db.SaveChangesAsync();
        TempData["Toast"] = "Two-step verification is on. You'll enter a code each time you sign in.";
        return Redirect("/portal/account");
    }

    public async Task<IActionResult> OnPostEndSessionAsync(string id)
    {
        await db.Sessions.Where(s => s.Id == id && s.UserId == me.Id && s.Id != me.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow).SetProperty(x => x.RevokedReason, "Ended by user"));
        TempData["Toast"] = "Session ended.";
        return Redirect("/portal/account");
    }

    public string OtpUri(string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(Settings.OrganizationName)}:{Uri.EscapeDataString(Me.Username)}?secret={secret}&issuer={Uri.EscapeDataString(Settings.OrganizationName + " supplier portal")}&digits=6&period=30";

    public bool IsCurrent(UserSession s) => s.Id == me.SessionId;
}
