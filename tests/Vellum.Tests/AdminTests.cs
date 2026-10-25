using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure.Jobs;
using Vellum.Modules.Admin;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>Administration Center (ADM §5, SEC §4.7, SAF-010/011). Tests restore any shared state they change.</summary>
[Collection("vellum")]
public class AdminTests(VellumFixture fx)
{
    private const string StrongPassword = "Vellum-Admin-Test-42!";

    private static T Svc<T>(IServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();

    /// <summary>Create a throw-away user (unique username) as the administrator.</summary>
    private async Task<User> NewUserAsync(string first = "Test", bool admin = false)
    {
        using var s = fx.As("admin");
        var name = "t" + Guid.NewGuid().ToString("N")[..10];
        var res = await Svc<UserAdminService>(s).CreateAsync(new UserInput
        {
            Username = name, FirstName = first, LastName = "User", Email = $"{name}@acme-medical.example", IsAdministrator = admin,
        }, StrongPassword, mustChange: true);
        Assert.True(res.Ok, res.Error);
        return await s.Db().Users.AsNoTracking().SingleAsync(u => u.Id == res.Id);
    }

    private async Task<Record> NewCapaAsync(IServiceScope s) =>
        await s.CreateAsync("capa", new()
        {
            ["subject"] = "Admin test CAPA", ["capa_type"] = "corrective", ["source"] = "internal_audit", ["priority"] = "high",
            ["owner"] = s.UserId("jlee"), ["description"] = "Something went wrong.",
            ["effectiveness_criteria"] = "It doesn't happen again.", ["effectiveness_verifier"] = s.UserId("tbrooks"),
        }, rows: new()
        {
            ["planned_actions"] = [new() { ["action"] = "Fix it", ["assignee"] = s.UserId("akim"), ["days"] = 5m, ["verify"] = false }],
        });

    private async Task<T> WithSettingsRestoredAsync<T>(Func<Task<T>> work)
    {
        SystemSettings original;
        using (var s = fx.As("admin")) original = await s.Db().Settings.AsNoTracking().FirstAsync();
        try { return await work(); }
        finally
        {
            using var s = fx.As("admin");
            var cur = await s.Db().Settings.FirstAsync();
            SettingsInput.From(original).ApplyTo(cur);
            await s.Db().SaveChangesAsync();
        }
    }

    // ------------------------------------------------------------------ SAF-010 / SAF-011

    [Fact] // SAF-010, SEC-201, SEC-401
    public async Task Regulated_mode_rejects_weakening_password_and_session_controls() => await WithSettingsRestoredAsync(async () =>
    {
        using var s = fx.As("admin");
        var svc = Svc<SettingsAdminService>(s);
        var current = await s.Db().Settings.AsNoTracking().FirstAsync();
        Assert.True(current.RegulatedMode);

        var weaker = SettingsInput.From(current);
        weaker.PasswordMinLength = 10;
        var res = await svc.SaveAsync(weaker, "Testing", TestHelpers.Password);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Contains("regulated mode") && e.Contains("12"));

        var longer = SettingsInput.From(current);
        longer.SessionIdleMinutes = 45;
        Assert.False((await svc.SaveAsync(longer, "Testing", TestHelpers.Password)).Ok);

        var lockout = SettingsInput.From(current);
        lockout.LockoutThreshold = 8;
        Assert.Contains((await svc.SaveAsync(lockout, "Testing", TestHelpers.Password)).Errors, e => e.Contains("regulated mode"));

        // Even outside the regulated floors, the hard limits (SEC-201: only stricter than 8) always apply.
        var floor = SettingsInput.From(current);
        floor.RegulatedMode = false;
        floor.PasswordMinLength = 6;
        Assert.False((await svc.SaveAsync(floor, "Testing", TestHelpers.Password)).Ok);

        s.Db().ChangeTracker.Clear();
        Assert.Equal(current.PasswordMinLength, (await s.Db().Settings.AsNoTracking().FirstAsync()).PasswordMinLength);
        return true;
    });

