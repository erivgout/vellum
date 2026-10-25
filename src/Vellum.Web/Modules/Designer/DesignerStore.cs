using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Designer;

/// <summary>An application opened in the designer: its live definition, its draft (if any) and the document being edited.</summary>
public sealed class DesignTarget
{
    public required string AppKey { get; init; }
    public AppDefinition? Live { get; init; }
    public AppDoc? LiveDoc { get; init; }
    public ConfigDraft? Draft { get; init; }
    public required AppDoc Doc { get; init; }
    public bool IsNew => Live == null;
    public bool HasDraft => Draft != null;
    public string Name => Doc.Name;
}

public sealed record LockResult(bool Ok, string? HolderName = null, DateTime? Since = null, bool Mine = false);

/// <summary>
/// Drafts (DSN-003), component locks (DSN-004), designer rights (DSN-001) and the facts validation needs.
/// The live configuration tables are only ever written by <see cref="DesignerPublisher"/>.
/// </summary>
[ScopedService]
public sealed class DesignerStore(VellumDbContext db, ConfigCache cfg, CurrentUser me, AccessService access, UserDirectory dir,
    AuditService audit, IConfiguration config, ILogger<DesignerStore> log)
{
    public const string CreatorsGroup = "app-creators";
    public int LockMinutes => config.GetValue("Vellum:DesignerLockMinutes", 30);

    public DbSet<ConfigDraft> Drafts => db.Set<ConfigDraft>();
    public DbSet<DesignerLock> Locks => db.Set<DesignerLock>();
    public DbSet<ConfigPublication> Publications => db.Set<ConfigPublication>();
    public ConfigModel Cfg => cfg.Current;
    public CurrentUser Me => me;
    public UserDirectory Directory => dir;

    // ------------------------------------------------------------------ schema

    private static volatile bool _schemaReady;
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly string[] Tables = ["DesignerDrafts", "DesignerLocks", "DesignerPublications"];

    /// <summary>
    /// The database is created with EnsureCreated, which only builds tables for a brand-new database. Databases created
    /// before the designer module existed get the module's tables here, from the EF model's own DDL.
    /// </summary>
    public async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await SchemaGate.WaitAsync();
        try
        {
            if (_schemaReady) return;
            var missing = new List<string>();
            foreach (var t in Tables)
            {
                var exists = await db.Database.SqlQueryRaw<int>($"SELECT CASE WHEN OBJECT_ID(N'[{t}]', N'U') IS NULL THEN 0 ELSE 1 END AS [Value]").SingleAsync();
                if (exists == 0) missing.Add(t);
            }
            if (missing.Count > 0)
            {
                var script = db.Database.GenerateCreateScript();
                var statements = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline).Select(s => s.Trim()).Where(s => s.Length > 0);
                foreach (var st in statements)
                    if (missing.Any(t => st.Contains($"CREATE TABLE [{t}]") || Regex.IsMatch(st, $@"CREATE (UNIQUE )?INDEX \[[^\]]+\] ON \[{t}\]")))
                        await db.Database.ExecuteSqlRawAsync(st);
                log.LogInformation("Designer: created tables {Tables}", string.Join(", ", missing));
            }
            _schemaReady = true;
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    // ------------------------------------------------------------------ access (DSN-001, DSN-005)

    public bool IsAnyDesigner => me.IsAdmin || access.IsAnyDesigner;

    /// <summary>DSN-001: creating applications needs the separate Application Creator permission (administrators, or the app-creators group).</summary>
    public bool CanCreateApps => me.IsAdmin || (me.IsAuthenticated && dir.IsMember(me.Id, CreatorsGroup));

    public bool CanDesign(AppDefinition app) => !app.IsSystem && access.IsDesigner(app.Id);

    public bool CanDesign(DesignTarget t) =>
        t.Live != null ? CanDesign(t.Live) : t.Draft is { IsNewApp: true } d && (me.IsAdmin || d.CreatedById == me.Id);

    /// <summary>Applications the current user can design, plus their own unpublished new applications.</summary>
    public async Task<List<DesignTarget>> MyTargetsAsync()
    {
        await EnsureSchemaAsync();
        var drafts = await Drafts.AsNoTracking().ToListAsync();
        var list = new List<DesignTarget>();
        foreach (var app in Cfg.Apps.Where(a => !a.IsSystem && access.IsDesigner(a.Id)))
        {
            var d = drafts.FirstOrDefault(x => x.AppKey == app.Key);
            var liveDoc = DesignDocs.FromLive(app);
            list.Add(new DesignTarget { AppKey = app.Key, Live = app, LiveDoc = liveDoc, Draft = d, Doc = d != null ? Parse(d) : liveDoc });
        }
        foreach (var d in drafts.Where(x => x.IsNewApp && !Cfg.AppsByKey.ContainsKey(x.AppKey) && (me.IsAdmin || x.CreatedById == me.Id)))
            list.Add(new DesignTarget { AppKey = d.AppKey, Draft = d, Doc = Parse(d) });
        return list;
    }

    /// <summary>Open an application for design. Returns null when it doesn't exist or the user may not design it.</summary>
    public async Task<DesignTarget?> OpenAsync(string appKey)
    {
        await EnsureSchemaAsync();
        var live = Cfg.AppsByKey.GetValueOrDefault(appKey);
        var draft = await Drafts.AsNoTracking().FirstOrDefaultAsync(d => d.AppKey == appKey);
        if (live == null && draft is not { IsNewApp: true }) return null;
        var liveDoc = live != null ? DesignDocs.FromLive(live) : null;
        var t = new DesignTarget { AppKey = live?.Key ?? appKey, Live = live, LiveDoc = liveDoc, Draft = draft, Doc = draft != null ? Parse(draft) : DesignDocs.Clone(liveDoc!) };
        return CanDesign(t) ? t : null;
    }

    private static AppDoc Parse(ConfigDraft d) => DesignDocs.Deserialize<AppDoc>(d.Json) ?? new AppDoc { Key = d.AppKey, Name = d.AppKey };

    public static string Hash(AppDoc doc) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DesignDocs.Serialize(Sanitize(DesignDocs.Clone(doc)))))).ToLowerInvariant();

    // ------------------------------------------------------------------ drafts

    /// <summary>
    /// Change the draft of an application (creating it from the live configuration on first change). Concurrent edits by
    /// different designers to different components merge because each change is applied to the latest stored document.
    /// When the result equals the live configuration again, the draft is dropped.
    /// </summary>
    public async Task<AppDoc> MutateAsync(string appKey, Action<AppDoc> change, string? what = null)
    {
        await EnsureSchemaAsync();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var draft = await Drafts.FirstOrDefaultAsync(d => d.AppKey == appKey);
                var live = Cfg.AppsByKey.GetValueOrDefault(appKey);
                var liveDoc = live != null ? DesignDocs.FromLive(live) : null;
                AppDoc doc;
                if (draft == null)
                {
                    if (liveDoc == null) throw new InvalidOperationException($"Application '{appKey}' doesn't exist.");
                    doc = DesignDocs.Clone(liveDoc);
                    draft = new ConfigDraft
                    {
                        AppKey = appKey, BaseHash = Hash(liveDoc), BaseVersion = Cfg.Version, CreatedAt = DateTime.UtcNow, CreatedById = me.Id, CreatedByName = me.Name,
                    };
                    Drafts.Add(draft);
                }
                else doc = Parse(draft);

                change(doc);
                Sanitize(doc);
                if (liveDoc != null && !draft.IsNewApp && Hash(doc) == Hash(liveDoc))
                {
                    if (draft.Id != 0) Drafts.Remove(draft);
                    else Drafts.Local.Remove(draft);
                }
                else
                {
                    draft.Json = DesignDocs.Serialize(doc);
                    draft.UpdatedAt = DateTime.UtcNow;
                    draft.UpdatedById = me.Id;
                    draft.UpdatedByName = me.Name;
                    draft.Revision++;
                }
                await db.SaveChangesAsync();
                return doc;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.ChangeTracker.Clear(); // someone saved the same draft at the same moment: re-apply on top of theirs
            }
        }
    }

    /// <summary>"New application" (DSN-001): the application exists only as a draft until it's published.</summary>
    public async Task<ConfigDraft> CreateAppDraftAsync(AppDoc doc)
    {
        await EnsureSchemaAsync();
        Sanitize(doc);
        var d = new ConfigDraft
        {
            AppKey = doc.Key, IsNewApp = true, Json = DesignDocs.Serialize(doc), BaseHash = "", BaseVersion = Cfg.Version,
            CreatedAt = DateTime.UtcNow, CreatedById = me.Id, CreatedByName = me.Name, UpdatedAt = DateTime.UtcNow, UpdatedById = me.Id, UpdatedByName = me.Name, Revision = 1,
        };
        Drafts.Add(d);
        audit.Config("Application", doc.Key, "Draft created", null, DesignDocs.Serialize(new { doc.Name, doc.Suite }), "New application");
        await db.SaveChangesAsync();
        return d;
    }

    /// <summary>Throw the draft away; the live configuration is untouched.</summary>
    public async Task DiscardAsync(string appKey)
    {
        await EnsureSchemaAsync();
        var d = await Drafts.FirstOrDefaultAsync(x => x.AppKey == appKey);
        if (d != null)
        {
            Drafts.Remove(d);
            audit.Config("Application", appKey, "Draft discarded", null, null, d.IsNewApp ? "Unpublished application deleted" : null);
        }
        Locks.RemoveRange(await Locks.Where(l => l.AppKey == appKey).ToListAsync());
        await db.SaveChangesAsync();
    }

    /// <summary>Normalize a document the way the designer stores it (trimmed strings, empty → null, clamped widths, de-duplicated lists).</summary>
    public static AppDoc Sanitize(AppDoc doc)
    {
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        static string T(string? s) => (s ?? "").Trim();
        static List<string> L(IEnumerable<string>? xs) => (xs ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        doc.Key = T(doc.Key); doc.Name = T(doc.Name); doc.Description = N(doc.Description); doc.Icon = T(doc.Icon); doc.Color = T(doc.Color); doc.Suite = T(doc.Suite);
        doc.Forms ??= []; doc.Views ??= [];
        foreach (var f in doc.Forms)
        {
            f.Key = T(f.Key); f.Name = T(f.Name); f.Description = N(f.Description); f.Icon = N(f.Icon) ?? "bi-file-earmark-text";
            f.ParentForms = L(f.ParentForms); f.NumberPrefix ??= ""; f.NumberSuffix ??= ""; f.NumberScope = N(f.NumberScope);
            f.TitleTemplate = N(f.TitleTemplate) ?? "{title}";
            f.Sections ??= []; f.Detached ??= []; f.Workflows ??= [];
            foreach (var s in f.Sections)
            {
                s.Key = T(s.Key); s.Name = T(s.Name); s.Description = N(s.Description); s.Tab = N(s.Tab) ?? "General"; s.Icon = N(s.Icon);
                s.RowNoun = s.IsSubform ? N(s.RowNoun) : null;
                if (!s.IsSubform) { s.MaxRows = null; s.Required = false; }
                s.Fields ??= [];
            }
            foreach (var x in f.AllFields)
            {
                x.Key = T(x.Key); x.Label = T(x.Label); x.Help = N(x.Help); x.Placeholder = N(x.Placeholder); x.DefaultValue = N(x.DefaultValue);
                x.VisibleWhen = N(x.VisibleWhen); x.RequiredInPhases = L(x.RequiredInPhases); x.LinkForms = L(x.LinkForms);
                x.Width = x.Type == FieldType.Prompt ? 12 : Math.Clamp(x.Width, 1, 12);
                if (x.Type is not (FieldType.Keyword or FieldType.KeywordMulti)) x.KeywordList = null; else x.KeywordList = N(x.KeywordList);
                if (x.Type != FieldType.Link) x.LinkForms = [];
                if (x.Type is not (FieldType.Number or FieldType.Currency)) { x.Min = null; x.Max = null; x.Decimals = null; }
                if (x.Type != FieldType.Currency) x.CurrencyCode = null; else x.CurrencyCode = N(x.CurrencyCode)?.ToUpperInvariant() ?? "USD";
                if (x.Type is not (FieldType.Text or FieldType.TextArea)) x.MaxLength = null;
                if (x.Type == FieldType.Prompt) { x.Required = false; x.RequiredInPhases = []; x.PreventNull = false; x.Unique = false; x.ShowInHeader = false; }
            }
            foreach (var w in f.Workflows)
            {
                w.Key = T(w.Key); w.Name = T(w.Name); w.Phases ??= []; w.Routes ??= [];
                foreach (var p in w.Phases)
                {
                    p.Key = T(p.Key); p.Name = T(p.Name); p.Purpose = N(p.Purpose); p.AssigneeField = N(p.AssigneeField); p.AssigneeGroup = N(p.AssigneeGroup);
                    p.DueDateField = N(p.DueDateField); p.Color = N(p.Color);
                    p.EditableSections = L(p.EditableSections); p.ReadOnlySections = L(p.ReadOnlySections); p.HiddenSections = L(p.HiddenSections);
                    p.MustCreateForms = L(p.MustCreateForms); p.MustCompleteForms = L(p.MustCompleteForms); p.ActivityForms = L(p.ActivityForms);
                    p.QuorumPercent = Math.Clamp(p.QuorumPercent, 0, 100);
                    if (p.Assignment != AssignmentMode.Group) { p.AssigneeGroup = null; p.AssignAllMembers = false; }
                    if (p.Type != PhaseType.Normal) p.IsStart = false;
                }
                foreach (var r in w.Routes)
                {
                    r.From = T(r.From); r.To = T(r.To); r.Label = N(r.Label); r.Condition = N(r.Condition); r.AllowedPrincipals = L(r.AllowedPrincipals);
                    r.SignatureMeaning = r.RequireSignature ? (r.SignatureMeaning ?? "").Trim() : null;
                    if (r.Kind != RouteKind.SpecialForward) r.AllowedPrincipals = [];
                }
            }
        }
        foreach (var v in doc.Views)
        {
            v.Key = T(v.Key); v.Name = T(v.Name); v.Category = N(v.Category) ?? "Records"; v.Forms = L(v.Forms); v.Columns = L(v.Columns); v.Phases = L(v.Phases);
            v.Status = N(v.Status)?.ToLowerInvariant() ?? "open"; v.SortBy = N(v.SortBy) ?? "number"; v.CalendarField = N(v.CalendarField);
            v.Filters = (v.Filters ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Field))
                .Select(x => new Engine.Views.ViewFilter { Field = x.Field.Trim(), Op = string.IsNullOrWhiteSpace(x.Op) ? "eq" : x.Op.Trim(), Value = N(x.Value) }).ToList();
        }
        return doc;
    }

    // ------------------------------------------------------------------ locks (DSN-004)

    private static string LockKey(string app, string component) => $"{app}|{component}";

    private bool Active(DesignerLock l) => l.LastActivityAt > DateTime.UtcNow.AddMinutes(-LockMinutes);

    /// <summary>Take (or refresh) the lock on a component. Fails while another designer holds an active lock.</summary>
    public async Task<LockResult> AcquireAsync(string appKey, string component, string label)
    {
        await EnsureSchemaAsync();
        var key = LockKey(appKey, component);
        for (var attempt = 0; ; attempt++)
        {
            var l = await Locks.FirstOrDefaultAsync(x => x.Key == key);
            if (l != null && l.UserId != me.Id && Active(l)) return new LockResult(false, l.UserName, l.AcquiredAt);
            var now = DateTime.UtcNow;
            if (l == null)
            {
                l = new DesignerLock { Key = key, AppKey = appKey, Component = component, AcquiredAt = now };
                Locks.Add(l);
            }
            else if (l.UserId != me.Id) l.AcquiredAt = now;
            l.UserId = me.Id; l.UserName = me.Name; l.Label = label; l.LastActivityAt = now;
            try
            {
                await db.SaveChangesAsync();
                return new LockResult(true, me.Name, l.AcquiredAt, true);
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<LockResult> LockStateAsync(string appKey, string component)
    {
        await EnsureSchemaAsync();
        var l = await Locks.AsNoTracking().FirstOrDefaultAsync(x => x.Key == LockKey(appKey, component));
        if (l == null || !Active(l)) return new LockResult(true);
        return new LockResult(l.UserId == me.Id, l.UserName, l.AcquiredAt, l.UserId == me.Id);
    }

    public async Task ReleaseAsync(string appKey, string component)
    {
        await EnsureSchemaAsync();
        var l = await Locks.FirstOrDefaultAsync(x => x.Key == LockKey(appKey, component));
        if (l != null && (l.UserId == me.Id || me.IsAdmin)) { Locks.Remove(l); await db.SaveChangesAsync(); }
    }

    /// <summary>DSN-004: administrators may break another designer's lock. Recorded in the configuration audit trail.</summary>
    public async Task<bool> BreakAsync(string appKey, string component)
    {
        if (!me.IsAdmin) return false;
        await EnsureSchemaAsync();
        var l = await Locks.FirstOrDefaultAsync(x => x.Key == LockKey(appKey, component));
        if (l == null) return true;
        Locks.Remove(l);
        audit.Config("Designer lock", $"{appKey}/{component}", "Lock broken", DesignDocs.Serialize(new { l.UserName, l.AcquiredAt, l.LastActivityAt }), null);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<DesignerLock>> ActiveLocksAsync(string appKey)
    {
        await EnsureSchemaAsync();
        var since = DateTime.UtcNow.AddMinutes(-LockMinutes);
        return await Locks.AsNoTracking().Where(l => l.AppKey == appKey && l.LastActivityAt > since).OrderBy(l => l.AcquiredAt).ToListAsync();
    }

    // ------------------------------------------------------------------ validation context

    public async Task<DesignContext> ContextAsync(DesignTarget t)
    {
        var locks = (await ActiveLocksAsync(t.AppKey)).Where(l => l.UserId != me.Id).ToList();
        if (t.Live == null)
            return new DesignContext { Cfg = Cfg, Dir = dir, OtherLocks = locks };
        var appId = t.Live.Id;
        var phaseIds = t.Live.Forms.SelectMany(f => f.Workflows).SelectMany(w => w.Phases).Select(p => p.Id).ToList();

        var inPhase = await db.Records.AsNoTracking().Where(r => r.AppId == appId && r.PhaseId != null)
            .GroupBy(r => new { r.PhaseId, Open = r.PhaseType == PhaseType.Normal && !r.IsArchived })
            .Select(g => new { g.Key.PhaseId, g.Key.Open, Count = g.Count() }).ToListAsync();
        var history = await db.PhaseEntries.AsNoTracking().Where(e => phaseIds.Contains(e.PhaseId))
            .GroupBy(e => e.PhaseId).Select(g => new { PhaseId = g.Key, Count = g.Select(x => x.RecordId).Distinct().Count() }).ToListAsync();
        var byForm = await db.Records.AsNoTracking().Where(r => r.AppId == appId).GroupBy(r => r.FormId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var byWf = await db.Records.AsNoTracking().Where(r => r.AppId == appId && r.WorkflowId != null).GroupBy(r => r.WorkflowId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var values = await (from v in db.RecordValues.AsNoTracking()
                            join r in db.Records on v.RecordId equals r.Id
                            where r.AppId == appId
                            group v by new { r.FormId, v.FieldKey } into g
                            select new { g.Key.FormId, g.Key.FieldKey, Count = g.Select(x => x.RecordId).Distinct().Count() }).ToListAsync();
        var files = await (from a in db.Attachments.AsNoTracking()
                           join r in db.Records on a.RecordId equals r.Id
                           where r.AppId == appId && a.FieldKey != null
                           group a by new { r.FormId, a.FieldKey } into g
                           select new { g.Key.FormId, g.Key.FieldKey, Count = g.Select(x => x.RecordId).Distinct().Count() }).ToListAsync();

        var data = new Dictionary<(int, string), int>();
        foreach (var x in values.Concat(files))
        {
            var k = (x.FormId, (x.FieldKey ?? "").ToLowerInvariant());
            data[k] = Math.Max(data.GetValueOrDefault(k), x.Count);
        }
        return new DesignContext
        {
            Cfg = Cfg, Dir = dir, Live = t.LiveDoc,
            RecordsInPhase = inPhase.GroupBy(x => x.PhaseId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Count)),
            OpenRecordsInPhase = inPhase.Where(x => x.Open).ToDictionary(x => x.PhaseId!.Value, x => x.Count),
            PhaseHistory = history.ToDictionary(x => x.PhaseId, x => x.Count),
            RecordsByForm = byForm.ToDictionary(x => x.Key, x => x.Count),
            RecordsByWorkflow = byWf.ToDictionary(x => x.Key, x => x.Count),
            FieldData = data,
            LiveChangedSinceDraft = t.Draft is { IsNewApp: false } d && t.LiveDoc != null && d.BaseHash != Hash(t.LiveDoc),
            OtherLocks = locks,
        };
    }

    /// <summary>Validate a document (reconciled against live first, the same way publishing does).</summary>
    public async Task<(AppDoc Doc, DesignContext Ctx, List<DesignIssue> Issues)> ValidateAsync(DesignTarget t)
    {
        var doc = DesignDocs.Clone(t.Doc);
        DesignDocs.Reconcile(doc, t.LiveDoc);
        var ctx = await ContextAsync(t);
        return (doc, ctx, DesignerValidator.Validate(doc, ctx));
    }
}
