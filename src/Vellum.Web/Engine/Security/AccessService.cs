using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;

namespace Vellum.Engine.Security;

public sealed record AccessDecision(bool Allowed, string? Reason = null, int? OnBehalfOfId = null)
{
    public static readonly AccessDecision Yes = new(true);
    public static AccessDecision No(string reason) => new(false, reason);
}

/// <summary>
/// The authorization model (SEC §4.5). Every server-side operation must go through this service:
/// UI hiding never counts as access control (SEC-519, INV-082). Deny by default (INV-083).
/// External (supplier portal) users get no application roles at all: they read exactly the records shared with their
/// organization (<see cref="ExternalShare"/>, INV-088) and never edit through the engine's normal rights (SEC-804).
/// </summary>
public sealed class AccessService(VellumDbContext db, CurrentUser me, UserDirectory dir, ConfigCache cfg)
{
    private Dictionary<(int App, string? Form), AppRole>? _grants;
    private HashSet<int>? _designerApps;
    private List<int>? _delegators;

    public bool IsAdmin => me.IsAdmin;
    public int UserId => me.Id;

    private void EnsureGrants()
    {
        if (_grants != null) return;
        _grants = [];
        _designerApps = [];
        if (!me.IsAuthenticated) return;
        var groupIds = dir.GroupsOf(me.Id);
        var rows = db.Grants.AsNoTracking()
            .Where(g => (g.PrincipalKind == PrincipalKind.User && g.PrincipalId == me.Id) ||
                        (g.PrincipalKind == PrincipalKind.Group && groupIds.Contains(g.PrincipalId)))
            .ToList();
        foreach (var g in rows)
        {
            var key = (g.AppId, g.FormKey);
            if (!_grants.TryGetValue(key, out var r) || g.Role > r) _grants[key] = g.Role;
            if (g.IsDesigner) _designerApps.Add(g.AppId);
        }
    }

    /// <summary>SEC-504/505: the highest applicable grant wins; administrators manage every application.</summary>
    public AppRole RoleFor(int appId, string? formKey = null)
    {
        if (me.IsSystem || me.IsAdmin) return AppRole.Manager;
        if (!me.IsAuthenticated || me.IsExternal) return AppRole.None; // SEC-804: no default access, no grants
        EnsureGrants();
        var role = cfg.Current.AppsById.TryGetValue(appId, out var app) && !app.Disabled ? app.DefaultAccess : AppRole.None;
        if (_grants!.TryGetValue((appId, null), out var a) && a > role) role = a;
        if (formKey != null && _grants.TryGetValue((appId, formKey), out var f) && f > role) role = f;
        return role;
    }

    public bool CanOpenApp(int appId) =>
        cfg.Current.AppsById.TryGetValue(appId, out var app) && !app.Disabled &&
        (RoleFor(appId) >= AppRole.Reader || app.Forms.Any(f => RoleFor(appId, f.Key) >= AppRole.Reader));

    public bool IsDesigner(int appId)
    {
        if (me.IsExternal) return false;
        if (me.IsAdmin) return true;
        EnsureGrants();
        return _designerApps!.Contains(appId);
    }

    public bool IsAnyDesigner { get { if (me.IsExternal) return false; if (me.IsAdmin) return true; EnsureGrants(); return _designerApps!.Count > 0; } }

    /// <summary>SEC-506: creation rights.</summary>
    public bool CanCreate(FormDefinition form) =>
        !form.Disabled && !form.App.Disabled && RoleFor(form.AppId, form.Key) >= AppRole.Author;

    public bool CanSeeSensitive(int appId) => RoleFor(appId) >= AppRole.Manager;

    /// <summary>Users who have an active delegation to the current user (PLT-1302).</summary>
    public IReadOnlyList<int> DelegatorIds
    {
        get
        {
            if (_delegators != null) return _delegators;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            _delegators = me.IsAuthenticated && !me.IsSystem && !me.IsExternal
                ? db.Delegations.AsNoTracking()
                    .Where(d => d.DelegateId == me.Id && d.Status == DelegationStatus.Active && d.StartDate <= today && d.EndDate >= today)
                    .Select(d => d.RequesterId).ToList()
                : [];
            return _delegators;
        }
    }

    /// <summary>Form IDs the user can read at all.</summary>
    public List<int> ReadableFormIds() =>
        cfg.Current.FormsById.Values.Where(f => !f.App.Disabled && RoleFor(f.AppId, f.Key) >= AppRole.Reader).Select(f => f.Id).ToList();

    /// <summary>
    /// The security-trimmed record set (INV-081). Views, search, reports, lookups and APIs must start from here.
    /// SEC-507: when a record has a Readers list, only listed principals, editors/managers, assignees, creators and app managers see it.
    /// </summary>
    public IQueryable<Record> Readable()
    {
        if (me.IsSystem || me.IsAdmin) return db.Records;
        if (!me.IsAuthenticated) return db.Records.Where(_ => false);
        if (me.IsExternal) return ExternalReadable();
        var formIds = ReadableFormIds();
        var managerForms = cfg.Current.FormsById.Values.Where(f => RoleFor(f.AppId, f.Key) >= AppRole.Manager).Select(f => f.Id).ToList();
        var uid = me.Id;
        var gids = dir.GroupsOf(uid).ToList();
        return db.Records.Where(r => formIds.Contains(r.FormId) && (
            managerForms.Contains(r.FormId) ||
            r.CreatedById == uid ||
            !db.RecordAccess.Any(a => a.RecordId == r.Id && a.Level == RecordAccessLevel.Reader) ||
            db.RecordAccess.Any(a => a.RecordId == r.Id &&
                ((a.PrincipalKind == PrincipalKind.User && a.PrincipalId == uid) ||
                 (a.PrincipalKind == PrincipalKind.Group && gids.Contains(a.PrincipalId)))) ||
            db.Assignments.Any(a => a.RecordId == r.Id && a.UserId == uid && a.Status == AssignmentStatus.Active)));
    }

