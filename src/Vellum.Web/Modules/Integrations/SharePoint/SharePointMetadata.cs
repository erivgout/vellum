using System.Globalization;
using System.Text.RegularExpressions;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>Values that aren't form fields, available to mapping profiles as <c>$name</c>.</summary>
public sealed record SpSystemValue(string Key, string Label, string Kind);

/// <summary>Per-publication values for system sources.</summary>
public sealed record MetadataContext(string FileName, DateTime PublishedAt, string RecordUrl, string? StatusOverride = null);

public sealed record MetadataResult(Dictionary<string, object?> Fields, List<string> Problems);

/// <summary>
/// INT-302/303: turns a record into SharePoint list-item fields. Mapped columns are matched case-sensitively, must exist
/// and can't be SharePoint system columns. Without a mapping profile, fields and system values whose names match a column
/// exactly are filled in automatically.
/// </summary>
public static partial class SharePointMetadata
{
    public static readonly IReadOnlyList<SpSystemValue> SystemValues =
    [
        new("$number", "Record number", "text"),
        new("$revision", "Revision", "text"),
        new("$title", "Title", "text"),
        new("$status", "Status (Effective, Superseded, Obsolete…)", "text"),
        new("$effective_date", "Effective date", "date"),
        new("$phase", "Phase", "text"),
        new("$form", "Form name", "text"),
        new("$app", "Application name", "text"),
        new("$created", "Created (date and time)", "datetime"),
        new("$modified", "Last modified (date and time)", "datetime"),
        new("$published_at", "Published (date and time)", "datetime"),
        new("$record_url", "Link to the Vellum record", "text"),
        new("$file_name", "Published file name", "text"),
    ];

