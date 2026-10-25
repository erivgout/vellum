using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using UiH = Vellum.Infrastructure.Ui.Ui;

namespace Vellum.Modules.Admin;

public sealed class GroupInput
{
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? LocationId { get; set; }
    public bool Inactive { get; set; }
    public bool IsTrainingGroup { get; set; }

    public static GroupInput From(Group g) => new()
    {
        Key = g.Key, Name = g.Name, Description = g.Description, LocationId = g.LocationId, Inactive = g.Inactive, IsTrainingGroup = g.IsTrainingGroup,
    };
}

/// <summary>
/// SEC-706 groups: display name, location scope, members (with inactive members shown separately), training-group and
/// inactive flags. Groups are never deleted; they are made inactive. Membership changes are permission changes (SEC-1001).
/// </summary>
[ScopedService]
public sealed class GroupAdminService(VellumDbContext db, CurrentUser me, UserDirectory dir, AuditService audit, AccessAdminService access)
{
    private async Task<string> SnapAsync(int groupId)
    {
        var g = await db.Groups.AsNoTracking().Include(x => x.Members).FirstAsync(x => x.Id == groupId);
        var loc = g.LocationId is { } l ? await db.Locations.Where(x => x.Id == l).Select(x => x.Code).FirstOrDefaultAsync() : null;
        var users = await db.Users.AsNoTracking().Where(u => g.Members.Select(m => m.UserId).Contains(u.Id)).Select(u => u.Username).ToListAsync();
        return AdminJson.Of(new { g.Key, g.Name, g.Description, location = loc, g.Inactive, trainingGroup = g.IsTrainingGroup, members = users.Order().ToList() });
    }

    private async Task<List<string>> ValidateAsync(GroupInput i, Group? existing)
    {
        var errors = new List<string>();
        i.Name = i.Name?.Trim();
        i.Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description.Trim();
        if (string.IsNullOrEmpty(i.Name)) errors.Add("Enter a group name.");
        else if (await db.Groups.AnyAsync(g => g.Name == i.Name && g.Id != (existing == null ? 0 : existing.Id))) errors.Add($"A group called “{i.Name}” already exists.");
        if (existing == null)
        {
            i.Key = string.IsNullOrWhiteSpace(i.Key) ? AdminText.Slug(i.Name) : i.Key.Trim().ToLowerInvariant();
            if (!AdminText.KeyPattern.IsMatch(i.Key ?? "")) errors.Add("The key uses lowercase letters, digits, dashes and underscores.");
            else if (await db.Groups.AnyAsync(g => g.Key == i.Key)) errors.Add($"The key “{i.Key}” is already used by another group.");
        }
        if (i.LocationId is { } loc && !await db.Locations.AnyAsync(l => l.Id == loc)) errors.Add("Choose a location from the list.");
        return errors;
    }

    public async Task<AdminResult> CreateAsync(GroupInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var errors = await ValidateAsync(input, null);
        if (errors.Count > 0) return AdminResult.Fail(errors);
        var g = new Group { Key = input.Key!, Name = input.Name!, Description = input.Description, LocationId = input.LocationId, Inactive = input.Inactive, IsTrainingGroup = input.IsTrainingGroup };
        db.Groups.Add(g);
        await db.SaveChangesAsync();
        audit.Global("Group created", $"{g.Name} ({g.Key})");
        audit.Config("Group", g.Key, "Created", null, await SnapAsync(g.Id));
        await db.SaveChangesAsync();
        dir.Invalidate();
        return AdminResult.Success($"Created {g.Name}.", g.Id);
    }

    public async Task<AdminResult> UpdateAsync(int id, GroupInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id);
        if (g == null) return AdminResult.Fail("That group doesn't exist.");
        var errors = await ValidateAsync(input, g);
        if (errors.Count > 0) return AdminResult.Fail(errors);
        if (!g.Inactive && input.Inactive && await access.LosesLastManagerAsync(new ManagerScenario { InactivatedGroup = g.Id }) is { Count: > 0 } lost)
            return AdminResult.Fail(AccessAdminService.Inv084Message(lost, $"Making {g.Name} inactive"));

        var before = await SnapAsync(g.Id);
        var changes = new List<string>();
        if (g.Name != input.Name) changes.Add($"name “{g.Name}” → “{input.Name}”");
        if (g.Description != input.Description) changes.Add("description");
        if (g.LocationId != input.LocationId) changes.Add("location scope");
        if (g.Inactive != input.Inactive) changes.Add(input.Inactive ? "made inactive" : "made active");
        if (g.IsTrainingGroup != input.IsTrainingGroup) changes.Add(input.IsTrainingGroup ? "marked as training group" : "no longer a training group");
        if (changes.Count == 0) return AdminResult.Success("No changes to save.", g.Id);

