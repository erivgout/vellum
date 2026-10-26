using System.Net;
using System.Text.Json;
using Vellum.Infrastructure.Auth;

namespace Vellum.Tests.E2e;

/// <summary>The Administration Center end to end: users, preferences, maintenance, sessions, logs, master data and tasks.</summary>
[Collection("vellum")]
public class AdminE2eTests(VellumFixture fx)
{
    private const string BadCredentials = "The username or password isn't right.";

    [Fact] // SEC-201, SEC-203, SEC-204, SEC-205, SEC-709, SEC-704, SEC-405, INV-150
    public async Task A_new_user_must_replace_the_temporary_password_and_loses_access_when_inactivated()
    {
        using var admin = await fx.SignedInAsync("admin");

        // The password policy applies to the temporary password too.
        var weak = await (await admin.GoAsync("/admin/users/new")).FormOf("#u-username")
            .Set("Input.Username", "e2e.weak." + E2e.Tag().ToLowerInvariant()).Set("Input.FirstName", "Weak").Set("Input.LastName", "Password")
            .Set("Input.Email", $"weak.{E2e.Tag()}@acme-medical.example").Set("Password", "short").SubmitAsync();
        Assert.Equal("/admin/users/new", weak.Url.AbsolutePath);
        Assert.Contains(weak.Errors, e => e.StartsWith("Password: Use at least"));
        Assert.Contains(weak.Errors, e => e == "Password: Add at least one special character.");

        var user = await admin.CreateUserAsync("Newhire");
        using var b = new Browser(fx);
        var forced = await b.SignInAsync(user.Username, user.Password);
        Assert.Equal("/account/password?forced=1", forced.Path);
        forced.ExpectText("You need to set a new password").ExpectText("Password rules");
        Assert.Equal("/account/password?forced=1", (await b.GoAsync("/work")).Path);

        // Each mistake gets its own message (SEC-205); the temporary password can't be kept.
        FormFill Change(Page p, string current, string next, string confirm) => p.FormOf("#new").Set("Current", current).Set("New", next).Set("Confirm", confirm);
        var wrongCurrent = await Change(forced, "Not-The-Temp-1!", SecuritySteps.NewPassword, SecuritySteps.NewPassword).SubmitAsync();
        Assert.Contains("Your current password isn't right.", wrongCurrent.Errors);
        var mismatch = await Change(wrongCurrent, user.Password, SecuritySteps.NewPassword, SecuritySteps.NewPassword + "x").SubmitAsync();
        Assert.Contains("The new passwords don't match.", mismatch.Errors);
        var reused = await Change(mismatch, user.Password, user.Password, user.Password).SubmitAsync();
        Assert.Contains(reused.Errors, e => e.StartsWith("You used this password recently."));
        var changed = await Change(reused, user.Password, SecuritySteps.NewPassword, SecuritySteps.NewPassword).SubmitAsync();
        Assert.Equal("Password changed. Other sessions were signed out.", changed.Toast);
        Assert.Equal("/", (await b.GoAsync("/")).Path);
        (await b.GoAsync("/notifications")).ExpectText("Welcome to Vellum");

        // Inactivated: signed out at once, can't sign in, and no longer offered as a person to pick.
        var inactive = await admin.AdminUserActionAsync(user.Id, "Inactivate", f => f.Set("reason", "Left the company"));
        Assert.Equal($"{user.DisplayName} is inactive.", inactive.Toast);
        Assert.False(await b.IsSignedInAsync());
        Assert.Equal("Your account is inactive. Contact your administrator.", (await b.TrySignInAsync(user.Username, SecuritySteps.NewPassword)).LoginError());
        // Without the right password nothing gives away that the account exists or what state it's in.
        Assert.Equal(BadCredentials, (await b.TrySignInAsync(user.Username, "Some-Guess-123!")).LoginError());
        Assert.DoesNotContain(user.DisplayName, await (await admin.RawGetAsync($"/api/lookup/users?q={user.Username}")).Content.ReadAsStringAsync());
    }

