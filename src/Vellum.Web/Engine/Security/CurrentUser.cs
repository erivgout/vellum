namespace Vellum.Engine.Security;

/// <summary>
/// The identity a request (or background job) runs as. Populated by the session middleware for web requests,
/// or switched to the system identity (SEC-806) by background jobs via <see cref="UseSystem"/>.
/// </summary>
public sealed class CurrentUser(UserDirectory directory)
{
    public int Id { get; private set; }
    public bool IsAuthenticated => Id != 0;
    public bool IsSystem { get; private set; }
    public string? SessionId { get; private set; }
    public string? Ip { get; private set; }
    /// <summary>UI | API | Import | System | Script (PLT-1001 source).</summary>
    public string Source { get; set; } = "UI";
    /// <summary>Name of the rule/task/escalation when acting as the system identity (INV-051).</summary>
    public string? SystemReason { get; private set; }

    public UserInfo? Info => directory.Get(Id);
    public string Name => IsSystem ? "System" + (SystemReason is { } r ? $" ({r})" : "") : Info?.DisplayName ?? "Anonymous";
    public bool IsAdmin => !IsSystem && Info?.IsAdmin == true;

    public void SignIn(int userId, string sessionId, string? ip)
    {
        Id = userId;
        SessionId = sessionId;
        Ip = ip;
        IsSystem = false;
    }

    public void UseSystem(string reason)
    {
        Id = directory.SystemUserId;
        IsSystem = true;
        Source = "System";
        SystemReason = reason;
    }

    /// <summary>For tests and seeding: act as a specific user without a session.</summary>
    public void Impersonate(int userId, string source = "UI")
    {
        Id = userId;
        IsSystem = false;
        Source = source;
    }
}
