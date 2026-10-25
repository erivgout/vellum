using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;

namespace Vellum.Modules.Insights;

public sealed class AlertFieldValue
{
    public string Field { get; set; } = "";
    /// <summary>A token template (<see cref="AlertTemplate"/>), e.g. <c>Recurring complaints: {group}</c>.</summary>
    public string? Value { get; set; }
}

/// <summary>RPT-603: the create-record action of an alert template, stored as JSON on the template.</summary>
public sealed class AlertCreateConfig
{
    public string? App { get; set; }
    public string? Form { get; set; }
    /// <summary>Starting phase as "workflowKey/phaseKey" (only phases a record can be created in).</summary>
    public string? Phase { get; set; }
    /// <summary>group (one record per group, the default) | report (one per report) | record (one per matched record).</summary>
    public string Per { get; set; } = "group";
    /// <summary>Due in N business days (BusinessCalendar). Blank = the starting phase's own due-date rule.</summary>
    public int? DueDays { get; set; }
    public List<int> Users { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public List<int> NotifyUsers { get; set; } = [];
    public List<string> NotifyGroups { get; set; } = [];
    public List<AlertFieldValue> Fields { get; set; } = [];
    /// <summary>Don't create another record for an item while the one this alert created for it earlier is still open.</summary>
    public bool SkipWhileOpen { get; set; } = true;

    public static AlertCreateConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AlertCreateConfig();
        try { return JsonSerializer.Deserialize<AlertCreateConfig>(json, AlertRecipients.JsonOpts) ?? new AlertCreateConfig(); }
        catch (JsonException) { return new AlertCreateConfig(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, AlertRecipients.JsonOpts);

    public static readonly (string Key, string Label, string Help)[] PerOptions =
    [
        ("group", "One per group", "For example one CAPA per product that reached the minimum."),
        ("report", "One per report", "A single record covering every group in the report."),
        ("record", "One per matched record", "A record for each matching record."),
    ];
}

/// <summary>What was (or, in a test report, would be) created for one item: shown on the alert report.</summary>
public sealed class AlertCreationDetail
{
    public string App { get; set; } = "";
    public string Form { get; set; } = "";
    public string? Phase { get; set; }
    public DateOnly? Due { get; set; }
    public string? Title { get; set; }
    public List<string> Assignees { get; set; } = [];
    public List<string> Notify { get; set; } = [];
    public List<AlertDetailField> Fields { get; set; } = [];
    public List<string> Sources { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

/// <param name="Origin">alert (set by the action) | form (a default or computed by the form's rules)</param>
public sealed record AlertDetailField(string Key, string Label, string Value, bool Required, string Origin);

/// <summary>Pure rules for the create-record action: which forms, phases and fields qualify, and what is required (INV-127).</summary>
public static class AlertCreateRules
{
    /// <summary>Forms an alert can create: enabled workflow forms people can create from New (activities need a parent record).</summary>
    public static IEnumerable<FormDefinition> CreatableForms(AppDefinition app) =>
        app.Forms.Where(f => !f.Disabled && !f.IsActivity && !f.IsNonWorkflow && !f.HiddenFromNew && StartPhases(f).Count > 0).OrderBy(f => f.Order);

    /// <summary>RPT-603 "only enabled phases": starting phases of the form's enabled workflows.</summary>
    public static List<(WorkflowDefinition Workflow, PhaseDefinition Phase)> StartPhases(FormDefinition form) =>
        form.Workflows.Where(w => !w.Disabled)
            .SelectMany(w => w.Phases.Where(p => p.IsStart && p.Type == PhaseType.Normal).OrderBy(p => p.Order).Select(p => (w, p))).ToList();

    public static string PhaseValue(WorkflowDefinition w, PhaseDefinition p) => $"{w.Key}/{p.Key}";

    public static (WorkflowDefinition Workflow, PhaseDefinition Phase)? FindPhase(FormDefinition form, string? value)
    {
        var phases = StartPhases(form);
        if (string.IsNullOrEmpty(value)) return phases.Count > 0 ? phases[0] : null;
        var hit = phases.FirstOrDefault(x => PhaseValue(x.Workflow, x.Phase) == value);
        return hit.Phase == null ? null : hit;
    }

    public static bool IsMainField(FormDefinition form, FieldDefinition f) =>
        form.Sections.FirstOrDefault(s => s.Key == f.SectionKey) is not { IsSubform: true };

    /// <summary>Fields an alert can set: main-form fields that store values and aren't computed by the form.</summary>
    public static bool Settable(FormDefinition form, FieldDefinition f) =>
        IsMainField(form, f) && FieldCodec.StoresValues(f.Type) && !f.ReadOnly;

    /// <summary>INV-127: required fields of the form, plus those required in the starting phase.</summary>
    public static bool IsRequired(FieldDefinition f, PhaseDefinition? start) =>
        f.Type != FieldType.Prompt && (f.Required || (start != null && f.RequiredInPhases.Contains(start.Key)));

    public static List<FieldDefinition> RequiredFields(FormDefinition form, PhaseDefinition? start) =>
        form.Fields.Where(f => IsMainField(form, f) && IsRequired(f, start)).OrderBy(f => f.Order).ToList();

    /// <summary>The default rule that applies when an alert creates the record. "$me" never applies: alerts run as the system identity.</summary>
    public static string? EffectiveDefault(FieldDefinition f) => f.DefaultValue is null or "$me" ? null : f.DefaultValue;

    /// <summary>The form's default rules (PLT-402) as they apply to a record created by the system identity.</summary>
    public static void ApplyDefaults(FormDefinition form, ValueBag data, ConfigModel cfg)
    {
        foreach (var f in form.Fields.Where(f => IsMainField(form, f) && FieldCodec.StoresValues(f.Type) && EffectiveDefault(f) != null && !data.Has(f.Key)))
        {
            var dv = EffectiveDefault(f)!;
            try
            {
                object value = dv switch
                {
                    "$today" => BusinessCalendar.Today,
                    "$now" => DateTime.UtcNow,
                    _ when dv.StartsWith("$today+", StringComparison.Ordinal) => BusinessCalendar.Today.AddDays(int.Parse(dv[7..], CultureInfo.InvariantCulture)),
                    _ => dv,
                };
                data.Set(f.Key, FieldCodec.FromObject(f, value, cfg));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException) { /* a broken default counts as unset */ }
        }
    }

    /// <summary>INV-127: required fields that are shown (PLT-506) but have no value. Attachments can never be set by an alert.</summary>
    public static List<FieldDefinition> Missing(FormDefinition form, PhaseDefinition? start, ValueBag data, ConfigModel cfg, ISet<string>? attachmentFields = null) =>
        RequiredFields(form, start)
            .Where(f => FormRules.IsFieldVisible(f, data, form, cfg))
            .Where(f => f.Type == FieldType.Attachment ? attachmentFields?.Contains(f.Key) != true : !data.Has(f.Key))
            .ToList();

    public static string FieldList(IEnumerable<FieldDefinition> fields) => string.Join(", ", fields.Select(f => f.Label));
}

/// <summary>
/// Carries out an alert's create-record action (RPT-603) for each item of a report, as the system identity with the
/// audit naming the template. Every attempt validates the target form's required fields first and again on the created
/// record inside the same transaction, so a record is either complete or not created at all and the attempt is recorded
/// as a visible failure (INV-127, RPT-606). Created rows are unique per report and item (idempotent retries). A dry run
/// (test report, RPT-605) takes the same path inside a transaction that is always rolled back: nothing is stored,
/// numbered, linked or sent.
/// </summary>
[ScopedService]
public sealed class AlertCreateAction(IServiceScopeFactory scopes, VellumDbContext db, ConfigCache cfgCache, UserDirectory dir)
{
    public const int MaxLinks = 200;

    public sealed record Item(string Key, string Label, AlertGroup Group, List<AlertRecord> Records);

    public sealed class Outcome
    {
        public int Created, Already, Skipped, Failed, Previewed, WouldFail;
        public List<string> CreatedLines { get; } = [];
        public List<string> FailedLines { get; } = [];
        public List<string> SkippedLines { get; } = [];
        public HashSet<int> Notified { get; } = [];

        public string Summary(string formName, bool test)
        {
            var parts = new List<string>();
            if (test)
            {
                if (Previewed > 0) parts.Add($"Test run: would create {Previewed} {formName} record(s).");
                if (WouldFail > 0) parts.Add($"{WouldFail} would fail: {string.Join(" ", FailedLines)}");
                if (Skipped > 0) parts.Add($"{Skipped} would be skipped: {string.Join(" ", SkippedLines)}");
                return parts.Count == 0 ? "Test run: nothing would be created." : string.Join(" ", parts);
            }
            if (Created + Already > 0) parts.Add($"Created {Created + Already} {formName} record(s): {string.Join("; ", CreatedLines)}.");
            if (Skipped > 0) parts.Add($"Skipped {Skipped}: {string.Join(" ", SkippedLines)}");
            if (Failed > 0) parts.Add($"{Failed} couldn't be created, so nothing was created for {(Failed == 1 ? "it" : "them")}: {string.Join(" ", FailedLines)}");
            return parts.Count == 0 ? "Nothing was created." : string.Join(" ", parts);
        }
    }

    public static AlertCreateConfig ConfigOf(InsightsAlertTemplate t) => AlertCreateConfig.Parse(t.CreateJson);

    /// <summary>The items a report produces records for, depending on the "create one record per" option.</summary>
    public static List<Item> Items(AlertCreateConfig c, AlertResult result)
    {
        switch (c.Per)
        {
            case "report":
                if (result.Groups.Count == 0) return [];
                var all = result.Groups.SelectMany(g => g.Records).DistinctBy(r => r.Id).ToList();
                var merged = new AlertGroup { Values = result.Groups[0].Values, Count = all.Count, Records = all };
                return [new Item("report", string.Join("; ", result.Groups.Select(g => string.Join(" / ", g.Values))), merged, all)];
            case "record":
                return result.Groups.SelectMany(g => g.Records.Select(r => (g, r))).DistinctBy(x => x.r.Id)
                    .Select(x => new Item("r:" + x.r.Id.ToString("N"), $"{x.r.Number} {x.r.Title}".Trim(), x.g, [x.r])).ToList();
            default:
                return result.Groups.Select(g => new Item(GroupKey(g.Values), string.Join(" / ", g.Values), g, g.Records)).ToList();
        }
    }

    public static string GroupKey(IEnumerable<string> values) =>
        "g:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", values).ToLowerInvariant())))[..40];

