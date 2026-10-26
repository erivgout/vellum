using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;

namespace Vellum.Engine.Audit;

/// <summary>
/// Appends system audit trail entries (PLT-1001) to the current unit of work. They commit (and are hash-chained)
/// in the same transaction as the change they describe (INV-050).
/// </summary>
public sealed class AuditService(VellumDbContext db, CurrentUser me, UserDirectory dir)
{
    /// <summary>
    /// SEC-520: show the old and new values of sensitive fields as "Restricted" to someone who can't see those fields. Only the
    /// loaded (untracked) entries are changed for display or export; the stored, hash-chained trail stays as it is.
    /// </summary>
    public static List<AuditEntry> MaskSensitive(List<AuditEntry> entries, FormDefinition form)
    {
        var keys = form.Fields.Where(f => f.Sensitive).SelectMany(f => new[] { f.Key, $"{f.SectionKey}.{f.Key}" }).ToHashSet();
        foreach (var a in entries.Where(a => a.FieldKey != null && keys.Contains(a.FieldKey)))
        {
            if (!string.IsNullOrEmpty(a.OldValue)) a.OldValue = "Restricted";
            if (!string.IsNullOrEmpty(a.NewValue)) a.NewValue = "Restricted";
        }
        return entries;
    }

    public AuditEntry Record(Guid? recordId, string action, string? detail = null, string? reason = null,
        string? fieldKey = null, string? fieldLabel = null, string? oldValue = null, string? newValue = null,
        string? phaseName = null, int? recordVersion = null, int? onBehalfOfId = null)
    {
        var entry = new AuditEntry
        {
            RecordId = recordId,
            At = DateTime.UtcNow,
            UserId = me.IsAuthenticated ? me.Id : null,
            UserName = me.IsAuthenticated ? me.Name : "Anonymous",
            ActorKind = me.IsSystem ? ActorKind.System : onBehalfOfId != null ? ActorKind.Delegate : ActorKind.User,
            OnBehalfOfId = onBehalfOfId,
            OnBehalfOfName = onBehalfOfId != null ? dir.Name(onBehalfOfId) : null,
            Source = me.IsSystem ? "System" : me.Source,
            Action = action,
            FieldKey = fieldKey,
            FieldLabel = fieldLabel,
            OldValue = oldValue,
            NewValue = newValue,
            Detail = detail,
            Reason = reason,
            PhaseName = phaseName,
            RecordVersion = recordVersion,
            Ip = me.Ip,
        };
        db.Audit.Add(entry);
        return entry;
    }

    /// <summary>System-wide (non-record) event, e.g. configuration publish or administrative action.</summary>
    public AuditEntry Global(string action, string? detail = null, string? reason = null) => Record(null, action, detail, reason);

    /// <summary>DSN-1202: configuration audit entry with a before/after diff.</summary>
    public void Config(string objectType, string objectKey, string action, string? before, string? after, string? reason = null)
    {
        db.ConfigAudit.Add(new ConfigAuditEntry
        {
            At = DateTime.UtcNow,
            UserId = me.IsAuthenticated ? me.Id : null,
            UserName = me.Name,
            ObjectType = objectType,
            ObjectKey = objectKey,
            Action = action,
            Before = before,
            After = after,
            Reason = reason,
        });
    }

    public void Security(string kind, bool success, int? userId, string? username, string? detail = null) =>
        db.SecurityEvents.Add(new SecurityEvent
        {
            At = DateTime.UtcNow, Kind = kind, Success = success, UserId = userId, Username = username, Ip = me.Ip, Detail = detail,
        });
}
