using System.Globalization;

namespace Vellum.Modules.Inspections;

/// <summary>Numeric type of a criteria profile (INS-001). Controls which limits apply.</summary>
public enum NumericType { Range, Target, Min, Max }

/// <summary>Criteria, sample and record outcome (INS-012). Pending = not measured yet / not yet defined.</summary>
public enum InspectionResult { Pending, Pass, Warn, Fail }

/// <summary>Limits of a numeric criteria profile. Limits that don't apply to the type are ignored.</summary>
public sealed record NumericLimits(NumericType Type, decimal? Lsl = null, decimal? Lcl = null, decimal? Target = null,
    decimal? Ucl = null, decimal? Usl = null, decimal? Tolerance = null)
{
    /// <summary>Lower spec limit in effect: LSL (RANGE, MIN), target − tolerance (TARGET), none (MAX).</summary>
    public decimal? SpecLow => Type switch
    {
        NumericType.Target => Target is { } t && Tolerance is { } tol ? t - tol : null,
        NumericType.Max => null,
        _ => Lsl,
    };

    /// <summary>Upper spec limit in effect: USL (RANGE, MAX), target + tolerance (TARGET), none (MIN).</summary>
    public decimal? SpecHigh => Type switch
    {
        NumericType.Target => Target is { } t && Tolerance is { } tol ? t + tol : null,
        NumericType.Min => null,
        _ => Usl,
    };

    public decimal? ControlLow => Type == NumericType.Max ? null : Lcl;
    public decimal? ControlHigh => Type == NumericType.Min ? null : Ucl;
}

/// <summary>A criterion of a sample: its class (critical / major / minor) and evaluated result.</summary>
public sealed record CriterionOutcome(string Class, InspectionResult Result);

/// <summary>Sampling progress of one inspection point within an inspection record.</summary>
public sealed record PointProgress(string Point, int Total, int Done)
{
    public int Remaining => Math.Max(0, Total - Done);
}

/// <summary>
/// The inspection evaluation rules (INS-002, INS-003, INS-010, INS-014..016) as pure functions, so they can be tested
/// exhaustively and behave identically wherever they're used. No I/O, no clock, no configuration.
/// </summary>
public static class InspectionEvaluator
{
    /// <summary>Field keys of the limits that apply to a numeric type (INS-001). Tolerance only applies to TARGET.</summary>
    public static IReadOnlyList<string> ApplicableLimits(NumericType type) => type switch
    {
        NumericType.Range => ["lsl", "lcl", "target", "ucl", "usl"],
        NumericType.Target => ["lcl", "target", "ucl", "tolerance"],
        NumericType.Min => ["lsl", "lcl", "target"],
        NumericType.Max => ["target", "ucl", "usl"],
        _ => [],
    };

    /// <summary>
    /// INV-068 / INS-002: the limits that apply must satisfy LSL ≤ LCL ≤ Target ≤ UCL ≤ USL, and the type's mandatory limits
    /// must be present (RANGE: LSL and USL; MIN: LSL; MAX: USL; TARGET: target and a non-negative tolerance).
    /// Returns human-readable problems (empty when valid).
    /// </summary>
    public static List<string> ValidateLimits(NumericLimits l)
    {
        var errors = new List<string>();
        switch (l.Type)
        {
            case NumericType.Range:
                if (l.Lsl == null) errors.Add("A RANGE criterion needs a lower spec limit (LSL).");
                if (l.Usl == null) errors.Add("A RANGE criterion needs an upper spec limit (USL).");
                break;
            case NumericType.Min:
                if (l.Lsl == null) errors.Add("A MIN criterion needs a lower spec limit (LSL).");
                break;
            case NumericType.Max:
                if (l.Usl == null) errors.Add("A MAX criterion needs an upper spec limit (USL).");
                break;
            case NumericType.Target:
                if (l.Target == null) errors.Add("A TARGET criterion needs a target value.");
                if (l.Tolerance == null) errors.Add("A TARGET criterion needs a tolerance (±) around the target.");
                else if (l.Tolerance < 0) errors.Add("The tolerance can't be negative.");
                break;
        }

        // Conventional order over whichever limits are present and apply.
        var chain = new List<(string Name, decimal Value)>();
        void Add(string name, decimal? v) { if (v is { } x) chain.Add((name, x)); }
        Add(l.Type == NumericType.Target ? "Target − tolerance" : "LSL", l.SpecLow);
        Add("LCL", l.ControlLow);
        Add("Target", l.Target);
        Add("UCL", l.ControlHigh);
        Add(l.Type == NumericType.Target ? "Target + tolerance" : "USL", l.SpecHigh);
        for (var i = 1; i < chain.Count; i++)
            if (chain[i - 1].Value > chain[i].Value)
                errors.Add($"{chain[i - 1].Name} ({Fmt(chain[i - 1].Value)}) must not be greater than {chain[i].Name} ({Fmt(chain[i].Value)}): limits must satisfy LSL ≤ LCL ≤ Target ≤ UCL ≤ USL.");
        return errors;
    }

