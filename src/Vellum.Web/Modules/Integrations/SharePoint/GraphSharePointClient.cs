using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellum.Modules.Integrations.SharePoint;

public sealed class GraphOptions
{
    public string GraphBase { get; init; } = "https://graph.microsoft.com/v1.0";
    public string Authority { get; init; } = "https://login.microsoftonline.com";
    /// <summary>Files up to this size use a single PUT; larger ones use a resumable upload session.</summary>
    public int SimpleUploadLimit { get; init; } = 4 * 1024 * 1024;
    /// <summary>Upload session chunk size. Graph requires a multiple of 320 KiB.</summary>
    public int ChunkSize { get; init; } = 10 * 320 * 1024;
    public TimeSpan ResolveCacheFor { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>Access tokens and resolved site/drive IDs, shared by every client of the process.</summary>
public sealed class GraphCache
{
    public static readonly GraphCache Shared = new();
    internal readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> Tokens = new();
    internal readonly ConcurrentDictionary<string, (string SiteId, string DriveId, string? WebUrl, DateTime At)> Drives = new();

    /// <summary>Forget everything cached for a profile (after its credentials or libraries change).</summary>
    public void Forget(int profileId)
    {
        foreach (var k in Tokens.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal))) Tokens.TryRemove(k, out _);
        foreach (var k in Drives.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal))) Drives.TryRemove(k, out _);
    }
}

