using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Notifications;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Admin;

/// <summary>
/// Task profiles (ADM §5.8): enable/disable, interval, run now (ADM-803) and run history (ADM-804). Runs go through the
/// <see cref="Scheduler"/>, so the lease (INV-121), system identity (SEC-806) and failure alerts all apply.
/// </summary>
[ScopedService]
public sealed class TaskAdminService(VellumDbContext db, CurrentUser me, AuditService audit, Scheduler scheduler, IServiceProvider services)
{
    public const int MaxIntervalMinutes = 43200; // 30 days

    /// <summary>Make sure every packaged job has a task profile (the scheduler does the same on its first tick).</summary>
    public async Task EnsureProfilesAsync()
    {
        var jobs = services.GetServices<IScheduledJob>().ToList();
        var keys = await db.Tasks.Select(t => t.Key).ToListAsync();
        var missing = jobs.Where(j => !keys.Contains(j.Key)).ToList();
        if (missing.Count == 0) return;
        foreach (var j in missing)
            db.Tasks.Add(new TaskProfile { Key = j.Key, Name = j.Name, Description = j.Description, IntervalMinutes = j.DefaultIntervalMinutes, NextRunAt = DateTime.UtcNow.AddMinutes(1) });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // the scheduler created them at the same moment
    }

    private static string Snap(TaskProfile t) => AdminJson.Of(new { t.Key, t.Name, t.Enabled, t.IntervalMinutes });

    public async Task<AdminResult> SetEnabledAsync(string key, bool enabled)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var t = await db.Tasks.FirstOrDefaultAsync(x => x.Key == key);
        if (t == null) return AdminResult.Fail("That task doesn't exist.");
        if (t.Enabled == enabled) return AdminResult.Success("No changes.");
        var before = Snap(t);
        t.Enabled = enabled;
        if (enabled && (t.NextRunAt == null || t.NextRunAt < DateTime.UtcNow)) t.NextRunAt = DateTime.UtcNow.AddMinutes(1);
        audit.Global(enabled ? "Scheduled task enabled" : "Scheduled task disabled", t.Name);
        audit.Config("TaskProfile", t.Key, enabled ? "Enabled" : "Disabled", before, Snap(t));
        await db.SaveChangesAsync();
        return AdminResult.Success(enabled ? $"{t.Name} is enabled." : $"{t.Name} is disabled. It won't run until you enable it again.");
    }

    public async Task<AdminResult> SetIntervalAsync(string key, int minutes)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var t = await db.Tasks.FirstOrDefaultAsync(x => x.Key == key);
        if (t == null) return AdminResult.Fail("That task doesn't exist.");
        if (minutes is < 1 or > MaxIntervalMinutes) return AdminResult.Fail("The interval must be between 1 minute and 30 days.");
        if (t.IntervalMinutes == minutes) return AdminResult.Success("No changes.");
        var before = Snap(t);
        var old = t.IntervalMinutes;
        t.IntervalMinutes = minutes;
        t.NextRunAt = (t.LastRunAt ?? DateTime.UtcNow).AddMinutes(minutes);
        if (t.NextRunAt < DateTime.UtcNow) t.NextRunAt = DateTime.UtcNow.AddMinutes(1);
        audit.Global("Scheduled task changed", $"{t.Name}: {AdminText.Interval(old)} → {AdminText.Interval(minutes)}");
        audit.Config("TaskProfile", t.Key, "Schedule changed", before, Snap(t));
        await db.SaveChangesAsync();
        return AdminResult.Success($"{t.Name} now runs {AdminText.Interval(minutes).ToLowerInvariant()}.");
    }

    /// <summary>ADM-803: run an enabled task now. A pending scheduled run is skipped because the next run moves forward.</summary>
    public async Task<AdminResult> RunNowAsync(string key)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        await EnsureProfilesAsync();
        var t = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key);
        if (t == null) return AdminResult.Fail("That task doesn't exist.");
        if (!t.Enabled) return AdminResult.Fail($"{t.Name} is disabled. Enable it before running it.");
        audit.Global("Scheduled task run manually", t.Name);
        await db.SaveChangesAsync();
        var started = DateTime.UtcNow;
        if (!await scheduler.RunAsync(key, manual: true))
            return AdminResult.Fail($"{t.Name} is already running. Try again when the current run has finished (INV-121).");
        var run = await db.TaskRuns.AsNoTracking().Where(r => r.TaskId == t.Id && r.StartedAt >= started.AddSeconds(-1)).OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (run == null) return AdminResult.Success($"{t.Name} ran.");
        var summary = run.Log?.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (summary?.Length > 9 && summary[2] == ':' && summary[5] == ':') summary = summary[9..];
        return run.Outcome == "Succeeded"
            ? AdminResult.Success($"{t.Name} succeeded. {summary}")
            : AdminResult.Fail($"{t.Name} failed. {summary}");
    }
}