    /// <summary>
    /// INV-069 / INS-003: Fail outside the spec limits, Warn within spec but outside the control limits, Pass otherwise.
    /// The result depends only on the value and the limits. A missing value is Pending.
    /// </summary>
    public static InspectionResult Evaluate(NumericLimits l, decimal? x)
    {
        if (x is not { } v) return InspectionResult.Pending;
        if (l.SpecLow is { } lo && v < lo) return InspectionResult.Fail;
        if (l.SpecHigh is { } hi && v > hi) return InspectionResult.Fail;
        if (l.ControlLow is { } clo && v < clo) return InspectionResult.Warn;
        if (l.ControlHigh is { } chi && v > chi) return InspectionResult.Warn;
        return InspectionResult.Pass;
    }

    /// <summary>Keyword criteria (INS-001): the result configured for the observed keyword. Disabled keywords never match.</summary>
    public static InspectionResult EvaluateKeyword(IEnumerable<(string Keyword, InspectionResult Result, bool Disabled)> options, string? observed)
    {
        if (string.IsNullOrWhiteSpace(observed)) return InspectionResult.Pending;
        var hit = options.FirstOrDefault(o => !o.Disabled && string.Equals(o.Keyword.Trim(), observed.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit.Keyword == null ? InspectionResult.Pending : hit.Result;
    }

    /// <summary>
    /// INV-167 / INS-014: a sample fails if and only if a criterion whose class is among the point's sample-failure classes fails.
    /// Criteria of other classes don't affect it. While any criterion is unmeasured (and none of the failure classes failed) it's Pending.
    /// </summary>
    public static InspectionResult SampleResult(IEnumerable<CriterionOutcome> criteria, IReadOnlyCollection<string> failureClasses)
    {
        var list = criteria.ToList();
        if (list.Any(c => c.Result == InspectionResult.Fail && failureClasses.Contains(c.Class, StringComparer.OrdinalIgnoreCase)))
            return InspectionResult.Fail;
        if (list.Count == 0 || list.Any(c => c.Result == InspectionResult.Pending)) return InspectionResult.Pending;
        return InspectionResult.Pass;
    }

    /// <summary>
    /// INV-169 / INS-016: whether the sample must pass through Review. <paramref name="reviewFor"/> is one of
    /// never | warnings | failures | sample_failures | always.
    /// </summary>
    public static bool ReviewRequired(string? reviewFor, IEnumerable<InspectionResult> criteria, InspectionResult sample) => reviewFor switch
    {
        "always" => true,
        "warnings" => criteria.Any(r => r is InspectionResult.Warn or InspectionResult.Fail),
        "failures" => criteria.Any(r => r == InspectionResult.Fail),
        "sample_failures" => sample == InspectionResult.Fail,
        _ => false,
    };

    /// <summary>
    /// INS-010: total samples a point needs for a lot. sampling = full (100%) | quantity (n per lot quantity) | time (n per N minutes) | none.
    /// Never more than the lot quantity.
    /// </summary>
    public static int TotalSamples(string? sampling, decimal? sampleQty, decimal? perQuantity, decimal? perMinutes, decimal lotQuantity,
        DateTime? lotStart = null, DateTime? lotEnd = null)
    {
        var lot = (int)Math.Ceiling(Math.Max(0, lotQuantity));
        var n = (int)Math.Max(1, Math.Ceiling(sampleQty ?? 1));
        var total = sampling switch
        {
            "full" => lot,
            "quantity" => perQuantity is > 0 ? n * (int)Math.Ceiling(lot / perQuantity.Value) : n,
            "time" => perMinutes is > 0 && lotStart is { } s && lotEnd is { } e && e > s
                ? n * (int)Math.Max(1, Math.Ceiling((decimal)(e - s).TotalMinutes / perMinutes.Value))
                : n,
            "none" => 0,
            _ => n,
        };
        return Math.Min(total, lot);
    }

    /// <summary>
    /// INV-168 / INS-015: an inspection record result is defined only when remaining samples = 0 for every point.
    /// It's Fail if and only if a non-voided sample failed; otherwise Pass. Undefined results are Pending.
    /// </summary>
    public static InspectionResult RecordResult(IReadOnlyCollection<PointProgress> points, IEnumerable<InspectionResult> nonVoidedSamples)
    {
        if (points.Count == 0 || points.Any(p => p.Remaining > 0)) return InspectionResult.Pending;
        return nonVoidedSamples.Any(s => s == InspectionResult.Fail) ? InspectionResult.Fail : InspectionResult.Pass;
    }

    /// <summary>Composite description of a numeric profile (INS-001), e.g. "RANGE 3.90 … 4.10 mm (control 3.95 … 4.05, target 4.00)".</summary>
    public static string Describe(NumericLimits l, string? unit)
    {
        var u = string.IsNullOrWhiteSpace(unit) ? "" : " " + unit.Trim();
        string? control = l.ControlLow != null || l.ControlHigh != null
            ? $"control {(l.ControlLow is { } a ? Fmt(a) : "–")} … {(l.ControlHigh is { } b ? Fmt(b) : "–")}"
            : null;
        var main = l.Type switch
        {
            NumericType.Range => $"RANGE {Opt(l.Lsl)} … {Opt(l.Usl)}{u}",
            NumericType.Target => $"TARGET {Opt(l.Target)} ± {Opt(l.Tolerance)}{u}",
            NumericType.Min => $"MIN ≥ {Opt(l.Lsl)}{u}",
            NumericType.Max => $"MAX ≤ {Opt(l.Usl)}{u}",
            _ => "",
        };
        var extras = new List<string>();
        if (control != null) extras.Add(control);
        if (l.Target != null && l.Type != NumericType.Target) extras.Add($"target {Fmt(l.Target.Value)}");
        return extras.Count > 0 ? $"{main} ({string.Join(", ", extras)})" : main;
    }

    public static NumericType? ParseType(string? key) => key switch
    {
        "range" => NumericType.Range,
        "target" => NumericType.Target,
        "min" => NumericType.Min,
        "max" => NumericType.Max,
        _ => null,
    };

    public static InspectionResult ParseResult(string? key) => key switch
    {
        "pass" => InspectionResult.Pass,
        "warn" => InspectionResult.Warn,
        "fail" => InspectionResult.Fail,
        _ => InspectionResult.Pending,
    };

    /// <summary>Keyword key of a result in the <c>ins_result</c> list.</summary>
    public static string Key(InspectionResult r) => r switch
    {
        InspectionResult.Pass => "pass",
        InspectionResult.Warn => "warn",
        InspectionResult.Fail => "fail",
        _ => "pending",
    };

    private static string Opt(decimal? v) => v is { } x ? Fmt(x) : "?";
    private static string Fmt(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
