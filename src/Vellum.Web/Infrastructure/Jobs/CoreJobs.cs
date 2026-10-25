using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure.Files;

namespace Vellum.Infrastructure.Jobs;

/// <summary>Delivers queued outbox email (INV-040). Without an SMTP relay, mail is written to a pickup folder as .eml.</summary>
public sealed class OutboxDispatchJob(VellumDbContext db, IWebHostEnvironment env, IConfiguration config) : IScheduledJob
{
    public string Key => "outbox-dispatch";
    public string Name => "Outbound email delivery";
    public string Description => "Delivers queued notification email from the transactional outbox, with retries.";
    public int DefaultIntervalMinutes => 1;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var pickup = config["Vellum:MailPickup"] ?? Path.Combine(env.ContentRootPath, "App_Data", "mail");
        Directory.CreateDirectory(pickup);
        var batch = await db.Outbox.Where(o => o.ProcessedAt == null && o.Attempts < 10).OrderBy(o => o.Id).Take(200).ToListAsync(ct);
        var sent = 0;
        foreach (var msg in batch)
        {
            msg.Attempts++;
            try
            {
                if (msg.Type == "email")
                {
                    var m = JsonSerializer.Deserialize<JsonElement>(msg.Payload);
                    var eml = $"To: {m.GetProperty("to").GetString()}\r\nSubject: {m.GetProperty("subject").GetString()}\r\nX-Vellum-Outbox: {msg.Id}\r\n\r\n{m.GetProperty("body").GetString()}\r\n";
                    await File.WriteAllTextAsync(Path.Combine(pickup, $"{msg.CreatedAt:yyyyMMddHHmmss}-{msg.Id}.eml"), eml, ct);
                    var log = await db.EmailLog.Where(e => e.RecordId == msg.RecordId && e.Status == "Queued" && e.Subject == m.GetProperty("subject").GetString())
                        .OrderBy(e => e.Id).FirstOrDefaultAsync(ct);
                    if (log != null) log.Status = "Delivered";
                }
                msg.ProcessedAt = DateTime.UtcNow;
                msg.Error = null;
                sent++;
            }
            catch (Exception ex)
            {
                msg.Error = ex.Message;
            }
        }
        await db.SaveChangesAsync(ct);
        return new JobResult(sent, $"Delivered {sent} of {batch.Count} queued messages.");
    }
}

/// <summary>
/// Overdue reminders and escalation to the reports-to manager (PLT-1207, CAPA-007). Evaluated against the business calendar;
/// a run after downtime catches up because it looks at the current overdue state, not at missed ticks (PLT-1211).
/// </summary>
public sealed class OverdueEscalationJob(VellumDbContext db, NotificationService notify, UserDirectory dir, ConfigCache cfg) : IScheduledJob
{
    public string Key => "overdue-escalation";
    public string Name => "Overdue assignment escalation";
    public string Description => "Reminds assignees of overdue work daily and escalates to their manager after the configured number of days.";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        var today = BusinessCalendar.Today;
        var cutoff = DateTime.UtcNow.AddHours(-20);
        var overdue = await db.Assignments.Include(a => a.Record)
            .Where(a => a.Status == AssignmentStatus.Active && a.Record.PhaseType == PhaseType.Normal && a.Record.DueDate < today &&
                        (a.LastReminderAt == null || a.LastReminderAt < cutoff))
            .Take(500).ToListAsync(ct);
        foreach (var a in overdue)
        {
            var r = a.Record;
            var form = cfg.Current.FormsById[r.FormId];
            var days = today.DayNumber - r.DueDate!.Value.DayNumber;
            notify.Notify(a.UserId, NotificationKind.Reminder, $"{form.Name} {r.Number} is {days} day{(days == 1 ? "" : "s")} overdue", r.Title, r.Id);
            if (days >= settings.EscalateToManagerAfterDays && dir.Get(a.UserId)?.ReportsToId is { } mgr && dir.Get(mgr) is { IsActive: true })
                notify.Notify(mgr, NotificationKind.Escalation, $"Escalation: {form.Name} {r.Number} overdue {days} days",
                    $"Assigned to {dir.Name(a.UserId)}: {r.Title}", r.Id);
            a.LastReminderAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return new JobResult(overdue.Count, $"Sent {overdue.Count} overdue reminder(s).");
    }
}

