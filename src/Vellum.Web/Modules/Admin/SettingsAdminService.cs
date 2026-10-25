using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Admin;

/// <summary>The editable part of the System Preferences record (ADM-101).</summary>
public sealed class SettingsInput
{
    public string EnvironmentName { get; set; } = "";
    public EnvironmentType EnvironmentType { get; set; }
    public string? BannerText { get; set; }
    public string OrganizationName { get; set; } = "";
    public bool RegulatedMode { get; set; }
    public int SessionIdleMinutes { get; set; }
    public int SessionAbsoluteHours { get; set; }
    public int PasswordMinLength { get; set; }
    public int PasswordHistoryCount { get; set; }
    public int PasswordExpiryDays { get; set; }
    public int LockoutThreshold { get; set; }
    public bool MfaRequired { get; set; }
    public int LockTimeoutMinutes { get; set; }
    public int EscalateToManagerAfterDays { get; set; }
    public string SystemIdentityName { get; set; } = "";
    public bool GlobalSearchEnabled { get; set; }
    public string EmptyValueSymbol { get; set; } = "";
    public string LabelForward { get; set; } = "";
    public string LabelBack { get; set; } = "";
    public string LabelVoid { get; set; } = "";
    public string ESignatureCertification { get; set; } = "";
    public string? LoginMessage { get; set; }

    public static SettingsInput From(SystemSettings s) => new()
    {
        EnvironmentName = s.EnvironmentName, EnvironmentType = s.EnvironmentType, BannerText = s.BannerText, OrganizationName = s.OrganizationName,
        RegulatedMode = s.RegulatedMode, SessionIdleMinutes = s.SessionIdleMinutes, SessionAbsoluteHours = s.SessionAbsoluteHours,
        PasswordMinLength = s.PasswordMinLength, PasswordHistoryCount = s.PasswordHistoryCount, PasswordExpiryDays = s.PasswordExpiryDays,
        LockoutThreshold = s.LockoutThreshold, MfaRequired = s.MfaRequired, LockTimeoutMinutes = s.LockTimeoutMinutes,
        EscalateToManagerAfterDays = s.EscalateToManagerAfterDays, SystemIdentityName = s.SystemIdentityName, GlobalSearchEnabled = s.GlobalSearchEnabled,
        EmptyValueSymbol = s.EmptyValueSymbol, LabelForward = s.LabelForward, LabelBack = s.LabelBack, LabelVoid = s.LabelVoid,
        ESignatureCertification = s.ESignatureCertification, LoginMessage = s.LoginMessage,
    };

    public void ApplyTo(SystemSettings s)
    {
        static string T(string? v) => (v ?? "").Trim();
        static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        s.EnvironmentName = T(EnvironmentName);
        s.EnvironmentType = EnvironmentType;
        s.BannerText = N(BannerText);
        s.OrganizationName = T(OrganizationName);
        s.RegulatedMode = RegulatedMode;
        s.SessionIdleMinutes = SessionIdleMinutes;
        s.SessionAbsoluteHours = SessionAbsoluteHours;
        s.PasswordMinLength = PasswordMinLength;
        s.PasswordHistoryCount = PasswordHistoryCount;
        s.PasswordExpiryDays = PasswordExpiryDays;
        s.LockoutThreshold = LockoutThreshold;
        s.MfaRequired = MfaRequired;
        s.LockTimeoutMinutes = LockTimeoutMinutes;
        s.EscalateToManagerAfterDays = EscalateToManagerAfterDays;
        s.SystemIdentityName = T(SystemIdentityName);
        s.GlobalSearchEnabled = GlobalSearchEnabled;
        s.EmptyValueSymbol = T(EmptyValueSymbol);
        s.LabelForward = T(LabelForward);
        s.LabelBack = T(LabelBack);
        s.LabelVoid = T(LabelVoid);
        s.ESignatureCertification = T(ESignatureCertification);
        s.LoginMessage = N(LoginMessage);
    }
}

public sealed record SettingChange(string Key, string Label, string? Before, string? After, bool Sensitive);

