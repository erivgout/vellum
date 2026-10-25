using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;

namespace Vellum.Packaging;

/// <summary>
/// Helpers for seeding realistic demo data through the real engine (so audit trails, signatures and phase tracking exist).
/// Every helper throws with a clear message when the engine rejects an operation.
/// </summary>
public sealed class DemoContext(IServiceProvider services)
{
    public const string Password = "Vellum!Demo2026";

    public IServiceProvider Services { get; } = services;
    public VellumDbContext Db => Services.GetRequiredService<VellumDbContext>();
    public ConfigModel Config => Services.GetRequiredService<ConfigCache>().Current;
    public UserDirectory Directory => Services.GetRequiredService<UserDirectory>();
    private CurrentUser Me => Services.GetRequiredService<CurrentUser>();
    private RecordService Records => Services.GetRequiredService<RecordService>();
    private WorkflowEngine Workflow => Services.GetRequiredService<WorkflowEngine>();

    public int User(string username) => Directory.Get(username)?.Id ?? throw new InvalidOperationException($"Demo user '{username}' doesn't exist.");

    public void As(string username) => Me.Impersonate(User(username));

    /// <summary>Create a record as <paramref name="asUser"/> (and optionally backdate it).</summary>
    public async Task<Record> CreateAsync(string formKey, Dictionary<string, object?> values, string asUser,
        Guid? parent = null, Dictionary<string, List<Dictionary<string, object?>>>? rows = null,
        IReadOnlyList<string>? assignees = null, DateOnly? due = null, int daysAgo = 0, string? workflow = null)
    {
        As(asUser);
        Db.ChangeTracker.Clear();
        var res = await Records.CreateAsync(new CreateRequest
        {
            FormKey = formKey, WorkflowKey = workflow, Values = values, RowValues = rows, ParentId = parent,
            Assignees = assignees?.Select(User).ToList(), DueDate = due, SkipAccessCheck = true,
            CreatedAt = daysAgo > 0 ? DateTime.UtcNow.AddDays(-daysAgo).AddHours(-Random.Shared.Next(1, 9)) : null,
        });
        if (!res.Ok) throw new InvalidOperationException($"Demo create of {formKey} failed: {string.Join("; ", res.Errors.Select(e => $"{e.FieldKey}: {e.Message}"))}");
        return res.Record!;
    }

    /// <summary>
    /// Route a record to <paramref name="toPhaseKey"/> through the route defined from its current phase, acting as
    /// <paramref name="asUser"/> (default: its current assignee). Signs with the demo password when the route needs it.
    /// </summary>
    public async Task RouteAsync(Guid recordId, string toPhaseKey, string? asUser = null, string? comment = null,
        IReadOnlyList<string>? assignees = null, DateOnly? due = null)
    {
        Db.ChangeTracker.Clear();
        var snap = await Records.LoadRequiredAsync(recordId);
        var route = snap.Workflow!.Routes.FirstOrDefault(r => r.FromPhaseId == snap.Record.PhaseId && r.ToPhase.Key == toPhaseKey)
            ?? throw new InvalidOperationException($"{snap.Record.Number}: no route from {snap.Phase?.Key} to {toPhaseKey}.");
        var actor = asUser != null ? User(asUser)
            : await Db.Assignments.Where(a => a.RecordId == recordId && a.Status == AssignmentStatus.Active).Select(a => a.UserId).FirstAsync();
        Me.Impersonate(actor);
        var res = await Workflow.RouteAsync(new RouteRequest
        {
            RecordId = recordId, RouteId = route.Id, Comment = comment ?? (route.Kind is RouteKind.Back or RouteKind.Void ? "Demo data" : null),
            AssigneeIds = assignees?.Select(User).ToList() ?? [], DueDate = due, Password = Password, Certify = true,
        });
        if (!res.Ok) throw new InvalidOperationException($"Demo route of {snap.Record.Number} to {toPhaseKey} failed: {string.Join("; ", res.Errors.Select(e => e.Message))}");
    }

    /// <summary>Route through several phases in order.</summary>
    public async Task RouteThroughAsync(Guid recordId, params string[] phaseKeys)
    {
        foreach (var p in phaseKeys) await RouteAsync(recordId, p);
    }

    /// <summary>Set field values as the system (validated and audited).</summary>
    public async Task UpdateAsync(Guid recordId, Dictionary<string, object?> values, string asUser)
    {
        As(asUser);
        Db.ChangeTracker.Clear();
        var res = await Records.UpdateAsync(recordId, values);
        if (!res.Ok) throw new InvalidOperationException($"Demo update failed: {string.Join("; ", res.Errors.Select(e => e.Message))}");
    }

    public async Task CommentAsync(Guid recordId, string text, string asUser)
    {
        As(asUser);
        Db.ChangeTracker.Clear();
        await Records.AddCommentAsync(recordId, text);
    }

    /// <summary>Demo-only: shift the current due date (e.g. to make a record overdue). Not audited; never use outside seeding.</summary>
    public async Task SetDueAsync(Guid recordId, DateOnly due)
    {
        await Db.Records.Where(r => r.Id == recordId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DueDate, due));
        var entryId = await Db.PhaseEntries.Where(e => e.RecordId == recordId && e.ExitedAt == null).OrderByDescending(e => e.Id).Select(e => e.Id).FirstOrDefaultAsync();
        if (entryId != 0) await Db.PhaseEntries.Where(e => e.Id == entryId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DueDate, due));
    }

    public static DateOnly Today => BusinessCalendar.Today;
    public static DateOnly Days(int n) => BusinessCalendar.Today.AddDays(n);
}
