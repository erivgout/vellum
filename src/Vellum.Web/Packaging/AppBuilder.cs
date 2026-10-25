using Vellum.Domain;

namespace Vellum.Packaging;

/// <summary>
/// A packaged application template (DSN-901). Implementations are discovered automatically and installed at startup
/// (in <see cref="Order"/>) when their app key doesn't exist yet. Demo data is seeded once, after every app is installed.
/// </summary>
public interface IPackagedApp
{
    string Key { get; }
    /// <summary>Install order. Apps that others link to (e.g. Action Items, Document Control) install first.</summary>
    int Order => 100;
    void Define(AppBuilder app);
    Task SeedDemoAsync(DemoContext demo) => Task.CompletedTask;
}

/// <summary>Fluent definition of an application: keyword lists, forms, sections, fields, workflows, views and grants.</summary>
public sealed class AppBuilder(string key)
{
    internal AppDefinition App { get; } = new() { Key = key, Name = key };
    internal List<FormBuilder> Forms { get; } = [];
    internal List<ViewBuilder> Views { get; } = [];
    internal List<KeywordListBuilder> Lists { get; } = [];
    internal List<(string Principal, AppRole Role, bool Designer, string? Form)> Grants { get; } = [];

    public AppBuilder Name(string name) { App.Name = name; return this; }
    public AppBuilder Description(string d) { App.Description = d; return this; }
    public AppBuilder Icon(string bi) { App.Icon = bi; return this; }
    public AppBuilder Color(string hex) { App.Color = hex; return this; }
    /// <summary>Documents, Quality, Supplier, Risk, EHS, Operations.</summary>
    public AppBuilder Suite(string suite) { App.Suite = suite; return this; }
    public AppBuilder Version(string v) { App.PackageVersion = v; return this; }
    public AppBuilder Order(int order) { App.Order = order; return this; }
    public AppBuilder DefaultAccess(AppRole role) { App.DefaultAccess = role; return this; }
    public AppBuilder HideMyAssignments() { App.HideMyAssignments = true; return this; }

    /// <summary>Grant a role to a user (username) or group (<c>@group-key</c>).</summary>
    public AppBuilder Grant(string principal, AppRole role, bool designer = false, string? formKey = null)
    {
        Grants.Add((principal, role, designer, formKey));
        return this;
    }

    /// <summary>Define (or extend) a keyword list. Lists are global and shared by key across apps.</summary>
    public AppBuilder Keywords(string listKey, string name, Action<KeywordListBuilder> build)
    {
        var b = new KeywordListBuilder(listKey, name);
        build(b);
        Lists.Add(b);
        return this;
    }

    public AppBuilder Form(string key, string name, Action<FormBuilder> build)
    {
        var f = new FormBuilder(key, name, Forms.Count);
        build(f);
        Forms.Add(f);
        return this;
    }

    public AppBuilder View(string key, string name, Action<ViewBuilder> build)
    {
        var v = new ViewBuilder(key, name, Views.Count);
        build(v);
        Views.Add(v);
        return this;
    }
}

public sealed class KeywordListBuilder(string key, string name)
{
    internal string Key { get; } = key;
    internal string Name { get; } = name;
    internal List<Keyword> Items { get; } = [];

    /// <param name="color">Bootstrap variant (success, warning, danger, info, secondary, primary, dark) or hex.</param>
    public KeywordListBuilder Add(string key, string label, string? color = null, string? parent = null)
    {
        Items.Add(new Keyword { Key = key, Label = label, Color = color, Order = Items.Count, Disabled = false });
        if (parent != null) ParentOf[key] = parent;
        return this;
    }

    internal Dictionary<string, string> ParentOf { get; } = [];
}

public sealed class FormBuilder(string key, string name, int order)
{
    internal FormDefinition Form { get; } = new() { Key = key, Name = name, Order = order };
    internal List<SectionBuilder> Sections { get; } = [];
    internal List<WorkflowBuilder> Workflows { get; } = [];

    public FormBuilder Icon(string bi) { Form.Icon = bi; return this; }
    public FormBuilder Description(string d) { Form.Description = d; return this; }
    /// <summary>Number format: prefix + [year-] + zero-padded sequence + suffix (PLT-202).</summary>
    public FormBuilder Numbering(string prefix, int padding = 5, bool includeYear = false, string suffix = "", string? scope = null)
    {
        Form.NumberPrefix = prefix;
        Form.NumberPadding = padding;
        Form.NumberIncludesYear = includeYear;
        Form.NumberSuffix = suffix;
        Form.NumberScope = scope;
        return this;
    }
    /// <summary>Title template with {field} placeholders, e.g. "{subject}".</summary>
    public FormBuilder Title(string template) { Form.TitleTemplate = template; return this; }
    /// <summary>Activity form, created from parent records of the given forms (any form in this app when empty).</summary>
    public FormBuilder Activity(params string[] parentForms) { Form.IsActivity = true; Form.ParentForms = [.. parentForms]; return this; }
    public FormBuilder NonWorkflow() { Form.IsNonWorkflow = true; return this; }
    public FormBuilder HiddenFromNew() { Form.HiddenFromNew = true; return this; }
    public FormBuilder Revisions(RevisionFormat format) { Form.RevisionControlled = true; Form.RevisionFormat = format; return this; }

