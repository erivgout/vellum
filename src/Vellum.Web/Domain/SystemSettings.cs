namespace Vellum.Domain;

/// <summary>ADM-101: one tenant-wide settings record (Id = 1).</summary>
public class SystemSettings
{
    public int Id { get; set; } = 1;
    public string EnvironmentName { get; set; } = "Development";
    public EnvironmentType EnvironmentType { get; set; } = EnvironmentType.Development;
    public string? BannerText { get; set; }
    /// <summary>SAF-010.</summary>
    public bool RegulatedMode { get; set; } = true;
    public string OrganizationName { get; set; } = "Acme Medical Devices";

    // Sessions (SEC-401)
    public int SessionIdleMinutes { get; set; } = 30;
    public int SessionAbsoluteHours { get; set; } = 12;

    // Passwords (SEC-201)
    public int PasswordMinLength { get; set; } = 12;
    public int PasswordHistoryCount { get; set; } = 3;
    public int PasswordExpiryDays { get; set; } = 90;
    public int LockoutThreshold { get; set; } = 5;
    public bool MfaRequired { get; set; }

    // Records and workflow
    public int LockTimeoutMinutes { get; set; } = 30;
    public int EscalateToManagerAfterDays { get; set; } = 3;
    public string SystemIdentityName { get; set; } = "System";
    public bool GlobalSearchEnabled { get; set; } = true;
    public string EmptyValueSymbol { get; set; } = "–";

    // Routing labels (ADM-105)
    public string LabelForward { get; set; } = "Send";
    public string LabelBack { get; set; } = "Back";
    public string LabelVoid { get; set; } = "Void";

    /// <summary>SEC-612.</summary>
    public string ESignatureCertification { get; set; } =
        "I certify that my electronic signature in this system is the legally binding equivalent of my handwritten signature, in accordance with 21 CFR Part 11.";

    public string? LoginMessage { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>ADM-301: business calendar exceptions.</summary>
public class CalendarDay
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
    /// <summary>True = extra working day (overrides a weekend); false = holiday / non-working day.</summary>
    public bool IsWorking { get; set; }
}

/// <summary>ADM §5.8: task profiles.</summary>
public class TaskProfile
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public string? LastOutcome { get; set; }
    /// <summary>INV-121: at most one run at a time.</summary>
    public DateTime? LeaseUntil { get; set; }
}

public class TaskRun
{
    public long Id { get; set; }
    public int TaskId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Outcome { get; set; } = "Running";
    public int RecordsAffected { get; set; }
    public string? Log { get; set; }
    public bool Manual { get; set; }
}
