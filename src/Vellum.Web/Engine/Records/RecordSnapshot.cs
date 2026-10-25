using Vellum.Domain;

namespace Vellum.Engine.Records;

/// <summary>A loaded record with its configuration and data. The <see cref="Record"/> entity is tracked by the DbContext.</summary>
public sealed class RecordSnapshot
{
    public required Record Record { get; init; }
    public required FormDefinition Form { get; init; }
    public required ConfigModel Config { get; init; }
    public required RecordData Data { get; set; }
    public List<Attachment> Attachments { get; init; } = [];

    // Tracked storage rows backing Data (used by RecordService to persist diffs).
    internal List<RecordValue> ValueRows { get; init; } = [];
    internal List<SubformRow> SubformRows { get; init; } = [];

    public AppDefinition App => Form.App;
    public WorkflowDefinition? Workflow => Record.WorkflowId is { } id ? Config.WorkflowsById[id] : null;
    public PhaseDefinition? Phase => Config.Phase(Record.PhaseId);
    public bool IsTerminal => Record.WorkflowId != null && Record.PhaseType != PhaseType.Normal;
    public string PhaseKey => Phase?.Key ?? "";

    public FieldDefinition? Field(string key) => Form.Fields.FirstOrDefault(f => f.Key == key);

    // Convenience accessors over main-form values.
    public string? Text(string key) => Data.Main.Text(key);
    public decimal? Number(string key) => Data.Main.Number(key);
    public DateOnly? Date(string key) => Data.Main.Date(key);
    public DateTime? DateTime(string key) => Data.Main.DateTime(key);
    public int? UserId(string key) => Data.Main.Ref(key);
    public IReadOnlyList<int> UserIds(string key) => Data.Main.Refs(key);
    public IReadOnlyList<Guid> Links(string key) => Data.Main.Links(key);
    public bool? Bool(string key) => Data.Main.Bool(key);
    public Keyword? Keyword(string key) => Config.Keyword(Data.Main.Ref(key));
    public string? KeywordKey(string key) => Keyword(key)?.Key;
    public IEnumerable<string> KeywordKeys(string key) => Data.Main.Refs(key).Select(id => Config.Keyword(id)?.Key).OfType<string>();
    public List<RowBag> Rows(string sectionKey) => Data.RowsOf(sectionKey);

    /// <summary>Keyword key of a field inside a subform row.</summary>
    public string? KeywordKey(RowBag row, string key) => Config.Keyword(row.Values.Ref(key))?.Key;

    public IEnumerable<Attachment> AttachmentsOf(string fieldKey, Guid? rowId = null) =>
        Attachments.Where(a => a.FieldKey == fieldKey && a.RowId == rowId && !a.Removed);
}
