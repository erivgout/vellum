using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Ui;

namespace Vellum.Modules.Designer;

/// <param name="Tone">ok | info | warn | bad</param>
public sealed record ImpactItem(string Tone, string Icon, string Title, string Detail, int Count = 0);

public sealed record PublishResult(bool Ok, IReadOnlyList<string> Errors, int Version = 0, int Changes = 0)
{
    public static PublishResult Fail(params string[] errors) => new(false, errors);
}

/// <summary>Everything the publish page shows: changes, validation, impact (SAF-071).</summary>
public sealed class PublishPlan
{
    public required DesignTarget Target { get; init; }
    public required AppDoc Doc { get; init; }
    public required List<ObjectChange> Changes { get; init; }
    public required List<DesignIssue> Issues { get; init; }
    public required List<ImpactItem> Impact { get; init; }
    public IEnumerable<DesignIssue> Errors => Issues.Where(i => i.IsError);
    public IEnumerable<DesignIssue> Warnings => Issues.Where(i => !i.IsError);
    public bool CanPublish => !Errors.Any() && (Changes.Count > 0 || Target.IsNew);
}

/// <summary>
/// Publishing (DSN-003, DSN-1201..1203, SAF-070): validate, then apply the draft to the live configuration tables in one
/// transaction, updating existing rows in place (stable IDs, so records keep their form and phase), writing one configuration
/// audit entry per changed object and a new numbered configuration version.
/// </summary>
[ScopedService]
public sealed class DesignerPublisher(VellumDbContext db, DesignerStore store, ConfigCache cfg, CurrentUser me, AuditService audit, ILogger<DesignerPublisher> log)
{
    public async Task<PublishPlan> PlanAsync(DesignTarget t)
    {
        var (doc, ctx, issues) = await store.ValidateAsync(t);
        var changes = DesignDiff.Compare(t.LiveDoc, doc);
        return new PublishPlan { Target = t, Doc = doc, Changes = changes, Issues = issues, Impact = Impact(t.LiveDoc, doc, ctx, changes) };
    }

    // ------------------------------------------------------------------ impact analysis (SAF-071)

