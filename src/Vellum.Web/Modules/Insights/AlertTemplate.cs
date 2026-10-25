using System.Globalization;
using System.Text;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Modules.Insights;

/// <summary>
/// The value language of an alert's create-record action (RPT-603: "pairs of field and value, or a script"). It is a closed
/// token template, never code: text with <c>{token}</c> or <c>{token|filter|filter:arg}</c> placeholders, parsed by hand
/// and resolved from the alert context. Unknown tokens and filters are rejected when the template is saved and fail the run
/// visibly (RPT-606). <c>{{</c> and <c>}}</c> are literal braces.
/// </summary>
public static class AlertTemplate
{
    public sealed record TokenDoc(string Token, string Meaning);

    /// <summary>Documented in the editor (the single source of the help text).</summary>
    public static readonly TokenDoc[] Tokens =
    [
        new("{today}", "Today's date. {today+30} or {today-7} adds or subtracts calendar days."),
        new("{due}", "The due date the new record gets."),
        new("{count}", "Matching records in the group (per record: the record's group; per report: all matches)."),
        new("{groups}", "Groups that reached the minimum in this report."),
        new("{minimum}", "The alert's minimum number of matches."),
        new("{group}", "The group's match values, for example the product. {group.KEY} is one match column."),
        new("{record}", "The matched record: its number in text, a link in a link field. Per group or report: the most recent match."),
        new("{record.number}", "Number, title, phase, created, due or creator of the matched record. {record.KEY} is any field of the watched forms."),
        new("{records}", "Every matched record of the group: their numbers in text, links in a link field."),
        new("{alert.name}", "The alert's name. Also {alert.number}, {report.number} and {report.link}."),
    ];

    public sealed record FilterDoc(string Filter, string Meaning);

    public static readonly FilterDoc[] FilterDocs =
    [
        new("upper", "UPPER CASE"), new("lower", "lower case"), new("trim", "remove surrounding spaces"),
        new("truncate:N", "at most N characters"), new("default:text", "use text when the value is empty"),
    ];

    private static readonly HashSet<string> Simple = ["today", "due", "count", "groups", "minimum", "group", "record", "records",
        "alert.name", "alert.number", "report.number", "report.link"];

    /// <summary>System columns a <c>{record.KEY}</c> token can read besides the form's fields.</summary>
    public static readonly string[] RecordSystem = ["number", "title", "phase", "created", "due", "creator"];

    private static readonly HashSet<string> FilterNames = ["upper", "lower", "trim", "truncate", "default"];

    public sealed record Filter(string Name, string? Arg);

    /// <summary>A literal run of text, or a token with its filters.</summary>
    public sealed record Segment(string? Literal, string? Token, IReadOnlyList<Filter> Filters)
    {
        public bool IsToken => Token != null;
    }

    public sealed record Parsed(IReadOnlyList<Segment> Segments, string? Error)
    {
        /// <summary>The template is a single token without filters (surrounding spaces ignored): its typed value is used as is.</summary>
        public Segment? Whole => Segments.Where(s => s.IsToken || !string.IsNullOrWhiteSpace(s.Literal)).ToList() is [{ IsToken: true, Filters.Count: 0 } only] ? only : null;
        public IEnumerable<string> TokenNames => Segments.Where(s => s.IsToken).Select(s => s.Token!);
        public bool HasTokens => Segments.Any(s => s.IsToken);
    }