    [Fact] // SAF-010/011, ADM-101, INV-145
    public async Task Compliance_sensitive_changes_need_a_reason_and_the_admins_password() => await WithSettingsRestoredAsync(async () =>
    {
        using var s = fx.As("admin");
        var svc = Svc<SettingsAdminService>(s);
        var current = await s.Db().Settings.AsNoTracking().FirstAsync();
        var stricter = SettingsInput.From(current);
        stricter.PasswordMinLength = current.PasswordMinLength + 2;

        Assert.Contains((await svc.SaveAsync(stricter, null, TestHelpers.Password)).Errors, e => e.Contains("reason"));
        Assert.Contains((await svc.SaveAsync(stricter, "Tighten policy", "wrong-password")).Errors, e => e.Contains("password"));
        Assert.True(await s.Db().SecurityEvents.AnyAsync(e => e.Kind == "Re-authentication failed" && !e.Success));

        var before = await s.Db().ConfigAudit.CountAsync(c => c.ObjectType == "SystemSettings");
        var ok = await svc.SaveAsync(stricter, "Tighten policy", TestHelpers.Password);
        Assert.True(ok.Ok, ok.Error);
        // Exactly one configuration audit entry with the before/after (INV-145) and a field-level system audit entry.
        Assert.Equal(before + 1, await s.Db().ConfigAudit.CountAsync(c => c.ObjectType == "SystemSettings"));
        var cfgEntry = await s.Db().ConfigAudit.AsNoTracking().Where(c => c.ObjectType == "SystemSettings").OrderByDescending(c => c.Id).FirstAsync();
        Assert.Contains(AdminJson.Diff(cfgEntry.Before, cfgEntry.After), d => d.Path == "passwordMinLength");
        Assert.True(await s.Db().Audit.AnyAsync(a => a.RecordId == null && a.FieldKey == "passwordMinLength" && a.Reason == "Tighten policy"));

        // Non-sensitive preferences need neither.
        s.Db().ChangeTracker.Clear();
        var labels = SettingsInput.From(await s.Db().Settings.AsNoTracking().FirstAsync());
        labels.LabelForward = "Submit";
        Assert.True((await svc.SaveAsync(labels, null, null)).Ok);

        // The posture report lists every compliance-sensitive setting.
        var posture = await Svc<AdminOverviewService>(s).PostureAsync();
        Assert.All(ComplianceCatalog.Settings, x => Assert.Contains(posture, p => p.Label == x.Label));
        Assert.Contains(posture, p => p.Label == "Tamper protection" && p.Status.Level == "ok");
        return true;
    });

    [Fact] // SEC-519: services re-check administrator rights
    public async Task Non_administrators_are_refused_by_every_service()
    {
        using var s = fx.As("jlee");
        var current = await s.Db().Settings.AsNoTracking().FirstAsync();
        Assert.Equal(AdminResult.NotAdmin, (await Svc<SettingsAdminService>(s).SaveAsync(SettingsInput.From(current), "x", TestHelpers.Password)).Error);
        Assert.False((await Svc<UserAdminService>(s).DisableAsync(s.UserId("pshah"), "x")).Ok);
        Assert.False((await Svc<UserAdminService>(s).LogOutEveryoneAsync()).Ok);
        Assert.False((await Svc<AccessAdminService>(s).AddGrantAsync(1, PrincipalKind.User, s.UserId("jlee"), AppRole.Manager, null, true)).Ok);
        Assert.False((await Svc<KeywordAdminService>(s).AddKeywordAsync("priority", "Urgent", null, null)).Ok);
        Assert.False((await Svc<TaskAdminService>(s).RunNowAsync("session-cleanup")).Ok);
    }

    // ------------------------------------------------------------------ INV-084

