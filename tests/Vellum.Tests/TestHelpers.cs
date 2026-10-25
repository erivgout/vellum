using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Data;
using Vellum.Domain;
using Record = Vellum.Domain.Record;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;

namespace Vellum.Tests;

public static class TestHelpers
{
    public const string Password = Packaging.DemoContext.Password;

    public static async Task<Record> CreateAsync(this IServiceScope scope, string formKey, Dictionary<string, object?> values,
        Dictionary<string, List<Dictionary<string, object?>>>? rows = null, Guid? parent = null)
    {
        var res = await scope.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = formKey, Values = values, RowValues = rows, ParentId = parent,
        });
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => $"{e.FieldKey}: {e.Message}")));
        return res.Record!;
    }

    /// <summary>Route to a phase by key via the route defined from the current phase.</summary>
    public static async Task<RouteResult> RouteAsync(this IServiceScope scope, Guid recordId, string toPhase, string? password = Password,
        string? comment = null, IReadOnlyList<int>? assignees = null)
    {
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<VellumDbContext>().ChangeTracker.Clear();
        var snap = await sp.GetRequiredService<RecordService>().LoadRequiredAsync(recordId);
        var route = snap.Workflow!.Routes.First(r => r.FromPhaseId == snap.Record.PhaseId && r.ToPhase.Key == toPhase);
        return await sp.GetRequiredService<WorkflowEngine>().RouteAsync(new RouteRequest
        {
            RecordId = recordId, RouteId = route.Id, Password = password, Certify = true,
            Comment = comment ?? (route.Kind is RouteKind.Back or RouteKind.Void ? "test" : null), AssigneeIds = assignees ?? [],
        });
    }

    public static async Task<RecordSnapshot> LoadAsync(this IServiceScope scope, Guid id)
    {
        scope.ServiceProvider.GetRequiredService<VellumDbContext>().ChangeTracker.Clear();
        return await scope.ServiceProvider.GetRequiredService<RecordService>().LoadRequiredAsync(id);
    }

    public static VellumDbContext Db(this IServiceScope scope) => scope.ServiceProvider.GetRequiredService<VellumDbContext>();

    public static int UserId(this IServiceScope scope, string username) =>
        scope.ServiceProvider.GetRequiredService<Engine.Security.UserDirectory>().Get(username)!.Id;
}
