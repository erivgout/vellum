using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Access;

public sealed record AppAccessRow(AppDefinition App, int Grants, List<GrantRow> Managers, int Designers, int ActiveManagers);

/// <summary>SEC-521 access report: applications and their managers, and each group's access level per application (ADM-201).</summary>
public class IndexModel(AccessAdminService access, UserDirectory dir) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? App { get; set; }
    [BindProperty(SupportsGet = true, Name = "user")] public int? UserFilter { get; set; }

    public List<AppAccessRow> Apps { get; private set; } = [];
    public Dictionary<(int Group, int App), (AppRole Role, bool Designer, bool FormOnly)> Matrix { get; private set; } = [];
    public List<GroupInfo> Groups { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!string.IsNullOrEmpty(App)) return Redirect($"/admin/access/apps/{Uri.EscapeDataString(App)}");
        if (UserFilter is { } u) return Redirect($"/admin/access/users/{u}");
        var grants = await access.GrantsAsync();
        var counts = await access.ActiveManagerCountsAsync();
        Apps = access.Apps.Select(a =>
        {
            var g = grants.Where(x => x.Grant.AppId == a.Id).ToList();
            return new AppAccessRow(a, g.Count, g.Where(x => x.Grant.Role == AppRole.Manager && x.Grant.FormKey == null).ToList(),
                g.Count(x => x.Grant.IsDesigner), counts.GetValueOrDefault(a.Id));
        }).ToList();
        Matrix = await access.GroupMatrixAsync();
        var withGrants = Matrix.Keys.Select(k => k.Group).ToHashSet();
        Groups = dir.Groups.Where(g => withGrants.Contains(g.Id)).ToList();
        return Page();
    }
}
