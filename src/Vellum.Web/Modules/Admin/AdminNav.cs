using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Admin;

/// <summary>Sidebar entry for the Administration Center, shown to administrators only.</summary>
public sealed class AdminNavContributor(CurrentUser me) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        if (!me.IsAdmin) yield break;
        yield return new NavItem("Administration", "Administration Center", "/admin", "bi-shield-lock", Order: 900, ActivePrefix: "/admin");
    }
}

public sealed record AdminArea(string Label, string Href, string Icon, string Tone, string Description, string Refs);

public sealed record AdminCategory(int Number, string Name, IReadOnlyList<AdminArea> Areas);

/// <summary>ADM-1201: the Administration Center's numbered categories, shared by the home page and the left navigation.</summary>
public static class AdminCatalog
{
    public static readonly IReadOnlyList<AdminCategory> Categories =
    [
        new(1, "People and access",
        [
            new("Users", "/admin/users", "bi-people", "tone-info", "Accounts, status, passwords, MFA and group membership.", "SEC §4.7"),
            new("Groups", "/admin/groups", "bi-diagram-3", "tone-info", "Membership, training groups and inactive groups.", "SEC-706"),
            new("Locations", "/admin/locations", "bi-geo-alt", "tone-info", "Organization hierarchy, codes and time zones.", "ADM §5.4"),
            new("Access report", "/admin/access", "bi-key", "tone-violet", "Application roles, and what each user can do.", "SEC-521"),
        ]),
        new(2, "Configuration",
        [
            new("System preferences", "/admin/settings", "bi-sliders", "tone-brand", "Environment, sessions, passwords, labels and e-signatures.", "ADM §5.1"),
            new("Keyword lists", "/admin/keywords", "bi-tags", "tone-brand", "Shared keyword lists, their usage and orphans.", "ADM-603"),
            new("Business calendar", "/admin/calendar", "bi-calendar3", "tone-brand", "Holidays and extra working days for due dates.", "ADM-301"),
            new("Compliance posture", "/admin/compliance", "bi-shield-check", "tone-ok", "Every compliance-sensitive setting and control.", "SAF-011"),
            new("eMDR gateway", "/admin/emdr", "bi-send-check", "tone-brand", "This environment's FDA eMDR profile: account, routing IDs and certificates.", "INT-902"),
        ]),
        new(3, "Security",
        [
            new("Sessions", "/admin/sessions", "bi-laptop", "tone-warn", "Who is signed in, and forced sign-out.", "SEC-403"),
            new("Security log", "/admin/security-log", "bi-shield-exclamation", "tone-warn", "Sign-ins, lockouts, MFA and permission changes.", "SEC-1001"),
        ]),
        new(4, "Audit and logs",
        [
            new("Audit trail", "/admin/audit", "bi-clock-history", "tone-neutral", "Search every audit entry and verify the hash chains.", "PLT-1007"),
            new("Configuration audit", "/admin/audit/config", "bi-file-diff", "tone-neutral", "Before and after of every configuration change.", "DSN-1202"),
            new("Email log", "/admin/email-log", "bi-envelope", "tone-neutral", "Every notification email and its delivery status.", "ADM-1002"),
            new("Deletion history", "/admin/deletions", "bi-trash3", "tone-neutral", "Every record deletion with its snapshot and reason.", "ADM-1003"),
        ]),
        new(5, "Operations",
        [
            new("Scheduled tasks", "/admin/tasks", "bi-stopwatch", "tone-ok", "Task profiles, schedules, run now and run history.", "ADM §5.8"),
        ]),
        new(6, "Integrations",
        [
            new("SharePoint", "/admin/integrations/sharepoint", "bi-cloud-arrow-up", "tone-info", "Publish controlled files to SharePoint libraries: profiles, mappings, rules and the publication log.", "INT §6.3"),
        ]),
    ];

    public static IEnumerable<AdminArea> All => Categories.SelectMany(c => c.Areas);

    /// <summary>The area whose link is the longest prefix of the path.</summary>
    public static AdminArea? Active(string path)
    {
        path = path.ToLowerInvariant().TrimEnd('/');
        return All.Where(a => path == a.Href || path.StartsWith(a.Href + "/")).OrderByDescending(a => a.Href.Length).FirstOrDefault();
    }
}