    [Fact] // INV-084, SEC-502
    public async Task The_last_active_manager_of_an_application_cannot_be_removed()
    {
        using var s = fx.As("admin");
        var access = Svc<AccessAdminService>(s);
        var capa = Svc<ConfigCache>(s).Current.App("capa");
        var sortiz = s.UserId("sortiz");
        var grant = await s.Db().Grants.AsNoTracking().SingleAsync(g => g.AppId == capa.Id && g.PrincipalKind == PrincipalKind.User && g.PrincipalId == sortiz && g.FormKey == null);

        Assert.Contains("INV-084", (await access.RemoveGrantAsync(grant.Id)).Error);
        Assert.Contains("INV-084", (await access.UpdateGrantAsync(grant.Id, AppRole.Author, false)).Error);
        Assert.Contains("INV-084", (await Svc<UserAdminService>(s).InactivateAsync(sortiz, "Leaving")).Error);
        // Action Items' only Manager is the Quality group: making it inactive is refused too.
        var quality = await s.Db().Groups.AsNoTracking().SingleAsync(g => g.Key == "quality");
        var input = GroupInput.From(quality);
        input.Inactive = true;
        Assert.Contains("INV-084", (await Svc<GroupAdminService>(s).UpdateAsync(quality.Id, input)).Error);

        // With a second manager the first one can go, and the grant comes back afterwards.
        var helper = await NewUserAsync("Manager");
        Assert.True((await access.AddGrantAsync(capa.Id, PrincipalKind.User, helper.Id, AppRole.Manager, null, false)).Ok);
        Assert.True((await access.UpdateGrantAsync(grant.Id, AppRole.Author, false)).Ok);
        Assert.Contains("INV-084", (await access.RemoveGrantAsync((await s.Db().Grants.AsNoTracking().SingleAsync(g => g.AppId == capa.Id && g.PrincipalId == helper.Id && g.PrincipalKind == PrincipalKind.User)).Id)).Error);
        Assert.True((await access.UpdateGrantAsync(grant.Id, AppRole.Manager, true)).Ok);
        Assert.True((await access.RemoveGrantAsync((await s.Db().Grants.AsNoTracking().SingleAsync(g => g.AppId == capa.Id && g.PrincipalId == helper.Id && g.PrincipalKind == PrincipalKind.User)).Id)).Ok);
        Assert.True(await s.Db().SecurityEvents.AnyAsync(e => e.Kind == "Role removed" && e.UserId == helper.Id));
        Assert.True(await s.Db().ConfigAudit.AnyAsync(c => c.ObjectType == "AppGrants" && c.ObjectKey == "capa"));
    }

    // ------------------------------------------------------------------ SEC-704 / INV-073

    [Fact] // SEC-704, SEC-405, SEC-705, INV-073
    public async Task Inactivation_ends_sessions_lists_open_work_and_allows_bulk_reassignment()
    {
        var u = await NewUserAsync("Leaver");
        using (var s = fx.As("admin"))
        {
            var quality = await s.Db().Groups.AsNoTracking().SingleAsync(g => g.Key == "quality");
            Assert.True((await Svc<GroupAdminService>(s).AddMembersAsync(quality.Id, [u.Id])).Ok);
            s.Db().Sessions.Add(new UserSession { Id = "test-" + Guid.NewGuid().ToString("N"), UserId = u.Id, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1) });
            await s.Db().SaveChangesAsync();
        }
        Guid recordId;
        using (var author = fx.As(u.Username)) recordId = (await NewCapaAsync(author)).Id; // the creator holds the start-phase assignment

        using var admin = fx.As("admin");
        var users = Svc<UserAdminService>(admin);
        Assert.False((await users.InactivateAsync(u.Id, null)).Ok); // a reason is required
        var res = await users.InactivateAsync(u.Id, "Left the company");
        Assert.True(res.Ok, res.Error);

        admin.Db().ChangeTracker.Clear();
        var after = await admin.Db().Users.AsNoTracking().SingleAsync(x => x.Id == u.Id);
        Assert.Equal(UserStatus.Inactive, after.Status);
        Assert.False(await admin.Db().Sessions.AnyAsync(x => x.UserId == u.Id && x.RevokedAt == null));
        Assert.True(await admin.Db().SecurityEvents.AnyAsync(e => e.Kind == "Account inactivated" && e.UserId == u.Id));
        // Membership is kept but no longer counts (inactive member).
        var dir = Svc<UserDirectory>(admin);
        Assert.Contains(dir.Group("quality")!.Id, dir.GroupsOf(u.Id));
        Assert.DoesNotContain(dir.ActiveMembers("quality"), m => m.Id == u.Id);

        var open = await users.OpenAssignmentsAsync(u.Id);
        var mine = Assert.Single(open, a => a.RecordId == recordId);

        // INV-073: work can't go to an inactive user.
        var other = await NewUserAsync("Other");
        Assert.True((await Svc<UserAdminService>(admin).InactivateAsync(other.Id, "Test")).Ok);
        Assert.False((await users.ReassignAsync(u.Id, [mine.Id], other.Id, null)).Ok);

        var pshah = admin.UserId("pshah");
        var moved = await users.ReassignAsync(u.Id, [mine.Id], pshah, "Handover");
        Assert.True(moved.Ok, moved.Error);
        Assert.Empty(await users.OpenAssignmentsAsync(u.Id));
        Assert.True(await admin.Db().Assignments.AnyAsync(a => a.RecordId == recordId && a.UserId == pshah && a.Status == AssignmentStatus.Active));
        Assert.True(await admin.Db().Audit.AnyAsync(a => a.RecordId == recordId && a.Action == "Reassigned"));