    /// <summary>
    /// Run the action for every item of the report. Template-level problems (target gone, owner without rights) throw
    /// <see cref="AlertFailure"/>; item-level problems are recorded per item. Created rows are committed with their record;
    /// other rows are added to this context for the caller to save with the report.
    /// </summary>
    public async Task<Outcome> ExecuteAsync(InsightsAlertTemplate t, InsightsAlertReport report, AlertResult result, bool dryRun)
    {
        var c = ConfigOf(t);
        var cfg = cfgCache.Current;
        if (c.Form == null || !cfg.FormsByKey.TryGetValue(c.Form, out var form) || form.App.Key != c.App || form.Disabled || form.App.Disabled)
            throw new AlertFailure($"The target form '{c.App}/{c.Form}' no longer exists or is disabled, so no record could be created.");
        var phase = AlertCreateRules.FindPhase(form, c.Phase)
            ?? throw new AlertFailure($"The starting phase '{c.Phase}' is no longer a starting phase of {form.Name}, so no record could be created.");
        CheckOwner(t, form, c);

        var outcome = new Outcome();
        var items = Items(c, result);
        var done = await db.Set<InsightsAlertCreation>().AsNoTracking()
            .Where(x => x.ReportId == report.Id && x.Status == "Created").ToListAsync();
        foreach (var item in items)
        {
            if (!dryRun && done.FirstOrDefault(d => d.ItemKey == item.Key) is { } prior)
            {
                outcome.Already++;
                outcome.CreatedLines.Add($"{prior.RecordNumber} for {item.Label}");
                continue;
            }
            if (c.SkipWhileOpen && await OpenFromEarlierAsync(t, report, item.Key) is { } open)
            {
                var line = $"{open.Number} (created for {item.Label} by an earlier report) is still open.";
                outcome.Skipped++;
                outcome.SkippedLines.Add(line);
                db.Set<InsightsAlertCreation>().Add(Row(t, report, item, "Skipped", line, null));
                continue;
            }
            var row = await AttemptAsync(t, report, result, c, form, phase.Workflow, phase.Phase, item, dryRun, outcome.Notified);
            switch (row.Status)
            {
                case "Created":
                    outcome.Created++;
                    outcome.CreatedLines.Add($"{row.RecordNumber} for {item.Label}");
                    break; // committed with the record
                case "Preview":
                    outcome.Previewed++;
                    db.Set<InsightsAlertCreation>().Add(row);
                    break;
                default:
                    if (dryRun) outcome.WouldFail++; else outcome.Failed++;
                    outcome.FailedLines.Add($"{item.Label}: {row.Message}");
                    db.Set<InsightsAlertCreation>().Add(row);
                    break;
            }
        }
        return outcome;
    }

