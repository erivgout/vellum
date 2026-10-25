using System.Globalization;

namespace Vellum.Modules.Supplier;

/// <summary>A scorecard criterion: its weight (%) and its measure converted to a 0–100 score (null = not applicable).</summary>
public sealed record CriterionScore(string Key, string Label, decimal Weight, decimal? Score)
{
    public bool Applicable => Score != null;
}

public sealed record ScorecardResult(IReadOnlyList<CriterionScore> Criteria, decimal? Overall, string? Grade, IReadOnlyList<string> Errors)
{
    /// <summary>INV-095: effective weight after redistributing not-applicable weight proportionally.</summary>
    public decimal EffectiveWeight(CriterionScore c)
    {
        var applicable = Criteria.Where(x => x.Applicable).Sum(x => x.Weight);
        return !c.Applicable || applicable == 0 ? 0 : Scoring.R(c.Weight * 100m / applicable);
    }
}

/// <summary>Grade band on the 0–100 overall score. Bands are contiguous and don't overlap (validated at startup).</summary>
public sealed record GradeBand(string Key, string Label, decimal Min, decimal Max, string Guidance);

/// <summary>
/// Supplier scorecard arithmetic (SUP §15.5, simplified). Everything is exact <see cref="decimal"/> arithmetic, rounded
/// half away from zero to 2 places only at the end of each step, so the same inputs always give the same score.
/// </summary>
public static class Scoring
{
    public const string Quality = "quality", Delivery = "delivery", Response = "response", Audit = "audit";

    public static readonly GradeBand[] Grades =
    [
        new("a", "A · Preferred", 90m, 100m, "Preferred supplier: eligible for skip-lot and reduced surveillance."),
        new("b", "B · Approved", 80m, 89.99m, "Approved: maintain normal surveillance."),
        new("c", "C · Improvement needed", 70m, 79.99m, "Improvement plan required at the next business review."),
        new("d", "D · Probation", 0m, 69.99m, "Probation: issue a SCAR and consider conditional status."),
    ];

    public static decimal R(decimal d) => decimal.Round(d, 2, MidpointRounding.AwayFromZero);

    /// <summary>0 ppm scores 100; every 50 ppm costs one point; 5,000 ppm or more scores 0.</summary>
    public static decimal QualityScore(decimal ppm) => R(Math.Clamp(100m - ppm / 50m, 0m, 100m));

    public static decimal DeliveryScore(decimal onTimePct) => R(Math.Clamp(onTimePct, 0m, 100m));

    /// <summary>Responsiveness rating 1–5 → 20–100.</summary>
    public static decimal ResponseScore(decimal rating) => R(Math.Clamp(rating, 0m, 5m) * 20m);

    public static decimal AuditScore(decimal pct) => R(Math.Clamp(pct, 0m, 100m));

    /// <summary>INV-094: the criteria weights total exactly 100%.</summary>
    public static string? WeightError(decimal wq, decimal wd, decimal wr, decimal wa)
    {
        if (wq < 0 || wd < 0 || wr < 0 || wa < 0) return "Criteria weights can't be negative.";
        var sum = wq + wd + wr + wa;
        return sum == 100m ? null : $"Criteria weights must total 100% (they total {sum.ToString("0.##", CultureInfo.InvariantCulture)}%).";
    }

    public static ScorecardResult Calculate(decimal? ppm, decimal? onTimePct, decimal? responsiveness, decimal? auditPct,
        decimal wq, decimal wd, decimal wr, decimal wa)
    {
        var errors = new List<string>();
        if (WeightError(wq, wd, wr, wa) is { } we) errors.Add(we);
        var criteria = new List<CriterionScore>
        {
            new(Quality, "Quality (ppm)", wq, ppm is { } p ? QualityScore(p) : null),
            new(Delivery, "On-time delivery", wd, onTimePct is { } o ? DeliveryScore(o) : null),
            new(Response, "Responsiveness", wr, responsiveness is { } r ? ResponseScore(r) : null),
            new(Audit, "Audit", wa, auditPct is { } a ? AuditScore(a) : null),
        };
        decimal? overall = null;
        var applicableWeight = criteria.Where(c => c.Applicable).Sum(c => c.Weight);
        if (errors.Count == 0 && applicableWeight > 0)
            overall = R(criteria.Where(c => c.Applicable).Sum(c => c.Weight * c.Score!.Value) / applicableWeight); // INV-095
        return new ScorecardResult(criteria, overall, overall is { } s ? GradeFor(s)?.Key : null, errors);
    }

    public static GradeBand? GradeFor(decimal score)
    {
        var s = R(score);
        return Grades.FirstOrDefault(g => s >= g.Min && s <= g.Max);
    }

    /// <summary>Bands must not overlap and must cover 0–100 at 0.01 resolution.</summary>
    public static List<string> ValidateGrades(IReadOnlyList<GradeBand> bands)
    {
        var issues = new List<string>();
        var ordered = bands.OrderBy(b => b.Min).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Min > ordered[i].Max) issues.Add($"Grade {ordered[i].Label}: minimum is above maximum.");
            if (i > 0 && ordered[i].Min <= ordered[i - 1].Max) issues.Add($"Grades {ordered[i - 1].Label} and {ordered[i].Label} overlap.");
            if (i > 0 && ordered[i].Min > ordered[i - 1].Max + 0.01m) issues.Add($"Scores between {ordered[i - 1].Max} and {ordered[i].Min} have no grade.");
        }
        if (ordered.Count == 0 || ordered[0].Min > 0m) issues.Add("Scores from 0 have no grade.");
        if (ordered.Count == 0 || ordered[^1].Max < 100m) issues.Add("Scores up to 100 have no grade.");
        return issues;
    }
}
