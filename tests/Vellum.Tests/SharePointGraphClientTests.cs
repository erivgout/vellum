using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Integrations.SharePoint;

namespace Vellum.Tests;

/// <summary>
/// Microsoft Graph client request shapes (no tenant needed): a fake <see cref="HttpMessageHandler"/> records every request and
/// answers like Graph and Microsoft Entra ID would.
/// </summary>
public class SharePointGraphClientTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string TokenUrl = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token";

    public sealed record Call(HttpMethod Method, string Url, string? Auth, string? Body, string? ContentRange, string? ContentType);

    private sealed class FakeGraph : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];
        public Func<HttpRequestMessage, string?, HttpResponseMessage?>? Override { get; set; }
        public int TokensIssued { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content == null ? null : await req.Content.ReadAsStringAsync(ct);
            var url = req.RequestUri!.AbsoluteUri;
            Calls.Add(new Call(req.Method, url, req.Headers.Authorization?.ToString(), body, req.Content?.Headers.ContentRange?.ToString(), req.Content?.Headers.ContentType?.MediaType));
            if (Override?.Invoke(req, body) is { } custom) return custom;
            if (url == TokenUrl) { TokensIssued++; return Json(new { token_type = "Bearer", expires_in = 3599, access_token = $"tok-{TokensIssued}" }); }
            if (url == $"{Graph}/sites/contoso.sharepoint.com:/sites/QMS?$select=id,webUrl") return Json(new { id = "site-1", webUrl = "https://contoso.sharepoint.com/sites/QMS" });
            if (url == $"{Graph}/sites/contoso.sharepoint.com?$select=id,webUrl") return Json(new { id = "root-site", webUrl = "https://contoso.sharepoint.com" });
            if (url.StartsWith($"{Graph}/sites/") && url.EndsWith("/drives?$select=id,name,webUrl"))
                return Json(new
                {
                    value = new object[]
                    {
                        new { id = "drive-shared", name = "Documents", webUrl = "https://contoso.sharepoint.com/sites/QMS/Shared%20Documents" },
                        new { id = "drive-1", name = "Controlled Documents", webUrl = "https://contoso.sharepoint.com/sites/QMS/Controlled%20Documents" },
                        new { id = "drive-root", name = "Documents", webUrl = "https://contoso.sharepoint.com/Shared%20Documents" },
                    },
                });
            if (url == $"{Graph}/drives/drive-1/list/columns")
                return Json(new
                {
                    value = new object[]
                    {
                        new { name = "Title", displayName = "Title", readOnly = false, text = new { } },
                        new { name = "DocumentNumber", displayName = "Document number", readOnly = false, text = new { } },
                        new { name = "EffectiveDate", displayName = "Effective date", readOnly = false, dateTime = new { } },
                        new { name = "Notes", displayName = "Notes", readOnly = false, text = new { allowMultipleLines = true } },
                        new { name = "Created", displayName = "Created", readOnly = true, dateTime = new { } },
                    },
                });
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"code":"itemNotFound","message":"Not found"}}""") };
        }
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static readonly SharePointProfile Profile = new() { Id = 42, Name = "Contoso", Host = "contoso.sharepoint.com", Connection = SpConnection.Graph };
    private static readonly SharePointLibrary Lib = new() { Id = 7, Key = "docs", Name = "Controlled Documents", RelativeUrl = "/sites/QMS/Controlled Documents" };

    private static (GraphSharePointClient Client, FakeGraph Fake) Make(SpCredential? cred = null, GraphOptions? options = null)
    {
        var fake = new FakeGraph();
        var client = new GraphSharePointClient(Profile, cred ?? new SpCredential(Tenant, ClientId, "s3cret", null, null), new HttpClient(fake), options, new GraphCache());
        return (client, fake);
    }

    private static List<Call> GraphCalls(FakeGraph f) => f.Calls.Where(c => c.Url != TokenUrl).ToList();

    [Fact] // client-credentials token acquisition with caching; site and drive resolution
    public async Task Token_uses_client_credentials_and_is_cached_and_the_library_resolves_from_its_url()
    {
        var (client, fake) = Make();
        var columns = await client.GetColumnsAsync(Lib);
        await client.GetColumnsAsync(Lib);

        var token = Assert.Single(fake.Calls, c => c.Url == TokenUrl);
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Null(token.Auth);
        var form = token.Body!.Split('&').Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Equal("https://graph.microsoft.com/.default", form["scope"]);
        Assert.Equal("s3cret", form["client_secret"]);

        var calls = GraphCalls(fake);
        Assert.Equal($"{Graph}/sites/contoso.sharepoint.com:/sites/QMS?$select=id,webUrl", calls[0].Url);
        Assert.Equal($"{Graph}/sites/site-1/drives?$select=id,name,webUrl", calls[1].Url);
        Assert.Equal($"{Graph}/drives/drive-1/list/columns", calls[2].Url);
        Assert.Equal(4, calls.Count); // site and drive are resolved once, columns read twice
        Assert.All(calls, c => Assert.Equal("Bearer tok-1", c.Auth));

        Assert.NotNull(columns);
        Assert.Equal("dateTime", columns!.First(c => c.Name == "EffectiveDate").Type);
        Assert.Equal("note", columns.First(c => c.Name == "Notes").Type);
        Assert.True(columns.First(c => c.Name == "Created").ReadOnly);
    }

    [Fact]
    public async Task A_library_of_the_root_site_resolves_the_root_site()
    {
        var (client, fake) = Make();
        var (siteId, driveId, _) = await client.ResolveAsync(new SharePointLibrary { Id = 8, Key = "shared", Name = "Documents", RelativeUrl = "/Shared Documents" });
        Assert.Equal("root-site", siteId);
        Assert.Equal("drive-root", driveId);
        Assert.Equal($"{Graph}/sites/contoso.sharepoint.com?$select=id,webUrl", GraphCalls(fake)[0].Url);
    }

    [Fact] // DOC-084: a missing folder is created on first publish
    public async Task Missing_folders_are_created_from_the_nearest_existing_parent()
    {
        var (client, fake) = Make();
        fake.Override = (req, body) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == $"{Graph}/drives/drive-1/root?$select=id") return Json(new { id = "root-id" });
            if (req.Method == HttpMethod.Post && url == $"{Graph}/drives/drive-1/items/root-id/children") return Json(new { id = "f-proc" }, HttpStatusCode.Created);
            if (req.Method == HttpMethod.Post && url == $"{Graph}/drives/drive-1/items/f-proc/children") return Json(new { id = "f-2026" }, HttpStatusCode.Created);
            return null;
        };
        await client.EnsureFolderAsync(Lib, "Standard Procedures/2026");

        var calls = GraphCalls(fake).Skip(2).ToList(); // after site and drive resolution
        Assert.Equal((HttpMethod.Get, $"{Graph}/drives/drive-1/root:/Standard%20Procedures/2026?$select=id,folder"), (calls[0].Method, calls[0].Url));
        Assert.Equal($"{Graph}/drives/drive-1/root:/Standard%20Procedures?$select=id,folder", calls[1].Url);
        Assert.Equal($"{Graph}/drives/drive-1/root?$select=id", calls[2].Url);
        Assert.Equal(HttpMethod.Post, calls[3].Method);
        var first = JsonNode.Parse(calls[3].Body!)!;
        Assert.Equal("Standard Procedures", first["name"]!.GetValue<string>());
        Assert.NotNull(first["folder"]);
        Assert.Equal("fail", first["@microsoft.graph.conflictBehavior"]!.GetValue<string>());
        Assert.Equal($"{Graph}/drives/drive-1/items/f-proc/children", calls[4].Url);
        Assert.Equal("2026", JsonNode.Parse(calls[4].Body!)!["name"]!.GetValue<string>());
        Assert.Equal(5, calls.Count);
    }

    [Fact] // simple upload for small files; list item fields
    public async Task Small_files_use_a_single_put_and_columns_are_set_with_a_patch()
    {
        var (client, fake) = Make();
        fake.Override = (req, body) => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith(":/content")
            ? Json(new { id = "item-1", webUrl = "https://contoso.sharepoint.com/sites/QMS/Controlled%20Documents/Procedure/SOP-1%20-%20Gowning.pdf" }, HttpStatusCode.Created)
            : req.Method == HttpMethod.Patch ? Json(new { DocumentNumber = "SOP-1" }) : null;
        var item = await client.UploadAsync(Lib, "Procedure", "SOP-1 - Gowning.pdf", "%PDF-1.4"u8.ToArray(), "application/pdf");
        await client.SetFieldsAsync(Lib, item, new Dictionary<string, object?> { ["DocumentNumber"] = "SOP-1", ["Revision"] = "B", ["EffectiveDate"] = "2026-10-25T00:00:00Z", ["Copies"] = 3m });

        Assert.Equal("item-1", item.Id);
        Assert.StartsWith("https://contoso.sharepoint.com/", item.WebUrl);
        var put = Assert.Single(fake.Calls, c => c.Method == HttpMethod.Put);
        Assert.Equal($"{Graph}/drives/drive-1/root:/Procedure/SOP-1%20-%20Gowning.pdf:/content?@microsoft.graph.conflictBehavior=replace", put.Url);
        Assert.Equal("Bearer tok-1", put.Auth);
        Assert.Equal("application/pdf", put.ContentType);
        Assert.Equal("%PDF-1.4", put.Body);
        var patch = Assert.Single(fake.Calls, c => c.Method == HttpMethod.Patch);
        Assert.Equal($"{Graph}/drives/drive-1/items/item-1/listItem/fields", patch.Url);
        Assert.Equal("application/json", patch.ContentType);
        var fields = JsonNode.Parse(patch.Body!)!.AsObject();
        Assert.Equal("SOP-1", fields["DocumentNumber"]!.GetValue<string>());
        Assert.Equal("2026-10-25T00:00:00Z", fields["EffectiveDate"]!.GetValue<string>());
        Assert.Equal(3m, fields["Copies"]!.GetValue<decimal>());
    }

    [Fact] // upload sessions for large files: byte ranges to the pre-authenticated URL, no bearer token
    public async Task Large_files_use_an_upload_session()
    {
        var (client, fake) = Make(options: new GraphOptions { SimpleUploadLimit = 8, ChunkSize = 5 });
        var puts = 0;
        fake.Override = (req, body) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri.EndsWith(":/createUploadSession"))
                return Json(new { uploadUrl = "https://contoso.sharepoint.com/sites/QMS/_api/v2.0/uploadSession?guid=abc", expirationDateTime = "2026-10-26T00:00:00Z" });
            if (req.Method == HttpMethod.Put && req.RequestUri!.Host == "contoso.sharepoint.com")
                return ++puts < 3 ? Json(new { nextExpectedRanges = new[] { $"{puts * 5}-" } }, HttpStatusCode.Accepted) : Json(new { id = "item-big" }, HttpStatusCode.Created);
            return null;
        };
        var item = await client.UploadAsync(Lib, "", "Big file.pdf", Encoding.ASCII.GetBytes("0123456789AB"), "application/pdf");

        Assert.Equal("item-big", item.Id);
        var session = Assert.Single(fake.Calls, c => c.Method == HttpMethod.Post && c.Url.EndsWith(":/createUploadSession"));
        Assert.Equal($"{Graph}/drives/drive-1/root:/Big%20file.pdf:/createUploadSession", session.Url);
        Assert.Equal("replace", JsonNode.Parse(session.Body!)!["item"]!["@microsoft.graph.conflictBehavior"]!.GetValue<string>());
        var chunks = fake.Calls.Where(c => c.Method == HttpMethod.Put).ToList();
        Assert.Equal(["bytes 0-4/12", "bytes 5-9/12", "bytes 10-11/12"], chunks.Select(c => c.ContentRange).ToList());
        Assert.Equal(["01234", "56789", "AB"], chunks.Select(c => c.Body).ToList());
        Assert.All(chunks, c => Assert.Null(c.Auth));
        Assert.DoesNotContain(fake.Calls, c => c.Url.EndsWith(":/content"));
    }

    [Fact] // INT-308: delete or move/rename for obsolete handling
    public async Task Delete_and_move_use_the_drive_item()
    {
        var (client, fake) = Make();
        fake.Override = (req, body) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(url.EndsWith("/item-1") ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
            if (url == $"{Graph}/drives/drive-1/root:/Obsolete?$select=id,folder") return Json(new { id = "f-obs", folder = new { childCount = 0 } });
            if (req.Method == HttpMethod.Patch) return Json(new { id = "item-1", webUrl = "https://contoso.sharepoint.com/x" });
            return null;
        };
        Assert.True(await client.DeleteAsync(Lib, "item-1"));
        Assert.False(await client.DeleteAsync(Lib, "gone"));
        var moved = await client.MoveAsync(Lib, "item-1", "Obsolete", "SOP-1 (rev A obsolete).pdf");

        Assert.Equal($"{Graph}/drives/drive-1/items/item-1", fake.Calls.First(c => c.Method == HttpMethod.Delete).Url);
        var patch = Assert.Single(fake.Calls, c => c.Method == HttpMethod.Patch);
        Assert.Equal($"{Graph}/drives/drive-1/items/item-1?@microsoft.graph.conflictBehavior=replace", patch.Url);
        var body = JsonNode.Parse(patch.Body!)!;
        Assert.Equal("f-obs", body["parentReference"]!["id"]!.GetValue<string>());
        Assert.Equal("SOP-1 (rev A obsolete).pdf", body["name"]!.GetValue<string>());
        Assert.Equal("Obsolete/SOP-1 (rev A obsolete).pdf", moved!.Path);
    }

    [Fact] // NFR-025: transient errors are retryable (honouring Retry-After), configuration errors aren't
    public async Task Graph_errors_map_to_codes_and_retryability()
    {
        async Task<SharePointException> Fail(Func<HttpRequestMessage, string?, HttpResponseMessage?> respond)
        {
            var (client, fake) = Make();
            fake.Override = respond;
            return await Assert.ThrowsAsync<SharePointException>(() => client.GetColumnsAsync(Lib));
        }
        static HttpResponseMessage Err(HttpStatusCode s, string code, int? retryAfter = null)
        {
            var r = new HttpResponseMessage(s) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = code + " happened" } })) };
            if (retryAfter is { } ra) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(ra));
            return r;
        }
        bool Columns(HttpRequestMessage r) => r.RequestUri!.AbsoluteUri.EndsWith("/list/columns");

        var throttled = await Fail((r, _) => Columns(r) ? Err(HttpStatusCode.TooManyRequests, "activityLimitReached", 120) : null);
        Assert.Equal(SpErrors.Throttled, throttled.Code);
        Assert.True(throttled.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(120), throttled.RetryAfter);

        var down = await Fail((r, _) => Columns(r) ? Err(HttpStatusCode.ServiceUnavailable, "serviceNotAvailable") : null);
        Assert.Equal(SpErrors.Throttled, down.Code);
        Assert.True(down.Retryable);
        var gateway = await Fail((r, _) => Columns(r) ? Err(HttpStatusCode.BadGateway, "badGateway") : null);
        Assert.Equal(SpErrors.Unavailable, gateway.Code);
        Assert.True(gateway.Retryable);

        var denied = await Fail((r, _) => Columns(r) ? Err(HttpStatusCode.Forbidden, "accessDenied") : null);
        Assert.Equal(SpErrors.Access, denied.Code);
        Assert.False(denied.Retryable);
        Assert.Contains("Sites.Selected", denied.Message);

        var noSite = await Fail((r, _) => r.RequestUri!.AbsoluteUri.Contains("/sites/contoso") ? Err(HttpStatusCode.NotFound, "itemNotFound") : null);
        Assert.Equal(SpErrors.NotFound, noSite.Code);
        Assert.False(noSite.Retryable);

        var badSecret = await Fail((r, _) => r.RequestUri!.AbsoluteUri == TokenUrl
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.\r\nTrace ID: x"}""") }
            : null);
        Assert.Equal(SpErrors.Auth, badSecret.Code);
        Assert.False(badSecret.Retryable);
        Assert.Contains("AADSTS7000215", badSecret.Message);
        Assert.DoesNotContain("Trace ID", badSecret.Message);

        var offline = await Fail((r, _) => throw new HttpRequestException("No such host is known."));
        Assert.Equal(SpErrors.Network, offline.Code);
        Assert.True(offline.Retryable);
    }

    [Fact] // a revoked or expired token is refreshed once
    public async Task A_401_refreshes_the_token_and_retries_once()
    {
        var (client, fake) = Make();
        var rejected = false;
        fake.Override = (req, _) =>
        {
            if (!rejected && req.RequestUri!.AbsoluteUri.Contains("/sites/contoso")) { rejected = true; return new HttpResponseMessage(HttpStatusCode.Unauthorized); }
            return null;
        };
        await client.ResolveAsync(Lib);
        Assert.Equal(2, fake.TokensIssued);
        var siteCalls = fake.Calls.Where(c => c.Url.Contains("/sites/contoso")).ToList();
        Assert.Equal(["Bearer tok-1", "Bearer tok-2"], siteCalls.Select(c => c.Auth).ToList());
    }

    [Fact] // certificate credential: RFC 7523 client assertion signed with the private key
    public async Task Certificate_credentials_send_a_signed_client_assertion()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Vellum SharePoint test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = cert.Export(X509ContentType.Pfx, "pfx-pass");
        Assert.Null(SharePointSecrets.LoadCertificate(pfx, "pfx-pass").Error);
        Assert.NotNull(SharePointSecrets.LoadCertificate(pfx, "wrong").Error);

        var (client, fake) = Make(new SpCredential(Tenant, ClientId, null, pfx, "pfx-pass"));
        Assert.Equal("tok-1", await client.TokenAsync());

        var form = fake.Calls.Single(c => c.Url == TokenUrl).Body!.Split('&').Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
        Assert.False(form.ContainsKey("client_secret"));
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
        var parts = form["client_assertion"].Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        var header = JsonNode.Parse(B64(parts[0]))!;
        var payload = JsonNode.Parse(B64(parts[1]))!;
        Assert.Equal("RS256", header["alg"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(cert.GetCertHash()).TrimEnd('=').Replace('+', '-').Replace('/', '_'), header["x5t"]!.GetValue<string>());
        Assert.Equal(TokenUrl, payload["aud"]!.GetValue<string>());
        Assert.Equal(ClientId, payload["iss"]!.GetValue<string>());
        Assert.Equal(ClientId, payload["sub"]!.GetValue<string>());
        Assert.True(payload["exp"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}

/// <summary>INT-304 file names and the NFR-025 backoff schedule.</summary>
public class SharePointNamingTests
{
    [Theory] // INT-304: characters SharePoint doesn't allow become underscores
    [InlineData("a:b*c?d.pdf", "a_b_c_d.pdf")]
    [InlineData("Report <final> | v2.docx", "Report _final_ _ v2.docx")]
    [InlineData("SOP \"quoted\" \\ path/name.pdf", "SOP _quoted_ _ path_name.pdf")]
    [InlineData("50% done #1.pdf", "50_ done _1.pdf")]
    [InlineData("tab\there.pdf", "tab_here.pdf")]
    [InlineData("  spaced    name.pdf  ", "spaced name.pdf")]
    [InlineData("trailing.", "trailing_")]
    [InlineData("..", "_")]
    [InlineData("~$lock.docx", "_$lock.docx")]
    [InlineData("CON.pdf", "_CON.pdf")]
    [InlineData("desktop.ini", "_desktop.ini")]
    [InlineData("x_vti_y.pdf", "x_vti-y.pdf")]
    [InlineData("", "_")]
    public void File_names_are_sanitised(string input, string expected) => Assert.Equal(expected, SharePointNaming.SanitizeName(input));

    [Fact]
    public void Long_names_are_shortened_keeping_the_extension()
    {
        var name = SharePointNaming.SanitizeName(new string('x', 300) + ".pdf");
        Assert.Equal(SharePointNaming.MaxNameLength, name.Length);
        Assert.EndsWith(".pdf", name);
        var fitted = SharePointNaming.FitPath(new string('f', 300), new string('y', 200) + ".pdf");
        Assert.True(300 + 1 + fitted.Length <= SharePointNaming.MaxPathLength);
        Assert.EndsWith(".pdf", fitted);
    }

    [Fact]
    public void Folders_and_library_urls_are_normalized()
    {
        Assert.Equal("_/Procedures/2026", SharePointNaming.NormalizeFolder("../Procedures//2026/"));
        Assert.Equal("Quality_ Records/A_B", SharePointNaming.NormalizeFolder("\\Quality: Records\\A|B"));
        Assert.Equal(("/sites/QMS", "Controlled Documents"), SharePointNaming.SplitLibraryUrl("sites/QMS/Controlled Documents/"));
        Assert.Equal(("", "Shared Documents"), SharePointNaming.SplitLibraryUrl("/Shared Documents"));
        Assert.Equal(SharePointNaming.PathKey(3, "Procedure", "SOP-1.pdf"), SharePointNaming.PathKey(3, "procedure", "sop-1.PDF"));
        Assert.Equal(["number", "title"], SharePointNaming.TokensOf("{number} - {title} {number}").ToList());
    }

    [Theory] // NFR-025: exponential backoff, capped, never sooner than Retry-After
    [InlineData(1, null, 1)]
    [InlineData(2, null, 2)]
    [InlineData(3, null, 4)]
    [InlineData(5, null, 16)]
    [InlineData(10, null, 360)]
    [InlineData(40, null, 360)]
    [InlineData(1, 600, 10)]
    [InlineData(4, 60, 8)]
    [InlineData(1, 86400, 360)]
    public void Backoff_is_exponential(int attempts, int? retryAfterSeconds, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            SharePointPublisher.Backoff(attempts, retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null, jitter: 1.0));

    [Fact]
    public void Backoff_adds_jitter_within_fifteen_percent()
    {
        for (var i = 0; i < 50; i++)
            Assert.InRange(SharePointPublisher.Backoff(3).TotalMinutes, 4 * 0.85, 4 * 1.15);
    }

    [Fact] // INT-302: system columns can never be mapped
    public void System_columns_are_recognized()
    {
        Assert.True(SharePointMetadata.IsSystemColumn("Created"));
        Assert.True(SharePointMetadata.IsSystemColumn("FileLeafRef"));
        Assert.True(SharePointMetadata.IsSystemColumn("MediaServiceOCR"));
        Assert.False(SharePointMetadata.IsSystemColumn("Title"));
        Assert.False(SharePointMetadata.IsSystemColumn("DocumentNumber"));
        Assert.True(SharePointMetadata.IsValidColumnName("Effective_x0020_Date"));
        Assert.False(SharePointMetadata.IsValidColumnName("Effective Date"));
    }
}
