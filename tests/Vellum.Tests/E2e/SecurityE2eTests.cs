using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Tests.E2e;

/// <summary>Sign-in, sessions, MFA, application access and the account pages, end to end through the web pages.</summary>
[Collection("vellum")]
public class SecurityE2eTests(VellumFixture fx)
{
    private const string BadCredentials = "The username or password isn't right.";

    [Fact] // SEC-101, SEC-205, INV-150, SEC-106
    public async Task Unknown_users_and_wrong_passwords_get_the_same_answer_and_deep_links_survive_sign_in()
    {
        using var b = new Browser(fx);
        var login = await b.GoAsync("/work?filter=week");
        Assert.Equal("/login", login.Url.AbsolutePath);
        Assert.Equal("/work?filter=week", login.One("form[action*='handler=Identify'] input[name=ReturnUrl]").GetAttribute("value"));

        // Unknown and real usernames get the same next step, so the page doesn't reveal who has an account.
        var unknownStep = await login.Form("form[action*='handler=Identify']").Set("Username", "no.such.person").SubmitAsync();
        var knownStep = await (await b.GoAsync(login.Path)).Form("form[action*='handler=Identify']").Set("Username", "cwu").SubmitAsync();
        Assert.Equal(unknownStep.Text.Replace("no.such.person", "USER"), knownStep.Text.Replace("cwu", "USER"));

        var unknown = await unknownStep.Form("form[action*='handler=Password']").Set("Password", TestHelpers.Password).SubmitAsync();
        Assert.Equal(BadCredentials, unknown.LoginError());
        var wrong = await knownStep.Form("form[action*='handler=Password']").Set("Password", "Not-Her-Password-1!").SubmitAsync();
        Assert.Equal(BadCredentials, wrong.LoginError());

        // The right password, after a wrong one, still lands on the page asked for.
        var landed = await wrong.Form("form[action*='handler=Password']").Set("Password", TestHelpers.Password).SubmitAsync();
        Assert.Equal("/work?filter=week", landed.Path);

        // A return URL pointing at another site is ignored (no open redirect).
        using var other = new Browser(fx);
        var home = await other.TrySignInAsync("cwu", TestHelpers.Password, returnUrl: "https://evil.example/phish");
        Assert.Equal("/", home.Path);
    }

    [Fact] // SEC-702, SEC-205, SEC-1001, SEC-403
    public async Task Repeated_wrong_passwords_lock_the_account_until_an_administrator_unlocks_it()
    {
        using var admin = await fx.SignedInAsync("admin");
        var threshold = int.Parse((await admin.GoAsync("/admin/settings")).One("#s-lock").GetAttribute("value")!);
        var user = await admin.CreateUserAsync("Lockout", mustChange: false, reportsTo: fx.Uid("sortiz"));

        using var b = new Browser(fx);
        for (var i = 1; i <= threshold; i++)
            Assert.Equal(BadCredentials, (await b.TrySignInAsync(user.Username, $"Wrong-Guess-{i}!")).LoginError());

        // Now even the right password is refused, and the person is told why.
        var locked = await b.TrySignInAsync(user.Username, user.Password);
        Assert.Equal("/login?handler=Password", locked.Path);
        Assert.Contains("Your account is locked", locked.LoginError());

        // Their manager hears about it, and the administrator sees the reason on the profile.
        using var sortiz = await fx.SignedInAsync("sortiz");
        (await sortiz.GoAsync("/notifications")).ExpectText($"{user.DisplayName}'s account was locked");
        (await admin.GoAsync($"/admin/users/{user.Id}")).ExpectText($"This account is disabled: Locked after {threshold} failed sign-in attempts");

        var unlocked = await admin.AdminUserActionAsync(user.Id, "Unlock");
        Assert.Equal($"{user.DisplayName} can sign in again.", unlocked.Toast);
        Assert.Equal("/", (await b.SignInAsync(user.Username, user.Password)).Path);

        var log = await admin.GoAsync($"/admin/security-log?user={user.Username}");
        log.ExpectText("Account locked").ExpectText("Account unlocked").ExpectText("Wrong password (attempt 1)");
    }

