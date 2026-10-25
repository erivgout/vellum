using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Infrastructure;

namespace Vellum.Modules.Supplier;

/// <summary>A detached, read-only view of another record's data (no change tracking), for cross-record rules and panels.</summary>
public sealed class LiteRecord(Record record, FormDefinition form, ConfigModel cfg, RecordData data)
{
    public Record Record { get; } = record;
    public FormDefinition Form { get; } = form;
    public RecordData Data { get; } = data;
    public Guid Id => Record.Id;
    public PhaseDefinition? Phase => cfg.Phase(Record.PhaseId);
    public string PhaseKey => Phase?.Key ?? "";
    public string? Text(string key) => Data.Main.Text(key);
    public decimal? Number(string key) => Data.Main.Number(key);
    public DateOnly? Date(string key) => Data.Main.Date(key);
    public int? UserId(string key) => Data.Main.Ref(key);
    public bool? Bool(string key) => Data.Main.Bool(key);
    public Guid? Link(string key) => Data.Main.Link(key);
    public Keyword? Keyword(string key) => cfg.Keyword(Data.Main.Ref(key));
    public string? KeywordKey(string key) => Keyword(key)?.Key;
    public string? KeywordKey(RowBag row, string key) => cfg.Keyword(row.Values.Ref(key))?.Key;
    public List<RowBag> Rows(string section) => Data.RowsOf(section);
}

/// <summary>
/// No-tracking reads of record data for module rules (skip-lot history, approved-supplier checks), panels and pages.
/// Callers that show data to users must start from <see cref="Engine.Security.AccessService.Readable"/> (INV-081).
/// </summary>
[ScopedService]
public sealed class RecordReader(VellumDbContext db, ConfigCache cfgCache)
{
    private ConfigModel Cfg => cfgCache.Current;

    public async Task<LiteRecord?> LoadAsync(Guid id, bool rows = true) =>
        (await LoadManyAsync([id], rows)).FirstOrDefault();

    public async Task<List<LiteRecord>> LoadManyAsync(IReadOnlyCollection<Guid> ids, bool rows = true)
    {
        if (ids.Count == 0) return [];
        var list = ids.Distinct().ToList();
        var records = await db.Records.AsNoTracking().Where(r => list.Contains(r.Id)).ToListAsync();
        var values = await db.RecordValues.AsNoTracking().Where(v => list.Contains(v.RecordId) && (rows || v.RowId == null)).ToListAsync();
        var subRows = rows ? await db.SubformRows.AsNoTracking().Where(r => list.Contains(r.RecordId)).OrderBy(r => r.Order).ToListAsync() : [];
        var byRecord = values.ToLookup(v => v.RecordId);
        var rowsByRecord = subRows.ToLookup(r => r.RecordId);
        var result = new List<LiteRecord>();
        foreach (var r in records)
        {
            if (!Cfg.FormsById.TryGetValue(r.FormId, out var form)) continue;
            var vals = byRecord[r.Id].ToList();
            var data = new RecordData();
            foreach (var g in vals.Where(v => v.RowId == null).GroupBy(v => v.FieldKey))
                data.Main.Set(g.Key, g.OrderBy(v => v.Index).Select(ToVal));
            foreach (var row in rowsByRecord[r.Id])
            {
                var bag = new RowBag { Id = row.Id, Order = row.Order, CreatedRecordId = row.CreatedRecordId };
                foreach (var g in vals.Where(v => v.RowId == row.Id).GroupBy(v => v.FieldKey))
                    bag.Values.Set(g.Key, g.OrderBy(v => v.Index).Select(ToVal));
                data.RowsOf(row.SectionKey).Add(bag);
            }
            result.Add(new LiteRecord(r, form, Cfg, data));
        }
        return result;
    }

    /// <summary>IDs of records of <paramref name="formKey"/> whose main-form link field <paramref name="fieldKey"/> points at <paramref name="target"/>.</summary>
    public IQueryable<Guid> LinkingTo(IQueryable<Record> scope, string formKey, string fieldKey, Guid target, bool inRows = false)
    {
        if (!Cfg.FormsByKey.TryGetValue(formKey, out var form)) return scope.Where(_ => false).Select(r => r.Id);
        var fid = form.Id;
        return scope.Where(r => r.FormId == fid && db.RecordValues.Any(v => v.RecordId == r.Id && v.FieldKey == fieldKey &&
                (inRows ? v.RowId != null : v.RowId == null) && v.RefGuid == target))
            .Select(r => r.Id);
    }

    /// <summary>Records of a form in the given scope (default: every record; pass a security-trimmed query for user-facing lists).</summary>
    public IQueryable<Record> OfForm(string formKey, IQueryable<Record>? scope = null)
    {
        var q = scope ?? db.Records.AsNoTracking();
        if (!Cfg.FormsByKey.TryGetValue(formKey, out var form)) return q.Where(_ => false);
        var fid = form.Id;
        return q.Where(r => r.FormId == fid);
    }

    private static Val ToVal(RecordValue v) => new(v.Text, v.Number, v.Date, v.RefId, v.RefGuid);
}

/// <summary>Small helpers shared by the supplier and risk behaviors.</summary>
public static class BehaviorHelpers
{
    /// <summary>Set a value in a subform row (computed columns), converting through the field codec.</summary>
    public static void SetRow(ConfigModel cfg, FormDefinition form, RowBag row, string fieldKey, object? value)
    {
        var f = form.Fields.First(x => x.Key == fieldKey);
        row.Values.Set(fieldKey, FieldCodec.FromObject(f, value, cfg));
    }

    public static decimal Round(decimal d, int places) => decimal.Round(d, places, MidpointRounding.AwayFromZero);

    /// <summary>"Q3 2026" when the period is a calendar quarter, otherwise a date range.</summary>
    public static string PeriodLabel(DateOnly start, DateOnly end)
    {
        var q = (start.Month - 1) / 3 + 1;
        var qStart = new DateOnly(start.Year, (q - 1) * 3 + 1, 1);
        var qEnd = qStart.AddMonths(3).AddDays(-1);
        if (start == qStart && end == qEnd) return $"Q{q} {start.Year}";
        if (start.Day == 1 && end == start.AddMonths(1).AddDays(-1)) return start.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return $"{start:dd MMM yyyy} – {end:dd MMM yyyy}";
    }
}
