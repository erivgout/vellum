using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Modules.Risk;
using Vellum.Modules.Supplier;

namespace Vellum.Areas.Risk.Pages;

public sealed record HeatRisk(Guid Id, string Number, string Title, int? S, int? L, int? Score, RiskBand? Band, RiskBand? InitialBand,
    Keyword? Category, Keyword? Acceptance, int? OwnerId, PhaseDefinition? Phase, PhaseType PhaseType, DateOnly? Due);

/// <summary>
/// RSK-015: heat map of every open risk assessment the user can read (INV-081), on the current (residual when assessed)
/// or the initial position, with the register list filtered by cell or level.
/// </summary>
public class HeatMapModel(AccessService access, ConfigCache cfg, RecordReader reader) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Basis { get; set; } = "current";
    [BindProperty(SupportsGet = true)] public int? S { get; set; }
    [BindProperty(SupportsGet = true)] public int? L { get; set; }
    [BindProperty(SupportsGet = true)] public string? Level { get; set; }

    public List<HeatRisk> Risks { get; private set; } = [];
    public List<HeatRisk> Shown { get; private set; } = [];
    public int Unscored { get; private set; }
    public int ReviewsDue { get; private set; }
    public int[,] Counts { get; } = new int[RiskMatrix.Size, RiskMatrix.Size];
    public bool Initial => Basis == "initial";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!cfg.Current.AppsByKey.TryGetValue("risk", out var app) || !access.CanOpenApp(app.Id)) return NotFound();
        if (Basis is not ("current" or "initial")) Basis = "current";
        if (S is < 1 or > RiskMatrix.Size || L is < 1 or > RiskMatrix.Size) { S = null; L = null; }

        var readable = reader.OfForm("risk_assessment", access.Readable());
        var ids = await readable.Where(r => r.PhaseType == PhaseType.Normal && !r.IsArchived).Select(r => r.Id).ToListAsync();
        foreach (var r in await reader.LoadManyAsync(ids, rows: false))
        {
            int? V(string k) => RiskMatrix.Value(r.KeywordKey(k));
            var hasResidual = V("residual_severity") != null && V("residual_likelihood") != null;
            var (s, l) = Initial || !hasResidual ? (V("severity"), V("likelihood")) : (V("residual_severity"), V("residual_likelihood"));
            int? score = s is { } a && l is { } b ? RiskMatrix.Score(a, b) : null;
            int? initialScore = V("severity") is { } x && V("likelihood") is { } y ? RiskMatrix.Score(x, y) : null;
            Risks.Add(new HeatRisk(r.Id, r.Record.Number, r.Record.Title, s, l, score, RiskMatrix.BandFor(score), RiskMatrix.BandFor(initialScore),
                r.Keyword("risk_category"), r.Keyword("acceptance_status"), r.UserId("owner"), r.Phase, r.Record.PhaseType, r.Record.DueDate));
        }
        foreach (var r in Risks.Where(r => r.S != null && r.L != null)) Counts[r.S!.Value - 1, r.L!.Value - 1]++;
        Unscored = Risks.Count(r => r.Score == null);

        IEnumerable<HeatRisk> shown = Risks.Where(r => r.Score != null);
        if (S != null && L != null) shown = shown.Where(r => r.S == S && r.L == L);
        else if (RiskMatrix.Band(Level) is { } band) shown = shown.Where(r => r.Band?.Key == band.Key);
        Shown = shown.OrderByDescending(r => r.Score).ThenBy(r => r.Number).ToList();

        var completedIds = await readable.Where(r => r.PhaseType == PhaseType.Completed && !r.IsArchived).Select(r => r.Id).ToListAsync();
        ReviewsDue = (await reader.LoadManyAsync(completedIds, rows: false)).Count(r => r.KeywordKey("review_state") is "due_soon" or "overdue");
        return Page();
    }

    public int CountLevel(string key) => Risks.Count(r => r.Band?.Key == key);

    public string Link(string? basis = null, int? s = null, int? l = null, string? level = null)
    {
        var parts = new List<string>();
        var b = basis ?? Basis;
        if (b != "current") parts.Add("basis=" + b);
        if (s != null && l != null) parts.Add($"s={s}&l={l}");
        if (level != null) parts.Add("level=" + Uri.EscapeDataString(level));
        return "/risk/heatmap" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
    }
}
