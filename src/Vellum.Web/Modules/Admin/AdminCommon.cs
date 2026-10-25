using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using Vellum.Domain;

namespace Vellum.Modules.Admin;

/// <summary>Outcome of an administrative command. Pages turn it into a toast or an error banner.</summary>
public sealed record AdminResult(bool Ok, IReadOnlyList<string> Errors, string? Message = null, int? Id = null)
{
    public const string NotAdmin = "Only administrators can do that.";
    public static AdminResult Success(string message, int? id = null) => new(true, [], message, id);
    public static AdminResult Fail(params string[] errors) => new(false, errors);
    public static AdminResult Fail(IEnumerable<string> errors) => new(false, errors.ToList());
    public string Error => string.Join(" ", Errors);
}

/// <summary>Stable JSON snapshots for the configuration audit trail (DSN-1202, INV-145).</summary>
public static class AdminJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Of(object? value) => JsonSerializer.Serialize(value, Options);

    public sealed record DiffRow(string Path, string? Before, string? After)
    {
        public string Kind => Before == null ? "added" : After == null ? "removed" : "changed";
    }

    /// <summary>
    /// Flatten two JSON documents and return the paths whose values differ. Array elements that carry a
    /// <c>key</c>, <c>username</c>, <c>principal</c> or <c>date</c> property are matched by that identity, so a
    /// re-ordered list reads as a re-order rather than as every row changing.
    /// </summary>
    public static List<DiffRow> Diff(string? before, string? after)
    {
        var a = Flatten(before);
        var b = Flatten(after);
        if (a == null || b == null)
        {
            if (before == after) return [];
            return [new DiffRow("value", before, after)];
        }
        var rows = new List<DiffRow>();
        foreach (var path in a.Keys.Union(b.Keys))
        {
            a.TryGetValue(path, out var x);
            b.TryGetValue(path, out var y);
            if (x != y) rows.Add(new DiffRow(path, x, y));
        }
        return rows;
    }

    private static Dictionary<string, string?>? Flatten(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        Walk(node, "", result);
        return result;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string?> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj) Walk(v, path.Length == 0 ? k : $"{path}.{k}", into);
                break;
            case JsonArray arr:
                if (arr.All(x => x is null or JsonValue))
                {
                    into[path] = string.Join(", ", arr.Select(x => x?.ToString() ?? ""));
                    break;
                }
                for (var i = 0; i < arr.Count; i++)
                {
                    var id = arr[i] is JsonObject o
                        ? new[] { "key", "username", "principal", "date", "id" }.Select(p => o[p]?.ToString()).FirstOrDefault(s => !string.IsNullOrEmpty(s))
                        : null;
                    Walk(arr[i], $"{path}[{id ?? i.ToString(CultureInfo.InvariantCulture)}]", into);
                }
                break;
            case null:
                into[path] = null;
                break;
            default:
                into[path] = node.ToString();
                break;
        }
    }

    /// <summary>Readable label for a camelCase JSON path ("passwordMinLength" → "Password min length").</summary>
    public static string Humanize(string path)
    {
        var s = Regex.Replace(path, "(?<=[a-z0-9])([A-Z])", " $1").Replace('.', ' ').Replace("[", " · ").Replace("]", "");
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }
}

