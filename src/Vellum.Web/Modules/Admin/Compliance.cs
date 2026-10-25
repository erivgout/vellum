using Vellum.Domain;

namespace Vellum.Modules.Admin;

/// <summary>ok | warn | bad | info</summary>
public sealed record PostureStatus(string Level, string Note)
{
    public static PostureStatus Ok(string note) => new("ok", note);
    public static PostureStatus Warn(string note) => new("warn", note);
    public static PostureStatus Bad(string note) => new("bad", note);
    public static PostureStatus Info(string note) => new("info", note);
}

/// <summary>A setting that changes how the system meets its regulatory obligations (SAF-011).</summary>
public sealed record SensitiveSetting(string Key, string Label, string Reference, string Effect,
    Func<SystemSettings, string> Display, Func<SystemSettings, PostureStatus> Evaluate);

/// <summary>
/// SAF-010/011: the catalog of compliance-sensitive system preferences, their hard limits (SEC-201, SEC-401, SEC-702)
/// and the stricter floors that apply in regulated mode.
/// </summary>
public static class ComplianceCatalog
{
    public const int RegulatedMinPasswordLength = 12;
    public const int RegulatedMaxAbsoluteHours = 12;
    public const int RegulatedMaxLockoutThreshold = 5;
    public const int RegulatedMaxPasswordExpiryDays = 90;

    public static readonly IReadOnlyList<SensitiveSetting> Settings =
    [
        new("regulatedMode", "Regulated mode", "SAF-010",
            "When on, configuration that weakens compliance controls (password, session and signature controls) is rejected.",
            s => s.RegulatedMode ? "On" : "Off",
            s => s.RegulatedMode ? PostureStatus.Ok("Weakening changes are rejected.") : PostureStatus.Warn("Weakening changes are allowed with a reason and re-authentication.")),
        new("environmentType", "Environment type", "ADM-103, SAF-012",
            "Non-production environments always show a banner and prefix notification subjects, so test data can't be mistaken for production.",
            s => $"{s.EnvironmentType} ({s.EnvironmentName})",
            s => s.EnvironmentType == EnvironmentType.Production
                ? PostureStatus.Ok("Production: no banner, no subject prefix.")
                : PostureStatus.Ok($"Banner always shown; emails prefixed [{s.EnvironmentName.ToUpperInvariant()}].")),
        new("sessionIdleMinutes", "Idle session timeout", "SEC-401, 21 CFR 11.300",
            "Signs out inactive sessions so an unattended workstation can't be used to act as someone else.",
            s => $"{s.SessionIdleMinutes} minutes",
            s => s.SessionIdleMinutes <= 30 ? PostureStatus.Ok("Within 3–30 minutes.") : PostureStatus.Bad("Above the 30-minute maximum.")),
        new("sessionAbsoluteHours", "Absolute session lifetime", "SEC-401",
            "Ends every session after a fixed time, even when it is active.",
            s => $"{s.SessionAbsoluteHours} hours",
            s => s.SessionAbsoluteHours <= RegulatedMaxAbsoluteHours ? PostureStatus.Ok($"At most {RegulatedMaxAbsoluteHours} hours.") : PostureStatus.Warn($"Longer than the recommended {RegulatedMaxAbsoluteHours} hours.")),
        new("passwordMinLength", "Minimum password length", "SEC-201, 21 CFR 11.300(a)",
            "Longer passwords resist guessing. Vellum's floor is 8; regulated tenants need at least 12.",
            s => $"{s.PasswordMinLength} characters",
            s => s.PasswordMinLength >= RegulatedMinPasswordLength ? PostureStatus.Ok($"At least {RegulatedMinPasswordLength}.") : PostureStatus.Warn($"Below the regulated minimum of {RegulatedMinPasswordLength}.")),
        new("passwordHistoryCount", "Password history", "SEC-201",
            "Stops people re-using recent passwords.",
            s => $"Last {s.PasswordHistoryCount} passwords",
            s => s.PasswordHistoryCount >= 3 ? PostureStatus.Ok("At least 3.") : PostureStatus.Bad("Below the minimum of 3.")),
        new("passwordExpiryDays", "Password expiration", "SEC-201, NIST SP 800-63B",
            "Forces periodic password changes. It may be off only when MFA is required for every local user.",
            s => s.PasswordExpiryDays == 0 ? "Off" : $"{s.PasswordExpiryDays} days",
            s => s.PasswordExpiryDays == 0
                ? (s.MfaRequired ? PostureStatus.Ok("Off, with MFA required for everyone (NIST SP 800-63B).") : PostureStatus.Bad("Off without MFA: not allowed."))
                : s.PasswordExpiryDays <= RegulatedMaxPasswordExpiryDays ? PostureStatus.Ok($"At most {RegulatedMaxPasswordExpiryDays} days.") : PostureStatus.Warn($"Longer than {RegulatedMaxPasswordExpiryDays} days.")),
        new("lockoutThreshold", "Account lockout threshold", "SEC-702, 21 CFR 11.300(d)",
            "Disables an account after repeated failed sign-ins or signing attempts, and notifies the manager.",
            s => $"{s.LockoutThreshold} failed attempts",
            s => s.LockoutThreshold <= RegulatedMaxLockoutThreshold ? PostureStatus.Ok($"At most {RegulatedMaxLockoutThreshold}.") : PostureStatus.Warn($"More than {RegulatedMaxLockoutThreshold} attempts allowed.")),
        new("mfaRequired", "Multi-factor authentication required", "SEC-301",
            "Requires an authenticator app in addition to the password for local accounts.",
            s => s.MfaRequired ? "Required" : "Optional",
            s => s.MfaRequired ? PostureStatus.Ok("Every local user enrolls at next sign-in.") : PostureStatus.Warn("Optional. Recommended for regulated tenants.")),
        new("eSignatureCertification", "Electronic signature certification", "SEC-612, 21 CFR 11.100",
            "The statement every user acknowledges before their first electronic signature.",
            s => s.ESignatureCertification.Length > 90 ? s.ESignatureCertification[..90] + "…" : s.ESignatureCertification,
            s => string.IsNullOrWhiteSpace(s.ESignatureCertification) ? PostureStatus.Bad("Missing.") : PostureStatus.Ok("Set.")),
    ];

