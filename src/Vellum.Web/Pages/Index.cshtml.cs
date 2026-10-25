using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Pages.Work;

namespace Vellum.Pages;

public class IndexModel(WorkService work, VellumDbContext db, CurrentUser me, AccessService access, ConfigCache cfg, IEnumerable<IHomeWidget> widgets) : PageModel
{
    public List<WorkItem> Work { get; private set; } = [];
    public string Filter { get; private set; } = "all";
    public int Overdue => Work.Count(w => w.Overdue);
    public int DueWeek => Work.Count(w => w.DueSoon);
    public int OpenCreated { get; private set; }
    public int SignedThisMonth { get; private set; }
    public List<(Record Record, FormDefinition Form, DateTime At, string Action)> Recent { get; } = [];
    public List<Notification> SafetyAlerts { get; private set; } = [];
    public List<Delegation> Delegations { get; private set; } = [];
    public List<HomeWidgetModel> MainWidgets { get; } = [];
    public List<HomeWidgetModel> SideWidgets { get; } = [];
    public DateOnly Month { get; private set; }

    public async Task OnGetAsync(string? filter, string? month)
    {
        Filter = filter is "overdue" or "week" ? filter : "all";
        Month = DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out var m) ? m : new DateOnly(BusinessCalendar.Today.Year, BusinessCalendar.Today.Month, 1);
        Work = await work.MyAssignmentsAsync();

        OpenCreated = await access.Readable().CountAsync(r => r.CreatedById == me.Id && r.PhaseType == PhaseType.Normal && !r.IsArchived);
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        SignedThisMonth = await db.Signatures.CountAsync(s => s.SignerId == me.Id && s.SignedAt >= monthStart);

        var recentIds = await db.Audit.AsNoTracking().Where(a => a.UserId == me.Id && a.RecordId != null && a.Action != "Attachment viewed" && a.Action != "Locked" && a.Action != "Unlocked")
            .GroupBy(a => a.RecordId).Select(g => new { Id = g.Key!.Value, At = g.Max(a => a.At) })
            .OrderByDescending(x => x.At).Take(6).ToListAsync();
        var ids = recentIds.Select(x => x.Id).ToList();
        var recs = await access.Readable().AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync();
        foreach (var x in recentIds)
        {
            var r = recs.FirstOrDefault(r => r.Id == x.Id);
            if (r == null || !cfg.Current.FormsById.TryGetValue(r.FormId, out var f)) continue;
            var last = await db.Audit.AsNoTracking().Where(a => a.RecordId == r.Id && a.UserId == me.Id).OrderByDescending(a => a.Seq).Select(a => a.Action).FirstAsync();
            Recent.Add((r, f, x.At, last));
        }

        SafetyAlerts = await db.Notifications.AsNoTracking().Where(n => n.UserId == me.Id && n.Kind == NotificationKind.SafetyCritical && n.AcknowledgedAt == null)
            .OrderByDescending(n => n.CreatedAt).Take(5).ToListAsync();
        var today = BusinessCalendar.Today;
        Delegations = await db.Delegations.AsNoTracking().Include(d => d.Requester).Include(d => d.Delegate)
            .Where(d => (d.DelegateId == me.Id || d.RequesterId == me.Id) && (d.Status == DelegationStatus.Pending || (d.Status == DelegationStatus.Active && d.EndDate >= today)))
            .ToListAsync();

        foreach (var w in widgets.OrderBy(w => w.Order))
        {
            var model = await w.BuildAsync();
            if (model == null) continue;
            (w.Column == "side" ? SideWidgets : MainWidgets).Add(model);
        }
    }

    public IEnumerable<WorkItem> Filtered => Filter switch
    {
        "overdue" => Work.Where(w => w.Overdue),
        "week" => Work.Where(w => w.DueSoon || w.Overdue),
        _ => Work,
    };

    public string Greeting
    {
        get
        {
            var tz = RecordService.Tz(me.Info?.TimeZone);
            var h = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Hour;
            return h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
        }
    }
}
