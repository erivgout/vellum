using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

public sealed record EmailRow(EmailLogEntry E, string? Number);

/// <summary>ADM-1002: every notification email with recipient, subject, record and delivery status. Exportable.</summary>
public class EmailLogModel(VellumDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<EmailRow> Result { get; private set; } = null!;
    public List<(string Status, int Count)> Statuses { get; private set; } = [];
    public bool Filtered => Q != null || Status != null || From != null || To != null;

    private IQueryable<EmailRow> Query()
    {
        var q = db.EmailLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q)) { var s = Q.Trim(); q = q.Where(e => e.To.Contains(s) || e.Subject.Contains(s)); }
        if (!string.IsNullOrWhiteSpace(Status)) q = q.Where(e => e.Status == Status);
        if (From is { } f) { var t = f.ToDateTime(TimeOnly.MinValue); q = q.Where(e => e.At >= t); }
        if (To is { } to) { var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(e => e.At < t); }
        return from e in q
               join r in db.Records on e.RecordId equals (Guid?)r.Id into rr
               from r in rr.DefaultIfEmpty()
               orderby e.Id descending
               select new EmailRow(e, r == null ? null : r.Number);
    }

    public async Task OnGetAsync()
    {
        Result = await Query().PageAsync(P, 50);
        Statuses = (await db.EmailLog.GroupBy(e => e.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).Select(x => (x.Key, x.N)).OrderBy(x => x.Key).ToList();
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        var rows = await Query().Take(100_000).ToListAsync();
        static string C(string? s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Id,Time (UTC),To,Subject,Record,Status,Body\r\n");
        foreach (var r in rows)
            sb.AppendJoin(',', r.E.Id, r.E.At.ToString("yyyy-MM-dd HH:mm:ss"), C(r.E.To), C(r.E.Subject), C(r.Number), C(r.E.Status), C(r.E.Body)).Append("\r\n");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"email-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }
}