    [Fact] // SEC-401, SEC-402, SEC-106
    public async Task Signing_out_ends_the_session_on_the_server_and_protected_pages_ask_you_to_sign_in()
    {
        using var admin = await fx.SignedInAsync("admin");
        var user = await admin.CreateUserAsync("Signout", mustChange: false);
        using var b = new Browser(fx);
        await b.SignInAsync(user.Username, user.Password);
        var sid = Assert.Single(await fx.OpenSessionIdsAsync(user.Id));
        Assert.Equal(HttpStatusCode.OK, await fx.ReplaySessionAsync(sid));

        var signedOut = await b.SignOutAsync();
        Assert.Equal("/login?signedout=1", signedOut.Path);
        Assert.Equal("You've signed out.", signedOut.LoginInfo());

        // A copy of the old cookie is worthless now, and protected pages send the browser to sign in (keeping the deep link).
        Assert.Equal(HttpStatusCode.Unauthorized, await fx.ReplaySessionAsync(sid));
        Assert.Empty(await fx.OpenSessionIdsAsync(user.Id));
        var again = await b.GoAsync("/account/profile");
        Assert.Equal("/login?returnUrl=%2Faccount%2Fprofile", again.Path);
        Assert.False(await b.IsSignedInAsync());
    }

    // ------------------------------------------------------------------ MFA

    [Fact] // SEC-301, SEC-304, SEC-305, SEC-306, SEC-310
    public async Task An_authenticator_set_up_on_the_profile_is_asked_for_at_sign_in_until_an_administrator_removes_it()
    {
        using var admin = await fx.SignedInAsync("admin");
        var user = await admin.CreateUserAsync("Totp", mustChange: false);
        using var b = new Browser(fx);
        await b.SignInAsync(user.Username, user.Password);

        // The person copies the setup key into their app; a wrong code is refused and the same key stays on screen.
        var setup = await (await b.GoAsync("/account/profile")).Form("form[action*='handler=MfaStart']").SubmitAsync();
        var secret = setup.One("code.fs-5").TextContent.Replace(" ", "");
        var refused = await setup.Form("form[action*='handler=MfaConfirm']").Set("code", SecuritySteps.WrongTotp(secret)).SubmitAsync();
        Assert.Contains("That code didn't match", refused.Alert);
        Assert.Equal(secret, refused.One("code.fs-5").TextContent.Replace(" ", ""));
        var on = await refused.Form("form[action*='handler=MfaConfirm']").Set("code", SecuritySteps.Totp(secret)).SubmitAsync();
        Assert.Equal("Two-step verification is on.", on.Toast);
        await b.SignOutAsync();

        // From now on the password alone doesn't get them in.
        var challenge = await b.TrySignInAsync(user.Username, user.Password);
        challenge.ExpectText("Enter the 6-digit code from your authenticator app.");
        Assert.False(await b.IsSignedInAsync());
        var wrong = await challenge.Form("form[action*='handler=Mfa']").Set("Code", SecuritySteps.WrongTotp(secret)).SubmitAsync();
        Assert.Equal("That code isn't right. Check your authenticator app and try again.", wrong.LoginError());
        Assert.False(await b.IsSignedInAsync());
        Assert.Equal("/", (await wrong.Form("form[action*='handler=Mfa']").Set("Code", SecuritySteps.Totp(secret)).SubmitAsync()).Path);

        // A lost phone: the administrator removes the factor, which signs the person out and forces a new password.
        (await admin.GoAsync($"/admin/users/{user.Id}")).ExpectText("Active · authenticator app");
        var removed = await admin.AdminUserActionAsync(user.Id, "RemoveMfa");
        Assert.Equal($"Removed {user.DisplayName}'s authenticator. They were signed out and must reset their password.", removed.Toast);
        Assert.False(await b.IsSignedInAsync());
        Assert.StartsWith("/account/password", (await b.SignInAsync(user.Username, user.Password)).Path);

        (await admin.GoAsync($"/admin/security-log?user={user.Username}"))
            .ExpectText("MFA enrollment completed").ExpectText("MFA verification failed").ExpectText("MFA factor removed");
    }

