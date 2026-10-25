using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;

namespace Vellum.Engine.Behaviors;

/// <summary>
/// Server-side business logic for packaged applications (the "hooks" of DSN-1002, written in C#).
/// Implementations are discovered automatically (any non-abstract subclass in the assembly) and registered as scoped services.
/// Everything runs inside the same transaction as the triggering save or route, so automation is never lost (INV-040).
/// Behaviors enforce invariants on the server; never rely on the UI for them (INV-082).
/// </summary>
public abstract class RecordBehavior
{
    /// <summary>Form keys this behavior applies to.</summary>
    public abstract IReadOnlyCollection<string> Forms { get; }

    /// <summary>Lower runs first.</summary>
    public virtual int Order => 0;

    /// <summary>Before validation and persistence of a save (and of a create). Compute fields and add errors here.</summary>
    public virtual Task OnSavingAsync(SavingContext ctx) => Task.CompletedTask;

    /// <summary>After a new record has been created (same transaction).</summary>
    public virtual Task OnCreatedAsync(RecordContext ctx) => Task.CompletedTask;

    /// <summary>Before a route is applied (and before any signature is collected, SEC-607). Add errors to block it.</summary>
    public virtual Task ValidateRouteAsync(RoutingContext ctx) => Task.CompletedTask;

    /// <summary>After the record has entered its new phase (same transaction).</summary>
    public virtual Task OnRoutedAsync(RoutingContext ctx) => Task.CompletedTask;

    /// <summary>Custom actions shown in the record toolbar.</summary>
    public virtual Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx) => Task.FromResult(Enumerable.Empty<RecordAction>());

    /// <summary>Run a custom action. Authorization must be re-checked here.</summary>
    public virtual Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input) =>
        Task.FromResult(ActionOutcome.Fail("This action isn't available."));

    /// <summary>Extra UI panels rendered on the record page.</summary>
    public virtual Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx) => Task.FromResult(Enumerable.Empty<RecordPanel>());
}

public class RecordContext(RecordSnapshot snapshot, IServiceProvider services)
{
    public RecordSnapshot Snapshot { get; } = snapshot;
    public IServiceProvider Services { get; } = services;
    public Record Record => Snapshot.Record;
    public ConfigModel Config => Snapshot.Config;
    public VellumDbContext Db => Services.GetRequiredService<VellumDbContext>();
    public RecordService Records => Services.GetRequiredService<RecordService>();
    public WorkflowEngine Workflow => Services.GetRequiredService<WorkflowEngine>();
    public CurrentUser Me => Services.GetRequiredService<CurrentUser>();
    public AccessService Access => Services.GetRequiredService<AccessService>();
    public UserDirectory Directory => Services.GetRequiredService<UserDirectory>();
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
}

public sealed class SavingContext(RecordSnapshot? current, RecordData proposed, FormDefinition form, ConfigModel config, IServiceProvider services, bool isNew)
{
    /// <summary>The stored state before this save. Null when the record is being created.</summary>
    public RecordSnapshot? Current { get; } = current;
    /// <summary>The data that will be stored. Behaviors may change it (computed fields).</summary>
    public RecordData Proposed { get; } = proposed;
    public FormDefinition Form { get; } = form;
    public ConfigModel Config { get; } = config;
    public IServiceProvider Services { get; } = services;
    public bool IsNew { get; } = isNew;
    public PhaseDefinition? Phase => Current?.Phase;
    public List<FieldError> Errors { get; } = [];

    public void Error(string? fieldKey, string message, Guid? rowId = null) => Errors.Add(new FieldError(fieldKey, message, rowId));

    /// <summary>Set a computed main-form value.</summary>
    public void Set(string fieldKey, object? value)
    {
        var f = Form.Fields.First(x => x.Key == fieldKey);
        Proposed.Main.Set(fieldKey, FieldCodec.FromObject(f, value, Config));
    }

