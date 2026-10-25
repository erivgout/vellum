using Microsoft.AspNetCore.Mvc;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

/// <summary>"Where used" for a keyword list, field, form or group (DSN-1205).</summary>
public class UsageModel(DesignerStore store) : DesignerPageModel(store)
{
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "list";
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    public List<UsageRef> Refs { get; private set; } = [];
    public string Title { get; private set; } = "";

    public IActionResult OnGet()
    {
        if (!Store.IsAnyDesigner) return Forbid();
        var cfg = Store.Cfg;
        Title = Kind switch
        {
            "list" => cfg.KeywordListsByKey.TryGetValue(Key, out var l) ? $"Keyword list {l.Name}" : $"Keyword list {Key}",
            "field" => Key.Split('.', 2) is [var fk, var x] && cfg.FormsByKey.TryGetValue(fk, out var f) ? $"{f.Name} › {f.Fields.FirstOrDefault(y => y.Key == x)?.Label ?? x}" : $"Field {Key}",
            "form" => cfg.FormsByKey.TryGetValue(Key, out var ff) ? $"Form {ff.Name}" : $"Form {Key}",
            "group" => $"Group {Key}",
            _ => Key,
        };
        // Only list references in applications the user may design (administrators see everything).
        var allowed = cfg.Apps.Where(a => Store.Me.IsAdmin || Store.CanDesign(a)).Select(a => $"/designer/apps/{a.Key}/").ToList();
        Refs = EngineCatalog.WhereUsed(cfg, Kind, Key)
            .Where(r => Store.Me.IsAdmin || (r.Href != null && allowed.Any(p => r.Href.StartsWith(p, StringComparison.Ordinal))))
            .ToList();
        return Page();
    }
}
