using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Supplier;

/// <summary>Runs a behavior's custom action during demo seeding exactly as the record page would (same checks, same signature).</summary>
public static class DemoActions
{
    public static async Task<ActionOutcome> RunAsync(DemoContext d, Guid recordId, string actionKey, string asUser,
        Dictionary<string, string>? inputs = null, string? comment = null)
    {
        d.As(asUser);
        d.Db.ChangeTracker.Clear();
        var records = d.Services.GetRequiredService<RecordService>();
        var snap = await records.LoadRequiredAsync(recordId);
        var ctx = new RecordContext(snap, d.Services);
        RecordBehavior? owner = null;
        foreach (var b in d.Services.GetServices<RecordBehavior>().Where(b => b.Forms.Contains(snap.Form.Key)))
            if ((await b.ActionsAsync(ctx)).Any(a => a.Key == actionKey && a.DisabledReason == null)) { owner = b; break; }
        if (owner == null) throw new InvalidOperationException($"Demo action '{actionKey}' isn't available on {snap.Record.Number} for {asUser}.");
        var outcome = await owner.ExecuteAsync(ctx, actionKey, new ActionInput
        {
            Values = inputs?.ToDictionary(kv => kv.Key, kv => new[] { kv.Value }) ?? [],
            Comment = comment, Password = DemoContext.Password, Certify = true,
        });
        if (!outcome.Success)
            throw new InvalidOperationException($"Demo action '{actionKey}' on {snap.Record.Number} failed: {string.Join("; ", outcome.Errors ?? [])}");
        return outcome;
    }
}
