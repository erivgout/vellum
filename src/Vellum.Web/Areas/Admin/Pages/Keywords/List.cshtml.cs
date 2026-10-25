using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Keywords;

/// <summary>Edit one keyword list (ADM-603..607): add, rename with warning, color, order, disable.</summary>
public class ListModel(VellumDbContext db, KeywordAdminService keywords) : AdminPageModel
{
    public static readonly (string Value, string Label)[] Colors =
        [("", "Neutral"), ("success", "Green"), ("warning", "Amber"), ("danger", "Red"), ("info", "Blue"), ("violet", "Violet"), ("secondary", "Grey"), ("dark", "Ink")];

    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }

    public KeywordList List { get; private set; } = null!;
    public List<Keyword> Ordered { get; private set; } = [];
    public List<KeywordUse> Uses { get; private set; } = [];
    public Dictionary<int, int> Counts { get; private set; } = [];
    public string? EditLabel { get; private set; }
    public string? EditColor { get; private set; }
    public bool NeedsConfirm { get; private set; }
    public List<string> RowErrors { get; } = [];

    private async Task<bool> LoadAsync()
    {
        var list = await db.KeywordLists.AsNoTracking().Include(l => l.Keywords).FirstOrDefaultAsync(l => l.Key == Key);
        if (list == null) return false;
        List = list;
        Ordered = list.Keywords.OrderBy(k => k.Order).ThenBy(k => k.Label).ToList();
        Uses = keywords.Usage().GetValueOrDefault(Key) ?? [];
        Counts = await keywords.ValueCountsAsync(Key);
        return true;
    }

    private string Self => $"/admin/keywords/{Key}";

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostDetailsAsync(string? name, string? description) =>
        Done(await keywords.UpdateListAsync(Key, name, description), Self);

    public async Task<IActionResult> OnPostAddAsync(string? label, string? keywordKey, string? color, string? customColor)
    {
        var res = await keywords.AddKeywordAsync(Key, label, keywordKey, color == "custom" ? customColor : color);
        return Done(res, Self + "#keywords");
    }

    public async Task<IActionResult> OnPostUpdateAsync(int keywordId, string? label, string? color, string? customColor, bool confirmRename)
    {
        var c = color == "custom" ? customColor : color;
        var res = await keywords.UpdateKeywordAsync(Key, keywordId, label, c, confirmRename);
        if (res.Ok) return Done(res, Self + "#keywords");
        if (!await LoadAsync()) return NotFound();
        Edit = keywordId;
        EditLabel = label;
        EditColor = c;
        NeedsConfirm = res.Error.Contains("ADM-607");
        RowErrors.AddRange(res.Errors);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(int keywordId, bool disabled) =>
        Done(await keywords.SetDisabledAsync(Key, keywordId, disabled), Self + "#keywords");

    public async Task<IActionResult> OnPostMoveAsync(int keywordId, int delta)
    {
        var res = await keywords.MoveAsync(Key, keywordId, delta);
        return res.Ok ? Redirect(Self + "#kw-" + keywordId) : Done(res, Self + "#keywords");
    }
}
