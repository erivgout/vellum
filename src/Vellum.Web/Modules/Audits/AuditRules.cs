using System.Globalization;

namespace Vellum.Modules.Audits;

/// <summary>A checklist question as far as scoring and the summary are concerned.</summary>
public sealed record QuestionScore(string? Classification, decimal? OutOf, decimal? Score, bool Required);

/// <summary>Audit summary (AUD-028): totals, answered questions, findings by classification and a suggested result.</summary>
public sealed record AuditSummary(decimal Total, decimal Max, decimal? Percent, int Questions, int Answered, int RequiredUnanswered,
    IReadOnlyDictionary<string, int> Counts, string SuggestedResult)
{
    public int Count(string classification) => Counts.TryGetValue(classification, out var n) ? n : 0;
    public int Findings => AuditRules.FindingClasses.Sum(Count);
    public int Nonconformances => Count("minor_nc") + Count("major_nc");
}

/// <summary>Audit scoring and summary rules as pure functions (AUD-022, AUD-023, AUD-028, INV-067).</summary>
public static class AuditRules
{
    public static readonly string[] Classifications = ["conforming", "observation", "minor_nc", "major_nc", "ofi"];
    /// <summary>Classifications that are findings (everything except Conforming).</summary>
    public static readonly string[] FindingClasses = ["observation", "minor_nc", "major_nc", "ofi"];
    public static readonly string[] NonconformanceClasses = ["minor_nc", "major_nc"];
    /// <summary>Most severe first, for sorting findings.</summary>
    public static readonly string[] SeverityOrder = ["major_nc", "minor_nc", "observation", "ofi", "conforming"];

    /// <summary>INV-067: 0 ≤ score ≤ out-of. A question without an "out of" value isn't scored, so it can't hold a score.</summary>
    public static string? ScoreError(decimal? outOf, decimal? score)
    {
        if (outOf is < 0) return "\"Out of\" can't be negative.";
        if (score is not { } s) return null;
        if (s < 0) return "The score can't be negative.";
        if (outOf is not { } max) return "This question isn't scored: set \"Out of\" first, or clear the score.";
        if (s > max) return $"The score ({Fmt(s)}) can't be more than the question's \"out of\" value ({Fmt(max)}) (INV-067).";
        return null;
    }

    /// <summary>
    /// AUD-022: a question is answered once it has a classification and, in scoring audits, a score when it's scored.
    /// Neutralized questions (not required, no "out of") never block.
    /// </summary>
    public static bool IsAnswered(QuestionScore q, bool scoring) =>
        q.Classification != null && (!scoring || q.OutOf is not > 0 || q.Score != null);

    /// <summary>
    /// AUD-028 summary. The total score sums scored questions (those with an "out of" value). The suggested result takes the
    /// worse of the score bands (scoring audits) and the findings: a major nonconformance means at best Marginal, two or more
    /// mean Unacceptable; three or more minor nonconformances mean at best Marginal.
    /// </summary>
    public static AuditSummary Summarize(IReadOnlyCollection<QuestionScore> questions, bool scoring, decimal acceptablePercent = 85, decimal marginalPercent = 70)
    {
        var scored = questions.Where(q => q.OutOf is > 0).ToList();
        var total = scored.Sum(q => q.Score ?? 0);
        var max = scored.Sum(q => q.OutOf ?? 0);
        decimal? pct = scoring && max > 0 ? Math.Round(total * 100m / max, 1) : null;
        var counts = Classifications.ToDictionary(c => c, c => questions.Count(q => q.Classification == c));
        var answered = questions.Count(q => IsAnswered(q, scoring));
        var requiredOpen = questions.Count(q => q.Required && !IsAnswered(q, scoring));

        var rank = 0; // 0 acceptable, 1 marginal, 2 unacceptable
        if (pct is { } p) rank = p >= acceptablePercent ? 0 : p >= marginalPercent ? 1 : 2;
        var majors = counts["major_nc"];
        if (majors >= 2) rank = 2;
        else if (majors == 1 || counts["minor_nc"] >= 3) rank = Math.Max(rank, 1);
        var suggested = rank switch { 0 => "acceptable", 1 => "marginal", _ => "unacceptable" };
        return new AuditSummary(total, max, pct, questions.Count, answered, requiredOpen, counts, suggested);
    }

    /// <summary>
    /// AUD-015: the next audit date from the last one, the audit type's frequency and the last frequency recommendation
    /// (increase = half the interval, decrease = 1.5×, keep = unchanged).
    /// </summary>
    public static DateOnly NextDue(DateOnly lastAudit, int frequencyMonths, string? recommendation)
    {
        var months = recommendation switch
        {
            "increase" => Math.Max(1, (int)Math.Round(frequencyMonths / 2.0, MidpointRounding.AwayFromZero)),
            "decrease" => (int)Math.Round(frequencyMonths * 1.5, MidpointRounding.AwayFromZero),
            _ => frequencyMonths,
        };
        return lastAudit.AddMonths(Math.Max(1, months));
    }

    private static string Fmt(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
