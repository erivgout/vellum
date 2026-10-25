using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Infrastructure;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>
/// Publishing rules by form, cached for the behavior's <see cref="Engine.Behaviors.RecordBehavior.Forms"/> check, which runs on
/// every save and route. Invalidated on every rule change; a short time-to-live covers other server instances.
/// </summary>
public static class SharePointRuleCache
{
    private static volatile Snapshot? _snap;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private sealed record Snapshot(DateTime At, IReadOnlyList<SharePointRule> Rules, IReadOnlySet<string> Forms);

    public static void Invalidate() => _snap = null;

    public static IReadOnlyList<SharePointRule> Rules(VellumDbContext db) => Load(db).Rules;

    /// <summary>Forms with at least one rule (enabled or not), so their records show the publication panel.</summary>
    public static IReadOnlySet<string> Forms(VellumDbContext db) => Load(db).Forms;

    private static Snapshot Load(VellumDbContext db)
    {
        var s = _snap;
        if (s != null && DateTime.UtcNow - s.At < Ttl) return s;
        try
        {
            var rules = db.Set<SharePointRule>().AsNoTracking().OrderBy(r => r.Id).ToList();
            s = new Snapshot(DateTime.UtcNow, rules, rules.Select(r => r.FormKey).ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            // NFR-025: publishing configuration problems never block record work.
            s = new Snapshot(DateTime.UtcNow, [], new HashSet<string>());
        }
        _snap = s;
        return s;
    }
}

/// <summary>Adds publish and withdraw work to the durable queue (same unit of work as the triggering save or route).</summary>
[ScopedService]
public sealed class SharePointQueue(VellumDbContext db, SharePointClientFactory factory)
{
    private DbSet<SharePointJob> Jobs => db.Set<SharePointJob>();

    /// <summary>Queue a publish unless one is already waiting for the same record and rule (then it runs now).</summary>
    public async Task<SharePointJob> PublishAsync(Guid recordId, int ruleId, string trigger)
    {
        var pending = Jobs.Local.FirstOrDefault(j => j.RecordId == recordId && j.RuleId == ruleId && j.Kind == SpJobKind.Publish && j.Status == SpJobStatus.Pending)
                      ?? await Jobs.FirstOrDefaultAsync(j => j.RecordId == recordId && j.RuleId == ruleId && j.Kind == SpJobKind.Publish && j.Status == SpJobStatus.Pending);
        if (pending != null)
        {
            pending.NextAttemptAt = DateTime.UtcNow;
            return pending;
        }
        return Add(SpJobKind.Publish, recordId, ruleId, trigger);
    }

    /// <summary>Queue the withdrawal of every current publication of a record (SAF-021, INT-308).</summary>
    public async Task<SharePointJob> WithdrawAsync(Guid recordId, string trigger)
    {
        var pending = Jobs.Local.FirstOrDefault(j => j.RecordId == recordId && j.Kind == SpJobKind.Withdraw && j.Status == SpJobStatus.Pending)
                      ?? await Jobs.FirstOrDefaultAsync(j => j.RecordId == recordId && j.Kind == SpJobKind.Withdraw && j.Status == SpJobStatus.Pending);
        if (pending != null)
        {
            pending.NextAttemptAt = DateTime.UtcNow;
            return pending;
        }
        return Add(SpJobKind.Withdraw, recordId, null, trigger);
    }

    private SharePointJob Add(SpJobKind kind, Guid recordId, int? ruleId, string trigger)
    {
        var now = DateTime.UtcNow;
        var job = new SharePointJob
        {
            Kind = kind, RecordId = recordId, RuleId = ruleId, Trigger = trigger.Length > 200 ? trigger[..200] : trigger,
            Status = SpJobStatus.Pending, MaxAttempts = factory.MaxAttempts, NextAttemptAt = now, CreatedAt = now,
        };
        Jobs.Add(job);
        return job;
    }
}
