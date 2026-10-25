using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Auth;
using UiH = Vellum.Infrastructure.Ui.Ui;

namespace Vellum.Modules.Admin;

/// <summary>SEC-701 profile fields an administrator can set.</summary>
public sealed class UserInput
{
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public int? PrimaryLocationId { get; set; }
    public string? TimeZone { get; set; }
    public int? ReportsToId { get; set; }
    public LicenseType License { get; set; } = LicenseType.Full;
    public bool IsExternal { get; set; }
    public bool IsAdministrator { get; set; }

    public static UserInput From(User u) => new()
    {
        Username = u.Username, FirstName = u.FirstName, LastName = u.LastName, DisplayName = u.DisplayName, Email = u.Email,
        JobTitle = u.JobTitle, Department = u.Department, PrimaryLocationId = u.PrimaryLocationId, TimeZone = u.TimeZone,
        ReportsToId = u.ReportsToId, License = u.License, IsExternal = u.IsExternal, IsAdministrator = u.IsAdministrator,
    };
}

public sealed record OpenAssignment(long Id, Guid RecordId, string Number, string Title, int FormId, string Phase, DateOnly? Due,
    AssignmentStatus Status, DateTime AssignedAt);

public sealed record SessionRow(UserSession Session, UserInfo? User, bool Idle);

