using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;

namespace Vellum.Engine.Workflow;

public sealed record LockState(bool Mine, int? HolderId, string? HolderName, DateTime? Since, bool Expired);

/// <summary>PLT-901..904 / INV-091: exclusive edit locks with idle timeout. The primary key guarantees one holder.</summary>
public sealed class LockService(VellumDbContext db, CurrentUser me, UserDirectory dir, AuditService audit)
{
    public static int TimeoutMinutes { get; set; } = 30;

    public async Task<LockState> GetAsync(Guid recordId)
    {
        var l = await db.Locks.AsNoTracking().FirstOrDefaultAsync(x => x.RecordId == recordId);
        if (l == null) return new LockState(false, null, null, null, false);
        var expired = l.LastActivityAt < DateTime.UtcNow.AddMinutes(-TimeoutMinutes);
        return new LockState(l.UserId == me.Id, l.UserId, dir.Name(l.UserId), l.AcquiredAt, expired);
    }

    /// <summary>Take the lock if free, expired, or already ours. Returns the resulting state.</summary>
    public async Task<LockState> AcquireAsync(Guid recordId)
    {
        var now = DateTime.UtcNow;
        var l = await db.Locks.FirstOrDefaultAsync(x => x.RecordId == recordId);
        if (l == null)
        {
            db.Locks.Add(new RecordLock { RecordId = recordId, UserId = me.Id, AcquiredAt = now, LastActivityAt = now });
            audit.Record(recordId, "Locked", "Edit lock taken");
        }
        else if (l.UserId == me.Id)
            l.LastActivityAt = now;
        else if (l.LastActivityAt < now.AddMinutes(-TimeoutMinutes))
        {
            audit.Record(recordId, "Lock expired", $"Idle lock held by {dir.Name(l.UserId)} since {l.AcquiredAt:u} expired");
            l.UserId = me.Id;
            l.AcquiredAt = now;
            l.LastActivityAt = now;
        }
        else
            return new LockState(false, l.UserId, dir.Name(l.UserId), l.AcquiredAt, false);

        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return await GetAsync(recordId); }
        return new LockState(true, me.Id, me.Name, now, false);
    }

    public async Task ReleaseAsync(Guid recordId)
    {
        var l = await db.Locks.FirstOrDefaultAsync(x => x.RecordId == recordId && x.UserId == me.Id);
        if (l == null) return;
        db.Locks.Remove(l);
        audit.Record(recordId, "Unlocked", "Edit lock released");
        await db.SaveChangesAsync();
    }

    /// <summary>PLT-903: break someone else's lock (their unsaved changes are lost; they are told on next save).</summary>
    public async Task<bool> BreakAsync(Guid recordId)
    {
        var l = await db.Locks.FirstOrDefaultAsync(x => x.RecordId == recordId);
        if (l == null) return false;
        audit.Record(recordId, "Lock broken", $"Lock held by {dir.Name(l.UserId)} since {l.AcquiredAt:u} was broken");
        db.Locks.Remove(l);
        await db.SaveChangesAsync();
        return true;
    }
}
