using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Infrastructure.Auth;

namespace Vellum.Pages.Account;

/// <summary>SEC-201..205: change password under the always-on policy; the session is rotated afterwards (SEC-402).</summary>
public class PasswordModel(VellumDbContext db, CurrentUser me, SessionService sessions, UserDirectory dir) : PageModel
{
    [BindProperty] public string? Current { get; set; }
    [BindProperty] public string? New { get; set; }
    [BindProperty] public string? Confirm { get; set; }
    [BindProperty(SupportsGet = true)] public bool Forced { get; set; }
    public List<string> Errors { get; } = [];
    public SystemSettings Settings { get; private set; } = null!;
    public bool Must { get; private set; }

    public async Task OnGetAsync()
    {
        Settings = await db.Settings.AsNoTracking().FirstAsync();
        Must = await db.Users.Where(u => u.Id == me.Id).Select(u => u.MustChangePassword).FirstAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await OnGetAsync();
        var user = await db.Users.FirstAsync(u => u.Id == me.Id);
        if (!PasswordHasher.Verify(Current ?? "", user.PasswordHash)) Errors.Add("Your current password isn't right.");
        if (New != Confirm) Errors.Add("The new passwords don't match.");
        var history = await db.PasswordHistory.Where(h => h.UserId == user.Id).OrderByDescending(h => h.CreatedAt).Take(Settings.PasswordHistoryCount).Select(h => h.Hash).ToListAsync();
        if (Errors.Count == 0) Errors.AddRange(PasswordPolicy.Validate(New ?? "", Settings, user, history));
        if (Errors.Count > 0) return Page();

        var hash = PasswordHasher.Hash(New!);
        user.PasswordHash = hash;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.MustChangePassword = false;
        db.PasswordHistory.Add(new PasswordHistory { UserId = user.Id, Hash = hash, CreatedAt = DateTime.UtcNow });
        db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "Password changed", Success = true, UserId = user.Id, Username = user.Username, Ip = me.Ip });
        await db.SaveChangesAsync();
        await sessions.RevokeAllAsync(user.Id, "Password changed");
        await sessions.StartAsync(user);
        dir.Invalidate();
        TempData["Toast"] = "Password changed. Other sessions were signed out.";
        return Redirect("/account/profile");
    }
}
