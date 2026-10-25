using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Views;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Insights;

public sealed class AlertRecipients
{
    public List<int> Users { get; set; } = [];
    public List<string> Groups { get; set; } = [];

    public static AlertRecipients Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AlertRecipients();
        try { return JsonSerializer.Deserialize<AlertRecipients>(json, JsonOpts) ?? new AlertRecipients(); }
        catch (JsonException) { return new AlertRecipients(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}

/// <summary>Machine-readable alert result (RPT-604), stored with the report and offered as a JSON download.</summary>
public sealed class AlertResult
{
    public List<AlertColumn> Columns { get; set; } = [];
    public List<AlertGroup> Groups { get; set; } = [];
    public int Evaluated { get; set; }
    public bool Truncated { get; set; }
}

public sealed record AlertColumn(string Key, string Label, bool Match);

public sealed class AlertGroup
{
    public List<string> Values { get; set; } = [];
    public int Count { get; set; }
    public List<AlertRecord> Records { get; set; } = [];
}

public sealed class AlertRecord
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public Dictionary<string, string> Cells { get; set; } = [];
}

/// <summary>Raised when an alert can't be evaluated or its action can't be carried out (RPT-606).</summary>
public sealed class AlertFailure(string message) : Exception(message);

/// <summary>
/// Threshold alerts (RPT §7.6). The watched view is evaluated as the template owner, so a report never contains records
/// the owner can't open (INV-081); report pages re-trim for whoever views them. A run always produces a report; when
/// the notify (RPT-602) or create-record (RPT-603, <see cref="AlertCreateAction"/>) action fails, the failure is raised to
/// the owner and administrators as a safety-critical alert (RPT-606, SAF-020) instead of failing silently.
/// </summary>
[ScopedService]
public sealed partial class AlertEngine(IServiceScopeFactory scopes, VellumDbContext db, NotificationService notify, NumberingService numbers,
    AuditService audit, UserDirectory dir, ConfigCache cfg, AlertCreateAction creator)
{
    public const int MaxRecords = 20_000;
    public static readonly string[] Placeholders = ["name", "number", "app", "view", "groups", "records", "minimum", "date", "link"];
    public const string DefaultSubject = "{name}: {groups} group(s) reached {minimum} or more";
    public const string DefaultBody = "Alert {number} found {groups} group(s) in {app} › {view} with at least {minimum} matching records ({records} records in total). Open the report: {link}";

    [GeneratedRegex(@"\{([a-zA-Z_]+)\}")]
    private static partial Regex PlaceholderRx();

    /// <summary>Unknown placeholders in a subject/body (validated on save; a run with one fails visibly).</summary>
    public static List<string> UnknownPlaceholders(string? text) =>
        PlaceholderRx().Matches(text ?? "").Select(m => m.Groups[1].Value).Where(p => !Placeholders.Contains(p)).Distinct().ToList();

    public static bool IsDue(InsightsAlertTemplate t, DateTime now)
    {
        if (!t.Enabled) return false;
        var today = DateOnly.FromDateTime(now);
        if (t.WindowStart is { } s && today < s) return false;
        if (t.WindowEnd is { } e && today > e) return false;
        return t.LastReportAt == null || now >= t.LastReportAt.Value.AddDays(Math.Max(1, t.FrequencyDays)).AddMinutes(-30);
    }

    /// <summary>
    /// Run a template now. <paramref name="test"/> (RPT-605) runs even when disabled, sends nothing and creates nothing
    /// (a create-record action is dry-run). A real run whose action didn't finish (the report is still Pending, for example
    /// after a crash) is resumed instead of repeated, so it never notifies or creates twice.
    /// </summary>
    public async Task<InsightsAlertReport> RunAsync(int templateId, bool manual, bool test, DateTime? now = null)
    {
        await InsightsSchema.EnsureAsync(db);
        var at = now ?? DateTime.UtcNow;
        var t = await db.Set<InsightsAlertTemplate>().FirstAsync(x => x.Id == templateId);
        if (!test && await db.Set<InsightsAlertReport>().Where(r => r.TemplateId == t.Id && !r.IsTest && r.ActionStatus == "Pending")
                .OrderBy(r => r.Id).FirstOrDefaultAsync() is { } pending)
            // While Pending, a detail is only ever the evaluation failure (and then there are no matches to act on).
            return await CompleteAsync(t, pending, manual, at, evaluationFailure: pending.GroupCount == 0 ? pending.ActionDetail : null, retry: false);

        var report = new InsightsAlertReport { TemplateId = t.Id, GeneratedAt = at, IsTest = test, Manual = manual, ActionStatus = "Pending" };
        report.Number = $"ALR-{await numbers.NextAsync("insights-alert-report"):D5}";
        var c = cfg.Current;
        var app = c.AppsByKey.GetValueOrDefault(t.AppKey);
        var view = app?.Views.FirstOrDefault(v => v.Key == t.ViewKey);
        var match = JsonSerializer.Deserialize<List<string>>(t.MatchColumnsJson) ?? [];
        report.OriginJson = JsonSerializer.Serialize(new
        {
            template = t.Number, name = t.Name, application = app?.Name ?? t.AppKey, appKey = t.AppKey,
            forms = view == null ? [] : (view.Forms.Count > 0 ? view.Forms : app!.Forms.Where(f => !f.IsActivity).Select(f => f.Key).ToList()),
            view = view?.Name ?? t.ViewKey, viewKey = t.ViewKey, matchColumns = match, minimum = t.MinMatches, frequencyDays = t.FrequencyDays,
            lookbackDays = t.LookbackDays, windowStart = t.WindowStart, windowEnd = t.WindowEnd, owner = dir.Name(t.OwnerId),
        }, AlertRecipients.JsonOpts);

        string? failure = null;
        AlertResult? result = null;
        try { result = await EvaluateAsOwnerAsync(t, at); }
        catch (AlertFailure ex) { failure = ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { failure = "The view couldn't be evaluated: " + ex.Message; }

        result ??= new AlertResult();
        report.ResultJson = JsonSerializer.Serialize(result, AlertRecipients.JsonOpts);
        report.GroupCount = result.Groups.Count;
        report.RecordCount = result.Groups.Sum(g => g.Count);
        report.ResultHash = Hash(result);
        report.ActionDetail = failure; // kept while Pending so a resumed run knows the evaluation failed
        db.Set<InsightsAlertReport>().Add(report);
        await db.SaveChangesAsync();
        return await CompleteAsync(t, report, manual, at, failure, retry: false);
    }

    /// <summary>
    /// RPT-603: retry the create-record action of a report whose action failed (or didn't finish). Items that already have a
    /// created record are skipped (idempotent), so only the missing records are created. Notify actions can't be retried:
    /// their messages were already sent.
    /// </summary>
    public async Task<InsightsAlertReport> RetryAsync(long reportId)
    {
        await InsightsSchema.EnsureAsync(db);
        var report = await db.Set<InsightsAlertReport>().FirstAsync(r => r.Id == reportId);
        var t = await db.Set<InsightsAlertTemplate>().FirstAsync(x => x.Id == report.TemplateId);
        if (report.IsTest) throw new AlertFailure("Test reports never create records, so there is nothing to retry. Run a new test report instead.");
        if (t.Action != "create") throw new AlertFailure("Only create-record actions can be retried: the notifications of this report were already sent.");
        if (report.ActionStatus is not ("Failed" or "Pending")) throw new AlertFailure("This report's action didn't fail, so there is nothing to retry.");
        var evaluationFailed = report.GroupCount == 0 && report.RecordCount == 0 && report.ActionStatus == "Failed";
        if (evaluationFailed) throw new AlertFailure("The view couldn't be evaluated when this report ran, so it has no matches to act on. Run the alert again instead.");
        return await CompleteAsync(t, report, report.Manual, DateTime.UtcNow, null, retry: true);
    }

    /// <summary>Carry out (or resume) the action of a saved report and finish it.</summary>
    private async Task<InsightsAlertReport> CompleteAsync(InsightsAlertTemplate t, InsightsAlertReport report, bool manual, DateTime at, string? evaluationFailure, bool retry)
    {
        var test = report.IsTest;
        var failure = evaluationFailure;
        var result = JsonSerializer.Deserialize<AlertResult>(report.ResultJson, AlertRecipients.JsonOpts) ?? new AlertResult();
        var c = cfg.Current;
        var app = c.AppsByKey.GetValueOrDefault(t.AppKey);
        var view = app?.Views.FirstOrDefault(v => v.Key == t.ViewKey);
        var url = $"/insights/alerts/reports/{report.Id}";

        var notified = AlertStore.Notified(report);
        if (failure == null && t.Action == "create")
        {
            try
            {
                var form = AlertCreateAction.ConfigOf(t).Form is { } fk ? c.FormsByKey.GetValueOrDefault(fk) : null;
                var formName = form?.Name ?? "record";
                if (report.GroupCount == 0)
                {
                    report.ActionStatus = test ? "Test" : "NoMatches";
                    report.ActionDetail = test ? "Test run: no group reached the minimum, so nothing would be created." : "No group reached the minimum. Nothing was created.";
                }
                else if (!test && !retry && t.OnlyWhenChanged && await db.Set<InsightsAlertReport>().Where(r => r.TemplateId == t.Id && !r.IsTest && r.Id != report.Id && r.ActionStatus != "Pending")
                             .OrderByDescending(r => r.Id).Select(r => r.ResultHash).FirstOrDefaultAsync() == report.ResultHash)
                {
                    report.ActionStatus = "Unchanged";
                    report.ActionDetail = "The results are the same as the previous run, so nothing was created (only act when results differ).";
                }
                else
                {
                    var o = await creator.ExecuteAsync(t, report, result, dryRun: test);
                    var summary = o.Summary(formName, test);
                    notified.AddRange(o.Notified);
                    if (o.Failed > 0) failure = summary;
                    else
                    {
                        report.ActionStatus = test ? (o.WouldFail > 0 ? "Failed" : "Test") : o.Created + o.Already > 0 ? "Created" : "Skipped";
                        report.ActionDetail = summary;
                    }
                }
            }
            catch (AlertFailure ex) { failure = ex.Message; }
            catch (Exception ex) when (ex is not OperationCanceledException) { failure = "The create-record action failed: " + ex.GetBaseException().Message; }
        }
        else if (failure == null)
        {
            try
            {
                if (test)
                {
                    var (to, skipped) = ResolveRecipients(t, app!);
                    Render(t.Subject, t, app!, view!, report, url);
                    Render(t.Body, t, app!, view!, report, url);
                    report.ActionStatus = "Test";
                    report.ActionDetail = report.GroupCount == 0
                        ? "Test run: no group reached the minimum, so nothing would be sent."
                        : $"Test run: would notify {string.Join(", ", to.Select(dir.Name))}." + (skipped.Count > 0 ? " " + string.Join(" ", skipped) : "");
                }
                else if (report.GroupCount == 0)
                {
                    report.ActionStatus = "NoMatches";
                    report.ActionDetail = "No group reached the minimum. Nothing was sent.";
                }
                else if (t.OnlyWhenChanged && await db.Set<InsightsAlertReport>().Where(r => r.TemplateId == t.Id && !r.IsTest && r.Id != report.Id && r.ActionStatus != "Pending")
                             .OrderByDescending(r => r.Id).Select(r => r.ResultHash).FirstOrDefaultAsync() == report.ResultHash)
                {
                    report.ActionStatus = "Unchanged";
                    report.ActionDetail = "The results are the same as the previous run, so nothing was sent (only act when results differ).";
                }
                else
                {
                    var (to, skipped) = ResolveRecipients(t, app!);
                    var subject = Render(t.Subject, t, app!, view!, report, url);
                    var body = Render(t.Body, t, app!, view!, report, url);
                    foreach (var uid in to) notify.Notify(uid, NotificationKind.Info, subject, body, null, url);
                    notified.AddRange(to);
                    report.ActionStatus = "Notified";
                    report.ActionDetail = $"Notified {to.Count} recipient(s): {string.Join(", ", to.Select(dir.Name))}." + (skipped.Count > 0 ? " " + string.Join(" ", skipped) : "");
                }
            }
            catch (AlertFailure ex) { failure = ex.Message; }
            catch (Exception ex) when (ex is not OperationCanceledException) { failure = "The notify action failed: " + ex.Message; }
        }

        if (failure != null)
        {
            report.ActionStatus = "Failed";
            report.ActionDetail = failure;
            if (!test)
            {
                // RPT-606 / SAF-020: never fail silently. Raise to the owner and every administrator.
                var raise = await db.Users.Where(u => u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity).Select(u => u.Id).ToListAsync();
                raise.Add(t.OwnerId);
                foreach (var uid in raise.Distinct())
                    notify.Notify(uid, NotificationKind.SafetyCritical, $"Alert action failed: {t.Name} ({t.Number})", failure, null, url);
                notified.AddRange(raise);
            }
        }
        report.NotifiedJson = JsonSerializer.Serialize(notified.Distinct().ToList());

        if (!retry)
        {
            t.LastRunAt = at;
            if (!test)
            {
                t.LastReportAt = at;
                if (t.IncrementDates && !manual)
                {
                    if (t.WindowStart is { } ws) t.WindowStart = ws.AddDays(t.FrequencyDays);
                    if (t.WindowEnd is { } we) t.WindowEnd = we.AddDays(t.FrequencyDays);
                }
            }
        }
        // The system log (Administration › Audit trail) records every outcome, failures included (INV-127, RPT-606).
        audit.Global(retry ? (failure != null ? "Alert action retry failed" : "Alert action retried") : failure != null ? "Alert action failed" : "Alert report generated",
            $"{report.Number} from {t.Number} \"{t.Name}\": {report.GroupCount} group(s), {report.RecordCount} record(s); action {report.ActionStatus}" +
            (failure != null ? $": {failure}" : report.ActionStatus is "Created" or "Skipped" ? $": {report.ActionDetail}" : "") + (test ? " (test report)" : ""));
        await db.SaveChangesAsync();
        return report;
    }

    private async Task<AlertResult> EvaluateAsOwnerAsync(InsightsAlertTemplate t, DateTime at)
    {
        var owner = dir.Get(t.OwnerId);
        if (owner == null || !owner.IsActive) throw new AlertFailure("The template owner's account isn't active, so the view can't be evaluated on their behalf. Assign the template to another owner.");
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<CurrentUser>().Impersonate(owner.Id, "System");
        var c = sp.GetRequiredService<ConfigCache>().Current;
        var access = sp.GetRequiredService<AccessService>();
        var views = sp.GetRequiredService<ViewService>();

        if (!c.AppsByKey.TryGetValue(t.AppKey, out var app) || app.Disabled) throw new AlertFailure($"The application '{t.AppKey}' no longer exists or is disabled.");
        if (!access.CanOpenApp(app.Id)) throw new AlertFailure($"The template owner ({owner.DisplayName}) no longer has access to {app.Name}.");
        var view = app.Views.FirstOrDefault(v => v.Key == t.ViewKey) ?? throw new AlertFailure($"The view '{t.ViewKey}' no longer exists in {app.Name}.");

        var match = JsonSerializer.Deserialize<List<string>>(t.MatchColumnsJson) ?? [];
        var extra = JsonSerializer.Deserialize<List<string>>(t.ReportColumnsJson) ?? [];
        if (match.Count == 0) throw new AlertFailure("The template has no match columns.");
        var forms = views.FormsOf(view);
        var cols = match.Select(k => Column(k, forms, true)).Concat(extra.Where(k => !match.Contains(k) && k is not ("number" or "title")).Select(k => Column(k, forms, false))).ToList();
        if (cols.Any(x => x == null)) throw new AlertFailure("A match or report column no longer exists in the view's forms.");

        var q = views.Query(view);
        if (t.LookbackDays is > 0) { var since = at.AddDays(-t.LookbackDays.Value); q = q.Where(r => r.CreatedAt >= since); }
        var records = await q.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(MaxRecords + 1).ToListAsync();
        var truncated = records.Count > MaxRecords;
        if (truncated) records.RemoveAt(records.Count - 1);

        var viewCols = cols.Select(x => x!.Value.Col).ToList();
        var rows = await views.BuildRowsAsync(records, viewCols);
        var result = new AlertResult
        {
            Evaluated = records.Count, Truncated = truncated,
            Columns = cols.Select(x => new AlertColumn(x!.Value.Col.Key, x.Value.Col.Label, x.Value.Match)).ToList(),
        };
        string Text(ViewCell cell) => cell.UserIds.Count > 0 ? string.Join("; ", cell.UserIds.Select(dir.Name)) : cell.Text;
        var groups = rows
            .Select(r => (Row: r, Key: match.Select(k => Text(r.Cells[k]).Trim()).ToList()))
            .Where(x => x.Key.All(v => v.Length > 0 && v != "Restricted"))
            .GroupBy(x => string.Join("\u001f", x.Key))
            .Where(g => g.Count() >= Math.Max(1, t.MinMatches))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture);
        foreach (var g in groups)
            result.Groups.Add(new AlertGroup
            {
                Values = g.First().Key, Count = g.Count(),
                Records = g.Select(x => new AlertRecord
                {
                    Id = x.Row.Record.Id, Number = x.Row.Record.Number, Title = x.Row.Record.Title,
                    Cells = viewCols.ToDictionary(vc => vc.Key, vc => Text(x.Row.Cells[vc.Key])),
                }).ToList(),
            });
        return result;
    }

    private static (ViewColumn Col, bool Match)? Column(string key, List<FormDefinition> forms, bool match)
    {
        if (ViewService.SystemColumns.TryGetValue(key, out var label)) return (new ViewColumn(key, label, false, null), match);
        var f = forms.SelectMany(x => x.Fields).FirstOrDefault(x => x.Key == key);
        return f == null ? null : (new ViewColumn(key, f.Label, false, f), match);
    }

    /// <summary>RPT-602: inactive users and groups are skipped; a recipient without an email address is replaced by their reports-to manager.</summary>
    private (List<int> To, List<string> Notes) ResolveRecipients(InsightsAlertTemplate t, AppDefinition app)
    {
        var to = AlertRecipients.Parse(t.RecipientsJson);
        var cc = AlertRecipients.Parse(t.CcJson);
        var notes = new List<string>();
        var ids = new List<int>(to.Users.Concat(cc.Users));
        foreach (var g in to.Groups.Concat(cc.Groups))
        {
            var group = dir.Group(g);
            if (group == null || group.Inactive) { notes.Add($"Group '{g}' is inactive or missing and was skipped."); continue; }
            ids.AddRange(dir.ActiveMembers(g).Select(u => u.Id));
        }
        var result = new List<int>();
        var form = InsightsQueries.MainForms(app).FirstOrDefault();
        using var scope = scopes.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<AccessService>();
        foreach (var id in ids.Distinct())
        {
            var u = dir.Get(id);
            if (u == null || !u.IsActive) { notes.Add($"{u?.DisplayName ?? "A recipient"} isn't active and was skipped."); continue; }
            var target = u;
            if (string.IsNullOrWhiteSpace(u.Email))
            {
                var mgr = dir.Get(u.ReportsToId);
                if (mgr is not { IsActive: true }) { notes.Add($"{u.DisplayName} has no email address and no active manager, so was skipped."); continue; }
                notes.Add($"{u.DisplayName} has no email address; their manager {mgr.DisplayName} was notified instead.");
                target = mgr;
            }
            if (form != null && !access.UserCanReadForm(target.Id, form)) { notes.Add($"{target.DisplayName} can't open {app.Name} and was skipped."); continue; }
            result.Add(target.Id);
        }
        if (result.Count == 0)
            throw new AlertFailure("No active recipient with access to " + app.Name + " could be notified." + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""));
        return (result.Distinct().ToList(), notes);
    }

