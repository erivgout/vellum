using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using UiH = Vellum.Infrastructure.Ui.Ui;

namespace Vellum.Modules.Admin;

public sealed record GrantRow(AppRoleGrant Grant, string PrincipalName, string? PrincipalSub, UserInfo? User, GroupInfo? Group, bool PrincipalActive);

/// <summary>A hypothetical change, used to check INV-084 before it is made.</summary>
public sealed record ManagerScenario
{
    /// <summary>Grants that stop counting as Manager grants (removed, or downgraded).</summary>
    public Func<AppRoleGrant, bool>? GrantRemoved { get; init; }
    public int? InactivatedUser { get; init; }
    public int? InactivatedGroup { get; init; }
    public (int GroupId, int UserId)? RemovedMember { get; init; }
}

public sealed record EffectiveAccess(AppDefinition App, AppRole Role, string Source, bool Designer, bool CanOpen,
    IReadOnlyList<(FormDefinition Form, AppRole Role, string Source)> FormOverrides);

/// <summary>
/// Application role grants (SEC-503/504) and the access report (SEC-521). Enforces INV-084: every application keeps
/// at least one active Manager; a change that would remove the last one is rejected.
/// </summary>
[ScopedService]
public sealed class AccessAdminService(VellumDbContext db, CurrentUser me, UserDirectory dir, ConfigCache cfg, AuditService audit)
{
    public IEnumerable<AppDefinition> Apps => cfg.Current.Apps.Where(a => !a.IsSystem);

    public async Task<List<GrantRow>> GrantsAsync(int? appId = null)
    {
        var q = db.Grants.AsNoTracking();
        if (appId is { } id) q = q.Where(g => g.AppId == id);
        var grants = await q.ToListAsync();
        return grants.Select(Describe)
            .OrderByDescending(r => r.Grant.Role).ThenBy(r => r.Grant.FormKey != null).ThenBy(r => r.Grant.PrincipalKind).ThenBy(r => r.PrincipalName)
            .ToList();
    }

    private GrantRow Describe(AppRoleGrant g)
    {
        if (g.PrincipalKind == PrincipalKind.User)
        {
            var u = dir.Get(g.PrincipalId);
            return new GrantRow(g, u?.DisplayName ?? "Unknown user", u?.JobTitle, u, null, u is { Status: not UserStatus.Inactive });
        }
        var grp = dir.Group(g.PrincipalId);
        var members = grp?.MemberIds.Count(m => dir.Get(m) is { Status: not UserStatus.Inactive }) ?? 0;
        return new GrantRow(g, grp?.Name ?? "Unknown group", grp == null ? null : UiH.Plural(members, "active member"),
            null, grp, grp is { Inactive: false } && members > 0);
    }

    /// <summary>Principals counted as an active Manager of the app (application-level Manager grants only).</summary>
    public async Task<Dictionary<int, int>> ActiveManagerCountsAsync(ManagerScenario? s = null)
    {
        var grants = await db.Grants.AsNoTracking().Where(g => g.Role == AppRole.Manager && g.FormKey == null).ToListAsync();
        var result = Apps.ToDictionary(a => a.Id, _ => 0);
        foreach (var g in grants)
        {
            if (!result.ContainsKey(g.AppId)) continue;
            if (s?.GrantRemoved?.Invoke(g) == true) continue;
            if (IsActivePrincipal(g, s)) result[g.AppId]++;
        }
        return result;
    }

    private bool IsActivePrincipal(AppRoleGrant g, ManagerScenario? s)
    {
        bool UserActive(int id) => id != s?.InactivatedUser && dir.Get(id) is { IsSystem: false, Status: not UserStatus.Inactive };
        if (g.PrincipalKind == PrincipalKind.User) return UserActive(g.PrincipalId);
        var grp = dir.Group(g.PrincipalId);
        if (grp == null || grp.Inactive || grp.Id == s?.InactivatedGroup) return false;
        return grp.MemberIds.Any(m => UserActive(m) && !(s?.RemovedMember is { } rm && rm.GroupId == grp.Id && rm.UserId == m));
    }

    /// <summary>INV-084: applications that have an active Manager now and would have none after the change.</summary>
    public async Task<List<AppDefinition>> LosesLastManagerAsync(ManagerScenario s)
    {
        var before = await ActiveManagerCountsAsync();
        var after = await ActiveManagerCountsAsync(s);
        return Apps.Where(a => before.GetValueOrDefault(a.Id) > 0 && after.GetValueOrDefault(a.Id) == 0).ToList();
    }

    public static string Inv084Message(IEnumerable<AppDefinition> apps, string what) =>
        $"{what} would leave {string.Join(", ", apps.Select(a => a.Name))} without an active Manager. Every application needs at least one (INV-084). Grant the Manager role to someone else first.";