    /// <summary>
    /// INV-088: an external user reads only records shared (and not withdrawn) with their own organization. No organization
    /// means nothing. Grants, readers lists, assignments and creator rights never apply to external users.
    /// </summary>
    private IQueryable<Record> ExternalReadable()
    {
        if (me.ExternalOrgId is not { } org) return db.Records.Where(_ => false);
        return db.Records.Where(r => db.Set<ExternalShare>().Any(s =>
            s.RecordId == r.Id && s.OrgId == org && s.Status != ExternalShareStatus.Withdrawn));
    }

    public Task<bool> CanReadAsync(Guid recordId) => Readable().AnyAsync(r => r.Id == recordId);

    /// <summary>Active assignment of the current user, or of someone who delegated to them (PLT-604, INV-070).</summary>
    public async Task<Assignment?> ActingAssignmentAsync(Guid recordId)
    {
        if (!me.IsAuthenticated || me.IsSystem || me.IsExternal) return null;
        var ids = DelegatorIds.Append(me.Id).ToList();
        var mine = await db.Assignments.Where(a => a.RecordId == recordId && a.Status == AssignmentStatus.Active && ids.Contains(a.UserId))
            .ToListAsync();
        return mine.FirstOrDefault(a => a.UserId == me.Id) ?? mine.FirstOrDefault();
    }

    /// <summary>SEC-509: edit rights outside forced read mode.</summary>
    public async Task<AccessDecision> CanEditAsync(Record r)
    {
        if (r.IsArchived) return AccessDecision.No("Archived records are read-only.");
        if (r.PhaseType != PhaseType.Normal && r.WorkflowId != null)
            return AccessDecision.No($"This record is {r.PhaseType.ToString().ToLowerInvariant()} and read-only.");
        if (me.IsSystem) return AccessDecision.Yes;
        // SEC-804: supplier portal writes are made by the portal service after it has checked the share and the fields meant
        // for the supplier; the engine's normal edit rights never apply to external users.
        if (me.IsExternal) return AccessDecision.No("Supplier portal users respond through the portal.");
        var role = RoleFor(r.AppId, cfg.Current.FormsById[r.FormId].Key);
        if (role == AppRole.Manager) return AccessDecision.Yes;
        if (role < AppRole.Reader) return AccessDecision.No("You don't have access to this application.");

        var acting = await ActingAssignmentAsync(r.Id);
        if (acting != null && role >= AppRole.Author)
            return new AccessDecision(true, null, acting.UserId == me.Id ? null : acting.UserId);

        var gids = dir.GroupsOf(me.Id).ToList();
        var listed = await db.RecordAccess.AnyAsync(a => a.RecordId == r.Id && a.Level >= RecordAccessLevel.Editor &&
            ((a.PrincipalKind == PrincipalKind.User && a.PrincipalId == me.Id) || (a.PrincipalKind == PrincipalKind.Group && gids.Contains(a.PrincipalId))));
        if (listed && role >= AppRole.Author) return AccessDecision.Yes;

        if (r.WorkflowId == null && role >= AppRole.Author) return AccessDecision.Yes; // non-workflow profiles
        return AccessDecision.No(acting == null ? "Only the current assignee can edit this record." : "You have read-only access to this application.");
    }

    /// <summary>SEC-508: who can edit the record security section.</summary>
    public async Task<bool> CanManageSecurityAsync(Record r)
    {
        if (me.IsExternal) return false;
        if (RoleFor(r.AppId) == AppRole.Manager) return true;
        var gids = dir.GroupsOf(me.Id).ToList();
        return await db.RecordAccess.AnyAsync(a => a.RecordId == r.Id && a.Level == RecordAccessLevel.Manager &&
            ((a.PrincipalKind == PrincipalKind.User && a.PrincipalId == me.Id) || (a.PrincipalKind == PrincipalKind.Group && gids.Contains(a.PrincipalId))));
    }

    /// <summary>Check whether an arbitrary user (e.g. an assignee candidate) can read a record (INV-080).</summary>
    public bool UserCanReadForm(int userId, FormDefinition form)
    {
        var u = dir.Get(userId);
        if (u == null || u.IsSystem || u.IsExternal) return false; // SEC-804: never a candidate for internal work
        if (u.IsAdmin) return true;
        var gids = dir.GroupsOf(userId);
        var role = form.App.DefaultAccess;
        var grants = db.Grants.AsNoTracking().Where(g => g.AppId == form.AppId && (g.FormKey == null || g.FormKey == form.Key) &&
            ((g.PrincipalKind == PrincipalKind.User && g.PrincipalId == userId) || (g.PrincipalKind == PrincipalKind.Group && gids.Contains(g.PrincipalId))))
            .Select(g => g.Role).ToList();
        if (grants.Count > 0 && grants.Max() > role) role = grants.Max();
        return role >= AppRole.Reader;
    }
}
