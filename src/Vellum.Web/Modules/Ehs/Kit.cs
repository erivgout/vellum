using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Modules.Kit;

/// <summary>Small helpers shared by the EHS, Assets and Projects modules.</summary>
public static class ModuleKit
{
    /// <summary>Set a (computed) value on a subform row being saved.</summary>
    public static void SetRow(this SavingContext ctx, RowBag row, string fieldKey, object? value)
    {
        var f = ctx.Form.Fields.First(x => x.Key == fieldKey);
        row.Values.Set(fieldKey, FieldCodec.FromObject(f, value, ctx.Config));
    }

    /// <summary>Main-form values of several records at once (no snapshot loading): record ID → bag of the requested fields.</summary>
    public static async Task<Dictionary<Guid, ValueBag>> MainValuesAsync(VellumDbContext db, IReadOnlyCollection<Guid> ids, params string[] keys)
    {
        var result = ids.Distinct().ToDictionary(id => id, _ => new ValueBag());
        if (result.Count == 0) return result;
        var rows = await db.RecordValues.AsNoTracking()
            .Where(v => ids.Contains(v.RecordId) && v.RowId == null && keys.Contains(v.FieldKey))
            .OrderBy(v => v.Index).ToListAsync();
        foreach (var g in rows.GroupBy(v => (v.RecordId, v.FieldKey)))
            result[g.Key.RecordId].Set(g.Key.FieldKey, g.Select(v => new Val(v.Text, v.Number, v.Date, v.RefId, v.RefGuid)));
        return result;
    }

    /// <summary>The reports-to chain above a person (manager, their manager, …), without cycles.</summary>
    public static List<int> ReportsToChain(UserDirectory dir, int? userId, int maxLevels = 6)
    {
        var chain = new List<int>();
        var current = dir.Get(userId);
        while (current?.ReportsToId is { } mgr && chain.Count < maxLevels && !chain.Contains(mgr) && mgr != userId)
        {
            chain.Add(mgr);
            current = dir.Get(mgr);
        }
        return chain;
    }

    public static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public static string Num(decimal? n, int decimals = 2) =>
        n is { } d ? d.ToString("N" + decimals, CultureInfo.InvariantCulture) : "–";

    /// <summary>"3h 05m", "2d 4h", "45m".</summary>
    public static string Span(TimeSpan t)
    {
        t = t.Duration();
        if (t.TotalDays >= 2) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{Math.Max(0, t.Minutes)}m";
    }
}

/// <summary>Acme Medical Devices sites (keyword list <c>ops_site</c>) and their time zones (SAF-024).</summary>
public static class Sites
{
    public static readonly (string Key, string Name, string TimeZone)[] All =
    [
        ("kzo", "Kalamazoo", "America/Detroit"),
        ("bos", "Boston", "America/New_York"),
        ("gal", "Galway", "Europe/Dublin"),
    ];

    public static TimeZoneInfo Tz(string? siteKey) =>
        RecordService.Tz(All.FirstOrDefault(s => s.Key == siteKey).TimeZone ?? "UTC");

    public static string Name(string? key) => All.FirstOrDefault(s => s.Key == key).Name ?? key ?? "–";

    /// <summary>Local wall-clock text with its UTC offset, e.g. "24 Oct 2026 14:05 (UTC-04:00)".</summary>
    public static string Local(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(ModuleKit.Utc(utc), tz);
        return local.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " (" + Infrastructure.Ui.Ui.Abbrev(tz, local) + ")";
    }

    public static void Define(Packaging.AppBuilder app) =>
        app.Keywords("ops_site", "Site", k => { foreach (var s in All) k.Add(s.Key, s.Name); });

    public static DateOnly LocalDate(DateTime utc, string? siteKey) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(ModuleKit.Utc(utc), Tz(siteKey)));

    public static DateOnly Today => BusinessCalendar.Today;
}
