using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Keywords;

/// <summary>ADM-603/606: every master keyword list, the fields and applications that use it, and orphaned lists.</summary>
public class IndexModel(ConfigCache cfg, KeywordAdminService keywords) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Filter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<(KeywordList List, List<KeywordUse> Uses)> Rows { get; private set; } = [];
    public int All { get; private set; }
    public int Orphaned { get; private set; }

    public void OnGet()
    {
        var usage = keywords.Usage();
        var lists = cfg.Current.KeywordListsById.Values.OrderBy(l => l.Name).Select(l => (l, usage.GetValueOrDefault(l.Key) ?? [])).ToList();
        All = lists.Count;
        Orphaned = lists.Count(x => x.Item2.Count == 0);
        IEnumerable<(KeywordList, List<KeywordUse>)> q = lists;
        if (Filter == "used") q = q.Where(x => x.Item2.Count > 0);
        else if (Filter == "orphaned") q = q.Where(x => x.Item2.Count == 0);
        if (!string.IsNullOrWhiteSpace(Q))
            q = q.Where(x => x.Item1.Name.Contains(Q, StringComparison.OrdinalIgnoreCase) || x.Item1.Key.Contains(Q, StringComparison.OrdinalIgnoreCase) ||
                             x.Item1.Keywords.Any(k => k.Label.Contains(Q, StringComparison.OrdinalIgnoreCase)));
        Rows = q.ToList();
    }
}
