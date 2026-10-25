using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Users;

/// <summary>ADM-201 user views: active, disabled and inactive, with MFA status (SEC-308) and last sign-in.</summary>
public class IndexModel(VellumDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Mfa { get; set; }
    [BindProperty(SupportsGet = true)] public int? Admin { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<User> Result { get; private set; } = null!;
    public Dictionary<UserStatus, int> Counts { get; private set; } = [];
    public int Total { get; private set; }
    public Dictionary<int, Location> Locations { get; private set; } = [];
    public bool MfaRequired { get; private set; }

    public async Task OnGetAsync()
    {
        var all = db.Users.AsNoTracking().Where(u => !u.IsSystemIdentity);
        Counts = await all.GroupBy(u => u.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        Total = Counts.Values.Sum();
        MfaRequired = await db.Settings.Select(s => s.MfaRequired).FirstAsync();

        var q = all;
        q = Status switch
        {
            "active" => q.Where(u => u.Status == UserStatus.Active),
            "disabled" => q.Where(u => u.Status == UserStatus.Disabled),
            "inactive" => q.Where(u => u.Status == UserStatus.Inactive),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var t = Q.Trim();
            q = q.Where(u => u.DisplayName.Contains(t) || u.Username.Contains(t) || u.Email.Contains(t) ||
                             (u.JobTitle != null && u.JobTitle.Contains(t)) || (u.Department != null && u.Department.Contains(t)));
        }
        if (Mfa == "on") q = q.Where(u => u.MfaEnrolled);
        else if (Mfa == "off") q = q.Where(u => !u.MfaEnrolled);
        if (Admin == 1) q = q.Where(u => u.IsAdministrator);
        Result = await q.OrderBy(u => u.LastName).ThenBy(u => u.FirstName).PageAsync(P, 50);
        Locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id);
    }
}
