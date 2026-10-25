using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;

namespace Vellum.Engine.Notifications;

/// <summary>
/// In-app notifications plus email (PLT §2.12). Email is written to the record's email log and queued in the
/// transactional outbox (INV-040); the outbox job delivers it. Inactive users are never notified (INV-073).
/// Non-production environments prefix subjects (SAF-012).
/// </summary>
public sealed class NotificationService(VellumDbContext db, UserDirectory dir, CurrentUser me)
{
    private string? _prefix;

    private string Prefix
    {
        get
        {
            if (_prefix != null) return _prefix;
            var s = db.Settings.AsNoTracking().FirstOrDefault();
            _prefix = s == null || s.EnvironmentType == EnvironmentType.Production ? "" : $"[{s.EnvironmentName.ToUpperInvariant()}] ";
            return _prefix;
        }
    }

    public void Notify(int userId, NotificationKind kind, string title, string? body = null, Guid? recordId = null, string? url = null)
    {
        var u = dir.Get(userId);
        if (u == null || u.Status == UserStatus.Inactive || u.IsSystem) return;
        url ??= recordId is { } rid ? $"/records/{rid}" : null;
        // SEC-804: external (supplier portal) users only ever get portal links; internal links would be refused anyway.
        if (u.IsExternal && url != null && !url.StartsWith("/portal", StringComparison.OrdinalIgnoreCase)) url = "/portal";
        db.Notifications.Add(new Notification
        {
            UserId = userId, Kind = kind, Title = title, Body = body, RecordId = recordId, Url = url, CreatedAt = DateTime.UtcNow,
        });
        var subject = Prefix + (kind == NotificationKind.SafetyCritical ? "SAFETY-CRITICAL: " : "") + title;
        db.EmailLog.Add(new EmailLogEntry { RecordId = recordId, To = u.Email, Subject = subject, Body = body, At = DateTime.UtcNow, Status = "Queued" });
        db.Outbox.Add(new OutboxMessage
        {
            Type = "email",
            RecordId = recordId,
            CreatedAt = DateTime.UtcNow,
            Payload = JsonSerializer.Serialize(new { to = u.Email, subject, body, url }),
        });
    }

    public void Assigned(Record r, FormDefinition form, PhaseDefinition phase, IEnumerable<int> userIds, DateOnly? due)
    {
        foreach (var uid in userIds.Distinct())
            Notify(uid, NotificationKind.Assignment, $"{form.Name} {r.Number} assigned to you",
                $"{r.Title} is in {phase.Name}" + (due is { } d ? $", due {d:dd MMM yyyy}." : ".") + $" Assigned by {me.Name}.", r.Id);
    }

    /// <summary>SAF-020: never digested or muted; needs acknowledgment.</summary>
    public void SafetyCritical(IEnumerable<int> userIds, string title, string body, Guid? recordId)
    {
        foreach (var uid in userIds.Distinct()) Notify(uid, NotificationKind.SafetyCritical, title, body, recordId);
    }

    public void ToGroup(string groupKey, NotificationKind kind, string title, string? body, Guid? recordId)
    {
        foreach (var u in dir.ActiveMembers(groupKey)) Notify(u.Id, kind, title, body, recordId);
    }
}
