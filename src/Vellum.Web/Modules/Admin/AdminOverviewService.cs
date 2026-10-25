using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Admin;

public sealed record AdminStats(
    int UsersActive, int UsersDisabled, int UsersInactive, int UsersLocked,
    int Groups, int GroupsInactive, int Locations, int LocationsDisabled,
    int Sessions, int SignedInUsers, int FailedLogins24h, int TaskFailures, int TasksTotal,
    SecurityEvent? LastIntegrity, int PostureWarnings, int PostureIssues);

public sealed record PostureItem(string Area, string Label, string Value, PostureStatus Status, string Reference, string Effect, string? Href = null);

/// <summary>Counts for the Administration Center home (ADM-1201) and the compliance posture report (SAF-011).</summary>
[ScopedService]
public sealed class AdminOverviewService(VellumDbContext db, AccessAdminService access)
{
    public static readonly string[] FailedLoginKinds = ["Login failed", "Login blocked", "Account locked"];
    public static readonly string[] Triggers = ["trg_Audit_AppendOnly", "trg_ConfigAudit_AppendOnly", "trg_Signatures_Immutable"];

    public async Task<AdminStats> StatsAsync()
    {
        var now = DateTime.UtcNow;
        var users = await db.Users.AsNoTracking().Where(u => !u.IsSystemIdentity)
            .GroupBy(u => u.Status).Select(g => new { g.Key, Count = g.Count(), Locked = g.Count(u => u.StatusReason != null && u.StatusReason.StartsWith("Locked")) }).ToListAsync();
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var idleCut = now.AddMinutes(-settings.SessionIdleMinutes);
        var sessions = await db.Sessions.AsNoTracking().Where(s => s.RevokedAt == null && s.AbsoluteExpiresAt > now && s.LastSeenAt >= idleCut)
            .Select(s => s.UserId).ToListAsync();
        var since = now.AddHours(-24);
        var failed = await db.SecurityEvents.CountAsync(e => !e.Success && e.At >= since && FailedLoginKinds.Contains(e.Kind));
        var tasks = await db.Tasks.AsNoTracking().ToListAsync();
        var integrity = await db.SecurityEvents.AsNoTracking().Where(e => e.Kind == "Integrity check").OrderByDescending(e => e.Id).FirstOrDefaultAsync();
        var posture = await PostureAsync();
        int Count(UserStatus s) => users.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        return new AdminStats(
            Count(UserStatus.Active), Count(UserStatus.Disabled), Count(UserStatus.Inactive), users.Sum(x => x.Locked),
            await db.Groups.CountAsync(g => !g.Inactive), await db.Groups.CountAsync(g => g.Inactive),
            await db.Locations.CountAsync(l => !l.Disabled), await db.Locations.CountAsync(l => l.Disabled),
            sessions.Count, sessions.Distinct().Count(), failed,
            tasks.Count(t => t.Enabled && t.LastOutcome == "Failed"), tasks.Count, integrity,
            posture.Count(p => p.Status.Level == "warn"), posture.Count(p => p.Status.Level == "bad"));
    }

