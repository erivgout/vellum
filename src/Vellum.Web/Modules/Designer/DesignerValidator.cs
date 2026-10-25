using System.Text.RegularExpressions;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Engine.Views;

namespace Vellum.Modules.Designer;

public enum IssueSeverity { Error = 0, Warning = 1 }

/// <param name="Path">Object path: <c>app</c>, <c>form:capa</c>, <c>form:capa/field:subject</c>, <c>form:capa/wf:capa/phase:initiate</c>, <c>view:open</c>…</param>
public sealed record DesignIssue(IssueSeverity Severity, string Path, string Message, string? Rule = null)
{
    public string? Href { get; init; }
    public string Object { get; init; } = "";
    public bool IsError => Severity == IssueSeverity.Error;
}

/// <summary>Facts about live data the validator needs (records in phases, stored values, …).</summary>
public sealed class DesignContext
{
    public required ConfigModel Cfg { get; init; }
    public required UserDirectory Dir { get; init; }
    public AppDoc? Live { get; init; }
    public Dictionary<int, int> RecordsInPhase { get; init; } = [];
    public Dictionary<int, int> OpenRecordsInPhase { get; init; } = [];
    public Dictionary<int, int> PhaseHistory { get; init; } = [];
    public Dictionary<int, int> RecordsByForm { get; init; } = [];
    public Dictionary<int, int> RecordsByWorkflow { get; init; } = [];
    /// <summary>(form ID, field key) → number of records holding a value.</summary>
    public Dictionary<(int Form, string Field), int> FieldData { get; init; } = [];
    public bool LiveChangedSinceDraft { get; init; }
    public List<DesignerLock> OtherLocks { get; init; } = [];

    public int DataFor(int? formId, string fieldKey) =>
        formId is { } f && FieldData.TryGetValue((f, fieldKey.ToLowerInvariant()), out var n) ? n : 0;
}

