using System.Globalization;

namespace Vellum.Engine.Records;

/// <summary>One stored value. Exactly one of the typed members is used, depending on the field type.</summary>
public readonly record struct Val(string? Text = null, decimal? Number = null, DateTime? Date = null, int? RefId = null, Guid? RefGuid = null)
{
    public bool IsEmpty => string.IsNullOrEmpty(Text) && Number is null && Date is null && RefId is null && RefGuid is null;

    /// <summary>Stable, culture-invariant form used for diffs and content hashes.</summary>
    public string Canonical =>
        Text ?? Number?.ToString("G29", CultureInfo.InvariantCulture) ?? Date?.Ticks.ToString(CultureInfo.InvariantCulture)
        ?? RefId?.ToString(CultureInfo.InvariantCulture) ?? RefGuid?.ToString("N") ?? "";

    public static Val Of(string? s) => new(Text: s);
    public static Val Of(decimal d) => new(Number: d);
    public static Val Of(DateOnly d) => new(Date: d.ToDateTime(TimeOnly.MinValue));
    public static Val OfUtc(DateTime d) => new(Date: d);
    public static Val Ref(int id) => new(RefId: id);
    public static Val Link(Guid id) => new(RefGuid: id);
    public static Val Bool(bool b) => new(RefId: b ? 1 : 0);
}

/// <summary>Field key → ordered values. Used for record data, proposed edits and subform rows.</summary>
public sealed class ValueBag
{
    private readonly Dictionary<string, List<Val>> _d = new(StringComparer.Ordinal);

    public IReadOnlyList<Val> this[string key] => _d.TryGetValue(key, out var l) ? l : [];
    public IEnumerable<string> Keys => _d.Keys;
    public bool Has(string key) => _d.TryGetValue(key, out var l) && l.Count > 0;
    /// <summary>True when the key was set (even to empty). Used to tell "cleared" from "not submitted".</summary>
    public bool ContainsKey(string key) => _d.ContainsKey(key);

    public void Set(string key, IEnumerable<Val> values) => _d[key] = values.Where(v => !v.IsEmpty).ToList();
    public void Set(string key, Val value) => Set(key, [value]);
    public void Clear(string key) => _d.Remove(key);

    public string? Text(string key) => this[key].Select(v => v.Text).FirstOrDefault(t => t != null);
    public decimal? Number(string key) => this[key].Select(v => v.Number).FirstOrDefault(n => n != null);
    public DateOnly? Date(string key) => this[key].Select(v => v.Date).FirstOrDefault(d => d != null) is { } d ? DateOnly.FromDateTime(d) : null;
    public DateTime? DateTime(string key) => this[key].Select(v => v.Date).FirstOrDefault(d => d != null);
    public int? Ref(string key) => this[key].Select(v => v.RefId).FirstOrDefault(r => r != null);
    public IReadOnlyList<int> Refs(string key) => this[key].Where(v => v.RefId != null).Select(v => v.RefId!.Value).ToList();
    public Guid? Link(string key) => this[key].Select(v => v.RefGuid).FirstOrDefault(g => g != null);
    public IReadOnlyList<Guid> Links(string key) => this[key].Where(v => v.RefGuid != null).Select(v => v.RefGuid!.Value).ToList();
    public bool? Bool(string key) => Ref(key) is { } r ? r == 1 : null;

    public string Canonical(string key) => string.Join("\u001e", this[key].Select(v => v.Canonical));

    public ValueBag Clone()
    {
        var c = new ValueBag();
        foreach (var (k, v) in _d) c._d[k] = [.. v];
        return c;
    }
}

public sealed class RowBag
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int Order { get; set; }
    public bool IsNew { get; set; }
    public Guid? CreatedRecordId { get; set; }
    public ValueBag Values { get; set; } = new();

    public RowBag Clone() => new() { Id = Id, Order = Order, IsNew = IsNew, CreatedRecordId = CreatedRecordId, Values = Values.Clone() };
}

/// <summary>All field data of a record: main-form values plus subform rows by section key.</summary>
public sealed class RecordData
{
    public ValueBag Main { get; set; } = new();
    public Dictionary<string, List<RowBag>> Rows { get; set; } = new(StringComparer.Ordinal);

    public List<RowBag> RowsOf(string sectionKey)
    {
        if (!Rows.TryGetValue(sectionKey, out var rows)) Rows[sectionKey] = rows = [];
        return rows;
    }

    public RecordData Clone() => new()
    {
        Main = Main.Clone(),
        Rows = Rows.ToDictionary(kv => kv.Key, kv => kv.Value.Select(r => r.Clone()).ToList(), StringComparer.Ordinal),
    };
}
