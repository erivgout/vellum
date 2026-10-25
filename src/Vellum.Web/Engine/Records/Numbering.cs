using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;

namespace Vellum.Engine.Records;

/// <summary>PLT-202 / DOC-010 / INV-020: atomic, never-reused record numbers.</summary>
public sealed class NumberingService(VellumDbContext db)
{
    public async Task<long> NextAsync(string scope)
    {
        var result = await db.Database.SqlQueryRaw<long>("""
            MERGE [NumberSequences] WITH (HOLDLOCK) AS t
            USING (SELECT {0} AS [Scope]) AS s ON t.[Scope] = s.[Scope]
            WHEN MATCHED THEN UPDATE SET t.[Next] = t.[Next] + 1
            WHEN NOT MATCHED THEN INSERT ([Scope], [Next]) VALUES (s.[Scope], 2)
            OUTPUT inserted.[Next] - 1 AS [Value];
            """, scope).ToListAsync();
        return result.Single();
    }

    public static string Format(FormDefinition form, long sequence, DateTime now) =>
        form.NumberPrefix + (form.NumberIncludesYear ? now.Year.ToString(CultureInfo.InvariantCulture) + "-" : "") +
        sequence.ToString("D" + Math.Clamp(form.NumberPadding, 1, 12), CultureInfo.InvariantCulture) + form.NumberSuffix;

    /// <summary>DOC-014/015: next revision identifier for automatic formats.</summary>
    public static string? NextRevision(RevisionFormat format, string? current) => format switch
    {
        RevisionFormat.NumericFrom0 => current == null ? "0" : (int.Parse(current, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture),
        RevisionFormat.NumericFrom1 => current == null ? "1" : (int.Parse(current, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture),
        RevisionFormat.Alphabetic => current == null ? "A" : NextAlpha(current),
        _ => null,
    };

    /// <summary>A, B … Z, AA, AB … AZ, BA …</summary>
    public static string NextAlpha(string s)
    {
        var chars = s.ToUpperInvariant().ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] != 'Z') { chars[i]++; return new string(chars); }
            chars[i] = 'A';
        }
        return "A" + new string(chars);
    }
}

/// <summary>ADM-301 / PLT-630: due dates skip weekends and holidays from the business calendar.</summary>
public sealed class BusinessCalendar(VellumDbContext db)
{
    private Dictionary<DateOnly, bool>? _exceptions;

    private async Task<Dictionary<DateOnly, bool>> ExceptionsAsync() =>
        _exceptions ??= await db.CalendarDays.AsNoTracking().ToDictionaryAsync(d => d.Date, d => d.IsWorking);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task<bool> IsWorkingDayAsync(DateOnly d)
    {
        var ex = await ExceptionsAsync();
        if (ex.TryGetValue(d, out var working)) return working;
        return d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    /// <summary>Add days from <paramref name="start"/>; 0 means the start date itself (moved to a working day for business days).</summary>
    public async Task<DateOnly> AddAsync(DateOnly start, int days, bool businessDays)
    {
        if (!businessDays) return start.AddDays(days);
        var d = start;
        while (!await IsWorkingDayAsync(d)) d = d.AddDays(1);
        var added = 0;
        while (added < days)
        {
            d = d.AddDays(1);
            if (await IsWorkingDayAsync(d)) added++;
        }
        return d;
    }
}
