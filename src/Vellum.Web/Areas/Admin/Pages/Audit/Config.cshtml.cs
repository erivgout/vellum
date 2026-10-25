using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Audit;

/// <summary>DSN-1202 / INV-145: the configuration audit trail with each entry's before/after shown as a readable diff.</summary>
public class ConfigModel(VellumDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    [BindProperty(SupportsGet = true, Name = "user")] public string? UserFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<ConfigAuditEntry> Result { get; private set; } = null!;
    public List<(string Type, int Count)> Types { get; private set; } = [];
    public bool Filtered => Type != null || UserFilter != null || Key != null || From != null || To != null;

    public async Task OnGetAsync()
    {
        var q = db.ConfigAudit.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Type)) q = q.Where(c => c.ObjectType == Type);
        if (!string.IsNullOrWhiteSpace(UserFilter)) q = q.Where(c => c.UserName.Contains(UserFilter.Trim()));
        if (!string.IsNullOrWhiteSpace(Key)) q = q.Where(c => c.ObjectKey.Contains(Key.Trim()) || c.Action.Contains(Key.Trim()));
        if (From is { } f) { var t = f.ToDateTime(TimeOnly.MinValue); q = q.Where(c => c.At >= t); }
        if (To is { } to) { var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(c => c.At < t); }
        Result = await q.OrderByDescending(c => c.Id).PageAsync(P, 25);
        Types = (await db.ConfigAudit.GroupBy(c => c.ObjectType).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
            .OrderBy(x => x.Key).Select(x => (x.Key, x.N)).ToList();
    }

    public static string Label(string type) => type switch
    {
        "SystemSettings" => "System preferences",
        "KeywordList" => "Keyword list",
        "AppGrants" => "Application roles",
        "TaskProfile" => "Scheduled task",
        "CalendarDay" => "Business calendar",
        _ => System.Text.RegularExpressions.Regex.Replace(type, "(?<=[a-z])([A-Z])", " $1"),
    };

    public static string Icon(string type) => type switch
    {
        "SystemSettings" => "bi-sliders",
        "KeywordList" => "bi-tags",
        "AppGrants" => "bi-key",
        "TaskProfile" => "bi-stopwatch",
        "CalendarDay" => "bi-calendar3",
        "User" => "bi-person",
        "Group" => "bi-diagram-3",
        "Location" => "bi-geo-alt",
        "Application" => "bi-grid",
        _ => "bi-gear",
    };
}