    private async Task<string> SnapshotAsync(int appId)
    {
        var grants = await db.Grants.AsNoTracking().Where(g => g.AppId == appId).ToListAsync();
        return AdminJson.Of(grants.Select(g => new
        {
            principal = (g.PrincipalKind == PrincipalKind.User ? "user:" + (dir.Get(g.PrincipalId)?.Username ?? g.PrincipalId.ToString()) : "group:" + (dir.Group(g.PrincipalId)?.Key ?? g.PrincipalId.ToString()))
                        + (g.FormKey != null ? "/" + g.FormKey : ""),
            role = g.Role, form = g.FormKey, designer = g.IsDesigner,
        }).OrderBy(x => x.principal));
    }

    private string PrincipalLabel(PrincipalKind kind, int id) =>
        kind == PrincipalKind.User ? dir.Name(id) : $"group {dir.Group(id)?.Name ?? "?"}";

    public async Task<AdminResult> AddGrantAsync(int appId, PrincipalKind kind, int principalId, AppRole role, string? formKey, bool designer)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        if (!cfg.Current.AppsById.TryGetValue(appId, out var app) || app.IsSystem) return AdminResult.Fail("That application doesn't exist.");
        if (role is not (AppRole.Reader or AppRole.Author or AppRole.Manager)) return AdminResult.Fail("Choose Reader, Author or Manager.");
        formKey = string.IsNullOrWhiteSpace(formKey) ? null : formKey;
        if (formKey != null && app.Forms.All(f => f.Key != formKey)) return AdminResult.Fail("That form isn't part of this application.");
        if (kind == PrincipalKind.User)
        {
            if (dir.Get(principalId) is not { IsSystem: false } u) return AdminResult.Fail("Choose a person.");
            if (u.Status == UserStatus.Inactive) return AdminResult.Fail($"{u.DisplayName} is inactive and can't be granted access.");
        }
        else if (dir.Group(principalId) is not { } g) return AdminResult.Fail("Choose a group.");
        else if (g.Inactive) return AdminResult.Fail($"{g.Name} is inactive and can't be granted access.");

        var before = await SnapshotAsync(appId);
        var existing = await db.Grants.FirstOrDefaultAsync(x => x.AppId == appId && x.PrincipalKind == kind && x.PrincipalId == principalId && x.FormKey == formKey);
        var who = PrincipalLabel(kind, principalId);
        var scope = formKey != null ? $"{app.Name} › {app.Forms.First(f => f.Key == formKey).Name}" : app.Name;
        if (existing != null)
        {
            if (existing.Role == role && existing.IsDesigner == designer) return AdminResult.Fail($"{who} already has that role.");
            if (existing.Role == AppRole.Manager && role != AppRole.Manager && formKey == null &&
                await LosesLastManagerAsync(new ManagerScenario { GrantRemoved = x => x.Id == existing.Id }) is { Count: > 0 } lost)
                return AdminResult.Fail(Inv084Message(lost, "Changing this role"));
            existing.Role = role;
            existing.IsDesigner = designer;
        }
        else db.Grants.Add(new AppRoleGrant { AppId = appId, PrincipalKind = kind, PrincipalId = principalId, Role = role, FormKey = formKey, IsDesigner = designer });