    [Fact] // ADM-101, SAF-010, SAF-011, INV-145, SEC-1001
    public async Task Preference_changes_take_effect_and_are_recorded_and_sensitive_ones_need_a_reason_and_password()
    {
        var original = await fx.SettingsAsync();
        var tag = E2e.Tag();
        using var admin = await fx.SignedInAsync("admin");
        try
        {
            var message = $"Authorized use only. Monitored ({tag}).";
            var saved = await (await admin.GoAsync("/admin/settings")).Form("form[data-adm-settings]").Set("Input.LoginMessage", message).SubmitAsync();
            Assert.Equal("Saved 1 change to system preferences.", saved.Toast);
            using (var anon = new Browser(fx)) (await anon.GoAsync("/login")).ExpectText(message);
            (await admin.GoAsync($"/admin/audit?q={tag}")).ExpectText("System preference changed").ExpectText("Login message");

            // Turning global search off takes it away everywhere (PLT-1501), and turning it back on restores it.
            using var jlee = await fx.SignedInAsync("jlee");
            var draft = await jlee.CreateAsync("document", f => f.Pick("f.doc_type", "procedure").Set("f.title", $"Searchable {tag}"));
            Assert.Contains((await jlee.GoAsync($"/search?q={tag}")).SearchHits(), h => h.Contains(draft.Number()));
            var off = await (await admin.GoAsync("/admin/settings")).Form("form[data-adm-settings]").Set("Input.GlobalSearchEnabled", "false").SubmitAsync();
            Assert.Equal("Saved 1 change to system preferences.", off.Toast);
            var noSearch = await jlee.GoAsync($"/search?q={tag}");
            noSearch.ExpectText("Global search is turned off");
            Assert.Empty(noSearch.SearchHits());
            Assert.Null(noSearch.Q("a.vl-side-link[href='/search']"));
            Assert.DoesNotContain(tag, await (await jlee.RawGetAsync($"/api/search/quick?q={tag}")).Content.ReadAsStringAsync());
            await (await admin.GoAsync("/admin/settings")).Form("form[data-adm-settings]").Set("Input.GlobalSearchEnabled", "true").SubmitAsync();
            Assert.Contains((await jlee.GoAsync($"/search?q={tag}")).SearchHits(), h => h.Contains(draft.Number()));

            // The lockout threshold is compliance-sensitive. In regulated mode it can only be made stricter (SAF-010)…
            Assert.True(original.RegulatedMode, "The test tenant runs in regulated mode.");
            var looser = await (await admin.GoAsync("/admin/settings")).Form("form[data-adm-settings]").Set("Input.LockoutThreshold", 10)
                .Set("Reason", "Fewer lockouts").Set("Password", TestHelpers.Password).SubmitAsync();
            Assert.Contains("Allowing more than", looser.Alert);

            // …and a stricter value still needs a reason and the administrator's password.
            var threshold = original.LockoutThreshold - 1;
            FormFill Lockout(Page p) => p.Form("form[data-adm-settings]").Set("Input.LockoutThreshold", threshold);
            var noReason = await Lockout(await admin.GoAsync("/admin/settings")).SubmitAsync();
            Assert.Contains("Give a reason for changing lockout threshold", noReason.Alert);
            var badPassword = await Lockout(noReason).Set("Reason", $"Align with corporate policy {tag}").Set("Password", "Not-My-Password-1!").SubmitAsync();
            Assert.Contains("Your password isn't right.", badPassword.Alert);
            Assert.Equal(original.LockoutThreshold.ToString(), (await admin.GoAsync("/admin/settings")).One("#s-lock").GetAttribute("value"));
            var ok = await Lockout(await admin.GoAsync("/admin/settings")).Set("Reason", $"Align with corporate policy {tag}").Set("Password", TestHelpers.Password).SubmitAsync();
            Assert.Equal("Saved 1 change to system preferences.", ok.Toast);
            Assert.Equal(threshold.ToString(), ok.One("#s-lock").GetAttribute("value"));

            (await admin.GoAsync($"/admin/audit?q={tag}")).ExpectText("Compliance setting changed").ExpectText($"Align with corporate policy {tag}");
            (await admin.GoAsync("/admin/security-log?user=admin")).ExpectText("Compliance setting changed").ExpectText("Re-authentication failed");
        }
        finally
        {
            await fx.RestoreSettingsAsync(original);
        }
    }

