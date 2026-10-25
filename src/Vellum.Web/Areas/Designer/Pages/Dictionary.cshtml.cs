using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

/// <summary>Data dictionary for one form (DSN-1205): every field with its type, flags, keyword list and where it's used.</summary>
public class DictionaryModel(DesignerStore store, VellumDbContext db) : DesignerPageModel(store)
{
    [BindProperty(SupportsGet = true)] public string Form { get; set; } = "";
    public FormDefinition F { get; private set; } = null!;
    public Dictionary<string, int> Data { get; private set; } = [];
    public Dictionary<string, int> Uses { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Store.Cfg.FormsByKey.TryGetValue(Form, out var f)) return NotFound();
        if (!(Store.Me.IsAdmin || (!f.App.IsSystem && Store.CanDesign(f.App)))) return Forbid();
        F = f;
        Data = await (from v in db.RecordValues.AsNoTracking()
                      join r in db.Records on v.RecordId equals r.Id
                      where r.FormId == f.Id
                      group v by v.FieldKey into g
                      select new { g.Key, Count = g.Select(x => x.RecordId).Distinct().Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        foreach (var x in f.Fields) Uses[x.Key] = EngineCatalog.WhereUsed(Store.Cfg, "field", $"{f.Key}.{x.Key}").Count;
        return Page();
    }
}