    private void CheckOwner(InsightsAlertTemplate t, FormDefinition form, AlertCreateConfig c)
    {
        var owner = dir.Get(t.OwnerId);
        if (owner is not { IsActive: true }) throw new AlertFailure("The template owner's account isn't active, so records can't be created on their behalf.");
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Impersonate(owner.Id, "System");
        var access = scope.ServiceProvider.GetRequiredService<AccessService>();
        if (!access.CanCreate(form)) throw new AlertFailure($"The template owner ({owner.DisplayName}) can no longer create {form.Name} records, so none was created.");
        // A token may not copy a sensitive value the owner can't read into the new record (SEC-520).
        var cfg = cfgCache.Current;
        if (cfg.AppsByKey.TryGetValue(t.AppKey, out var watched) && !access.CanSeeSensitive(watched.Id))
        {
            var sensitive = watched.Forms.SelectMany(f => f.Fields).Where(f => f.Sensitive).Select(f => f.Key).ToHashSet();
            var leaks = c.Fields.SelectMany(fv => AlertTemplate.Parse(fv.Value).TokenNames)
                .Where(n => n.StartsWith("record.", StringComparison.Ordinal) && sensitive.Contains(n[7..])).Distinct().ToList();
            if (leaks.Count > 0) throw new AlertFailure($"The field values read restricted (sensitive) fields the owner can't see: {string.Join(", ", leaks.Select(l => "{" + l + "}"))}.");
        }
    }

