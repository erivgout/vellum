using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Engine.Views;

namespace Vellum.Pages.Apps;

/// <summary>Application page (PLT §2.11): view navigator plus the selected view.</summary>
public class AppModel(ConfigCache cfg, AccessService access, ViewService views) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public bool? Desc { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public AppDefinition App { get; private set; } = null!;
    public ViewDefinition? Current { get; private set; }
    public ViewOutput? Result { get; private set; }
    public List<IGrouping<string, ViewDefinition>> Navigator { get; private set; } = [];
    public List<FormDefinition> Creatable { get; private set; } = [];
    public Dictionary<string, string> Filters { get; private set; } = [];
    public List<FieldDefinition> FilterFields { get; private set; } = [];
    public Dictionary<string, int> Counts { get; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!cfg.Current.AppsByKey.TryGetValue(Key, out var app) || !access.CanOpenApp(app.Id)) return NotFound();
        App = app;
        var visible = app.Views.Where(v => !v.Hidden).ToList();
        Navigator = visible.GroupBy(v => v.Category).OrderBy(g => g.Key).ToList();
        Current = visible.FirstOrDefault(v => v.Key == View) ?? visible.FirstOrDefault(v => v.IsDefault) ?? visible.FirstOrDefault();
        Creatable = app.Forms.Where(f => !f.IsActivity && !f.HiddenFromNew && access.CanCreate(f)).ToList();
        if (Current == null) return Page();

        Filters = Request.Query.Where(kv => kv.Key.StartsWith("f.") && !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key[2..], kv => kv.Value.ToString());
        var forms = views.FormsOf(Current);
        FilterFields = views.Columns(Current).Where(c => c.Field is { Type: FieldType.Keyword or FieldType.YesNo }).Select(c => c.Field!).Take(3).ToList();
        Result = await views.RunAsync(new ViewQuery { View = Current, Search = Q, Sort = Sort, Desc = Desc, Page = P, Filters = Filters });
        foreach (var v in visible.Take(30))
            Counts[v.Key] = v.Key == Current.Key && string.IsNullOrEmpty(Q) && Filters.Count == 0 ? Result.Total : await CountAsync(v);
        return Page();
    }

    private Task<int> CountAsync(ViewDefinition v) => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(views.Query(v));

    public string Link(string? view = null, string? sort = null, bool? desc = null, int? page = null, string? q = null, Dictionary<string, string>? filters = null)
    {
        var parts = new List<string>();
        var v = view ?? Current?.Key;
        if (v != null) parts.Add("view=" + Uri.EscapeDataString(v));
        var qq = q ?? (view == null ? Q : null);
        if (!string.IsNullOrEmpty(qq)) parts.Add("q=" + Uri.EscapeDataString(qq));
        var s = sort ?? (view == null ? Sort : null);
        if (!string.IsNullOrEmpty(s)) parts.Add("sort=" + s);
        var d = desc ?? (view == null && sort == null ? Desc : null);
        if (d != null) parts.Add("desc=" + (d.Value ? "true" : "false"));
        if (page is > 1) parts.Add("p=" + page);
        foreach (var (k, val) in filters ?? (view == null ? Filters : []))
            if (!string.IsNullOrEmpty(val)) parts.Add($"f.{k}={Uri.EscapeDataString(val)}");
        return $"/apps/{App.Key}" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
    }

    public static string CategoryLabel(string c) => System.Text.RegularExpressions.Regex.Replace(c, @"^\d+[\.\)]\s*", "");
}
