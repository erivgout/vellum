using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Documents;

/// <summary>Runs a behavior's custom action for demo seeding in its own DI scope, exactly as a record-page request would.</summary>
internal static class DemoActions
{
    public static async Task<ActionOutcome> RunAsync<TBehavior>(DemoContext root, Guid id, string action, string asUser,
        Dictionary<string, string[]>? values = null, string? comment = null) where TBehavior : RecordBehavior
    {
        using var scope = root.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var d = new DemoContext(scope.ServiceProvider);
        d.As(asUser);
        var snap = await d.Services.GetRequiredService<RecordService>().LoadRequiredAsync(id);
        var behavior = d.Services.GetServices<RecordBehavior>().OfType<TBehavior>().Single();
        var outcome = await behavior.ExecuteAsync(new RecordContext(snap, d.Services), action,
            new ActionInput { Values = values ?? [], Comment = comment, Password = DemoContext.Password, Certify = true });
        if (!outcome.Success) throw new InvalidOperationException($"Demo action {action} failed: {string.Join("; ", outcome.Errors ?? [])}");
        return outcome;
    }
}
