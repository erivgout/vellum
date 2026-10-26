using System.Globalization;
using System.Text.RegularExpressions;

namespace Vellum.Tests.E2e;

/// <summary>EHS, assets and projects steps through the pages.</summary>
public static partial class EhsAssetsSteps
{
    /// <summary>
    /// A datetime-local value for a moment in time, in the time zone the form says it uses ("Your time zone: …"), as a
    /// person reading their own clock would type it.
    /// </summary>
    public static string LocalInput(this Page page, DateTime utc)
    {
        var id = TimeZoneHint().Match(page.Text) is { Success: true } m ? m.Groups[1].Value : "UTC";
        var tz = Engine.Records.RecordService.Tz(id);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
    }

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Open the record numbered <paramref name="number"/> from the person's work queue.</summary>
    public static async Task<Page> FromWorkAsync(this Browser b, string number)
    {
        var work = await b.GoAsync("/work");
        var link = work.All("a[href^='/records/']").FirstOrDefault(a => a.TextContent.Contains(number))
            ?? throw new Xunit.Sdk.XunitException($"{number} isn't in the work queue.\n{work.Excerpt()}");
        return await b.GoAsync(link.GetAttribute("href")!);
    }

    [GeneratedRegex(@"Your time zone: ([A-Za-z_/+\-0-9]+)")]
    private static partial Regex TimeZoneHint();
}