    public string? KeywordKey(string fieldKey) => Config.Keyword(Proposed.Main.Ref(fieldKey))?.Key;
    public string? KeywordKey(RowBag row, string fieldKey) => Config.Keyword(row.Values.Ref(fieldKey))?.Key;
}

public sealed class RoutingContext(RecordSnapshot snapshot, IServiceProvider services, RouteDefinition route) : RecordContext(snapshot, services)
{
    public RouteDefinition Route { get; } = route;
    public PhaseDefinition From => Route.FromPhase;
    public PhaseDefinition To => Route.ToPhase;
    public RouteKind Kind => Route.Kind;
    public string? Comment { get; init; }
    /// <summary>True when the system identity routes (escalation, auto-route, behavior).</summary>
    public bool IsSystem { get; init; }
    public List<string> Errors { get; } = [];
    public void Error(string message) => Errors.Add(message);
    /// <summary>Field-level route blockers (shown against the field, like required-field errors).</summary>
    public List<FieldError> FieldErrors { get; } = [];
    public void Error(string fieldKey, string message, Guid? rowId = null) => FieldErrors.Add(new FieldError(fieldKey, message, rowId));
}

public sealed record FieldError(string? FieldKey, string Message, Guid? RowId = null);

public sealed class RecordAction
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Icon { get; init; } = "bi-lightning-charge";
    public string? Description { get; init; }
    /// <summary>Bootstrap variant for the confirm button.</summary>
    public string Variant { get; init; } = "primary";
    /// <summary>When set, the action is shown but disabled with this explanation.</summary>
    public string? DisabledReason { get; init; }
    public bool RequiresComment { get; init; }
    public string? CommentLabel { get; init; }
    public string? Confirm { get; init; }
    /// <summary>When set, running the action needs an e-signature with this meaning.</summary>
    public string? SignatureMeaning { get; init; }
    public List<ActionField> Inputs { get; init; } = [];
    /// <summary>Menu group label (e.g. "Create", "Revision control").</summary>
    public string Group { get; init; } = "Actions";
}

public sealed record ActionField(string Key, string Label, string Type = "text", bool Required = false, string? Help = null,
    IReadOnlyList<(string Value, string Label)>? Options = null, string? Default = null);

public sealed class ActionInput
{
    public Dictionary<string, string[]> Values { get; init; } = [];
    public string? Comment { get; init; }
    public string? Password { get; init; }
    public bool Certify { get; init; }
    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) : null;
    public IEnumerable<string> GetAll(string key) => Values.TryGetValue(key, out var v) ? v.Where(s => !string.IsNullOrWhiteSpace(s)) : [];
}

public sealed record ActionOutcome(bool Success, string? Message = null, Guid? OpenRecordId = null, IReadOnlyList<string>? Errors = null)
{
    public static ActionOutcome Ok(string? message = null, Guid? open = null) => new(true, message, open);
    public static ActionOutcome Fail(params string[] errors) => new(false, null, null, errors);
}

/// <summary>An extra panel on the record page, rendered from a Razor partial with the given model.</summary>
public sealed class RecordPanel
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Icon { get; init; } = "bi-layout-text-sidebar";
    /// <summary>App-relative partial path, e.g. <c>~/Modules/DocumentControl/Views/_RevisionHistory.cshtml</c>.</summary>
    public required string Partial { get; init; }
    public object? Model { get; init; }
    /// <summary>tab = its own tab in the main column; side = a card in the right rail; top = above the tabs.</summary>
    public string Placement { get; init; } = "tab";
    public int Order { get; init; }
    public int? Count { get; init; }
}

public sealed class BehaviorRegistry(IServiceProvider services)
{
    public IEnumerable<RecordBehavior> For(FormDefinition form) =>
        services.GetServices<RecordBehavior>().Where(b => b.Forms.Contains(form.Key)).OrderBy(b => b.Order);
}