    public FormBuilder Section(string key, string name, Action<SectionBuilder> build, string tab = "General", string? icon = null,
        bool optional = false, string? description = null, bool collapsed = false)
    {
        var s = new SectionBuilder(new SectionDefinition
        {
            Key = key, Name = name, Tab = tab, Icon = icon, Optional = optional, Description = description, Collapsed = collapsed, Order = Sections.Count,
        });
        build(s);
        Sections.Add(s);
        return this;
    }

    /// <summary>Repeating group of fields (DSN-306).</summary>
    public FormBuilder Subform(string key, string name, Action<SectionBuilder> build, string tab = "General", string? rowNoun = null,
        bool required = false, int? maxRows = null, string? icon = null, string? description = null)
    {
        var s = new SectionBuilder(new SectionDefinition
        {
            Key = key, Name = name, Tab = tab, IsSubform = true, RowNoun = rowNoun, Required = required, MaxRows = maxRows,
            Icon = icon, Description = description, Order = Sections.Count,
        });
        build(s);
        Sections.Add(s);
        return this;
    }

    public FormBuilder Workflow(string key, string name, Action<WorkflowBuilder> build)
    {
        var w = new WorkflowBuilder(key, name);
        build(w);
        Workflows.Add(w);
        return this;
    }
}

public sealed class SectionBuilder(SectionDefinition section)
{
    internal SectionDefinition Section { get; } = section;
    internal List<FieldDefinition> Fields { get; } = [];

    private FieldBuilder Add(string key, string label, FieldType type, int width)
    {
        var f = new FieldDefinition { Key = key, Label = label, Type = type, Width = width, SectionKey = Section.Key };
        Fields.Add(f);
        return new FieldBuilder(f);
    }

    public FieldBuilder Text(string key, string label, int width = 6) => Add(key, label, FieldType.Text, width);
    public FieldBuilder TextArea(string key, string label, int width = 12) => Add(key, label, FieldType.TextArea, width);
    public FieldBuilder Number(string key, string label, int width = 3) => Add(key, label, FieldType.Number, width);
    public FieldBuilder Currency(string key, string label, string code = "USD", int width = 3) { var b = Add(key, label, FieldType.Currency, width); b.Field.CurrencyCode = code; b.Field.Decimals = 2; return b; }
    public FieldBuilder Date(string key, string label, int width = 3) => Add(key, label, FieldType.Date, width);
    public FieldBuilder DateTime(string key, string label, int width = 4) => Add(key, label, FieldType.DateTime, width);
    public FieldBuilder Keyword(string key, string label, string list, int width = 4) { var b = Add(key, label, FieldType.Keyword, width); b.Field.KeywordList = list; return b; }
    public FieldBuilder Keywords(string key, string label, string list, int width = 6) { var b = Add(key, label, FieldType.KeywordMulti, width); b.Field.KeywordList = list; return b; }
    public FieldBuilder YesNo(string key, string label, int width = 4) => Add(key, label, FieldType.YesNo, width);
    public FieldBuilder User(string key, string label, int width = 4) => Add(key, label, FieldType.User, width);
    public FieldBuilder Users(string key, string label, int width = 6) => Add(key, label, FieldType.UserMulti, width);
    /// <summary>Link to records of the given form keys (empty = any form).</summary>
    public FieldBuilder Link(string key, string label, params string[] forms) { var b = Add(key, label, FieldType.Link, 6); b.Field.LinkForms = [.. forms]; return b; }
    public FieldBuilder Url(string key, string label, int width = 6) => Add(key, label, FieldType.Url, width);
    public FieldBuilder Attachment(string key, string label, int width = 12) => Add(key, label, FieldType.Attachment, width);
    /// <summary>Static guidance text in the layout.</summary>
    public FieldBuilder Prompt(string key, string text) { var b = Add(key, "", FieldType.Prompt, 12); b.Field.Help = text; return b; }
}

public sealed class FieldBuilder(FieldDefinition field)
{
    internal FieldDefinition Field { get; } = field;

