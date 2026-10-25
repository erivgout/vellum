namespace Vellum.Engine.Security;

/// <summary>
/// The identity a request (or background job) runs as. Populated by the session middleware for web requests,
/// or switched to the system identity (SEC-806) by background jobs via <see cref="UseSystem"/>.
/// </summary>
public sealed class CurrentUser(UserDirectory directory)
{
    private bool _externalAtSignIn;

    public int Id { get; private set; }
    public bool IsAuthenticated => Id != 0;
    public bool IsSystem { get; private set; }
    public string? SessionId { get; private set; }
    public string? Ip { get; private set; }
    /// <summary>UI | API | Import | System | Script | Supplier portal (PLT-1001 source).</summary>
    public string Source { get; set; } = "UI";
    /// <summary>Name of the rule/task/escalation when acting as the system identity (INV-051).</summary>
    public string? SystemReason { get; private set; }

    public UserInfo? Info => directory.Get(Id);
    public string Name => IsSystem ? "System" + (SystemReason is { } r ? $" ({r})" : "") : Info?.DisplayName ?? "Anonymous";

    /// <summary>
    /// SEC-804: an external (supplier portal) user. True when either the session's user row (read fresh by the session handler)
    /// or the cached directory says so, so a stale cache can never turn an external user internal.
    /// </summary>
    public bool IsExternal => !IsSystem && IsAuthenticated && (_externalAtSignIn || Info?.IsExternal == true);

    /// <summary>INV-088: the organization (supplier profile record) of an external user; null for internal users.</summary>
    public Guid? ExternalOrgId => IsExternal ? Info?.ExternalOrgId : null;

    /// <summary>External users are never administrators, whatever their profile says.</summary>
    public bool IsAdmin => !IsSystem && !IsExternal && Info?.IsAdmin == true;

    public void SignIn(int userId, string sessionId, string? ip, bool isExternal = false)
    {
        Id = userId;
        SessionId = sessionId;
        Ip = ip;
        IsSystem = false;
        _externalAtSignIn = isExternal;
    }

    public void UseSystem(string reason)
    {
        Id = directory.SystemUserId;
        IsSystem = true;
        Source = "System";
        SystemReason = reason;
        _externalAtSignIn = false;
    }

    /// <summary>
    /// Act as the anonymous intake identity, so an anonymous report stores no reporter on the record or its audit trail.
    /// Unlike the system identity it gets no extra rights; it can never sign in or sign.
    /// </summary>
    public void UseAnonymousReporter(string reason)
    {
        if (directory.AnonymousReporterId == 0) throw new InvalidOperationException("The anonymous reporter identity is missing.");
        Id = directory.AnonymousReporterId;
        IsSystem = false;
        Source = "Anonymous report";
        SystemReason = reason;
    }

    /// <summary>For tests and seeding: act as a specific user without a session.</summary>
    public void Impersonate(int userId, string source = "UI")
    {
        Id = userId;
        IsSystem = false;
        Source = source;
        _externalAtSignIn = false;
    }
}
