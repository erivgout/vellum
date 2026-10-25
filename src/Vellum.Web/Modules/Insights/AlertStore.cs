using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Insights;

/// <summary>Input for creating or editing an alert template.</summary>
public sealed class AlertInput
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string App { get; set; } = "";
    public string View { get; set; } = "";
    public List<string> MatchColumns { get; set; } = [];
    public List<string> ReportColumns { get; set; } = [];
    public int MinMatches { get; set; } = 3;
    public int? LookbackDays { get; set; } = 90;
    public DateOnly? WindowStart { get; set; }
    public DateOnly? WindowEnd { get; set; }
    public bool IncrementDates { get; set; }
    public int FrequencyDays { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool OnlyWhenChanged { get; set; }
    public List<int> Users { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public List<int> CcUsers { get; set; } = [];
    public string Subject { get; set; } = AlertEngine.DefaultSubject;
    public string Body { get; set; } = AlertEngine.DefaultBody;
    /// <summary>notify (RPT-602) | create (RPT-603)</summary>
    public string Action { get; set; } = "notify";
    public AlertCreateConfig Create { get; set; } = new();

    public static AlertInput From(InsightsAlertTemplate t)
    {
        var to = AlertRecipients.Parse(t.RecipientsJson);
        return new AlertInput
        {
            Name = t.Name, Description = t.Description, App = t.AppKey, View = t.ViewKey,
            MatchColumns = JsonSerializer.Deserialize<List<string>>(t.MatchColumnsJson) ?? [],
            ReportColumns = JsonSerializer.Deserialize<List<string>>(t.ReportColumnsJson) ?? [],
            MinMatches = t.MinMatches, LookbackDays = t.LookbackDays, WindowStart = t.WindowStart, WindowEnd = t.WindowEnd,
            IncrementDates = t.IncrementDates, FrequencyDays = t.FrequencyDays, Enabled = t.Enabled, OnlyWhenChanged = t.OnlyWhenChanged,
            Users = to.Users, Groups = to.Groups, CcUsers = AlertRecipients.Parse(t.CcJson).Users, Subject = t.Subject, Body = t.Body,
            Action = t.Action == "create" ? "create" : "notify", Create = AlertCreateConfig.Parse(t.CreateJson),
        };
    }
}

/// <summary>
/// Alert templates and reports. Templates are visible to their owner and administrators; a report is visible to the
/// template owner, the users it notified and administrators, and its records are re-trimmed for whoever opens it.
/// </summary>
[ScopedService]
public sealed class AlertStore(VellumDbContext db, CurrentUser me, AccessService access, NumberingService numbers, BuilderCatalog catalog, UserDirectory dir,
    AlertCreateCatalog createCatalog)
{
    public IQueryable<InsightsAlertTemplate> Templates => db.Set<InsightsAlertTemplate>();
    public IQueryable<InsightsAlertReport> Reports => db.Set<InsightsAlertReport>();

    public Task EnsureAsync() => InsightsSchema.EnsureAsync(db);

    public async Task<List<InsightsAlertTemplate>> VisibleTemplatesAsync()
    {
        await EnsureAsync();
        var q = Templates.AsNoTracking();
        if (!access.IsAdmin) q = q.Where(t => t.OwnerId == me.Id);
        return await q.OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<InsightsAlertTemplate?> GetAsync(int id, bool tracking = false)
    {
        await EnsureAsync();
        var t = await (tracking ? Templates : Templates.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id);
        return t != null && (t.OwnerId == me.Id || access.IsAdmin) ? t : null;
    }

    public static List<int> Notified(InsightsAlertReport r) =>
        string.IsNullOrEmpty(r.NotifiedJson) ? [] : JsonSerializer.Deserialize<List<int>>(r.NotifiedJson) ?? [];

    public async Task<List<(InsightsAlertReport Report, InsightsAlertTemplate Template)>> VisibleReportsAsync(int? templateId = null, DateTime? since = null)
    {
        await EnsureAsync();
        var q = from r in Reports.AsNoTracking() join t in Templates.AsNoTracking() on r.TemplateId equals t.Id select new { r, t };
        if (templateId != null) q = q.Where(x => x.t.Id == templateId);
        if (since != null) q = q.Where(x => x.r.GeneratedAt >= since);
        var rows = await q.OrderByDescending(x => x.r.GeneratedAt).Take(1000).ToListAsync();
        return rows.Where(x => access.IsAdmin || x.t.OwnerId == me.Id || Notified(x.r).Contains(me.Id)).Select(x => (x.r, x.t)).ToList();
    }

    public async Task<(InsightsAlertReport Report, InsightsAlertTemplate Template)?> GetReportAsync(long id)
    {
        await EnsureAsync();
        var r = await Reports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return null;
        var t = await Templates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.TemplateId);
        if (t == null) return null;
        return access.IsAdmin || t.OwnerId == me.Id || Notified(r).Contains(me.Id) ? (r, t) : null;
    }

    /// <summary>Records created by (or attempted for) a report, in order.</summary>
    public Task<List<InsightsAlertCreation>> CreationsAsync(long reportId) =>
        db.Set<InsightsAlertCreation>().AsNoTracking().Where(c => c.ReportId == reportId).OrderBy(c => c.Id).ToListAsync();

    /// <summary>RPT-601/602/603 rules for a template. <paramref name="ownerId"/> is the template owner (the current user for a new template).</summary>
    public List<ChartIssue> Validate(AlertInput a, int? ownerId = null)
    {
        var issues = new List<ChartIssue>();
        void Err(string f, string m) => issues.Add(new ChartIssue(f, m));
        if (string.IsNullOrWhiteSpace(a.Name)) Err("Name", "Give the alert a name.");
        var cols = catalog.Columns(a.App, a.View);
        if (cols.Count == 0) Err("View", "Choose an application and a view you can read.");
        if (a.MatchColumns.Count == 0) Err("MatchColumns", "Choose at least one match column (the grouping key, for example Supplier).");
        foreach (var m in a.MatchColumns)
        {
            var col = cols.FirstOrDefault(c => c.Key == m);
            if (col == null) { if (cols.Count > 0) Err("MatchColumns", $"The match column '{m}' isn't in this view's forms."); }
            else if (col.IsNumber || col.IsDate) Err("MatchColumns", $"{col.Label} can't be a match column: use keyword, person, yes/no or text columns.");
        }
        foreach (var m in a.ReportColumns.Where(m => m is not ("number" or "title")))
            if (cols.Count > 0 && cols.All(c => c.Key != m) && !Engine.Views.ViewService.SystemColumns.ContainsKey(m)) Err("ReportColumns", $"The report column '{m}' isn't in this view's forms.");
        if (a.MinMatches is < 1 or > 10_000) Err("MinMatches", "The minimum number of matches is between 1 and 10,000.");
        if (a.FrequencyDays is < 1 or > 366) Err("FrequencyDays", "Run every 1 to 366 days.");
        if (a.LookbackDays is < 1 or > 3660) Err("LookbackDays", "Look back between 1 and 3,660 days, or leave it blank for every record in the view.");
        if (a.WindowStart is { } s && a.WindowEnd is { } e && e < s) Err("WindowEnd", "The window must end on or after its start.");
        if (a.IncrementDates && a.WindowStart == null && a.WindowEnd == null) Err("IncrementDates", "Sliding the window needs a start or end date.");
        if (a.Action is not ("notify" or "create")) Err("Action", "Choose what the alert does: notify people or create a record.");
        if (a.Action == "create")
        {
            var recordFields = cols.Where(c => c.Field != null).Select(c => c.Key).ToList();
            issues.AddRange(createCatalog.Validate(a.Create, ownerId ?? me.Id, a.MatchColumns, recordFields));
            return issues;
        }
        if (a.Users.Count == 0 && a.Groups.Count == 0) Err("Users", "Choose at least one recipient (a person or a group).");
        foreach (var u in a.Users.Concat(a.CcUsers).Distinct())
            if (dir.Get(u) is not { IsActive: true }) Err("Users", $"{dir.Get(u)?.DisplayName ?? "A selected user"} isn't active and can't be chosen.");
        foreach (var g in a.Groups.Distinct())
            if (dir.Group(g) is not { Inactive: false }) Err("Groups", $"The group '{g}' is inactive and can't be chosen.");
        if (string.IsNullOrWhiteSpace(a.Subject)) Err("Subject", "Enter a subject.");
        if (string.IsNullOrWhiteSpace(a.Body)) Err("Body", "Enter a message.");
        foreach (var (f, text) in new[] { ("Subject", a.Subject), ("Body", a.Body) })
            if (AlertEngine.UnknownPlaceholders(text) is { Count: > 0 } unknown)
                Err(f, $"Unknown placeholder(s): {string.Join(", ", unknown.Select(u => "{" + u + "}"))}. Use {string.Join(", ", AlertEngine.Placeholders.Select(p => "{" + p + "}"))}.");
        return issues;
    }

    public async Task<(InsightsAlertTemplate? Template, List<ChartIssue> Issues)> SaveAsync(InsightsAlertTemplate? existing, AlertInput a)
    {
        await EnsureAsync();
        var issues = Validate(a, existing?.OwnerId);
        if (issues.Count > 0) return (null, issues);
        var now = DateTime.UtcNow;
        var t = existing ?? new InsightsAlertTemplate { OwnerId = me.Id, CreatedAt = now };
        if (existing == null)
        {
            t.Number = $"ALT-{await numbers.NextAsync("insights-alert"):D5}";
            db.Set<InsightsAlertTemplate>().Add(t);
        }
        t.Name = a.Name.Trim();
        t.Description = string.IsNullOrWhiteSpace(a.Description) ? null : a.Description.Trim();
        t.AppKey = a.App;
        t.ViewKey = a.View;
        t.MatchColumnsJson = JsonSerializer.Serialize(a.MatchColumns.Distinct().ToList());
        t.ReportColumnsJson = JsonSerializer.Serialize(a.ReportColumns.Where(c => !a.MatchColumns.Contains(c)).Distinct().ToList());
        t.MinMatches = a.MinMatches;
        t.LookbackDays = a.LookbackDays;
        t.WindowStart = a.WindowStart;
        t.WindowEnd = a.WindowEnd;
        t.IncrementDates = a.IncrementDates;
        t.FrequencyDays = a.FrequencyDays;
        t.Enabled = a.Enabled;
        t.OnlyWhenChanged = a.OnlyWhenChanged;
        t.RecipientsJson = new AlertRecipients { Users = a.Users.Distinct().ToList(), Groups = a.Groups.Distinct().ToList() }.ToJson();
        t.CcJson = new AlertRecipients { Users = a.CcUsers.Distinct().ToList() }.ToJson();
        t.Subject = string.IsNullOrWhiteSpace(a.Subject) ? AlertEngine.DefaultSubject : a.Subject.Trim();
        t.Body = string.IsNullOrWhiteSpace(a.Body) ? AlertEngine.DefaultBody : a.Body.Trim();
        t.Action = a.Action;
        t.CreateJson = a.Action == "create" ? AlertCreateCatalog.Normalize(a.Create).ToJson() : null;
        t.ModifiedAt = now;
        await db.SaveChangesAsync();
        return (t, []);
    }
}
