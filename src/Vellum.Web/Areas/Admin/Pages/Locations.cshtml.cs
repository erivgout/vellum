using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>ADM §5.4: the location hierarchy as a tree, with an editor for the selected location.</summary>
public class LocationsModel(VellumDbContext db, LocationAdminService locations) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty(SupportsGet = true)] public bool New { get; set; }
    [BindProperty(SupportsGet = true)] public int? Parent { get; set; }
    [BindProperty] public LocationInput Input { get; set; } = new();

    public List<Location> All { get; private set; } = [];
    public Dictionary<int, int> Users { get; private set; } = [];
    public Dictionary<int, int> Records { get; private set; } = [];
    public Location? Selected { get; private set; }

    private async Task LoadAsync(bool keepInput = false)
    {
        All = await db.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync();
        Users = await db.Users.Where(u => u.PrimaryLocationId != null && !u.IsSystemIdentity && u.Status != UserStatus.Inactive)
            .GroupBy(u => u.PrimaryLocationId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        Records = await db.Records.Where(r => r.LocationId != null).GroupBy(r => r.LocationId!.Value)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        Selected = Id is { } id ? All.FirstOrDefault(l => l.Id == id) : null;
        if (keepInput) return;
        if (Selected != null) Input = LocationInput.From(Selected);
        else if (New) Input = new LocationInput { ParentId = Parent, TimeZone = All.FirstOrDefault(l => l.Id == Parent)?.TimeZone ?? "UTC" };
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (Id != null && Selected == null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var res = Id is { } id ? await locations.UpdateAsync(id, Input) : await locations.CreateAsync(Input);
        if (res.Ok) return Done(res, $"/admin/locations?id={res.Id}");
        New = Id == null;
        await LoadAsync(keepInput: true);
        Errors.AddRange(res.Errors);
        return Page();
    }

    public IEnumerable<Location> Children(int? parentId) => All.Where(l => l.ParentId == parentId).OrderBy(l => l.Name);
}