/// <summary>
/// SEC-703: accounts with no successful sign-in for N days (from creation if they never signed in) are disabled.
/// Turn the check on or off with the task profile; N comes from <c>Vellum:DormantAccountDays</c> (default 90, 0 = off).
/// The last active administrator is never disabled, so the tenant can't lock itself out.
/// </summary>
public sealed class DormantAccountJob(VellumDbContext db, IConfiguration config, AuditService audit, NotificationService notify, UserDirectory dir) : IScheduledJob
{
    public string Key => "dormant-accounts";
    public string Name => "Dormant account check";
    public string Description => "Disables accounts that haven't signed in for the configured number of days (SEC-703) and notifies administrators.";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var days = config.GetValue("Vellum:DormantAccountDays", 90);
        if (days <= 0)
        {
            ctx.Log("Dormant account check is turned off (Vellum:DormantAccountDays = 0).");
            return new JobResult(0, "Turned off.");
        }
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var dormant = await db.Users
            .Where(u => !u.IsSystemIdentity && u.Status == UserStatus.Active && (u.LastLoginAt ?? u.CreatedAt) < cutoff)
            .OrderBy(u => u.LastLoginAt ?? u.CreatedAt).ToListAsync(ct);
        var activeAdmins = await db.Users.CountAsync(u => u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity, ct);
        var disabled = new List<User>();
        foreach (var u in dormant)
        {
            if (u.IsAdministrator && activeAdmins - disabled.Count(x => x.IsAdministrator) <= 1)
            {
                ctx.Log($"Skipped {u.Username}: the last active administrator is never disabled automatically.");
                continue;
            }
            u.Status = UserStatus.Disabled;
            u.StatusReason = $"Dormant: no sign-in for {days} days";
            audit.Record(null, "User disabled", $"{u.DisplayName} ({u.Username})", reason: u.StatusReason, fieldKey: "status", fieldLabel: "Status",
                oldValue: nameof(UserStatus.Active), newValue: nameof(UserStatus.Disabled));
            audit.Security("Dormant account disabled", true, u.Id, u.Username, $"Last sign-in {(u.LastLoginAt is { } l ? l.ToString("yyyy-MM-dd") : "never")}");
            disabled.Add(u);
            ctx.Log($"Disabled {u.Username} (last sign-in {(u.LastLoginAt is { } d ? d.ToString("yyyy-MM-dd") : "never")}).");
        }
        if (disabled.Count > 0)
        {
            var admins = await db.Users.Where(u => u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity).Select(u => u.Id).ToListAsync(ct);
            foreach (var a in admins)
                notify.Notify(a, NotificationKind.Info, $"{Infrastructure.Ui.Ui.Plural(disabled.Count, "dormant account")} disabled",
                    string.Join(", ", disabled.Select(u => u.DisplayName)), url: "/admin/users?status=disabled");
        }
        await db.SaveChangesAsync(ct);
        if (disabled.Count > 0) dir.Invalidate();
        return new JobResult(disabled.Count, $"Disabled {Infrastructure.Ui.Ui.Plural(disabled.Count, "dormant account")} (threshold {days} days).");
    }
}