    [Fact] // SEC-404, SEC-1001
    public async Task The_maintenance_login_block_lets_only_the_allowed_people_sign_in()
    {
        var original = await fx.SettingsAsync();
        var message = $"Upgrade in progress until 18:00 ({E2e.Tag()}).";
        using var admin = await fx.SignedInAsync("admin");
        using var working = await fx.SignedInAsync("jlee");
        try
        {
            var on = await (await admin.GoAsync("/admin/sessions")).Form("#login-block")
                .Set("blocked", "true").Set("allowList", "sortiz").Set("message", message).SubmitAsync();
            Assert.Equal("Login block is on. Only admin, sortiz can sign in.", on.Toast);

            using var jlee = new Browser(fx);
            var refused = await jlee.TrySignInAsync("jlee", TestHelpers.Password);
            refused.ExpectText("Sign-in is restricted for maintenance.").ExpectText(message);
            Assert.Equal("Only designated users can sign in during maintenance. Try again later.", refused.LoginError());
            Assert.Equal(BadCredentials, (await jlee.TrySignInAsync("jlee", "Wrong-Password-1!")).LoginError());

            // People on the allow list (and administrators who turned it on) still get in; sessions already open carry on.
            using (var sortiz = new Browser(fx)) Assert.Equal("/", (await sortiz.SignInAsync("sortiz")).Path);
            using (var admin2 = new Browser(fx)) Assert.Equal("/", (await admin2.SignInAsync("admin")).Path);
            Assert.True(await working.IsSignedInAsync());

            var off = await (await admin.GoAsync("/admin/sessions")).Form("#login-block").Set("blocked", "false").SubmitAsync();
            Assert.Equal("Login block is off. Everyone can sign in again.", off.Toast);
            Assert.Equal("/", (await jlee.SignInAsync("jlee")).Path);
            (await admin.GoAsync("/admin/security-log?user=admin")).ExpectText("Login block turned on").ExpectText("Login block turned off");
        }
        finally
        {
            await fx.RestoreSettingsAsync(original);
        }
    }

    [Fact] // SEC-403, SEC-402, SEC-1001
    public async Task Ending_a_session_on_the_sessions_page_signs_that_browser_out_and_session_ids_are_never_shown()
    {
        using var admin = await fx.SignedInAsync("admin");
        var user = await admin.CreateUserAsync("Sessions", mustChange: false);
        using var laptop = new Browser(fx);
        await laptop.SignInAsync(user.Username, user.Password);
        var laptopSid = Assert.Single(await fx.OpenSessionIdsAsync(user.Id));
        using var phone = new Browser(fx);
        await phone.SignInAsync(user.Username, user.Password);
        var phoneSid = Assert.Single((await fx.OpenSessionIdsAsync(user.Id)).Except([laptopSid]));

        // The cookie values are bearer secrets: neither the administrator's pages nor the person's own profile carry them.
        var sessions = await admin.GoAsync("/admin/sessions");
        Assert.DoesNotContain(laptopSid, sessions.Html);
        Assert.DoesNotContain(phoneSid, sessions.Html);
        Assert.DoesNotContain(phoneSid, (await admin.GoAsync($"/admin/users/{user.Id}")).Html);
        Assert.DoesNotContain(phoneSid, (await laptop.GoAsync("/account/profile")).Html);

        var ended = await sessions.Form("form[action*='handler=End']").Set("ids", SessionService.Handle(phoneSid)).SubmitAsync();
        Assert.Equal("Ended 1 session.", ended.Toast);
        Assert.False(await phone.IsSignedInAsync());
        Assert.True(await laptop.IsSignedInAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await fx.ReplaySessionAsync(phoneSid));
        // Pressing Sign out on the page still open in that browser just lands on the sign-in page.
        Assert.StartsWith("/login", (await phone.SignOutAsync()).Url.AbsolutePath);
        Assert.StartsWith("/login", (await phone.GoAsync("/work")).Url.AbsolutePath);
        // Where the idle-timeout script sends a browser whose session ended.
        Assert.Equal("Your session ended. Sign in again to continue.", (await phone.GoAsync("/login?expired=1&returnUrl=%2Fwork")).LoginInfo());

        // "Sign out everywhere" on the user's page ends the rest.
        var everywhere = await admin.AdminUserActionAsync(user.Id, "Logout");
        Assert.Equal($"Signed {user.DisplayName} out of 1 session.", everywhere.Toast);
        Assert.False(await laptop.IsSignedInAsync());
        (await admin.GoAsync($"/admin/security-log?user={user.Username}")).ExpectText("Forced logout");
    }

