using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Portal;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>
/// Supplier portal (SUP-004, SEC-804, INV-088, CAPA-011, AUD-031, SUP-036): external users are confined to /portal by a central,
/// deny-by-default boundary; they read only records shared with their own supplier and only the fields meant for them; their
/// responses go through the engine, attributed to them with the source "Supplier portal".
/// </summary>
[Collection("vellum")]
public class PortalTests(VellumFixture fx)
{
    private const string Pw = TestHelpers.Password;
    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    // ------------------------------------------------------------------ fixtures

    private async Task<Record> SupplierAsync(string? name = null)
    {
        using var cwu = fx.As("cwu");
        return await cwu.CreateAsync("supplier", new()
        {
            ["name"] = name ?? "Portal Supplier " + U(), ["code"] = "V-" + U(), ["supplier_type"] = "component", ["risk_class"] = "medium",
            ["owner"] = cwu.UserId("cwu"), ["scope"] = "Molded parts",
        }, rows: new() { ["contacts"] = [new() { ["contact_name"] = "Robin Doe", ["email"] = $"robin.{U().ToLowerInvariant()}@supplier.example" }] });
    }

    private static string LatestLink(VellumDbContext db, string email)
    {
        var payload = db.Outbox.AsNoTracking().Where(o => o.Payload.Contains(email)).OrderByDescending(o => o.Id).Select(o => o.Payload).First();
        var body = JsonDocument.Parse(payload).RootElement.GetProperty("body").GetString()!;
        return Regex.Match(body, @"welcome\?token=([A-Za-z0-9_%\-]+)").Groups[1].Value;
    }

    /// <summary>An activated portal contact of <paramref name="supplierId"/> (invited by cwu, password set from the link).</summary>
    private async Task<(int Id, string Username)> PortalUserAsync(Guid supplierId)
    {
        var email = $"contact.{U().ToLowerInvariant()}@supplier.example";
        string token;
        using (var cwu = fx.As("cwu"))
        {
            var res = await cwu.ServiceProvider.GetRequiredService<PortalAccounts>().InviteAsync(supplierId, "Pat " + U(), email, "Quality Manager");
            Assert.True(res.Success, string.Join("; ", res.Errors ?? []));
            token = Uri.UnescapeDataString(LatestLink(cwu.Db(), email));
        }
        using var anon = fx.As(null);
        var (ok, errors, user) = await anon.ServiceProvider.GetRequiredService<PortalAccounts>().SetPasswordAsync(token, Pw, Pw, null);
        Assert.True(ok, string.Join("; ", errors));
        return (user!.Id, user.Username);
    }

    /// <summary>A SCAR with the supplier (Assign to Supplier), carrying internal-only data, shared with the supplier's portal.</summary>
    private async Task<(Record Scar, int ShareId)> SharedScarAsync(Guid supplierId, string secret)
    {
        Record scar;
        using (var cwu = fx.As("cwu"))
        {
            scar = await cwu.CreateAsync("scar", new()
            {
                ["subject"] = "Burrs on ports " + U(), ["owner"] = cwu.UserId("cwu"), ["priority"] = "high",
                ["description"] = "Burrs found in 12 of 50 manifolds.", ["supplier_name"] = "Portal supplier", ["part_number"] = "40-1180",
                ["containment_required"] = false, ["response_due"] = BusinessCalendar.Today.AddDays(10),
            });
            Assert.True((await cwu.RouteAsync(scar.Id, "initial_approval", assignees: [cwu.UserId("sortiz")])).Ok);
        }
        using (var so = fx.As("sortiz")) Assert.True((await so.RouteAsync(scar.Id, "assign_to_supplier")).Ok);
        using (var cwu = fx.As("cwu"))
        {
            // Internal-only content: verification section, a comment and the internal email log.
            Assert.True((await cwu.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(scar.Id, new()
            {
                ["verification_method"] = "VERIFY-" + secret, ["scar_verifier"] = cwu.UserId("jlee"),
            })).Ok);
            await cwu.ServiceProvider.GetRequiredService<RecordService>().AddCommentAsync(scar.Id, "INTERNAL-COMMENT-" + secret);
            var sent = await SupplierTests.ActAsync(cwu, scar.Id, "portal-send", new()
            {
                ["supplier"] = supplierId.ToString(), ["due"] = BusinessCalendar.Today.AddDays(10).ToString("yyyy-MM-dd"), ["message"] = "Please respond.",
            });
            Assert.True(sent.Success, string.Join("; ", sent.Errors ?? []));
            var share = await cwu.Db().Set<ExternalShare>().AsNoTracking().FirstAsync(s => s.RecordId == scar.Id);
            return (scar, share.Id);
        }
    }

