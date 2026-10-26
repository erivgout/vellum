using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Insights;

/// <summary>
/// NFR-006 / RPT-704: aggregates are cached per user for at most five minutes (or until the user refreshes).
/// Keys always start with the user ID, so one user's security-trimmed numbers are never served to another.
/// </summary>
public static class InsightsCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (DateTime At, object Value)> Items = new();

    public static async Task<(T Value, DateTime At)> GetAsync<T>(int userId, string key, bool refresh, Func<Task<T>> factory) where T : class
    {
        var k = $"{userId}|{key}";
        var now = DateTime.UtcNow;
        if (!refresh && Items.TryGetValue(k, out var hit) && now - hit.At < Ttl && hit.Value is T t) return (t, hit.At);
        var value = await factory();
        Items[k] = (now, value);
        if (Items.Count > 4000)
            foreach (var old in Items.Where(kv => now - kv.Value.At > Ttl).Select(kv => kv.Key).ToList()) Items.TryRemove(old, out _);
        return (value, now);
    }

    /// <summary>Drop a user's cached aggregates (after they change one of their charts).</summary>
    public static void Forget(int userId, string? prefix = null)
    {
        var p = $"{userId}|{prefix}";
        foreach (var k in Items.Keys.Where(k => k.StartsWith(p, StringComparison.Ordinal)).ToList()) Items.TryRemove(k, out _);
    }
}

/// <summary>
/// A set of records for drill-through (RPT-403): every dashboard number links to /insights/records with one of these.
/// The list page re-runs the filter for the viewing user, so links never widen access (INV-081).
/// </summary>
public sealed class RecordFilter
{
    public string? App { get; set; }
    public string? Form { get; set; }
    /// <summary>open | overdue | completed | closed | voided | all</summary>
    public string Status { get; set; } = "open";
    public string? Phase { get; set; }
    public int? Assignee { get; set; }
    public string? Field { get; set; }
    /// <summary>Keyword key, yes/no, user ID or text. "_none" = blank.</summary>
    public string? Value { get; set; }
    /// <summary>Event the date window applies to: created | completed | signed | late | exited</summary>
    public string? On { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    /// <summary>Open age in days since creation.</summary>
    public int? AgeMin { get; set; }
    public int? AgeMax { get; set; }
    /// <summary>Heading for the list page.</summary>
    public string? Title { get; set; }

    public string Href()
    {
        var p = new List<string>();
        void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) p.Add($"{k}={Uri.EscapeDataString(v)}"); }
        Add("app", App); Add("form", Form); Add("st", Status); Add("ph", Phase); Add("au", Assignee?.ToString(CultureInfo.InvariantCulture));
        Add("fk", Field); Add("fv", Value); Add("on", On);
        Add("from", From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); Add("to", To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("amin", AgeMin?.ToString(CultureInfo.InvariantCulture)); Add("amax", AgeMax?.ToString(CultureInfo.InvariantCulture));
        Add("t", Title);
        return "/insights/records?" + string.Join("&", p);
    }

