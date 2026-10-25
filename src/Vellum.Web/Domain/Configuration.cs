namespace Vellum.Domain;

// The configuration metamodel (DSN §3). Every packaged application is built only from these objects.

public class AppDefinition
{
    public int Id { get; set; }
    /// <summary>Design name (DSN-007). Unique, never all digits.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Bootstrap Icons class, e.g. <c>bi-shield-check</c>.</summary>
    public string Icon { get; set; } = "bi-grid";
    /// <summary>Accent color (hex) used for the app tile and record chrome.</summary>
    public string Color { get; set; } = "#e11d48";
    /// <summary>Navigation group: Documents, Quality, Supplier, Risk, EHS, Operations, Administration.</summary>
    public string Suite { get; set; } = "Quality";
    public int Order { get; set; }
    /// <summary>SEC-505: default access for every authenticated user.</summary>
    public AppRole DefaultAccess { get; set; } = AppRole.None;
    public bool IsSystem { get; set; }
    public bool Disabled { get; set; }
    public bool HideMyAssignments { get; set; }
    /// <summary>DSN-901: packaged template version, e.g. "2.0.0".</summary>
    public string? PackageVersion { get; set; }
    /// <summary>DSN-903.</summary>
    public bool ProtectFromUpgrade { get; set; }
    /// <summary>PLT-206: users who may hard-delete records (still subject to INV-010).</summary>
    public List<string> DeleterUsernames { get; set; } = [];

    public List<FormDefinition> Forms { get; set; } = [];
    public List<ViewDefinition> Views { get; set; } = [];
    public List<AppRoleGrant> Grants { get; set; } = [];
}

public class FormDefinition
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public AppDefinition App { get; set; } = null!;
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Icon { get; set; } = "bi-file-earmark-text";
    public int Order { get; set; }
    public bool Disabled { get; set; }
    /// <summary>PLT-205: activity forms are created from a parent record.</summary>
    public bool IsActivity { get; set; }
    /// <summary>Form keys (any application) that may parent this activity. Empty = any form in the same application.</summary>
    public List<string> ParentForms { get; set; } = [];
    /// <summary>PLT-203: profiles and master data. No phases; enabled/disabled instead.</summary>
    public bool IsNonWorkflow { get; set; }
    public bool HiddenFromNew { get; set; }

    // Numbering (PLT-202, DOC-010)
    public string NumberPrefix { get; set; } = "";
    public string NumberSuffix { get; set; } = "";
    public int NumberPadding { get; set; } = 5;
    public bool NumberIncludesYear { get; set; }
    /// <summary>Sequence scope. Forms that share a scope share one counter. Defaults to the form key.</summary>
    public string? NumberScope { get; set; }

    /// <summary>Title template, e.g. <c>{title}</c> or <c>{part_number} – {subject}</c>.</summary>
    public string TitleTemplate { get; set; } = "{title}";

    // Revision control (DOC §10.2)
    public bool RevisionControlled { get; set; }
    public RevisionFormat RevisionFormat { get; set; } = RevisionFormat.None;

    public List<SectionDefinition> Sections { get; set; } = [];
    public List<FieldDefinition> Fields { get; set; } = [];
    public List<WorkflowDefinition> Workflows { get; set; } = [];
}

public class SectionDefinition
{
    public int Id { get; set; }
    public int FormId { get; set; }
    public FormDefinition Form { get; set; } = null!;
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Tab the section lives on. Sections with the same tab are grouped.</summary>
    public string Tab { get; set; } = "General";
    public int Order { get; set; }
    public string? Icon { get; set; }
    /// <summary>Repeating group of fields (DSN-306).</summary>
    public bool IsSubform { get; set; }
    public string? RowNoun { get; set; }
    public int? MaxRows { get; set; }
    /// <summary>PLT-504: at least one complete row before sending forward.</summary>
    public bool Required { get; set; }
    /// <summary>PLT-304: shown or hidden by the user.</summary>
    public bool Optional { get; set; }
    public bool Collapsed { get; set; }
}

public class FieldDefinition
{
    public int Id { get; set; }
    public int FormId { get; set; }
    public FormDefinition Form { get; set; } = null!;
    public string SectionKey { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }
    public int Order { get; set; }
    /// <summary>Grid width, 1–12 (DSN-301).</summary>
    public int Width { get; set; } = 6;
    /// <summary>Required on send-forward from any phase where the field is editable (PLT-501).</summary>
    public bool Required { get; set; }
    /// <summary>DSN-410: phases (keys) in which the field is required when sending forward.</summary>
    public List<string> RequiredInPhases { get; set; } = [];
    /// <summary>PLT-503: enforced on every save path.</summary>
    public bool PreventNull { get; set; }
    /// <summary>PLT-410.</summary>
    public bool Unique { get; set; }
    /// <summary>SEC-520: readable only by managers and the field's sensitive readers.</summary>
    public bool Sensitive { get; set; }
    /// <summary>Computed / system-maintained. Never editable in the UI.</summary>
    public bool ReadOnly { get; set; }
    /// <summary>PLT-408.</summary>
    public bool ProtectFromAdminUpdate { get; set; }
    public string? KeywordList { get; set; }
    /// <summary>Allowed target form keys for link fields.</summary>
    public List<string> LinkForms { get; set; } = [];
    public string? Help { get; set; }
    public string? Placeholder { get; set; }
    /// <summary>Default rule: a literal, a keyword key, or one of <c>$me</c>, <c>$today</c>, <c>$today+N</c>.</summary>
    public string? DefaultValue { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int? Decimals { get; set; }
    public int? MaxLength { get; set; }
    public string? CurrencyCode { get; set; }
    /// <summary>Show this field in the record header summary strip.</summary>
    public bool ShowInHeader { get; set; }
    /// <summary>Simple visibility rule: show only when another field has a value (<c>field=value</c>, <c>field!=value</c>, <c>field</c> = has value).</summary>
    public string? VisibleWhen { get; set; }
}

public class WorkflowDefinition
{
    public int Id { get; set; }
    public int FormId { get; set; }
    public FormDefinition Form { get; set; } = null!;
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Disabled { get; set; }
    public List<PhaseDefinition> Phases { get; set; } = [];
    public List<RouteDefinition> Routes { get; set; } = [];
}

public class PhaseDefinition
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public WorkflowDefinition Workflow { get; set; } = null!;
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Purpose { get; set; }
    public PhaseType Type { get; set; }
    public bool IsStart { get; set; }
    public int Order { get; set; }