    private HttpClient Client() => fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>Sign in through the real login page (identifier-first, password, session cookie).</summary>
    private async Task<HttpClient> SignedInAsync(string username)
    {
        var c = Client();
        var page = await c.GetStringAsync($"/login?u={Uri.EscapeDataString(username)}");
        var res = await c.PostAsync("/login?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = username, ["Password"] = Pw, ["__RequestVerificationToken"] = AntiforgeryToken(page),
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return c;
    }

    // ------------------------------------------------------------------ SEC-804: the boundary

    [Fact] // SEC-804: every internal page and endpoint refuses external users (deny by default, enforced centrally)
    public async Task External_users_are_refused_on_every_internal_route()
    {
        var supplier = await SupplierAsync();
        var (_, username) = await PortalUserAsync(supplier.Id);
        var (scar, _) = await SharedScarAsync(supplier.Id, U());
        var client = await SignedInAsync(username);

        var endpoints = fx.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var failures = new List<string>();
        var checkedRoutes = 0;
        foreach (var e in endpoints)
        {
            var raw = "/" + (e.RoutePattern.RawText ?? "").TrimStart('/');
            if (IsStaticAsset(e.RoutePattern, raw) || IsAllowedForExternals(raw)) continue;
            var url = Fill(e.RoutePattern, scar.Id);
            var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } m ? m : ["GET", "POST"];
            foreach (var method in methods)
            {
                var req = new HttpRequestMessage(new HttpMethod(method), url);
                if (method != "GET" && method != "HEAD") req.Content = new FormUrlEncodedContent([]);
                var res = await client.SendAsync(req);
                checkedRoutes++;
                if (raw == "/")
                {
                    if (res.StatusCode != HttpStatusCode.Redirect || res.Headers.Location?.OriginalString != "/portal") failures.Add($"{method} {url} → {(int)res.StatusCode}");
                }
                else if (res.StatusCode != HttpStatusCode.Forbidden) failures.Add($"{method} {url} → {(int)res.StatusCode}");
            }
        }
        Assert.True(checkedRoutes > 100, $"Only {checkedRoutes} internal routes were checked: is the endpoint data source complete?");
        Assert.Empty(failures);

        // The refusal itself is rendered in the portal's own chrome, never the internal shell.
        var body = await (await client.GetAsync($"/records/{scar.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("part of the supplier portal", body);
        Assert.DoesNotContain("vl-sidebar", body);
        Assert.DoesNotContain(scar.Title, body);

        // ...while the portal and the endpoints it needs stay reachable.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/portal")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/session/status")).StatusCode);
    }

    private static bool IsStaticAsset(RoutePattern p, string raw) =>
        raw.StartsWith("/lib/") || raw.StartsWith("/css/") || raw.StartsWith("/js/") || raw.StartsWith("/img/") ||
        (p.Parameters.Count == 0 && raw.Split('/').Last().Contains('.'));

    /// <summary>Mirror of ExternalUserBoundary's allow-list (the portal itself plus login, logout, error and session pings).</summary>
    private static bool IsAllowedForExternals(string raw) =>
        ExternalUserBoundaryAllowList.Any(p => raw.Equals(p, StringComparison.OrdinalIgnoreCase) || raw.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ExternalUserBoundaryAllowList = Infrastructure.Auth.ExternalUserBoundary.AllowedPrefixes;

    private static string Fill(RoutePattern p, Guid recordId)
    {
        var parts = new List<string>();
        foreach (var seg in p.PathSegments)
            parts.Add(string.Concat(seg.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart l => l.Content,
                RoutePatternSeparatorPart s => s.Content,
                RoutePatternParameterPart prm => prm.ParameterPolicies.Any(pp => pp.Content == "guid") ? recordId.ToString()
                    : prm.ParameterPolicies.Any(pp => pp.Content is "int" or "long") ? "1"
                    : prm.Name == "key" ? "suppliers" : prm.Name == "format" ? "csv" : "x",
                _ => "",
            })));
        return "/" + string.Join('/', parts);
    }

    [Fact] // SEC-804: the portal pages are for external users only
    public async Task Internal_and_anonymous_users_cannot_use_the_portal()
    {
        var anon = Client();
        var r1 = await anon.GetAsync("/portal");
        Assert.Equal(HttpStatusCode.Redirect, r1.StatusCode);
        Assert.StartsWith("/login", r1.Headers.Location!.OriginalString);

        var cwu = await SignedInAsync("cwu");
        var r2 = await cwu.GetAsync("/portal/requests");
        Assert.Equal(HttpStatusCode.Redirect, r2.StatusCode);
        Assert.Equal("/forbidden", r2.Headers.Location!.OriginalString);
    }

    // ------------------------------------------------------------------ INV-088: only their own company's records

    [Fact] // INV-088
    public async Task External_users_read_only_records_shared_with_their_own_supplier()
    {
        var a = await SupplierAsync();
        var b = await SupplierAsync();
        var (aUserId, aUser) = await PortalUserAsync(a.Id);
        await PortalUserAsync(b.Id);
        var (scarA, shareA) = await SharedScarAsync(a.Id, U());
        var (scarB, _) = await SharedScarAsync(b.Id, U());
        Record unshared;
        using (var cwu = fx.As("cwu"))
            unshared = await cwu.CreateAsync("scar", new()
            {
                ["subject"] = "Not shared " + U(), ["owner"] = cwu.UserId("cwu"), ["description"] = "x", ["supplier_name"] = "A", ["part_number"] = "1",
            });

        using (var ext = fx.As(aUser))
        {
            var access = ext.ServiceProvider.GetRequiredService<AccessService>();
            var me = ext.ServiceProvider.GetRequiredService<CurrentUser>();
            Assert.True(me.IsExternal);
            Assert.Equal(a.Id, me.ExternalOrgId);
            var readable = await access.Readable().Select(r => r.Id).ToListAsync();
            Assert.Contains(scarA.Id, readable);
            Assert.DoesNotContain(scarB.Id, readable);   // another supplier's record
            Assert.DoesNotContain(unshared.Id, readable); // never shared
            Assert.DoesNotContain(a.Id, readable);        // not even their own supplier profile
            Assert.All(readable, id => Assert.True(ext.Db().Set<ExternalShare>().Any(s => s.RecordId == id && s.OrgId == a.Id)));

            var cfg = ext.ServiceProvider.GetRequiredService<Engine.ConfigCache>().Current;
            Assert.All(cfg.Apps, app => Assert.Equal(AppRole.None, access.RoleFor(app.Id)));
            Assert.All(cfg.Apps, app => Assert.False(access.CanOpenApp(app.Id)));
            Assert.False(access.IsAnyDesigner);
            var editable = await access.CanEditAsync(await ext.Db().Records.AsNoTracking().FirstAsync(r => r.Id == scarA.Id));
            Assert.False(editable.Allowed);
            Assert.Null(await access.ActingAssignmentAsync(scarA.Id));

            // The supplier portal service itself never returns another supplier's request.
            var portal = ext.ServiceProvider.GetRequiredService<PortalService>();
            Assert.Contains(await portal.ListAsync(), i => i.Share.Id == shareA);
            var otherShare = await ext.Db().Set<ExternalShare>().AsNoTracking().FirstAsync(s => s.RecordId == scarB.Id);
            Assert.Null(await portal.ViewAsync(otherShare.Id));
        }

        // Withdrawing the share takes the record away at once.
        using (var cwu = fx.As("cwu"))
            await cwu.ServiceProvider.GetRequiredService<PortalService>().SetStatusAsync(scarA.Id, ExternalShareStatus.Withdrawn, reason: "test");
        using (var ext = fx.As(aUser))
            Assert.False(await ext.ServiceProvider.GetRequiredService<AccessService>().CanReadAsync(scarA.Id));

        // An administrator flag on an external profile grants nothing, and externals are never offered as people to pick.
        using (var admin = fx.As("admin"))
        {
            await admin.Db().Users.Where(u => u.Id == aUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdministrator, true));
            admin.ServiceProvider.GetRequiredService<UserDirectory>().Invalidate();
        }
        using (var ext = fx.As(aUser))
        {
            Assert.False(ext.ServiceProvider.GetRequiredService<CurrentUser>().IsAdmin);
            Assert.DoesNotContain(scarB.Id, await ext.ServiceProvider.GetRequiredService<AccessService>().Readable().Select(r => r.Id).ToListAsync());
            var dir = ext.ServiceProvider.GetRequiredService<UserDirectory>();
            Assert.DoesNotContain(dir.All, u => u.Id == aUserId);
            Assert.DoesNotContain(dir.Assignable, u => u.Id == aUserId);
            Assert.Contains(dir.Externals(a.Id), u => u.Id == aUserId);
        }
        using (var admin = fx.As("admin"))
        {
            await admin.Db().Users.Where(u => u.Id == aUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsAdministrator, false));
            admin.ServiceProvider.GetRequiredService<UserDirectory>().Invalidate();
        }
    }

    [Fact] // INV-088: a record is only ever shared with the supplier it belongs to
    public async Task A_request_cannot_be_shared_with_another_supplier()
    {
        var a = await SupplierAsync();
        var b = await SupplierAsync();
        await PortalUserAsync(a.Id);
        await PortalUserAsync(b.Id);
        using var cwu = fx.As("cwu");
        var req = await cwu.CreateAsync(PortalKinds.DocForm, new()
        {
            ["supplier"] = a.Id, ["doc_type"] = "sds", ["subject"] = "SDS", ["owner"] = cwu.UserId("cwu"), ["due_date"] = BusinessCalendar.Today.AddDays(5),
        });
        var snap = await cwu.LoadAsync(req.Id);
        var res = await cwu.ServiceProvider.GetRequiredService<PortalService>().ShareAsync(snap, b.Id, PortalKinds.DocumentKind, null, null);
        Assert.False(res.Success);
        Assert.False(await cwu.Db().Set<ExternalShare>().AnyAsync(s => s.RecordId == req.Id));
    }

    // ------------------------------------------------------------------ only the parts meant for them

    [Fact] // CAPA-011: the SCAR up to the supplier response section, never verification, comments or the audit trail
    public async Task The_portal_shows_only_the_fields_meant_for_the_supplier()
    {
        var supplier = await SupplierAsync();
        var (_, username) = await PortalUserAsync(supplier.Id);
        var secret = U();
        var (scar, shareId) = await SharedScarAsync(supplier.Id, secret);
        var other = await SupplierAsync();
        var (_, otherUser) = await PortalUserAsync(other.Id);

        var client = await SignedInAsync(username);
        var html = await client.GetStringAsync($"/portal/requests/{shareId}");
        Assert.Contains("Burrs found in 12 of 50 manifolds.", html);
        Assert.Contains("name=\"f.sr_root_cause\"", html);
        Assert.DoesNotContain("VERIFY-" + secret, html);
        Assert.DoesNotContain("INTERNAL-COMMENT-" + secret, html);
        Assert.DoesNotContain("f.verification_method", html);
        Assert.DoesNotContain("Jordan Lee", html); // the internal verifier
        Assert.DoesNotContain($"/records/{scar.Id}", html);

        var stranger = await SignedInAsync(otherUser);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/portal/requests/{shareId}")).StatusCode);
    }

    [Fact] // Files: only the attachment fields meant for the supplier can be downloaded
    public async Task Portal_file_downloads_are_limited_to_supplier_facing_fields()
    {
        var supplier = await SupplierAsync();
        var (_, username) = await PortalUserAsync(supplier.Id);
        Guid id;
        long visible, hidden;
        using (var cwu = fx.As("cwu"))
        {
            var rs = cwu.ServiceProvider.GetRequiredService<RecordService>();
            var res = await rs.CreateAsync(new CreateRequest
            {
                FormKey = PortalKinds.DocForm,
                Values = new() { ["supplier"] = supplier.Id, ["doc_type"] = "sds", ["subject"] = "SDS " + U(), ["owner"] = cwu.UserId("cwu"), ["due_date"] = BusinessCalendar.Today.AddDays(5) },
                Files =
                [
                    ("request_files", "template.txt", "text/plain", new MemoryStream("template"u8.ToArray())),
                    ("review_files", "internal-review.txt", "text/plain", new MemoryStream("internal"u8.ToArray())),
                ],
            });
            Assert.True(res.Ok);
            id = res.Record!.Id;
            Assert.True((await cwu.RouteAsync(id, "with_supplier")).Ok);
            visible = await cwu.Db().Attachments.Where(a => a.RecordId == id && a.FieldKey == "request_files").Select(a => a.Id).FirstAsync();
            hidden = await cwu.Db().Attachments.Where(a => a.RecordId == id && a.FieldKey == "review_files").Select(a => a.Id).FirstAsync();
        }
        int shareId;
        using (var s = fx.As("cwu")) shareId = await s.Db().Set<ExternalShare>().Where(x => x.RecordId == id).Select(x => x.Id).FirstAsync();

        var client = await SignedInAsync(username);
        var page = await client.GetStringAsync($"/portal/requests/{shareId}");
        Assert.Contains("template.txt", page);
        Assert.DoesNotContain("internal-review.txt", page);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/portal/requests/{shareId}?handler=File&fileId={visible}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/portal/requests/{shareId}?handler=File&fileId={hidden}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/files/{visible}")).StatusCode); // the internal download endpoint
    }

    // ------------------------------------------------------------------ CAPA-011: the supplier response

    [Fact] // CAPA-011: the portal response fills the supplier response section directly; internal fields can't be posted
    public async Task Supplier_response_fills_the_scar_and_notifies_the_owner()
    {
        var supplier = await SupplierAsync();
        var (userId, username) = await PortalUserAsync(supplier.Id);
        var (scar, shareId) = await SharedScarAsync(supplier.Id, U());
        var client = await SignedInAsync(username);
        var page = await client.GetStringAsync($"/portal/requests/{shareId}");
        var version = Regex.Match(page, "name=\"version\" value=\"(\\d+)\"").Groups[1].Value;

        HttpContent Form(string submit) => new MultipartFormDataContent
        {
            { new StringContent(AntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent(version), "version" },
            { new StringContent(submit), "submit" },
            { new StringContent("Worn deburring tool on cell 4."), "f.sr_root_cause" },
            { new StringContent("Tool-life counter at 800 parts."), "f.sr_corrective_action" },
            { new StringContent(BusinessCalendar.Today.AddDays(-1).ToString("yyyy-MM-dd")), "f.sr_implementation_date" },
            // Not meant for the supplier: must be ignored.
            { new StringContent("Supplier wrote this"), "f.verification_method" },
            { new StringContent("Changed by supplier"), "f.description" },
            { new ByteArrayContent("8D report"u8.ToArray()), "file.sr_file", "8d.txt" },
            { new ByteArrayContent("sneaky"u8.ToArray()), "file.verification_evidence", "sneaky.txt" },
        };
        var res = await client.PostAsync($"/portal/requests/{shareId}?handler=Respond", Form("1"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        using var cwu = fx.As("cwu");
        var snap = await cwu.LoadAsync(scar.Id);
        Assert.Equal("Worn deburring tool on cell 4.", snap.Text("sr_root_cause"));
        Assert.Equal(BusinessCalendar.Today, snap.Date("sr_received_on"));
        Assert.StartsWith("VERIFY-", snap.Text("verification_method"));
        Assert.Equal("Burrs found in 12 of 50 manifolds.", snap.Text("description"));
        Assert.Single(snap.AttachmentsOf("sr_file"));
        Assert.Equal(1, snap.Attachments.Count(a => !a.Removed));
        Assert.Equal("assign_to_supplier", snap.PhaseKey); // the SQE routes the SCAR on

        var share = await cwu.Db().Set<ExternalShare>().AsNoTracking().FirstAsync(s => s.Id == shareId);
        Assert.Equal(ExternalShareStatus.Submitted, share.Status);
        Assert.Equal(userId, share.SubmittedById);

        var entries = await cwu.Db().Audit.AsNoTracking().Where(a => a.RecordId == scar.Id && a.UserId == userId).ToListAsync();
        Assert.Contains(entries, a => a.FieldKey == "sr_root_cause");
        Assert.Contains(entries, a => a.Action == "Supplier response submitted");
        Assert.All(entries, a => Assert.Equal("Supplier portal", a.Source));
        Assert.True(await cwu.Db().Notifications.AnyAsync(n => n.UserId == cwu.UserId("cwu") && n.RecordId == scar.Id && n.Title.Contains("responded")));

        // Submitted responses are locked until the SQE returns them.
        var again = await client.PostAsync($"/portal/requests/{shareId}?handler=Respond", Form("1"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("being reviewed", await again.Content.ReadAsStringAsync());

        var returned = await SupplierTests.ActAsync(cwu, scar.Id, "portal-return", comment: "Add the escape root cause.");
        Assert.True(returned.Success);
        using var ext = fx.As(username);
        var view = await ext.ServiceProvider.GetRequiredService<PortalService>().ViewAsync(shareId);
        Assert.True(view!.CanRespond);
        Assert.Equal("Add the escape root cause.", view.Share.Feedback);
    }

    [Fact] // CAPA-011: required supplier-response fields are enforced on submit, not on draft
    public async Task Submit_needs_the_required_response_fields()
    {
        var supplier = await SupplierAsync();
        var (_, username) = await PortalUserAsync(supplier.Id);
        var (_, shareId) = await SharedScarAsync(supplier.Id, U());
        using var ext = fx.As(username);
        var portal = ext.ServiceProvider.GetRequiredService<PortalService>();
        var view = await portal.ViewAsync(shareId);
        var form = new FormCollection(new Dictionary<string, StringValues> { ["f.sr_root_cause"] = "Draft only" });
        var draft = await portal.RespondAsync(shareId, view!.Version, form, submit: false);
        Assert.True(draft.Ok, string.Join("; ", draft.Errors));
        view = await portal.ViewAsync(shareId);
        var submit = await portal.RespondAsync(shareId, view!.Version, new FormCollection([]), submit: true);
        Assert.False(submit.Ok);
        Assert.Contains(submit.Errors, e => e.Contains("Corrective action"));
    }

    // ------------------------------------------------------------------ invitations (SEC-804, open question 4)

    [Fact] // Invitation: creates the external account, emails a one-time expiring link, password policy applies
    public async Task Invitation_link_is_one_time_expiring_and_enforces_the_password_policy()
    {
        var supplier = await SupplierAsync();
        string email;
        using (var cwu = fx.As("cwu"))
        {
            var snap = await cwu.LoadAsync(supplier.Id);
            var row = snap.Rows("contacts").Single();
            email = row.Values.Text("email")!;
            var res = await SupplierTests.ActAsync(cwu, supplier.Id, "portal-invite", new() { ["contact"] = row.Id.ToString() });
            Assert.True(res.Success, string.Join("; ", res.Errors ?? []));
            var user = await cwu.Db().Users.AsNoTracking().FirstAsync(u => u.Email == email);
            Assert.True(user.IsExternal);
            Assert.Equal(supplier.Id, user.ExternalOrgId);
            Assert.Equal(LicenseType.External, user.License);
            Assert.Null(user.PasswordHash);
            Assert.False(user.IsAdministrator);
            // The email log (admin-visible) is redacted; only the outbox message that delivers the email carries the link.
            var logged = await cwu.Db().EmailLog.AsNoTracking().Where(m => m.To == email).Select(m => m.Body).FirstAsync();
            Assert.DoesNotContain(LatestLink(cwu.Db(), email), logged!);
            Assert.True(await cwu.Db().Set<AccountToken>().AnyAsync(t => t.UserId == user.Id && t.ExpiresAt > DateTime.UtcNow && t.ExpiresAt < DateTime.UtcNow.AddDays(8)));
        }
        string link;
        using (var s = fx.As(null)) link = LatestLink(s.Db(), email);

        var anon = Client();
        var page = await anon.GetStringAsync($"/portal/welcome?token={link}");
        Assert.Contains("Activate my account", page);
        async Task<string> Post(string pw) => await (await anon.PostAsync($"/portal/welcome?token={link}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = pw, ["confirm"] = pw, ["__RequestVerificationToken"] = AntiforgeryToken(page),
        }))).Content.ReadAsStringAsync();

        Assert.Contains("Use at least", await Post("short1!"));           // SEC-201 policy
        Assert.Contains("Your password is set", await Post(Pw));
        Assert.Contains("doesn't work any more", await Post("Another!Pass2026")); // single use
        Assert.Contains("doesn't work any more", await anon.GetStringAsync($"/portal/welcome?token={link}"));

        // Signing in uses the normal stack and lands in the portal.
        var client = await SignedInAsync(email);
        var home = await client.GetAsync("/");
        Assert.Equal("/portal", home.Headers.Location!.OriginalString);

        // An expired link never works.
        using (var cwu = fx.As("cwu"))
        {
            var uid = await cwu.Db().Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
            Assert.True((await SupplierTests.ActAsync(cwu, supplier.Id, "portal-resend", new() { ["user"] = uid.ToString() })).Success);
            await cwu.Db().Set<AccountToken>().Where(t => t.UserId == uid && t.UsedAt == null && t.RevokedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            var expired = Uri.UnescapeDataString(LatestLink(cwu.Db(), email));
            cwu.Db().ChangeTracker.Clear();
            Assert.False((await cwu.ServiceProvider.GetRequiredService<PortalAccounts>().CheckAsync(expired)).Valid);
        }
    }

    [Fact] // SEC-405: disabling portal access ends the contact's sessions at once
    public async Task Disabling_portal_access_signs_the_contact_out()
    {
        var supplier = await SupplierAsync();
        var (id, username) = await PortalUserAsync(supplier.Id);
        var client = await SignedInAsync(username);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/portal")).StatusCode);
        using (var cwu = fx.As("cwu"))
            Assert.True((await SupplierTests.ActAsync(cwu, supplier.Id, "portal-disable", new() { ["user"] = id.ToString() }, comment: "Left the company")).Success);
        var after = await client.GetAsync("/portal");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/login", after.Headers.Location!.OriginalString);
    }

    // ------------------------------------------------------------------ document requests, audit findings, scorecards

    [Fact] // SUP-004 / SUP-003: a requested certificate is uploaded in the portal, reviewed, and filed on the supplier profile
    public async Task Document_request_round_trip_files_the_certificate()
    {
        var supplier = await SupplierAsync();
        var (_, username) = await PortalUserAsync(supplier.Id);
        Guid id;
        using (var cwu = fx.As("cwu"))
        {
            var res = await SupplierTests.ActAsync(cwu, supplier.Id, "portal-request-doc", new()
            {
                ["doc_type"] = "iso_cert", ["subject"] = "Renewed ISO 13485 certificate", ["standard"] = "iso_13485",
                ["due"] = BusinessCalendar.Today.AddDays(7).ToString("yyyy-MM-dd"),
            });
            Assert.True(res.Success, string.Join("; ", res.Errors ?? []));
            id = res.OpenRecordId!.Value;
            Assert.Equal("with_supplier", (await cwu.LoadAsync(id)).PhaseKey);
        }
        using (var ext = fx.As(username))
        {
            var portal = ext.ServiceProvider.GetRequiredService<PortalService>();
            var item = (await portal.ListAsync()).Single(i => i.Share.RecordId == id);
            var view = await portal.ViewAsync(item.Share.Id);
            var noFile = await portal.RespondAsync(item.Share.Id, view!.Version, new FormCollection(new Dictionary<string, StringValues>
            {
                ["f.expires_on"] = BusinessCalendar.Today.AddYears(3).ToString("yyyy-MM-dd"),
            }), submit: true);
            Assert.False(noFile.Ok); // the document itself is required
            var files = new FormFileCollection { new FormFile(new MemoryStream("%PDF-1.4"u8.ToArray()), 0, 8, "file.doc_file", "cert.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" } };
            var ok = await portal.RespondAsync(item.Share.Id, view.Version, new FormCollection(new Dictionary<string, StringValues>
            {
                ["f.doc_number"] = "MD-77", ["f.issuer"] = "BSI", ["f.expires_on"] = BusinessCalendar.Today.AddYears(3).ToString("yyyy-MM-dd"),
            }, files), submit: true);
            Assert.True(ok.Ok, string.Join("; ", ok.Errors));
        }
        using (var cwu = fx.As("cwu"))
        {
            var snap = await cwu.LoadAsync(id);
            Assert.Equal("review", snap.PhaseKey); // the submit routed it to the internal review
            Assert.Equal(cwu.UserId("cwu"), await cwu.Db().Assignments.Where(a => a.RecordId == id && a.Status == AssignmentStatus.Active).Select(a => a.UserId).FirstAsync());
            Assert.True((await cwu.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, new() { ["review_result"] = "accepted" })).Ok);
            Assert.True((await cwu.RouteAsync(id, "accepted")).Ok);
            var sup = await cwu.LoadAsync(supplier.Id);
            Assert.Contains(sup.Rows("certifications"), r => r.Values.Text("cert_number") == "MD-77");
            Assert.Single(sup.AttachmentsOf("cert_files"));
            Assert.Equal(ExternalShareStatus.Accepted, (await cwu.Db().Set<ExternalShare>().FirstAsync(s => s.RecordId == id)).Status);
        }
    }

    [Fact] // AUD-031: supplier audit findings answered through the portal
    public async Task Audit_findings_are_answered_in_the_portal()
    {
        var supplier = await SupplierAsync();
        var (_, username) = await PortalUserAsync(supplier.Id);
        Record audit;
        using (var tb = fx.As("tbrooks"))
        {
            audit = await tb.CreateAsync("audit", new()
            {
                ["subject"] = "Supplier audit " + U(), ["audit_kind"] = "supplier", ["auditee"] = "Supplier", ["lead_auditor"] = tb.UserId("tbrooks"),
                ["start_at"] = DateTime.UtcNow.AddDays(-3),
            }, rows: new()
            {
                ["checklist"] =
                [
                    new() { ["question"] = "Lot release controlled?", ["classification"] = "minor_nc", ["comments"] = "Released without CoA" },
                    new() { ["question"] = "Records legible?", ["classification"] = "conforming" },
                    new() { ["question"] = "Shipping segregated?", ["classification"] = "observation" },
                ],
            });
            Assert.True((await tb.RouteAsync(audit.Id, "scheduled")).Ok);
            var sent = await SupplierTests.ActAsync(tb, audit.Id, "portal-findings", new()
            {
                ["supplier"] = supplier.Id.ToString(), ["due"] = BusinessCalendar.Today.AddDays(20).ToString("yyyy-MM-dd"),
            });
            Assert.True(sent.Success, string.Join("; ", sent.Errors ?? []));
        }
        using var ext = fx.As(username);
        var portal = ext.ServiceProvider.GetRequiredService<PortalService>();
        var item = (await portal.ListAsync()).Single(i => i.Kind.Key == PortalKinds.AuditResponse);
        var view = await portal.ViewAsync(item.Share.Id);
        Assert.Equal(2, view!.Rows.Count); // findings only, not the conforming question
        var nc = view.Rows.Single(r => r.KeywordKey("classification") == "minor_nc");
        var missing = await portal.RespondAsync(item.Share.Id, view.Version, new FormCollection([]), submit: true);
        Assert.False(missing.Ok);
        var ok = await portal.RespondAsync(item.Share.Id, view.Version, new FormCollection(new Dictionary<string, StringValues>
        {
            [$"r.{nc.Id:N}.sr_root_cause"] = "LIMS outage procedure allowed shipping.",
            [$"r.{nc.Id:N}.sr_corrective_action"] = "Removed the pending-CoA release option.",
            [$"r.{nc.Id:N}.sr_target_date"] = BusinessCalendar.Today.AddDays(14).ToString("yyyy-MM-dd"),
            [$"r.{nc.Id:N}.accepted"] = "yes", // internal field: ignored
        }), submit: true);
        Assert.True(ok.Ok, string.Join("; ", ok.Errors));
        var snap = await ext.LoadAsync(item.Share.RecordId);
        Assert.Equal("review", snap.PhaseKey);
        var row = snap.Rows("findings").Single(r => r.Id == nc.Id);
        Assert.Equal("Removed the pending-CoA release option.", row.Values.Text("sr_corrective_action"));
        Assert.Null(row.Values.Bool("accepted"));
    }

    [Fact] // SUP-036: only released (published) scorecards are visible, read-only
    public async Task Only_published_scorecards_are_visible_in_the_portal()
    {
        var supplier = await SupplierAsync();
        var (_, username) = await PortalUserAsync(supplier.Id);
        Record card;
        using (var cwu = fx.As("cwu"))
        {
            card = await cwu.CreateAsync("scorecard", new()
            {
                ["supplier"] = supplier.Id, ["period_start"] = BusinessCalendar.Today.AddMonths(-3), ["period_end"] = BusinessCalendar.Today.AddDays(-1),
                ["ppm"] = 100m, ["otd_pct"] = 98m, ["responsiveness"] = 4m,
            });
            Assert.Empty(await SupplierTestsActions(cwu, card.Id)); // not offered while in draft
            // Even if a draft were shared, the portal would not show it.
            var snap = await cwu.LoadAsync(card.Id);
            Assert.True((await cwu.ServiceProvider.GetRequiredService<PortalService>().ShareAsync(snap, supplier.Id, PortalKinds.ScorecardKind, null, null)).Success);
        }
        using (var ext = fx.As(username))
        {
            var portal = ext.ServiceProvider.GetRequiredService<PortalService>();
            Assert.DoesNotContain(await portal.ListAsync(), i => i.Share.RecordId == card.Id);
        }
        using (var cwu = fx.As("cwu")) Assert.True((await cwu.RouteAsync(card.Id, "published")).Ok);
        using (var ext = fx.As(username))
        {
            var portal = ext.ServiceProvider.GetRequiredService<PortalService>();
            var item = Assert.Single(await portal.ListAsync(), i => i.Share.RecordId == card.Id);
            var view = await portal.ViewAsync(item.Share.Id);
            Assert.False(view!.CanRespond);
            Assert.NotNull(view.Value("overall_score"));
            Assert.Empty(view.Editable);
        }
    }

    private static async Task<List<string>> SupplierTestsActions(IServiceScope s, Guid id)
    {
        var snap = await s.LoadAsync(id);
        var ctx = new Engine.Behaviors.RecordContext(snap, s.ServiceProvider);
        var list = new List<string>();
        foreach (var b in s.ServiceProvider.GetServices<Engine.Behaviors.RecordBehavior>().Where(b => b.Forms.Contains(snap.Form.Key)))
            list.AddRange((await b.ActionsAsync(ctx)).Where(a => a.Key.StartsWith("portal-")).Select(a => a.Key));
        return list;
    }
}
