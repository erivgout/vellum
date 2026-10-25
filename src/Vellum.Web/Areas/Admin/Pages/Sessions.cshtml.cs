using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>SEC-403: current sessions; end selected sessions, log out or kick a user, or log out everyone else.</summary>
public class SessionsModel(VellumDbContext db, UserAdminService users) : AdminPageModel
{
    public List<SessionRow> Rows { get; private set; } = [];
    public SystemSettings Settings { get; private set; } = null!;
    public int Ended24h { get; private set; }

    public async Task OnGetAsync()
    {
        Rows = await users.ActiveSessionsAsync();
        Settings = await db.Settings.AsNoTracking().FirstAsync();
        var since = DateTime.UtcNow.AddHours(-24);
        Ended24h = await db.Sessions.CountAsync(s => s.RevokedAt >= since);
    }

    public async Task<IActionResult> OnPostEndAsync(string[]? ids) => Done(await users.EndSessionsAsync(ids ?? []), "/admin/sessions");
    public async Task<IActionResult> OnPostLogoutUserAsync(int userId) => Done(await users.LogOutUserAsync(userId), "/admin/sessions");
    public async Task<IActionResult> OnPostKickAsync(int userId) => Done(await users.LogOutUserAsync(userId, kick: true), "/admin/sessions");
    public async Task<IActionResult> OnPostEveryoneAsync() => Done(await users.LogOutEveryoneAsync(), "/admin/sessions");
}