        // SEC-705: reactivation restores group membership.
        Assert.True((await users.ReactivateAsync(u.Id, "Rehired")).Ok);
        Assert.Contains(Svc<UserDirectory>(admin).ActiveMembers("quality"), m => m.Id == u.Id);
    }

    [Fact] // SEC-702, SEC-403
    public async Task Disable_unlock_and_kick_follow_the_account_lifecycle()
    {
        var u = await NewUserAsync("Locked");
        using var s = fx.As("admin");
        var users = Svc<UserAdminService>(s);
        Assert.False((await users.DisableAsync(u.Id, "")).Ok);
        Assert.True((await users.DisableAsync(u.Id, "Extended leave")).Ok);
        Assert.True((await users.UnlockAsync(u.Id)).Ok);
        s.Db().ChangeTracker.Clear();
        Assert.Equal(UserStatus.Active, (await s.Db().Users.AsNoTracking().SingleAsync(x => x.Id == u.Id)).Status);

        Assert.True((await users.LogOutUserAsync(u.Id, kick: true)).Ok);
        s.Db().ChangeTracker.Clear();
        var kicked = await s.Db().Users.AsNoTracking().SingleAsync(x => x.Id == u.Id);
        Assert.Equal(UserStatus.Disabled, kicked.Status);
        Assert.True(await s.Db().SecurityEvents.AnyAsync(e => e.Kind == "User kicked out" && e.UserId == u.Id));

        // An administrator can't lock themselves out.
        Assert.False((await users.DisableAsync(s.UserId("admin"), "Oops")).Ok);
        Assert.False((await users.InactivateAsync(s.UserId("admin"), "Oops")).Ok);
    }

    // ------------------------------------------------------------------ SEC-201/203/305

    [Fact] // SEC-201, SEC-203, INV-151
    public async Task Admin_password_reset_follows_the_password_policy_and_forces_a_change()
    {
        var u = await NewUserAsync("Reset");
        using var s = fx.As("admin");
        var users = Svc<UserAdminService>(s);
        Assert.False((await users.ResetPasswordAsync(u.Id, "short", true)).Ok);
        Assert.Contains((await users.ResetPasswordAsync(u.Id, "Password123!", true)).Errors, e => e.Contains("breached"));
        Assert.Contains((await users.ResetPasswordAsync(u.Id, StrongPassword, true)).Errors, e => e.Contains("recently"));
        Assert.Contains((await users.ResetPasswordAsync(u.Id, $"{u.Username}-Aa1!xyz", true)).Errors, e => e.Contains("username"));

        s.Db().Sessions.Add(new UserSession { Id = "test-" + Guid.NewGuid().ToString("N"), UserId = u.Id, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1) });
        await s.Db().SaveChangesAsync();
        const string temp = "Temporary-Pass-77#";
        Assert.True((await users.ResetPasswordAsync(u.Id, temp, mustChange: true)).Ok);

        s.Db().ChangeTracker.Clear();
        var after = await s.Db().Users.AsNoTracking().SingleAsync(x => x.Id == u.Id);
        Assert.True(after.MustChangePassword);
        Assert.True(PasswordHasher.Verify(temp, after.PasswordHash));
        Assert.False(await s.Db().Sessions.AnyAsync(x => x.UserId == u.Id && x.RevokedAt == null));
        // The password never reaches the audit trail or the security log.
        Assert.False(await s.Db().Audit.AnyAsync(a => (a.Detail != null && a.Detail.Contains(temp)) || (a.NewValue != null && a.NewValue.Contains(temp))));
        Assert.False(await s.Db().SecurityEvents.AnyAsync(e => e.Detail != null && e.Detail.Contains(temp)));
    }

    [Fact] // SEC-305, SEC-310
    public async Task Removing_an_mfa_factor_ends_sessions_and_forces_a_password_reset()
    {
        var u = await NewUserAsync("Mfa");
        using var s = fx.As("admin");
        var tracked = await s.Db().Users.SingleAsync(x => x.Id == u.Id);
        tracked.TotpSecret = "JBSWY3DPEHPK3PXP";
        tracked.MfaEnrolled = true;
        tracked.MustChangePassword = false;
        s.Db().Sessions.Add(new UserSession { Id = "test-" + Guid.NewGuid().ToString("N"), UserId = u.Id, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1) });
        await s.Db().SaveChangesAsync();
        s.Db().ChangeTracker.Clear();

        Assert.True((await Svc<UserAdminService>(s).RemoveMfaAsync(u.Id)).Ok);
        s.Db().ChangeTracker.Clear();
        var after = await s.Db().Users.AsNoTracking().SingleAsync(x => x.Id == u.Id);
        Assert.False(after.MfaEnrolled);
        Assert.Null(after.TotpSecret);
        Assert.True(after.MustChangePassword);
        Assert.False(await s.Db().Sessions.AnyAsync(x => x.UserId == u.Id && x.RevokedAt == null));
        Assert.True(await s.Db().SecurityEvents.AnyAsync(e => e.Kind == "MFA factor removed" && e.UserId == u.Id));
    }

    [Fact] // SEC-701
    public async Task Profile_validation_rejects_duplicates_and_reporting_loops()
    {
        var a = await NewUserAsync("Alpha");
        var b = await NewUserAsync("Beta");
        using var s = fx.As("admin");
        var users = Svc<UserAdminService>(s);
        var dup = UserInput.From(b);
        dup.Email = a.Email;
        Assert.Contains((await users.UpdateAsync(b.Id, dup)).Errors, e => e.Contains("email"));

        var ab = UserInput.From(a);
        ab.ReportsToId = b.Id;
        Assert.True((await users.UpdateAsync(a.Id, ab)).Ok);
        var ba = UserInput.From(b);
        ba.ReportsToId = a.Id;
        Assert.Contains((await users.UpdateAsync(b.Id, ba)).Errors, e => e.Contains("loop"));

        // Field-level audit entries for profile changes.
        Assert.True(await s.Db().Audit.AnyAsync(x => x.RecordId == null && x.Action == "User profile changed" && x.FieldKey == "reportsTo"));
    }

    // ------------------------------------------------------------------ keywords (ADM-607, INV-061, INV-145)

    [Fact] // ADM-607, INV-061, INV-145
    public async Task Renaming_a_keyword_in_use_needs_confirmation_and_is_audited()
    {
        using (var author = fx.As("jlee")) await NewCapaAsync(author); // uses priority "high"
        using var s = fx.As("admin");
        var keywords = Svc<KeywordAdminService>(s);
        var cfg = Svc<ConfigCache>(s);
        var high = cfg.Current.Keyword("priority", "high")!;
        Assert.True((await keywords.ValueCountsAsync("priority")).GetValueOrDefault(high.Id) > 0);

        var refused = await keywords.UpdateKeywordAsync("priority", high.Id, "Very high", high.Color, confirmRename: false);
        Assert.Contains("ADM-607", refused.Error);
        var before = await s.Db().ConfigAudit.CountAsync(c => c.ObjectType == "KeywordList" && c.ObjectKey == "priority");
        Assert.True((await keywords.UpdateKeywordAsync("priority", high.Id, "Very high", high.Color, confirmRename: true)).Ok);
        Assert.Equal("Very high", cfg.Current.Keyword("priority", "high")!.Label); // cache refreshed; the ID never changes
        Assert.Equal(before + 1, await s.Db().ConfigAudit.CountAsync(c => c.ObjectType == "KeywordList" && c.ObjectKey == "priority"));
        Assert.True((await keywords.UpdateKeywordAsync("priority", high.Id, high.Label, high.Color, confirmRename: true)).Ok);

        // Keywords are disabled, never deleted, and disabled ones aren't offered for new values.
        Assert.True((await keywords.SetDisabledAsync("priority", high.Id, true)).Ok);
        Assert.DoesNotContain(cfg.Current.Options("priority"), k => k.Id == high.Id);
        Assert.NotNull(cfg.Current.Keyword(high.Id));
        Assert.True((await keywords.SetDisabledAsync("priority", high.Id, false)).Ok);
        Assert.Contains(cfg.Current.Options("priority"), k => k.Id == high.Id);
    }

    // ------------------------------------------------------------------ other areas

    [Fact] // SEC-703
    public async Task Dormant_account_check_disables_accounts_without_recent_sign_in()
    {
        var u = await NewUserAsync("Dormant");
        using var s = fx.As("admin");
        await s.Db().Users.Where(x => x.Id == u.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.CreatedAt, DateTime.UtcNow.AddDays(-200)));
        await Svc<TaskAdminService>(s).EnsureProfilesAsync();
        Assert.True(await Svc<Scheduler>(s).RunAsync("dormant-accounts", manual: true));
        var after = await s.Db().Users.AsNoTracking().SingleAsync(x => x.Id == u.Id);
        Assert.Equal(UserStatus.Disabled, after.Status);
        Assert.StartsWith("Dormant", after.StatusReason);
        Assert.Equal(UserStatus.Active, (await s.Db().Users.AsNoTracking().SingleAsync(x => x.Username == "jlee")).Status);
        Assert.True(await s.Db().SecurityEvents.AnyAsync(e => e.Kind == "Dormant account disabled" && e.UserId == u.Id));
    }

    [Fact] // ADM-301
    public async Task Business_calendar_rejects_pointless_working_days_and_audits_changes()
    {
        using var s = fx.As("admin");
        var cal = Svc<CalendarAdminService>(s);
        var weekday = new DateOnly(2031, 3, 5); // a Wednesday
        Assert.False((await cal.AddAsync(weekday, "Extra", isWorking: true)).Ok);
        var saturday = new DateOnly(2031, 3, 8);
        Assert.True((await cal.AddAsync(saturday, "Stock-take", isWorking: true)).Ok);
        Assert.False((await cal.AddAsync(saturday, "Again", isWorking: false)).Ok);
        Assert.True(await Svc<BusinessCalendar>(s).IsWorkingDayAsync(saturday));
        var day = await s.Db().CalendarDays.AsNoTracking().SingleAsync(d => d.Date == saturday);
        Assert.True((await cal.RemoveAsync(day.Id)).Ok);
        Assert.Equal(2, await s.Db().ConfigAudit.CountAsync(c => c.ObjectType == "CalendarDay" && c.ObjectKey == "2031-03-08"));
    }

    [Fact] // SEC-706, SEC-704
    public async Task Groups_take_only_active_members_and_keys_are_unique()
    {
        using var s = fx.As("admin");
        var groups = Svc<GroupAdminService>(s);
        var name = "Test group " + Guid.NewGuid().ToString("N")[..6];
        var created = await groups.CreateAsync(new GroupInput { Name = name, Description = "For tests" });
        Assert.True(created.Ok, created.Error);
        Assert.False((await groups.CreateAsync(new GroupInput { Name = name + " 2", Key = AdminText.Slug(name) })).Ok);

        var gone = await NewUserAsync("Gone");
        Assert.True((await Svc<UserAdminService>(s).InactivateAsync(gone.Id, "Test")).Ok);
        Assert.Contains("inactive", (await groups.AddMembersAsync(created.Id!.Value, [gone.Id])).Error);
        Assert.True((await groups.AddMembersAsync(created.Id!.Value, [s.UserId("pshah")])).Ok);
        Assert.True(Svc<UserDirectory>(s).IsMember(s.UserId("pshah"), AdminText.Slug(name)));
        Assert.True((await groups.RemoveMemberAsync(created.Id!.Value, s.UserId("pshah"))).Ok);
    }

    [Fact] // SEC-521
    public async Task Access_report_matches_the_authorization_service()
    {
        using var admin = fx.As("admin");
        var report = await Svc<AccessAdminService>(admin).EffectiveAsync(admin.UserId("jlee"));
        using var jlee = fx.As("jlee");
        var access = Svc<AccessService>(jlee);
        foreach (var row in report) Assert.Equal(access.RoleFor(row.App.Id), row.Role);
        Assert.All(await Svc<AccessAdminService>(admin).EffectiveAsync(admin.UserId("admin")), r => Assert.Equal(AppRole.Manager, r.Role));
    }

    [Fact]
    public void Config_diff_matches_list_items_by_key()
    {
        var before = AdminJson.Of(new { name = "Priority", keywords = new[] { new { key = "low", label = "Low" }, new { key = "high", label = "High" } } });
        var after = AdminJson.Of(new { name = "Priority", keywords = new[] { new { key = "high", label = "Major" }, new { key = "low", label = "Low" } } });
        var diff = AdminJson.Diff(before, after);
        var row = Assert.Single(diff);
        Assert.Equal("keywords[high].label", row.Path);
        Assert.Equal(("High", "Major"), (row.Before, row.After));
    }
}
