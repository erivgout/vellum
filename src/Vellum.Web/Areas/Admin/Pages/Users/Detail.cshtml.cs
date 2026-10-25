using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages.Users;

/// <summary>
/// One user (SEC-701/704): profile, status lifecycle, password reset (SEC-203), MFA removal (SEC-305), sessions (SEC-403),
/// group memberships and bulk reassignment of open work (SEC-704, INV-073).
/// </summary>
public class DetailModel(VellumDbContext db, UserAdminService users, GroupAdminService groups, UserDirectory dir, ConfigCache cfg) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    [BindProperty] public UserInput Input { get; set; } = new();

    public User U { get; private set; } = null!;
    public UserFormVm Form { get; private set; } = null!;
    public SystemSettings Settings { get; private set; } = null!;
    public List<OpenAssignment> Assignments { get; private set; } = [];
    public List<SessionRow> Sessions { get; private set; } = [];
    public List<GroupInfo> MemberOf { get; private set; } = [];
    public List<GroupInfo> OtherGroups { get; private set; } = [];
    public List<SecurityEvent> Events { get; private set; } = [];
    public int Delegations { get; private set; }
    public string? Location { get; private set; }
    public string? OpenModal { get; private set; }
    public List<string> ModalErrors { get; } = [];
    public bool IsSelf => U.Id == Me.Id;
    public ConfigModel Config => cfg.Current;

    private async Task<bool> LoadAsync(bool keepInput = false)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Id && !x.IsSystemIdentity);
        if (u == null) return false;
        U = u;
        if (!keepInput) Input = UserInput.From(u);
        else Input.Username = u.Username;
        Settings = await db.Settings.AsNoTracking().FirstAsync();
        var locations = await db.Locations.AsNoTracking().OrderBy(l => l.Path).ToListAsync();
        var depts = await db.Users.Where(x => x.Department != null).Select(x => x.Department!).Distinct().OrderBy(d => d).ToListAsync();
        Form = new UserFormVm(Input, false, u.Id == Me.Id, locations, depts);
        Location = locations.FirstOrDefault(l => l.Id == u.PrimaryLocationId)?.Path;
        Assignments = await users.OpenAssignmentsAsync(u.Id);
        Sessions = await users.ActiveSessionsAsync(u.Id);
        var mine = dir.GroupsOf(u.Id);
        MemberOf = mine.Select(dir.Group).OfType<GroupInfo>().OrderBy(g => g.Name).ToList();
        OtherGroups = dir.Groups.Where(g => !mine.Contains(g.Id) && !g.Inactive).ToList();
        Events = await db.SecurityEvents.AsNoTracking().Where(e => e.UserId == u.Id).OrderByDescending(e => e.Id).Take(6).ToListAsync();
        Delegations = await db.Delegations.CountAsync(d => (d.RequesterId == u.Id || d.DelegateId == u.Id) && (d.Status == DelegationStatus.Active || d.Status == DelegationStatus.Pending));
        return true;
    }

    private string Self => $"/admin/users/{Id}";

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostProfileAsync()
    {
        var res = await users.UpdateAsync(Id, Input);
        if (res.Ok) return Done(res, Self);
        if (!await LoadAsync(keepInput: true)) return NotFound();
        Errors.AddRange(res.Errors);
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(string? reason) => Done(await users.DisableAsync(Id, reason), Self);
    public async Task<IActionResult> OnPostUnlockAsync() => Done(await users.UnlockAsync(Id), Self);
    public async Task<IActionResult> OnPostReactivateAsync(string? reason) => Done(await users.ReactivateAsync(Id, reason), Self);
    public async Task<IActionResult> OnPostRemoveMfaAsync() => Done(await users.RemoveMfaAsync(Id), Self);
    public async Task<IActionResult> OnPostLogoutAsync() => Done(await users.LogOutUserAsync(Id), Self);
    public async Task<IActionResult> OnPostKickAsync() => Done(await users.LogOutUserAsync(Id, kick: true), Self);
    public async Task<IActionResult> OnPostEndSessionAsync(string sessionId) => Done(await users.EndSessionsAsync([sessionId]), Self);
    public async Task<IActionResult> OnPostAddGroupsAsync(int[]? groupIds) => Done(await groups.AddToGroupsAsync(Id, groupIds ?? []), Self + "#groups");
    public async Task<IActionResult> OnPostRemoveGroupAsync(int groupId) => Done(await groups.RemoveMemberAsync(groupId, Id), Self + "#groups");

    public async Task<IActionResult> OnPostInactivateAsync(string? reason)
    {
        var res = await users.InactivateAsync(Id, reason);
        return Done(res, Self + (res.Ok ? "#assignments" : ""));
    }

    public async Task<IActionResult> OnPostReassignAsync(long[]? assignments, int? toUserId, string? comment) =>
        Done(await users.ReassignAsync(Id, assignments ?? [], toUserId, comment), Self + "#assignments");

    public async Task<IActionResult> OnPostPasswordAsync(string? password, bool mustChange)
    {
        var res = await users.ResetPasswordAsync(Id, password, mustChange);
        if (res.Ok) return Done(res, Self);
        if (!await LoadAsync()) return NotFound();
        OpenModal = "password";
        ModalErrors.AddRange(res.Errors);
        return Page();
    }

    public IEnumerable<string> Policy => PasswordPolicy.Describe(Settings);
}
