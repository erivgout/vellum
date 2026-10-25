using System.Collections;
using System.Globalization;
using Vellum.Domain;
using Vellum.Engine.Security;

namespace Vellum.Engine.Records;

/// <summary>Converts between field values and UI input strings, programmatic objects and display text.</summary>
public static class FieldCodec
{
    public static bool IsMulti(FieldType t) => t is FieldType.KeywordMulti or FieldType.UserMulti or FieldType.Link;
    public static bool StoresValues(FieldType t) => t is not (FieldType.Prompt or FieldType.Attachment);

    /// <summary>Parse raw form input for one field. Returns an error message for invalid input.</summary>
    public static (List<Val> Values, string? Error) Parse(FieldDefinition f, IEnumerable<string?> raw, ConfigModel cfg, TimeZoneInfo tz)
    {
        var inputs = raw.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        if (!IsMulti(f.Type) && inputs.Count > 1) inputs = [inputs[0]];
        var list = new List<Val>();
        foreach (var s in inputs)
        {
            switch (f.Type)
            {
                case FieldType.Text or FieldType.TextArea:
                    list.Add(Val.Of(s));
                    break;
                case FieldType.Url:
                    if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                        return ([], "Enter a full https:// address.");
                    list.Add(Val.Of(uri.ToString()));
                    break;
                case FieldType.Number or FieldType.Currency:
                    if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) &&
                        !decimal.TryParse(s, NumberStyles.Number, CultureInfo.CurrentCulture, out d))
                        return ([], "Enter a number.");
                    list.Add(Val.Of(d));
                    break;
                case FieldType.Date:
                    if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                        !DateOnly.TryParse(s, CultureInfo.CurrentCulture, out date))
                        return ([], "Enter a valid date.");
                    list.Add(Val.Of(date));
                    break;
                case FieldType.DateTime:
                    if (!System.DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                        return ([], "Enter a valid date and time.");
                    // PLT-406: entered in the viewer's time zone, stored as UTC.
                    list.Add(Val.OfUtc(TimeZoneInfo.ConvertTimeToUtc(System.DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz)));
                    break;
                case FieldType.Keyword or FieldType.KeywordMulti:
                    var kw = int.TryParse(s, out var kid) ? cfg.Keyword(kid) : f.KeywordList != null ? cfg.Keyword(f.KeywordList, s) : null;
                    if (kw == null || (f.KeywordList != null && cfg.KeywordListsById[kw.ListId].Key != f.KeywordList))
                        return ([], "Choose a value from the list.");
                    list.Add(Val.Ref(kw.Id));
                    break;
                case FieldType.YesNo:
                    var yes = s.ToLowerInvariant() is "yes" or "true" or "1" or "on";
                    var no = s.ToLowerInvariant() is "no" or "false" or "0" or "off";
                    if (!yes && !no) return ([], "Choose Yes or No.");
                    list.Add(Val.Bool(yes));
                    break;
                case FieldType.User or FieldType.UserMulti:
                    if (!int.TryParse(s, out var uid)) return ([], "Choose a person from the directory.");
                    list.Add(Val.Ref(uid));
                    break;
                case FieldType.Link:
                    if (!Guid.TryParse(s, out var g)) return ([], "Choose a record.");
                    list.Add(Val.Link(g));
                    break;
            }
        }
        return (list.DistinctBy(v => v.Canonical).ToList(), null);
    }

    /// <summary>Convert a programmatic value (behaviors, seeding, imports) to stored values.</summary>
    public static List<Val> FromObject(FieldDefinition f, object? o, ConfigModel cfg)
    {
        if (o == null) return [];
        if (o is Val v) return [v];
        if (o is IEnumerable<Val> vals) return vals.ToList();
        if (o is not string && o is IEnumerable e)
            return e.Cast<object?>().SelectMany(x => FromObject(f, x, cfg)).DistinctBy(x => x.Canonical).ToList();

        switch (f.Type)
        {
            case FieldType.Number or FieldType.Currency:
                return [Val.Of(Convert.ToDecimal(o, CultureInfo.InvariantCulture))];
            case FieldType.Date:
                return o switch
                {
                    DateOnly d => [Val.Of(d)],
                    DateTime dt => [Val.Of(DateOnly.FromDateTime(dt))],
                    string s => [Val.Of(DateOnly.Parse(s, CultureInfo.InvariantCulture))],
                    _ => throw new ArgumentException($"Can't convert {o.GetType().Name} to a date for '{f.Key}'."),
                };
            case FieldType.DateTime:
                return o switch
                {
                    DateTime dt => [Val.OfUtc(dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt)],
                    DateOnly d => [Val.OfUtc(d.ToDateTime(TimeOnly.MinValue))],
                    string s => [Val.OfUtc(System.DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal))],
                    _ => throw new ArgumentException($"Can't convert {o.GetType().Name} to a date-time for '{f.Key}'."),
                };
            case FieldType.Keyword or FieldType.KeywordMulti:
                var kw = o switch
                {
                    int id => cfg.Keyword(id),
                    Keyword k => k,
                    string s => f.KeywordList != null ? cfg.Keyword(f.KeywordList, s) : null,
                    _ => null,
                } ?? throw new ArgumentException($"Unknown keyword '{o}' for field '{f.Key}' (list {f.KeywordList}).");
                return [Val.Ref(kw.Id)];
            case FieldType.YesNo:
                return [Val.Bool(o is bool b ? b : o.ToString()!.ToLowerInvariant() is "yes" or "true" or "1")];
            case FieldType.User or FieldType.UserMulti:
                return o switch
                {
                    int id => [Val.Ref(id)],
                    UserInfo u => [Val.Ref(u.Id)],
                    User u => [Val.Ref(u.Id)],
                    _ => throw new ArgumentException($"Pass a user ID for '{f.Key}'."),
                };
            case FieldType.Link:
                return o switch
                {
                    Guid g => [Val.Link(g)],
                    Record r => [Val.Link(r.Id)],
                    _ => throw new ArgumentException($"Pass a record ID for '{f.Key}'."),
                };
            default:
                return [Val.Of(o.ToString())];
        }
    }

    /// <summary>Human-readable text (audit trail, exports, titles, search).</summary>
    public static string Display(FieldDefinition f, IReadOnlyList<Val> values, ConfigModel cfg, UserDirectory dir, TimeZoneInfo tz,
        IReadOnlyDictionary<Guid, string>? linkLabels = null)
    {
        if (values.Count == 0) return "";
        return string.Join(", ", values.Select(v => f.Type switch
        {
            FieldType.Number => FormatNumber(v.Number, f.Decimals),
            FieldType.Currency => (f.CurrencyCode is { } c ? c + " " : "") + FormatNumber(v.Number, f.Decimals ?? 2),
            FieldType.Date => v.Date is { } d ? d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : "",
            FieldType.DateTime => v.Date is { } dt ? TimeZoneInfo.ConvertTimeFromUtc(System.DateTime.SpecifyKind(dt, DateTimeKind.Utc), tz).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) : "",
            FieldType.Keyword or FieldType.KeywordMulti => cfg.Keyword(v.RefId)?.Label ?? $"#{v.RefId}",
            FieldType.YesNo => v.RefId == 1 ? "Yes" : "No",
            FieldType.User or FieldType.UserMulti => dir.Name(v.RefId),
            FieldType.Link => v.RefGuid is { } g && linkLabels != null && linkLabels.TryGetValue(g, out var l) ? l : v.RefGuid?.ToString()[..8] ?? "",
            _ => v.Text ?? "",
        }));
    }

    public static string FormatNumber(decimal? n, int? decimals) =>
        n is not { } d ? "" : decimals is { } p ? d.ToString("N" + p, CultureInfo.InvariantCulture) : d.ToString("#,##0.##########", CultureInfo.InvariantCulture);

    /// <summary>Value for an HTML input control.</summary>
    public static string InputValue(FieldDefinition f, Val v, TimeZoneInfo tz) => f.Type switch
    {
        FieldType.Number or FieldType.Currency => v.Number?.ToString(CultureInfo.InvariantCulture) ?? "",
        FieldType.Date => v.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        FieldType.DateTime => v.Date is { } dt ? TimeZoneInfo.ConvertTimeFromUtc(System.DateTime.SpecifyKind(dt, DateTimeKind.Utc), tz).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : "",
        FieldType.Keyword or FieldType.KeywordMulti or FieldType.User or FieldType.UserMulti => v.RefId?.ToString(CultureInfo.InvariantCulture) ?? "",
        FieldType.YesNo => v.RefId == 1 ? "yes" : v.RefId == 0 ? "no" : "",
        FieldType.Link => v.RefGuid?.ToString() ?? "",
        _ => v.Text ?? "",
    };
}