    public static SensitiveSetting? Find(string key) => Settings.FirstOrDefault(s => s.Key == key);
    public static bool IsSensitive(string key) => Settings.Any(s => s.Key == key);

    /// <summary>Hard limits that apply in every mode (SEC-201 "only stricter", SEC-401, SEC-702). Keyed by setting.</summary>
    public static List<(string Key, string Message)> LimitErrors(SystemSettings s)
    {
        var e = new List<(string Key, string Message)>();
        void Add(string key, string message) => e.Add((key, message));
        if (string.IsNullOrWhiteSpace(s.EnvironmentName) || s.EnvironmentName.Length > 40) Add("environmentName", "Environment name: enter up to 40 characters.");
        if (string.IsNullOrWhiteSpace(s.OrganizationName) || s.OrganizationName.Length > 120) Add("organizationName", "Organization name: enter up to 120 characters.");
        if (s.BannerText?.Length > 120) Add("bannerText", "Banner text: keep it under 120 characters.");
        if (s.SessionIdleMinutes is < 3 or > 30) Add("sessionIdleMinutes", "Idle session timeout must be between 3 and 30 minutes (SEC-401).");
        if (s.SessionAbsoluteHours is < 1 or > 24) Add("sessionAbsoluteHours", "Absolute session lifetime must be between 1 and 24 hours.");
        if (s.PasswordMinLength is < 8 or > 128) Add("passwordMinLength", "Minimum password length must be between 8 and 128. Password rules can only be made stricter than the floor of 8 (SEC-201).");
        if (s.PasswordHistoryCount is < 3 or > 24) Add("passwordHistoryCount", "Password history must be between 3 and 24 (SEC-201).");
        if (s.PasswordExpiryDays is < 0 or > 365) Add("passwordExpiryDays", "Password expiration must be between 1 and 365 days, or 0 for off.");
        else if (s.PasswordExpiryDays == 0 && !s.MfaRequired) Add("passwordExpiryDays", "Password expiration can be turned off only when MFA is required for all local users (SEC-201).");
        if (s.LockoutThreshold is < 3 or > 10) Add("lockoutThreshold", "Lockout threshold must be between 3 and 10 failed attempts (SEC-702).");
        if (s.LockTimeoutMinutes is < 5 or > 480) Add("lockTimeoutMinutes", "Edit lock timeout must be between 5 and 480 minutes.");
        if (s.EscalateToManagerAfterDays is < 1 or > 60) Add("escalateToManagerAfterDays", "Escalate to the manager after 1 to 60 days.");
        if (string.IsNullOrWhiteSpace(s.SystemIdentityName) || s.SystemIdentityName.Length > 40) Add("systemIdentityName", "System identity name: enter up to 40 characters.");
        if (string.IsNullOrWhiteSpace(s.EmptyValueSymbol) || s.EmptyValueSymbol.Length > 5) Add("emptyValueSymbol", "Empty value symbol: enter 1 to 5 characters.");
        foreach (var (key, label, v) in new[] { ("labelForward", "Forward", s.LabelForward), ("labelBack", "Back", s.LabelBack), ("labelVoid", "Void", s.LabelVoid) })
            if (string.IsNullOrWhiteSpace(v) || v.Length > 30) Add(key, $"{label} label: enter up to 30 characters.");
        if (string.IsNullOrWhiteSpace(s.ESignatureCertification) || s.ESignatureCertification.Trim().Length < 40)
            Add("eSignatureCertification", "The electronic signature certification needs at least 40 characters (SEC-612).");
        if (s.LoginMessage?.Length > 1000) Add("loginMessage", "Login message: keep it under 1,000 characters.");
        return e;
    }

