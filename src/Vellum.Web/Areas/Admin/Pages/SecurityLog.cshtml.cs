using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>SEC-1001: filterable security log with CSV export (for SIEM hand-off, SEC-1002).</summary>
public class SecurityLogModel(VellumDbContext db, AuditService audit) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public string? Outcome { get; set; }
    [BindProperty(SupportsGet = true, Name = "user")] public string? UserFilter { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<SecurityEvent> Result { get; private set; } = null!;
    public List<string> Kinds { get; private set; } = [];
    public int Failures { get; private set; }
    public bool Filtered => Kind != null || Outcome != null || UserFilter != null || From != null || To != null || Q != null;

    private IQueryable<SecurityEvent> Query()
    {
        var q = db.SecurityEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Kind)) q = q.Where(e => e.Kind == Kind);
        if (Outcome == "success") q = q.Where(e => e.Success);
        else if (Outcome == "failure") q = q.Where(e => !e.Success);
        if (!string.IsNullOrWhiteSpace(UserFilter)) q = q.Where(e => e.Username != null && e.Username.Contains(UserFilter.Trim()));
        if (From is { } f) { var t = f.ToDateTime(TimeOnly.MinValue); q = q.Where(e => e.At >= t); }
        if (To is { } to) { var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(e => e.At < t); }
        if (!string.IsNullOrWhiteSpace(Q)) q = q.Where(e => (e.Detail != null && e.Detail.Contains(Q.Trim())) || (e.Ip != null && e.Ip.Contains(Q.Trim())));
        return q;
    }

    public async Task OnGetAsync()
    {
        var q = Query();
        Failures = await q.CountAsync(e => !e.Success);
        Result = await q.OrderByDescending(e => e.Id).PageAsync(P, 50);
        Kinds = await db.SecurityEvents.Select(e => e.Kind).Distinct().OrderBy(k => k).ToListAsync();
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        var rows = await Query().OrderByDescending(e => e.Id).Take(100_000).ToListAsync();
        static string C(string? s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Id,Time (UTC),Kind,Outcome,User ID,Username,IP,Detail\r\n");
        foreach (var e in rows)
            sb.AppendJoin(',', e.Id, e.At.ToString("yyyy-MM-dd HH:mm:ss"), C(e.Kind), e.Success ? "Success" : "Failure", e.UserId, C(e.Username), C(e.Ip), C(e.Detail)).Append("\r\n");
        audit.Global("Security log exported", $"{rows.Count} events" + (Filtered ? " (filtered)" : ""));
        await db.SaveChangesAsync();
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"security-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }
}