    public static List<ImpactItem> Impact(AppDoc? live, AppDoc doc, DesignContext ctx, List<ObjectChange> changes)
    {
        var items = new List<ImpactItem>();
        if (live == null)
        {
            items.Add(new ImpactItem("info", "bi-stars", "New application", $"Publishing creates {doc.Name} with {Ui.Plural(doc.Forms.Count, "form")} and {Ui.Plural(doc.Views.Count, "view")}. You become its manager and designer."));
            return items;
        }
        if (live.DefaultAccess != doc.DefaultAccess)
            items.Add(new ImpactItem("warn", "bi-shield-lock", "Default access changes", $"Every user's default access changes from {live.DefaultAccess} to {doc.DefaultAccess} as soon as you publish (DSN-103)."));

        foreach (var lf in live.Forms)
        {
            var df = doc.Forms.FirstOrDefault(f => f.Id == lf.Id);
            if (df == null) continue;
            if (!lf.Disabled && df.Disabled)
                items.Add(new ImpactItem("warn", "bi-slash-circle", $"{df.Name} is disabled", "Users can no longer create new records of this form. Existing records stay as they are."));

            foreach (var lw in lf.Workflows)
            {
                var dw = df.Workflows.FirstOrDefault(w => w.Id == lw.Id);
                if (dw == null) continue;
                foreach (var lp in lw.Phases)
                {
                    var dp = dw.Phases.FirstOrDefault(p => p.Id == lp.Id);
                    var n = lp.Id is { } pid ? ctx.RecordsInPhase.GetValueOrDefault(pid) : 0;
                    if (n == 0) continue;
                    if (dp == null)
                    {
                        items.Add(new ImpactItem("bad", "bi-x-octagon", $"{lp.Name} is deleted but holds records", $"{Ui.Plural(n, "record")} are in {lp.Name}. Publishing is blocked until they're moved (DSN-601).", n));
                        continue;
                    }
                    var props = DesignDiff.Properties(DesignDocs.Serialize(lp), DesignDocs.Serialize(dp)).Where(p => p.Name != "Id").Select(p => p.Name.ToLowerInvariant()).ToList();
                    var routesChanged = changes.Any(c => c.Type == "Route" && c.Key.StartsWith($"{doc.Key}/{df.Key}/{dw.Key}/{lp.Key}->", StringComparison.OrdinalIgnoreCase));
                    if (props.Count == 0 && !routesChanged) continue;
                    var visibility = props.Any(p => p.Contains("sections"));
                    var routing = props.Where(p => !p.Contains("sections")).ToList();
                    var parts = new List<string>();
                    if (routing.Count > 0) parts.Add($"They keep their current assignees and due date; the new {string.Join(", ", routing)} apply from their next route into {dp.Name} (INV-071).");
                    if (visibility) parts.Add("Section visibility changes for them immediately.");
                    if (routesChanged) parts.Add("Their routing options change immediately.");
                    items.Add(new ImpactItem("info", "bi-signpost-split", $"{Ui.Plural(n, "record")} in {lp.Name}", string.Join(" ", parts), n));
                }
            }

            // Newly required fields on records that are already open.
            foreach (var s in df.Sections)
                foreach (var x in s.Fields)
                {
                    var lx = lf.AllFields.FirstOrDefault(l => l.Id == x.Id);
                    var newly = x.RequiredInPhases.Except(lx?.RequiredInPhases ?? []).ToList();
                    var nowRequired = x.Required && lx?.Required != true;
                    if (!nowRequired && newly.Count == 0) continue;
                    var open = 0;
                    foreach (var w in df.Workflows)
                        foreach (var p in w.Phases.Where(p => p.Id != null && (nowRequired || newly.Contains(p.Key))))
                            if (SectionStateOf(s, p) == SectionState.Editable) open += ctx.OpenRecordsInPhase.GetValueOrDefault(p.Id!.Value);
                    if (open > 0)
                        items.Add(new ImpactItem("warn", "bi-asterisk", $"{x.Label} becomes required", $"{Ui.Plural(open, "open record")} will need {x.Label} before they can be sent forward.", open));
                }
            foreach (var lx in lf.AllFields)
            {
                var x = df.AllFields.FirstOrDefault(d => d.Id == lx.Id);
                var n = ctx.DataFor(lf.Id, lx.Key);
                if (x == null && n > 0)
                    items.Add(new ImpactItem("warn", "bi-eye-slash", $"{lx.Label} is deleted", $"Values held by {Ui.Plural(n, "record")} stay in the database and audit trail but are no longer shown.", n));
                else if (x != null && x.Type != lx.Type && n == 0)
                    items.Add(new ImpactItem("info", "bi-arrow-left-right", $"{lx.Label} changes type", $"{DesignDocs.TypeLabel(lx.Type)} → {DesignDocs.TypeLabel(x.Type)}. No record holds a value."));
            }
        }

        var viewChanges = changes.Count(c => c.Type == "View");
        if (viewChanges > 0)
            items.Add(new ImpactItem("info", "bi-table", Ui.Plural(viewChanges, "view") + " change", "Users see the new columns, filters and navigator entries the next time they open the application."));
        var other = ctx.Cfg.Apps.Where(a => a.Id != doc.Id && a.Views.Any(v => v.Forms.Any(k => doc.Forms.Any(f => f.Key == k)))).Select(a => a.Name).ToList();
        if (other.Count > 0 && changes.Any(c => c.Type is "Field" or "Phase"))
            items.Add(new ImpactItem("info", "bi-diagram-3", "Views in other applications", $"{string.Join(", ", other)} show records of this application; their columns and phase filters use the changed fields and phases."));
        if (items.Count == 0)
            items.Add(new ImpactItem("ok", "bi-check2-circle", "No records are affected", "None of the changed phases hold records, and no open record gains a new required field."));
        return items;
    }

    /// <summary>FormRules.StateOf for draft documents (DSN-305).</summary>
    public static SectionState SectionStateOf(SectionDoc s, PhaseDoc p)
    {
        var terminal = p.Type != PhaseType.Normal;
        SectionState state;
        if (p.HiddenSections.Contains(s.Key)) state = SectionState.Hidden;
        else if (p.EditableSections.Count == 0 && p.ReadOnlySections.Count == 0) state = terminal ? SectionState.ReadOnly : SectionState.Editable;
        else if (p.ReadOnlySections.Contains(s.Key)) state = SectionState.ReadOnly;
        else if (p.EditableSections.Contains(s.Key) || p.EditableSections.Contains("*")) state = SectionState.Editable;
        else if (p.ReadOnlySections.Contains("*")) state = SectionState.ReadOnly;
        else state = SectionState.Hidden;
        return terminal && state == SectionState.Editable ? SectionState.ReadOnly : state;
    }

