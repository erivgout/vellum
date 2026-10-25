namespace Vellum.Modules.Ehs;

/// <summary>A severe-injury reporting rule (29 CFR 1904.39).</summary>
public sealed record OshaRule(string EventKey, string EventLabel, int Hours, string Cite)
{
    /// <summary>SAF-024: every deadline shows the rule that produced it.</summary>
    public string Description => $"{Cite}: {EventLabel.ToLowerInvariant()} must be reported to OSHA within {Hours} hours. " +
                                 "Vellum starts the clock at the time of the event (SAF-023), the earliest possible deadline.";
}

public enum OshaClockState { None, Running, DueSoon, Missed, ReportedOnTime, ReportedLate }

/// <summary>
/// EHS-114 / SAF-020 / SAF-023: OSHA severe-injury reporting clocks. Calendar hours from the event date-time (stored in UTC,
/// so DST changes never shift the deadline). Business calendars don't apply to these deadlines (SAF-024).
/// </summary>
public static class OshaClock
{
    /// <summary>Warn when less than this remains.</summary>
    public static readonly TimeSpan DueSoonWindow = TimeSpan.FromHours(2);

    /// <summary>Unacknowledged clocks are escalated this often (EHS-114).</summary>
    public static readonly TimeSpan EscalationInterval = TimeSpan.FromHours(1);

    /// <summary>Entries made this long after the event are flagged as late in the audit trail (SAF-023).</summary>
    public static readonly TimeSpan LateEntryThreshold = TimeSpan.FromHours(24);

    public static readonly IReadOnlyDictionary<string, OshaRule> Rules = new Dictionary<string, OshaRule>
    {
        ["fatality"] = new("fatality", "Work-related fatality", 8, "29 CFR 1904.39(a)(1)"),
        ["inpatient"] = new("inpatient", "In-patient hospitalization", 24, "29 CFR 1904.39(a)(2)"),
        ["amputation"] = new("amputation", "Amputation", 24, "29 CFR 1904.39(a)(2)"),
        ["eye_loss"] = new("eye_loss", "Loss of an eye", 24, "29 CFR 1904.39(a)(2)"),
    };

    public static OshaRule? RuleFor(string? severeEventKey) =>
        severeEventKey != null && Rules.TryGetValue(severeEventKey, out var r) ? r : null;

    /// <summary>The reporting deadline: event time + the rule's hours, in UTC.</summary>
    public static DateTime Deadline(DateTime occurredUtc, OshaRule rule) =>
        DateTime.SpecifyKind(occurredUtc, DateTimeKind.Utc).AddHours(rule.Hours);

    public static OshaClockState State(DateTime? deadlineUtc, DateTime? reportedUtc, DateTime nowUtc)
    {
        if (deadlineUtc is not { } d) return OshaClockState.None;
        if (reportedUtc is { } r) return r <= d ? OshaClockState.ReportedOnTime : OshaClockState.ReportedLate;
        if (nowUtc > d) return OshaClockState.Missed;
        return d - nowUtc <= DueSoonWindow ? OshaClockState.DueSoon : OshaClockState.Running;
    }
}

/// <summary>RPT-702 / EHS-113: OSHA incidence rates per 200,000 hours worked (100 full-time workers for a year).</summary>
public static class OshaRates
{
    public const decimal BaseHours = 200_000m;

    /// <summary>cases × 200,000 / hours, as an exact decimal. Null when no hours are recorded (a rate is undefined).</summary>
    public static decimal? Rate(int cases, decimal hoursWorked) =>
        hoursWorked <= 0 ? null : cases * BaseHours / hoursWorked;

    /// <summary>TRIR = recordable cases × 200,000 / hours worked.</summary>
    public static decimal? Trir(int recordableCases, decimal hoursWorked) => Rate(recordableCases, hoursWorked);

    /// <summary>DART rate = days-away, restricted or transferred cases × 200,000 / hours worked.</summary>
    public static decimal? Dart(int dartCases, decimal hoursWorked) => Rate(dartCases, hoursWorked);

    /// <summary>Display rounding only (away from zero, 2 places); calculations keep the exact value.</summary>
    public static string Format(decimal? rate) =>
        rate is { } r ? decimal.Round(r, 2, MidpointRounding.AwayFromZero).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "–";

    /// <summary>A DART case: recordable with days away from work or days of job transfer or restriction.</summary>
    public static bool IsDart(bool recordable, string? classification, decimal daysAway, decimal daysRestricted) =>
        recordable && classification switch
        {
            "days_away" or "restricted" => true,
            "death" or "other_recordable" => false,
            _ => daysAway > 0 || daysRestricted > 0,
        };
}
