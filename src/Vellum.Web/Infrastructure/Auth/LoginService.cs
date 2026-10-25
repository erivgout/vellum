using Microsoft.EntityFrameworkCore;
using OtpNet;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Notifications;
using Vellum.Engine.Security;

namespace Vellum.Infrastructure.Auth;

public enum LoginOutcome { Success, Invalid, Locked, Disabled, Blocked, MfaRequired, MfaEnrollRequired, PasswordExpired }

public sealed record LoginResult(LoginOutcome Outcome, User? User = null, string? Message = null);

/// <summary>
/// Local password authentication (SEC §4.1–4.3). Messages never reveal which part was wrong or whether a username exists
/// (SEC-205, INV-150). Lockout after N consecutive failures notifies the manager (SEC-702).
/// </summary>
public sealed class LoginService(VellumDbContext db, IHttpContextAccessor http, UserDirectory dir, NotificationService notify)
{
    // Used to spend comparable time on unknown usernames (INV-150).
    private static readonly string DummyHash = PasswordHasher.Hash("not-a-real-password-" + Guid.NewGuid());

    public async Task<LoginResult> VerifyPasswordAsync(string username, string password)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username.Trim() && !u.IsSystemIdentity);
        var ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString();
        if (user == null)
        {
            PasswordHasher.Verify(password, DummyHash);
            Log("Login failed", false, null, username, "Unknown username", ip);
            await db.SaveChangesAsync();
            return new LoginResult(LoginOutcome.Invalid, Message: "The username or password isn't right.");
        }

        var ok = PasswordHasher.Verify(password, user.PasswordHash);
        // The account's state is only revealed to someone who knows the password (INV-150).
        if (ok && user.Status != UserStatus.Active)
        {
            Log("Login blocked", false, user.Id, username, $"Account {user.Status.ToString().ToLowerInvariant()}", ip);
            await db.SaveChangesAsync();
            return new LoginResult(user.Status == UserStatus.Disabled ? LoginOutcome.Locked : LoginOutcome.Disabled,
                Message: user.Status == UserStatus.Disabled
                    ? "Your account is locked. Contact your administrator to unlock it."
                    : "Your account is inactive. Contact your administrator.");
        }
        if (!ok)
        {
            user.FailedLoginCount++;
            var locked = user.Status == UserStatus.Active && user.FailedLoginCount >= settings.LockoutThreshold;
            if (locked)
            {
                user.Status = UserStatus.Disabled;
                user.StatusReason = $"Locked after {user.FailedLoginCount} failed sign-in attempts";
                Log("Account locked", false, user.Id, username, user.StatusReason, ip);
                var recipients = user.ReportsToId is { } m ? [m] : dir.All.Where(u => u.IsAdmin).Select(u => u.Id).ToList();
                foreach (var r in recipients)
                    notify.Notify(r, NotificationKind.Escalation, $"{user.DisplayName}'s account was locked", user.StatusReason, url: "/admin/users");
                dir.Invalidate();
            }
            else Log("Login failed", false, user.Id, username, $"Wrong password (attempt {user.FailedLoginCount})", ip);
            await db.SaveChangesAsync();
            return new LoginResult(LoginOutcome.Invalid, Message: "The username or password isn't right.");
        }

        user.FailedLoginCount = 0;
        // SEC-404: while the login block is on, only the allow-listed users can sign in. Checked after the password so the
        // message never helps anyone guess which usernames exist.
        if (!settings.CanSignInDuringBlock(user.Username))
        {
            Log("Login blocked", false, user.Id, username, "Login block (maintenance mode) is on", ip);
            await db.SaveChangesAsync();
            return new LoginResult(LoginOutcome.Blocked, Message: "Only designated users can sign in during maintenance. Try again later.");
        }
        await db.SaveChangesAsync();
        if (user.MfaEnrolled && user.TotpSecret != null) return new LoginResult(LoginOutcome.MfaRequired, user);
        if (settings.MfaRequired && !user.MfaEnrolled) return new LoginResult(LoginOutcome.MfaEnrollRequired, user);
        if (PasswordExpired(user, settings)) return new LoginResult(LoginOutcome.PasswordExpired, user);
        return new LoginResult(LoginOutcome.Success, user);
    }

    /// <summary>SEC-201: the password is older than the expiration period. Checked on every sign-in path, with or without MFA.</summary>
    public static bool PasswordExpired(User user, SystemSettings settings) =>
        settings.PasswordExpiryDays > 0 && user.PasswordChangedAt is { } changed && changed.AddDays(settings.PasswordExpiryDays) < DateTime.UtcNow;

    public static bool VerifyTotp(string secret, string code) =>
        new Totp(Base32Encoding.ToBytes(secret)).VerifyTotp(code.Replace(" ", ""), out _, new VerificationWindow(1, 1));

    public async Task CompleteLoginAsync(User user)
    {
        var tracked = await db.Users.FirstAsync(u => u.Id == user.Id);
        tracked.LastLoginAt = DateTime.UtcNow;
        Log("Login", true, user.Id, user.Username, null, http.HttpContext?.Connection.RemoteIpAddress?.ToString());
        await db.SaveChangesAsync();
    }

    private void Log(string kind, bool success, int? userId, string username, string? detail, string? ip) =>
        db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = kind, Success = success, UserId = userId, Username = username, Ip = ip, Detail = detail });
}
