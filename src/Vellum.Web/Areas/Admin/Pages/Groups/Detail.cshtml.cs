using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Groups;

/// <summary>SEC-706: one group with its settings, members, inactive members and application access.</summary>
public class DetailModel(VellumDbContext db, GroupAdminService groups, AccessAdminService access, UserDirectory dir, ConfigCache cfg) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty] public GroupInput Input { get; set; } = new();

    public Group G { get; private set; } = null!;
    public GroupFormVm Form { get; private set; } = null!;
    public List<UserInfo> Members { get; private set; } = [];
    public List<UserInfo> InactiveMembers { get; private set; } = [];
    public List<GrantRow> Grants { get; private set; } = [];
    public ConfigModel Config => cfg.Current;

    private async Task<bool> LoadAsync(bool keepInput = false)
    {
        var g = await db.Groups.AsNoTracking().Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == Id);
        if (g == null) return false;
        G = g;
        if (!keepInput) Input = GroupInput.From(g);
        else Input.Key = g.Key;
        var locations = await db.Locations.AsNoTracking().Where(l => !l.Disabled || l.Id == g.LocationId).OrderBy(l => l.Path).ToListAsync();
        Form = new GroupFormVm(Input, false, locations);
        var members = g.Members.Select(m => dir.Get(m.UserId)).OfType<UserInfo>().OrderBy(u => u.DisplayName).ToList();
        Members = members.Where(m => m.Status != UserStatus.Inactive).ToList();
        InactiveMembers = members.Where(m => m.Status == UserStatus.Inactive).ToList();
        Grants = (await access.GrantsAsync()).Where(r => r.Grant.PrincipalKind == PrincipalKind.Group && r.Grant.PrincipalId == g.Id).ToList();
        return true;
    }

    private string Self => $"/admin/groups/{Id}";

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var res = await groups.UpdateAsync(Id, Input);
        if (res.Ok) return Done(res, Self);
        if (!await LoadAsync(keepInput: true)) return NotFound();
        Errors.AddRange(res.Errors);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(int[]? users) => Done(await groups.AddMembersAsync(Id, users ?? []), Self + "#members");
    public async Task<IActionResult> OnPostRemoveAsync(int userId) => Done(await groups.RemoveMemberAsync(Id, userId), Self + "#members");
}
