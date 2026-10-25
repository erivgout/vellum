using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Vellum.Domain;
using Vellum.Engine.Views;

namespace Vellum.Modules.Designer;

// The designer edits an application as one JSON document (a draft, DSN-003). These classes mirror the configuration
// metamodel. `Id` is the live row ID (null for objects that were never published); publishing updates rows in place so
// records keep pointing at their forms and phases. Children are nested (fields inside their sections) and ordered by
// list position. Routes refer to phases by design name.

public sealed class AppDoc
{
    public int? Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Icon { get; set; } = "bi-grid";
    public string Color { get; set; } = "#e11d48";
    public string Suite { get; set; } = "Quality";
    public int Order { get; set; }
    public AppRole DefaultAccess { get; set; }
    public bool HideMyAssignments { get; set; }
    public bool ProtectFromUpgrade { get; set; }
    public bool Disabled { get; set; }
    public bool IsSystem { get; set; }
    public string? PackageVersion { get; set; }
    public List<FormDoc> Forms { get; set; } = [];
    public List<ViewDoc> Views { get; set; } = [];
}

public sealed class FormDoc
{
    public int? Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Icon { get; set; } = "bi-file-earmark-text";
    public bool Disabled { get; set; }
    public bool IsActivity { get; set; }
    public List<string> ParentForms { get; set; } = [];
    public bool IsNonWorkflow { get; set; }
    public bool HiddenFromNew { get; set; }
    public string NumberPrefix { get; set; } = "";
    public string NumberSuffix { get; set; } = "";
    public int NumberPadding { get; set; } = 5;
    public bool NumberIncludesYear { get; set; }
    public string? NumberScope { get; set; }
    public string TitleTemplate { get; set; } = "{title}";
    public bool RevisionControlled { get; set; }
    public RevisionFormat RevisionFormat { get; set; }
    public List<SectionDoc> Sections { get; set; } = [];
    /// <summary>DSN-302: fields detached from the layout (kept, not shown).</summary>
    public List<FieldDoc> Detached { get; set; } = [];
    public List<WorkflowDoc> Workflows { get; set; } = [];

    public IEnumerable<FieldDoc> AllFields => Sections.SelectMany(s => s.Fields).Concat(Detached);
    public IEnumerable<FieldDoc> MainFields => Sections.Where(s => !s.IsSubform).SelectMany(s => s.Fields);
}

public sealed class SectionDoc
{
    public int? Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Tab { get; set; } = "General";
    public string? Icon { get; set; }
    public bool IsSubform { get; set; }
    public string? RowNoun { get; set; }
    public int? MaxRows { get; set; }
    public bool Required { get; set; }
    public bool Optional { get; set; }
    public bool Collapsed { get; set; }
    public List<FieldDoc> Fields { get; set; } = [];
}

