using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Pages.Account;

/// <summary>PLT §2.13: New → Approval (by the delegate) → Active → Completed. Signatures are never delegated (PLT-1306).</summary>
public class DelegationsModel(VellumDbContext db, CurrentUser me, UserDirectory dir, NotificationService notify, AuditService audit) : PageModel
{
    public List<Delegation> Mine { get; private set; } = [];
    public List<Delegation> ToMe { get; private set; } = [];
    public string? Error { get; private set; }

    private async Task LoadAsync()
    {
        Mine = await db.Delegations.AsNoTracking().Where(d => d.RequesterId == me.Id).OrderByDescending(d => d.CreatedAt).ToListAsync();
        ToMe = await db.Delegations.AsNoTracking().Where(d => d.DelegateId == me.Id).OrderByDescending(d => d.CreatedAt).ToListAsync();
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync(int[]? delegateId, DateOnly start, DateOnly end, string? reason)
    {
        var target = delegateId?.FirstOrDefault() ?? 0;
        if (target == 0 || target == me.Id) Error = "Choose someone else to delegate to.";
        else if (dir.Get(target) is not { IsActive: true }) Error = "That person can't receive delegations.";
        else if (end < start) Error = "The end date must be on or after the start date.";
        else if (end < BusinessCalendar.Today) Error = "The end date is in the past.";
        if (Error != null) { await LoadAsync(); return Page(); }
        db.Delegations.Add(new Delegation { RequesterId = me.Id, DelegateId = target, StartDate = start, EndDate = end, Reason = reason, Status = DelegationStatus.Pending, CreatedAt = DateTime.UtcNow });
        notify.Notify(target, NotificationKind.Info, $"{me.Name} asked you to cover their assignments", $"{start:dd MMM} – {end:dd MMM yyyy}. {reason}", url: "/account/delegations");
        audit.Global("Delegation requested", $"{me.Name} → {dir.Name(target)}, {start:yyyy-MM-dd} to {end:yyyy-MM-dd}", reason);
        await db.SaveChangesAsync();
        TempData["Toast"] = $"Delegation sent to {dir.Name(target)} for approval.";
        return Redirect("/account/delegations");
    }

    public async Task<IActionResult> OnPostDecideAsync(int id, bool accept)
    {
        var d = await db.Delegations.FirstOrDefaultAsync(x => x.Id == id && x.DelegateId == me.Id && x.Status == DelegationStatus.Pending);
        if (d == null) return NotFound();
        d.Status = accept ? DelegationStatus.Active : DelegationStatus.Rejected;
        d.DecidedAt = DateTime.UtcNow;
        notify.Notify(d.RequesterId, NotificationKind.Info, $"{me.Name} {(accept ? "accepted" : "declined")} your delegation", null, url: "/account/delegations");
        audit.Global(accept ? "Delegation accepted" : "Delegation declined", $"{dir.Name(d.RequesterId)} → {me.Name}");
        await db.SaveChangesAsync();
        TempData["Toast"] = accept ? "Delegation accepted. Their assignments now appear in your work." : "Delegation declined.";
        return Redirect("/account/delegations");
    }

    public async Task<IActionResult> OnPostEndAsync(int id)
    {
        var d = await db.Delegations.FirstOrDefaultAsync(x => x.Id == id && (x.RequesterId == me.Id || x.DelegateId == me.Id) && x.Status != DelegationStatus.Completed);
        if (d == null) return NotFound();
        d.Status = DelegationStatus.Completed;
        audit.Global("Delegation ended", $"{dir.Name(d.RequesterId)} → {dir.Name(d.DelegateId)} ended by {me.Name}");
        await db.SaveChangesAsync();
        TempData["Toast"] = "Delegation ended.";
        return Redirect("/account/delegations");
    }
}