    [Fact] // SEC-201, SEC-301
    public async Task An_expired_password_must_be_changed_even_when_signing_in_with_an_authenticator()
    {
        var expiryDays = (await fx.SettingsAsync()).PasswordExpiryDays;
        Assert.True(expiryDays > 0, "Password expiration is on by default (SEC-201).");
        using var admin = await fx.SignedInAsync("admin");
        var user = await admin.CreateUserAsync("Expired", mustChange: false);
        using var b = new Browser(fx);
        await b.SignInAsync(user.Username, user.Password);
        var setup = await (await b.GoAsync("/account/profile")).Form("form[action*='handler=MfaStart']").SubmitAsync();
        var secret = setup.One("code.fs-5").TextContent.Replace(" ", "");
        await setup.Form("form[action*='handler=MfaConfirm']").Set("code", SecuritySteps.Totp(secret)).SubmitAsync();
        await b.SignOutAsync();

        // Time passes: the password is now older than the expiration period (no page can age a password, so set it directly).
        using (var s = fx.As("admin"))
            await s.Db().Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.PasswordChangedAt, DateTime.UtcNow.AddDays(-expiryDays - 1)));

        var challenge = await b.TrySignInAsync(user.Username, user.Password);
        var landed = await challenge.Form("form[action*='handler=Mfa']").Set("Code", SecuritySteps.Totp(secret)).SubmitAsync();
        Assert.Equal("/account/password?forced=1", landed.Path);
        landed.ExpectText("You need to set a new password");
    }

    [Fact] // SEC-302, SEC-310
    public async Task When_mfa_is_required_people_without_an_authenticator_enroll_before_they_get_in()
    {
        var original = await fx.SettingsAsync();
        using var admin = await fx.SignedInAsync("admin");
        var user = await admin.CreateUserAsync("Enroll", mustChange: false);
        try
        {
            var saved = await (await admin.GoAsync("/admin/settings")).Form("form[data-adm-settings]")
                .Set("Input.MfaRequired", "true").Set("Reason", "Every local account needs MFA").Set("Password", TestHelpers.Password).SubmitAsync();
            Assert.Equal("Saved 1 change to system preferences.", saved.Toast);

            // The right password leads to enrollment, not into the application.
            using var b = new Browser(fx);
            var enroll = await b.TrySignInAsync(user.Username, user.Password);
            enroll.ExpectText("Set up two-step verification");
            Assert.False(await b.IsSignedInAsync());

            // An incomplete enrollment can be retried with the same key; the right code finishes it and signs in.
            var secret = enroll.One("[data-setup-key]").TextContent.Replace(" ", "");
            var retry = await enroll.Form("form[action*='handler=Enroll']").Set("Code", SecuritySteps.WrongTotp(secret)).SubmitAsync();
            Assert.Contains("That code didn't match", retry.LoginError());
            Assert.False(await b.IsSignedInAsync());
            Assert.Equal(secret, retry.One("[data-setup-key]").TextContent.Replace(" ", ""));
            Assert.Equal("/", (await retry.Form("form[action*='handler=Enroll']").Set("Code", SecuritySteps.Totp(secret)).SubmitAsync()).Path);

            await b.SignOutAsync();
            (await b.TrySignInAsync(user.Username, user.Password)).ExpectText("Enter the 6-digit code from your authenticator app.");
            (await admin.GoAsync($"/admin/users/{user.Id}")).ExpectText("Active · authenticator app");
            (await admin.GoAsync($"/admin/security-log?user={user.Username}")).ExpectText("MFA enrollment started").ExpectText("MFA enrollment completed");
        }
        finally
        {
            await fx.RestoreSettingsAsync(original);
        }
    }

    // ------------------------------------------------------------------ application access

    private static string ProductCode() => new([.. Enumerable.Range(0, 3).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    /// <summary>Everything a person without access to Adverse Event Reporting tries: nothing shows the record, nothing opens it.</summary>
    private static async Task ExpectNoAccessAsync(Browser b, string recordUrl, string number, string tag)
    {
        await b.GoAsync("/apps/adverse_events", HttpStatusCode.NotFound);
        await b.GoAsync(recordUrl, HttpStatusCode.NotFound);
        await b.GoAsync(recordUrl + "/print", HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await b.RawGetAsync(recordUrl + "/audit.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.RawGetAsync("/apps/adverse_events/export.csv?view=product_codes")).StatusCode);
        Assert.Empty((await b.GoAsync($"/search?q={tag}")).SearchHits());
        Assert.DoesNotContain(tag, await (await b.RawGetAsync($"/api/lookup/records?q={tag}")).Content.ReadAsStringAsync());
        Assert.DoesNotContain(tag, await (await b.RawGetAsync($"/api/search/quick?q={tag}")).Content.ReadAsStringAsync());
        (await b.GoAsync("/apps")).ExpectNoText("Adverse Event Reporting");
        (await b.GoAsync("/records/new?form=aer_product_code")).ExpectText("You don't have access");
    }

    [Fact] // SEC-501, SEC-503, SEC-504, SEC-506, SEC-521, INV-081, INV-083
    public async Task Roles_an_administrator_grants_and_revokes_decide_what_a_person_can_find_open_and_create()
    {
        var tag = E2e.Tag();
        using var rnovak = await fx.SignedInAsync("rnovak");
        var rec = await rnovak.CreateAsync("aer_product_code", f => f.Set("f.pc_code", ProductCode()).Set("f.pc_device_name", $"Infusion pump {tag}"));
        var (url, number) = (rec.Path, rec.Number());

        // Adverse Event Reporting has no default access, and Kiran has no grant: deny by default.
        using var kpatel = await fx.SignedInAsync("kpatel");
        await ExpectNoAccessAsync(kpatel, url, number, tag);

        using var admin = await fx.SignedInAsync("admin");
        var kiran = fx.Person("kpatel");
        var regulatory = (await admin.GoAsync("/admin/groups")).Link("Regulatory Affairs").GetAttribute("href")!;
        try
        {
            // Reader on the whole application: the record can be found, opened and exported, but nothing can be created.
            var granted = await (await admin.GoAsync("/admin/access/apps/adverse_events")).Form("form[data-adm-grant]")
                .Set("userId", kiran.Id.ToString()).Pick("role", "Reader").SubmitAsync();
            Assert.Equal($"Granted Reader to {kiran.DisplayName}.", granted.Toast);
            (await admin.GoAsync($"/admin/access/users/{kiran.Id}")).ExpectText("Adverse Event Reporting");

            (await kpatel.GoAsync("/apps/adverse_events?view=product_codes")).ExpectText($"Infusion pump {tag}");
            var opened = await kpatel.GoAsync(url);
            opened.ExpectText($"Infusion pump {tag}");
            Assert.Null(opened.Q("[data-save-button]"));
            Assert.Contains((await kpatel.GoAsync($"/search?q={tag}")).SearchHits(), h => h.Contains(number));
            Assert.Contains(number, await (await kpatel.RawGetAsync($"/api/lookup/records?q={tag}")).Content.ReadAsStringAsync());
            Assert.Contains(tag, await (await kpatel.RawGetAsync("/apps/adverse_events/export.csv?view=product_codes")).Content.ReadAsStringAsync());
            (await kpatel.GoAsync("/records/new?form=aer_product_code")).ExpectText("You don't have access");

            // Membership of Regulatory Affairs (Author on the application) lets her create product codes.
            var joined = await (await admin.GoAsync(regulatory)).Form("form[action*='handler=Add']").Set("users", kiran.Id.ToString()).SubmitAsync();
            Assert.Equal($"Added {kiran.DisplayName} to Regulatory Affairs.", joined.Toast);
            var own = await kpatel.CreateAsync("aer_product_code", f => f.Set("f.pc_code", ProductCode()).Set("f.pc_device_name", $"Syringe driver {tag}"));
            var ownUrl = own.Path;

            // Revoking both takes everything away again, including the record she created herself.
            var left = await (await admin.GoAsync(regulatory)).FormOf($"button[aria-label='Remove {kiran.DisplayName}']").SubmitAsync();
            Assert.Equal($"Removed {kiran.DisplayName} from Regulatory Affairs.", left.Toast);
            var revoked = await (await admin.GoAsync("/admin/access/apps/adverse_events")).FormOf($"button[aria-label='Remove grant for {kiran.DisplayName}']").SubmitAsync();
            Assert.Equal("ok", revoked.ToastKind());
            await ExpectNoAccessAsync(kpatel, url, number, tag);
            await kpatel.GoAsync(ownUrl, HttpStatusCode.NotFound);
        }
        finally
        {
            using var s = fx.As("admin");
            var groupId = int.Parse(regulatory.Split('/').Last());
            await s.ServiceProvider.GetRequiredService<GroupAdminService>().RemoveMemberAsync(groupId, kiran.Id);
            var access = s.ServiceProvider.GetRequiredService<AccessAdminService>();
            foreach (var g in (await access.GrantsAsync()).Where(g => g.Grant.PrincipalKind == PrincipalKind.User && g.Grant.PrincipalId == kiran.Id))
                await access.RemoveGrantAsync(g.Grant.Id);
        }
    }

    [Fact] // SEC-506, SEC-519, INV-082, PLT-640, PLT-642
    public async Task Being_assigned_a_record_does_not_let_someone_create_forms_that_are_not_its_activities()
    {
        // Kiran writes a draft document, so she is its assignee. She has no access to Adverse Event Reporting.
        using var kpatel = await fx.SignedInAsync("kpatel");
        var draft = await kpatel.CreateAsync("document", f => f.Pick("f.doc_type", "procedure").Set("f.title", $"Training matrix {E2e.Tag()}"));
        var parent = draft.RecordId;

        // The activities the record page offers still work...
        var offered = draft.All("a[href^='/records/new?form=']").Select(a => a.GetAttribute("href")!).ToList();
        Assert.Contains(offered, h => h.StartsWith("/records/new?form=action_item&"));
        await kpatel.GoAsync(offered.First(h => h.StartsWith("/records/new?form=action_item&")));

        // ...but naming any other form next to the parent doesn't borrow the parent's rights.
        var device = $"Borrowed rights {E2e.Tag()}";
        Assert.Equal(HttpStatusCode.BadRequest, (await kpatel.RawGetAsync($"/records/new?form=aer_product_code&parent={parent}")).StatusCode);
        var posted = await (await kpatel.GoAsync(draft.Path)).Form("form[action$='handler=Comment']").Remove("text")
            .Set("f.pc_code", ProductCode()).Set("f.pc_device_name", device).SubmitToAsync($"/records/new?form=aer_product_code&parent={parent}");
        Assert.Equal(HttpStatusCode.BadRequest, posted.Status);
        using var rnovak = await fx.SignedInAsync("rnovak");
        Assert.Empty((await rnovak.GoAsync($"/search?q={Uri.EscapeDataString(device)}")).SearchHits());
    }

    // ------------------------------------------------------------------ account

    [Fact] // PLT-1404, SEC-201, SEC-205, SEC-402
    public async Task People_set_their_time_zone_end_their_other_sessions_and_change_their_password()
    {
        using var admin = await fx.SignedInAsync("admin");
        var user = await admin.CreateUserAsync("Account", mustChange: false);
        using var laptop = new Browser(fx);
        using var phone = new Browser(fx);
        await laptop.SignInAsync(user.Username, user.Password);
        await phone.SignInAsync(user.Username, user.Password);

        var saved = await (await laptop.GoAsync("/account/profile")).Form("form[action*='handler=Preferences']")
            .Pick("timeZone", "Asia/Tokyo").Pick("theme", "dark").SubmitAsync();
        Assert.Equal("Preferences saved.", saved.Toast);
        Assert.Equal("Asia/Tokyo", saved.One("#tz option[selected]").GetAttribute("value"));
        Assert.Contains("UTC+09:00", saved.All("dl.vl-kv dt").First(dt => dt.TextContent == "Last sign-in").NextElementSibling!.TextContent);

        // The profile lists both sessions; ending the other one signs that browser out.
        Assert.Single(saved.All("form[action*='handler=EndSession']"));
        var ended = await saved.Form("form[action*='handler=EndSession']").SubmitAsync();
        Assert.Equal("Session ended.", ended.Toast);
        Assert.False(await phone.IsSignedInAsync());
        Assert.True(await laptop.IsSignedInAsync());

        // Changing the password: a wrong current password is refused; success signs out every other session.
        await phone.SignInAsync(user.Username, user.Password);
        var page = await laptop.GoAsync("/account/password");
        var wrong = await page.FormOf("#new").Set("Current", "Not-Mine-123!").Set("New", SecuritySteps.NewPassword).Set("Confirm", SecuritySteps.NewPassword).SubmitAsync();
        Assert.Equal(["Your current password isn't right."], wrong.Errors);
        var changed = await wrong.FormOf("#new").Set("Current", user.Password).Set("New", SecuritySteps.NewPassword).Set("Confirm", SecuritySteps.NewPassword).SubmitAsync();
        Assert.Equal("Password changed. Other sessions were signed out.", changed.Toast);
        Assert.True(await laptop.IsSignedInAsync());
        Assert.False(await phone.IsSignedInAsync());
        Assert.Equal(BadCredentials, (await phone.TrySignInAsync(user.Username, user.Password)).LoginError());
        Assert.Equal("/", (await phone.SignInAsync(user.Username, SecuritySteps.NewPassword)).Path);
    }

    [Fact] // PLT-1201, PLT-1402, PLT-604
    public async Task Routing_puts_the_record_in_the_new_assignees_work_queue_and_notifications()
    {
        using var jlee = await fx.SignedInAsync("jlee");
        var doc = await DocumentE2eTests.NewSopAsync(fx, jlee, $"Label reconciliation {E2e.Tag()}");
        var (url, number) = (doc.Path, doc.Number());
        await doc.RouteOkAsync("Submit to Document Control");

        using var pshah = await fx.SignedInAsync("pshah");
        var inbox = await pshah.GoAsync("/notifications");
        inbox.ExpectText($"{number} assigned to you");
        Assert.NotNull(inbox.Q($"a[href='{url}']"));
        (await pshah.GoAsync("/work")).ExpectText(number);

        await (await pshah.GoAsync(url)).RouteOkAsync("Format checked: send for initial approval");
        (await pshah.GoAsync("/work")).ExpectNoText(number);
        using var sortiz = await fx.SignedInAsync("sortiz");
        (await sortiz.GoAsync("/work")).ExpectText(number);
        (await sortiz.GoAsync("/notifications")).ExpectText($"{number} assigned to you");
        Assert.Contains("\"unread\":", await (await sortiz.RawGetAsync("/api/notifications")).Content.ReadAsStringAsync());
    }

    [Fact] // PLT-1302, PLT-1305, PLT-1306
    public async Task An_out_of_office_approver_delegates_and_the_delegate_signs_on_their_behalf()
    {
        var title = $"Batch record review {E2e.Tag()}";
        using var jlee = await fx.SignedInAsync("jlee");
        var doc = await jlee.CreateAsync("document", f => f
            .Pick("f.doc_type", "procedure").Set("f.title", title).Set("f.owner", fx.Uid("sortiz").ToString())
            .Set("f.reviewers", fx.Uids("tbrooks", "cwu")).Pick("f.department", "operations")
            .Set("f.revision_reason", "Delegation test.").Pick("f.training_required", "no").Set("f.training_justification", "Administrative.")
            .Attach("file.primary_file", "review.pdf", E2e.Pdf(title), "application/pdf"));
        var (url, number) = (doc.Path, doc.Number());
        await doc.RouteOkAsync("Submit to Document Control");
        using var pshah = await fx.SignedInAsync("pshah");
        await (await pshah.GoAsync(url)).RouteOkAsync("Format checked: send for initial approval");

        var sam = fx.Person("sortiz");
        var dana = fx.Person("dokafor");
        using var sortiz = await fx.SignedInAsync("sortiz");
        using var dokafor = await fx.SignedInAsync("dokafor");
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var sent = await (await sortiz.GoAsync("/account/delegations")).Form("form[action*='handler=Create']")
                .Set("delegateId", dana.Id.ToString()).Set("start", today.ToString("yyyy-MM-dd")).Set("end", today.AddDays(3).ToString("yyyy-MM-dd"))
                .Set("reason", "Out of office").SubmitAsync();
            Assert.Equal($"Delegation sent to {dana.DisplayName} for approval.", sent.Toast);

            // Nothing is delegated until the delegate accepts.
            (await dokafor.GoAsync("/work?filter=delegated")).ExpectNoText(number);
            (await dokafor.GoAsync("/notifications")).ExpectText($"{sam.DisplayName} asked you to cover their assignments");
            var accepted = await (await dokafor.GoAsync("/account/delegations")).FormOf("form[action*='handler=Decide'] button[value=true]")
                .SubmitAsync("form[action*='handler=Decide'] button[value=true]");
            Assert.Equal("Delegation accepted. Their assignments now appear in your work.", accepted.Toast);

            (await dokafor.GoAsync("/work?filter=delegated")).ExpectText(number);
            var signed = await (await dokafor.GoAsync(url)).RouteOkAsync("Approve: send to reviewers");
            Assert.Equal("Review", signed.Phase());
            Assert.Contains(signed.All(".vl-sig"), s => Page.Squash(s.TextContent).Contains($"{dana.DisplayName}") && Page.Squash(s.TextContent).Contains($"On behalf of {sam.DisplayName}"));

            var ended = await (await sortiz.GoAsync("/account/delegations")).Form("form[action*='handler=End']").SubmitAsync();
            Assert.Equal("Delegation ended.", ended.Toast);
            (await dokafor.GoAsync("/work?filter=delegated")).ExpectNoText(number);
        }
        finally
        {
            using var s = fx.As("admin");
            await s.Db().Delegations.Where(d => d.RequesterId == sam.Id && d.DelegateId == dana.Id && d.Status != DelegationStatus.Completed)
                .ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DelegationStatus.Completed));
        }
    }

    [Fact] // PLT-1302, INV-081, DOC-080
    public async Task Delegated_work_the_delegate_cannot_open_stays_out_of_their_work_queue()
    {
        // A draft SOP is readable only by its owner, reviewers and Document Control (DOC-080); Noel, a production operator, is none of those.
        using var jlee = await fx.SignedInAsync("jlee");
        var doc = await DocumentE2eTests.NewSopAsync(fx, jlee, $"Allergen control {E2e.Tag()}");
        var (url, number) = (doc.Path, doc.Number());
        await doc.RouteOkAsync("Submit to Document Control");
        using var pshah = await fx.SignedInAsync("pshah");
        await (await pshah.GoAsync(url)).RouteOkAsync("Format checked: send for initial approval");

        var sam = fx.Person("sortiz");
        var noel = fx.Person("nflores");
        using var sortiz = await fx.SignedInAsync("sortiz");
        using var nflores = await fx.SignedInAsync("nflores");
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await (await sortiz.GoAsync("/account/delegations")).Form("form[action*='handler=Create']")
                .Set("delegateId", noel.Id.ToString()).Set("start", today.ToString("yyyy-MM-dd")).Set("end", today.AddDays(1).ToString("yyyy-MM-dd")).SubmitAsync();
            var accepted = await (await nflores.GoAsync("/account/delegations")).FormOf("form[action*='handler=Decide'] button[value=true]")
                .SubmitAsync("form[action*='handler=Decide'] button[value=true]");
            Assert.Equal("ok", accepted.ToastKind());

            await nflores.GoAsync(url, HttpStatusCode.NotFound);
            (await nflores.GoAsync("/work?filter=delegated")).ExpectNoText(number).ExpectNoText("Allergen control");
            (await nflores.GoAsync("/")).ExpectNoText(number);
        }
        finally
        {
            using var s = fx.As("admin");
            await s.Db().Delegations.Where(d => d.RequesterId == sam.Id && d.DelegateId == noel.Id && d.Status != DelegationStatus.Completed)
                .ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DelegationStatus.Completed));
        }
    }
}