        var detail = $"{AdminText.Role(role)}{(designer ? " + designer" : "")} on {scope} for {who}";
        audit.Global(existing != null ? "Role changed" : "Role granted", detail);
        audit.Security(existing != null ? "Role changed" : "Role granted", true, kind == PrincipalKind.User ? principalId : null,
            kind == PrincipalKind.User ? dir.Get(principalId)?.Username : null, detail);
        await db.SaveChangesAsync();
        audit.Config("AppGrants", app.Key, existing != null ? "Role changed" : "Role granted", before, await SnapshotAsync(appId));
        await db.SaveChangesAsync();
        return AdminResult.Success(existing != null ? $"Updated {who}'s role." : $"Granted {AdminText.Role(role)} to {who}.");
    }

    public async Task<AdminResult> UpdateGrantAsync(int grantId, AppRole role, bool designer)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var g = await db.Grants.FirstOrDefaultAsync(x => x.Id == grantId);
        if (g == null || !cfg.Current.AppsById.TryGetValue(g.AppId, out var app)) return AdminResult.Fail("That grant doesn't exist.");
        if (role is not (AppRole.Reader or AppRole.Author or AppRole.Manager)) return AdminResult.Fail("Choose Reader, Author or Manager.");
        if (g.Role == role && g.IsDesigner == designer) return AdminResult.Success("No changes.");
        if (g.Role == AppRole.Manager && role != AppRole.Manager && g.FormKey == null &&
            await LosesLastManagerAsync(new ManagerScenario { GrantRemoved = x => x.Id == grantId }) is { Count: > 0 } lost)
            return AdminResult.Fail(Inv084Message(lost, "Changing this role"));
        var before = await SnapshotAsync(g.AppId);
        var who = PrincipalLabel(g.PrincipalKind, g.PrincipalId);
        var detail = $"{who} on {app.Name}{(g.FormKey != null ? " › " + g.FormKey : "")}: {AdminText.Role(g.Role)}{(g.IsDesigner ? " + designer" : "")} → {AdminText.Role(role)}{(designer ? " + designer" : "")}";
        g.Role = role;
        g.IsDesigner = designer;
        audit.Global("Role changed", detail);
        audit.Security("Role changed", true, g.PrincipalKind == PrincipalKind.User ? g.PrincipalId : null,
            g.PrincipalKind == PrincipalKind.User ? dir.Get(g.PrincipalId)?.Username : null, detail);
        await db.SaveChangesAsync();
        audit.Config("AppGrants", app.Key, "Role changed", before, await SnapshotAsync(g.AppId));
        await db.SaveChangesAsync();
        return AdminResult.Success($"Updated {who}'s role.");
    }

    public async Task<AdminResult> RemoveGrantAsync(int grantId)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var g = await db.Grants.FirstOrDefaultAsync(x => x.Id == grantId);
        if (g == null || !cfg.Current.AppsById.TryGetValue(g.AppId, out var app)) return AdminResult.Fail("That grant doesn't exist.");
        if (g.Role == AppRole.Manager && g.FormKey == null &&
            await LosesLastManagerAsync(new ManagerScenario { GrantRemoved = x => x.Id == grantId }) is { Count: > 0 } lost)
            return AdminResult.Fail(Inv084Message(lost, "Removing this grant"));
        var before = await SnapshotAsync(g.AppId);
        var who = PrincipalLabel(g.PrincipalKind, g.PrincipalId);
        var detail = $"{AdminText.Role(g.Role)}{(g.IsDesigner ? " + designer" : "")} on {app.Name}{(g.FormKey != null ? " › " + g.FormKey : "")} removed from {who}";
        db.Grants.Remove(g);
        audit.Global("Role removed", detail);
        audit.Security("Role removed", true, g.PrincipalKind == PrincipalKind.User ? g.PrincipalId : null,
            g.PrincipalKind == PrincipalKind.User ? dir.Get(g.PrincipalId)?.Username : null, detail);
        await db.SaveChangesAsync();
        audit.Config("AppGrants", app.Key, "Role removed", before, await SnapshotAsync(g.AppId));
        await db.SaveChangesAsync();
        return AdminResult.Success($"Removed {who}'s access.");
    }

    /// <summary>
    /// SEC-521 "what can this user do": the effective role per application, computed with the same rules as
    /// <see cref="AccessService.RoleFor"/> (administrators manage everything; otherwise the highest of the default access
    /// and every grant to the user or one of their groups).
    /// </summary>
    public async Task<List<EffectiveAccess>> EffectiveAsync(int userId)
    {
        var u = dir.Get(userId);
        var groupIds = dir.GroupsOf(userId);
        var grants = await db.Grants.AsNoTracking()
            .Where(g => (g.PrincipalKind == PrincipalKind.User && g.PrincipalId == userId) || (g.PrincipalKind == PrincipalKind.Group && groupIds.Contains(g.PrincipalId)))
            .ToListAsync();
        string Source(AppRoleGrant g) => g.PrincipalKind == PrincipalKind.User ? "Direct grant" : $"Group: {dir.Group(g.PrincipalId)?.Name}";
        var list = new List<EffectiveAccess>();
        foreach (var app in Apps)
        {
            if (u?.IsAdmin == true)
            {
                list.Add(new EffectiveAccess(app, AppRole.Manager, "Administrator", true, !app.Disabled, []));
                continue;
            }
            var role = app.Disabled ? AppRole.None : app.DefaultAccess;
            var source = role > AppRole.None ? "Default access" : "No grant";
            foreach (var g in grants.Where(g => g.AppId == app.Id && g.FormKey == null).OrderByDescending(g => g.Role))
                if (g.Role > role) { role = g.Role; source = Source(g); }
            var overrides = new List<(FormDefinition, AppRole, string)>();
            foreach (var form in app.Forms)
            {
                var fg = grants.Where(g => g.AppId == app.Id && g.FormKey == form.Key).OrderByDescending(g => g.Role).FirstOrDefault();
                if (fg != null && fg.Role > role) overrides.Add((form, fg.Role, Source(fg)));
            }
            var designer = grants.Any(g => g.AppId == app.Id && g.IsDesigner);
            var canOpen = !app.Disabled && (role >= AppRole.Reader || overrides.Any(o => o.Item2 >= AppRole.Reader));
            list.Add(new EffectiveAccess(app, role, source, designer, canOpen, overrides));
        }
        return list;
    }

    /// <summary>ADM-201 "groups by access": the highest application-level role each group holds per app.</summary>
    public async Task<Dictionary<(int Group, int App), (AppRole Role, bool Designer, bool FormOnly)>> GroupMatrixAsync()
    {
        var grants = await db.Grants.AsNoTracking().Where(g => g.PrincipalKind == PrincipalKind.Group).ToListAsync();
        return grants.GroupBy(g => (g.PrincipalId, g.AppId)).ToDictionary(
            x => x.Key,
            x => (x.Where(g => g.FormKey == null).Select(g => g.Role).DefaultIfEmpty(x.Max(g => g.Role)).Max(), x.Any(g => g.IsDesigner), x.All(g => g.FormKey != null)));
    }
}