    private static string Render(string template, InsightsAlertTemplate t, AppDefinition app, ViewDefinition view, InsightsAlertReport r, string url)
    {
        var unknown = UnknownPlaceholders(template);
        if (unknown.Count > 0) throw new AlertFailure($"The message uses unknown placeholder(s) {string.Join(", ", unknown.Select(u => "{" + u + "}"))}.");
        return PlaceholderRx().Replace(template, m => m.Groups[1].Value switch
        {
            "name" => t.Name, "number" => t.Number, "app" => app.Name, "view" => view.Name,
            "groups" => r.GroupCount.ToString(), "records" => r.RecordCount.ToString(), "minimum" => t.MinMatches.ToString(),
            "date" => r.GeneratedAt.ToString("d MMM yyyy HH:mm 'UTC'"), "link" => url, _ => m.Value,
        });
    }

    private static string Hash(AlertResult r)
    {
        var s = string.Join("|", r.Groups.Select(g => string.Join("\u001f", g.Values) + ":" + string.Join(",", g.Records.Select(x => x.Id).Order())));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }
}

/// <summary>RPT-604: evaluates enabled alert templates whose interval has passed while today is inside their window.</summary>
public sealed class InsightsAlertJob(VellumDbContext db, AlertEngine engine) : IScheduledJob
{
    public string Key => "insights-alerts";
    public string Name => "Insights threshold alerts";
    public string Description => "Evaluates enabled alert templates, produces alert reports, and notifies recipients or creates records. Failed actions are raised to the owner and administrators.";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        await InsightsSchema.EnsureAsync(db);
        var now = DateTime.UtcNow;
        var templates = await db.Set<InsightsAlertTemplate>().AsNoTracking().Where(t => t.Enabled).ToListAsync(ct);
        var due = templates.Where(t => AlertEngine.IsDue(t, now)).ToList();
        var failed = 0;
        foreach (var t in due)
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            var r = await engine.RunAsync(t.Id, manual: false, test: false, now);
            if (r.ActionStatus == "Failed") failed++;
            ctx.Log($"{t.Number} {t.Name}: {r.Number} · {r.GroupCount} group(s) · {r.ActionStatus}{(r.ActionStatus == "Failed" ? " · " + r.ActionDetail : "")}");
        }
        return new JobResult(due.Count, $"Evaluated {due.Count} of {templates.Count} enabled alert template(s); {failed} action failure(s) raised.");
    }
}
