using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Auth;

namespace Vellum.Modules.Portal;

public enum PortalAccess { None, Invited, InviteExpired, Active, Locked, Disabled }

public sealed record PortalContactVm(string Name, string? Role, string? Email, Guid? ContactRowId, int? UserId, string? Username,
    PortalAccess Access, DateTime? LastLoginAt, DateTime? InvitedAt, DateTime? InviteExpiresAt, bool NotifyRatings);

public sealed record TokenCheck(bool Valid, User? User, AccountToken? Token, string? Error);

/// <summary>
/// Supplier portal accounts (SEC-804, open question 4: named external users). A supplier quality engineer or administrator
/// invites a supplier contact: the invitation creates an external user linked to the supplier profile and emails a one-time,
/// expiring set-password link. The account then uses the normal authentication stack (Argon2id, lockout, sessions, TOTP,
/// login block). Only the SHA-256 of a link's token is stored, and the copy of the email kept in the email log is redacted:
/// the plaintext link exists only in the outbox message that delivers it.
/// </summary>
[ScopedService]
public sealed class PortalAccounts(VellumDbContext db, CurrentUser me, UserDirectory dir, AuditService audit, SessionService sessions, PortalService portal)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(2);

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string WelcomePath(string token) => "/portal/welcome?token=" + Uri.EscapeDataString(token);

    // ------------------------------------------------------------------ contacts list (supplier profile)

    public async Task<List<PortalContactVm>> ContactsAsync(Guid supplierId, IEnumerable<RowBag> contactRows)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.IsExternal && u.ExternalOrgId == supplierId).ToListAsync();
        var ids = users.Select(u => u.Id).ToList();
        var tokens = await db.Set<AccountToken>().AsNoTracking().Where(t => ids.Contains(t.UserId) && t.Purpose == "invite")
            .OrderByDescending(t => t.CreatedAt).ToListAsync();
        var list = new List<PortalContactVm>();
        var matched = new HashSet<int>();
        foreach (var row in contactRows.OrderBy(r => r.Order))
        {
            var email = row.Values.Text("email")?.Trim();
            var u = email == null ? null : users.FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
            if (u != null) matched.Add(u.Id);
            list.Add(Vm(row.Values.Text("contact_name") ?? email ?? "Contact", row.Values.Text("contact_role"), email, row.Id, u, row.Values.Bool("notify_ratings") == true));
        }
        foreach (var u in users.Where(u => !matched.Contains(u.Id)))
            list.Add(Vm(u.DisplayName, u.JobTitle, u.Email, null, u, false));
        return list;

        PortalContactVm Vm(string name, string? role, string? email, Guid? rowId, User? u, bool notifyRatings)
        {
            var invite = u == null ? null : tokens.FirstOrDefault(t => t.UserId == u.Id);
            return new PortalContactVm(name, role, email, rowId, u?.Id, u?.Username, StateOf(u, invite), u?.LastLoginAt, invite?.CreatedAt, invite?.ExpiresAt, notifyRatings);
        }
    }

    public static PortalAccess StateOf(User? u, AccountToken? latestInvite)
    {
        if (u == null) return PortalAccess.None;
        if (u.Status == UserStatus.Inactive) return PortalAccess.Disabled;
        if (u.Status == UserStatus.Disabled) return PortalAccess.Locked;
        if (u.PasswordHash != null) return PortalAccess.Active;
        return latestInvite is { UsedAt: null, RevokedAt: null } t && t.ExpiresAt > DateTime.UtcNow ? PortalAccess.Invited : PortalAccess.InviteExpired;
    }

    // ------------------------------------------------------------------ invite / resend / disable / enable

    /// <summary>Create the external account and email the one-time set-password link. The caller checks the inviter's rights.</summary>
    public async Task<ActionOutcome> InviteAsync(Guid supplierId, string displayName, string email, string? role, string? username = null)
    {
        if (me.IsExternal || !me.IsAuthenticated) return ActionOutcome.Fail("Only internal users can invite supplier contacts.");
        displayName = displayName.Trim();
        email = email.Trim();
        if (displayName.Length == 0) return ActionOutcome.Fail("Enter the contact's name.");
        if (!IsEmail(email)) return ActionOutcome.Fail($"\"{email}\" isn't a valid email address.");
        username = (username ?? email).Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            var existing = await db.Users.AsNoTracking().FirstAsync(u => u.Email == email);
            return ActionOutcome.Fail(existing.IsExternal && existing.ExternalOrgId == supplierId
                ? $"{existing.DisplayName} already has supplier portal access. Use Resend invitation if they need a new link."
                : $"{email} already belongs to another Vellum account.");
        }
        if (await db.Users.AnyAsync(u => u.Username == username)) return ActionOutcome.Fail($"The username {username} is already taken.");

        var supplierName = await portal.SupplierNameAsync(supplierId);
        var parts = displayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = username, DisplayName = displayName, FirstName = parts[0], LastName = parts.Length > 1 ? parts[1] : "",
            Email = email, JobTitle = string.IsNullOrWhiteSpace(role) ? null : role.Trim(), Department = supplierName,
            IsExternal = true, ExternalOrgId = supplierId, License = LicenseType.External, IsAdministrator = false,
            Status = UserStatus.Active, PasswordHash = null, CreatedAt = now, TimeZone = "UTC",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        audit.Security("Account created", true, user.Id, user.Username, $"Supplier portal contact for {supplierName}, invited by {me.Name}");
        audit.Record(supplierId, "Portal contact invited", $"{displayName} ({email}) was invited to the supplier portal");
        audit.Config("User", user.Username, "Created", null, JsonSerializer.Serialize(new { user.Username, user.DisplayName, user.Email, external = true, supplier = supplierName }));
        await db.SaveChangesAsync();
        dir.Invalidate();
        await SendLinkAsync(user, "invite", supplierName);
        return ActionOutcome.Ok($"Invitation sent to {displayName} ({email}). The link works once and expires in {InviteLifetime.Days} days.");
    }

    /// <summary>A new one-time link: an invitation when the contact never set a password, otherwise a password reset link.</summary>
    public async Task<ActionOutcome> ResendAsync(Guid supplierId, int userId)
    {
        if (me.IsExternal || !me.IsAuthenticated) return ActionOutcome.Fail("Only internal users can do this.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsExternal && u.ExternalOrgId == supplierId);
        if (user == null) return ActionOutcome.Fail("Choose one of this supplier's portal contacts.");
        if (user.Status == UserStatus.Inactive) return ActionOutcome.Fail($"{user.DisplayName}'s portal access is disabled. Enable it first.");
        var purpose = user.PasswordHash == null ? "invite" : "reset";
        await SendLinkAsync(user, purpose, await portal.SupplierNameAsync(supplierId));
        audit.Record(supplierId, purpose == "invite" ? "Portal invitation resent" : "Portal password reset sent", $"New one-time link emailed to {user.DisplayName} ({user.Email})");
        await db.SaveChangesAsync();
        return ActionOutcome.Ok(purpose == "invite" ? $"A new invitation was sent to {user.Email}. Earlier links no longer work."
            : $"A password reset link was sent to {user.Email}. It works once and expires in {ResetLifetime.TotalHours:0} hours.");
    }

    public async Task<ActionOutcome> DisableAsync(Guid supplierId, int userId, string reason)
    {
        if (me.IsExternal || !me.IsAuthenticated) return ActionOutcome.Fail("Only internal users can do this.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsExternal && u.ExternalOrgId == supplierId);
        if (user == null) return ActionOutcome.Fail("Choose one of this supplier's portal contacts.");
        if (user.Status == UserStatus.Inactive) return ActionOutcome.Fail($"{user.DisplayName}'s portal access is already disabled.");
        user.Status = UserStatus.Inactive;
        user.StatusReason = $"Supplier portal access disabled by {me.Name}: {reason}";
        await RevokeTokensAsync(user.Id);
        audit.Security("Portal access disabled", true, user.Id, user.Username, $"By {me.Name}: {reason}");
        audit.Record(supplierId, "Portal access disabled", $"{user.DisplayName} ({user.Email}) can no longer sign in to the supplier portal", reason: reason);
        await db.SaveChangesAsync();
        await sessions.RevokeAllAsync(user.Id, "Portal access disabled"); // SEC-405: signed out at once
        dir.Invalidate();
        return ActionOutcome.Ok($"{user.DisplayName} can no longer sign in to the supplier portal.");
    }

    public async Task<ActionOutcome> EnableAsync(Guid supplierId, int userId)
    {
        if (me.IsExternal || !me.IsAuthenticated) return ActionOutcome.Fail("Only internal users can do this.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsExternal && u.ExternalOrgId == supplierId);
        if (user == null) return ActionOutcome.Fail("Choose one of this supplier's portal contacts.");
        if (user.Status == UserStatus.Active) return ActionOutcome.Fail($"{user.DisplayName} already has portal access.");
        user.Status = UserStatus.Active;
        user.StatusReason = null;
        user.FailedLoginCount = 0;
        audit.Security("Portal access enabled", true, user.Id, user.Username, $"By {me.Name}");
        audit.Record(supplierId, "Portal access enabled", $"{user.DisplayName} ({user.Email}) can sign in to the supplier portal again");
        await db.SaveChangesAsync();
        dir.Invalidate();
        return ActionOutcome.Ok(user.PasswordHash == null ? $"{user.DisplayName}'s access is enabled. Resend the invitation so they can set a password."
            : $"{user.DisplayName} can sign in to the supplier portal again.");
    }

    private async Task RevokeTokensAsync(int userId) =>
        await db.Set<AccountToken>().Where(t => t.UserId == userId && t.UsedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));

    private async Task SendLinkAsync(User user, string purpose, string supplierName)
    {
        await RevokeTokensAsync(user.Id); // only the newest link ever works
        var token = NewToken();
        var now = DateTime.UtcNow;
        var expires = now + (purpose == "invite" ? InviteLifetime : ResetLifetime);
        db.Set<AccountToken>().Add(new AccountToken
        {
            UserId = user.Id, Purpose = purpose, TokenHash = HashToken(token), CreatedAt = now, ExpiresAt = expires, CreatedById = me.Id,
        });
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var prefix = settings.EnvironmentType == EnvironmentType.Production ? "" : $"[{settings.EnvironmentName.ToUpperInvariant()}] ";
        var org = settings.OrganizationName;
        var link = portal.AbsoluteUrl(WelcomePath(token));
        var subject = prefix + (purpose == "invite" ? $"{org} invites you to its supplier portal" : $"Reset your {org} supplier portal password");
        string Body(string url) => purpose == "invite"
            ? $"Hello {user.FirstName},\n\n{me.Name} at {org} has invited you to the {org} supplier portal for {supplierName}. " +
              "There you can answer corrective action requests, upload certificates and other documents, complete questionnaires, respond to audit findings and see your scorecards.\n\n" +
              $"Set your password to activate your account. This link works once and expires on {expires:dd MMM yyyy HH:mm} UTC:\n{url}\n\n" +
              $"Your username: {user.Username}\n\nIf you weren't expecting this invitation, you can ignore this email."
            : $"Hello {user.FirstName},\n\n{me.Name} at {org} sent you a link to set a new password for the supplier portal. " +
              $"It works once and expires on {expires:dd MMM yyyy HH:mm} UTC:\n{url}\n\nYour username: {user.Username}";
        // The email log is visible to administrators: it keeps a redacted copy. Only the outbox message carries the link.
        db.EmailLog.Add(new EmailLogEntry { RecordId = user.ExternalOrgId, To = user.Email, Subject = subject, Body = Body(portal.AbsoluteUrl("/portal/welcome?token=[one-time link, not stored]")), At = now, Status = "Queued" });
        db.Outbox.Add(new OutboxMessage
        {
            Type = "email", RecordId = user.ExternalOrgId, CreatedAt = now,
            Payload = JsonSerializer.Serialize(new { to = user.Email, subject, body = Body(link), url = (string?)null }),
        });
        audit.Security(purpose == "invite" ? "Portal invitation sent" : "Password reset link sent", true, user.Id, user.Username,
            $"One-time link expiring {expires:yyyy-MM-dd HH:mm} UTC, sent by {me.Name}");
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ the one-time link (anonymous)

    public async Task<TokenCheck> CheckAsync(string? token)
    {
        const string bad = "This link has expired or has already been used. Ask your contact to send you a new one.";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return new(false, null, null, bad);
        var hash = HashToken(token);
        var t = await db.Set<AccountToken>().Include(x => x.User).FirstOrDefaultAsync(x => x.TokenHash == hash);
        if (t == null || t.UsedAt != null || t.RevokedAt != null || t.ExpiresAt <= DateTime.UtcNow) return new(false, null, null, bad);
        if (!t.User.IsExternal || t.User.IsSystemIdentity || t.User.Status == UserStatus.Inactive) return new(false, null, null, bad);
        return new(true, t.User, t, null);
    }

    /// <summary>Set the password from a one-time link. The password policy applies (SEC-201); the link is spent on success.</summary>
    public async Task<(bool Ok, List<string> Errors, User? User)> SetPasswordAsync(string? token, string? password, string? confirm, string? ip)
    {
        var check = await CheckAsync(token);
        if (!check.Valid) return (false, [check.Error!], null);
        var user = check.User!;
        var errors = new List<string>();
        if (password != confirm) errors.Add("The passwords don't match.");
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var history = await db.PasswordHistory.Where(h => h.UserId == user.Id).OrderByDescending(h => h.CreatedAt).Take(settings.PasswordHistoryCount).Select(h => h.Hash).ToListAsync();
        if (errors.Count == 0) errors.AddRange(PasswordPolicy.Validate(password ?? "", settings, user, history));
        if (errors.Count > 0) return (false, errors, user);

        var now = DateTime.UtcNow;
        var hash = PasswordHasher.Hash(password!);
        var first = user.PasswordHash == null;
        user.PasswordHash = hash;
        user.PasswordChangedAt = now;
        user.MustChangePassword = false;
        check.Token!.UsedAt = now;
        db.PasswordHistory.Add(new PasswordHistory { UserId = user.Id, Hash = hash, CreatedAt = now });
        db.SecurityEvents.Add(new SecurityEvent
        {
            At = now, Kind = first ? "Password set (portal invitation)" : "Password reset (emailed link)", Success = true, UserId = user.Id, Username = user.Username, Ip = ip,
        });
        await db.SaveChangesAsync();
        await RevokeTokensAsync(user.Id);
        await sessions.RevokeAllAsync(user.Id, first ? "Account activated" : "Password reset");
        dir.Invalidate();
        return (true, [], user);
    }

    private static bool IsEmail(string s) =>
        s.Length is > 3 and <= 254 && !s.Contains(' ') && s.IndexOf('@') is > 0 and var at && at < s.Length - 3 && s[(at + 1)..].Contains('.');
}
