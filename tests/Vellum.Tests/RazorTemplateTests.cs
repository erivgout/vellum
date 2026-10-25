using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Vellum.Tests;

/// <summary>Static checks over the Razor templates for mistakes the compiler accepts but people would see on the page.</summary>
public partial class RazorTemplateTests
{
    private static string WebRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Vellum.Web"));

    /// <summary>
    /// Razor treats "word@word" as an email address and prints it literally, so text written directly against a code
    /// transition ("open@if (...) { ... }") shows the raw code on the page. A transition right after another expression
    /// ("@a.Name@if") is fine.
    /// </summary>
    [Fact]
    public void Text_never_runs_straight_into_a_code_transition()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(WebRoot(), "*.cshtml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            for (var n = 0; n < lines.Length; n++)
                foreach (Match m in TextThenCode().Matches(lines[n]))
                {
                    // Walk back over the word before the @; if that word is itself an expression (@x.Name), it isn't text.
                    var i = m.Index;
                    while (i > 0 && (char.IsLetterOrDigit(lines[n][i - 1]) || lines[n][i - 1] is '_' or '.' or '!' or '?')) i--;
                    if (i > 0 && lines[n][i - 1] == '@') continue;
                    problems.Add($"{Path.GetRelativePath(WebRoot(), file)}:{n + 1}: {lines[n].Trim()}");
                }
        }
        Assert.True(problems.Count == 0, "Text runs into a Razor transition, so the code is printed literally:\n" + string.Join("\n", problems));
    }

    [GeneratedRegex(@"(?<=[A-Za-z0-9])@(if|foreach|for|while|switch|using|await|Model|Html|Url|Ui|Shell|ViewData|vm)\b")]
    private static partial Regex TextThenCode();
}