    public static RecordFilter Parse(IQueryCollection q)
    {
        static DateOnly? D(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        static int? I(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
        string? S(string k) => q.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;
        return new RecordFilter
        {
            App = S("app"), Form = S("form"), Status = S("st") ?? "open", Phase = S("ph"), Assignee = I(S("au")),
            Field = S("fk"), Value = S("fv"), On = S("on"), From = D(S("from")), To = D(S("to")),
            AgeMin = I(S("amin")), AgeMax = I(S("amax")), Title = S("t"),
        };
    }

    public RecordFilter With(Action<RecordFilter> change)
    {
        var f = (RecordFilter)MemberwiseClone();
        change(f);
        return f;
    }
}

/// <summary>Security-trimmed building blocks shared by the dashboards, the chart engine, the drill-through list and alerts.</summary>
[ScopedService]
public sealed class InsightsQueries(VellumDbContext db, AccessService access, ConfigCache cfg, CurrentUser me, UserDirectory dir)
{
    public ConfigModel Config => cfg.Current;

    /// <summary>Applications the user can open that carry workflow records (generic over whatever is installed).</summary>
    public List<AppDefinition> Apps() => Config.Apps
        .Where(a => !a.IsSystem && !a.Disabled && access.CanOpenApp(a.Id) && a.Forms.Any(f => !f.IsNonWorkflow && f.Workflows.Count > 0))
        .ToList();

    /// <summary>The app's main forms: non-activity workflow forms (or every workflow form when the app only has activities).</summary>
    public static List<FormDefinition> MainForms(AppDefinition app)
    {
        var wf = app.Forms.Where(f => !f.Disabled && !f.IsNonWorkflow && f.Workflows.Count > 0).ToList();
        var main = wf.Where(f => !f.IsActivity).ToList();
        return main.Count > 0 ? main : wf;
    }

    /// <summary>Workflow records the user can read in the given apps (archived included; filter as needed). INV-081.</summary>
    public IQueryable<Record> Readable(IEnumerable<AppDefinition> apps)
    {
        var ids = apps.Select(a => a.Id).ToList();
        return access.Readable().Where(r => ids.Contains(r.AppId) && r.WorkflowId != null);
    }

    public IQueryable<Record> ReadableMain(AppDefinition app)
    {
        var formIds = MainForms(app).Select(f => f.Id).ToList();
        return access.Readable().Where(r => r.AppId == app.Id && formIds.Contains(r.FormId) && r.WorkflowId != null);
    }

    public List<int> CompletedPhaseIds() => Config.PhasesById.Values.Where(p => p.Type == PhaseType.Completed).Select(p => p.Id).ToList();

    public static DateTime Utc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>The records a drill-through filter describes, for the current user.</summary>
    public IQueryable<Record> Apply(RecordFilter f)
    {
        var apps = Apps();
        AppDefinition? app = null;
        if (f.App != null)
        {
            app = apps.FirstOrDefault(a => a.Key == f.App);
            if (app == null) return db.Records.Where(_ => false);
        }
        var q = app != null && f.Form == null ? ReadableMain(app) : Readable(app != null ? [app] : apps);
        if (f.Form != null && Config.FormsByKey.TryGetValue(f.Form, out var form)) q = q.Where(r => r.FormId == form.Id);

        var today = BusinessCalendar.Today;
        q = f.Status switch
        {
            "overdue" => q.Where(r => r.PhaseType == PhaseType.Normal && !r.IsArchived && r.DueDate < today),
            "ontrack" => q.Where(r => r.PhaseType == PhaseType.Normal && !r.IsArchived && (r.DueDate == null || r.DueDate >= today)),
            "completed" => q.Where(r => r.PhaseType == PhaseType.Completed),
            "closed" => q.Where(r => r.PhaseType != PhaseType.Normal),
            "voided" => q.Where(r => r.PhaseType == PhaseType.Voided),
            "all" => q,
            _ => q.Where(r => r.PhaseType == PhaseType.Normal && !r.IsArchived),
        };
        if (f.Phase != null)
        {
            var phaseIds = Config.PhasesById.Values.Where(p => p.Key == f.Phase && (app == null || p.Workflow.Form.AppId == app.Id)).Select(p => p.Id).ToList();
            q = f.On == "exited" ? q : q.Where(r => r.PhaseId != null && phaseIds.Contains(r.PhaseId.Value));
        }
        if (f.Assignee is { } uid)
            q = q.Where(r => db.Assignments.Any(a => a.RecordId == r.Id && a.UserId == uid && a.Status == AssignmentStatus.Active));
        if (f.Field != null) q = FieldEquals(q, f.Field, f.Value, FormsOf(f));
        if (f.AgeMin is { } amin) { var cut = DateTime.UtcNow.AddDays(-amin); q = q.Where(r => r.CreatedAt <= cut); }
        if (f.AgeMax is { } amax) { var cut = DateTime.UtcNow.AddDays(-(amax + 1)); q = q.Where(r => r.CreatedAt > cut); }

        if (f.On != null && (f.From != null || f.To != null))
        {
            var from = f.From is { } a ? Utc(a) : DateTime.MinValue;
            var to = f.To is { } b ? Utc(b.AddDays(1)) : DateTime.MaxValue;
            switch (f.On)
            {
                case "created":
                    q = q.Where(r => r.CreatedAt >= from && r.CreatedAt < to);
                    break;
                case "completed":
                    var done = CompletedPhaseIds();
                    q = q.Where(r => db.PhaseEntries.Any(e => e.RecordId == r.Id && done.Contains(e.PhaseId) && e.EnteredAt >= from && e.EnteredAt < to));
                    break;
                case "signed":
                    q = q.Where(r => db.Signatures.Any(s => s.RecordId == r.Id && s.SignedAt >= from && s.SignedAt < to));
                    break;
                case "late":
                    q = q.Where(r => db.PhaseEntries.Any(e => e.RecordId == r.Id && e.ExitedAt >= from && e.ExitedAt < to && e.DueDate != null &&
                        e.ExitAction != "void" && DateOnly.FromDateTime(e.ExitedAt!.Value) > e.DueDate));
                    break;
                case "exited" when f.Phase != null:
                    var pids = Config.PhasesById.Values.Where(p => p.Key == f.Phase && (app == null || p.Workflow.Form.AppId == app.Id)).Select(p => p.Id).ToList();
                    q = q.Where(r => db.PhaseEntries.Any(e => e.RecordId == r.Id && pids.Contains(e.PhaseId) && e.ExitedAt >= from && e.ExitedAt < to));
                    break;
            }
        }
        return q;
    }

    /// <summary>The forms a drill-through filter covers: its form, its application's forms, or every form.</summary>
    private IEnumerable<FormDefinition> FormsOf(RecordFilter f) =>
        f.Form != null && Config.FormsByKey.TryGetValue(f.Form, out var form) ? [form]
        : f.App != null && Config.AppsByKey.TryGetValue(f.App, out var app) ? app.Forms
        : Config.FormsById.Values;

    /// <summary>
    /// Records whose (top-level) field equals a value. Keyword values are keys; "_none" matches blank. Forms of different
    /// applications can use the same design name with different keyword lists, so the field is looked up in
    /// <paramref name="forms"/> and a keyword key matches in any of their lists (RPT-403).
    /// </summary>
    public IQueryable<Record> FieldEquals(IQueryable<Record> q, string fieldKey, string? value, IEnumerable<FormDefinition>? forms = null)
    {
        if (value is null or "_none")
            return q.Where(r => !db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == fieldKey));
        var fields = (forms ?? Config.FormsById.Values).SelectMany(f => f.Fields).Where(x => x.Key == fieldKey).ToList();
        var field = fields.FirstOrDefault();
        IQueryable<RecordValue> m = db.RecordValues.Where(v => v.RowId == null && v.FieldKey == fieldKey);
        switch (field?.Type)
        {
            case FieldType.Keyword or FieldType.KeywordMulti:
                var lists = fields.Where(x => x.Type is FieldType.Keyword or FieldType.KeywordMulti).Select(x => x.KeywordList).ToHashSet();
                var ids = Config.KeywordsById.Values.Where(k => k.Key == value && (lists.Contains(null) || lists.Contains(Config.KeywordListsById[k.ListId].Key))).Select(k => k.Id).ToList();
                m = m.Where(v => v.RefId != null && ids.Contains(v.RefId.Value));
                break;
            case FieldType.YesNo:
                var b = value is "yes" or "true" or "1" ? 1 : 0;
                m = m.Where(v => v.RefId == b);
                break;
            case FieldType.User or FieldType.UserMulti:
                var uid = int.TryParse(value, out var u) ? u : -1;
                m = m.Where(v => v.RefId == uid);
                break;
            default:
                m = m.Where(v => v.Text == value);
                break;
        }
        return q.Where(r => m.Any(v => v.RecordId == r.Id));
    }

    /// <summary>Human description of a drill-through filter, as chips.</summary>
    public List<string> Describe(RecordFilter f)
    {
        var chips = new List<string>();
        if (f.App != null) chips.Add(Config.AppsByKey.TryGetValue(f.App, out var a) ? a.Name : f.App);
        if (f.Form != null) chips.Add(Config.FormsByKey.TryGetValue(f.Form, out var fm) ? fm.Name : f.Form);
        chips.Add(f.Status switch { "overdue" => "Overdue", "ontrack" => "Open, not overdue", "completed" => "Completed", "closed" => "Closed", "voided" => "Voided", "all" => "Any status", _ => "Open" });
        var forms = FormsOf(f).ToList();
        if (f.Phase != null)
            chips.Add((f.On == "exited" ? "Left phase: " : "Phase: ") + (forms.SelectMany(x => x.Workflows).SelectMany(w => w.Phases).FirstOrDefault(p => p.Key == f.Phase)?.Name ?? f.Phase));
        if (f.Assignee is { } u) chips.Add("Assigned to " + dir.Name(u));
        if (f.Field != null)
        {
            var field = forms.SelectMany(x => x.Fields).FirstOrDefault(x => x.Key == f.Field);
            var label = field?.Label ?? f.Field;
            var value = f.Value is null or "_none" ? "(blank)"
                : field?.Type is FieldType.Keyword or FieldType.KeywordMulti ? (field.KeywordList is { } list ? Config.Keyword(list, f.Value)?.Label : null) ?? f.Value
                : field?.Type is FieldType.User or FieldType.UserMulti && int.TryParse(f.Value, out var uid) ? dir.Name(uid)
                : field?.Type == FieldType.YesNo ? (f.Value == "yes" ? "Yes" : "No") : f.Value;
            chips.Add($"{label}: {value}");
        }
        if (f.AgeMin != null || f.AgeMax != null)
            chips.Add(f.AgeMax == null ? $"Open more than {f.AgeMin} days" : $"Open {f.AgeMin ?? 0}–{f.AgeMax} days");
        if (f.On != null && (f.From != null || f.To != null))
            chips.Add((f.On switch { "completed" => "Completed ", "signed" => "Signed ", "late" => "Late phase exit ", "exited" => "Exited ", _ => "Created " }) + RelativeDates.Span(f.From, f.To));
        return chips;
    }

    public int UserId => me.Id;
}
