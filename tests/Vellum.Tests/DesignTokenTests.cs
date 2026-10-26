using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Vellum.Tests;

/// <summary>
/// Readability of the design system's colour tokens (site.css): every text colour must keep WCAG AA contrast (4.5:1)
/// on every surface it's used on, in both themes, so secondary labels, timestamps and placeholders stay legible.
/// </summary>
public partial class DesignTokenTests
{
    private static string Css([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Vellum.Web", "wwwroot", "css", "site.css"));

    private static Dictionary<string, string> Tokens(string selectorStart)
    {
        var css = Css();
        var start = css.IndexOf(selectorStart, StringComparison.Ordinal);
        var block = css[(css.IndexOf('{', start) + 1)..css.IndexOf('}', start)];
        return Token().Matches(block).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    public static TheoryData<string, string, string> Pairs()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (theme, selector) in new[] { ("light", ":root,"), ("dark", "[data-bs-theme=\"dark\"] {") })
            foreach (var text in new[] { "--vl-text", "--vl-text-2", "--vl-text-3" })
                foreach (var surface in new[] { "--vl-bg", "--vl-surface", "--vl-surface-2", "--vl-surface-3" })
                    data.Add(selector, text, surface);
        // Status colours as text on cards (light theme; the dark theme's status colours are bright on dark surfaces).
        foreach (var status in new[] { "--vl-ok", "--vl-warn", "--vl-bad", "--vl-info", "--vl-violet" })
            data.Add(":root,", status, "--vl-surface");
        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Text_tokens_keep_AA_contrast_on_every_surface(string selector, string text, string surface)
    {
        var t = Tokens(selector);
        var ratio = Contrast(t[text], t[surface]);
        Assert.True(ratio >= 4.5, $"{text} {t[text]} on {surface} {t[surface]} ({selector}) is {ratio:0.00}:1, below 4.5:1.");
    }

    private static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            double C(int i) { var v = int.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * C(1) + 0.7152 * C(3) + 0.0722 * C(5);
        }
        double x = L(a), y = L(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [GeneratedRegex(@"(--vl-[a-z0-9-]+):\s*(#[0-9a-fA-F]{6})\b")]
    private static partial Regex Token();
}