/// <summary>
/// System Preferences (ADM §5.1). Every change needs to pass the hard limits; compliance-sensitive changes (SAF-011) also
/// need a reason and the administrator's password, and in regulated mode weakening changes are rejected (SAF-010).
/// Each save writes field-level entries to the system audit trail and one configuration audit entry (INV-145).
/// </summary>
[ScopedService]
public sealed class SettingsAdminService(VellumDbContext db, CurrentUser me, AuditService audit)
{
    public static readonly IReadOnlyList<(string Key, string Label, Func<SystemSettings, string?> Get)> Fields =
    [
        ("environmentName", "Environment name", s => s.EnvironmentName),
        ("environmentType", "Environment type", s => s.EnvironmentType.ToString()),
        ("bannerText", "Banner text", s => s.BannerText),
        ("organizationName", "Organization name", s => s.OrganizationName),
        ("regulatedMode", "Regulated mode", s => s.RegulatedMode ? "On" : "Off"),
        ("sessionIdleMinutes", "Idle session timeout (minutes)", s => s.SessionIdleMinutes.ToString()),
        ("sessionAbsoluteHours", "Absolute session lifetime (hours)", s => s.SessionAbsoluteHours.ToString()),
        ("passwordMinLength", "Minimum password length", s => s.PasswordMinLength.ToString()),
        ("passwordHistoryCount", "Password history", s => s.PasswordHistoryCount.ToString()),
        ("passwordExpiryDays", "Password expiration (days)", s => s.PasswordExpiryDays.ToString()),
        ("lockoutThreshold", "Lockout threshold", s => s.LockoutThreshold.ToString()),
        ("mfaRequired", "MFA required", s => s.MfaRequired ? "Yes" : "No"),
        ("lockTimeoutMinutes", "Edit lock timeout (minutes)", s => s.LockTimeoutMinutes.ToString()),
        ("escalateToManagerAfterDays", "Escalate to manager after (days)", s => s.EscalateToManagerAfterDays.ToString()),
        ("systemIdentityName", "System identity name", s => s.SystemIdentityName),
        ("globalSearchEnabled", "Global search", s => s.GlobalSearchEnabled ? "On" : "Off"),
        ("emptyValueSymbol", "Empty value symbol", s => s.EmptyValueSymbol),
        ("labelForward", "Forward label", s => s.LabelForward),
        ("labelBack", "Back label", s => s.LabelBack),
        ("labelVoid", "Void label", s => s.LabelVoid),
        ("eSignatureCertification", "E-signature certification", s => s.ESignatureCertification),
        ("loginMessage", "Login message", s => s.LoginMessage),
    ];

    public static string Snapshot(SystemSettings s) => AdminJson.Of(Fields.ToDictionary(f => f.Key, f => f.Get(s)));

    private static SystemSettings Clone(SystemSettings s)
    {
        var c = new SystemSettings();
        SettingsInput.From(s).ApplyTo(c);
        return c;
    }

    public static List<SettingChange> Changes(SystemSettings before, SystemSettings after) =>
        Fields.Where(f => (f.Get(before) ?? "") != (f.Get(after) ?? ""))
            .Select(f => new SettingChange(f.Key, f.Label, f.Get(before), f.Get(after), ComplianceCatalog.IsSensitive(f.Key)))
            .ToList();

    public async Task<AdminResult> SaveAsync(SettingsInput input, string? reason, string? password)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var current = await db.Settings.FirstAsync();
        var before = Clone(current);
        var proposed = Clone(current);
        input.ApplyTo(proposed);
        var changes = Changes(before, proposed);
        if (changes.Count == 0) return AdminResult.Success("No changes to save.");

        var rejected = ComplianceCatalog.RegulatedRejections(before, proposed);
        var errors = rejected.Concat(ComplianceCatalog.LimitErrors(proposed).Where(e => rejected.All(r => r.Key != e.Key))).Select(e => e.Message).ToList();
        if (errors.Count > 0) return AdminResult.Fail(errors);

