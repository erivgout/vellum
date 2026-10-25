using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Access;

/// <summary>SEC-521 "what can this user do": effective role per application and the groups behind it.</summary>
public class UserModel(AccessAdminService access, UserDirectory dir) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }

    public UserInfo Person { get; private set; } = null!;
    public List<EffectiveAccess> Rows { get; private set; } = [];
    public List<GroupInfo> Groups { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (dir.Get(Id) is not { IsSystem: false } u) return NotFound();
        Person = u;
        Rows = await access.EffectiveAsync(u.Id);
        Groups = dir.GroupsOf(u.Id).Select(dir.Group).OfType<GroupInfo>().OrderBy(g => g.Name).ToList();
        return Page();
    }
}