    // ------------------------------------------------------------------ publish

    public async Task<PublishResult> PublishAsync(string appKey, string? note, bool acceptWarnings)
    {
        var t = await store.OpenAsync(appKey);
        if (t == null) return PublishResult.Fail("You don't have designer rights for this application.");
        if (!t.HasDraft) return PublishResult.Fail("There's nothing to publish: the draft is empty.");
        if (string.IsNullOrWhiteSpace(note)) return PublishResult.Fail("Describe the change (or give the change request number) before publishing.");
        var plan = await PlanAsync(t);
        if (plan.Errors.Any()) return new PublishResult(false, plan.Errors.Select(e => e.Message).ToList());
        if (plan.Warnings.Any() && !acceptWarnings) return PublishResult.Fail("Review and accept the warnings before publishing (DSN-1201).");
        if (plan.Changes.Count == 0 && !t.IsNew) return PublishResult.Fail("The draft doesn't change anything.");

        var doc = plan.Doc;
        int number;
        try
        {
            number = await Tx.RunAsync(db, async () =>
            {
                var n = (await db.ConfigVersions.MaxAsync(v => (int?)v.Number) ?? 0) + 1;
                var app = await ApplyAsync(doc, t.Live?.Id);
                if (t.IsNew)
                    db.Grants.Add(new AppRoleGrant { AppId = app.Id, PrincipalKind = PrincipalKind.User, PrincipalId = me.Id, Role = AppRole.Manager, IsDesigner = true });

                var reason = note!.Trim();
                foreach (var c in plan.Changes) audit.Config(c.Type, c.Key, c.Action, c.Before, c.After, reason);
                var warnings = plan.Warnings.Select(w => $"{w.Object}: {w.Message}").ToList();
                if (warnings.Count > 0)
                    audit.Config("Publication", appKey, "Accepted warnings", null, JsonSerializer.Serialize(warnings), reason);
                foreach (var e in db.ChangeTracker.Entries<ConfigAuditEntry>().Where(e => e.State == EntityState.Added))
                    e.Entity.ConfigVersion = n;

                db.ConfigVersions.Add(new ConfigVersion { Number = n, PublishedAt = DateTime.UtcNow, PublishedById = me.Id, Note = $"{doc.Name}: {reason}" });
                store.Publications.Add(new ConfigPublication
                {
                    VersionNumber = n, AppKey = app.Key, AppName = app.Name, PublishedAt = DateTime.UtcNow, PublishedById = me.Id, PublishedByName = me.Name,
                    Note = reason, Changes = plan.Changes.Count, AcceptedWarnings = warnings.Count > 0 ? JsonSerializer.Serialize(warnings) : null,
                    Impact = JsonSerializer.Serialize(plan.Impact), Snapshot = DesignDocs.Serialize(DesignDocs.FromLive(Ordered(app))),
                });
                var draft = await store.Drafts.FirstOrDefaultAsync(d => d.AppKey == appKey);
                if (draft != null) store.Drafts.Remove(draft);
                store.Locks.RemoveRange(await store.Locks.Where(l => l.AppKey == appKey).ToListAsync());
                audit.Global("Configuration published", $"{doc.Name}: version {n}, {Ui.Plural(plan.Changes.Count, "change")}", reason);
                await db.SaveChangesAsync();
                return n;
            });
        }
        catch (DbUpdateException ex)
        {
            log.LogError(ex, "Publishing {App} failed", appKey);
            db.ChangeTracker.Clear();
            return PublishResult.Fail("Publishing failed and nothing was changed. Someone may have published at the same moment; reload and try again.");
        }
        finally
        {
            cfg.Invalidate();
        }
        log.LogInformation("Published {App} as configuration version {Version} ({Changes} changes)", appKey, number, plan.Changes.Count);
        return new PublishResult(true, [], number, plan.Changes.Count);
    }

    private static AppDefinition Ordered(AppDefinition app)
    {
        foreach (var f in app.Forms)
            foreach (var w in f.Workflows) w.Phases = w.Phases.OrderBy(p => p.Order).ToList();
        return app;
    }