/// <summary>PLT-1304: delegations start and end automatically on their dates.</summary>
public sealed class DelegationJob(VellumDbContext db) : IScheduledJob
{
    public string Key => "delegation-expiry";
    public string Name => "Delegation start and expiry";
    public string Description => "Ends delegations whose end date has passed.";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var today = BusinessCalendar.Today;
        var ended = await db.Delegations.Where(d => d.Status == DelegationStatus.Active && d.EndDate < today).ToListAsync(ct);
        foreach (var d in ended) d.Status = DelegationStatus.Completed;
        await db.SaveChangesAsync(ct);
        return new JobResult(ended.Count, $"Completed {ended.Count} expired delegation(s).");
    }
}

/// <summary>
/// SAF-066: verifies every audit hash chain (INV-055), signature links and stored file checksums (INV-053).
/// Discrepancies are raised to administrators as security events.
/// </summary>
public sealed class IntegrityCheckJob(VellumDbContext db, FileStore files, NotificationService notify) : IScheduledJob
{
    public string Key => "integrity-check";
    public string Name => "Data integrity checker";
    public string Description => "Verifies audit-trail hash chains, signature links and attachment checksums.";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var problems = new List<string>();
        var chains = 0;
        var recordIds = await db.Audit.Select(a => a.RecordId).Distinct().ToListAsync(ct);
        foreach (var rid in recordIds)
        {
            var entries = await db.Audit.AsNoTracking().Where(a => a.RecordId == rid).OrderBy(a => a.Seq).ToListAsync(ct);
            var broken = AuditHashing.FirstBreak(entries);
            chains++;
            if (broken != null) problems.Add($"Audit chain {(rid?.ToString() ?? "global")} breaks at entry #{broken.Seq} ({broken.Action}).");
        }
        var orphanSigs = await db.Signatures.CountAsync(s => !db.Records.Any(r => r.Id == s.RecordId), ct);
        if (orphanSigs > 0) problems.Add($"{orphanSigs} signature(s) are not linked to an existing record.");

        var blobs = await db.FileBlobs.AsNoTracking().Select(b => b.Sha256).ToListAsync(ct);
        foreach (var sha in blobs.Where(sha => !files.Verify(sha)))
            problems.Add($"Stored file {sha[..12]}… is missing or fails its checksum.");

        foreach (var p in problems) ctx.Log("PROBLEM: " + p);
        db.SecurityEvents.Add(new SecurityEvent
        {
            At = DateTime.UtcNow, Kind = "Integrity check", Success = problems.Count == 0,
            Detail = problems.Count == 0 ? $"{chains} audit chains, {blobs.Count} files verified." : string.Join(" ", problems.Take(10)),
        });
        if (problems.Count > 0)
        {
            var admins = await db.Users.Where(u => u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity).Select(u => u.Id).ToListAsync(ct);
            notify.SafetyCritical(admins, "Data integrity discrepancy detected", string.Join(" ", problems.Take(5)), null);
        }
        await db.SaveChangesAsync(ct);
        return new JobResult(chains + blobs.Count, problems.Count == 0 ? $"All {chains} audit chains and {blobs.Count} files verified." : $"{problems.Count} problem(s) found.");
    }
}

/// <summary>Removes expired sessions and stale edit locks.</summary>
public sealed class SessionCleanupJob(VellumDbContext db) : IScheduledJob
{
    public string Key => "session-cleanup";
    public string Name => "Session and lock cleanup";
    public string Description => "Ends expired sessions and releases idle edit locks.";
    public int DefaultIntervalMinutes => 15;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync(ct);
        var now = DateTime.UtcNow;
        var idle = now.AddMinutes(-settings.SessionIdleMinutes);
        var n = await db.Sessions.Where(s => s.RevokedAt == null && (s.LastSeenAt < idle || s.AbsoluteExpiresAt < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, "Expired"), ct);
        var lockCut = now.AddMinutes(-settings.LockTimeoutMinutes);
        var l = await db.Locks.Where(x => x.LastActivityAt < lockCut).ExecuteDeleteAsync(ct);
        return new JobResult(n + l, $"Expired {n} session(s), released {l} idle lock(s).");
    }
}