/// <summary>
/// SharePoint Online through Microsoft Graph with an app registration (client credentials flow, Sites.Selected).
/// Uses <see cref="HttpClient"/> directly: token acquisition with caching (client secret or certificate assertion),
/// site and drive resolution from the library's server-relative URL, folder creation, simple upload for small files and
/// upload sessions for large ones, list-item fields, delete and move/rename for obsolete handling.
/// </summary>
public sealed class GraphSharePointClient(SharePointProfile profile, SpCredential credential, HttpClient http, GraphOptions? options = null, GraphCache? cache = null)
    : ISharePointClient
{
    private readonly GraphOptions _o = options ?? new GraphOptions();
    private readonly GraphCache _cache = cache ?? GraphCache.Shared;
    private string TokenKey => $"{profile.Id}|{credential.Fingerprint}";

    // ------------------------------------------------------------------ tokens

    public async Task<string> TokenAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cache.Tokens.TryGetValue(TokenKey, out var t) && t.ExpiresAt > DateTime.UtcNow.AddMinutes(5)) return t.Token;
        var form = new Dictionary<string, string>
        {
            ["client_id"] = credential.ClientId,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        };
        var tokenUrl = $"{_o.Authority.TrimEnd('/')}/{Uri.EscapeDataString(credential.TenantId)}/oauth2/v2.0/token";
        if (credential.Secret != null) form["client_secret"] = credential.Secret;
        else if (credential.CertificatePfx != null)
        {
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = ClientAssertion(credential, tokenUrl);
        }
        else throw new SharePointException(SpErrors.Config, "The profile has no client secret or certificate.", false);

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(form) }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new SharePointException(SpErrors.Network, "Microsoft Entra ID couldn't be reached: " + ex.Message, true, inner: ex);
        }
        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new SharePointException(SpErrors.Unavailable, $"Token endpoint returned {(int)res.StatusCode}.", true, RetryAfter(res));
                var (code, msg) = OAuthError(body);
                throw new SharePointException(SpErrors.Auth, $"Couldn't get an access token ({code}): {msg}", false);
            }
            var json = JsonNode.Parse(body)!;
            var token = json["access_token"]?.GetValue<string>() ?? throw new SharePointException(SpErrors.Auth, "The token response had no access token.", false);
            var expires = json["expires_in"] is { } e ? (e.GetValueKind() == JsonValueKind.String ? int.Parse(e.GetValue<string>(), CultureInfo.InvariantCulture) : e.GetValue<int>()) : 3599;
            _cache.Tokens[TokenKey] = (token, DateTime.UtcNow.AddSeconds(expires));
            return token;
        }
    }

    /// <summary>RFC 7523 client assertion signed with the certificate's private key (RS256, x5t = SHA-1 thumbprint).</summary>
    public static string ClientAssertion(SpCredential c, string audience)
    {
        using var cert = X509CertificateLoader.LoadPkcs12(c.CertificatePfx!, c.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
        using var rsa = cert.GetRSAPrivateKey() ?? throw new SharePointException(SpErrors.Config, "The certificate has no RSA private key.", false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.Serialize(new Dictionary<string, string> { ["alg"] = "RS256", ["typ"] = "JWT", ["x5t"] = B64Url(cert.GetCertHash()) });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = audience, ["iss"] = c.ClientId, ["sub"] = c.ClientId, ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = now - 60, ["iat"] = now, ["exp"] = now + 600,
        });
        var signingInput = B64Url(Encoding.UTF8.GetBytes(header)) + "." + B64Url(Encoding.UTF8.GetBytes(payload));
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + B64Url(sig);
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Code, string Message) OAuthError(string body)
    {
        try
        {
            var j = JsonNode.Parse(body);
            var desc = j?["error_description"]?.GetValue<string>() ?? "";
            var first = desc.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? desc;
            var trace = first.IndexOf(" Trace ID:", StringComparison.Ordinal);
            if (trace > 0) first = first[..trace];
            return (j?["error"]?.GetValue<string>() ?? "error", first);
        }
        catch (JsonException) { return ("error", body.Length > 200 ? body[..200] : body); }
    }

    // ------------------------------------------------------------------ HTTP

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct, bool auth = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            var req = build();
            if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(refresh: attempt > 0, ct));
            HttpResponseMessage res;
            try { res = await http.SendAsync(req, ct); }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new SharePointException(SpErrors.Network, "SharePoint couldn't be reached: " + ex.Message, true, inner: ex);
            }
            // An expired or revoked token: refresh it once.
            if (res.StatusCode == HttpStatusCode.Unauthorized && auth && attempt == 0) { res.Dispose(); continue; }
            return res;
        }
    }

    /// <summary>Turn an unsuccessful Graph response into a coded exception (NFR-025: transient errors are retryable).</summary>
    public static async Task<SharePointException> ErrorAsync(HttpResponseMessage res, string what, CancellationToken ct = default)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        string? gcode = null, gmsg = null;
        try
        {
            var err = JsonNode.Parse(body)?["error"];
            gcode = err?["code"]?.GetValue<string>();
            gmsg = err?["message"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        var detail = $"{what}: HTTP {(int)res.StatusCode}" + (gcode != null ? $" {gcode}" : "") + (gmsg != null ? $" – {gmsg}" : "");
        var status = (int)res.StatusCode;
        return status switch
        {
            401 => new SharePointException(SpErrors.Auth, detail, false),
            403 => new SharePointException(SpErrors.Access, detail + ". Grant the app registration write access to this site (Sites.Selected).", false),
            404 => new SharePointException(SpErrors.NotFound, detail, false),
            409 => new SharePointException(SpErrors.Conflict, detail, false),
            423 => new SharePointException(SpErrors.Conflict, detail + " (the file is locked or checked out)", true, RetryAfter(res)),
            429 or 503 or 509 => new SharePointException(SpErrors.Throttled, detail, true, RetryAfter(res)),
            >= 500 => new SharePointException(SpErrors.Unavailable, detail, true, RetryAfter(res)),
            400 when (gmsg ?? "").Contains("field", StringComparison.OrdinalIgnoreCase) || (gmsg ?? "").Contains("column", StringComparison.OrdinalIgnoreCase)
                => new SharePointException(SpErrors.Column, detail, false),
            _ => new SharePointException(SpErrors.Unknown, detail, false),
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage res) =>
        res.Headers.RetryAfter?.Delta ?? (res.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);

    private async Task<JsonNode> JsonAsync(Func<HttpRequestMessage> build, string what, CancellationToken ct)
    {
        using var res = await SendAsync(build, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, what, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
    }

    private static StringContent JsonBody(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string Seg(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    // ------------------------------------------------------------------ resolution

    /// <summary>Site and drive of a library, from its server-relative URL ("/sites/QMS/Controlled Documents").</summary>
    public async Task<(string SiteId, string DriveId, string? WebUrl)> ResolveAsync(SharePointLibrary lib, CancellationToken ct = default)
    {
        var url = SharePointNaming.NormalizeRelativeUrl(lib.RelativeUrl);
        var key = $"{profile.Id}|{profile.Host}|{url}".ToLowerInvariant();
        if (_cache.Drives.TryGetValue(key, out var hit) && hit.At > DateTime.UtcNow - _o.ResolveCacheFor) return (hit.SiteId, hit.DriveId, hit.WebUrl);

        var (sitePath, librarySegment) = SharePointNaming.SplitLibraryUrl(url);
        if (librarySegment.Length == 0) throw new SharePointException(SpErrors.Config, $"Library {lib.Name} has no relative URL.", false);
        var host = Uri.EscapeDataString(profile.Host);
        var siteUrl = sitePath.Length == 0 ? $"{_o.GraphBase}/sites/{host}" : $"{_o.GraphBase}/sites/{host}:/{Seg(sitePath)}";
        JsonNode site;
        try { site = await JsonAsync(() => new HttpRequestMessage(HttpMethod.Get, siteUrl + "?$select=id,webUrl"), $"Site {profile.Host}{sitePath}", ct); }
        catch (SharePointException ex) when (ex.Code == SpErrors.NotFound)
        {
            throw new SharePointException(SpErrors.NotFound, $"Site {profile.Host}{(sitePath.Length == 0 ? "/" : sitePath)} doesn't exist.", false);
        }
        var siteId = site["id"]!.GetValue<string>();
        var drives = await JsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_o.GraphBase}/sites/{siteId}/drives?$select=id,name,webUrl"), "Document libraries", ct);
        JsonNode? drive = null;
        foreach (var d in drives["value"]?.AsArray() ?? [])
        {
            var web = d?["webUrl"]?.GetValue<string>();
            if (web != null && Uri.TryCreate(web, UriKind.Absolute, out var u) &&
                string.Equals(SharePointNaming.NormalizeRelativeUrl(Uri.UnescapeDataString(u.AbsolutePath)), url, StringComparison.OrdinalIgnoreCase))
            { drive = d; break; }
        }
        drive ??= (drives["value"]?.AsArray() ?? []).FirstOrDefault(d => string.Equals(d?["name"]?.GetValue<string>(), librarySegment, StringComparison.OrdinalIgnoreCase));
        if (drive == null) throw new SharePointException(SpErrors.NotFound, $"Library \"{librarySegment}\" doesn't exist in site {profile.Host}{sitePath}.", false);
        var result = (siteId, drive["id"]!.GetValue<string>(), drive["webUrl"]?.GetValue<string>());
        _cache.Drives[key] = (result.siteId, result.Item2, result.Item3, DateTime.UtcNow);
        return result;
    }

    // ------------------------------------------------------------------ ISharePointClient

    public async Task<SpTestResult> TestAsync(IReadOnlyList<SharePointLibrary> libraries, CancellationToken ct = default)
    {
        try { await TokenAsync(refresh: true, ct); }
        catch (SharePointException ex) { return new SpTestResult(false, $"{ex.Code}: {ex.Message}", []); }
        var checks = new List<SpLibraryCheck>();
        foreach (var lib in libraries)
        {
            try
            {
                var (_, driveId, web) = await ResolveAsync(lib, ct);
                var cols = await GetColumnsAsync(lib, ct);
                checks.Add(new SpLibraryCheck(lib.Key, lib.Name, true, $"Resolved {web ?? lib.RelativeUrl}. {cols?.Count ?? 0} column(s).", cols?.Count, driveId));
            }
            catch (SharePointException ex)
            {
                checks.Add(new SpLibraryCheck(lib.Key, lib.Name, false, $"{ex.Code}: {ex.Message}"));
            }
        }
        var ok = checks.All(c => c.Ok);
        return new SpTestResult(ok, ok ? $"Signed in to tenant {credential.TenantId} and resolved {checks.Count} librar{(checks.Count == 1 ? "y" : "ies")}." : "Signed in, but some libraries failed.", checks);
    }

    public async Task<IReadOnlyList<SpColumn>?> GetColumnsAsync(SharePointLibrary library, CancellationToken ct = default)
    {
        var (_, driveId, _) = await ResolveAsync(library, ct);
        var json = await JsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_o.GraphBase}/drives/{driveId}/list/columns"), $"Columns of {library.Name}", ct);
        return (json["value"]?.AsArray() ?? []).OfType<JsonNode>().Select(c => new SpColumn(
            c["name"]?.GetValue<string>() ?? "",
            c["displayName"]?.GetValue<string>(),
            ColumnType(c),
            c["readOnly"]?.GetValue<bool>() == true)).Where(c => c.Name.Length > 0).ToList();
    }

    private static string ColumnType(JsonNode c)
    {
        if (c["text"] is { } t) return t["allowMultipleLines"]?.GetValue<bool>() == true ? "note" : "text";
        foreach (var k in new[] { "number", "currency", "dateTime", "boolean", "choice", "lookup", "calculated" })
            if (c[k] != null) return k;
        if (c["personOrGroup"] != null) return "person";
        return "other";
    }

    public async Task EnsureFolderAsync(SharePointLibrary library, string folderPath, CancellationToken ct = default) =>
        await FolderIdAsync(library, folderPath, ct);

    /// <summary>ID of a folder, created with its parents when missing (DOC-084: a missing folder is created on first publish).</summary>
    private async Task<string> FolderIdAsync(SharePointLibrary library, string folderPath, CancellationToken ct)
    {
        var (_, driveId, _) = await ResolveAsync(library, ct);
        var segments = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return (await JsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_o.GraphBase}/drives/{driveId}/root?$select=id"), "Library root", ct))["id"]!.GetValue<string>();

        using (var probe = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_o.GraphBase}/drives/{driveId}/root:/{Seg(folderPath)}?$select=id,folder"), ct))
        {
            if (probe.IsSuccessStatusCode) return JsonNode.Parse(await probe.Content.ReadAsStringAsync(ct))!["id"]!.GetValue<string>();
            if (probe.StatusCode != HttpStatusCode.NotFound) throw await ErrorAsync(probe, $"Folder {folderPath}", ct);
        }
        var parentId = await FolderIdAsync(library, string.Join('/', segments[..^1]), ct);
        var name = segments[^1];
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_o.GraphBase}/drives/{driveId}/items/{parentId}/children")
        {
            Content = JsonBody(new Dictionary<string, object> { ["name"] = name, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" }),
        }, ct);
        if (res.IsSuccessStatusCode) return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!["id"]!.GetValue<string>();
        if (res.StatusCode == HttpStatusCode.Conflict)
            // Created by someone else at the same moment.
            return (await JsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_o.GraphBase}/drives/{driveId}/root:/{Seg(folderPath)}?$select=id"), $"Folder {folderPath}", ct))["id"]!.GetValue<string>();
        throw await ErrorAsync(res, $"Create folder {folderPath}", ct);
    }

    public async Task<SpItem> UploadAsync(SharePointLibrary library, string folderPath, string fileName, byte[] content, string contentType, CancellationToken ct = default)
    {
        var (_, driveId, _) = await ResolveAsync(library, ct);
        var path = SharePointNaming.Join(folderPath, fileName);
        JsonNode item;
        if (content.Length <= _o.SimpleUploadLimit)
        {
            item = await JsonAsync(() =>
            {
                var body = new ByteArrayContent(content);
                body.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var ctype) ? ctype : new MediaTypeHeaderValue("application/octet-stream");
                return new HttpRequestMessage(HttpMethod.Put, $"{_o.GraphBase}/drives/{driveId}/root:/{Seg(path)}:/content?@microsoft.graph.conflictBehavior=replace") { Content = body };
            }, $"Upload {path}", ct);
        }
        else item = await UploadSessionAsync(driveId, path, content, ct);
        return new SpItem(item["id"]!.GetValue<string>(), item["webUrl"]?.GetValue<string>(), path);
    }

    /// <summary>Resumable upload for large files: create a session, then PUT byte ranges to its pre-authenticated URL.</summary>
    private async Task<JsonNode> UploadSessionAsync(string driveId, string path, byte[] content, CancellationToken ct)
    {
        var session = await JsonAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_o.GraphBase}/drives/{driveId}/root:/{Seg(path)}:/createUploadSession")
        {
            Content = JsonBody(new { item = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "replace" } }),
        }, $"Upload session for {path}", ct);
        var uploadUrl = session["uploadUrl"]?.GetValue<string>();
        if (uploadUrl == null || !Uri.TryCreate(uploadUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new SharePointException(SpErrors.Unknown, "Graph returned an invalid upload URL.", false);
        try
        {
            for (var offset = 0; offset < content.Length; offset += _o.ChunkSize)
            {
                var len = Math.Min(_o.ChunkSize, content.Length - offset);
                var start = offset;
                // The upload URL is pre-authenticated: it must not carry the Authorization header.
                using var res = await SendAsync(() =>
                {
                    var chunk = new ByteArrayContent(content, start, len);
                    chunk.Headers.ContentLength = len;
                    chunk.Headers.ContentRange = new ContentRangeHeaderValue(start, start + len - 1, content.Length);
                    return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = chunk };
                }, ct, auth: false);
                if (res.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                    return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
                if (res.StatusCode != HttpStatusCode.Accepted) throw await ErrorAsync(res, $"Upload {path} (bytes {start}-{start + len - 1})", ct);
            }
        }
        catch (SharePointException)
        {
            try { using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, uploadUrl), CancellationToken.None, auth: false); }
            catch (SharePointException) { }
            throw;
        }
        throw new SharePointException(SpErrors.Unknown, $"The upload session for {path} didn't complete.", true);
    }

    public async Task SetFieldsAsync(SharePointLibrary library, SpItem item, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        if (fields.Count == 0) return;
        var (_, driveId, _) = await ResolveAsync(library, ct);
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{_o.GraphBase}/drives/{driveId}/items/{item.Id}/listItem/fields")
        {
            Content = JsonBody(fields),
        }, ct);
        if (!res.IsSuccessStatusCode)
        {
            var ex = await ErrorAsync(res, $"Set columns of {item.Path}", ct);
            throw res.StatusCode == HttpStatusCode.BadRequest ? new SharePointException(SpErrors.Column, ex.Message, false) : ex;
        }
    }

    public async Task<bool> DeleteAsync(SharePointLibrary library, string itemId, CancellationToken ct = default)
    {
        var (_, driveId, _) = await ResolveAsync(library, ct);
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{_o.GraphBase}/drives/{driveId}/items/{itemId}"), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return false;
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, "Delete file", ct);
        return true;
    }

    public async Task<SpItem?> MoveAsync(SharePointLibrary library, string itemId, string folderPath, string newName, CancellationToken ct = default)
    {
        var (_, driveId, _) = await ResolveAsync(library, ct);
        var folderId = await FolderIdAsync(library, folderPath, ct);
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{_o.GraphBase}/drives/{driveId}/items/{itemId}?@microsoft.graph.conflictBehavior=replace")
        {
            Content = JsonBody(new { parentReference = new { id = folderId }, name = newName }),
        }, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, "Move file", ct);
        var item = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
        return new SpItem(item["id"]!.GetValue<string>(), item["webUrl"]?.GetValue<string>(), SharePointNaming.Join(folderPath, newName));
    }
}
