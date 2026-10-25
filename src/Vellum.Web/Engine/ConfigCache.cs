using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;

namespace Vellum.Engine;

/// <summary>
/// Immutable snapshot of the published configuration (apps, forms, fields, workflows, views, keywords).
/// Loaded once, replaced atomically when configuration changes (call <see cref="ConfigCache.Invalidate"/>).
/// Entities in here are detached: never attach or modify them.
/// </summary>
public sealed class ConfigModel
{
    public required int Version { get; init; }
    public required IReadOnlyList<AppDefinition> Apps { get; init; }
    public required IReadOnlyDictionary<int, AppDefinition> AppsById { get; init; }
    public required IReadOnlyDictionary<string, AppDefinition> AppsByKey { get; init; }
    public required IReadOnlyDictionary<int, FormDefinition> FormsById { get; init; }
    public required IReadOnlyDictionary<string, FormDefinition> FormsByKey { get; init; }
    public required IReadOnlyDictionary<int, WorkflowDefinition> WorkflowsById { get; init; }
    public required IReadOnlyDictionary<int, PhaseDefinition> PhasesById { get; init; }
    public required IReadOnlyDictionary<int, KeywordList> KeywordListsById { get; init; }
    public required IReadOnlyDictionary<string, KeywordList> KeywordListsByKey { get; init; }
    public required IReadOnlyDictionary<int, Keyword> KeywordsById { get; init; }

    public FormDefinition Form(string key) =>
        FormsByKey.TryGetValue(key, out var f) ? f : throw new InvalidOperationException($"Unknown form '{key}'.");

    public AppDefinition App(string key) =>
        AppsByKey.TryGetValue(key, out var a) ? a : throw new InvalidOperationException($"Unknown application '{key}'.");

    public FieldDefinition? Field(FormDefinition form, string key) => form.Fields.FirstOrDefault(f => f.Key == key);

    public PhaseDefinition? Phase(int? id) => id is { } i && PhasesById.TryGetValue(i, out var p) ? p : null;

    public PhaseDefinition? Phase(WorkflowDefinition wf, string key) => wf.Phases.FirstOrDefault(p => p.Key == key);

    public Keyword? Keyword(int? id) => id is { } i && KeywordsById.TryGetValue(i, out var k) ? k : null;

    public Keyword? Keyword(string listKey, string keywordKey) =>
        KeywordListsByKey.TryGetValue(listKey, out var l) ? l.Keywords.FirstOrDefault(k => k.Key == keywordKey) : null;

    /// <summary>Enabled keywords of a list in display order (INV-061: disabled keywords are not offered for new values).</summary>
    public IEnumerable<Keyword> Options(string? listKey) =>
        listKey != null && KeywordListsByKey.TryGetValue(listKey, out var l)
            ? l.Keywords.Where(k => !k.Disabled).OrderBy(k => k.Order).ThenBy(k => k.Label)
            : [];

    /// <summary>Activity forms that may be created under a record of <paramref name="parent"/>.</summary>
    public IEnumerable<FormDefinition> ActivityFormsFor(FormDefinition parent) =>
        FormsById.Values.Where(f => f.IsActivity && !f.Disabled && f.Key != parent.Key &&
            (f.ParentForms.Count == 0 ? f.AppId == parent.AppId : f.ParentForms.Contains(parent.Key) || f.ParentForms.Contains("*")))
            .OrderBy(f => f.App.Order).ThenBy(f => f.Order);
}

public sealed class ConfigCache(IServiceScopeFactory scopes)
{
    private volatile ConfigModel? _model;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConfigModel Current => _model ?? Load();

    public void Invalidate() => _model = null;

    private ConfigModel Load()
    {
        _gate.Wait();
        try
        {
            if (_model != null) return _model;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VellumDbContext>();

            var apps = db.Apps.AsNoTracking()
                .Include(a => a.Forms).ThenInclude(f => f.Sections)
                .Include(a => a.Forms).ThenInclude(f => f.Fields)
                .Include(a => a.Forms).ThenInclude(f => f.Workflows).ThenInclude(w => w.Phases)
                .Include(a => a.Forms).ThenInclude(f => f.Workflows).ThenInclude(w => w.Routes)
                .Include(a => a.Views)
                .AsSplitQuery()
                .OrderBy(a => a.Order).ThenBy(a => a.Name)
                .ToList();
            var lists = db.KeywordLists.AsNoTracking().Include(l => l.Keywords).AsSplitQuery().ToList();
            var version = db.ConfigVersions.OrderByDescending(v => v.Number).Select(v => v.Number).FirstOrDefault();

            foreach (var app in apps)
            {
                app.Forms = app.Forms.OrderBy(f => f.Order).ThenBy(f => f.Name).ToList();
                app.Views = app.Views.OrderBy(v => v.Category).ThenBy(v => v.Order).ThenBy(v => v.Name).ToList();
                foreach (var form in app.Forms)
                {
                    form.App = app;
                    form.Sections = form.Sections.OrderBy(s => s.Order).ToList();
                    form.Fields = form.Fields.OrderBy(f => f.Order).ToList();
                    foreach (var wf in form.Workflows)
                    {
                        wf.Form = form;
                        wf.Phases = wf.Phases.OrderBy(p => p.Order).ToList();
                        foreach (var p in wf.Phases) p.Workflow = wf;
                        var phases = wf.Phases.ToDictionary(p => p.Id);
                        foreach (var r in wf.Routes)
                        {
                            r.Workflow = wf;
                            r.FromPhase = phases[r.FromPhaseId];
                            r.ToPhase = phases[r.ToPhaseId];
                        }
                        wf.Routes = wf.Routes.OrderBy(r => r.Kind).ThenBy(r => r.Order).ToList();
                    }
                }
            }

            var forms = apps.SelectMany(a => a.Forms).ToList();
            var workflows = forms.SelectMany(f => f.Workflows).ToList();
            foreach (var l in lists) l.Keywords = l.Keywords.OrderBy(k => k.Order).ThenBy(k => k.Label).ToList();

            _model = new ConfigModel
            {
                Version = version,
                Apps = apps,
                AppsById = apps.ToDictionary(a => a.Id),
                AppsByKey = apps.ToDictionary(a => a.Key),
                FormsById = forms.ToDictionary(f => f.Id),
                FormsByKey = forms.ToDictionary(f => f.Key),
                WorkflowsById = workflows.ToDictionary(w => w.Id),
                PhasesById = workflows.SelectMany(w => w.Phases).ToDictionary(p => p.Id),
                KeywordListsById = lists.ToDictionary(l => l.Id),
                KeywordListsByKey = lists.ToDictionary(l => l.Key),
                KeywordsById = lists.SelectMany(l => l.Keywords).ToDictionary(k => k.Id),
            };
            return _model;
        }
        finally
        {
            _gate.Release();
        }
    }
}
