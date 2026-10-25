using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure.Auth;

namespace Vellum.Pages.Account;

/// <summary>PLT-1404 preferences, SEC-612 e-signature certification, SEC §4.3 TOTP enrollment, active sessions.</summary>
public class ProfileModel(VellumDbContext db, CurrentUser me, UserDirectory dir, AuditService audit, SessionService sessions) : PageModel
{
    public User Me { get; private set; } = null!;
    public SystemSettings Settings { get; private set; } = null!;
    public List<UserSession> Sessions { get; private set; } = [];
    public string? Location { get; private set; }
    public string? Manager { get; private set; }
    public List<string> Groups { get; private set; } = [];
    public string? PendingSecret { get; private set; }
    public string? Error { get; private set; }

    public static readonly string[] TimeZones =
        ["UTC", "America/New_York", "America/Detroit", "America/Chicago", "America/Denver", "America/Los_Angeles", "Europe/London", "Europe/Dublin",
         "Europe/Berlin", "Europe/Paris", "Asia/Singapore", "Asia/Tokyo", "Australia/Sydney"];

    private async Task LoadAsync()
    {
        Me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.Id);
        Settings = await db.Settings.AsNoTracking().FirstAsync();
        Sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == me.Id && s.RevokedAt == null).OrderByDescending(s => s.LastSeenAt).ToListAsync();
        Location = Me.PrimaryLocationId is { } l ? await db.Locations.Where(x => x.Id == l).Select(x => x.Path).FirstOrDefaultAsync() : null;
        Manager = Me.ReportsToId is { } m ? dir.Name(m) : null;
        Groups = dir.GroupsOf(me.Id).Select(g => dir.Group(g)?.Name).OfType<string>().Order().ToList();
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostPreferencesAsync(string timeZone, string theme, string? defaultApp)
    {
        var u = await db.Users.FirstAsync(x => x.Id == me.Id);
        if (TimeZones.Contains(timeZone)) u.TimeZone = timeZone;
        u.Theme = theme is "light" or "dark" ? theme : "auto";
        u.DefaultAppKey = string.IsNullOrEmpty(defaultApp) ? null : defaultApp;
        await db.SaveChangesAsync();
        dir.Invalidate();
        TempData["Toast"] = "Preferences saved.";
        return Redirect("/account/profile");
    }

    public async Task<IActionResult> OnPostCertifyAsync(bool agree)
    {
        if (!agree) { TempData["Toast"] = "Tick the box to acknowledge the certification."; TempData["ToastKind"] = "bad"; return Redirect("/account/profile"); }
        var u = await db.Users.FirstAsync(x => x.Id == me.Id);
        if (u.ESignatureCertifiedAt == null)
        {
            u.ESignatureCertifiedAt = DateTime.UtcNow;
            audit.Global("E-signature certification", $"{u.DisplayName} acknowledged the electronic signature certification (21 CFR 11.100).");
            await db.SaveChangesAsync();
        }
        TempData["Toast"] = "Certification recorded.";
        return Redirect("/account/profile");
    }

    public async Task<IActionResult> OnPostMfaStartAsync()
    {
        await LoadAsync();
        PendingSecret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        return Page();
    }

    public async Task<IActionResult> OnPostMfaConfirmAsync(string secret, string code)
    {
        await LoadAsync();
        if (!LoginService.VerifyTotp(secret, code ?? ""))
        {
            PendingSecret = secret;
            Error = "That code didn't match. Make sure your device's clock is correct and try the newest code.";
            return Page();
        }
        var u = await db.Users.FirstAsync(x => x.Id == me.Id);
        u.TotpSecret = secret;
        u.MfaEnrolled = true;
        db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "MFA enrollment completed", Success = true, UserId = u.Id, Username = u.Username });
        await db.SaveChangesAsync();
        TempData["Toast"] = "Two-step verification is on.";
        return Redirect("/account/profile");
    }

    public async Task<IActionResult> OnPostEndSessionAsync(string id)
    {
        await db.Sessions.Where(s => s.Id == id && s.UserId == me.Id && s.Id != me.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow).SetProperty(x => x.RevokedReason, "Ended by user"));
        TempData["Toast"] = "Session ended.";
        return Redirect("/account/profile");
    }

    public string OtpUri(string secret) =>
        $"otpauth://totp/Vellum:{Uri.EscapeDataString(Me.Username)}?secret={secret}&issuer=Vellum&digits=6&period=30";
}
