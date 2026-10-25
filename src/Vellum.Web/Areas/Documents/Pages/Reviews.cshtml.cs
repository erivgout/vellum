using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Modules.Documents;

namespace Vellum.Areas.Documents.Pages;

/// <summary>DOC-120 / DOC-071: effective documents by periodic review date (overdue, within 30/60/90 days), security-trimmed (INV-081).</summary>
public class ReviewsModel(AccessService access, ConfigCache cfg, DocumentControlService svc) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Window { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<ReviewScheduleRow> Rows { get; private set; } = [];
    public Dictionary<string, int> Counts { get; } = [];
    public int OpenReviews { get; private set; }

    public static readonly (string Key, string Label)[] Windows =
        [("overdue", "Overdue"), ("30", "Within 30 days"), ("60", "Within 60 days"), ("90", "Within 90 days"), ("all", "All effective")];

    private static bool In(ReviewInfo i, string w) => w switch
    {
        "overdue" => i.Window == "overdue",
        "30" => i.DaysLeft is >= 0 and <= 30,
        "60" => i.DaysLeft is >= 0 and <= 60,
        "90" => i.DaysLeft is >= 0 and <= 90,
        _ => true,
    };

    public async Task<IActionResult> OnGetAsync()
    {
        if (!cfg.Current.AppsByKey.TryGetValue(DocKeys.App, out var app) || !access.CanOpenApp(app.Id)) return NotFound();
        Window = Windows.Any(w => w.Key == Window) ? Window : "all";
        var formId = svc.DocumentForm.Id;
        var docs = await access.Readable().AsNoTracking().Where(r => r.FormId == formId && r.IsEffective).ToListAsync();
        var all = await svc.ReviewScheduleAsync(docs);
        foreach (var (key, _) in Windows) Counts[key] = all.Count(i => In(i, key));
        OpenReviews = all.Count(i => i.OpenReview != null);
        var term = Q?.Trim();
        Rows = all.Where(i => In(i, Window!))
            .Where(i => string.IsNullOrEmpty(term) || i.Doc.Number.Contains(term, StringComparison.OrdinalIgnoreCase) || i.Doc.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.NextReview == null).ThenBy(i => i.NextReview).ThenBy(i => i.Doc.Number)
            .Select(i =>
            {
                var kw = i.TypeKey == null ? null : cfg.Current.Keyword("doc_type", i.TypeKey);
                return new ReviewScheduleRow(i, kw?.Label, kw?.Color);
            }).ToList();
        return Page();
    }
}