        g.Name = input.Name!;
        g.Description = input.Description;
        g.LocationId = input.LocationId;
        g.Inactive = input.Inactive;
        g.IsTrainingGroup = input.IsTrainingGroup;
        audit.Global("Group updated", $"{g.Name} ({g.Key}): {string.Join("; ", changes)}");
        await db.SaveChangesAsync();
        audit.Config("Group", g.Key, "Updated", before, await SnapAsync(g.Id));
        await db.SaveChangesAsync();
        dir.Invalidate();
        return AdminResult.Success($"Saved {g.Name}.", g.Id);
    }

    public async Task<AdminResult> AddMembersAsync(int groupId, IReadOnlyList<int> userIds)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var g = await db.Groups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == groupId);
        if (g == null) return AdminResult.Fail("That group doesn't exist.");
        var add = userIds.Distinct().Where(id => g.Members.All(m => m.UserId != id)).ToList();
        if (add.Count == 0) return AdminResult.Fail("Choose at least one person who isn't already a member.");
        var bad = add.Where(id => dir.Get(id) is not { IsSystem: false, Status: not UserStatus.Inactive }).ToList();
        if (bad.Count > 0) return AdminResult.Fail($"{string.Join(", ", bad.Select(dir.Name))} can't be added: inactive users can't join groups (SEC-704).");

        var before = await SnapAsync(g.Id);
        foreach (var id in add) g.Members.Add(new GroupMember { GroupId = g.Id, UserId = id });
        var names = string.Join(", ", add.Select(dir.Name));
        audit.Global("Group membership changed", $"Added to {g.Name}: {names}");
        foreach (var id in add) audit.Security("Group membership changed", true, id, dir.Get(id)?.Username, $"Added to {g.Name} by {me.Name}");
        await db.SaveChangesAsync();
        audit.Config("Group", g.Key, "Members added", before, await SnapAsync(g.Id));
        await db.SaveChangesAsync();
        dir.Invalidate();
        return AdminResult.Success(add.Count == 1 ? $"Added {names} to {g.Name}." : $"Added {UiH.Plural(add.Count, "member")} to {g.Name}.", g.Id);
    }

    public async Task<AdminResult> RemoveMemberAsync(int groupId, int userId)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var g = await db.Groups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == groupId);
        var m = g?.Members.FirstOrDefault(x => x.UserId == userId);
        if (g == null || m == null) return AdminResult.Fail("That person isn't a member of the group.");
        if (await access.LosesLastManagerAsync(new ManagerScenario { RemovedMember = (groupId, userId) }) is { Count: > 0 } lost)
            return AdminResult.Fail(AccessAdminService.Inv084Message(lost, $"Removing {dir.Name(userId)} from {g.Name}"));
        var before = await SnapAsync(g.Id);
        g.Members.Remove(m);
        db.GroupMembers.Remove(m);
        audit.Global("Group membership changed", $"Removed from {g.Name}: {dir.Name(userId)}");
        audit.Security("Group membership changed", true, userId, dir.Get(userId)?.Username, $"Removed from {g.Name} by {me.Name}");
        await db.SaveChangesAsync();
        audit.Config("Group", g.Key, "Member removed", before, await SnapAsync(g.Id));
        await db.SaveChangesAsync();
        dir.Invalidate();
        return AdminResult.Success($"Removed {dir.Name(userId)} from {g.Name}.", g.Id);
    }

    /// <summary>Add one user to several groups (from the user page).</summary>
    public async Task<AdminResult> AddToGroupsAsync(int userId, IReadOnlyList<int> groupIds)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        if (groupIds.Count == 0) return AdminResult.Fail("Choose at least one group.");
        var added = new List<string>();
        foreach (var gid in groupIds.Distinct())
        {
            var res = await AddMembersAsync(gid, [userId]);
            if (!res.Ok) return added.Count > 0 ? new AdminResult(false, res.Errors, $"Added to {string.Join(", ", added)}.") : res;
            added.Add(dir.Group(gid)?.Name ?? "group");
        }
        return AdminResult.Success($"Added {dir.Name(userId)} to {string.Join(", ", added)}.");
    }
}

public sealed class LocationInput
{
    public int? ParentId { get; set; }
    public string? Name { get; set; }
    public string? Code { get; set; }
    public string? TimeZone { get; set; }
    public bool Disabled { get; set; }

    public static LocationInput From(Location l) => new() { ParentId = l.ParentId, Name = l.Name, Code = l.Code, TimeZone = l.TimeZone, Disabled = l.Disabled };
}