    private async Task<Record?> OpenFromEarlierAsync(InsightsAlertTemplate t, InsightsAlertReport report, string itemKey)
    {
        var ids = await db.Set<InsightsAlertCreation>().AsNoTracking()
            .Where(x => x.TemplateId == t.Id && x.ItemKey == itemKey && x.Status == "Created" && x.ReportId != report.Id && x.RecordId != null)
            .Select(x => x.RecordId!.Value).ToListAsync();
        if (ids.Count == 0) return null;
        return await db.Records.AsNoTracking().Where(r => ids.Contains(r.Id) && r.PhaseType == PhaseType.Normal).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    }

    private static InsightsAlertCreation Row(InsightsAlertTemplate t, InsightsAlertReport report, Item item, string status, string? message, AlertCreationDetail? detail) => new()
    {
        ReportId = report.Id, TemplateId = t.Id, ItemKey = item.Key, ItemLabel = Clip(item.Label, 1000), Status = status, Message = message == null ? null : Clip(message, 4000),
        DetailJson = detail == null ? null : JsonSerializer.Serialize(detail, AlertRecipients.JsonOpts),
        SourcesJson = JsonSerializer.Serialize(item.Records.Select(r => r.Id).Take(MaxLinks)), At = DateTime.UtcNow,
    };

