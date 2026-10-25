namespace Vellum.Modules.Risk;

/// <summary>A risk result band (RSK-004): a score range with its label, guidance and whether it is acceptable.</summary>
public sealed record RiskBand(string Key, string Label, int Min, int Max, bool Acceptable, string Guidance)
{
    public string Letter => Label[..1];
}

/// <summary>
/// The 5 × 5 risk matrix (RSK-001..004, RSK-009). Score = severity × likelihood (RSK-003: product, made explicit).
/// Bands must not overlap and must cover every attainable score (INV-175); <see cref="Validate"/> checks both and runs
/// when the application is installed, so an invalid matrix can never be published.
/// </summary>
public static class RiskMatrix
{
    public const int Size = 5;

    public static readonly string[] Severity = ["Negligible", "Minor", "Serious", "Critical", "Catastrophic"];
    public static readonly string[] Likelihood = ["Improbable", "Remote", "Occasional", "Probable", "Frequent"];

    public static readonly string[] SeverityHelp =
    [
        "Inconvenience or temporary discomfort; no injury.",
        "Temporary injury or impairment not requiring medical intervention.",
        "Injury or impairment requiring medical intervention.",
        "Permanent impairment or life-threatening injury.",
        "Death.",
    ];

    public static readonly string[] LikelihoodHelp =
    [
        "Less than 1 in 1,000,000 uses.", "1 in 100,000 to 1 in 1,000,000.", "1 in 10,000 to 1 in 100,000.",
        "1 in 1,000 to 1 in 10,000.", "More than 1 in 1,000 uses.",
    ];

    public static readonly RiskBand[] Bands =
    [
        new("low", "Low", 1, 4, true, "Acceptable. No further action beyond routine monitoring."),
        new("medium", "Medium", 5, 9, true, "Acceptable when reduced as far as possible (ALARP). Monitor and review."),
        new("high", "High", 10, 15, false, "Unacceptable. Mitigate, or Quality must accept the residual risk with an e-signature."),
        new("critical", "Critical", 16, 25, false, "Unacceptable. Escalate now; mitigate before release or obtain an e-signed benefit-risk acceptance."),
    ];

    /// <summary>Every score the matrix can produce (products of 1..5 × 1..5).</summary>
    public static IReadOnlyList<int> Domain { get; } =
        Enumerable.Range(1, Size).SelectMany(s => Enumerable.Range(1, Size).Select(l => s * l)).Distinct().Order().ToList();

    /// <summary>Numeric value of a severity (s1..s5) or likelihood (l1..l5) keyword key.</summary>
    public static int? Value(string? key) =>
        key is { Length: 2 } && key[0] is 's' or 'l' && key[1] is >= '1' and <= '5' ? key[1] - '0' : null;

    public static int Score(int severity, int likelihood) => severity * likelihood;

    /// <summary>The band for a score, or null ("No matching results", RSK-004).</summary>
    public static RiskBand? BandFor(int? score, IReadOnlyList<RiskBand>? bands = null) =>
        score is { } s ? (bands ?? Bands).FirstOrDefault(b => s >= b.Min && s <= b.Max) : null;

    public static RiskBand? Band(string? key) => Bands.FirstOrDefault(b => b.Key == key);

    public static bool IsUnacceptable(int? score) => BandFor(score) is { Acceptable: false };

    /// <summary>Badge variant of a risk level (matches the risk_level keyword colours).</summary>
    public static string Color(string? key) => key switch { "low" => "success", "medium" => "warning", "high" => "danger", "critical" => "dark", _ => "secondary" };

    /// <summary>INV-175: overlapping bands are errors; attainable scores no band covers are reported as gaps.</summary>
    public static List<string> Validate(IReadOnlyList<RiskBand> bands, IReadOnlyList<int>? domain = null)
    {
        domain ??= Domain;
        var issues = new List<string>();
        foreach (var b in bands.Where(b => b.Min > b.Max)) issues.Add($"Band {b.Label}: the minimum ({b.Min}) is above the maximum ({b.Max}).");
        for (var i = 0; i < bands.Count; i++)
            for (var j = i + 1; j < bands.Count; j++)
            {
                var lo = Math.Max(bands[i].Min, bands[j].Min);
                var hi = Math.Min(bands[i].Max, bands[j].Max);
                if (lo <= hi) issues.Add($"Bands {bands[i].Label} and {bands[j].Label} overlap ({(lo == hi ? lo.ToString() : $"{lo}–{hi}")}).");
            }
        var gaps = domain.Where(v => !bands.Any(b => v >= b.Min && v <= b.Max)).ToList();
        if (gaps.Count > 0) issues.Add($"No band covers the score{(gaps.Count == 1 ? "" : "s")} {string.Join(", ", gaps)}: they would show \"No matching results\".");
        if (bands.GroupBy(b => b.Key).Any(g => g.Count() > 1)) issues.Add("Band keys must be unique.");
        return issues;
    }
}