        var sensitive = changes.Where(c => c.Sensitive).ToList();
        if (sensitive.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return AdminResult.Fail($"Give a reason for changing {string.Join(", ", sensitive.Select(c => c.Label.ToLowerInvariant()))}. Compliance-sensitive settings need a reason and your password (SAF-010).");
            var hash = await db.Users.Where(u => u.Id == me.Id).Select(u => u.PasswordHash).FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(password) || !PasswordHasher.Verify(password, hash))
            {
                audit.Security("Re-authentication failed", false, me.Id, me.Info?.Username, "Wrong password when changing compliance-sensitive system preferences");
                await db.SaveChangesAsync();
                return AdminResult.Fail("Your password isn't right. Compliance-sensitive changes need your password to confirm it's you.");
            }
        }

        input.ApplyTo(current);
        current.UpdatedAt = DateTime.UtcNow;
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        foreach (var c in changes)
            audit.Record(null, c.Sensitive ? "Compliance setting changed" : "System preference changed", "System preferences", reason,
                fieldKey: c.Key, fieldLabel: c.Label, oldValue: c.Before, newValue: c.After);
        if (sensitive.Count > 0)
            audit.Security("Compliance setting changed", true, me.Id, me.Info?.Username,
                string.Join("; ", sensitive.Select(c => $"{c.Label}: {Short(c.Before)} → {Short(c.After)}")) + (reason != null ? $". Reason: {reason}" : ""));
        audit.Config("SystemSettings", "system", "Updated", Snapshot(before), Snapshot(current), reason);
        await db.SaveChangesAsync();
        return AdminResult.Success($"Saved {UiPlural(changes.Count, "change")} to system preferences.");

        static string Short(string? v) => v == null ? "–" : v.Length > 40 ? v[..40] + "…" : v;
    }

    private static string UiPlural(int n, string noun) => Infrastructure.Ui.Ui.Plural(n, noun);
}

/// <summary>ADM-301: holidays and extra working days of the business calendar (weekends are Saturday and Sunday).</summary>
[ScopedService]
public sealed class CalendarAdminService(VellumDbContext db, CurrentUser me, AuditService audit)
{
    private static string Snap(CalendarDay d) => AdminJson.Of(new { date = d.Date.ToString("yyyy-MM-dd"), d.Name, working = d.IsWorking });

    public async Task<AdminResult> AddAsync(DateOnly? date, string? name, bool isWorking)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        if (date is not { } d) return AdminResult.Fail("Choose a date.");
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return AdminResult.Fail("Give the day a name, for example “Christmas Day”.");
        if (await db.CalendarDays.AnyAsync(x => x.Date == d)) return AdminResult.Fail($"{d:dd MMM yyyy} is already in the calendar.");
        var weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (isWorking && !weekend) return AdminResult.Fail($"{d:dddd dd MMM yyyy} is already a working day. Extra working days override a weekend.");
        var day = new CalendarDay { Date = d, Name = name, IsWorking = isWorking };
        db.CalendarDays.Add(day);
        audit.Global("Business calendar changed", $"Added {(isWorking ? "extra working day" : "non-working day")} {d:dd MMM yyyy}: {name}");
        audit.Config("CalendarDay", d.ToString("yyyy-MM-dd"), "Added", null, Snap(day));
        await db.SaveChangesAsync();
        return AdminResult.Success($"Added {name} ({d:dd MMM yyyy}).");
    }

    public async Task<AdminResult> RemoveAsync(int id)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var day = await db.CalendarDays.FirstOrDefaultAsync(x => x.Id == id);
        if (day == null) return AdminResult.Fail("That day isn't in the calendar.");
        db.CalendarDays.Remove(day);
        audit.Global("Business calendar changed", $"Removed {day.Date:dd MMM yyyy}: {day.Name}");
        audit.Config("CalendarDay", day.Date.ToString("yyyy-MM-dd"), "Removed", Snap(day), null);
        await db.SaveChangesAsync();
        return AdminResult.Success($"Removed {day.Name} ({day.Date:dd MMM yyyy}). Due dates calculated from now on use the change.");
    }
}