    /// <summary>Write the document into the live tables. Three passes so foreign keys hold at every step: upsert, routes, deletions.</summary>
    private async Task<AppDefinition> ApplyAsync(AppDoc doc, int? appId)
    {
        AppDefinition app;
        if (appId is { } id)
        {
            app = await db.Apps
                .Include(a => a.Forms).ThenInclude(f => f.Sections)
                .Include(a => a.Forms).ThenInclude(f => f.Fields)
                .Include(a => a.Forms).ThenInclude(f => f.Workflows).ThenInclude(w => w.Phases)
                .Include(a => a.Forms).ThenInclude(f => f.Workflows).ThenInclude(w => w.Routes)
                .Include(a => a.Views)
                .AsSplitQuery().FirstAsync(a => a.Id == id);
        }
        else
        {
            app = new AppDefinition { Order = (await db.Apps.MaxAsync(a => (int?)a.Order) ?? 0) + 1 };
            db.Apps.Add(app);
        }

        app.Key = doc.Key; app.Name = doc.Name; app.Description = doc.Description; app.Icon = doc.Icon; app.Color = doc.Color; app.Suite = doc.Suite;
        app.DefaultAccess = doc.DefaultAccess; app.HideMyAssignments = doc.HideMyAssignments; app.ProtectFromUpgrade = doc.ProtectFromUpgrade; app.Disabled = doc.Disabled;

        // ---- pass A: insert and update (no deletions yet)
        var wfMap = new List<(WorkflowDoc Doc, WorkflowDefinition Entity, Dictionary<string, PhaseDefinition> Phases)>();
        var keptForms = new HashSet<FormDefinition>();
        var keptSections = new HashSet<SectionDefinition>();
        var keptFields = new HashSet<FieldDefinition>();
        for (var i = 0; i < doc.Forms.Count; i++)
        {
            var fd = doc.Forms[i];
            var form = fd.Id is { } fid ? app.Forms.FirstOrDefault(f => f.Id == fid) : null;
            if (form == null) { form = new FormDefinition(); app.Forms.Add(form); }
            keptForms.Add(form);
            form.Key = fd.Key; form.Name = fd.Name; form.Description = fd.Description; form.Icon = fd.Icon; form.Order = i; form.Disabled = fd.Disabled;
            form.IsActivity = fd.IsActivity; form.ParentForms = [.. fd.ParentForms]; form.IsNonWorkflow = fd.IsNonWorkflow; form.HiddenFromNew = fd.HiddenFromNew;
            form.NumberPrefix = fd.NumberPrefix; form.NumberSuffix = fd.NumberSuffix; form.NumberPadding = fd.NumberPadding; form.NumberIncludesYear = fd.NumberIncludesYear;
            form.NumberScope = fd.NumberScope; form.TitleTemplate = fd.TitleTemplate; form.RevisionControlled = fd.RevisionControlled; form.RevisionFormat = fd.RevisionFormat;

            for (var j = 0; j < fd.Sections.Count; j++)
            {
                var sd = fd.Sections[j];
                var s = sd.Id is { } sid ? form.Sections.FirstOrDefault(x => x.Id == sid) : null;
                if (s == null) { s = new SectionDefinition(); form.Sections.Add(s); }
                keptSections.Add(s);
                s.Key = sd.Key; s.Name = sd.Name; s.Description = sd.Description; s.Tab = sd.Tab; s.Order = j; s.Icon = sd.Icon; s.IsSubform = sd.IsSubform;
                s.RowNoun = sd.RowNoun; s.MaxRows = sd.MaxRows; s.Required = sd.Required; s.Optional = sd.Optional; s.Collapsed = sd.Collapsed;
            }
            var order = 0;
            foreach (var (xd, sectionKey) in fd.Sections.SelectMany(s => s.Fields.Select(x => (x, s.Key))).Concat(fd.Detached.Select(x => (x, ""))))
            {
                var x = xd.Id is { } xid ? form.Fields.FirstOrDefault(f => f.Id == xid) : null;
                if (x == null) { x = new FieldDefinition(); form.Fields.Add(x); }
                keptFields.Add(x);
                x.SectionKey = sectionKey; x.Key = xd.Key; x.Label = xd.Label; x.Type = xd.Type; x.Order = order++; x.Width = xd.Width; x.Required = xd.Required;
                x.RequiredInPhases = [.. xd.RequiredInPhases]; x.PreventNull = xd.PreventNull; x.Unique = xd.Unique; x.Sensitive = xd.Sensitive; x.ReadOnly = xd.ReadOnly;
                x.ProtectFromAdminUpdate = xd.ProtectFromAdminUpdate; x.KeywordList = xd.KeywordList; x.LinkForms = [.. xd.LinkForms]; x.Help = xd.Help;
                x.Placeholder = xd.Placeholder; x.DefaultValue = xd.DefaultValue; x.Min = xd.Min; x.Max = xd.Max; x.Decimals = xd.Decimals; x.MaxLength = xd.MaxLength;
                x.CurrencyCode = xd.CurrencyCode; x.ShowInHeader = xd.ShowInHeader; x.VisibleWhen = xd.VisibleWhen;
            }
            foreach (var wd in fd.Workflows)
            {
                var w = wd.Id is { } wid ? form.Workflows.FirstOrDefault(x => x.Id == wid) : null;
                if (w == null) { w = new WorkflowDefinition(); form.Workflows.Add(w); }
                w.Key = wd.Key; w.Name = wd.Name; w.Disabled = wd.Disabled;
                var phases = new Dictionary<string, PhaseDefinition>(StringComparer.OrdinalIgnoreCase);
                for (var k = 0; k < wd.Phases.Count; k++)
                {
                    var pd = wd.Phases[k];
                    var p = pd.Id is { } pid ? w.Phases.FirstOrDefault(x => x.Id == pid) : null;
                    if (p == null) { p = new PhaseDefinition { Type = pd.Type }; w.Phases.Add(p); }
                    p.Key = pd.Key; p.Name = pd.Name; p.Purpose = pd.Purpose; p.Type = pd.Type; p.IsStart = pd.IsStart; p.Order = k;
                    p.EditableSections = [.. pd.EditableSections]; p.ReadOnlySections = [.. pd.ReadOnlySections]; p.HiddenSections = [.. pd.HiddenSections];
                    p.Assignment = pd.Assignment; p.AssigneeField = pd.AssigneeField; p.AssigneeGroup = pd.AssigneeGroup; p.AssignAllMembers = pd.AssignAllMembers;
                    p.Routing = pd.Routing; p.QuorumPercent = pd.QuorumPercent; p.DueDays = pd.DueDays; p.DueDateField = pd.DueDateField; p.BusinessDays = pd.BusinessDays;
                    p.AssignerCanChangeDue = pd.AssignerCanChangeDue; p.MustCreateForms = [.. pd.MustCreateForms]; p.MustCompleteForms = [.. pd.MustCompleteForms];
                    p.ActivityForms = [.. pd.ActivityForms]; p.Color = pd.Color;
                    phases[pd.Key] = p;
                }
                wfMap.Add((wd, w, phases));
            }
        }
        var keptViews = new HashSet<ViewDefinition>();
        for (var i = 0; i < doc.Views.Count; i++)
        {
            var vd = doc.Views[i];
            var v = vd.Id is { } vid ? app.Views.FirstOrDefault(x => x.Id == vid) : null;
            if (v == null) { v = new ViewDefinition(); app.Views.Add(v); }
            keptViews.Add(v);
            v.Key = vd.Key; v.Name = vd.Name; v.Category = vd.Category; v.Order = i; v.Forms = [.. vd.Forms]; v.Columns = [.. vd.Columns]; v.Status = vd.Status;
            v.Phases = [.. vd.Phases]; v.AssignedToMe = vd.AssignedToMe; v.CreatedByMe = vd.CreatedByMe; v.OverdueOnly = vd.OverdueOnly;
            v.FilterJson = vd.Filters.Count == 0 ? null : JsonSerializer.Serialize(vd.Filters); v.SortBy = vd.SortBy; v.SortDesc = vd.SortDesc;
            v.PageSize = vd.PageSize; v.IsDefault = vd.IsDefault; v.Hidden = vd.Hidden; v.CalendarField = vd.CalendarField;
        }
        await db.SaveChangesAsync();

        // ---- pass B: routes (every phase now has an ID)
        var keptWorkflows = wfMap.Select(x => x.Entity).ToHashSet();
        foreach (var (wd, w, phases) in wfMap)
        {
            var keep = new HashSet<RouteDefinition>();
            for (var i = 0; i < wd.Routes.Count; i++)
            {
                var rd = wd.Routes[i];
                var r = rd.Id is { } rid ? w.Routes.FirstOrDefault(x => x.Id == rid) : null;
                if (r == null) { r = new RouteDefinition(); w.Routes.Add(r); }
                keep.Add(r);
                r.FromPhaseId = phases[rd.From].Id; r.ToPhaseId = phases[rd.To].Id; r.Kind = rd.Kind; r.Label = rd.Label; r.Order = i;
                r.RequireComment = rd.RequireComment; r.SignatureMeaning = rd.RequireSignature ? rd.SignatureMeaning : null; r.Condition = rd.Condition;
                r.AllowedPrincipals = [.. rd.AllowedPrincipals];
            }
            foreach (var r in w.Routes.Where(r => !keep.Contains(r)).ToList()) db.Routes.Remove(r);
        }
        foreach (var w in app.Forms.SelectMany(f => f.Workflows).Where(w => !keptWorkflows.Contains(w)))
            foreach (var r in w.Routes.ToList()) db.Routes.Remove(r);
        await db.SaveChangesAsync();

        // ---- pass C: deletions (validation guarantees none of these hold records)
        foreach (var (_, w, phases) in wfMap)
        {
            var keepPhases = phases.Values.ToHashSet();
            foreach (var p in w.Phases.Where(p => !keepPhases.Contains(p)).ToList()) db.Phases.Remove(p);
        }
        foreach (var form in app.Forms.ToList())
        {
            if (!keptForms.Contains(form))
            {
                foreach (var w in form.Workflows.ToList()) { foreach (var p in w.Phases.ToList()) db.Phases.Remove(p); db.Workflows.Remove(w); }
                foreach (var s in form.Sections.ToList()) db.Sections.Remove(s);
                foreach (var x in form.Fields.ToList()) db.Fields.Remove(x);
                db.Forms.Remove(form);
                continue;
            }
            foreach (var w in form.Workflows.Where(w => !keptWorkflows.Contains(w)).ToList())
            {
                foreach (var p in w.Phases.ToList()) db.Phases.Remove(p);
                db.Workflows.Remove(w);
            }
            foreach (var s in form.Sections.Where(s => !keptSections.Contains(s)).ToList()) db.Sections.Remove(s);
            foreach (var x in form.Fields.Where(x => !keptFields.Contains(x)).ToList()) db.Fields.Remove(x);
        }
        foreach (var v in app.Views.Where(v => !keptViews.Contains(v)).ToList()) db.Views.Remove(v);
        await db.SaveChangesAsync();
        return app;
    }