    /// <summary>
    /// SharePoint system and computed columns. They can never be mapped (INT-302); compared case-insensitively to be safe.
    /// Columns that Graph reports as read-only are refused too.
    /// </summary>
    public static readonly HashSet<string> SystemColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "ID", "GUID", "UniqueId", "Created", "Modified", "Author", "Editor", "Created_x0020_By", "Modified_x0020_By",
        "Created_x0020_Date", "Last_x0020_Modified", "FileLeafRef", "FileRef", "FileDirRef", "FSObjType", "FileSizeDisplay",
        "File_x0020_Size", "File_x0020_Type", "LinkFilename", "LinkFilenameNoMenu", "LinkFilename2", "BaseName", "DocIcon",
        "ContentType", "ContentTypeId", "_UIVersion", "_UIVersionString", "owshiddenversion", "_Level", "_IsCurrentVersion",
        "_ModerationStatus", "_ModerationComments", "CheckoutUser", "CheckedOutUserId", "CheckedOutTitle", "_CheckinComment",
        "ServerUrl", "EncodedAbsUrl", "ProgId", "ScopeId", "SortBehavior", "SyncClientId", "HTML_x0020_File_x0020_Type",
        "_SourceUrl", "_SharedFileIndex", "_CopySource", "_HasCopyDestinations", "TemplateUrl", "xd_ProgID", "xd_Signature",
        "Edit", "SelectTitle", "ItemChildCount", "FolderChildCount", "AppAuthor", "AppEditor", "ParentVersionString",
        "ParentLeafName", "SharedWithUsers", "SharedWithDetails", "MetaInfo", "Order", "WorkflowVersion", "WorkflowInstanceID",
        "InstanceID", "Attachments", "Restricted", "OriginatorId", "NoExecute", "ContentVersion", "AccessPolicy", "_ComplianceTag",
        "_ComplianceFlags", "_ComplianceTagUserId", "_ComplianceTagWrittenTime", "_IsRecord", "ComplianceAssetId", "_VirusStatus",
        "_VirusVendorID", "_VirusInfo", "_DisplayName", "_CommentFlags", "_CommentCount", "_LikeCount", "_ExtendedDescription",
        "_DocIcon", "_ShortcutUrl", "_ShortcutSiteId", "_ShortcutWebId", "_ShortcutUniqueId", "Thumbnail", "Preview",
    };

    public static bool IsSystemColumn(string name) =>
        SystemColumns.Contains(name) || name.StartsWith("MediaService", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_vti_", StringComparison.OrdinalIgnoreCase);

    /// <summary>SharePoint column internal names: letters, digits and underscores (spaces are encoded as _x0020_).</summary>
    public static bool IsValidColumnName(string? name) => name != null && ColumnName().IsMatch(name);

    public static bool IsTemplate(string source) => source.Contains('{');

    /// <summary>Validate one mapping entry's source against the form. Returns an error, or null.</summary>
    public static string? SourceError(string? source, FormDefinition form)
    {
        if (string.IsNullOrWhiteSpace(source)) return "Choose a field, a system value or write a template.";
        if (source.StartsWith('$')) return SystemValues.Any(s => s.Key == source) ? null : $"\"{source}\" isn't a known system value.";
        if (IsTemplate(source))
        {
            var unknown = SharePointNaming.TokensOf(source)
                .Where(t => SharePointNaming.SystemTokens.All(s => s.Token != t) && form.Fields.All(f => f.Key != t)).ToList();
            return unknown.Count == 0 ? null : $"Unknown token{(unknown.Count > 1 ? "s" : "")} in \"{source}\": {string.Join(", ", unknown.Select(u => "{" + u + "}"))}.";
        }
        var field = form.Fields.FirstOrDefault(f => f.Key == source);
        if (field == null) return $"{form.Name} has no field \"{source}\".";
        if (field.Type == FieldType.Prompt) return $"{field.Label} is a prompt and has no value.";
        var section = form.Sections.FirstOrDefault(s => s.Key == field.SectionKey);
        if (section?.IsSubform == true) return $"{field.Label} is in a subform; only main-form fields can be mapped.";
        return null;
    }

    /// <summary>Label of a source for display ("Document type", "System: Revision", "Template").</summary>
    public static string SourceLabel(string source, FormDefinition? form)
    {
        if (source.StartsWith('$')) return SystemValues.FirstOrDefault(s => s.Key == source)?.Label ?? source;
        if (IsTemplate(source)) return "Template";
        return form?.Fields.FirstOrDefault(f => f.Key == source)?.Label ?? source;
    }

    /// <summary>Check mapped columns against the library schema (INT-302). Null columns = unknown schema.</summary>
    public static List<string> ColumnProblems(IEnumerable<string> mappedColumns, IReadOnlyList<SpColumn>? columns)
    {
        var problems = new List<string>();
        foreach (var name in mappedColumns.Distinct(StringComparer.Ordinal))
        {
            if (IsSystemColumn(name)) { problems.Add($"\"{name}\" is a SharePoint system column and can't be mapped."); continue; }
            if (columns == null) continue;
            var col = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            if (col == null)
            {
                var near = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                                       string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                problems.Add($"Column \"{name}\" doesn't exist in the library" + (near != null ? $" (internal names are case-sensitive; did you mean \"{near.Name}\"?)." : "."));
            }
            else if (col.ReadOnly) problems.Add($"Column \"{name}\" is read-only.");
        }
        return problems;
    }

    /// <summary>Build the list-item fields for a record. <paramref name="entries"/> null = automatic matching by name.</summary>
    public static MetadataResult Build(RecordSnapshot snap, UserDirectory dir, IReadOnlyList<SharePointMappingEntry>? entries,
        IReadOnlyList<SpColumn>? columns, MetadataContext ctx, IReadOnlyDictionary<Guid, string>? linkLabels = null)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var problems = new List<string>();
        IEnumerable<(string Source, string Column)> plan;
        if (entries != null)
        {
            plan = entries.OrderBy(e => e.Order).Select(e => (e.Source, e.Column)).ToList();
            problems.AddRange(ColumnProblems(plan.Select(p => p.Column), columns));
        }
        else
        {
            // INT-302: no mapping profile → fill columns whose internal name equals a field key or a system value name.
            var auto = new List<(string, string)>();
            foreach (var c in columns ?? [])
            {
                if (c.ReadOnly || IsSystemColumn(c.Name)) continue;
                if (snap.Form.Fields.Any(f => f.Key == c.Name && f.Type != FieldType.Prompt)) auto.Add((c.Name, c.Name));
                else if (SystemValues.Any(s => s.Key == "$" + c.Name)) auto.Add(("$" + c.Name, c.Name));
            }
            plan = auto;
        }
        foreach (var (source, column) in plan)
        {
            var col = columns?.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.Ordinal));
            fields[column] = Value(source, snap, dir, ctx, col, linkLabels);
        }
        return new MetadataResult(fields, problems);
    }

    private static object? Value(string source, RecordSnapshot snap, UserDirectory dir, MetadataContext ctx, SpColumn? col,
        IReadOnlyDictionary<Guid, string>? linkLabels)
    {
        var r = snap.Record;
        if (source.StartsWith('$'))
        {
            return source switch
            {
                "$number" => r.Number,
                "$revision" => r.Revision,
                "$title" => Line(r.Title),
                "$status" => ctx.StatusOverride ?? SharePointNaming.Status(r, snap.Phase, snap.Form),
                "$effective_date" => r.EffectiveDate is { } d ? DateValue(d.ToDateTime(TimeOnly.MinValue), col) : null,
                "$phase" => snap.Phase?.Name,
                "$form" => snap.Form.Name,
                "$app" => snap.Config.AppsById.TryGetValue(r.AppId, out var a) ? a.Name : null,
                "$created" => DateTimeValue(r.CreatedAt, col),
                "$modified" => DateTimeValue(r.ModifiedAt, col),
                "$published_at" => DateTimeValue(ctx.PublishedAt, col),
                "$record_url" => ctx.RecordUrl,
                "$file_name" => ctx.FileName,
                _ => null,
            };
        }
        if (IsTemplate(source))
        {
            var text = FormRules.ApplyTemplate(source, key => key.Trim() == "status" && ctx.StatusOverride != null
                ? ctx.StatusOverride
                : SharePointNaming.TokenValue(key.Trim(), snap, dir, ctx.FileName) ?? "");
            return text.Length == 0 ? null : Line(text);
        }
        var f = snap.Form.Fields.FirstOrDefault(x => x.Key == source);
        if (f == null) return null;
        if (f.Type == FieldType.Attachment)
        {
            var names = snap.AttachmentsOf(f.Key).Where(x => x.RenditionOfId == null).Select(x => x.FileName).ToList();
            return names.Count == 0 ? null : Line(string.Join("; ", names));
        }
        var vals = snap.Data.Main[f.Key];
        if (vals.Count == 0) return null;
        // INT-303 data type mapping.
        switch (f.Type)
        {
            case FieldType.Number:
            case FieldType.Currency:
                if (col is { Type: "text" or "note" }) return FieldCodec.Display(f, vals, snap.Config, dir, TimeZoneInfo.Utc);
                return vals[0].Number;
            case FieldType.Date:
                return vals[0].Date is { } d ? DateValue(d, col) : null;
            case FieldType.DateTime:
                return vals[0].Date is { } dt ? DateTimeValue(dt, col) : null;
            case FieldType.YesNo:
                var yes = vals[0].RefId == 1;
                return col?.Type == "boolean" ? yes : yes ? "Yes" : "No";
            case FieldType.TextArea:
                return vals[0].Text is { } t ? (t.Length > 63999 ? t[..63999] : t) : null;
            default:
                // Text, keyword, names, link and external link → single line of text; multiple values separated by "; ".
                return Line(string.Join("; ", vals.Select(v => FieldCodec.Display(f, [v], snap.Config, dir, TimeZoneInfo.Utc, linkLabels))));
        }
    }

    /// <summary>Single line of text holds at most 255 characters.</summary>
    private static string Line(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 255 ? s[..254] + "…" : s;
    }

    private static object DateValue(DateTime date, SpColumn? col) =>
        col is { Type: "text" or "note" } ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateTime.SpecifyKind(date.Date, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);

    private static object DateTimeValue(DateTime utc, SpColumn? col) =>
        col is { Type: "text" or "note" } ? utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,254}$")]
    private static partial Regex ColumnName();
}