    public static Parsed Parse(string? template)
    {
        var segs = new List<Segment>();
        var text = template ?? "";
        var lit = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '{' && i + 1 < text.Length && text[i + 1] == '{') { lit.Append('{'); i += 2; continue; }
            if (ch == '}' && i + 1 < text.Length && text[i + 1] == '}') { lit.Append('}'); i += 2; continue; }
            if (ch != '{') { lit.Append(ch); i++; continue; }
            var close = text.IndexOf('}', i + 1);
            if (close < 0) return new Parsed(segs, $"A token isn't closed: \"{Clip(text[i..])}\". Close it with }} or write {{{{ for a literal brace.");
            var inner = text[(i + 1)..close];
            if (inner.Contains('{')) return new Parsed(segs, $"Tokens can't be nested: \"{Clip(text[i..(close + 1)])}\".");
            var parts = inner.Split('|');
            var name = parts[0].Trim();
            if (name.Length == 0) return new Parsed(segs, "A token is empty: write a name between the braces, for example {group}.");
            var filters = new List<Filter>();
            foreach (var raw in parts.Skip(1))
            {
                var colon = raw.IndexOf(':');
                var fname = (colon < 0 ? raw : raw[..colon]).Trim().ToLowerInvariant();
                var arg = colon < 0 ? null : raw[(colon + 1)..];
                if (!FilterNames.Contains(fname)) return new Parsed(segs, $"Unknown filter \"{fname}\" in {{{inner}}}. Use {string.Join(", ", FilterDocs.Select(f => f.Filter))}.");
                if (fname == "truncate" && (!int.TryParse(arg?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 4000))
                    return new Parsed(segs, $"truncate needs a length from 1 to 4000, for example {{{name}|truncate:200}}.");
                if (fname == "default" && arg == null) return new Parsed(segs, $"default needs a text, for example {{{name}|default:unknown}}.");
                filters.Add(new Filter(fname, fname == "truncate" ? arg!.Trim() : arg));
            }
            if (lit.Length > 0) { segs.Add(new Segment(lit.ToString(), null, [])); lit.Clear(); }
            segs.Add(new Segment(null, name, filters));
            i = close + 1;
        }
        if (lit.Length > 0) segs.Add(new Segment(lit.ToString(), null, []));
        return new Parsed(segs, null);
    }

    private static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>
    /// Whether a token name is known. <paramref name="matchColumns"/> are the alert's match columns; <paramref name="recordFields"/>
    /// the fields of the watched forms the owner may read. Returns an error message, or null when the token is fine.
    /// </summary>
    public static string? CheckToken(string token, IReadOnlyCollection<string> matchColumns, IReadOnlyCollection<string> recordFields)
    {
        if (Simple.Contains(token)) return null;
        if (TodayOffset(token) != null) return null;
        if (token.StartsWith("today", StringComparison.Ordinal)) return $"{{{token}}}: write today+N or today-N with N from 0 to 3660 days.";
        if (token.StartsWith("group.", StringComparison.Ordinal))
            return matchColumns.Contains(token[6..]) ? null : $"{{{token}}}: '{token[6..]}' isn't one of the alert's match columns ({string.Join(", ", matchColumns)}).";
        if (token.StartsWith("record.", StringComparison.Ordinal))
        {
            var k = token[7..];
            return RecordSystem.Contains(k) || recordFields.Contains(k) ? null : $"{{{token}}}: the watched forms have no field '{k}' you can read.";
        }
        return $"Unknown token {{{token}}}. See the token reference below the field values.";
    }

    /// <summary>today+N / today-N → N (signed), today → 0; anything else → null.</summary>
    public static int? TodayOffset(string token)
    {
        if (token == "today") return 0;
        if (token.Length < 7 || !token.StartsWith("today", StringComparison.Ordinal) || token[5] is not ('+' or '-')) return null;
        if (!int.TryParse(token[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 3660) return null;
        return token[5] == '+' ? n : -n;
    }

    // ------------------------------------------------------------------ resolution

    /// <summary>A token's value: display text plus, where it has one, a typed value that keeps keywords, people, dates and links intact.</summary>
    public sealed record Value(string Text, FieldType? Type = null, IReadOnlyList<Val>? Vals = null)
    {
        public static readonly Value Empty = new("");
    }

    public static string ApplyFilters(string text, IEnumerable<Filter> filters)
    {
        foreach (var f in filters)
            text = f.Name switch
            {
                "upper" => text.ToUpperInvariant(),
                "lower" => text.ToLowerInvariant(),
                "trim" => text.Trim(),
                "truncate" => int.Parse(f.Arg!, CultureInfo.InvariantCulture) is var n && text.Length > n ? text[..n].TrimEnd() : text,
                "default" => string.IsNullOrWhiteSpace(text) ? f.Arg ?? "" : text,
                _ => text,
            };
        return text;
    }

    /// <summary>
    /// Render a template for one target field. A template that is a single token keeps the token's typed value when the
    /// field accepts it (for example {record.owner} into a person field, {records} into a link field); anything else is
    /// rendered to text and parsed for the field's type. Returns the values (empty when the template renders empty) or an error.
    /// </summary>
    public static (List<Val> Values, string? Error) Render(string? template, FieldDefinition target, Func<string, Value> resolve, ValueParser parser)
    {
        var parsed = Parse(template);
        if (parsed.Error != null) return ([], parsed.Error);
        if (parsed.Whole is { } whole)
        {
            var v = resolve(whole.Token!);
            if (v.Vals is { Count: > 0 } && v.Type is { } t && parser.Convert(v.Vals, t, target) is { } converted)
                return (converted, null);
            return parser.FromText(target, v.Text);
        }
        var sb = new StringBuilder();
        foreach (var s in parsed.Segments)
            sb.Append(s.IsToken ? ApplyFilters(resolve(s.Token!).Text, s.Filters) : s.Literal);
        return parser.FromText(target, sb.ToString());
    }
}

/// <summary>Turns rendered text (or typed token values) into stored values for a target field.</summary>
public sealed class ValueParser(ConfigModel cfg, UserDirectory dir, Func<string, FieldDefinition, Guid?>? recordByNumber = null)
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd MMM yyyy", "d MMM yyyy", "dd/MM/yyyy", "MM/dd/yyyy"];

    /// <summary>Typed token values for a field of another type or keyword list, or null when they can't be carried over (text is used instead).</summary>
    public List<Val>? Convert(IReadOnlyList<Val> vals, FieldType source, FieldDefinition target)
    {
        List<Val>? result = (source, target.Type) switch
        {
            (_, FieldType.Text or FieldType.TextArea or FieldType.Url) => null,
            (FieldType.Keyword or FieldType.KeywordMulti, FieldType.Keyword or FieldType.KeywordMulti) => MapKeywords(vals, target),
            (FieldType.User or FieldType.UserMulti, FieldType.User or FieldType.UserMulti) => vals.ToList(),
            (FieldType.Link, FieldType.Link) => vals.ToList(),
            (FieldType.Date, FieldType.Date) => vals.ToList(),
            (FieldType.DateTime, FieldType.Date) => vals.Select(v => v.Date is { } d ? Val.Of(DateOnly.FromDateTime(d)) : v).ToList(),
            (FieldType.Date or FieldType.DateTime, FieldType.DateTime) => vals.ToList(),
            (FieldType.Number or FieldType.Currency, FieldType.Number or FieldType.Currency) => vals.ToList(),
            (FieldType.YesNo, FieldType.YesNo) => vals.ToList(),
            _ => null,
        };
        if (result == null) return null;
        return FieldCodec.IsMulti(target.Type) ? result : result.Take(1).ToList();
    }

    private List<Val>? MapKeywords(IReadOnlyList<Val> vals, FieldDefinition target)
    {
        var list = new List<Val>();
        foreach (var v in vals)
        {
            var kw = cfg.Keyword(v.RefId);
            if (kw == null) continue;
            var mapped = target.KeywordList != null ? cfg.Keyword(target.KeywordList, kw.Key) : null;
            if (mapped == null) return null; // fall back to the label, which FromText matches by label
            list.Add(Val.Ref(mapped.Id));
        }
        return list;
    }

    /// <summary>Parse text for a field. Multi-value fields take several values separated by semicolons.</summary>
    public (List<Val> Values, string? Error) FromText(FieldDefinition f, string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return ([], null);
        var parts = FieldCodec.IsMulti(f.Type) && f.Type != FieldType.Text
            ? s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [s];
        var list = new List<Val>();
        foreach (var p in parts)
        {
            var (v, err) = One(f, p);
            if (err != null) return ([], $"{f.Label}: {err}");
            list.Add(v);
        }
        return (list.DistinctBy(v => v.Canonical).ToList(), null);
    }

    private (Val Value, string? Error) One(FieldDefinition f, string s)
    {
        switch (f.Type)
        {
            case FieldType.Text or FieldType.TextArea:
                return (Val.Of(s), null);
            case FieldType.Url:
                return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? (Val.Of(uri.ToString()), null) : (default, $"\"{s}\" isn't a full https:// address.");
            case FieldType.Number or FieldType.Currency:
                return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (Val.Of(d), null) : (default, $"\"{s}\" isn't a number.");
            case FieldType.Date:
                return DateOnly.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? (Val.Of(date), null) : (default, $"\"{s}\" isn't a date (use yyyy-mm-dd or a {{today}} token).");
            case FieldType.DateTime:
                return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                    ? (Val.OfUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc)), null) : (default, $"\"{s}\" isn't a date and time.");
            case FieldType.YesNo:
                return s.ToLowerInvariant() switch
                {
                    "yes" or "true" or "1" or "y" => (Val.Bool(true), null),
                    "no" or "false" or "0" or "n" => (Val.Bool(false), null),
                    _ => (default, $"\"{s}\" isn't yes or no."),
                };
            case FieldType.Keyword or FieldType.KeywordMulti:
                var options = cfg.Options(f.KeywordList).Where(k => !k.Disabled).ToList();
                var kw = options.FirstOrDefault(k => string.Equals(k.Key, s, StringComparison.OrdinalIgnoreCase))
                         ?? options.FirstOrDefault(k => string.Equals(k.Label, s, StringComparison.OrdinalIgnoreCase));
                return kw != null ? (Val.Ref(kw.Id), null)
                    : (default, $"\"{s}\" isn't one of the options ({string.Join(", ", options.Take(8).Select(o => o.Label))}{(options.Count > 8 ? ", …" : "")}).");
            case FieldType.User or FieldType.UserMulti:
                var u = int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? dir.Get(uid)
                    : dir.Get(s) ?? dir.All.FirstOrDefault(x => string.Equals(x.DisplayName, s, StringComparison.OrdinalIgnoreCase));
                if (u == null) return (default, $"nobody called \"{s}\" is in the directory.");
                return u.Assignable ? (Val.Ref(u.Id), null) : (default, $"{u.DisplayName} isn't active and can't be chosen.");
            case FieldType.Link:
                if (Guid.TryParse(s, out var g)) return (Val.Link(g), null);
                return recordByNumber?.Invoke(s, f) is { } found ? (Val.Link(found), null) : (default, $"no record numbered \"{s}\" can be linked here.");
            default:
                return (default, "this kind of field can't be set by an alert.");
        }
    }
}
