using System.Globalization;

namespace Vellum.Infrastructure;

/// <summary>Cell encoding shared by every CSV export.</summary>
public static class CsvCell
{
    /// <summary>
    /// A CSV cell. Text a spreadsheet would run as a formula (starting with =, @, a tab or a carriage return, or with + or -
    /// unless it's a plain number) gets a leading apostrophe, so an exported title like =HYPERLINK(...) stays text (CSV
    /// injection). Cells are quoted when they need it, or always when <paramref name="quote"/> is set.
    /// </summary>
    public static string Of(string? s, bool quote = false)
    {
        if (s == null) return "";
        if (s.Length == 0) return quote ? "\"\"" : "";
        if (IsFormula(s)) s = "'" + s;
        return quote || s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static bool IsFormula(string s) => s[0] switch
    {
        '=' or '@' or '\t' or '\r' => true,
        '+' or '-' => s.Length > 1 && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        _ => false,
    };
}