    // Visibility matrix (DSN-305). Empty lists = defaults (everything editable in Normal phases).
    public List<string> EditableSections { get; set; } = [];
    public List<string> ReadOnlySections { get; set; } = [];
    public List<string> HiddenSections { get; set; } = [];

    // Assignment (PLT-620)
    public AssignmentMode Assignment { get; set; } = AssignmentMode.Any;
    public string? AssigneeField { get; set; }
    public string? AssigneeGroup { get; set; }
    /// <summary>Group mode: assign every active member instead of picking.</summary>
    public bool AssignAllMembers { get; set; }
    public RoutingType Routing { get; set; } = RoutingType.Parallel;
    /// <summary>Voting quorum as a percentage (0–100) of assignees (PLT-612).</summary>
    public int QuorumPercent { get; set; } = 100;

    // Due dates (PLT-630)
    public int? DueDays { get; set; }
    public string? DueDateField { get; set; }
    public bool BusinessDays { get; set; } = true;
    public bool AssignerCanChangeDue { get; set; } = true;

    // Activity gates (PLT-640)
    public List<string> MustCreateForms { get; set; } = [];
    public List<string> MustCompleteForms { get; set; } = [];
    /// <summary>Activity forms that can be created in this phase. Empty = every allowed activity form.</summary>
    public List<string> ActivityForms { get; set; } = [];

    /// <summary>Hex or Bootstrap variant name used for the phase chip.</summary>
    public string? Color { get; set; }
}

public class RouteDefinition
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public WorkflowDefinition Workflow { get; set; } = null!;
    public int FromPhaseId { get; set; }
    public PhaseDefinition FromPhase { get; set; } = null!;
    public int ToPhaseId { get; set; }
    public PhaseDefinition ToPhase { get; set; } = null!;
    public RouteKind Kind { get; set; }
    public string? Label { get; set; }
    public int Order { get; set; }
    public bool RequireComment { get; set; }
    /// <summary>When set, the route needs an electronic signature with this meaning (SEC-601, DSN-606).</summary>
    public string? SignatureMeaning { get; set; }
    /// <summary>Simple condition on record data: <c>field=value</c>, <c>field!=value</c>, or <c>field</c> (has a value). Value is a keyword key, yes/no, or literal.</summary>
    public string? Condition { get; set; }
    /// <summary>Usernames/groups allowed to use a special-forward route (empty = administrators only).</summary>
    public List<string> AllowedPrincipals { get; set; } = [];
}

public class ViewDefinition
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public AppDefinition App { get; set; } = null!;
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Navigator category; numeric prefixes set the order (PLT-1101).</summary>
    public string Category { get; set; } = "Records";
    public int Order { get; set; }
    /// <summary>Form keys shown. Empty = every non-activity form of the app.</summary>
    public List<string> Forms { get; set; } = [];
    /// <summary>Columns: system keys (number, title, phase, due, assignees, created, modified, location, revision, effective, form, creator) or field keys.</summary>
    public List<string> Columns { get; set; } = [];
    /// <summary>open | completed | voided | closed | all | effective | archived</summary>
    public string Status { get; set; } = "open";
    public List<string> Phases { get; set; } = [];
    public bool AssignedToMe { get; set; }
    public bool CreatedByMe { get; set; }
    public bool OverdueOnly { get; set; }
    /// <summary>JSON array of <c>{ "field": "...", "op": "eq|ne|set|empty", "value": "..." }</c>.</summary>
    public string? FilterJson { get; set; }
    public string SortBy { get; set; } = "number";
    public bool SortDesc { get; set; } = true;
    public int PageSize { get; set; } = 25;
    public bool IsDefault { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Calendar views (PLT-1106) place records by this date column.</summary>
    public string? CalendarField { get; set; }
}

public class KeywordList
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<Keyword> Keywords { get; set; } = [];
}

/// <summary>PLT-404 / INV-061: stored by stable ID. Labels can change without touching data.</summary>
public class Keyword
{
    public int Id { get; set; }
    public int ListId { get; set; }
    public KeywordList List { get; set; } = null!;
    /// <summary>Stable key used by configuration and behaviors.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Bootstrap variant (success, warning, danger, info, secondary, primary) or hex color.</summary>
    public string? Color { get; set; }
    public int Order { get; set; }
    public bool Disabled { get; set; }
    public int? ParentId { get; set; }
}

/// <summary>DSN-1203: each published configuration is an immutable, numbered version.</summary>
public class ConfigVersion
{
    public int Id { get; set; }
    public int Number { get; set; }
    public DateTime PublishedAt { get; set; }
    public int? PublishedById { get; set; }
    public string? Note { get; set; }
}

/// <summary>DSN-1202 / INV-145: append-only configuration audit trail.</summary>
public class ConfigAuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public int ConfigVersion { get; set; }
    public string? Reason { get; set; }
}
