using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Insights;

/// <summary>
/// What the create-record editor (RPT-603) may offer: the applications and forms the template <b>owner</b> may create
/// records in, each form's starting phases and settable fields (required ones flagged, INV-127), and the save-time rules.
/// </summary>
[ScopedService]
public sealed class AlertCreateCatalog(IServiceScopeFactory scopes, ConfigCache cfgCache, UserDirectory dir, VellumDbContext db)
{
    public sealed record FormItem(FormDefinition Form, List<(WorkflowDefinition Workflow, PhaseDefinition Phase)> Phases);
    public sealed record AppItem(AppDefinition App, List<FormItem> Forms);

    private readonly Dictionary<int, List<AppItem>> _cache = [];

    /// <summary>Applications and forms <paramref name="ownerId"/> may create records in (SEC-506).</summary>
    public List<AppItem> Apps(int ownerId)
    {
        if (_cache.TryGetValue(ownerId, out var hit)) return hit;
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Impersonate(ownerId);
        var access = scope.ServiceProvider.GetRequiredService<AccessService>();
        var list = cfgCache.Current.Apps.Where(a => !a.IsSystem && !a.Disabled).OrderBy(a => a.Order).ThenBy(a => a.Name)
            .Select(a => new AppItem(a, AlertCreateRules.CreatableForms(a).Where(access.CanCreate).Select(f => new FormItem(f, AlertCreateRules.StartPhases(f))).ToList()))
            .Where(a => a.Forms.Count > 0).ToList();
        return _cache[ownerId] = list;
    }

