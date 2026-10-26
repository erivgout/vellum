using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;

namespace Vellum.Infrastructure.Jobs;

/// <summary>A packaged scheduled job (ADM-802). Runs as the system identity (SEC-806) in its own scope.</summary>
public interface IScheduledJob
{
    string Key { get; }
    string Name { get; }
    string Description { get; }
    int DefaultIntervalMinutes { get; }
    Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct);
}

public sealed record JobResult(int RecordsAffected, string? Summary = null);

public sealed class JobContext(IServiceProvider services, bool manual)
{
    private readonly StringBuilder _log = new();
    public IServiceProvider Services { get; } = services;
    public VellumDbContext Db => Services.GetRequiredService<VellumDbContext>();
    public bool Manual { get; } = manual;
    public void Log(string line) => _log.AppendLine($"{DateTime.UtcNow:HH:mm:ss} {line}");
    public string Output => _log.ToString();
}

/// <summary>
/// Runs scheduled jobs. A lease per task guarantees at most one run at a time (INV-121). Missed runs after downtime are
/// detected and caught up once, with an administrator alert (ADM-805, SAF-030). Failures notify administrators (ADM-804).
/// </summary>
public sealed class Scheduler(IServiceScopeFactory scopes, ILogger<Scheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stop);
        while (!stop.IsCancellationRequested)
        {
            try { await TickAsync(stop); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Scheduler tick failed"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stop);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<string> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VellumDbContext>();
            if (!await db.Database.CanConnectAsync(ct)) return;
            var jobs = scope.ServiceProvider.GetServices<IScheduledJob>().ToList();
            var tasks = await db.Tasks.ToListAsync(ct);
            foreach (var j in jobs.Where(j => tasks.All(t => t.Key != j.Key)))
                db.Tasks.Add(new TaskProfile { Key = j.Key, Name = j.Name, Description = j.Description, IntervalMinutes = j.DefaultIntervalMinutes, NextRunAt = DateTime.UtcNow.AddMinutes(1) });
            await db.SaveChangesAsync(ct);
            var now = DateTime.UtcNow;
            due = await db.Tasks.Where(t => t.Enabled && (t.NextRunAt == null || t.NextRunAt <= now)).Select(t => t.Key).ToListAsync(ct);
        }
        foreach (var key in due) await RunAsync(key, manual: false, ct);
    }

    /// <summary>Run a job now (also used by the administration "Run now" action). Returns false if another run holds the lease.</summary>
    public async Task<bool> RunAsync(string key, bool manual, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<VellumDbContext>();
        var job = sp.GetServices<IScheduledJob>().FirstOrDefault(j => j.Key == key);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Key == key, ct);
        if (job == null || task == null) return false;

        var now = DateTime.UtcNow;
        var leased = await db.Tasks.Where(t => t.Id == task.Id && (t.LeaseUntil == null || t.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LeaseUntil, now.AddMinutes(15)), ct);
        if (leased == 0) return false;

        sp.GetRequiredService<CurrentUser>().UseSystem(job.Name);
        var missed = task.NextRunAt is { } next && !manual && task.IntervalMinutes > 0
            ? (int)((now - next).TotalMinutes / task.IntervalMinutes) : 0;
        var run = new TaskRun { TaskId = task.Id, StartedAt = now, Manual = manual };
        db.TaskRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var ctx = new JobContext(sp, manual);
        if (missed > 0) ctx.Log($"Detected {missed} missed run(s) since {task.NextRunAt:u}; catching up now (run-once policy).");
        try
        {
            var result = await job.RunAsync(ctx, ct);
            run.Outcome = "Succeeded";
            run.RecordsAffected = result.RecordsAffected;
            if (result.Summary != null) ctx.Log(result.Summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: record the run as cancelled and release the lease (below) so the next start isn't blocked.
            db.ChangeTracker.Clear();
            run = await db.TaskRuns.FirstAsync(r => r.Id == run.Id, CancellationToken.None);
            run.Outcome = "Cancelled";
            ctx.Log("Stopped because the application is shutting down.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Job {Job} failed", key);
            db.ChangeTracker.Clear();
            run = await db.TaskRuns.FirstAsync(r => r.Id == run.Id, CancellationToken.None);
            run.Outcome = "Failed";
            ctx.Log("ERROR: " + ex.Message);
            var admins = await db.Users.Where(u => u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity).Select(u => u.Id).ToListAsync(CancellationToken.None);
            foreach (var a in admins)
                db.Notifications.Add(new Notification { UserId = a, Kind = NotificationKind.Escalation, CreatedAt = DateTime.UtcNow,
                    Title = $"Scheduled task failed: {job.Name}", Body = ex.Message, Url = "/admin/tasks" });
        }
        // The bookkeeping always completes, even during shutdown, so the lease is never left behind.
        task = await db.Tasks.FirstAsync(t => t.Id == task.Id, CancellationToken.None);
        run.EndedAt = DateTime.UtcNow;
        run.Log = ctx.Output;
        task.LastRunAt = now;
        task.LastOutcome = run.Outcome;
        task.NextRunAt = DateTime.UtcNow.AddMinutes(Math.Max(1, task.IntervalMinutes));
        task.LeaseUntil = null;
        // The lease was taken with ExecuteUpdate, which the tracked entity doesn't see: always write the release (INV-121).
        db.Entry(task).Property(t => t.LeaseUntil).IsModified = true;
        if (missed > 0)
            db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "Missed task runs caught up", Success = true, Detail = $"{job.Name}: {missed} missed run(s)" });
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }
}
