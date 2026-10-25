using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Domain;
using Vellum.Modules.Portal;

namespace Vellum.Areas.Portal.Pages;

/// <summary>Every request shared with the supplier, by status and kind.</summary>
public class RequestsModel(PortalShell shell) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }

    public List<PortalItem> All { get; private set; } = [];
    public List<PortalItem> Shown { get; private set; } = [];

    public static readonly (string Key, string Label)[] Tabs = [("open", "Needs response"), ("review", "Under review"), ("closed", "Closed"), ("all", "All")];

    public static bool InTab(PortalItem i, string tab) => tab switch
    {
        "open" => i.State.ActionNeeded,
        "review" => i.Share.Status == ExternalShareStatus.Submitted,
        "closed" => i.State.Closed,
        _ => true,
    };

    public async Task OnGetAsync()
    {
        All = (await shell.ItemsAsync()).Where(i => !i.Kind.ReadOnly).ToList();
        Status = Tabs.Any(t => t.Key == Status) ? Status : "open";
        Kind = PortalKinds.Get(Kind) is { ReadOnly: false } ? Kind : null;
        Shown = All.Where(i => InTab(i, Status!) && (Kind == null || i.Kind.Key == Kind))
            .OrderBy(i => i.State.Closed).ThenBy(i => i.State.Closed ? DateOnly.MaxValue : i.Due ?? DateOnly.MaxValue).ThenByDescending(i => i.UpdatedAt).ToList();
    }
}