/// <summary>
/// User lifecycle administration (SEC §4.7, ADM §5.2): create, edit, disable/unlock, inactivate/reactivate, password
/// reset, MFA removal, forced sign-out and bulk reassignment of an inactivated user's work (SEC-704, INV-073).
/// Every change is written to the system audit trail, the configuration audit trail and, where SEC-1001 asks, the security log.
/// </summary>
[ScopedService]
public sealed class UserAdminService(
    VellumDbContext db, CurrentUser me, UserDirectory dir, AuditService audit, SessionService sessions,
    WorkflowEngine workflow, NotificationService notify, AccessAdminService access)
{
    public static readonly string[] TimeZones = Vellum.Pages.Account.ProfileModel.TimeZones;

    public static string Snapshot(User u, IReadOnlyDictionary<int, string>? locations = null, UserDirectory? d = null) => AdminJson.Of(new
    {
        u.Username, u.FirstName, u.LastName, u.DisplayName, u.Email, u.JobTitle, u.Department,
        primaryLocation = u.PrimaryLocationId is { } l ? locations?.GetValueOrDefault(l) ?? l.ToString() : null,
        u.TimeZone, reportsTo = u.ReportsToId is { } r ? d?.Get(r)?.Username ?? r.ToString() : null,
        u.License, external = u.IsExternal, administrator = u.IsAdministrator, u.Status, u.StatusReason, u.MustChangePassword, mfa = u.MfaEnrolled,
    });

    private async Task<string> SnapAsync(User u)
    {
        var locs = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Code);
        return Snapshot(u, locs, dir);
    }

    private async Task<User?> TargetAsync(int id) => await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsSystemIdentity);

    private async Task<bool> IsLastActiveAdminAsync(int userId) =>
        !await db.Users.AnyAsync(u => u.Id != userId && u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity);

    // ------------------------------------------------------------------ create / edit

    private async Task<List<string>> ValidateAsync(UserInput i, User? existing)
    {
        var errors = new List<string>();
        if (existing == null)
        {
            i.Username = i.Username?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(i.Username)) errors.Add("Enter a username.");
            else if (!AdminText.UsernamePattern.IsMatch(i.Username)) errors.Add("Usernames use 2–40 lowercase letters, digits, dots, dashes or underscores.");
            else if (i.Username == "system" || await db.Users.AnyAsync(u => u.Username == i.Username)) errors.Add($"The username “{i.Username}” is already taken.");
        }
        i.FirstName = i.FirstName?.Trim();
        i.LastName = i.LastName?.Trim();
        i.Email = i.Email?.Trim();
        i.JobTitle = string.IsNullOrWhiteSpace(i.JobTitle) ? null : i.JobTitle.Trim();
        i.Department = string.IsNullOrWhiteSpace(i.Department) ? null : i.Department.Trim();
        i.DisplayName = string.IsNullOrWhiteSpace(i.DisplayName) ? $"{i.FirstName} {i.LastName}".Trim() : i.DisplayName.Trim();
        if (string.IsNullOrEmpty(i.FirstName)) errors.Add("Enter a first name.");
        if (string.IsNullOrEmpty(i.LastName)) errors.Add("Enter a last name.");
        if (!AdminText.IsEmail(i.Email)) errors.Add("Enter a valid email address.");
        else if (await db.Users.AnyAsync(u => u.Email == i.Email && u.Id != (existing == null ? 0 : existing.Id))) errors.Add($"Another user already has the email {i.Email}.");

        if (i.PrimaryLocationId is { } loc)
        {
            var l = await db.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == loc);
            if (l == null) errors.Add("Choose a primary location from the list.");
            else if (l.Disabled && existing?.PrimaryLocationId != loc) errors.Add($"{l.Name} is disabled and can't be picked for profiles (ADM-404).");
            if (string.IsNullOrEmpty(i.TimeZone) && l != null) i.TimeZone = l.TimeZone;
        }
        i.TimeZone = string.IsNullOrWhiteSpace(i.TimeZone) ? "UTC" : i.TimeZone;
        if (!TimeZones.Contains(i.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(i.TimeZone, out _)) errors.Add("Choose a time zone from the list.");

        if (i.ReportsToId is { } mgr)
        {
            var m = dir.Get(mgr);
            if (m == null || m.IsSystem) errors.Add("Choose who this person reports to from the directory.");
            else if (existing != null && mgr == existing.Id) errors.Add("A person can't report to themselves.");
            else if (m.Status == UserStatus.Inactive && existing?.ReportsToId != mgr) errors.Add($"{m.DisplayName} is inactive and can't be picked as a manager.");
            else if (existing != null)
            {
                // No reporting cycles.
                var seen = new HashSet<int> { existing.Id };
                for (var cur = dir.Get(mgr); cur != null; cur = dir.Get(cur.ReportsToId))
                {
                    if (!seen.Add(cur.Id)) { errors.Add($"That would make a reporting loop through {cur.DisplayName}."); break; }
                    if (cur.ReportsToId == null) break;
                }
            }
        }
        if (!Enum.IsDefined(i.License)) errors.Add("Choose a license type.");

        if (existing != null && existing.IsAdministrator && !i.IsAdministrator)
        {
            if (existing.Id == me.Id) errors.Add("You can't remove your own administrator rights.");
            else if (existing.Status == UserStatus.Active && await IsLastActiveAdminAsync(existing.Id)) errors.Add("This is the last active administrator. Make someone else an administrator first.");
        }
        return errors;
    }

    public async Task<AdminResult> CreateAsync(UserInput input, string? password, bool mustChange)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var errors = await ValidateAsync(input, null);
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        if (errors.Count == 0)
            errors.AddRange(PasswordPolicy.Validate(password ?? "", settings, new User { Username = input.Username! }, []).Select(e => "Password: " + e));
        if (errors.Count > 0) return AdminResult.Fail(errors);

        var now = DateTime.UtcNow;
        var hash = PasswordHasher.Hash(password!);
        var u = new User
        {
            Username = input.Username!, FirstName = input.FirstName!, LastName = input.LastName!, DisplayName = input.DisplayName!,
            Email = input.Email!, JobTitle = input.JobTitle, Department = input.Department, PrimaryLocationId = input.PrimaryLocationId,
            TimeZone = input.TimeZone!, ReportsToId = input.ReportsToId, License = input.License, IsExternal = input.IsExternal,
            IsAdministrator = input.IsAdministrator, PasswordHash = hash, PasswordChangedAt = now, MustChangePassword = mustChange,
            CreatedAt = now, Status = UserStatus.Active,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        db.PasswordHistory.Add(new PasswordHistory { UserId = u.Id, Hash = hash, CreatedAt = now });
        audit.Global("User created", $"{u.DisplayName} ({u.Username}) created{(u.IsAdministrator ? " as an administrator" : "")}{(mustChange ? "; must change password at next sign-in" : "")}.");
        audit.Security("Account created", true, u.Id, u.Username, $"Created by {me.Name}{(u.IsAdministrator ? "; administrator" : "")}");
        audit.Config("User", u.Username, "Created", null, await SnapAsync(u));
        await db.SaveChangesAsync();
        dir.Invalidate();
        // SEC-709: welcome email. Never includes the password (INV-151).
        notify.Notify(u.Id, NotificationKind.Info, "Welcome to Vellum",
            $"An account was created for you. Your username is {u.Username}. Your administrator will give you a temporary password{(mustChange ? " that you'll change when you first sign in" : "")}.");
        await db.SaveChangesAsync();
        return AdminResult.Success($"Created {u.DisplayName}.", u.Id);
    }

    public async Task<AdminResult> UpdateAsync(int id, UserInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var u = await TargetAsync(id);
        if (u == null) return AdminResult.Fail("That user doesn't exist.");
        var errors = await ValidateAsync(input, u);
        if (errors.Count > 0) return AdminResult.Fail(errors);

        var before = await SnapAsync(u);
        var locs = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name);
        var changes = new List<(string Key, string Label, string? Old, string? New)>();
        void Track(string key, string label, string? o, string? n) { if ((o ?? "") != (n ?? "")) changes.Add((key, label, o, n)); }
        Track("firstName", "First name", u.FirstName, input.FirstName);
        Track("lastName", "Last name", u.LastName, input.LastName);
        Track("displayName", "Display name", u.DisplayName, input.DisplayName);
        Track("email", "Email", u.Email, input.Email);
        Track("jobTitle", "Title", u.JobTitle, input.JobTitle);
        Track("department", "Department", u.Department, input.Department);
        Track("primaryLocation", "Primary location", u.PrimaryLocationId is { } a ? locs.GetValueOrDefault(a) : null, input.PrimaryLocationId is { } b ? locs.GetValueOrDefault(b) : null);
        Track("timeZone", "Time zone", u.TimeZone, input.TimeZone);
        Track("reportsTo", "Reports to", u.ReportsToId is { } r1 ? dir.Name(r1) : null, input.ReportsToId is { } r2 ? dir.Name(r2) : null);
        Track("license", "License", u.License.ToString(), input.License.ToString());
        Track("external", "External user", u.IsExternal ? "Yes" : "No", input.IsExternal ? "Yes" : "No");
        Track("administrator", "Administrator", u.IsAdministrator ? "Yes" : "No", input.IsAdministrator ? "Yes" : "No");
        if (changes.Count == 0) return AdminResult.Success("No changes to save.", u.Id);

        var adminChanged = u.IsAdministrator != input.IsAdministrator;
        u.FirstName = input.FirstName!;
        u.LastName = input.LastName!;
        u.DisplayName = input.DisplayName!;
        u.Email = input.Email!;
        u.JobTitle = input.JobTitle;
        u.Department = input.Department;
        u.PrimaryLocationId = input.PrimaryLocationId;
        u.TimeZone = input.TimeZone!;
        u.ReportsToId = input.ReportsToId;
        u.License = input.License;
        u.IsExternal = input.IsExternal;
        u.IsAdministrator = input.IsAdministrator;

        foreach (var c in changes)
            audit.Record(null, "User profile changed", $"{u.DisplayName} ({u.Username})", fieldKey: c.Key, fieldLabel: c.Label, oldValue: c.Old, newValue: c.New);
        if (adminChanged)
            audit.Security(u.IsAdministrator ? "Administrator rights granted" : "Administrator rights removed", true, u.Id, u.Username, $"By {me.Name}");
        audit.Config("User", u.Username, "Updated", before, await SnapAsync(u));
        await db.SaveChangesAsync();
        // SEC-402: privilege change rotates sessions.
        if (adminChanged) await sessions.RevokeAllAsync(u.Id, "Privileges changed");
        dir.Invalidate();
        return AdminResult.Success($"Saved {u.DisplayName} ({UiH.Plural(changes.Count, "change")}).", u.Id);
    }

    // ------------------------------------------------------------------ status (SEC-702/704)

    private async Task<AdminResult> ChangeStatusAsync(User u, UserStatus to, string? reason, string action, string securityKind, string message)
    {
        var before = await SnapAsync(u);
        var from = u.Status;
        u.Status = to;
        u.StatusReason = to == UserStatus.Active ? null : reason;
        if (to == UserStatus.Active) u.FailedLoginCount = 0;
        audit.Record(null, action, $"{u.DisplayName} ({u.Username})", reason: reason, fieldKey: "status", fieldLabel: "Status",
            oldValue: from.ToString(), newValue: to.ToString());
        audit.Security(securityKind, true, u.Id, u.Username, $"By {me.Name}" + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}"));
        audit.Config("User", u.Username, action, before, await SnapAsync(u), reason);
        await db.SaveChangesAsync();
        if (to != UserStatus.Active) await sessions.RevokeAllAsync(u.Id, action); // SEC-405
        dir.Invalidate();
        return AdminResult.Success(message, u.Id);
    }

    public async Task<AdminResult> DisableAsync(int id, string? reason)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var u = await TargetAsync(id);
        if (u == null) return AdminResult.Fail("That user doesn't exist.");
        if (u.Id == me.Id) return AdminResult.Fail("You can't disable your own account.");
        if (u.Status != UserStatus.Active) return AdminResult.Fail($"{u.DisplayName} isn't active.");
        if (string.IsNullOrWhiteSpace(reason)) return AdminResult.Fail("Give a reason for disabling the account.");
        if (u.IsAdministrator && await IsLastActiveAdminAsync(u.Id)) return AdminResult.Fail("This is the last active administrator.");
        return await ChangeStatusAsync(u, UserStatus.Disabled, reason.Trim(), "User disabled", "Account disabled",
            $"{u.DisplayName} is disabled and was signed out.");
    }

    /// <summary>Unlock a locked or disabled account and reset its failed sign-in count (SEC-702).</summary>
    public async Task<AdminResult> UnlockAsync(int id)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var u = await TargetAsync(id);
        if (u == null) return AdminResult.Fail("That user doesn't exist.");
        if (u.Status == UserStatus.Inactive) return AdminResult.Fail($"{u.DisplayName} is inactive. Reactivate the account instead.");
        if (u.Status == UserStatus.Active)
        {
            if (u.FailedLoginCount == 0) return AdminResult.Fail($"{u.DisplayName} isn't locked.");
            u.FailedLoginCount = 0;
            audit.Global("Failed sign-in count reset", $"{u.DisplayName} ({u.Username})");
            await db.SaveChangesAsync();
            return AdminResult.Success($"Reset {u.DisplayName}'s failed sign-in count.", u.Id);
        }
        return await ChangeStatusAsync(u, UserStatus.Active, null, "User unlocked", "Account unlocked", $"{u.DisplayName} can sign in again.");
    }

    public async Task<List<OpenAssignment>> OpenAssignmentsAsync(int userId) =>
        await db.Assignments.AsNoTracking()
            .Where(a => a.UserId == userId && (a.Status == AssignmentStatus.Active || a.Status == AssignmentStatus.Waiting) && a.Record.PhaseType == PhaseType.Normal)
            .OrderBy(a => a.Record.DueDate == null).ThenBy(a => a.Record.DueDate).ThenBy(a => a.Record.Number)
            .Select(a => new OpenAssignment(a.Id, a.RecordId, a.Record.Number, a.Record.Title, a.Record.FormId,
                a.PhaseEntry.PhaseName, a.Record.DueDate, a.Status, a.AssignedAt))
            .ToListAsync();

    /// <summary>
    /// SEC-704: inactivating ends the user's sessions and delegations. Group memberships are kept as inactive memberships
    /// (they no longer count, and come back on reactivation, SEC-705). Open assignments stay until reassigned (INV-073).
    /// </summary>
    public async Task<AdminResult> InactivateAsync(int id, string? reason)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var u = await TargetAsync(id);
        if (u == null) return AdminResult.Fail("That user doesn't exist.");
        if (u.Id == me.Id) return AdminResult.Fail("You can't inactivate your own account.");
        if (u.Status == UserStatus.Inactive) return AdminResult.Fail($"{u.DisplayName} is already inactive.");
        if (string.IsNullOrWhiteSpace(reason)) return AdminResult.Fail("Give a reason for inactivating the account.");
        if (u.IsAdministrator && await IsLastActiveAdminAsync(u.Id)) return AdminResult.Fail("This is the last active administrator.");
        if (await access.LosesLastManagerAsync(new ManagerScenario { InactivatedUser = u.Id }) is { Count: > 0 } lost)
            return AdminResult.Fail(AccessAdminService.Inv084Message(lost, $"Inactivating {u.DisplayName}"));

        var delegations = await db.Delegations.Where(d => (d.RequesterId == u.Id || d.DelegateId == u.Id) &&
            (d.Status == DelegationStatus.Active || d.Status == DelegationStatus.Pending)).ToListAsync();
        foreach (var d in delegations)
        {
            d.Status = d.Status == DelegationStatus.Pending ? DelegationStatus.Rejected : DelegationStatus.Completed;
            d.DecidedAt = DateTime.UtcNow;
        }
        var open = (await OpenAssignmentsAsync(u.Id)).Count;
        var res = await ChangeStatusAsync(u, UserStatus.Inactive, reason.Trim(), "User inactivated", "Account inactivated",
            $"{u.DisplayName} is inactive." + (open > 0 ? $" Reassign their {UiH.Plural(open, "open assignment")}." : ""));
        return res;
    }

    public async Task<AdminResult> ReactivateAsync(int id, string? reason)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var u = await TargetAsync(id);
        if (u == null) return AdminResult.Fail("That user doesn't exist.");
        if (u.Status != UserStatus.Inactive) return AdminResult.Fail($"{u.DisplayName} isn't inactive.");
        var groups = dir.GroupsOf(u.Id).Count;
        return await ChangeStatusAsync(u, UserStatus.Active, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), "User reactivated", "Account reactivated",
            $"{u.DisplayName} is active again" + (groups > 0 ? $" and back in {UiH.Plural(groups, "group")}." : "."));
    }

    /// <summary>
    /// Bulk reassignment of another user's open assignments (SEC-704). Runs through <see cref="WorkflowEngine.ReassignAsync"/>
    /// as the administrator, so it is validated and audited like any reassignment. Co-assignees keep their assignments.
    /// </summary>
    public async Task<AdminResult> ReassignAsync(int fromUserId, IReadOnlyList<long> assignmentIds, int? toUserId, string? comment)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        if (toUserId is not { } to || dir.Get(to) is not { IsSystem: false } target) return AdminResult.Fail("Choose who should take over the work.");
        // INV-073: never assign to an inactive user. Only active users are offered.
        if (target.Status != UserStatus.Active) return AdminResult.Fail($"{target.DisplayName} isn't active and can't receive assignments.");
        if (to == fromUserId) return AdminResult.Fail("Choose someone else.");
        if (assignmentIds.Count == 0) return AdminResult.Fail("Select at least one assignment.");

        var picked = await db.Assignments.AsNoTracking()
            .Where(a => assignmentIds.Contains(a.Id) && a.UserId == fromUserId && (a.Status == AssignmentStatus.Active || a.Status == AssignmentStatus.Waiting))
            .Select(a => new { a.Id, a.RecordId, a.PhaseEntryId, a.Record.Number }).ToListAsync();
        if (picked.Count == 0) return AdminResult.Fail("Those assignments are no longer open.");

        var from = dir.Name(fromUserId);
        var done = 0;
        var failed = new List<string>();
        foreach (var rec in picked.GroupBy(p => (p.RecordId, p.PhaseEntryId)))
        {
            db.ChangeTracker.Clear();
            var current = await db.Assignments.AsNoTracking()
                .Where(a => a.PhaseEntryId == rec.Key.PhaseEntryId && (a.Status == AssignmentStatus.Active || a.Status == AssignmentStatus.Waiting))
                .OrderBy(a => a.Order).Select(a => a.UserId).ToListAsync();
            var ids = current.Select(u => u == fromUserId ? to : u).Where(u => dir.Get(u) is { Assignable: true }).Distinct().ToList();
            if (!ids.Contains(to)) ids.Add(to);
            var note = string.IsNullOrWhiteSpace(comment) ? $"Reassigned from {from} by an administrator." : comment.Trim();
            try
            {
                var res = await workflow.ReassignAsync(rec.Key.RecordId, ids, note);
                if (res.Ok) done++;
                else failed.Add($"{rec.First().Number}: {string.Join(" ", res.Errors.Select(e => e.Message))}");
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                failed.Add($"{rec.First().Number}: {ex.Message}");
            }
        }
        db.ChangeTracker.Clear();
        if (done > 0)
        {
            audit.Global("Assignments reassigned", $"{UiH.Plural(done, "record")} reassigned from {from} to {target.DisplayName}", comment);
            await db.SaveChangesAsync();
        }
        if (failed.Count > 0)
            return new AdminResult(done > 0, failed, done > 0 ? $"Reassigned {UiH.Plural(done, "record")}; {failed.Count} couldn't be reassigned." : null);
        return AdminResult.Success($"Reassigned {UiH.Plural(done, "record")} to {target.DisplayName}.");
    }

    // ------------------------------------------------------------------ password and MFA

    /// <summary>SEC-203: set a temporary password under the always-on policy, and (recommended) force a change at next sign-in.</summary>
    public async Task<AdminResult> ResetPasswordAsync(int id, string? password, bool mustChange)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var u = await TargetAsync(id);
        if (u == null) return AdminResult.Fail("That user doesn't exist.");
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var history = await db.PasswordHistory.Where(h => h.UserId == u.Id).OrderByDescending(h => h.CreatedAt)
            .Take(Math.Max(1, settings.PasswordHistoryCount)).Select(h => h.Hash).ToListAsync();
        var errors = PasswordPolicy.Validate(password ?? "", settings, u, history);
        if (errors.Count > 0) return AdminResult.Fail(errors);

        var hash = PasswordHasher.Hash(password!);
        u.PasswordHash = hash;
        u.PasswordChangedAt = DateTime.UtcNow;
        u.MustChangePassword = mustChange;
        db.PasswordHistory.Add(new PasswordHistory { UserId = u.Id, Hash = hash, CreatedAt = DateTime.UtcNow });
        // INV-151: the password itself is never written anywhere but the hash.
        audit.Global("Password reset by administrator", $"{u.DisplayName} ({u.Username}){(mustChange ? "; must change at next sign-in" : "")}");
        audit.Security("Password reset by administrator", true, u.Id, u.Username, $"By {me.Name}{(mustChange ? "; change forced at next sign-in" : "")}");
        await db.SaveChangesAsync();
        if (u.Id != me.Id) await sessions.RevokeAllAsync(u.Id, "Password reset by administrator");
        notify.Notify(u.Id, NotificationKind.Info, "Your password was reset", $"{me.Name} set a temporary password for your account. Ask them for it, or contact your administrator if you didn't expect this.");
        await db.SaveChangesAsync();
        dir.Invalidate();
        return AdminResult.Success($"Set a temporary password for {u.DisplayName}." + (mustChange ? " They'll choose a new one when they sign in." : ""), u.Id);
    }

    /// <summary>SEC-305: removing a factor ends the user's sessions at once and forces a password reset at next sign-in.</summary>
    public async Task<AdminResult> RemoveMfaAsync(int id)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var u = await TargetAsync(id);
        if (u == null) return AdminResult.Fail("That user doesn't exist.");
        if (!u.MfaEnrolled && u.TotpSecret == null) return AdminResult.Fail($"{u.DisplayName} has no authenticator registered.");
        var before = await SnapAsync(u);
        u.TotpSecret = null;
        u.MfaEnrolled = false;
        u.MustChangePassword = true;
        audit.Global("MFA factor removed", $"{u.DisplayName} ({u.Username}); sessions ended and password change forced");
        audit.Security("MFA factor removed", true, u.Id, u.Username, $"Authenticator app removed by {me.Name}");
        audit.Config("User", u.Username, "MFA factor removed", before, await SnapAsync(u));
        await db.SaveChangesAsync();
        await sessions.RevokeAllAsync(u.Id, "MFA factor removed");
        dir.Invalidate();
        return AdminResult.Success($"Removed {u.DisplayName}'s authenticator. They were signed out and must reset their password.", u.Id);
    }

    // ------------------------------------------------------------------ sessions (SEC-403)

    public async Task<List<SessionRow>> ActiveSessionsAsync(int? userId = null)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var now = DateTime.UtcNow;
        var q = db.Sessions.AsNoTracking().Where(s => s.RevokedAt == null && s.AbsoluteExpiresAt > now);
        if (userId is { } uid) q = q.Where(s => s.UserId == uid);
        var list = await q.OrderByDescending(s => s.LastSeenAt).ToListAsync();
        var idleCut = now.AddMinutes(-settings.SessionIdleMinutes);
        return list.Select(s => new SessionRow(s, dir.Get(s.UserId), s.LastSeenAt < idleCut)).ToList();
    }

    public async Task<AdminResult> EndSessionsAsync(IReadOnlyList<string> ids)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var targets = await db.Sessions.Where(s => ids.Contains(s.Id) && s.RevokedAt == null && s.Id != me.SessionId).ToListAsync();
        if (targets.Count == 0) return AdminResult.Fail(ids.Contains(me.SessionId ?? "") ? "You can't end your own current session here. Sign out instead." : "Select at least one session.");
        foreach (var s in targets)
        {
            s.RevokedAt = DateTime.UtcNow;
            s.RevokedReason = $"Ended by administrator {me.Name}";
        }
        foreach (var g in targets.GroupBy(s => s.UserId))
            audit.Security("Forced logout", true, g.Key, dir.Get(g.Key)?.Username,
                $"{UiH.Plural(g.Count(), "session")} ({string.Join(", ", g.Select(s => AdminText.ShortSession(s.Id)))}) ended by {me.Name}");
        audit.Global("Sessions ended", $"{UiH.Plural(targets.Count, "session")} of {string.Join(", ", targets.Select(s => dir.Name(s.UserId)).Distinct())}");
        await db.SaveChangesAsync();
        return AdminResult.Success($"Ended {UiH.Plural(targets.Count, "session")}.");
    }

    public async Task<AdminResult> LogOutUserAsync(int userId, bool kick = false)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var u = await TargetAsync(userId);
        if (u == null) return AdminResult.Fail("That user doesn't exist.");
        if (u.Id == me.Id) return AdminResult.Fail("You can't log yourself out here. Use Sign out.");
        if (kick)
        {
            // SEC-403: kicking temporarily disables the account until an administrator re-enables it.
            if (u.Status != UserStatus.Active) return AdminResult.Fail($"{u.DisplayName} isn't active.");
            if (u.IsAdministrator && await IsLastActiveAdminAsync(u.Id)) return AdminResult.Fail("This is the last active administrator.");
            var res = await ChangeStatusAsync(u, UserStatus.Disabled, $"Kicked out by {me.Name}", "User kicked out", "User kicked out",
                $"{u.DisplayName} was signed out and disabled until an administrator unlocks the account.");
            return res;
        }
        var n = await sessions.RevokeAllAsync(u.Id, $"Logged out by administrator {me.Name}");
        audit.Security("Forced logout", true, u.Id, u.Username, $"All sessions ({n}) ended by {me.Name}");
        audit.Global("User logged out", $"{u.DisplayName}: {UiH.Plural(n, "session")} ended");
        await db.SaveChangesAsync();
        return AdminResult.Success(n == 0 ? $"{u.DisplayName} had no open sessions." : $"Signed {u.DisplayName} out of {UiH.Plural(n, "session")}.");
    }

    /// <summary>SEC-403: log out every user except the administrator doing it.</summary>
    public async Task<AdminResult> LogOutEveryoneAsync()
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var now = DateTime.UtcNow;
        var affected = await db.Sessions.Where(s => s.RevokedAt == null && s.UserId != me.Id).Select(s => s.UserId).Distinct().ToListAsync();
        var n = await db.Sessions.Where(s => s.RevokedAt == null && s.UserId != me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, $"All users logged out by {me.Name}"));
        audit.Security("Forced logout (all users)", true, me.Id, me.Info?.Username,
            $"{UiH.Plural(n, "session")} of {UiH.Plural(affected.Count, "user")} ended: {string.Join(", ", affected.Select(dir.Name).Take(40))}");
        audit.Global("All users logged out", $"{UiH.Plural(n, "session")} of {UiH.Plural(affected.Count, "user")} ended (the administrator's own sessions were kept)");
        await db.SaveChangesAsync();
        return AdminResult.Success(n == 0 ? "No one else was signed in." : $"Signed out {UiH.Plural(affected.Count, "user")} ({UiH.Plural(n, "session")}).");
    }
}
