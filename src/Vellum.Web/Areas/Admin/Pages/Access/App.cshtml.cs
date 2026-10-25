using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Access;

/// <summary>Application role grants (SEC-503/504) for one application, enforcing INV-084 on every change.</summary>
public class AppModel(AccessAdminService access, ConfigCache cfg) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";

    public AppDefinition App { get; private set; } = null!;
    public List<GrantRow> Grants { get; private set; } = [];
    public int ActiveManagers { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!cfg.Current.AppsByKey.TryGetValue(Key, out var app) || app.IsSystem) return NotFound();
        App = app;
        Grants = await access.GrantsAsync(app.Id);
        ActiveManagers = (await access.ActiveManagerCountsAsync()).GetValueOrDefault(app.Id);
        return Page();
    }

    private string Self => $"/admin/access/apps/{Key}";

    private int AppId => cfg.Current.AppsByKey.TryGetValue(Key, out var a) ? a.Id : 0;

    public async Task<IActionResult> OnPostAddAsync(string? kind, int? userId, int? groupId, AppRole role, string? formKey, bool designer)
    {
        var res = kind == "group"
            ? (groupId is { } g ? await access.AddGrantAsync(AppId, PrincipalKind.Group, g, role, formKey, designer) : AdminResult.Fail("Choose a group."))
            : (userId is { } u ? await access.AddGrantAsync(AppId, PrincipalKind.User, u, role, formKey, designer) : AdminResult.Fail("Choose a person."));
        return Done(res, Self);
    }

    public async Task<IActionResult> OnPostUpdateAsync(int grantId, AppRole role, bool designer) => Done(await access.UpdateGrantAsync(grantId, role, designer), Self);
    public async Task<IActionResult> OnPostRemoveAsync(int grantId) => Done(await access.RemoveGrantAsync(grantId), Self);
}
