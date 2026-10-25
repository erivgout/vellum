using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;

namespace Vellum.Engine.Security;

/// <summary>Lightweight, cached view of a user for display (names, avatars, pickers).</summary>
public sealed record UserInfo(int Id, string Username, string DisplayName, string Initials, string Email, string? JobTitle,
    string? Department, UserStatus Status, bool IsSystem, bool IsAdmin, int? ReportsToId, int? LocationId, string TimeZone)
{
    public bool IsActive => Status == UserStatus.Active && !IsSystem;
    /// <summary>INV-073: only active (or disabled-with-warning) users may get new assignments.</summary>
    public bool Assignable => Status != UserStatus.Inactive && !IsSystem;
    public string Hue => $"{(Id * 47) % 360}";
}

public sealed record GroupInfo(int Id, string Key, string Name, bool Inactive, IReadOnlyList<int> MemberIds);

/// <summary>In-memory directory of users and groups. Invalidate after any user or group change.</summary>
public sealed class UserDirectory(IServiceScopeFactory scopes)
{
    private volatile Snapshot? _snap;
    private readonly object _gate = new();

    private sealed record Snapshot(
        IReadOnlyDictionary<int, UserInfo> Users,
        IReadOnlyDictionary<string, UserInfo> ByUsername,
        IReadOnlyDictionary<int, GroupInfo> Groups,
        IReadOnlyDictionary<string, GroupInfo> GroupsByKey,
        IReadOnlyDictionary<int, IReadOnlyList<int>> GroupsOfUser,
        int SystemUserId,
        int AnonymousReporterId);

    public void Invalidate() => _snap = null;

    private Snapshot S
    {
        get
        {
            var s = _snap;
            if (s != null) return s;
            lock (_gate)
            {
                if (_snap != null) return _snap;
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<VellumDbContext>();
                var users = db.Users.AsNoTracking().ToList().Select(u => new UserInfo(u.Id, u.Username, u.DisplayName, u.Initials, u.Email,
                    u.JobTitle, u.Department, u.Status, u.IsSystemIdentity, u.IsAdministrator, u.ReportsToId, u.PrimaryLocationId, u.TimeZone)).ToList();
                var members = db.GroupMembers.AsNoTracking().ToList();
                var groups = db.Groups.AsNoTracking().ToList().Select(g => new GroupInfo(g.Id, g.Key, g.Name, g.Inactive,
                    members.Where(m => m.GroupId == g.Id).Select(m => m.UserId).ToList())).ToList();
                _snap = new Snapshot(
                    users.ToDictionary(u => u.Id),
                    users.ToDictionary(u => u.Username, StringComparer.OrdinalIgnoreCase),
                    groups.ToDictionary(g => g.Id),
                    groups.ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase),
                    members.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(m => m.GroupId).ToList()),
                    users.FirstOrDefault(u => u.IsSystem && u.Username == CoreSeed.SystemUsername)?.Id ?? users.FirstOrDefault(u => u.IsSystem)?.Id ?? 0,
                    users.FirstOrDefault(u => u.IsSystem && u.Username == CoreSeed.AnonymousUsername)?.Id ?? 0);
                return _snap;
            }
        }
    }

    public UserInfo? Get(int? id) => id is { } i && S.Users.TryGetValue(i, out var u) ? u : null;
    public UserInfo? Get(int id) => S.Users.TryGetValue(id, out var u) ? u : null;
    public UserInfo? Get(string username) => S.ByUsername.TryGetValue(username, out var u) ? u : null;
    public string Name(int? id) => Get(id)?.DisplayName ?? "Unknown user";
    public string Name(int id) => Get(id)?.DisplayName ?? "Unknown user";
    public IEnumerable<UserInfo> All => S.Users.Values.Where(u => !u.IsSystem).OrderBy(u => u.DisplayName);
    public IEnumerable<UserInfo> Assignable => All.Where(u => u.Assignable);
    public int SystemUserId => S.SystemUserId;
    /// <summary>The intake identity for anonymous reports (<see cref="CoreSeed.AnonymousUsername"/>).</summary>
    public int AnonymousReporterId => S.AnonymousReporterId;

    public GroupInfo? Group(int id) => S.Groups.TryGetValue(id, out var g) ? g : null;
    public GroupInfo? Group(string key) => S.GroupsByKey.TryGetValue(key, out var g) ? g : null;
    public IEnumerable<GroupInfo> Groups => S.Groups.Values.OrderBy(g => g.Name);
    public IReadOnlyList<int> GroupsOf(int userId) => S.GroupsOfUser.TryGetValue(userId, out var g) ? g : [];

    /// <summary>Active members of a group (PLT-620 "groups may be expanded into their active members").</summary>
    public IEnumerable<UserInfo> ActiveMembers(string groupKey) =>
        Group(groupKey) is { Inactive: false } g ? g.MemberIds.Select(Get).OfType<UserInfo>().Where(u => u.Assignable) : [];

    public bool IsMember(int userId, string groupKey) => Group(groupKey) is { } g && g.MemberIds.Contains(userId);

    /// <summary>Resolve usernames and group keys (as "@group") to active users.</summary>
    public IEnumerable<UserInfo> Resolve(IEnumerable<string> principals) =>
        principals.SelectMany(p => p.StartsWith('@') ? ActiveMembers(p[1..]) : Get(p) is { } u ? [u] : Array.Empty<UserInfo>())
            .DistinctBy(u => u.Id);
}