    [Fact] // PLT-1007, INV-055, SAF-066
    public async Task The_audit_trail_viewer_exports_and_integrity_check_agree_on_what_happened()
    {
        using var jlee = await fx.SignedInAsync("jlee");
        var title = $"Gowning procedure {E2e.Tag()}";
        var draft = await jlee.CreateAsync("document", f => f.Pick("f.doc_type", "procedure").Set("f.title", title));
        var number = draft.Number();
        Assert.Equal("Saved.", (await draft.SaveAsync(f => f.Set("f.title", title + " (rev)"))).Toast);

        var csv = await jlee.RawGetAsync($"{draft.Path}/audit.csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{number}-audit-trail.csv", csv.Content.Headers.ContentDisposition?.FileNameStar ?? csv.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var lines = (await csv.Content.ReadAsStringAsync()).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Seq,UTC time,Actor", lines[0]);
        Assert.Contains(lines, l => l.Contains("\"Created\"") && l.Contains(fx.Person("jlee").DisplayName));
        Assert.Contains(lines, l => l.Contains(title + " (rev)"));

        // The machine-readable export carries the hash chain: every entry points at the one before it.
        var json = JsonDocument.Parse(await (await jlee.RawGetAsync($"{draft.Path}/audit.json")).Content.ReadAsStringAsync()).RootElement;
        var entries = json.EnumerateArray().ToList();
        Assert.Equal("Created", entries[0].GetProperty("Action").GetString());
        for (var i = 1; i < entries.Count; i++)
            Assert.Equal(entries[i - 1].GetProperty("Hash").GetString(), entries[i].GetProperty("PrevHash").GetString());

        // The administrator sees the same history, including the exports themselves.
        using var admin = await fx.SignedInAsync("admin");
        (await admin.GoAsync($"/admin/audit?number={number}")).ExpectText("Created").ExpectText("Audit trail exported");
        var verified = await (await admin.GoAsync("/admin/audit")).Form("form[action*='handler=Verify']").SubmitAsync();
        Assert.StartsWith("/admin/audit?verified=", verified.Path);
        verified.ExpectText("All hash chains verified");
    }

    [Fact] // SEC-1001, SEC-1002, INV-150
    public async Task Failed_sign_ins_for_unknown_usernames_reach_the_security_log_and_its_export()
    {
        var ghost = "ghost." + E2e.Tag().ToLowerInvariant();
        using (var anon = new Browser(fx)) await anon.TrySignInAsync(ghost, "Anything-1234!");
        using var admin = await fx.SignedInAsync("admin");
        (await admin.GoAsync($"/admin/security-log?user={ghost}")).ExpectText("Login failed").ExpectText("Unknown username");
        var csv = await admin.RawGetAsync($"/admin/security-log?handler=Export&user={ghost}");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var body = await csv.Content.ReadAsStringAsync();
        Assert.Contains($"\"{ghost}\"", body);
        Assert.Contains("\"Unknown username\"", body);

        // Only administrators see it.
        using var jlee = await fx.SignedInAsync("jlee");
        (await jlee.GoAsync("/admin/security-log")).ExpectText("You don't have access");
        Assert.NotEqual(HttpStatusCode.OK, (await jlee.RawGetAsync("/admin/security-log?handler=Export")).StatusCode);
    }

    [Fact] // ADM-1002, INV-040, ADM-803, SEC-806
    public async Task Assignment_email_is_logged_and_a_manual_outbox_run_delivers_it_to_the_pickup_folder()
    {
        using var jlee = await fx.SignedInAsync("jlee");
        var doc = await DocumentE2eTests.NewSopAsync(fx, jlee, $"Cleanroom entry {E2e.Tag()}");
        var number = doc.Number();
        await doc.RouteOkAsync("Submit to Document Control");

        using var admin = await fx.SignedInAsync("admin");
        var log = await admin.GoAsync($"/admin/email-log?q={number}");
        log.ExpectText($"{number} assigned to you").ExpectText(fx.Person("pshah").Email);

        // The scheduler is off in tests: a manual run from the task page delivers the queued mail.
        var run = await (await admin.GoAsync("/admin/tasks/outbox-dispatch")).Form("form[action*='handler=Run']").SubmitAsync();
        Assert.Equal("ok", run.ToastKind());
        Assert.StartsWith("Outbound email delivery succeeded. Delivered", run.Toast);
        run.ExpectText("Manual");
        // Each run delivers the oldest batch; other tests may have left more mail queued than one batch holds.
        await SupplierSteps.DeliverMailAsync(fx);

        (await admin.GoAsync($"/admin/email-log?q={number}&status=Delivered")).ExpectText($"{number} assigned to you");
        var pickup = Path.Combine(Path.GetTempPath(), fx.Database, "mail");
        var eml = Directory.GetFiles(pickup, "*.eml").Select(File.ReadAllText).FirstOrDefault(t => t.Contains($"{number} assigned to you") && t.Contains(fx.Person("pshah").Email));
        Assert.NotNull(eml);
        Assert.Contains($"{number} assigned to you", await (await admin.RawGetAsync($"/admin/email-log?handler=Export&q={number}")).Content.ReadAsStringAsync());
    }

    [Fact] // ADM-603, ADM-604, INV-061
    public async Task A_keyword_added_by_an_administrator_is_offered_on_record_forms_until_it_is_disabled()
    {
        var label = $"Validation lab {E2e.Tag()}";
        using var admin = await fx.SignedInAsync("admin");
        var added = await (await admin.GoAsync("/admin/keywords/org_department")).Form("form[action*='handler=Add']").Set("label", label).SubmitAsync();
        Assert.Equal($"Added “{label}”.", added.Toast);
        var dup = await (await admin.GoAsync("/admin/keywords/org_department")).Form("form[action*='handler=Add']").Set("label", label.ToUpperInvariant()).SubmitAsync();
        Assert.Equal("bad", dup.ToastKind());

        using var jlee = await fx.SignedInAsync("jlee");
        var form = await jlee.NewAsync("document");
        Assert.Contains(form.All("select[name='f.department'] option"), o => Page.Squash(o.TextContent) == label);
        var doc = await jlee.CreateAsync("document", f => f.Pick("f.doc_type", "procedure").Set("f.title", $"Lab layout {E2e.Tag()}").Pick("f.department", label));

        // Disabled keywords aren't offered for new values but still show on records that use them (INV-061).
        var keywords = await admin.GoAsync("/admin/keywords/org_department");
        var row = keywords.All("tr[id^='kw-']").First(r => r.TextContent.Contains(label)).Id;
        var disabled = await keywords.Form($"#{row} form[action*='handler=Toggle']").SubmitAsync();
        Assert.Equal("ok", disabled.ToastKind());
        Assert.DoesNotContain((await jlee.NewAsync("document")).All("select[name='f.department'] option"), o => Page.Squash(o.TextContent) == label);
        (await jlee.GoAsync(doc.Path)).ExpectText(label);
    }

    [Fact] // ADM-301
    public async Task Business_calendar_days_can_be_added_and_removed_with_guard_rails()
    {
        using var admin = await fx.SignedInAsync("admin");
        // A weekday in a far year that no other test uses.
        var day = new DateOnly(2031, 1, 1).AddDays(Random.Shared.Next(0, 300));
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        var name = $"Site shutdown {E2e.Tag()}";
        FormFill Add(Page p, DateOnly d, string n, bool working) => p.Form("form[action*='handler=Add']").Set("date", d.ToString("yyyy-MM-dd")).Set("name", n).Set("isWorking", working ? "true" : "false");

        var calendar = await admin.GoAsync("/admin/calendar?year=2031");
        var added = await Add(calendar, day, name, false).SubmitAsync();
        Assert.Equal($"Added {name} ({day:dd MMM yyyy}).", added.Toast);
        (await admin.GoAsync("/admin/calendar?year=2031")).ExpectText(name);

        Assert.Contains("is already in the calendar", (await Add(await admin.GoAsync("/admin/calendar?year=2031"), day, "Again", false).SubmitAsync()).Toast);
        var weekday = day.AddDays(day.DayOfWeek == DayOfWeek.Friday ? 3 : 1);
        Assert.Contains("is already a working day", (await Add(await admin.GoAsync("/admin/calendar?year=2031"), weekday, "Pointless", true).SubmitAsync()).Toast);

        var page = await admin.GoAsync("/admin/calendar?year=2031");
        var removed = await page.FormOf($"button[aria-label='Remove {name}']").SubmitAsync();
        Assert.StartsWith($"Removed {name}", removed.Toast);
        (await admin.GoAsync("/admin/calendar?year=2031")).ExpectNoText(name);
    }

    [Fact] // ADM-401, ADM-404
    public async Task A_new_location_is_offered_on_profiles_until_it_is_disabled()
    {
        var tag = E2e.Tag();
        var name = $"Plant {tag}";
        using var admin = await fx.SignedInAsync("admin");
        var created = await (await admin.GoAsync("/admin/locations?new=true")).Form("form[action*='handler=Save']")
            .Set("Input.Name", name).Set("Input.Code", "P" + tag[..5]).SubmitAsync();
        Assert.Equal($"Created {name}.", created.Toast);
        Assert.Contains((await admin.GoAsync("/admin/users/new")).All("#u-loc option"), o => o.TextContent.Contains(name));

        var location = created.Path;
        var disabled = await (await admin.GoAsync(location)).Form("form[action*='handler=Save']").Set("Input.Disabled", "true").SubmitAsync();
        Assert.Equal($"Saved {name}.", disabled.Toast);
        Assert.DoesNotContain((await admin.GoAsync("/admin/users/new")).All("#u-loc option"), o => o.TextContent.Contains(name));
    }

    [Fact] // ADM-801, ADM-803, ADM-804, ADM-1003
    public async Task Tasks_run_on_demand_only_when_enabled_and_keep_a_run_history()
    {
        using var admin = await fx.SignedInAsync("admin");
        var tasks = await admin.GoAsync("/admin/tasks");
        tasks.ExpectText("Data integrity checker").ExpectText("Outbound email delivery").ExpectText("Session and lock cleanup");

        var ran = await (await admin.GoAsync("/admin/tasks/session-cleanup")).Form("form[action*='handler=Run']").SubmitAsync();
        Assert.StartsWith("Session and lock cleanup succeeded.", ran.Toast);
        try
        {
            var off = await (await admin.GoAsync("/admin/tasks/session-cleanup")).Form("form[action*='handler=Toggle']").SubmitAsync();
            Assert.Equal("Session and lock cleanup is disabled. It won't run until you enable it again.", off.Toast);
            var refused = await off.Form("form[action*='handler=Run']").SubmitAsync();
            Assert.Equal("Session and lock cleanup is disabled. Enable it before running it.", refused.Toast);
        }
        finally
        {
            var page = await admin.GoAsync("/admin/tasks/session-cleanup");
            if (page.Q("form[action*='handler=Toggle'] input[name=enabled][value=true]") != null)
                Assert.Equal("Session and lock cleanup is enabled.", (await page.Form("form[action*='handler=Toggle']").SubmitAsync()).Toast);
        }

        // Deletion history is part of the same logs area and is administrators only.
        (await admin.GoAsync("/admin/deletions")).ExpectText("Deletion history");
        using var jlee = await fx.SignedInAsync("jlee");
        (await jlee.GoAsync("/admin/tasks")).ExpectText("You don't have access");
    }
}
