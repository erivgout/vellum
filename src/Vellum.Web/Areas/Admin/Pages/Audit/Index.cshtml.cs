using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Infrastructure.Jobs;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Audit;

public sealed record AuditRow(AuditEntry A, string? Number, int? FormId, string? Revision);

/// <summary>
/// PLT-1007 audit trail explorer: search entries across every record and the system-wide chain, and verify the
/// hash chains on demand through the scheduled integrity checker (SAF-066, INV-055).
/// </summary>
public class IndexModel(VellumDbContext db, Scheduler scheduler, TaskAdminService tasks, AuditService audit, ConfigCache cfg) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Scope { get; set; }
    [BindProperty(SupportsGet = true, Name = "user")] public string? UserFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public string? Number { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public long? Verified { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<AuditRow> Result { get; private set; } = null!;
    public List<string> Actions { get; private set; } = [];
    public SecurityEvent? LastCheck { get; private set; }
    public TaskRun? Run { get; private set; }
    public int Chains { get; private set; }
    public long Entries { get; private set; }
    public Vellum.Engine.ConfigModel Config => cfg.Current;
    public bool Filtered => Scope != null || UserFilter != null || Action != null || Number != null || From != null || To != null || Q != null;

    public async Task OnGetAsync()
    {
        var q = from a in db.Audit.AsNoTracking()
                join r in db.Records on a.RecordId equals (Guid?)r.Id into rr
                from r in rr.DefaultIfEmpty()
                select new { a, Number = r == null ? null : r.Number, FormId = r == null ? (int?)null : r.FormId, Revision = r == null ? null : r.Revision };
        if (Scope == "records") q = q.Where(x => x.a.RecordId != null);
        else if (Scope == "global") q = q.Where(x => x.a.RecordId == null);
        if (!string.IsNullOrWhiteSpace(UserFilter)) q = q.Where(x => x.a.UserName.Contains(UserFilter.Trim()) || (x.a.OnBehalfOfName != null && x.a.OnBehalfOfName.Contains(UserFilter.Trim())));
        if (!string.IsNullOrWhiteSpace(Action)) q = q.Where(x => x.a.Action == Action);
        if (!string.IsNullOrWhiteSpace(Number)) q = q.Where(x => x.Number != null && x.Number.Contains(Number.Trim()));
        if (From is { } f) { var t = f.ToDateTime(TimeOnly.MinValue); q = q.Where(x => x.a.At >= t); }
        if (To is { } to) { var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(x => x.a.At < t); }
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            q = q.Where(x => (x.a.Detail != null && x.a.Detail.Contains(s)) || (x.a.Reason != null && x.a.Reason.Contains(s)) ||
                             (x.a.FieldLabel != null && x.a.FieldLabel.Contains(s)) || (x.a.NewValue != null && x.a.NewValue.Contains(s)) || (x.a.OldValue != null && x.a.OldValue.Contains(s)));
        }
        Result = await q.OrderByDescending(x => x.a.Id).Select(x => new AuditRow(x.a, x.Number, x.FormId, x.Revision)).PageAsync(P, 50);
        Actions = await db.Audit.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync();
        LastCheck = await db.SecurityEvents.AsNoTracking().Where(e => e.Kind == "Integrity check").OrderByDescending(e => e.Id).FirstOrDefaultAsync();
        Chains = await db.Audit.Select(a => a.RecordId).Distinct().CountAsync();
        Entries = await db.Audit.LongCountAsync();
        if (Verified is { } runId) Run = await db.TaskRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
    }

    /// <summary>Run the integrity checker now (manual run) and show its result.</summary>
    public async Task<IActionResult> OnPostVerifyAsync()
    {
        await tasks.EnsureProfilesAsync();
        audit.Global("Integrity check requested", "Manual hash-chain verification from the audit trail explorer");
        await db.SaveChangesAsync();
        var started = DateTime.UtcNow.AddSeconds(-1);
        if (!await scheduler.RunAsync("integrity-check", manual: true))
            return Toast("An integrity check is already running. Try again in a moment.", "/admin/audit", "bad");
        var task = await db.Tasks.AsNoTracking().FirstAsync(t => t.Key == "integrity-check");
        var run = await db.TaskRuns.AsNoTracking().Where(r => r.TaskId == task.Id && r.StartedAt >= started).OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        return Redirect("/admin/audit" + (run != null ? $"?verified={run.Id}" : ""));
    }
}