/// <summary>
/// Publish-time configuration validation (DSN-1201). A superset of the core <c>Data.ConfigValidator</c> rules, with severities:
/// errors block publishing, warnings must be accepted (and the acceptance is recorded).
/// </summary>
public static class DesignerValidator
{
    private static readonly HashSet<string> SystemColumns = [.. ViewService.SystemColumns.Keys];
    private static readonly HashSet<string> SortableSystem = ["number", "title", "phase", "due", "created", "modified", "revision", "effective"];
    private static readonly Regex Hex = new("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

    public static List<DesignIssue> Validate(AppDoc doc, DesignContext ctx)
    {
        var v = new Run(doc, ctx);
        v.All();
        return v.Issues
            .DistinctBy(i => (i.Severity, i.Path, i.Message))
            .OrderBy(i => i.Severity).ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class Run(AppDoc doc, DesignContext ctx)
    {
        public List<DesignIssue> Issues { get; } = [];
        private readonly ConfigModel cfg = ctx.Cfg;
        private string AppHref => $"/designer/apps/{doc.Key}";

        private void Error(string path, string obj, string msg, string? rule = null, string? href = null) =>
            Issues.Add(new DesignIssue(IssueSeverity.Error, path, msg, rule) { Href = href, Object = obj });

        private void Warn(string path, string obj, string msg, string? rule = null, string? href = null) =>
            Issues.Add(new DesignIssue(IssueSeverity.Warning, path, msg, rule) { Href = href, Object = obj });

        private string FormHref(FormDoc f) => $"{AppHref}/forms/{f.Key}";
        private string WfHref(FormDoc f, WorkflowDoc w) => $"{AppHref}/forms/{f.Key}/workflows/{w.Key}";
        private string ViewHref(ViewDoc v) => $"{AppHref}/views/{v.Key}";

        /// <summary>Forms visible to references from this app: the draft's own forms plus every other application's live forms.</summary>
        private bool FormExists(string key) =>
            key == "*" || doc.Forms.Any(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
            (cfg.FormsByKey.TryGetValue(key, out var lf) && lf.AppId != doc.Id);

        private bool IsActivityForm(string key) =>
            doc.Forms.FirstOrDefault(f => f.Key == key)?.IsActivity ?? (cfg.FormsByKey.TryGetValue(key, out var lf) && lf.IsActivity);

        public void All()
        {
            App();
            var formKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.Forms)
            {
                Form(f);
                if (!formKeys.Add(f.Key)) Error($"form:{f.Key}", f.Name, $"Two forms use the design name '{f.Key}'.", "INV-130", FormHref(f));
            }
            var viewKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in doc.Views)
            {
                View(v);
                if (!viewKeys.Add(v.Key)) Error($"view:{v.Key}", v.Name, $"Two views use the design name '{v.Key}'.", "INV-130", ViewHref(v));
            }
            if (doc.Views.Count(v => v.IsDefault) > 1)
                Warn("app", doc.Name, $"{doc.Views.Count(v => v.IsDefault)} views are marked as the default view; the first one is used.", "DSN-101", $"{AppHref}");
            if (doc.Views.Count(v => !v.Hidden) == 0 && doc.Forms.Count > 0)
                Warn("app", doc.Name, "The application has no visible views, so users can't find its records.", "DSN-101", AppHref);
            Deletions();
            if (ctx.LiveChangedSinceDraft)
                Warn("app", doc.Name, "The live configuration of this application changed after this draft was started. Publishing replaces those changes with the draft.", "DSN-003", AppHref);
            foreach (var l in ctx.OtherLocks)
                Warn("app", doc.Name, $"{l.UserName} is editing {l.Label}. Changes they haven't saved yet won't be published.", "DSN-004", AppHref);
        }

        private void App()
        {
            var href = $"{AppHref}/settings";
            if (doc.IsSystem) Error("app", doc.Name, "System applications can't be changed in the designer.", "DSN-005", href);
            if (DesignDocs.KeyProblem(doc.Key) is { } kp) Error("app", doc.Name, $"Application design name {kp}.", "INV-130", href);
            if (cfg.AppsByKey.TryGetValue(doc.Key, out var other) && other.Id != doc.Id)
                Error("app", doc.Name, $"Another application already uses the design name '{doc.Key}'.", "INV-130", href);
            if (string.IsNullOrWhiteSpace(doc.Name)) Error("app", "Application", "The application needs a display name.", "DSN-101", href);
            if (string.IsNullOrWhiteSpace(doc.Icon) || !doc.Icon.StartsWith("bi-")) Warn("app", doc.Name, "Pick an icon from the icon library.", "DSN-101", href);
            if (!Hex.IsMatch(doc.Color ?? "")) Error("app", doc.Name, $"Color '{doc.Color}' isn't a hex color such as #7c3aed.", "DSN-101", href);
            if (string.IsNullOrWhiteSpace(doc.Suite)) Error("app", doc.Name, "Choose the navigation suite the application appears in.", "DSN-101", href);
        }

        private void Form(FormDoc f)
        {
            var p = $"form:{f.Key}";
            var href = FormHref(f);
            if (DesignDocs.KeyProblem(f.Key) is { } kp) Error(p, f.Name, $"Form design name {kp}.", "INV-130", href);
            if (cfg.FormsByKey.TryGetValue(f.Key, out var other) && other.AppId != doc.Id)
                Error(p, f.Name, $"The design name '{f.Key}' is already used by a form in {other.App.Name}. Form design names are unique across the system.", "INV-130", href);
            if (string.IsNullOrWhiteSpace(f.Name)) Error(p, f.Key, "The form needs a display name.", "DSN-110", href);
            var live = ctx.Live?.Forms.FirstOrDefault(x => x.Id == f.Id);
            if (live != null && !live.Key.Equals(f.Key, StringComparison.OrdinalIgnoreCase))
                Error(p, f.Name, $"The design name of a published form can't change ('{live.Key}').", "INV-130", href);

            // INV-132 / DSN-112
            if (f.IsNonWorkflow && f.Workflows.Count > 0) Error(p, f.Name, "A form with workflows can't be a non-workflow form.", "INV-132", href);
            if (f.IsActivity && f.IsNonWorkflow) Error(p, f.Name, "An activity form can't be a non-workflow form.", "INV-132", href);
            if (f.IsActivity && f.HiddenFromNew) Error(p, f.Name, "An activity form can't be hidden from New.", "INV-132", href);
            if (!f.IsNonWorkflow && f.Workflows.All(w => w.Disabled))
                Error(p, f.Name, f.Workflows.Count == 0 ? "The form has no workflow. Add one, or make it a non-workflow (profile) form." : "Every workflow of this form is disabled, so no records can be created (INV-133).", "INV-133", href);
            foreach (var pf in f.ParentForms.Where(x => !FormExists(x)))
                Error(p, f.Name, $"Parent form '{pf}' doesn't exist.", "DSN-110", href);
            if (f.NumberPadding is < 1 or > 12) Error(p, f.Name, "Number padding must be between 1 and 12 digits.", "PLT-202", href);
            var known = f.AllFields.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in DesignDocs.TemplateFields(f.TitleTemplate).Where(t => t is not ("number" or "form" or "revision") && !known.Contains(t)))
                Warn(p, f.Name, $"The title template uses {{{t}}}, which isn't a field of this form.", "DSN-501", href);
            if (f.Sections.Count == 0) Warn(p, f.Name, "The form has no sections, so it shows nothing.", "DSN-301", href);

            var sectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in f.Sections)
            {
                var sp = $"{p}/section:{s.Key}";
                if (DesignDocs.KeyProblem(s.Key) is { } skp) Error(sp, s.Name, $"Section design name {skp}.", "INV-130", href);
                else if (!sectionKeys.Add(s.Key)) Error(sp, s.Name, $"Two sections use the design name '{s.Key}'.", "INV-130", href);
                if (string.IsNullOrWhiteSpace(s.Name)) Error(sp, s.Key, "The section needs a display name.", "DSN-303", href);
                if (string.IsNullOrWhiteSpace(s.Tab)) Error(sp, s.Name, "The section needs a tab.", "DSN-301", href);
                if (s.IsSubform && s.MaxRows is < 1) Error(sp, s.Name, "Maximum rows must be at least 1.", "DSN-306", href);
                if (s.IsSubform && s.Fields.Count(x => x.Type != FieldType.Prompt) == 0) Warn(sp, s.Name, "The subform has no columns.", "DSN-306", href);
                var ls = live?.Sections.FirstOrDefault(x => x.Id == s.Id);
                if (ls != null && !ls.Key.Equals(s.Key, StringComparison.OrdinalIgnoreCase))
                    Error(sp, s.Name, $"The design name of a published section can't change ('{ls.Key}').", "INV-130", href);
                if (ls != null && ls.IsSubform != s.IsSubform)
                    Error(sp, s.Name, "A published section can't switch between section and subform; stored values would no longer match.", "DSN-403", href);
            }

            var fieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in f.Sections)
                foreach (var x in s.Fields) Field(f, s, x, fieldKeys, live, href);
            foreach (var x in f.Detached)
            {
                Field(f, null, x, fieldKeys, live, href);
                if (x.Required || x.RequiredInPhases.Count > 0)
                    Warn($"{p}/field:{x.Key}", x.Label, "This detached field is marked required but isn't on the layout, so the rule never applies.", "DSN-302", href);
            }