    /// <summary>
    /// SAF-010: in regulated mode (before or after the change), changes that lower password or session controls below the
    /// regulated floors are rejected. Only settings that change are checked, except when regulated mode is being turned on,
    /// when every floor must already hold. Turning regulated mode off is itself allowed (with a reason and re-authentication),
    /// but a weakening change in the same save is still rejected, so the two are always separate, recorded decisions.
    /// </summary>
    public static List<(string Key, string Message)> RegulatedRejections(SystemSettings before, SystemSettings after)
    {
        var e = new List<(string Key, string Message)>();
        if (!before.RegulatedMode && !after.RegulatedMode) return e;
        var turningOn = !before.RegulatedMode && after.RegulatedMode;
        bool Changed<T>(Func<SystemSettings, T> f) => turningOn || !EqualityComparer<T>.Default.Equals(f(before), f(after));
        const string why = "is rejected in regulated mode (SAF-010)";
        if (Changed(s => s.PasswordMinLength) && after.PasswordMinLength < RegulatedMinPasswordLength)
            e.Add(("passwordMinLength", $"A minimum password length below {RegulatedMinPasswordLength} characters {why}."));
        if (Changed(s => s.SessionIdleMinutes) && after.SessionIdleMinutes > 30)
            e.Add(("sessionIdleMinutes", $"An idle session timeout above 30 minutes {why}."));
        if (Changed(s => s.SessionAbsoluteHours) && after.SessionAbsoluteHours > RegulatedMaxAbsoluteHours)
            e.Add(("sessionAbsoluteHours", $"An absolute session lifetime above {RegulatedMaxAbsoluteHours} hours {why}."));
        if (Changed(s => s.LockoutThreshold) && after.LockoutThreshold > RegulatedMaxLockoutThreshold)
            e.Add(("lockoutThreshold", $"Allowing more than {RegulatedMaxLockoutThreshold} failed sign-in attempts {why}."));
        if (Changed(s => s.PasswordHistoryCount) && after.PasswordHistoryCount < 3)
            e.Add(("passwordHistoryCount", $"Remembering fewer than 3 previous passwords {why}."));
        if (Changed(s => s.PasswordExpiryDays) && after.PasswordExpiryDays > RegulatedMaxPasswordExpiryDays)
            e.Add(("passwordExpiryDays", $"Password expiration longer than {RegulatedMaxPasswordExpiryDays} days {why}."));
        if ((Changed(s => s.PasswordExpiryDays) || Changed(s => s.MfaRequired)) && after.PasswordExpiryDays == 0 && !after.MfaRequired)
            e.Add(("passwordExpiryDays", $"Turning off password expiration without requiring MFA {why}."));
        return e;
    }
}