    public static AlertCreateConfig Normalize(AlertCreateConfig c) => new()
    {
        App = c.App, Form = c.Form, Phase = c.Phase, Per = c.Per, DueDays = c.DueDays, SkipWhileOpen = c.SkipWhileOpen,
        Users = c.Users.Distinct().ToList(), Groups = c.Groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList(),
        NotifyUsers = c.NotifyUsers.Distinct().ToList(), NotifyGroups = c.NotifyGroups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList(),
        // Blank values aren't stored: the field keeps the form's default (the editor lists required fields again on load).
        Fields = c.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Field) && !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new AlertFieldValue { Field = f.Field.Trim(), Value = f.Value!.Trim() }).ToList(),
    };

    /// <summary>How a starting phase assigns people when the action names nobody (shown in the editor).</summary>
    public string? AutoAssign(FormDefinition form, PhaseDefinition p) => p.Assignment switch
    {
        AssignmentMode.Group when p.AssigneeGroup != null && dir.ActiveMembers(p.AssigneeGroup).Any() => $"the {dir.Group(p.AssigneeGroup)?.Name ?? p.AssigneeGroup} group",
        AssignmentMode.Field when p.AssigneeField != null => $"the person in {form.Fields.FirstOrDefault(f => f.Key == p.AssigneeField)?.Label ?? p.AssigneeField}",
        _ => null,
    };

    /// <summary>Save-time rules (RPT-603, INV-127). Run-time checks repeat them against the live configuration.</summary>
    public List<ChartIssue> Validate(AlertCreateConfig c, int ownerId, IReadOnlyCollection<string> matchColumns, IReadOnlyCollection<string> recordFields)
    {
        var issues = new List<ChartIssue>();
        void Err(string f, string m) => issues.Add(new ChartIssue("Create." + f, m));
        var cfg = cfgCache.Current;
        var app = Apps(ownerId).FirstOrDefault(a => a.App.Key == c.App);
        var item = app?.Forms.FirstOrDefault(f => f.Form.Key == c.Form);
        if (item == null)
        {
            Err("Form", "Choose the application and form to create records in. Only forms the alert's owner may create records in can be chosen.");
            return issues;
        }
        var form = item.Form;
        var phase = AlertCreateRules.FindPhase(form, c.Phase);
        if (phase == null) Err("Phase", $"Choose a starting phase of {form.Name}.");
        if (AlertCreateConfig.PerOptions.All(o => o.Key != c.Per)) Err("Per", "Choose whether to create one record per group, per report or per matched record.");
        if (c.DueDays is < 0 or > 365) Err("DueDays", "The due date is 0 to 365 business days away, or blank for the phase's own rule.");

        foreach (var u in c.Users.Concat(c.NotifyUsers).Distinct())
            if (dir.Get(u) is not { IsActive: true }) Err(c.Users.Contains(u) ? "Users" : "NotifyUsers", $"{dir.Get(u)?.DisplayName ?? "A selected user"} isn't active and can't be chosen.");
        foreach (var g in c.Groups.Concat(c.NotifyGroups).Distinct())
            if (dir.Group(g) is not { Inactive: false }) Err(c.Groups.Contains(g) ? "Groups" : "NotifyGroups", $"The group '{g}' is inactive and can't be chosen.");

        // Field values: known, settable fields, each once, with valid templates.
        var parser = new ValueParser(cfg, dir, (number, f) => RecordByNumber(cfg, number, f));
        var set = new HashSet<string>();
        foreach (var fv in c.Fields.Where(x => !string.IsNullOrWhiteSpace(x.Field)))
        {
            var f = form.Fields.FirstOrDefault(x => x.Key == fv.Field);
            if (f == null || !AlertCreateRules.Settable(form, f)) { Err("Fields", $"{form.Name} has no field '{fv.Field}' an alert can set."); continue; }
            if (!set.Add(f.Key)) { Err("Fields", $"{f.Label} is listed more than once."); continue; }
            var parsed = AlertTemplate.Parse(fv.Value);
            if (parsed.Error != null) { Err("Fields", $"{f.Label}: {parsed.Error}"); continue; }
            foreach (var tok in parsed.TokenNames.Distinct())
                if (AlertTemplate.CheckToken(tok, matchColumns, recordFields) is { } bad) Err("Fields", $"{f.Label}: {bad}");
            if (!parsed.HasTokens && !string.IsNullOrWhiteSpace(fv.Value) && parser.FromText(f, string.Concat(parsed.Segments.Select(s => s.Literal))).Error is { } err)
                Err("Fields", err);
        }

        // INV-127: every required field gets a value from the action or from a default that applies to alerts.
        if (phase is { } ph)
        {
            var given = c.Fields.Where(x => !string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Field).ToHashSet();
            var required = AlertCreateRules.RequiredFields(form, ph.Phase).Where(f => f.VisibleWhen == null).ToList();
            var attachments = required.Where(f => f.Type == FieldType.Attachment).ToList();
            if (attachments.Count > 0)
                Err("Fields", $"{form.Name} requires {AlertCreateRules.FieldList(attachments)}, an attachment an alert can't add, so alerts can't create {form.Name} records.");
            var missing = required.Where(f => f.Type != FieldType.Attachment && !f.ReadOnly && !given.Contains(f.Key) && AlertCreateRules.EffectiveDefault(f) == null).ToList();
            if (missing.Count > 0)
                Err("Fields", $"Set every required field of {form.Name} (INV-127). Missing: {AlertCreateRules.FieldList(missing)}." +
                    (missing.Any(f => f.DefaultValue == "$me") ? " Defaults to the person creating the record don't apply: alerts create records as the system." : ""));

            // INV-072: the starting phase needs someone to assign.
            if (c.Users.Count == 0 && c.Groups.Count == 0)
            {
                var p = ph.Phase;
                var byField = p.Assignment == AssignmentMode.Field && p.AssigneeField != null && given.Contains(p.AssigneeField);
                var byGroup = p.Assignment == AssignmentMode.Group && p.AssigneeGroup != null && dir.ActiveMembers(p.AssigneeGroup).Any();
                if (!byField && !byGroup) Err("Users", $"Choose who to assign: {p.Name} doesn't assign anyone automatically.");
            }
        }
        return issues;
    }

    private Guid? RecordByNumber(ConfigModel cfg, string number, FieldDefinition f)
    {
        var formIds = f.LinkForms.Select(k => cfg.FormsByKey.GetValueOrDefault(k)?.Id).OfType<int>().ToList();
        var q = db.Records.AsNoTracking().Where(r => r.Number == number && !r.IsArchived);
        if (formIds.Count > 0) q = q.Where(r => formIds.Contains(r.FormId));
        return q.Select(r => (Guid?)r.Id).FirstOrDefault();
    }

    public static string TypeLabel(FieldType t) => t switch
    {
        FieldType.Text => "text", FieldType.TextArea => "long text", FieldType.Number => "number", FieldType.Currency => "amount",
        FieldType.Date => "date", FieldType.DateTime => "date and time", FieldType.Keyword => "choice", FieldType.KeywordMulti => "choices",
        FieldType.YesNo => "yes/no", FieldType.User => "person", FieldType.UserMulti => "people", FieldType.Link => "record link",
        FieldType.Url => "web address", FieldType.Attachment => "attachment", _ => t.ToString().ToLowerInvariant(),
    };

    /// <summary>JSON island for the editor script: { apps:[{key,name,forms:[{key,name,phases,fields}]}], per, tokens, filters, system }.</summary>
    public string Json(int ownerId)
    {
        var cfg = cfgCache.Current;
        var apps = Apps(ownerId).Select(a => new
        {
            key = a.App.Key, name = a.App.Name,
            forms = a.Forms.Select(fi => new
            {
                key = fi.Form.Key, name = fi.Form.Name,
                phases = fi.Phases.Select(p => new
                {
                    value = AlertCreateRules.PhaseValue(p.Workflow, p.Phase),
                    name = fi.Form.Workflows.Count(w => !w.Disabled) > 1 ? $"{p.Workflow.Name} › {p.Phase.Name}" : p.Phase.Name,
                    key = p.Phase.Key, dueDays = p.Phase.DueDays, businessDays = p.Phase.BusinessDays,
                    auto = AutoAssign(fi.Form, p.Phase), autoField = p.Phase.Assignment == AssignmentMode.Field ? p.Phase.AssigneeField : null,
                }),
                fields = fi.Form.Fields.Where(f => AlertCreateRules.IsMainField(fi.Form, f) && f.Type != FieldType.Prompt)
                    .Where(f => AlertCreateRules.Settable(fi.Form, f) || f.Required || f.RequiredInPhases.Count > 0)
                    .OrderBy(f => f.Order).Select(f => new
                    {
                        key = f.Key, label = f.Label, type = TypeLabel(f.Type), multi = FieldCodec.IsMulti(f.Type),
                        required = f.Required, requiredIn = f.RequiredInPhases, visibleWhen = f.VisibleWhen,
                        settable = AlertCreateRules.Settable(fi.Form, f), computed = f.ReadOnly, attachment = f.Type == FieldType.Attachment,
                        @default = DefaultLabel(f, cfg), meDefault = f.DefaultValue == "$me", maxLength = f.MaxLength, help = f.Help,
                        options = f.Type is FieldType.Keyword or FieldType.KeywordMulti ? cfg.Options(f.KeywordList).Where(k => !k.Disabled).Select(k => k.Label).ToList()
                            : f.Type == FieldType.YesNo ? ["Yes", "No"] : null,
                        kind = f.Type switch { FieldType.User or FieldType.UserMulti => "user", FieldType.Date => "date", FieldType.Link => "link", _ => null },
                    }),
            }),
        });
        return JsonSerializer.Serialize(new
        {
            apps,
            tokens = AlertTemplate.Tokens.Select(t => new { token = t.Token, meaning = t.Meaning }),
            filters = AlertTemplate.FilterDocs.Select(f => new { filter = f.Filter, meaning = f.Meaning }),
            system = AlertTemplate.RecordSystem,
        });
    }

    /// <summary>The form default that applies to records created by an alert, for display (null when none applies).</summary>
    public static string? DefaultLabel(FieldDefinition f, ConfigModel cfg)
    {
        var dv = AlertCreateRules.EffectiveDefault(f);
        if (dv == null) return null;
        if (dv == "$today") return "today";
        if (dv == "$now") return "now";
        if (dv.StartsWith("$today+", StringComparison.Ordinal)) return $"today + {dv[7..]} days";
        if (f.Type is FieldType.Keyword or FieldType.KeywordMulti && f.KeywordList != null) return cfg.Keyword(f.KeywordList, dv)?.Label ?? dv;
        if (f.Type == FieldType.YesNo) return dv is "yes" or "true" or "1" ? "Yes" : "No";
        return dv;
    }
}