    private static string Clip(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    private async Task<InsightsAlertCreation> AttemptAsync(InsightsAlertTemplate t, InsightsAlertReport report, AlertResult result, AlertCreateConfig c,
        FormDefinition form, WorkflowDefinition wf, PhaseDefinition start, Item item, bool dryRun, HashSet<int> notified)
    {
        var failStatus = dryRun ? "PreviewFailed" : "Failed";
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<CurrentUser>().UseSystem($"Alert {t.Number} {t.Name}");
        var db2 = sp.GetRequiredService<VellumDbContext>();
        var records = sp.GetRequiredService<RecordService>();
        var calendar = sp.GetRequiredService<BusinessCalendar>();
        var assignments = sp.GetRequiredService<AssignmentService>();
        var access = sp.GetRequiredService<AccessService>();
        var cfg = cfgCache.Current;
        var detail = new AlertCreationDetail { App = form.App.Name, Form = form.Name, Phase = start.Name };
        detail.Sources = item.Records.Select(r => r.Number).Take(MaxLinks).ToList();

        // Due date: N business days from today (BusinessCalendar), or the starting phase's own rule.
        DateOnly? due = c.DueDays is { } n ? await calendar.AddAsync(BusinessCalendar.Today, n, businessDays: true) : await assignments.DefaultDueAsync(start, new ValueBag());

        // Field values from the token templates.
        var tokens = await TokenResolverAsync(records, t, report, result, item, due);
        var parser = new ValueParser(cfg, dir, (number, f) => RecordByNumber(db2, cfg, number, f));
        var values = new Dictionary<string, object?>();
        var errors = new List<string>();
        foreach (var fv in c.Fields.Where(x => !string.IsNullOrWhiteSpace(x.Field)))
        {
            var f = form.Fields.FirstOrDefault(x => x.Key == fv.Field);
            if (f == null || !AlertCreateRules.Settable(form, f)) { errors.Add($"{form.Name} has no field '{fv.Field}' an alert can set."); continue; }
            var (vals, err) = AlertTemplate.Render(fv.Value, f, tokens, parser);
            if (err != null) { errors.Add(err.StartsWith(f.Label, StringComparison.Ordinal) ? err : $"{f.Label}: {err}"); continue; }
            if (vals.Count > 0) values[f.Key] = vals;
        }

        // INV-127, before anything is created: every required field must get a value.
        var data = new ValueBag();
        foreach (var (k, v) in values) data.Set(k, (List<Val>)v!);
        AlertCreateRules.ApplyDefaults(form, data, cfg);
        var missing = AlertCreateRules.Missing(form, start, data, cfg);
        if (missing.Count > 0) errors.Add($"Required field{(missing.Count == 1 ? "" : "s")} of {form.Name} not set: {AlertCreateRules.FieldList(missing)} (INV-127).");
        if (c.DueDays == null && start.DueDateField is { } dueField && data.Date(dueField) is { } fromField) due = fromField;
        detail.Due = due;

        // Assignees: active users and the active members of active groups who can open the form (INV-073, INV-080).
        var assignees = ResolvePeople(c.Users, c.Groups, form, access, detail.Notes, "assign");
        if (assignees.Count == 0)
        {
            var probe = new RecordSnapshot { Record = new Record { FormId = form.Id, AppId = form.AppId, CreatedById = dir.SystemUserId, WorkflowId = wf.Id }, Form = form, Config = cfg, Data = new RecordData { Main = data } };
            var (_, defaults, _) = await assignments.CandidatesAsync(probe, start);
            assignees = defaults.Where(id => dir.Get(id) is { Assignable: true }).ToList();
            if (assignees.Count == 0) errors.Add($"Nobody can be assigned to {start.Name}: choose assignees for the action.");
        }
        detail.Assignees = assignees.Select(dir.Name).ToList();
        var notifyList = ResolvePeople(c.NotifyUsers, c.NotifyGroups, form, access, detail.Notes, "notify");
        detail.Notify = notifyList.Select(dir.Name).ToList();
        detail.Fields = DetailFields(form, start, data, values.Keys.ToHashSet(), cfg, null);

        if (errors.Count > 0) return Row(t, report, item, failStatus, string.Join(" ", errors), detail);

        await using var tx = await db2.Database.BeginTransactionAsync();
        try
        {
            var owner = dir.Get(t.OwnerId);
            var res = await records.CreateAsync(new CreateRequest
            {
                FormKey = form.Key, WorkflowKey = wf.Key, StartPhaseKey = start.Key, Values = values, Assignees = assignees,
                DueDate = c.DueDays != null ? due : null, LocationId = owner?.LocationId, SkipAccessCheck = true,
                CreateComment = $"Created by alert {t.Number} \"{t.Name}\" from report {report.Number} (RPT-603)",
            });
            if (!res.Ok)
            {
                await tx.RollbackAsync();
                return Row(t, report, item, failStatus, $"{form.Name} rejected the values: " + string.Join(" ", res.Errors.Select(e => e.Message).Distinct()), detail);
            }

            // INV-127, after the form's own rules ran: verify the stored record before committing it.
            var snap = await records.LoadRequiredAsync(res.Record!.Id);
            var after = AlertCreateRules.Missing(form, start, snap.Data.Main, cfg, snap.Attachments.Where(a => !a.Removed).Select(a => a.FieldKey).ToHashSet());
            if (after.Count > 0)
            {
                await tx.RollbackAsync();
                return Row(t, report, item, failStatus, $"After {form.Name}'s rules ran, required field{(after.Count == 1 ? "" : "s")} {AlertCreateRules.FieldList(after)} had no value, so the record wasn't kept (INV-127).", detail);
            }
            detail.Title = snap.Record.Title;
            detail.Due = snap.Record.DueDate;
            detail.Phase = snap.Phase?.Name;
            var assignedIds = await db2.Assignments.Where(a => a.RecordId == snap.Record.Id && a.Status != AssignmentStatus.Cancelled).OrderBy(a => a.Order).Select(a => a.UserId).ToListAsync();
            detail.Assignees = assignedIds.Select(dir.Name).ToList();
            detail.Fields = DetailFields(form, start, snap.Data.Main, values.Keys.ToHashSet(), cfg, await records.LinkLabelsAsync(snap.Data.Main.Keys.SelectMany(k => snap.Data.Main[k])));

            if (dryRun)
            {
                await tx.RollbackAsync();
                var preview = Row(t, report, item, "Preview", null, detail);
                preview.RecordTitle = Clip(snap.Record.Title, 1000);
                return preview;
            }

            // Link the new record to the matched source records (both directions are listed on the record page).
            var sourceIds = item.Records.Select(r => r.Id).Distinct().Take(MaxLinks).ToList();
            var existing = await db2.Records.Where(r => sourceIds.Contains(r.Id)).Select(r => r.Id).ToListAsync();
            foreach (var sid in sourceIds.Where(existing.Contains))
                await records.LinkAsync(snap.Record.Id, sid, LinkKind.Related);
            var url = $"/insights/alerts/reports/{report.Id}";
            await records.AddCommentAsync(snap.Record.Id,
                $"Created by alert {t.Number} \"{t.Name}\" (report {report.Number}, {url}) for {item.Label}: {item.Group.Count} matching record(s), " +
                string.Join(", ", item.Records.Take(20).Select(r => r.Number)) + (item.Records.Count > 20 ? ", …" : "") + ".", "system", system: true);

            var notify = sp.GetRequiredService<NotificationService>();
            foreach (var uid in notifyList)
                notify.Notify(uid, NotificationKind.Info, $"{form.Name} {snap.Record.Number} created by alert {t.Name}",
                    $"{snap.Record.Title}. Alert {t.Number} created it for {item.Label} ({item.Group.Count} matching record(s)). Report: {url}", snap.Record.Id);

            var row = Row(t, report, item, "Created", null, detail);
            row.RecordId = snap.Record.Id;
            row.RecordNumber = snap.Record.Number;
            row.RecordTitle = Clip(snap.Record.Title, 1000);
            db2.Set<InsightsAlertCreation>().Add(row);
            await db2.SaveChangesAsync();
            await tx.CommitAsync();
            foreach (var uid in notifyList) notified.Add(uid);
            return row;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { await tx.RollbackAsync(); } catch (InvalidOperationException) { /* already rolled back */ }
            // A concurrent run may have created this item first: the unique index refused the duplicate.
            if (!dryRun && ex is DbUpdateException &&
                await db.Set<InsightsAlertCreation>().AsNoTracking().FirstOrDefaultAsync(x => x.ReportId == report.Id && x.ItemKey == item.Key && x.Status == "Created") is { } winner)
                return winner;
            var message = ex is WorkflowException or AlertFailure ? ex.Message : $"The record couldn't be created: {ex.GetBaseException().Message}";
            return Row(t, report, item, failStatus, message, detail);
        }
    }

    private List<int> ResolvePeople(List<int> users, List<string> groups, FormDefinition form, AccessService access, List<string> notes, string purpose)
    {
        var ids = new List<int>(users);
        foreach (var g in groups.Distinct())
        {
            var group = dir.Group(g);
            if (group is not { Inactive: false }) { notes.Add($"Group '{group?.Name ?? g}' is inactive or missing and was skipped."); continue; }
            ids.AddRange(dir.ActiveMembers(g).Select(u => u.Id));
        }
        var result = new List<int>();
        foreach (var id in ids.Distinct())
        {
            var u = dir.Get(id);
            if (u is not { Assignable: true, IsSystem: false }) { notes.Add($"{u?.DisplayName ?? "A selected person"} isn't active and wasn't {(purpose == "assign" ? "assigned" : "notified")}."); continue; }
            if (!access.UserCanReadForm(u.Id, form)) { notes.Add($"{u.DisplayName} can't open {form.Name} records and wasn't {(purpose == "assign" ? "assigned" : "notified")}."); continue; }
            result.Add(u.Id);
        }
        return result;
    }

    private List<AlertDetailField> DetailFields(FormDefinition form, PhaseDefinition start, ValueBag data, HashSet<string> fromAlert, ConfigModel cfg, IReadOnlyDictionary<Guid, string>? links)
    {
        var shown = form.Fields.Where(f => AlertCreateRules.IsMainField(form, f) && FieldCodec.StoresValues(f.Type) && (data.Has(f.Key) || AlertCreateRules.IsRequired(f, start)))
            .OrderBy(f => !AlertCreateRules.IsRequired(f, start)).ThenBy(f => f.Order);
        return shown.Select(f => new AlertDetailField(f.Key, f.Label, FieldCodec.Display(f, data[f.Key], cfg, dir, TimeZoneInfo.Utc, links),
            AlertCreateRules.IsRequired(f, start), fromAlert.Contains(f.Key) ? "alert" : "form")).ToList();
    }

    private static Guid? RecordByNumber(VellumDbContext db2, ConfigModel cfg, string number, FieldDefinition f)
    {
        var formIds = f.LinkForms.Select(k => cfg.FormsByKey.GetValueOrDefault(k)?.Id).OfType<int>().ToList();
        var q = db2.Records.AsNoTracking().Where(r => r.Number == number && !r.IsArchived);
        if (formIds.Count > 0) q = q.Where(r => formIds.Contains(r.FormId));
        return q.OrderByDescending(r => r.CreatedAt).Select(r => (Guid?)r.Id).FirstOrDefault();
    }

    /// <summary>Resolves tokens for one item. Field values of the representative (most recent) matched record are read on demand.</summary>
    private async Task<Func<string, AlertTemplate.Value>> TokenResolverAsync(RecordService records, InsightsAlertTemplate t, InsightsAlertReport report,
        AlertResult result, Item item, DateOnly? due)
    {
        var cfg = cfgCache.Current;
        var match = JsonSerializer.Deserialize<List<string>>(t.MatchColumnsJson) ?? [];
        var first = item.Records.FirstOrDefault();
        var snap = first == null ? null : await records.LoadAsync(first.Id);
        var linkLabels = snap == null ? new Dictionary<Guid, string>() : await records.LinkLabelsAsync(snap.Data.Main.Keys.SelectMany(k => snap.Data.Main[k]));
        static string D(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        AlertTemplate.Value DateValue(DateOnly? d) => d is { } x ? new(D(x), FieldType.Date, [Val.Of(x)]) : AlertTemplate.Value.Empty;
        AlertTemplate.Value Number(int n) => new(n.ToString(CultureInfo.InvariantCulture), FieldType.Number, [Val.Of(n)]);

        return token =>
        {
            if (AlertTemplate.TodayOffset(token) is { } off) return DateValue(BusinessCalendar.Today.AddDays(off));
            switch (token)
            {
                case "due": return DateValue(due);
                case "count": return Number(item.Group.Count);
                case "groups": return Number(result.Groups.Count);
                case "minimum": return Number(t.MinMatches);
                case "group": return new(item.Key == "report" ? item.Label : string.Join(" / ", item.Group.Values));
                case "record": return first == null ? AlertTemplate.Value.Empty : new(first.Number, FieldType.Link, [Val.Link(first.Id)]);
                case "records": return new(string.Join(", ", item.Records.Select(r => r.Number)), FieldType.Link, item.Records.Select(r => Val.Link(r.Id)).ToList());
                case "alert.name": return new(t.Name);
                case "alert.number": return new(t.Number);
                case "report.number": return new(report.Number);
                case "report.link": return new($"/insights/alerts/reports/{report.Id}");
            }
            if (token.StartsWith("group.", StringComparison.Ordinal))
            {
                var i = match.IndexOf(token[6..]);
                return i >= 0 && i < item.Group.Values.Count ? new(item.Group.Values[i]) : AlertTemplate.Value.Empty;
            }
            if (token.StartsWith("record.", StringComparison.Ordinal) && snap != null)
            {
                var key = token[7..];
                var r = snap.Record;
                switch (key)
                {
                    case "number": return new(r.Number);
                    case "title": return new(r.Title);
                    case "phase": return new(cfg.Phase(r.PhaseId)?.Name ?? "");
                    case "created": return DateValue(DateOnly.FromDateTime(r.CreatedAt));
                    case "due": return DateValue(r.DueDate);
                    case "creator": return dir.Get(r.CreatedById) is { IsSystem: false } u ? new(u.DisplayName, FieldType.User, [Val.Ref(u.Id)]) : AlertTemplate.Value.Empty;
                }
                var f = snap.Form.Fields.FirstOrDefault(x => x.Key == key);
                if (f == null || !FieldCodec.StoresValues(f.Type)) return AlertTemplate.Value.Empty;
                var vals = snap.Data.Main[key];
                var text = FieldCodec.Display(f, vals, cfg, dir, TimeZoneInfo.Utc, linkLabels);
                return new(text, f.Type, vals);
            }
            return AlertTemplate.Value.Empty;
        };
    }
}