    // ------------------------------------------------------------------ restore (DSN-1203)

    /// <summary>Load the configuration published as <paramref name="version"/> into the draft. Publishing it creates a new version.</summary>
    public async Task<(bool Ok, string Message)> RestoreAsync(string appKey, int version)
    {
        var t = await store.OpenAsync(appKey);
        if (t == null) return (false, "You don't have designer rights for this application.");
        var pub = await store.Publications.AsNoTracking().FirstOrDefaultAsync(p => p.AppKey == appKey && p.VersionNumber == version);
        if (pub == null) return (false, $"Version {version} has no stored snapshot of {t.Name}.");
        var snap = DesignDocs.Deserialize<AppDoc>(pub.Snapshot);
        if (snap == null) return (false, "The stored snapshot can't be read.");
        DesignDocs.Reconcile(snap, t.LiveDoc);
        if (t.Live == null) return (false, "Only published applications can be restored.");
        await store.MutateAsync(appKey, d =>
        {
            d.Name = snap.Name; d.Description = snap.Description; d.Icon = snap.Icon; d.Color = snap.Color; d.Suite = snap.Suite;
            d.DefaultAccess = snap.DefaultAccess; d.HideMyAssignments = snap.HideMyAssignments; d.ProtectFromUpgrade = snap.ProtectFromUpgrade;
            d.Forms = snap.Forms; d.Views = snap.Views;
        });
        audit.Config("Application", appKey, "Version restored into draft", null, JsonSerializer.Serialize(new { Version = version }));
        await db.SaveChangesAsync();
        return (true, $"Version {version} is loaded into the draft. Review it and publish to make it live as a new version.");
    }
}