            var wfKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in f.Workflows)
            {
                if (!wfKeys.Add(w.Key)) Error($"{p}/wf:{w.Key}", w.Name, $"Two workflows use the design name '{w.Key}'.", "INV-130", WfHref(f, w));
                Workflow(f, w, live?.Workflows.FirstOrDefault(x => x.Id == w.Id));
            }
            foreach (var x in f.AllFields)
                foreach (var ph in x.RequiredInPhases.Where(ph => f.Workflows.All(w => w.Phases.All(pp => pp.Key != ph))))
                    Error($"{p}/field:{x.Key}", x.Label, $"Required-in phase '{ph}' doesn't exist in this form's workflows.", "DSN-410", href);
        }

        private void Field(FormDoc f, SectionDoc? s, FieldDoc x, HashSet<string> keys, FormDoc? liveForm, string href)
        {
            var p = $"form:{f.Key}/field:{x.Key}";
            var name = string.IsNullOrWhiteSpace(x.Label) ? x.Key : x.Label;
            if (DesignDocs.KeyProblem(x.Key) is { } kp) Error(p, name, $"Field design name {kp}.", "INV-130", href);
            else if (!keys.Add(x.Key)) Error(p, name, $"Two fields of {f.Name} use the design name '{x.Key}'.", "INV-130", href);
            if (x.Type != FieldType.Prompt && string.IsNullOrWhiteSpace(x.Label)) Error(p, x.Key, "The field needs a display name.", "DSN-401", href);
            if (x.Type == FieldType.Prompt && string.IsNullOrWhiteSpace(x.Help)) Warn(p, x.Key, "The text prompt has no text.", "DSN-402", href);
            if (x.Width is < 1 or > 12) Error(p, name, "Width must be between 1 and 12 columns.", "DSN-301", href);
            if (x.Type is FieldType.Keyword or FieldType.KeywordMulti)
            {
                if (string.IsNullOrWhiteSpace(x.KeywordList)) Error(p, name, "Choose the keyword list this field uses.", "DSN-402", href);
                else if (!cfg.KeywordListsByKey.ContainsKey(x.KeywordList)) Error(p, name, $"Keyword list '{x.KeywordList}' doesn't exist.", "DSN-1201", href);
                else if (!string.IsNullOrEmpty(x.DefaultValue) && x.Type == FieldType.Keyword && cfg.Keyword(x.KeywordList, x.DefaultValue) == null)
                    Warn(p, name, $"Default '{x.DefaultValue}' isn't a keyword of '{x.KeywordList}'.", "PLT-402", href);
            }
            foreach (var lf in x.LinkForms.Where(lf => !FormExists(lf)))
                Error(p, name, $"Link target form '{lf}' doesn't exist.", "DSN-1201", href);
            if (x.Min is { } mn && x.Max is { } mx && mn > mx) Error(p, name, "Minimum is greater than maximum.", "PLT-407", href);
            if (x.Decimals is < 0 or > 30) Error(p, name, "Decimal places must be between 0 and 30.", "PLT-407", href);
            if (x.MaxLength is < 1) Error(p, name, "Maximum length must be at least 1.", "DSN-402", href);
            if (x.Type == FieldType.YesNo && x.DefaultValue is { Length: > 0 } dv && dv is not ("yes" or "no"))
                Warn(p, name, "A Yes/No default must be 'yes' or 'no'.", "DSN-404", href);
            var mainKeys = f.MainFields.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cf in DesignDocs.ConditionFields(x.VisibleWhen).Where(cf => !mainKeys.Contains(cf)))
                Error(p, name, $"The visibility rule refers to '{cf}', which isn't a field of this form.", "DSN-1201", href);
            if (s?.IsSubform == true && x.ShowInHeader) Warn(p, name, "Subform columns can't show in the record header.", "DSN-306", href);

            var lx = liveForm?.AllFields.FirstOrDefault(l => l.Id == x.Id);
            if (lx != null)
            {
                if (!lx.Key.Equals(x.Key, StringComparison.OrdinalIgnoreCase))
                    Error(p, name, $"The design name of a published field can't change ('{lx.Key}'): stored values are keyed by it.", "INV-130", href);
                if (lx.Type != x.Type)
                {
                    var n = ctx.DataFor(f.Id, lx.Key);
                    if (n > 0) Error(p, name, $"The type is fixed: {Ui(n)} hold values as {DesignDocs.TypeLabel(lx.Type)}. Add a new field and migrate instead.", "DSN-403", href);
                    else Warn(p, name, $"Type changes from {DesignDocs.TypeLabel(lx.Type)} to {DesignDocs.TypeLabel(x.Type)}. No record holds a value yet.", "DSN-403", href);
                }
                var wasSub = liveForm!.Sections.FirstOrDefault(ls => ls.Fields.Any(l => l.Id == x.Id))?.IsSubform ?? false;
                if (s != null && wasSub != s.IsSubform && ctx.DataFor(f.Id, lx.Key) > 0)
                    Error(p, name, "This field holds data and can't move between a subform and the main form.", "DSN-403", href);
            }
            else if (SystemColumns.Contains(x.Key) && x.Key != "title")
                Warn(p, name, $"'{x.Key}' is also a system column name; views show the system column instead of this field.", "INV-130", href);
        }

        private void Workflow(FormDoc f, WorkflowDoc w, WorkflowDoc? live)
        {
            var p = $"form:{f.Key}/wf:{w.Key}";
            var href = WfHref(f, w);
            if (DesignDocs.KeyProblem(w.Key) is { } kp) Error(p, w.Name, $"Workflow design name {kp}.", "INV-130", href);
            if (string.IsNullOrWhiteSpace(w.Name)) Error(p, w.Key, "The workflow needs a display name.", "DSN-115", href);
            if (live != null && !live.Key.Equals(w.Key, StringComparison.OrdinalIgnoreCase))
                Error(p, w.Name, $"The design name of a published workflow can't change ('{live.Key}').", "INV-130", href);
            if (w.Phases.Count == 0) { Error(p, w.Name, "The workflow has no phases.", "DSN-601", href); return; }
            if (!w.Phases.Any(x => x.IsStart)) Error(p, w.Name, "The workflow needs a starting phase.", "DSN-601", href);
            if (!w.Phases.Any(x => x.Type == PhaseType.Completed)) Warn(p, w.Name, "The workflow has no completed phase, so records can never be closed.", "PLT-601", href);

            var sections = f.Sections.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fields = f.MainFields.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            var phaseKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byKey = new Dictionary<string, PhaseDoc>(StringComparer.OrdinalIgnoreCase);
            foreach (var ph in w.Phases)
            {
                var pp = $"{p}/phase:{ph.Key}";
                var name = string.IsNullOrWhiteSpace(ph.Name) ? ph.Key : ph.Name;
                if (DesignDocs.KeyProblem(ph.Key) is { } pkp) Error(pp, name, $"Phase design name {pkp}.", "INV-130", href);
                else if (!phaseKeys.Add(ph.Key)) Error(pp, name, $"Two phases use the design name '{ph.Key}'.", "INV-130", href);
                byKey.TryAdd(ph.Key, ph);
                if (string.IsNullOrWhiteSpace(ph.Name)) Error(pp, ph.Key, "The phase needs a display name.", "DSN-602", href);
                if (ph.IsStart && ph.Type != PhaseType.Normal) Error(pp, name, "Only normal phases can be starting phases.", "DSN-601", href);
                var lp = live?.Phases.FirstOrDefault(x => x.Id == ph.Id);
                if (lp != null && lp.Type != ph.Type) Error(pp, name, $"A phase's type is fixed once published (was {lp.Type}).", "DSN-602", href);
                if (lp != null && !lp.Key.Equals(ph.Key, StringComparison.OrdinalIgnoreCase))
                    Error(pp, name, $"The design name of a published phase can't change ('{lp.Key}').", "INV-130", href);

                // DSN-305 / INV-135
                var both = ph.EditableSections.Intersect(ph.ReadOnlySections, StringComparer.OrdinalIgnoreCase).Where(s => s != "*").ToList();
                if (both.Count > 0) Error(pp, name, $"Sections can't be both editable and read-only in one phase: {string.Join(", ", both)}.", "INV-135", href);
                var hiddenAndShown = ph.HiddenSections.Intersect(ph.EditableSections.Concat(ph.ReadOnlySections), StringComparer.OrdinalIgnoreCase).Where(s => s != "*").ToList();
                if (hiddenAndShown.Count > 0) Warn(pp, name, $"Sections listed as hidden and shown at once (hidden wins): {string.Join(", ", hiddenAndShown)}.", "DSN-305", href);
                foreach (var s in ph.EditableSections.Concat(ph.ReadOnlySections).Concat(ph.HiddenSections).Where(s => s != "*" && !sections.Contains(s)).Distinct())
                    Error(pp, name, $"Unknown section '{s}' in the visibility matrix.", "DSN-305", href);

                if (ph.Type != PhaseType.Normal) continue;
                // Assignment (PLT-620, INV-072)
                if (ph.Assignment == AssignmentMode.Field && string.IsNullOrWhiteSpace(ph.AssigneeField))
                    Error(pp, name, "Assignment from a field needs the names field to assign from.", "PLT-620", href);
                if (!string.IsNullOrWhiteSpace(ph.AssigneeField))
                {
                    if (!fields.TryGetValue(ph.AssigneeField, out var af)) Error(pp, name, $"Assignee field '{ph.AssigneeField}' doesn't exist.", "DSN-1201", href);
                    else if (af.Type is not (FieldType.User or FieldType.UserMulti)) Error(pp, name, $"Assignee field '{af.Label}' isn't a names field.", "PLT-620", href);
                }
                if (ph.Assignment == AssignmentMode.Group)
                {
                    if (string.IsNullOrWhiteSpace(ph.AssigneeGroup)) Error(pp, name, "Group assignment needs a group.", "PLT-620", href);
                    else if (ctx.Dir.Group(ph.AssigneeGroup) == null) Error(pp, name, $"Group '{ph.AssigneeGroup}' doesn't exist.", "DSN-1201", href);
                    else if (!ctx.Dir.ActiveMembers(ph.AssigneeGroup).Any()) Warn(pp, name, $"Group '{ph.AssigneeGroup}' has no active members, so routes into this phase will be blocked (INV-072).", "PLT-622", href);
                }
                if (ph.Routing == RoutingType.Voting && ph.QuorumPercent is < 1 or > 100) Error(pp, name, "Voting quorum must be between 1 and 100 percent.", "PLT-612", href);
                // Due dates (PLT-630, INV-074)
                if (ph.DueDays is < 0) Error(pp, name, "Due days can't be negative.", "PLT-630", href);
                if (!string.IsNullOrWhiteSpace(ph.DueDateField))
                {
                    if (!fields.TryGetValue(ph.DueDateField, out var df)) Error(pp, name, $"Due-date field '{ph.DueDateField}' doesn't exist.", "DSN-1201", href);
                    else if (df.Type is not (FieldType.Date or FieldType.DateTime)) Error(pp, name, $"Due-date field '{df.Label}' isn't a date field.", "PLT-630", href);
                }
                if (ph.DueDays == null && string.IsNullOrWhiteSpace(ph.DueDateField))
                    Warn(pp, name, "No due-date rule: the 5-day default applies.", "PLT-630", href);
                // Gates (PLT-640)
                foreach (var g in ph.MustCreateForms.Concat(ph.MustCompleteForms).Concat(ph.ActivityForms).Distinct())
                {
                    if (!FormExists(g)) Error(pp, name, $"Activity form '{g}' doesn't exist.", "DSN-1201", href);
                    else if (!IsActivityForm(g)) Warn(pp, name, $"'{g}' isn't an activity form, so it can't be created from this record.", "PLT-640", href);
                }
            }

            // Routes
            var routeSeen = new HashSet<(string, string, RouteKind, string?)>();
            for (var i = 0; i < w.Routes.Count; i++)
            {
                var r = w.Routes[i];
                var rp = $"{p}/route:{i}";
                var label = $"{r.From} → {r.To}";
                if (!byKey.TryGetValue(r.From, out var from)) { Error(rp, label, $"Route starts at unknown phase '{r.From}'.", "DSN-1201", href); continue; }
                if (!byKey.TryGetValue(r.To, out var to)) { Error(rp, label, $"Route goes to unknown phase '{r.To}'.", "DSN-1201", href); continue; }
                label = $"{from.Name} → {to.Name}";
                if (from.Type != PhaseType.Normal) Warn(rp, label, $"Routes from {from.Name} are never offered: it's a {from.Type.ToString().ToLowerInvariant()} phase.", "PLT-601", href);
                if (r.Kind is RouteKind.Forward or RouteKind.SpecialForward && to.Type is not (PhaseType.Normal or PhaseType.Completed))
                    Error(rp, label, "Forward targets must be normal or completed phases.", "DSN-610", href);
                if (r.Kind == RouteKind.Back && to.Type != PhaseType.Normal) Error(rp, label, "Back routes must go to a normal phase.", "DSN-610", href);
                if (r.Kind == RouteKind.Void && to.Type != PhaseType.Voided) Error(rp, label, "Void routes must go to a voided phase.", "DSN-610", href);
                if (r.RequireSignature && string.IsNullOrWhiteSpace(r.SignatureMeaning))
                    Error(rp, label, "This route requires an e-signature but has no signature meaning.", "DSN-606", href);
                foreach (var cf in DesignDocs.ConditionFields(r.Condition).Where(cf => !fields.ContainsKey(cf)))
                    Error(rp, label, $"The route condition refers to '{cf}', which isn't a field of this form.", "DSN-1201", href);
                foreach (var pr in r.AllowedPrincipals)
                {
                    var ok = pr.StartsWith('@') ? ctx.Dir.Group(pr[1..]) != null : ctx.Dir.Get(pr) != null;
                    if (!ok) Warn(rp, label, $"Allowed user or group '{pr}' doesn't exist.", "DSN-604", href);
                }
                if (!routeSeen.Add((r.From.ToLowerInvariant(), r.To.ToLowerInvariant(), r.Kind, r.Condition)))
                    Warn(rp, label, "Duplicate route (same phases, kind and condition).", "DSN-612", href);
            }

            // DSN-612: reachability and dead ends.
            var a = WorkflowAnalysis.Analyze(w);
            foreach (var ph in w.Phases)
            {
                var pp = $"{p}/phase:{ph.Key}";
                var name = string.IsNullOrWhiteSpace(ph.Name) ? ph.Key : ph.Name;
                if (a.Unreachable.Contains(ph.Key))
                {
                    if (ph.Type == PhaseType.Normal) Error(pp, name, "No route leads into this phase from a starting phase, so it can't be reached.", "DSN-612", href);
                    else Warn(pp, name, "No route leads into this phase, so it can't be reached.", "DSN-612", href);
                }
                if (a.DeadEnds.Contains(ph.Key)) Error(pp, name, "This normal phase has no way out (dead end): add a forward, back or void route.", "DSN-612", href);
                else if (a.NoCompletion.Contains(ph.Key) && w.Phases.Any(x => x.Type == PhaseType.Completed))
                    Warn(pp, name, "Records in this phase can never reach a completed phase.", "DSN-612", href);
            }
        }

        private void View(ViewDoc v)
        {
            var p = $"view:{v.Key}";
            var href = ViewHref(v);
            if (DesignDocs.KeyProblem(v.Key) is { } kp) Error(p, v.Name, $"View design name {kp}.", "INV-130", href);
            if (string.IsNullOrWhiteSpace(v.Name)) Error(p, v.Key, "The view needs a display name.", "DSN-703", href);
            var lv = ctx.Live?.Views.FirstOrDefault(x => x.Id == v.Id);
            if (lv != null && !lv.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase))
                Error(p, v.Name, $"The design name of a published view can't change ('{lv.Key}'): favorites and links use it.", "INV-130", href);
            foreach (var k in v.Forms.Where(k => !FormExists(k) || k == "*")) Error(p, v.Name, $"Form '{k}' doesn't exist.", "INV-138", href);

            // Forms the view reads (its data source, INV-138).
            var forms = (v.Forms.Count > 0
                ? v.Forms.Select(k => (Doc: doc.Forms.FirstOrDefault(f => f.Key == k), Live: cfg.FormsByKey.GetValueOrDefault(k)))
                : doc.Forms.Where(f => !f.IsActivity).Select(f => (Doc: (FormDoc?)f, Live: (FormDefinition?)null))).ToList();
            if (forms.Count == 0) { Error(p, v.Name, "The view has no data source: the application has no main forms.", "INV-138", href); return; }
            var fieldTypes = new Dictionary<string, FieldType>(StringComparer.OrdinalIgnoreCase);
            var phases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (d, l) in forms)
            {
                if (d != null)
                {
                    foreach (var x in d.MainFields) fieldTypes.TryAdd(x.Key, x.Type);
                    foreach (var ph in d.Workflows.SelectMany(w => w.Phases)) phases.Add(ph.Key);
                }
                else if (l != null)
                {
                    var subs = l.Sections.Where(s => s.IsSubform).Select(s => s.Key).ToHashSet();
                    foreach (var x in l.Fields.Where(x => !subs.Contains(x.SectionKey))) fieldTypes.TryAdd(x.Key, x.Type);
                    foreach (var ph in l.Workflows.SelectMany(w => w.Phases)) phases.Add(ph.Key);
                }
            }
            if (!DesignDocs.ViewStatuses.Contains(v.Status)) Error(p, v.Name, $"Unknown status filter '{v.Status}'.", "DSN-703", href);
            foreach (var c in v.Columns.Where(c => !SystemColumns.Contains(c) && !fieldTypes.ContainsKey(c)))
                Error(p, v.Name, $"Column '{c}' isn't a system column or a field of the view's forms.", "DSN-704", href);
            foreach (var ph in v.Phases.Where(ph => !phases.Contains(ph)))
                Error(p, v.Name, $"Phase filter '{ph}' isn't a phase of the view's forms.", "DSN-703", href);
            if (!SortableSystem.Contains(v.SortBy) && !fieldTypes.ContainsKey(v.SortBy))
                Error(p, v.Name, $"Sort column '{v.SortBy}' doesn't exist.", "DSN-703", href);
            else if (fieldTypes.TryGetValue(v.SortBy, out var st) && st is FieldType.Link or FieldType.UserMulti or FieldType.KeywordMulti)
                Warn(p, v.Name, "Link and multi-value columns can't be sorted; the view falls back to unsorted order.", "DSN-704", href);
            if (v.PageSize is < 5 or > 500) Error(p, v.Name, "Page size must be between 5 and 500.", "DSN-703", href);
            foreach (var flt in v.Filters)
            {
                if (!fieldTypes.ContainsKey(flt.Field)) Error(p, v.Name, $"Filter field '{flt.Field}' isn't a field of the view's forms.", "DSN-703", href);
                if (!DesignDocs.FilterOps.Contains(flt.Op)) Error(p, v.Name, $"Unknown filter operator '{flt.Op}'.", "DSN-703", href);
                else if (flt.Op is "eq" or "ne" && string.IsNullOrWhiteSpace(flt.Value)) Error(p, v.Name, $"The filter on '{flt.Field}' needs a value.", "DSN-703", href);
            }
            if (v.CalendarField is { Length: > 0 } cal && cal is not ("due" or "created" or "modified" or "effective") &&
                !(fieldTypes.TryGetValue(cal, out var ct) && ct is FieldType.Date or FieldType.DateTime))
                Warn(p, v.Name, $"Calendar column '{cal}' isn't a date.", "PLT-1106", href);
            if (v.IsDefault && v.Hidden) Warn(p, v.Name, "The default view is hidden from the navigator.", "DSN-101", href);
        }

        /// <summary>Objects removed by the draft that still hold records (DSN-601) or are referenced elsewhere.</summary>
        private void Deletions()
        {
            if (ctx.Live == null) return;
            foreach (var lf in ctx.Live.Forms)
            {
                var df = doc.Forms.FirstOrDefault(f => f.Id == lf.Id);
                if (df == null)
                {
                    var n = lf.Id is { } fid ? ctx.RecordsByForm.GetValueOrDefault(fid) : 0;
                    if (n > 0) Error($"form:{lf.Key}", lf.Name, $"The form can't be deleted: {Ui(n)} use it.", "INV-010", AppHref);
                    foreach (var other in cfg.FormsById.Values.Where(o => o.AppId != doc.Id))
                    {
                        if (other.Fields.Any(x => x.LinkForms.Contains(lf.Key)) || other.ParentForms.Contains(lf.Key) ||
                            other.Workflows.SelectMany(w => w.Phases).Any(ph => ph.MustCompleteForms.Contains(lf.Key) || ph.MustCreateForms.Contains(lf.Key) || ph.ActivityForms.Contains(lf.Key)))
                            Error($"form:{lf.Key}", lf.Name, $"The form can't be deleted: {other.Name} ({other.App.Name}) still refers to it.", "DSN-1201", AppHref);
                    }
                    foreach (var ov in cfg.Apps.Where(a => a.Id != doc.Id).SelectMany(a => a.Views).Where(x => x.Forms.Contains(lf.Key)))
                        Error($"form:{lf.Key}", lf.Name, $"The form can't be deleted: view '{ov.Name}' in {cfg.AppsById[ov.AppId].Name} shows it.", "DSN-1201", AppHref);
                    continue;
                }
                foreach (var lw in lf.Workflows)
                {
                    var dw = df.Workflows.FirstOrDefault(w => w.Id == lw.Id);
                    if (dw == null)
                    {
                        var n = lw.Id is { } wid ? ctx.RecordsByWorkflow.GetValueOrDefault(wid) : 0;
                        if (n > 0) Error($"form:{lf.Key}/wf:{lw.Key}", lw.Name, $"The workflow can't be deleted: {Ui(n)} use it.", "INV-010", FormHref(df));
                        continue;
                    }
                    foreach (var lp in lw.Phases.Where(lp => dw.Phases.All(ph => ph.Id != lp.Id)))
                    {
                        var path = $"form:{lf.Key}/wf:{lw.Key}/phase:{lp.Key}";
                        var inPhase = lp.Id is { } pid ? ctx.RecordsInPhase.GetValueOrDefault(pid) : 0;
                        var history = lp.Id is { } hid ? ctx.PhaseHistory.GetValueOrDefault(hid) : 0;
                        if (inPhase > 0)
                            Error(path, lp.Name, $"The phase can't be deleted: {Ui(inPhase)} are in it. Move them to another phase first.", "DSN-601", WfHref(df, dw));
                        else if (history > 0)
                            Error(path, lp.Name, $"The phase can't be deleted: it appears in the phase history of {Ui(history)} (INV-146). Remove the routes into it instead, so no new records enter it.", "DSN-601", WfHref(df, dw));
                    }
                }
                foreach (var lx in lf.AllFields.Where(lx => df.AllFields.All(x => x.Id != lx.Id)))
                {
                    var n = ctx.DataFor(lf.Id, lx.Key);
                    if (n > 0) Warn($"form:{lf.Key}/field:{lx.Key}", lx.Label, $"Deleting the field hides values held by {Ui(n)}. The values stay in the database and the audit trail.", "DSN-302", FormHref(df));
                }
            }
        }

        private static string Ui(int n) => n == 1 ? "1 record" : $"{n} records";
    }
}

