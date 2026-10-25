using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Users;

public sealed record UserFormVm(UserInput Input, bool IsNew, bool IsSelf, List<Location> Locations, List<string> Departments);

/// <summary>Create a local user under the password policy (SEC-201), forcing a change at first sign-in by default (SEC-203).</summary>
public class NewModel(VellumDbContext db, UserAdminService users) : AdminPageModel
{
    [BindProperty] public UserInput Input { get; set; } = new();
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public bool MustChange { get; set; } = true;

    public UserFormVm Form { get; private set; } = null!;
    public SystemSettings Settings { get; private set; } = null!;

    private async Task LoadAsync()
    {
        Settings = await db.Settings.AsNoTracking().FirstAsync();
        var locations = await db.Locations.AsNoTracking().OrderBy(l => l.Path).ToListAsync();
        var depts = await db.Users.Where(u => u.Department != null).Select(u => u.Department!).Distinct().OrderBy(d => d).ToListAsync();
        Form = new UserFormVm(Input, true, false, locations, depts);
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        var res = await users.CreateAsync(Input, Password, MustChange);
        if (res.Ok) return Done(res, $"/admin/users/{res.Id}");
        Errors.AddRange(res.Errors);
        await LoadAsync();
        return Page();
    }

    public IEnumerable<string> Policy => PasswordPolicy.Describe(Settings);
}
