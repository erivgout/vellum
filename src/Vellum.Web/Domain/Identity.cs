namespace Vellum.Domain;

/// <summary>SEC-701.</summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    /// <summary>Argon2id hash (SEC-202). Never logged or shown.</summary>
    public string? PasswordHash { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public bool MustChangePassword { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;
    public string? StatusReason { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int? PrimaryLocationId { get; set; }
    public Location? PrimaryLocation { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string Locale { get; set; } = "en-US";
    public int? ReportsToId { get; set; }
    public User? ReportsTo { get; set; }
    public bool IsAdministrator { get; set; }
    /// <summary>SEC-806: the built-in non-interactive principal. Can never log in or sign.</summary>
    public bool IsSystemIdentity { get; set; }
    public LicenseType License { get; set; } = LicenseType.Full;
    public bool IsExternal { get; set; }
    /// <summary>SEC-612: acknowledgment of the e-signature certification.</summary>
    public DateTime? ESignatureCertifiedAt { get; set; }
    public string? TotpSecret { get; set; }
    public bool MfaEnrolled { get; set; }
    public string? DefaultAppKey { get; set; }
    /// <summary>light | dark | auto</summary>
    public string Theme { get; set; } = "auto";
    public DateTime CreatedAt { get; set; }

    public List<GroupMember> Groups { get; set; } = [];

    public string Initials =>
        string.Concat(new[] { FirstName, LastName }.Where(s => s.Length > 0).Select(s => char.ToUpperInvariant(s[0])))
            is { Length: > 0 } i ? i : (DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?");
}

/// <summary>SEC-706.</summary>
public class Group
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Inactive { get; set; }
    public bool IsTrainingGroup { get; set; }
    public int? LocationId { get; set; }
    public List<GroupMember> Members { get; set; } = [];
}

public class GroupMember
{
    public int GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
}

/// <summary>ADM-401.</summary>
public class Location
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public Location? Parent { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>Hierarchical path, e.g. "Global : North America : USA : Kalamazoo".</summary>
    public string Path { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
    public bool Disabled { get; set; }
}

/// <summary>SEC-503/504: a role on an application (or one form of it), granted to a user or group.</summary>
public class AppRoleGrant
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public AppDefinition App { get; set; } = null!;
    /// <summary>When set, the grant covers only this form.</summary>
    public string? FormKey { get; set; }
    public PrincipalKind PrincipalKind { get; set; }
    public int PrincipalId { get; set; }
    public AppRole Role { get; set; }
    /// <summary>Designer rights are granted per application (DSN-001).</summary>
    public bool IsDesigner { get; set; }
}

/// <summary>SEC-401/402: server-side session. The cookie only carries the random session ID.</summary>
public class UserSession
{
    public string Id { get; set; } = "";
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime AbsoluteExpiresAt { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}

public class PasswordHistory
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Hash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>SEC-1001.</summary>
public class SecurityEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Kind { get; set; } = "";
    public bool Success { get; set; }
    public int? UserId { get; set; }
    public string? Username { get; set; }
    public string? Ip { get; set; }
    public string? Detail { get; set; }
}

/// <summary>PLT §2.13.</summary>
public class Delegation
{
    public int Id { get; set; }
    public int RequesterId { get; set; }
    public User Requester { get; set; } = null!;
    public int DelegateId { get; set; }
    public User Delegate { get; set; } = null!;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DelegationStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}
