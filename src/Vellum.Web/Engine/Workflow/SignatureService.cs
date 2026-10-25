using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Engine.Workflow;

public sealed record SignOutcome(bool Ok, string? Error, Signature? Signature);

/// <summary>
/// Electronic signatures (SEC §4.6). Every signature needs a fresh password re-entry by the signer (INV-113),
/// is executed with the signer's own credentials even for delegates (INV-112), and stores the record content hash (INV-114).
/// The system identity can never sign (SEC-609).
/// </summary>
public sealed class SignatureService(VellumDbContext db, CurrentUser me, AuditService audit, UserDirectory dir,
    IServiceScopeFactory scopes)
{
    public async Task<SignOutcome> SignAsync(RecordSnapshot snap, string meaning, string action, string? password, bool certify,
        int? onBehalfOfId, Func<RecordSnapshot, string> hash, string? comment = null)
    {
        if (me.IsSystem || !me.IsAuthenticated) return new(false, "Only a person can sign electronically.", null);
        var user = await db.Users.FirstAsync(u => u.Id == me.Id);
        if (user.IsSystemIdentity) return new(false, "Only a person can sign electronically.", null); // e.g. the anonymous reporter
        if (user.Status != UserStatus.Active) return new(false, "Your account can't sign right now.", null);

        if (user.ESignatureCertifiedAt == null)
        {
            if (!certify) return new(false, "Confirm the electronic signature certification before your first signature.", null);
            user.ESignatureCertifiedAt = DateTime.UtcNow;
            audit.Global("E-signature certification", $"{user.DisplayName} acknowledged the electronic signature certification (21 CFR 11.100).");
        }

        if (string.IsNullOrEmpty(password) || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            await RecordFailureAsync(user.Id, snap.Record.Number);
            return new(false, "That password isn't right. Repeated failures lock your account.", null);
        }
        user.FailedLoginCount = 0;

        var phase = snap.Phase;
        var sig = new Signature
        {
            RecordId = snap.Record.Id,
            SignerId = user.Id,
            SignerName = user.DisplayName,
            SignerUsername = user.Username,
            OnBehalfOfId = onBehalfOfId,
            OnBehalfOfName = onBehalfOfId != null ? dir.Name(onBehalfOfId) : null,
            Meaning = meaning,
            PhaseKey = phase?.Key ?? "",
            PhaseName = phase?.Name ?? "",
            PhaseOrder = phase?.Order ?? 0,
            Action = action,
            SignedAt = DateTime.UtcNow,
            RecordHash = hash(snap),
            RecordVersion = snap.Record.Version,
            Comment = comment,
        };
        db.Signatures.Add(sig);
        audit.Record(snap.Record.Id, "Signed", $"Signed as \"{meaning}\" ({action})", reason: comment, phaseName: phase?.Name,
            recordVersion: snap.Record.Version, onBehalfOfId: onBehalfOfId, newValue: $"SHA-256 {sig.RecordHash}");
        db.Comments.Add(new Comment
        {
            RecordId = snap.Record.Id, UserId = user.Id, IsSystem = true, Kind = "signature", At = sig.SignedAt,
            Text = $"Signed as {meaning}" + (onBehalfOfId != null ? $" on behalf of {sig.OnBehalfOfName}" : "") + $" · {action}",
        });
        return new(true, null, sig);
    }

    /// <summary>SEC-611: failed signing attempts count toward lockout. Recorded in its own scope so a rollback can't erase it.</summary>
    private async Task RecordFailureAsync(int userId, string recordNumber)
    {
        using var scope = scopes.CreateScope();
        var sdb = scope.ServiceProvider.GetRequiredService<VellumDbContext>();
        var settings = await sdb.Settings.FirstAsync();
        var user = await sdb.Users.FirstAsync(u => u.Id == userId);
        user.FailedLoginCount++;
        var locked = user.FailedLoginCount >= settings.LockoutThreshold;
        if (locked)
        {
            user.Status = UserStatus.Disabled;
            user.StatusReason = $"Locked after {user.FailedLoginCount} failed signature attempts";
        }
        sdb.SecurityEvents.Add(new SecurityEvent
        {
            At = DateTime.UtcNow, Kind = locked ? "Account locked" : "Signature failed", Success = false, UserId = userId,
            Username = user.Username, Ip = me.Ip, Detail = $"Failed e-signature on {recordNumber} (attempt {user.FailedLoginCount})",
        });
        if (locked || user.FailedLoginCount >= 3)
        {
            var recipients = new List<int>();
            if (user.ReportsToId is { } mgr) recipients.Add(mgr);
            recipients.AddRange(await sdb.Users.Where(u => u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity).Select(u => u.Id).ToListAsync());
            foreach (var r in recipients.Distinct())
                sdb.Notifications.Add(new Notification
                {
                    UserId = r, Kind = NotificationKind.SafetyCritical, CreatedAt = DateTime.UtcNow,
                    Title = $"Possible signature misuse: {user.DisplayName}",
                    Body = $"{user.FailedLoginCount} failed e-signature attempts on {recordNumber}." + (locked ? " The account has been locked." : ""),
                });
        }
        await sdb.SaveChangesAsync();
        dir.Invalidate();
    }

    /// <summary>INV-114: "valid for current content" iff the current hash equals the signed hash.</summary>
    public static bool IsValidFor(Signature s, string currentHash) => !s.Superseded && s.RecordHash == currentHash;
}
