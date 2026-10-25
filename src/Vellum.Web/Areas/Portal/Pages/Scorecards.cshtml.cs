using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Modules.Portal;

namespace Vellum.Areas.Portal.Pages;

public sealed record PortalScore(int ShareId, string Number, string Period, DateOnly? Start, decimal? Overall, string? GradeKey, string? GradeLabel,
    string? Quality, string? Delivery, string? Response, string? Audit, string? Comments);

/// <summary>SUP-036: the supplier's released scorecards that were shared with the portal, read-only.</summary>
public class ScorecardsModel(PortalShell shell, PortalService portal) : PageModel
{
    public List<PortalScore> Scores { get; private set; } = [];

    public async Task OnGetAsync()
    {
        foreach (var item in (await shell.ItemsAsync()).Where(i => i.Kind.Key == PortalKinds.Scorecard))
        {
            if (await portal.ViewAsync(item.Share.Id) is not { } v) continue;
            var start = DateOnly.TryParseExact(v.Value("period_start"), "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) ? s : (DateOnly?)null;
            Scores.Add(new PortalScore(item.Share.Id, v.Number, v.Value("period") ?? v.Title, start,
                decimal.TryParse(v.Value("overall_score"), NumberStyles.Number, CultureInfo.InvariantCulture, out var o) ? o : null,
                v.Field("grade")?.KeywordKey, v.Value("grade"), v.Value("s_quality"), v.Value("s_delivery"), v.Value("s_response"), v.Value("s_audit"), v.Value("comments")));
        }
        Scores = Scores.OrderByDescending(x => x.Start ?? DateOnly.MinValue).ToList();
    }

    public static string Tone(string? grade) => grade switch { "a" => "tone-ok", "b" => "tone-info", "c" => "tone-warn", "d" => "tone-bad", _ => "tone-neutral" };
}