/// <summary>DSN-612: routing table facts for one workflow.</summary>
public sealed class WorkflowAnalysis
{
    public HashSet<string> Unreachable { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeadEnds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> NoCompletion { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static WorkflowAnalysis Analyze(WorkflowDoc w)
    {
        var a = new WorkflowAnalysis();
        var phases = w.Phases.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Only routes that leave a normal phase can ever be taken.
        var usable = w.Routes.Where(r => phases.TryGetValue(r.From, out var f) && f.Type == PhaseType.Normal && phases.ContainsKey(r.To)).ToList();

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(w.Phases.Where(p => p.IsStart && p.Type == PhaseType.Normal).Select(p => p.Key));
        foreach (var s in queue) reached.Add(s);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var r in usable.Where(r => r.From.Equals(cur, StringComparison.OrdinalIgnoreCase)))
                if (reached.Add(r.To)) queue.Enqueue(r.To);
        }
        foreach (var p in w.Phases.Where(p => !reached.Contains(p.Key))) a.Unreachable.Add(p.Key);
        foreach (var p in w.Phases.Where(p => p.Type == PhaseType.Normal && usable.All(r => !r.From.Equals(p.Key, StringComparison.OrdinalIgnoreCase))))
            a.DeadEnds.Add(p.Key);

        // Backwards search from completed phases over non-void routes.
        var canFinish = new HashSet<string>(w.Phases.Where(p => p.Type == PhaseType.Completed).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (var r in usable.Where(r => r.Kind != RouteKind.Void && canFinish.Contains(r.To)))
                if (canFinish.Add(r.From)) changed = true;
        } while (changed);
        foreach (var p in w.Phases.Where(p => p.Type == PhaseType.Normal && !canFinish.Contains(p.Key) && !a.DeadEnds.Contains(p.Key)))
            a.NoCompletion.Add(p.Key);
        return a;
    }
}