    public FieldBuilder Required() { Field.Required = true; return this; }
    /// <summary>Required when sending forward from these phases (DSN-410).</summary>
    public FieldBuilder RequiredIn(params string[] phases) { Field.RequiredInPhases.AddRange(phases); return this; }
    public FieldBuilder PreventNull() { Field.PreventNull = true; return this; }
    public FieldBuilder Unique() { Field.Unique = true; return this; }
    public FieldBuilder Sensitive() { Field.Sensitive = true; return this; }
    /// <summary>Computed / system-maintained: never editable in the UI.</summary>
    public FieldBuilder ReadOnly() { Field.ReadOnly = true; return this; }
    public FieldBuilder Protected() { Field.ProtectFromAdminUpdate = true; return this; }
    public FieldBuilder Help(string text) { Field.Help = text; return this; }
    public FieldBuilder Placeholder(string text) { Field.Placeholder = text; return this; }
    /// <summary>Literal, keyword key, yes/no, <c>$me</c>, <c>$today</c> or <c>$today+N</c>.</summary>
    public FieldBuilder Default(string value) { Field.DefaultValue = value; return this; }
    public FieldBuilder Min(decimal v) { Field.Min = v; return this; }
    public FieldBuilder Max(decimal v) { Field.Max = v; return this; }
    public FieldBuilder Decimals(int n) { Field.Decimals = n; return this; }
    public FieldBuilder MaxLength(int n) { Field.MaxLength = n; return this; }
    public FieldBuilder Width(int cols) { Field.Width = cols; return this; }
    /// <summary>Show in the record header summary strip.</summary>
    public FieldBuilder Header() { Field.ShowInHeader = true; return this; }
    /// <summary>Only shown when the condition holds, e.g. <c>supplier_fault=yes</c>.</summary>
    public FieldBuilder VisibleWhen(string condition) { Field.VisibleWhen = condition; return this; }
}

public sealed class WorkflowBuilder(string key, string name)
{
    internal WorkflowDefinition Workflow { get; } = new() { Key = key, Name = name };
    internal List<PhaseBuilder> Phases { get; } = [];
    internal List<(string From, string To, RouteKind Kind, string? Sign, string? Label, string? When, bool Comment, string[] Allowed)> Routes { get; } = [];

    public PhaseBuilder Phase(string key, string name, PhaseType type = PhaseType.Normal)
    {
        var p = new PhaseBuilder(new PhaseDefinition { Key = key, Name = name, Type = type, Order = Phases.Count });
        Phases.Add(p);
        return p;
    }

    /// <summary>A starting phase (PLT-602). Defaults to assigning the creator.</summary>
    public PhaseBuilder Start(string key, string name) => Phase(key, name).StartPhase().AssignCreator();
    public PhaseBuilder Completed(string key, string name) => Phase(key, name, PhaseType.Completed);
    public PhaseBuilder Voided(string key = "voided", string name = "Voided") => Phase(key, name, PhaseType.Voided);
    public PhaseBuilder Obsolete(string key = "obsolete", string name = "Obsolete") => Phase(key, name, PhaseType.Obsolete);
    public PhaseBuilder Archived(string key = "archived", string name = "Archived") => Phase(key, name, PhaseType.Archived);

    /// <param name="sign">E-signature meaning required to take this route (e.g. "Approved").</param>
    /// <param name="when">Condition on record data, e.g. <c>minor_change=yes</c>.</param>
    public WorkflowBuilder Forward(string from, string to, string? sign = null, string? label = null, string? when = null, bool comment = false)
    { Routes.Add((from, to, RouteKind.Forward, sign, label, when, comment, [])); return this; }

    public WorkflowBuilder Back(string from, string to, string? sign = null, string? label = null)
    { Routes.Add((from, to, RouteKind.Back, sign, label, null, true, [])); return this; }

    public WorkflowBuilder Void(string from, string to = "voided", string? sign = null, string? label = null)
    { Routes.Add((from, to, RouteKind.Void, sign, label, null, true, [])); return this; }

    /// <summary>Special-forward route for the given usernames / @groups (administrators always).</summary>
    public WorkflowBuilder Special(string from, string to, string? label = null, string? sign = null, params string[] allowed)
    { Routes.Add((from, to, RouteKind.SpecialForward, sign, label, null, true, allowed)); return this; }
}

public sealed class PhaseBuilder(PhaseDefinition phase)
{
    internal PhaseDefinition Phase { get; } = phase;

