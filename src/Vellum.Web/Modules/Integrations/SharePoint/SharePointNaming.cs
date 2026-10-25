using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>
/// File and folder names for SharePoint (INT-304): characters SharePoint doesn't allow become underscores, reserved names
/// are escaped and paths stay inside SharePoint's length limit. Folder and file templates use <c>{token}</c> placeholders.
/// </summary>
public static partial class SharePointNaming
{
    /// <summary>
    /// Not allowed in SharePoint Online file and folder names: <c>" * : &lt; &gt; ? / \ |</c> and control characters.
    /// <c>#</c> and <c>%</c> are also replaced: SharePoint only accepts them when the tenant allows it, and they break links
    /// in Office clients and older integrations.
    /// </summary>
    private static readonly HashSet<char> Invalid = ['"', '*', ':', '<', '>', '?', '/', '\\', '|', '#', '%'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lock", "CON", "PRN", "AUX", "NUL", "desktop.ini",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxNameLength = 255;
    /// <summary>SharePoint's limit for the decoded path of a file is 400 characters; keep a margin for the site and library.</summary>
    public const int MaxPathLength = 380;

    /// <summary>Make one file or folder name valid for SharePoint, replacing disallowed characters with underscores.</summary>
    public static string SanitizeName(string? name)
    {
        var sb = new StringBuilder((name ?? "").Length);
        foreach (var c in name ?? "") sb.Append(c < 32 || c == 127 || Invalid.Contains(c) ? '_' : c);
        var s = WhiteSpace().Replace(sb.ToString(), " ").Trim();
        if (s.StartsWith("~$", StringComparison.Ordinal)) s = "_" + s[1..];
        // Names can't end with a period (and "." / ".." would be path traversal).
        if (s.EndsWith('.')) s = s.TrimEnd('.') + "_";
        s = s.Replace("_vti_", "_vti-", StringComparison.OrdinalIgnoreCase);
        if (s.Length == 0) return "_";
        var stem = Path.GetFileNameWithoutExtension(s);
        if (Reserved.Contains(s) || Reserved.Contains(stem)) s = "_" + s;
        return Truncate(s, MaxNameLength);
    }

    /// <summary>Shorten a file name to <paramref name="max"/> characters, keeping its extension.</summary>
    public static string Truncate(string name, int max)
    {
        if (name.Length <= max) return name;
        var ext = Path.GetExtension(name);
        if (ext.Length > 10) ext = "";
        var stem = name[..^ext.Length];
        return stem[..Math.Max(1, max - ext.Length)].TrimEnd(' ', '.') + ext;
    }

    /// <summary>Normalize a folder path: segments sanitized, empty segments dropped, "/" separated, no leading or trailing slash.</summary>
    public static string NormalizeFolder(string? path) =>
        string.Join('/', (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeName));

    /// <summary>Server-relative library URL: leading slash, no trailing slash, single separators.</summary>
    public static string NormalizeRelativeUrl(string? url)
    {
        var parts = (url ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return "/" + string.Join('/', parts);
    }

    /// <summary>"/sites/QMS/Controlled Documents" → ("/sites/QMS", "Controlled Documents"). A one-segment URL is a library of the root site.</summary>
    public static (string SitePath, string LibrarySegment) SplitLibraryUrl(string relativeUrl)
    {
        var parts = NormalizeRelativeUrl(relativeUrl).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ("", "");
        return (parts.Length == 1 ? "" : "/" + string.Join('/', parts[..^1]), parts[^1]);
    }

    public static string Join(string folder, string file) => folder.Length == 0 ? file : folder + "/" + file;

    /// <summary>Case-insensitive identity of a published location (SharePoint URLs are case-insensitive).</summary>
    public static string PathKey(int libraryId, string folder, string file) =>
        $"{libraryId}:/{Join(folder, file)}".ToLowerInvariant();

    // ------------------------------------------------------------------ templates

    /// <summary>Tokens every template understands, besides field keys.</summary>
    public static readonly IReadOnlyList<(string Token, string Meaning)> SystemTokens =
    [
        ("number", "Record number, e.g. SOP-00012"),
        ("revision", "Revision, e.g. B"),
        ("title", "Record title"),
        ("form", "Form name"),
        ("app", "Application name"),
        ("phase", "Current phase"),
        ("status", "Effective, Superseded, Obsolete, Draft…"),
        ("effective_date", "Effective date (yyyy-MM-dd)"),
        ("year", "Year of the effective date, or of creation"),
        ("file", "Original file name without extension"),
    ];

    /// <summary>Resolve a <c>{token}</c> template against a record. Unknown tokens resolve to an empty string.</summary>
    public static string Apply(string template, RecordSnapshot snap, UserDirectory dir, string? originalFileName = null) =>
        Engine.Records.FormRules.ApplyTemplate(template, key => TokenValue(key.Trim(), snap, dir, originalFileName) ?? "");

    public static string? TokenValue(string key, RecordSnapshot snap, UserDirectory dir, string? originalFileName = null)
    {
        var r = snap.Record;
        switch (key)
        {
            case "number": return r.Number;
            case "revision": return r.Revision ?? "";
            case "title": return r.Title;
            case "form": return snap.Form.Name;
            case "app": return snap.Config.AppsById.TryGetValue(r.AppId, out var app) ? app.Name : "";
            case "phase": return snap.Phase?.Name ?? "";
            case "status": return Status(r, snap.Phase, snap.Form);
            case "effective_date": return r.EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            case "year": return (r.EffectiveDate?.Year ?? r.CreatedAt.Year).ToString(CultureInfo.InvariantCulture);
            case "file": return originalFileName != null ? Path.GetFileNameWithoutExtension(originalFileName) : "";
        }
        var f = snap.Form.Fields.FirstOrDefault(x => x.Key == key);
        if (f == null) return null;
        if (f.Type == FieldType.Attachment) return string.Join("; ", snap.AttachmentsOf(f.Key).Where(a => a.RenditionOfId == null).Select(a => a.FileName));
        return FieldCodec.Display(f, snap.Data.Main[f.Key], snap.Config, dir, TimeZoneInfo.Utc);
    }

    /// <summary>Document status as published to the status column (SAF-043).</summary>
    public static string Status(Record r, PhaseDefinition? phase, FormDefinition form)
    {
        if (r.IsEffective && !r.IsArchived) return "Effective";
        if (r.IsArchived && r.ArchiveReason == "superseded") return "Superseded";
        if (r.IsArchived && r.ArchiveReason == "obsolete" || r.PhaseType == PhaseType.Obsolete) return "Obsolete";
        if (r.IsArchived) return "Archived";
        return r.PhaseType switch
        {
            PhaseType.Voided => "Voided",
            PhaseType.Completed => "Completed",
            _ when form.RevisionControlled => "Draft",
            _ => phase?.Name ?? "Open",
        };
    }

    /// <summary>Folder from a template: token values can't introduce extra levels; literal "/" in the template does.</summary>
    public static string Folder(string template, RecordSnapshot snap, UserDirectory dir)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        var resolved = Engine.Records.FormRules.ApplyTemplate(template, key => (TokenValue(key.Trim(), snap, dir) ?? "").Replace('/', '_').Replace('\\', '_'));
        return NormalizeFolder(resolved);
    }

    /// <summary>File name for one published file: template (or the original name) plus the extension of the content published.</summary>
    public static string FileName(string? template, RecordSnapshot snap, UserDirectory dir, string originalFileName, string publishedExtension)
    {
        var stem = string.IsNullOrWhiteSpace(template)
            ? Path.GetFileNameWithoutExtension(originalFileName)
            : Apply(template, snap, dir, originalFileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = snap.Record.Number;
        var ext = publishedExtension.StartsWith('.') || publishedExtension.Length == 0 ? publishedExtension : "." + publishedExtension;
        return SanitizeName(Truncate(stem.Replace('/', '_').Replace('\\', '_'), MaxNameLength - ext.Length) + ext);
    }

    /// <summary>Keep folder + file inside SharePoint's path length limit by shortening the file name.</summary>
    public static string FitPath(string folder, string file)
    {
        var room = MaxPathLength - (folder.Length + 1);
        return room >= file.Length ? file : Truncate(file, Math.Max(20, room));
    }

    /// <summary>Tokens used in a template ("{number} - {title}" → number, title).</summary>
    public static IEnumerable<string> TokensOf(string? template) =>
        TokenPattern().Matches(template ?? "").Select(m => m.Groups[1].Value.Trim()).Distinct();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TokenPattern();
}
