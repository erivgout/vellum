using Microsoft.EntityFrameworkCore;

namespace Vellum.Data;

/// <summary>Run work in a transaction, joining the ambient one when it exists (INV-093: every record change is atomic).</summary>
public static class Tx
{
    public static async Task<T> RunAsync<T>(VellumDbContext db, Func<Task<T>> work, Func<T, bool>? commitWhen = null)
    {
        if (db.Database.CurrentTransaction != null) return await work();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var result = await work();
            if (commitWhen == null || commitWhen(result)) await tx.CommitAsync();
            else
            {
                await tx.RollbackAsync();
                db.ChangeTracker.Clear();
            }
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