public static class AdminText
{
    private static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "");

    public static readonly Regex UsernamePattern = new("^[a-z0-9][a-z0-9._-]{1,39}$", RegexOptions.Compiled);
    public static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);
    public static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    public static readonly string[] ColorVariants = ["success", "warning", "danger", "info", "violet", "secondary", "dark"];

    /// <summary>"Quality Approvers" → "quality-approvers".</summary>
    public static string Slug(string? s, char sep = '-')
    {
        var slug = Regex.Replace((s ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", sep.ToString()).Trim(sep);
        return slug.Length > 60 ? slug[..60].Trim(sep) : slug;
    }

    public static bool IsEmail(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 254) return false;
        return Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$");
    }

    public static string Interval(int minutes) => minutes switch
    {
        <= 0 => "Manual only",
        1 => "Every minute",
        < 60 => $"Every {minutes} minutes",
        60 => "Hourly",
        < 1440 when minutes % 60 == 0 => $"Every {minutes / 60} hours",
        1440 => "Daily",
        10080 => "Weekly",
        _ when minutes % 1440 == 0 => $"Every {minutes / 1440} days",
        _ => $"Every {minutes} minutes",
    };

    public static string Role(AppRole r) => r switch
    {
        AppRole.Manager => "Manager",
        AppRole.Author => "Author",
        AppRole.Reader => "Reader",
        _ => "No access",
    };

    public static IHtmlContent RoleBadge(AppRole r) => new HtmlString(r switch
    {
        AppRole.Manager => "<span class=\"vl-kw vl-kw-violet\">Manager</span>",
        AppRole.Author => "<span class=\"vl-kw vl-kw-info\">Author</span>",
        AppRole.Reader => "<span class=\"vl-kw\">Reader</span>",
        _ => "<span class=\"text-body-tertiary small\">No access</span>",
    });

    public static IHtmlContent StatusBadge(UserStatus s, string? reason = null) => new HtmlString(s switch
    {
        UserStatus.Active => "<span class=\"vl-kw vl-kw-success\">Active</span>",
        UserStatus.Disabled => $"<span class=\"vl-kw vl-kw-warning\" title=\"{E(reason)}\">Disabled</span>",
        _ => $"<span class=\"vl-kw vl-kw-secondary\" title=\"{E(reason)}\">Inactive</span>",
    });

    public static IHtmlContent Outcome(string? outcome) => new HtmlString(outcome switch
    {
        null => "<span class=\"text-body-tertiary small\">Never run</span>",
        "Succeeded" => "<span class=\"vl-kw vl-kw-success\"><i class=\"bi bi-check2\"></i>Succeeded</span>",
        "Failed" => "<span class=\"vl-kw vl-kw-danger\"><i class=\"bi bi-x-lg\"></i>Failed</span>",
        "Running" => "<span class=\"vl-kw vl-kw-info\"><i class=\"bi bi-arrow-repeat\"></i>Running</span>",
        "Cancelled" => "<span class=\"vl-kw vl-kw-warning\" title=\"Stopped because the application shut down; it runs again on the next start\"><i class=\"bi bi-stop-circle\"></i>Cancelled</span>",
        _ => $"<span class=\"vl-kw\">{E(outcome)}</span>",
    });

    /// <summary>Short client description from a user agent string ("Edge on Windows").</summary>
    public static (string Label, string Icon) Client(string? ua)
    {
        if (string.IsNullOrEmpty(ua)) return ("Unknown client", "bi-question-circle");
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : ua.Contains("curl/") ? "curl" : "Browser";
        var os = ua.Contains("Windows") ? "Windows" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" : ua.Contains("Android") ? "Android"
            : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : null;
        var mobile = ua.Contains("Mobile") || ua.Contains("iPhone") || ua.Contains("Android");
        return (os != null ? $"{browser} on {os}" : browser, mobile ? "bi-phone" : "bi-laptop");
    }

    /// <summary>SEC-402: at most the first characters of a session ID may be shown.</summary>
    public static string ShortSession(string id) => id.Length > 8 ? id[..8] + "…" : id;
}

/// <summary>A page of results with the total count.</summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
    public int First => Total == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int Last => Math.Min(Total, Page * PageSize);
}

public static class PagedExtensions
{
    public static async Task<Paged<T>> PageAsync<T>(this IQueryable<T> query, int page, int pageSize = 50)
    {
        var total = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, pages);
        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(query.Skip((page - 1) * pageSize).Take(pageSize));
        return new Paged<T>(items, total, page, pageSize);
    }
}
