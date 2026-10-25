using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Portal;

namespace Vellum.Areas.Portal.Pages;

public sealed record PortalContactPerson(string Name, string Email, string? Title);

/// <summary>Supplier portal home (SUP-004): what the customer needs from the supplier, with due dates.</summary>
public class IndexModel(PortalShell shell, CurrentUser me, VellumDbContext db, UserDirectory dir) : PageModel
{
    public List<PortalItem> Items { get; private set; } = [];
    public List<PortalItem> ToDo { get; private set; } = [];
    public List<PortalItem> UnderReview { get; private set; } = [];
    public List<PortalItem> Recent { get; private set; } = [];
    public PortalItem? LatestScorecard { get; private set; }
    public List<PortalContactPerson> Contacts { get; private set; } = [];
    public List<Notification> Updates { get; private set; } = [];
    public int DueSoon { get; private set; }
    public int Overdue { get; private set; }

    public async Task OnGetAsync()
    {
        Items = await shell.ItemsAsync();
        var today = BusinessCalendar.Today;
        ToDo = Items.Where(i => i.State.ActionNeeded).OrderBy(i => i.Due ?? DateOnly.MaxValue).ThenBy(i => i.Number).ToList();
        UnderReview = Items.Where(i => i.Share.Status == ExternalShareStatus.Submitted).OrderByDescending(i => i.UpdatedAt).ToList();
        Recent = Items.Where(i => i.State.Closed && !i.Kind.ReadOnly).OrderByDescending(i => i.UpdatedAt).Take(5).ToList();
        LatestScorecard = Items.Where(i => i.Kind.ReadOnly).OrderByDescending(i => i.Share.SharedAt).FirstOrDefault();
        DueSoon = ToDo.Count(i => i.Due is { } d && d >= today && d <= today.AddDays(7));
        Overdue = ToDo.Count(i => i.Due is { } d && d < today);
        Contacts = Items.Where(i => !i.State.Closed).Select(i => i.Share.SharedById).Distinct().Select(id => dir.Get(id)).OfType<UserInfo>()
            .Select(u => new PortalContactPerson(u.DisplayName, u.Email, u.JobTitle)).Take(4).ToList();
        Updates = await db.Notifications.AsNoTracking().Where(n => n.UserId == me.Id).OrderByDescending(n => n.CreatedAt).Take(5).ToListAsync();
    }

    public string Greeting()
    {
        var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Engine.Records.RecordService.Tz(me.Info?.TimeZone)).Hour;
        return hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
    }
}
