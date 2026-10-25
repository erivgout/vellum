using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;

namespace Vellum.Areas.Admin.Pages.Groups;

public sealed record GroupRow(GroupInfo Group, string? Description, bool Training, string? Location, List<UserInfo> Active, int InactiveMembers, int Grants);

/// <summary>ADM-201 group views: by group, inactive, training groups; with member and access counts.</summary>
public class IndexModel(VellumDbContext db, UserDirectory dir) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Filter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<GroupRow> Rows { get; private set; } = [];
    public int All { get; private set; }
    public int Inactive { get; private set; }
    public int Training { get; private set; }

    public async Task OnGetAsync()
    {
        var groups = await db.Groups.AsNoTracking().ToListAsync();
        var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name);
        var grants = await db.Grants.AsNoTracking().Where(g => g.PrincipalKind == PrincipalKind.Group).GroupBy(g => g.PrincipalId)
            .Select(g => new { g.Key, N = g.Select(x => x.AppId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        All = groups.Count;
        Inactive = groups.Count(g => g.Inactive);
        Training = groups.Count(g => g.IsTrainingGroup);
        IEnumerable<Group> q = groups;
        q = Filter switch
        {
            "active" => q.Where(g => !g.Inactive),
            "inactive" => q.Where(g => g.Inactive),
            "training" => q.Where(g => g.IsTrainingGroup),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(Q))
            q = q.Where(g => g.Name.Contains(Q, StringComparison.OrdinalIgnoreCase) || g.Key.Contains(Q, StringComparison.OrdinalIgnoreCase) ||
                             (g.Description?.Contains(Q, StringComparison.OrdinalIgnoreCase) ?? false));
        Rows = q.OrderBy(g => g.Name).Select(g =>
        {
            var info = dir.Group(g.Id) ?? new GroupInfo(g.Id, g.Key, g.Name, g.Inactive, []);
            var members = info.MemberIds.Select(dir.Get).OfType<UserInfo>().ToList();
            return new GroupRow(info, g.Description, g.IsTrainingGroup, g.LocationId is { } l ? locations.GetValueOrDefault(l) : null,
                members.Where(m => m.Status != UserStatus.Inactive).OrderBy(m => m.DisplayName).ToList(), members.Count(m => m.Status == UserStatus.Inactive), grants.GetValueOrDefault(g.Id));
        }).ToList();
    }
}