    /// <summary>SAF-011: every compliance-sensitive setting plus the platform controls that back them.</summary>
    public async Task<List<PostureItem>> PostureAsync()
    {
        var s = await db.Settings.AsNoTracking().FirstAsync();
        var items = ComplianceCatalog.Settings
            .Select(x => new PostureItem("Settings", x.Label, x.Display(s), x.Evaluate(s), x.Reference, x.Effect, "/admin/settings#" + x.Key))
            .ToList();

        var triggers = await db.Database.SqlQueryRaw<string>(
            "SELECT [name] AS [Value] FROM sys.triggers WHERE [name] IN ('trg_Audit_AppendOnly', 'trg_ConfigAudit_AppendOnly', 'trg_Signatures_Immutable')").ToListAsync();
        var missing = Triggers.Except(triggers).ToList();
        items.Add(new PostureItem("Controls", "Tamper protection", missing.Count == 0 ? "Database guards installed" : $"Missing: {string.Join(", ", missing)}",
            missing.Count == 0 ? PostureStatus.Ok("Audit trails are append-only and signatures immutable at the database level.") : PostureStatus.Bad("A database guard is missing. Restart the application to reinstall it."),
            "INV-050, INV-052, INV-145", "Updates and deletes of audit entries and signatures are refused by the database itself."));

        var integrity = await db.SecurityEvents.AsNoTracking().Where(e => e.Kind == "Integrity check").OrderByDescending(e => e.Id).FirstOrDefaultAsync();
        items.Add(new PostureItem("Controls", "Data integrity check", integrity == null ? "Never run" : $"{(integrity.Success ? "Passed" : "Failed")} {integrity.At:dd MMM yyyy HH:mm} UTC",
            integrity == null ? PostureStatus.Warn("Run the integrity check to verify the hash chains.")
                : integrity.Success ? (integrity.At < DateTime.UtcNow.AddDays(-2) ? PostureStatus.Warn("Last check is more than 2 days old.") : PostureStatus.Ok(integrity.Detail ?? "All chains verified."))
                : PostureStatus.Bad(integrity.Detail ?? "Discrepancies found."),
            "SAF-066, INV-055", "Verifies audit-trail hash chains, signature links and file checksums.", "/admin/audit"));

        var tasks = await db.Tasks.AsNoTracking().Where(t => t.Key == "integrity-check" || t.Key == "dormant-accounts").ToListAsync();
        var dormant = tasks.FirstOrDefault(t => t.Key == "dormant-accounts");
        items.Add(new PostureItem("Controls", "Dormant account check", dormant == null ? "Not scheduled yet" : dormant.Enabled ? AdminText.Interval(dormant.IntervalMinutes) : "Off",
            dormant is { Enabled: true } ? PostureStatus.Ok("Accounts without a sign-in for 90 days are disabled.") : PostureStatus.Warn("Dormant accounts stay enabled."),
            "SEC-703", "Disables accounts nobody uses, so they can't be taken over unnoticed.", "/admin/tasks/dormant-accounts"));

        var active = await db.Users.AsNoTracking().Where(u => !u.IsSystemIdentity && u.Status == UserStatus.Active).ToListAsync();
        var uncertified = active.Count(u => u.ESignatureCertifiedAt == null);
        items.Add(new PostureItem("People", "E-signature certification", $"{active.Count - uncertified} of {active.Count} active users certified",
            uncertified == 0 ? PostureStatus.Ok("Everyone has acknowledged the certification.") : PostureStatus.Info($"{Infrastructure.Ui.Ui.Plural(uncertified, "user")} will be asked before their first signature."),
            "SEC-612, 21 CFR 11.100", "Users acknowledge that their e-signature is legally binding before they first sign.", "/admin/users"));

        var mfa = active.Count(u => u.MfaEnrolled);
        items.Add(new PostureItem("People", "MFA enrollment", $"{mfa} of {active.Count} active users enrolled",
            s.MfaRequired ? (mfa == active.Count ? PostureStatus.Ok("Everyone is enrolled.") : PostureStatus.Info("The rest enroll at their next sign-in."))
                : mfa == active.Count ? PostureStatus.Ok("Everyone is enrolled.") : PostureStatus.Info("MFA is optional."),
            "SEC-301, SEC-308", "Second factor for local accounts.", "/admin/users?mfa=off"));

        var admins = active.Count(u => u.IsAdministrator);
        items.Add(new PostureItem("People", "Administrators", Infrastructure.Ui.Ui.Plural(admins, "active administrator"),
            admins >= 2 ? PostureStatus.Ok("More than one administrator can act.") : PostureStatus.Warn("Only one administrator. Consider a second for continuity."),
            "SEC-519", "Administrators manage every application and setting.", "/admin/users?admin=1"));

        var counts = await access.ActiveManagerCountsAsync();
        var noManager = access.Apps.Where(a => counts.GetValueOrDefault(a.Id) == 0).ToList();
        items.Add(new PostureItem("People", "Application managers", noManager.Count == 0 ? "Every application has one" : $"Missing: {string.Join(", ", noManager.Select(a => a.Name))}",
            noManager.Count == 0 ? PostureStatus.Ok("Every application has at least one active Manager.") : PostureStatus.Bad("Grant the Manager role on these applications."),
            "SEC-502, INV-084", "Someone accountable can manage every record of each application.", "/admin/access"));
        return items;
    }
}