public sealed class FieldDoc
{
    public int? Id { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }
    public int Width { get; set; } = 6;
    public bool Required { get; set; }
    public List<string> RequiredInPhases { get; set; } = [];
    public bool PreventNull { get; set; }
    public bool Unique { get; set; }
    public bool Sensitive { get; set; }
    public bool ReadOnly { get; set; }
    public bool ProtectFromAdminUpdate { get; set; }
    public string? KeywordList { get; set; }
    public List<string> LinkForms { get; set; } = [];
    public string? Help { get; set; }
    public string? Placeholder { get; set; }
    public string? DefaultValue { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int? Decimals { get; set; }
    public int? MaxLength { get; set; }
    public string? CurrencyCode { get; set; }
    public bool ShowInHeader { get; set; }
    public string? VisibleWhen { get; set; }
}

public sealed class WorkflowDoc
{
    public int? Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Disabled { get; set; }
    public List<PhaseDoc> Phases { get; set; } = [];
    public List<RouteDoc> Routes { get; set; } = [];
}

public sealed class PhaseDoc
{
    public int? Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Purpose { get; set; }
    public PhaseType Type { get; set; }
    public bool IsStart { get; set; }
    public List<string> EditableSections { get; set; } = [];
    public List<string> ReadOnlySections { get; set; } = [];
    public List<string> HiddenSections { get; set; } = [];
    public AssignmentMode Assignment { get; set; }
    public string? AssigneeField { get; set; }
    public string? AssigneeGroup { get; set; }
    public bool AssignAllMembers { get; set; }
    public RoutingType Routing { get; set; }
    public int QuorumPercent { get; set; } = 100;
    public int? DueDays { get; set; }
    public string? DueDateField { get; set; }
    public bool BusinessDays { get; set; } = true;
    public bool AssignerCanChangeDue { get; set; } = true;
    public List<string> MustCreateForms { get; set; } = [];
    public List<string> MustCompleteForms { get; set; } = [];
    public List<string> ActivityForms { get; set; } = [];
    public string? Color { get; set; }
}

public sealed class RouteDoc
{
    public int? Id { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public RouteKind Kind { get; set; }
    public string? Label { get; set; }
    public bool RequireComment { get; set; }
    /// <summary>Designer flag: the route needs an e-signature (then <see cref="SignatureMeaning"/> is mandatory, DSN-1201).</summary>
    public bool RequireSignature { get; set; }
    public string? SignatureMeaning { get; set; }
    public string? Condition { get; set; }
    public List<string> AllowedPrincipals { get; set; } = [];
}

public sealed class ViewDoc
{
    public int? Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Records";
    public List<string> Forms { get; set; } = [];
    public List<string> Columns { get; set; } = [];
    public string Status { get; set; } = "open";
    public List<string> Phases { get; set; } = [];
    public bool AssignedToMe { get; set; }
    public bool CreatedByMe { get; set; }
    public bool OverdueOnly { get; set; }
    public List<ViewFilter> Filters { get; set; } = [];
    public string SortBy { get; set; } = "number";
    public bool SortDesc { get; set; } = true;
    public int PageSize { get; set; } = 25;
    public bool IsDefault { get; set; }
    public bool Hidden { get; set; }
    public string? CalendarField { get; set; }
}

/// <summary>Serialization, mapping from the live configuration and small document helpers.</summary>
public static class DesignDocs
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value))!;

    public static readonly string[] ViewStatuses = ["open", "completed", "voided", "closed", "all", "effective", "archived"];
    public static readonly string[] FilterOps = ["eq", "ne", "set", "empty"];

    private static readonly Regex KeyPattern = new("^[A-Za-z0-9][A-Za-z0-9_\\-]{0,63}$", RegexOptions.Compiled);

    /// <summary>INV-130: a design name is letters, digits, '_' or '-', at most 64 characters, never only digits.</summary>
    public static string? KeyProblem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "is required";
        if (!KeyPattern.IsMatch(key)) return "may only use letters, digits, '_' and '-' (64 characters at most)";
        if (key.All(char.IsDigit)) return "can't be made only of digits (INV-130)";
        return null;
    }

    /// <summary>Design name suggestion from a display name: "Root cause &amp; plan" → "root_cause_plan".</summary>
    public static string Slug(string name, int max = 40)
    {
        var s = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (s.Length == 0) s = "item";
        if (char.IsDigit(s[0])) s = "n_" + s;
        return s.Length > max ? s[..max].TrimEnd('_') : s;
    }

    public static string UniqueKey(string baseKey, ICollection<string> taken)
    {
        var key = baseKey;
        for (var i = 2; taken.Contains(key); i++) key = $"{baseKey}_{i}";
        return key;
    }

    // ------------------------------------------------------------------ live → document

    public static AppDoc FromLive(AppDefinition app) => new()
    {
        Id = app.Id, Key = app.Key, Name = app.Name, Description = app.Description, Icon = app.Icon, Color = app.Color, Suite = app.Suite,
        Order = app.Order, DefaultAccess = app.DefaultAccess, HideMyAssignments = app.HideMyAssignments, ProtectFromUpgrade = app.ProtectFromUpgrade,
        Disabled = app.Disabled, IsSystem = app.IsSystem, PackageVersion = app.PackageVersion,
        Forms = app.Forms.OrderBy(f => f.Order).ThenBy(f => f.Name).Select(FromLive).ToList(),
        Views = app.Views.OrderBy(v => v.Order).ThenBy(v => v.Name).Select(FromLive).ToList(),
    };

    public static FormDoc FromLive(FormDefinition f)
    {
        var sections = f.Sections.OrderBy(s => s.Order).ToList();
        var fields = f.Fields.OrderBy(x => x.Order).ToList();
        return new FormDoc
        {
            Id = f.Id, Key = f.Key, Name = f.Name, Description = f.Description, Icon = f.Icon, Disabled = f.Disabled, IsActivity = f.IsActivity,
            ParentForms = [.. f.ParentForms], IsNonWorkflow = f.IsNonWorkflow, HiddenFromNew = f.HiddenFromNew, NumberPrefix = f.NumberPrefix,
            NumberSuffix = f.NumberSuffix, NumberPadding = f.NumberPadding, NumberIncludesYear = f.NumberIncludesYear, NumberScope = f.NumberScope,
            TitleTemplate = f.TitleTemplate, RevisionControlled = f.RevisionControlled, RevisionFormat = f.RevisionFormat,
            Sections = sections.Select(s => new SectionDoc
            {
                Id = s.Id, Key = s.Key, Name = s.Name, Description = s.Description, Tab = s.Tab, Icon = s.Icon, IsSubform = s.IsSubform,
                RowNoun = s.RowNoun, MaxRows = s.MaxRows, Required = s.Required, Optional = s.Optional, Collapsed = s.Collapsed,
                Fields = fields.Where(x => x.SectionKey == s.Key).Select(FromLive).ToList(),
            }).ToList(),
            Detached = fields.Where(x => sections.All(s => s.Key != x.SectionKey)).Select(FromLive).ToList(),
            Workflows = f.Workflows.OrderBy(w => w.Id).Select(FromLive).ToList(),
        };
    }

    public static FieldDoc FromLive(FieldDefinition f) => new()
    {
        Id = f.Id, Key = f.Key, Label = f.Label, Type = f.Type, Width = f.Width, Required = f.Required, RequiredInPhases = [.. f.RequiredInPhases],
        PreventNull = f.PreventNull, Unique = f.Unique, Sensitive = f.Sensitive, ReadOnly = f.ReadOnly, ProtectFromAdminUpdate = f.ProtectFromAdminUpdate,
        KeywordList = f.KeywordList, LinkForms = [.. f.LinkForms], Help = f.Help, Placeholder = f.Placeholder, DefaultValue = f.DefaultValue,
        Min = f.Min, Max = f.Max, Decimals = f.Decimals, MaxLength = f.MaxLength, CurrencyCode = f.CurrencyCode, ShowInHeader = f.ShowInHeader,
        VisibleWhen = f.VisibleWhen,
    };

    public static WorkflowDoc FromLive(WorkflowDefinition w)
    {
        var phases = w.Phases.OrderBy(p => p.Order).ToList();
        var byId = phases.ToDictionary(p => p.Id);
        return new WorkflowDoc
        {
            Id = w.Id, Key = w.Key, Name = w.Name, Disabled = w.Disabled,
            Phases = phases.Select(p => new PhaseDoc
            {
                Id = p.Id, Key = p.Key, Name = p.Name, Purpose = p.Purpose, Type = p.Type, IsStart = p.IsStart,
                EditableSections = [.. p.EditableSections], ReadOnlySections = [.. p.ReadOnlySections], HiddenSections = [.. p.HiddenSections],
                Assignment = p.Assignment, AssigneeField = p.AssigneeField, AssigneeGroup = p.AssigneeGroup, AssignAllMembers = p.AssignAllMembers,
                Routing = p.Routing, QuorumPercent = p.QuorumPercent, DueDays = p.DueDays, DueDateField = p.DueDateField, BusinessDays = p.BusinessDays,
                AssignerCanChangeDue = p.AssignerCanChangeDue, MustCreateForms = [.. p.MustCreateForms], MustCompleteForms = [.. p.MustCompleteForms],
                ActivityForms = [.. p.ActivityForms], Color = p.Color,
            }).ToList(),
            Routes = w.Routes.OrderBy(r => r.Kind).ThenBy(r => r.Order).Select(r => new RouteDoc
            {
                Id = r.Id,
                From = byId.TryGetValue(r.FromPhaseId, out var from) ? from.Key : "",
                To = byId.TryGetValue(r.ToPhaseId, out var to) ? to.Key : "",
                Kind = r.Kind, Label = r.Label, RequireComment = r.RequireComment, RequireSignature = !string.IsNullOrWhiteSpace(r.SignatureMeaning),
                SignatureMeaning = r.SignatureMeaning, Condition = r.Condition, AllowedPrincipals = [.. r.AllowedPrincipals],
            }).ToList(),
        };
    }

    public static ViewDoc FromLive(ViewDefinition v) => new()
    {
        Id = v.Id, Key = v.Key, Name = v.Name, Category = v.Category, Forms = [.. v.Forms], Columns = [.. v.Columns], Status = v.Status,
        Phases = [.. v.Phases], AssignedToMe = v.AssignedToMe, CreatedByMe = v.CreatedByMe, OverdueOnly = v.OverdueOnly,
        Filters = string.IsNullOrEmpty(v.FilterJson) ? [] : JsonSerializer.Deserialize<List<ViewFilter>>(v.FilterJson) ?? [],
        SortBy = v.SortBy, SortDesc = v.SortDesc, PageSize = v.PageSize, IsDefault = v.IsDefault, Hidden = v.Hidden, CalendarField = v.CalendarField,
    };

    /// <summary>A detached view definition for running a draft view against live data (preview).</summary>
    public static ViewDefinition ToDefinition(ViewDoc v, int appId) => new()
    {
        AppId = appId, Key = v.Key, Name = v.Name, Category = v.Category, Forms = [.. v.Forms], Columns = [.. v.Columns], Status = v.Status,
        Phases = [.. v.Phases], AssignedToMe = v.AssignedToMe, CreatedByMe = v.CreatedByMe, OverdueOnly = v.OverdueOnly,
        FilterJson = v.Filters.Count == 0 ? null : JsonSerializer.Serialize(v.Filters), SortBy = v.SortBy, SortDesc = v.SortDesc,
        PageSize = v.PageSize, IsDefault = v.IsDefault, Hidden = v.Hidden, CalendarField = v.CalendarField,
    };

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Match draft objects to live rows. Objects without an ID take the ID of a live object with the same design name that the
    /// draft no longer claims (deleting and re-adding "x" restores "x" instead of breaking the unique index); IDs that don't
    /// belong to this application's live configuration are dropped so they're treated as new.
    /// </summary>
    public static void Reconcile(AppDoc doc, AppDoc? live)
    {
        if (live == null)
        {
            doc.Id = null;
            foreach (var f in doc.Forms) ClearIds(f);
            foreach (var v in doc.Views) v.Id = null;
            return;
        }
        doc.Id = live.Id;
        Match(doc.Forms, live.Forms, x => x.Key, x => x.Id, (x, id) => x.Id = id);
        foreach (var f in doc.Forms)
        {
            var lf = live.Forms.FirstOrDefault(x => x.Id == f.Id);
            if (lf == null) { ClearIds(f); continue; }
            Match(f.Sections, lf.Sections, x => x.Key, x => x.Id, (x, id) => x.Id = id);
            Match(f.AllFields.ToList(), lf.AllFields.ToList(), x => x.Key, x => x.Id, (x, id) => x.Id = id);
            Match(f.Workflows, lf.Workflows, x => x.Key, x => x.Id, (x, id) => x.Id = id);
            foreach (var w in f.Workflows)
            {
                var lw = lf.Workflows.FirstOrDefault(x => x.Id == w.Id);
                if (lw == null) { w.Id = null; foreach (var p in w.Phases) p.Id = null; foreach (var r in w.Routes) r.Id = null; continue; }
                Match(w.Phases, lw.Phases, x => x.Key, x => x.Id, (x, id) => x.Id = id);
                var liveRoutes = lw.Routes.Select(r => r.Id).ToHashSet();
                var seen = new HashSet<int>();
                foreach (var r in w.Routes)
                    if (r.Id is not { } rid || !liveRoutes.Contains(rid) || !seen.Add(rid)) r.Id = null;
            }
        }
        Match(doc.Views, live.Views, x => x.Key, x => x.Id, (x, id) => x.Id = id);
    }

    private static void ClearIds(FormDoc f)
    {
        f.Id = null;
        foreach (var s in f.Sections) s.Id = null;
        foreach (var x in f.AllFields) x.Id = null;
        foreach (var w in f.Workflows)
        {
            w.Id = null;
            foreach (var p in w.Phases) p.Id = null;
            foreach (var r in w.Routes) r.Id = null;
        }
    }

    private static void Match<T>(IReadOnlyList<T> draft, IReadOnlyList<T> live, Func<T, string> key, Func<T, int?> id, Action<T, int?> setId)
    {
        var liveIds = live.Where(x => id(x) != null).ToDictionary(x => id(x)!.Value);
        var claimed = new HashSet<int>();
        foreach (var d in draft)
            if (id(d) is { } i && liveIds.ContainsKey(i) && claimed.Add(i)) { }
            else setId(d, null);
        foreach (var d in draft.Where(d => id(d) == null))
        {
            var match = live.FirstOrDefault(l => id(l) is { } li && !claimed.Contains(li) && string.Equals(key(l), key(d), StringComparison.OrdinalIgnoreCase));
            if (match != null) { setId(d, id(match)); claimed.Add(id(match)!.Value); }
        }
    }

    /// <summary>Field keys referenced by a condition such as <c>a=x &amp;&amp; !b || c!=y</c>.</summary>
    public static IEnumerable<string> ConditionFields(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) yield break;
        foreach (var any in condition.Split("||"))
            foreach (var clause in any.Split("&&"))
            {
                var c = clause.Trim();
                if (c.Length == 0) continue;
                var key = c.Split(c.Contains("!=") ? "!=" : "=", 2)[0].Trim().TrimStart('!');
                if (key.Length > 0) yield return key;
            }
    }

    /// <summary>{placeholders} of a title template.</summary>
    public static IEnumerable<string> TemplateFields(string? template) =>
        string.IsNullOrEmpty(template) ? [] : Regex.Matches(template, "\\{([^{}]+)\\}").Select(m => m.Groups[1].Value.Trim());

    public static string TypeLabel(FieldType t) => t switch
    {
        FieldType.TextArea => "Text area",
        FieldType.KeywordMulti => "Keywords (multi)",
        FieldType.YesNo => "Yes / No",
        FieldType.UserMulti => "Users (multi)",
        FieldType.DateTime => "Date & time",
        FieldType.Url => "External link",
        FieldType.Link => "Record link",
        FieldType.Prompt => "Text prompt",
        _ => t.ToString(),
    };

    public static string TypeIcon(FieldType t) => t switch
    {
        FieldType.Text => "bi-input-cursor-text",
        FieldType.TextArea => "bi-text-paragraph",
        FieldType.Number => "bi-123",
        FieldType.Currency => "bi-currency-dollar",
        FieldType.Date => "bi-calendar3",
        FieldType.DateTime => "bi-calendar-event",
        FieldType.Keyword => "bi-menu-button-wide",
        FieldType.KeywordMulti => "bi-ui-checks",
        FieldType.YesNo => "bi-toggle-on",
        FieldType.User => "bi-person",
        FieldType.UserMulti => "bi-people",
        FieldType.Link => "bi-link-45deg",
        FieldType.Url => "bi-box-arrow-up-right",
        FieldType.Attachment => "bi-paperclip",
        FieldType.Prompt => "bi-info-square",
        _ => "bi-square",
    };

    public static int DefaultWidth(FieldType t) => t switch
    {
        FieldType.TextArea or FieldType.Attachment or FieldType.Prompt => 12,
        FieldType.Number or FieldType.Currency or FieldType.Date => 3,
        FieldType.Keyword or FieldType.YesNo or FieldType.User or FieldType.DateTime => 4,
        _ => 6,
    };
}
