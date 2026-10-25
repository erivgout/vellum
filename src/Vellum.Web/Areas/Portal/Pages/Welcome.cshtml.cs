using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Portal;

namespace Vellum.Areas.Portal.Pages;

/// <summary>
/// The one-time link from a portal invitation or password reset email (anonymous). Sets the password under the normal policy,
/// spends the link, and sends the contact to the normal sign-in page (so lockout, the login block and two-step verification apply).
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class WelcomeModel(PortalAccounts accounts, VellumDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    public SystemSettings Settings { get; private set; } = null!;
    public string? Username { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Company { get; private set; }
    public bool Valid { get; private set; }
    public bool Done { get; private set; }
    public bool FirstTime { get; private set; }
    public List<string> Errors { get; private set; } = [];

    private async Task LoadAsync()
    {
        Settings = await db.Settings.AsNoTracking().FirstAsync();
        var check = await accounts.CheckAsync(Token);
        Valid = check.Valid;
        if (!check.Valid) { Errors = [check.Error!]; return; }
        Username = check.User!.Username;
        DisplayName = check.User.DisplayName;
        FirstTime = check.User.PasswordHash == null;
        Company = check.User.Department;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/portal");
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? password, string? confirm)
    {
        await LoadAsync();
        if (!Valid) return Page();
        var (ok, errors, user) = await accounts.SetPasswordAsync(Token, password, confirm, HttpContext.Connection.RemoteIpAddress?.ToString());
        if (!ok) { Errors = errors; return Page(); }
        Done = true;
        Valid = false;
        Username = user!.Username;
        return Page();
    }
}
