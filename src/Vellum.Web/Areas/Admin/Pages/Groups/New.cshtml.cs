using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Groups;

public sealed record GroupFormVm(GroupInput Input, bool IsNew, List<Location> Locations);

public class NewModel(VellumDbContext db, GroupAdminService groups) : AdminPageModel
{
    [BindProperty] public GroupInput Input { get; set; } = new();
    public GroupFormVm Form { get; private set; } = null!;

    private async Task LoadAsync() =>
        Form = new GroupFormVm(Input, true, await db.Locations.AsNoTracking().Where(l => !l.Disabled).OrderBy(l => l.Path).ToListAsync());

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        var res = await groups.CreateAsync(Input);
        if (res.Ok) return Done(res, $"/admin/groups/{res.Id}");
        Errors.AddRange(res.Errors);
        await LoadAsync();
        return Page();
    }
}