/// <summary>ADM §5.4: the location hierarchy. Paths are recomputed for the whole subtree when a location is renamed or moved.</summary>
[ScopedService]
public sealed class LocationAdminService(VellumDbContext db, CurrentUser me, AuditService audit)
{
    public static string Snapshot(Location l, IReadOnlyDictionary<int, Location> all) => AdminJson.Of(new
    {
        l.Name, l.Code, parent = l.ParentId is { } p && all.TryGetValue(p, out var pl) ? pl.Code : null, l.Path, l.TimeZone, l.Disabled,
    });

    private static IEnumerable<Location> Descendants(Location root, List<Location> all)
    {
        foreach (var c in all.Where(x => x.ParentId == root.Id))
        {
            yield return c;
            foreach (var d in Descendants(c, all)) yield return d;
        }
    }

    private static void Repath(Location l, List<Location> all)
    {
        var parent = l.ParentId is { } p ? all.FirstOrDefault(x => x.Id == p) : null;
        l.Path = parent == null ? l.Name : $"{parent.Path} : {l.Name}";
        foreach (var c in all.Where(x => x.ParentId == l.Id)) Repath(c, all);
    }

    private async Task<List<string>> ValidateAsync(LocationInput i, Location? existing, List<Location> all)
    {
        var errors = new List<string>();
        i.Name = i.Name?.Trim();
        i.Code = i.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(i.Name)) errors.Add("Enter a name.");
        else if (i.Name.Contains(" : ")) errors.Add("Names can't contain “ : ” (it separates the levels of a path).");
        if (string.IsNullOrEmpty(i.Code)) errors.Add("Enter a short code, for example KZO.");
        else if (!System.Text.RegularExpressions.Regex.IsMatch(i.Code, "^[A-Z0-9_-]{1,12}$")) errors.Add("Codes use up to 12 letters, digits, dashes or underscores.");
        else if (all.Any(l => l.Code == i.Code && l.Id != (existing?.Id ?? 0))) errors.Add($"The code {i.Code} is already used.");
        i.TimeZone = string.IsNullOrWhiteSpace(i.TimeZone) ? "UTC" : i.TimeZone;
        if (!UserAdminService.TimeZones.Contains(i.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(i.TimeZone, out _)) errors.Add("Choose a time zone from the list.");
        if (i.ParentId is { } p)
        {
            if (all.All(l => l.Id != p)) errors.Add("Choose a parent location from the list.");
            else if (existing != null && (p == existing.Id || Descendants(existing, all).Any(d => d.Id == p)))
                errors.Add("A location can't be moved under itself or one of its own sub-locations.");
        }
        await Task.CompletedTask;
        return errors;
    }

    public async Task<AdminResult> CreateAsync(LocationInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var all = await db.Locations.ToListAsync();
        var errors = await ValidateAsync(input, null, all);
        if (errors.Count > 0) return AdminResult.Fail(errors);
        var l = new Location { ParentId = input.ParentId, Name = input.Name!, Code = input.Code!, TimeZone = input.TimeZone!, Disabled = input.Disabled };
        all.Add(l);
        Repath(l, all);
        db.Locations.Add(l);
        await db.SaveChangesAsync();
        audit.Global("Location created", l.Path);
        audit.Config("Location", l.Code, "Created", null, Snapshot(l, all.ToDictionary(x => x.Id)));
        await db.SaveChangesAsync();
        return AdminResult.Success($"Created {l.Name}.", l.Id);
    }

    public async Task<AdminResult> UpdateAsync(int id, LocationInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var all = await db.Locations.ToListAsync();
        var l = all.FirstOrDefault(x => x.Id == id);
        if (l == null) return AdminResult.Fail("That location doesn't exist.");
        var errors = await ValidateAsync(input, l, all);
        if (errors.Count > 0) return AdminResult.Fail(errors);
        var byId = all.ToDictionary(x => x.Id);
        var before = Snapshot(l, byId);
        var oldPath = l.Path;
        var wasDisabled = l.Disabled;
        l.ParentId = input.ParentId;
        l.Name = input.Name!;
        l.Code = input.Code!;
        l.TimeZone = input.TimeZone!;
        l.Disabled = input.Disabled;
        Repath(l, all);
        var after = Snapshot(l, byId);
        if (before == after) return AdminResult.Success("No changes to save.", l.Id);
        var moved = Descendants(l, all).Count();
        audit.Global("Location updated", oldPath == l.Path ? l.Path : $"{oldPath} → {l.Path}" + (moved > 0 ? $" ({UiH.Plural(moved, "sub-location")} re-pathed)" : "")
            + (wasDisabled != l.Disabled ? (l.Disabled ? "; disabled" : "; enabled") : ""));
        audit.Config("Location", l.Code, "Updated", before, after);
        await db.SaveChangesAsync();
        return AdminResult.Success($"Saved {l.Name}.", l.Id);
    }
}