    public PhaseBuilder StartPhase() { Phase.IsStart = true; return this; }
    public PhaseBuilder Purpose(string text) { Phase.Purpose = text; return this; }
    public PhaseBuilder AssignCreator() { Phase.Assignment = AssignmentMode.Creator; return this; }
    /// <summary>Assign from a names field. <paramref name="forced"/> = no picking; otherwise the field only preselects.</summary>
    public PhaseBuilder AssignField(string fieldKey, bool forced = true)
    { Phase.Assignment = forced ? AssignmentMode.Field : AssignmentMode.Any; Phase.AssigneeField = fieldKey; return this; }
    public PhaseBuilder AssignAny() { Phase.Assignment = AssignmentMode.Any; Phase.AssigneeField = null; return this; }
    /// <summary>Assign members of a group: all of them, or a pick among them.</summary>
    public PhaseBuilder AssignGroup(string groupKey, bool all = false)
    { Phase.Assignment = AssignmentMode.Group; Phase.AssigneeGroup = groupKey; Phase.AssignAllMembers = all; return this; }
    public PhaseBuilder Parallel() { Phase.Routing = RoutingType.Parallel; return this; }
    public PhaseBuilder Sequential() { Phase.Routing = RoutingType.Sequential; return this; }
    public PhaseBuilder Voting(int quorumPercent = 100) { Phase.Routing = RoutingType.Voting; Phase.QuorumPercent = quorumPercent; return this; }
    public PhaseBuilder DueDays(int days, bool businessDays = true) { Phase.DueDays = days; Phase.BusinessDays = businessDays; return this; }
    public PhaseBuilder DueFrom(string dateField) { Phase.DueDateField = dateField; return this; }
    public PhaseBuilder FixedDue() { Phase.AssignerCanChangeDue = false; return this; }
    /// <summary>Sections editable in this phase ("*" = all). Sections not listed anywhere are hidden (DSN-305).</summary>
    public PhaseBuilder Edit(params string[] sections) { Phase.EditableSections.AddRange(sections); return this; }
    public PhaseBuilder ReadOnly(params string[] sections) { Phase.ReadOnlySections.AddRange(sections); return this; }
    /// <summary>Every section not listed as editable is shown read-only instead of hidden.</summary>
    public PhaseBuilder ReadOnlyRest() { Phase.ReadOnlySections.Add("*"); return this; }
    public PhaseBuilder Hide(params string[] sections) { Phase.HiddenSections.AddRange(sections); return this; }
    public PhaseBuilder MustCreate(params string[] forms) { Phase.MustCreateForms.AddRange(forms); return this; }
    /// <summary>Activities of these forms must be completed or voided before leaving the phase (INV-075).</summary>
    public PhaseBuilder MustComplete(params string[] forms) { Phase.MustCompleteForms.AddRange(forms); return this; }
    /// <summary>Activity forms that may be created in this phase (empty = all).</summary>
    public PhaseBuilder Activities(params string[] forms) { Phase.ActivityForms.AddRange(forms); return this; }
    public PhaseBuilder Color(string color) { Phase.Color = color; return this; }
}

public sealed class ViewBuilder(string key, string name, int order)
{
    internal ViewDefinition View { get; } = new() { Key = key, Name = name, Order = order };

    public ViewBuilder Category(string c) { View.Category = c; return this; }
    public ViewBuilder Forms(params string[] forms) { View.Forms = [.. forms]; return this; }
    /// <summary>System columns: number, title, phase, due, assignees, created, modified, creator, location, revision, effective, form, status. Or field keys.</summary>
    public ViewBuilder Columns(params string[] cols) { View.Columns = [.. cols]; return this; }
    public ViewBuilder Open() { View.Status = "open"; return this; }
    public ViewBuilder Completed() { View.Status = "completed"; return this; }
    public ViewBuilder Voided() { View.Status = "voided"; return this; }
    public ViewBuilder Closed() { View.Status = "closed"; return this; }
    public ViewBuilder All() { View.Status = "all"; return this; }
    public ViewBuilder Effective() { View.Status = "effective"; return this; }
    public ViewBuilder Archived() { View.Status = "archived"; return this; }
    public ViewBuilder Phases(params string[] phases) { View.Phases = [.. phases]; return this; }
    public ViewBuilder Mine() { View.AssignedToMe = true; return this; }
    public ViewBuilder CreatedByMe() { View.CreatedByMe = true; return this; }
    public ViewBuilder Overdue() { View.OverdueOnly = true; View.Status = "open"; return this; }
    /// <summary>Field filter: op = eq | ne | set | empty. Value: keyword key, yes/no, or text.</summary>
    public ViewBuilder Filter(string field, string op, string? value = null)
    {
        var list = string.IsNullOrEmpty(View.FilterJson) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<Engine.Views.ViewFilter>>(View.FilterJson)!;
        list.Add(new Engine.Views.ViewFilter { Field = field, Op = op, Value = value });
        View.FilterJson = System.Text.Json.JsonSerializer.Serialize(list);
        return this;
    }
    public ViewBuilder Sort(string by, bool desc = true) { View.SortBy = by; View.SortDesc = desc; return this; }
    public ViewBuilder PageSize(int n) { View.PageSize = n; return this; }
    public ViewBuilder Default() { View.IsDefault = true; return this; }
    public ViewBuilder Hidden() { View.Hidden = true; return this; }
    public ViewBuilder Calendar(string dateColumn) { View.CalendarField = dateColumn; return this; }
}
