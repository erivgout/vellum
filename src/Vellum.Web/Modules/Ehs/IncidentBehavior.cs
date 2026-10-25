using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Modules.Kit;

namespace Vellum.Modules.Ehs;

/// <summary>
/// Incident logic: OSHA severe-injury clocks (EHS-114, SAF-020/023), DART calculation, recordkeeping checks at close,
/// corrective actions → Action Items (EHS-104), and protection of sensitive personal fields (SEC-520).
/// </summary>
public sealed class IncidentBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["incident"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        var access = ctx.Services.GetRequiredService<AccessService>();

        // SEC-520: people who can't see sensitive fields can't change (or accidentally clear) them either.
        if (!ctx.IsNew && !access.CanSeeSensitive(ctx.Form.AppId))
            foreach (var f in ctx.Form.Fields.Where(f => f.Sensitive))
                main.Set(f.Key, ctx.Current!.Data.Main[f.Key]);

        var occurred = main.DateTime("occurred_at") is { } o ? ModuleKit.Utc(o) : (DateTime?)null;
        if (occurred > DateTime.UtcNow.AddMinutes(10))
            ctx.Error("occurred_at", "The date and time it happened can't be in the future.");

        // A fatality always starts the 8-hour clock, even if the reporter missed the severe-outcome field.
        if (ctx.KeywordKey("outcome") == "fatality" && ctx.KeywordKey("osha_severe_event") == null)
            ctx.Set("osha_severe_event", "fatality");

        // EHS-114 / SAF-023: the deadline is calculated from the event time and shows the rule that produced it (SAF-024).
        var rule = OshaClock.RuleFor(ctx.KeywordKey("osha_severe_event"));
        if (rule != null && occurred is { } at)
        {
            ctx.Set("osha_deadline", OshaClock.Deadline(at, rule));
            ctx.Set("osha_rule", rule.Description);
        }
        else
        {
            ctx.Set("osha_deadline", null);
            ctx.Set("osha_rule", null);
        }
        if (main.DateTime("osha_reported_at") is { } rep && occurred is { } ev && ModuleKit.Utc(rep) < ev)
            ctx.Error("osha_reported_at", "The OSHA report can't be made before the event happened.");

        // OSHA recordkeeping: DART is calculated, never typed (RPT-702 counts it).
        var recordable = main.Bool("recordable") == true;
        var cls = ctx.KeywordKey("osha_classification");
        var daysAway = main.Number("days_away") ?? 0;
        var daysRestricted = main.Number("days_restricted") ?? 0;
        if (recordable && cls == "other_recordable" && (daysAway > 0 || daysRestricted > 0))
            ctx.Error("osha_classification", "A case with days away or restricted days isn't \"Other recordable\": classify it as days away or job transfer/restriction.");
        if (recordable && cls == "death" && daysRestricted > 0)
            ctx.Error("days_restricted", "A death case has no restricted days.");
        ctx.Set("dart", OshaRates.IsDart(recordable, cls, daysAway, daysRestricted));

        if (occurred is { } when)
            ctx.Set("in_current_year", Sites.LocalDate(when, ctx.KeywordKey("site")).Year == Sites.Today.Year);

        var costs = ctx.Proposed.RowsOf("costs").Select(r => r.Values.Number("amount") ?? 0).ToList();
        ctx.Set("total_cost", costs.Count > 0 ? costs.Sum() : null);
        return Task.CompletedTask;
    }

    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        var snap = ctx.Snapshot;
        var occurred = snap.DateTime("occurred_at") is { } o ? ModuleKit.Utc(o) : (DateTime?)null;

        // SAF-023: late entries are flagged in the audit trail.
        if (occurred is { } at && snap.Record.CreatedAt - at > OshaClock.LateEntryThreshold)
            ctx.Get<AuditService>().Record(snap.Record.Id, "Late entry flagged",
                $"Reported {ModuleKit.Span(snap.Record.CreatedAt - at)} after the event (threshold {OshaClock.LateEntryThreshold.TotalHours:0} hours, SAF-023).");

        if (OshaClock.RuleFor(snap.KeywordKey("osha_severe_event")) != null)
            await IncidentAlerts.SendClockStartedAsync(ctx.Services, snap);
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        // Anonymous reports have no reporter to answer for them, so a named EHS team member must accept one before it moves on.
        if (IsAnonymous(s) && ctx.To.Type != PhaseType.Voided)
        {
            if (ctx.To.Key == "report") ctx.Error("This report was made anonymously, so there's no reporter to return it to. Add what you know in the investigation instead.");
            else if (s.UserId("anon_accepted_by") == null) ctx.Error("Accept this anonymous report first (\"Accept anonymous report\"), so a named EHS team member owns it.");
        }
        if (ctx.To.Key != "closed") return Task.CompletedTask;

        if (OshaClock.RuleFor(s.KeywordKey("osha_severe_event")) is { } rule && s.DateTime("osha_reported_at") == null)
            ctx.Error($"Record the OSHA report first: a {rule.EventLabel.ToLowerInvariant()} must be reported within {rule.Hours} hours ({rule.Cite}). Use \"Record OSHA report\" on the clock panel.");

        if (s.Bool("recordable") == true)
        {
            var cls = s.KeywordKey("osha_classification");
            if (cls == null) ctx.Error("Choose the OSHA case classification (OSHA tab) for this recordable case.");
            if (s.KeywordKey("osha_case_type") == null) ctx.Error("Choose the injury or illness type (OSHA tab) for this recordable case.");
            if (cls == "days_away" && (s.Number("days_away") ?? 0) < 1) ctx.Error("A days-away case needs the number of days away from work.");
            if (cls == "restricted" && (s.Number("days_restricted") ?? 0) < 1) ctx.Error("A job transfer or restriction case needs the number of restricted days.");
            if (string.IsNullOrWhiteSpace(s.Text("injured_name")) || string.IsNullOrWhiteSpace(s.Text("injured_job_title")))
                ctx.Error("Recordable cases need the injured person's name and job title on the Injury tab for the OSHA 300 log.");
        }
        return Task.CompletedTask;
    }

    /// <summary>EHS-104: corrective actions become Action Items when the incident enters Corrective Actions.</summary>
    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "corrective_actions") return;
        var calendar = ctx.Get<BusinessCalendar>();
        var snap = ctx.Snapshot;
        var proposed = new RecordData();
        var updated = proposed.RowsOf("corrective_actions");
        var created = 0;
        foreach (var row in snap.Rows("corrective_actions"))
        {
            var clone = row.Clone();
            if (row.CreatedRecordId == null && row.Values.Text("action") is { } action && row.Values.Ref("assignee") is { } assignee)
            {
                var due = await calendar.AddAsync(BusinessCalendar.Today, (int)(row.Values.Number("days") ?? 10), true);
                var verify = row.Values.Bool("verify") == true;
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "action_item", StartPhaseKey = "open", ParentId = snap.Record.Id, SkipAccessCheck = true,
                    Assignees = [assignee], DueDate = due,
                    Values = new()
                    {
                        ["subject"] = action,
                        ["description"] = $"Corrective action from incident {snap.Record.Number}: {snap.Record.Title}",
                        ["assignee"] = assignee, ["due_date"] = due,
                        ["verification_required"] = verify,
                        ["verifier"] = verify ? ctx.Directory.ActiveMembers("ehs").Select(u => (int?)u.Id).FirstOrDefault(id => id != assignee) : null,
                    },
                    CreateComment = $"Created automatically from the corrective actions of {snap.Record.Number} (EHS-104)",
                });
                if (!res.Ok) throw new WorkflowException("Couldn't create an action item: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                clone.CreatedRecordId = res.Record!.Id;
                created++;
            }
            updated.Add(clone);
        }
        if (created > 0)
        {
            var fresh = await ctx.Records.LoadRequiredAsync(snap.Record.Id);
            await ctx.Records.SaveAsync(fresh, proposed, new SaveOptions { System = true });
        }
    }

    // ------------------------------------------------------------------ OSHA clock actions and panel

    private static bool CanActOnClock(RecordContext ctx) =>
        ctx.Directory.IsMember(ctx.Me.Id, "ehs") || ctx.Access.RoleFor(ctx.Record.AppId) >= AppRole.Manager;

    private static bool ClockOpen(RecordSnapshot s) =>
        s.Record.PhaseType != PhaseType.Voided && OshaClock.RuleFor(s.KeywordKey("osha_severe_event")) != null && s.DateTime("osha_reported_at") == null;

    private static bool IsAnonymous(RecordSnapshot s) => s.Bool("anonymous") == true;

    private static bool AwaitingAcceptance(RecordSnapshot s) =>
        IsAnonymous(s) && s.UserId("anon_accepted_by") == null && s.Record.PhaseType is not (PhaseType.Voided or PhaseType.Completed);

    public override Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var list = new List<RecordAction>();
        if (ctx.Me.IsSystem) return Task.FromResult<IEnumerable<RecordAction>>(list);
        if (AwaitingAcceptance(s))
            list.Add(new RecordAction
            {
                Key = "anon_accept", Label = "Accept anonymous report", Icon = "bi-person-check", Group = "Anonymous report",
                Description = "Records you as the named EHS owner of this anonymous report. The investigation can't move on until someone in EHS accepts it.",
                DisabledReason = CanActOnClock(ctx) ? null : "Only the EHS team can accept an anonymous report.",
                Inputs = [new ActionField("note", "Triage note (optional)", "textarea", Help: "For example: credible, area inspected, shelving tagged out.")],
            });
        if (!ClockOpen(s)) return Task.FromResult<IEnumerable<RecordAction>>(list);
        var why = CanActOnClock(ctx) ? null : "Only the EHS team can acknowledge the clock or record the OSHA report.";
        var tz = ctx.Records.TimeZone;
        if (s.UserId("osha_ack_by") == null)
            list.Add(new RecordAction
            {
                Key = "osha_ack", Label = "Acknowledge OSHA clock", Icon = "bi-bell-slash", Group = "OSHA report", Variant = "danger",
                Description = "Confirms EHS has taken ownership of the report. Stops the hourly escalation; the deadline still applies.",
                DisabledReason = why,
                Inputs = [new ActionField("note", "Note (optional)", "textarea", Help: "For example: calling OSHA now; supervisor and HR informed.")],
            });
        list.Add(new RecordAction
        {
            Key = "osha_report", Label = "Record OSHA report", Icon = "bi-telephone-outbound", Group = "OSHA report",
            Description = "Record when and how the fatality or severe injury was reported to OSHA. This stops the clock.",
            DisabledReason = why,
            Inputs =
            [
                new ActionField("reported_at", "Reported at (your time zone)", "text", true, "Format yyyy-MM-dd HH:mm",
                    Default: TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                new ActionField("method", "How it was reported", "select", true, Options: [("phone", "Phone: 1-800-321-OSHA"), ("online", "Online at osha.gov"), ("area_office", "Phone or visit to the area office")]),
                new ActionField("reference", "OSHA reference number", "text", true, "The report or inspection number OSHA gave you."),
            ],
        });
        return Task.FromResult<IEnumerable<RecordAction>>(list);
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        var s = ctx.Snapshot;
        if (!CanActOnClock(ctx)) return ActionOutcome.Fail("Only the EHS team can do this.");
        var db = ctx.Db;
        var audit = ctx.Get<AuditService>();
        var now = DateTime.UtcNow;

        if (actionKey == "anon_accept")
        {
            if (!AwaitingAcceptance(s)) return ActionOutcome.Fail("This report isn't waiting for acceptance.");
            return await Tx.RunAsync(db, async () =>
            {
                var note = input.Get("note")?.Trim();
                var res = await ctx.Records.UpdateAsync(s.Record.Id, new() { ["anon_accepted_by"] = ctx.Me.Id, ["anon_accepted_at"] = now }, note);
                if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                audit.Record(s.Record.Id, "Anonymous report accepted", $"{ctx.Me.Name} accepted the anonymous report and owns it for EHS.", reason: note);
                db.Comments.Add(new Comment
                {
                    RecordId = s.Record.Id, UserId = ctx.Me.Id, IsSystem = string.IsNullOrEmpty(note), Kind = "system", At = now,
                    Text = "Accepted the anonymous report" + (string.IsNullOrEmpty(note) ? "" : ": " + note),
                });
                await db.SaveChangesAsync();
                return ActionOutcome.Ok("Anonymous report accepted. You're recorded as its EHS owner.");
            });
        }

        if (!ClockOpen(s)) return ActionOutcome.Fail("There's no open OSHA reporting clock on this incident.");

        switch (actionKey)
        {
            case "osha_ack":
                return await Tx.RunAsync(db, async () =>
                {
                    var note = input.Get("note")?.Trim();
                    var res = await ctx.Records.UpdateAsync(s.Record.Id, new() { ["osha_ack_by"] = ctx.Me.Id, ["osha_ack_at"] = now }, note);
                    if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                    await AcknowledgeMyAlertsAsync(ctx, now);
                    audit.Record(s.Record.Id, "OSHA clock acknowledged", $"{ctx.Me.Name} took ownership of the OSHA report; hourly escalation stopped.", reason: note);
                    db.Comments.Add(new Comment
                    {
                        RecordId = s.Record.Id, UserId = ctx.Me.Id, IsSystem = string.IsNullOrEmpty(note), Kind = "system", At = now,
                        Text = "Acknowledged the OSHA reporting clock" + (string.IsNullOrEmpty(note) ? "" : ": " + note),
                    });
                    await db.SaveChangesAsync();
                    return ActionOutcome.Ok("OSHA clock acknowledged. The deadline still applies: record the report once it's made.");
                });

            case "osha_report":
                var raw = input.Get("reported_at");
                if (!TryParseLocal(raw, ctx.Records.TimeZone, out var reportedUtc))
                    return ActionOutcome.Fail("Enter when the report was made as yyyy-MM-dd HH:mm, for example 2026-10-24 15:30.");
                if (reportedUtc > now.AddMinutes(5)) return ActionOutcome.Fail("The report time can't be in the future.");
                if (s.DateTime("occurred_at") is { } occ && reportedUtc < ModuleKit.Utc(occ)) return ActionOutcome.Fail("The report can't be made before the event happened.");
                var reference = input.Get("reference")?.Trim();
                if (string.IsNullOrEmpty(reference)) return ActionOutcome.Fail("Enter the OSHA reference number.");
                var method = input.Get("method") switch { "phone" => "by phone (1-800-321-OSHA)", "online" => "online", _ => "to the area office" };
                return await Tx.RunAsync(db, async () =>
                {
                    var values = new Dictionary<string, object?> { ["osha_reported_at"] = reportedUtc, ["osha_report_ref"] = reference, ["osha_reported_by"] = ctx.Me.Id };
                    if (s.UserId("osha_ack_by") == null) { values["osha_ack_by"] = ctx.Me.Id; values["osha_ack_at"] = now; }
                    var res = await ctx.Records.UpdateAsync(s.Record.Id, values, $"Reported to OSHA {method}, reference {reference}");
                    if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                    await AcknowledgeMyAlertsAsync(ctx, now);
                    var deadline = s.DateTime("osha_deadline") is { } d ? ModuleKit.Utc(d) : (DateTime?)null;
                    var onTime = deadline == null || reportedUtc <= deadline;
                    var text = $"Reported to OSHA {method} at {Sites.Local(reportedUtc, ctx.Records.TimeZone)}, reference {reference}" +
                               (deadline is { } dl ? onTime ? $", {ModuleKit.Span(dl - reportedUtc)} before the deadline." : $", {ModuleKit.Span(reportedUtc - dl)} after the deadline." : ".");
                    db.Comments.Add(new Comment { RecordId = s.Record.Id, UserId = ctx.Me.Id, IsSystem = true, Kind = "system", Text = text, At = now });
                    audit.Record(s.Record.Id, "OSHA report recorded", text);
                    await db.SaveChangesAsync();
                    return ActionOutcome.Ok(onTime ? "OSHA report recorded. The clock has stopped." : "OSHA report recorded, after the deadline. The late report is noted in the audit trail.");
                });
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    private static async Task AcknowledgeMyAlertsAsync(RecordContext ctx, DateTime now)
    {
        var mine = await ctx.Db.Notifications.Where(n => n.RecordId == ctx.Record.Id && n.UserId == ctx.Me.Id &&
            n.Kind == NotificationKind.SafetyCritical && n.AcknowledgedAt == null).ToListAsync();
        foreach (var n in mine)
        {
            n.AcknowledgedAt = now;
            n.ReadAt ??= now;
        }
    }

    public static bool TryParseLocal(string? raw, TimeZoneInfo tz, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm:ss"];
        if (!DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return false;
        utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
        return true;
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var panels = new List<RecordPanel>();
        if (IsAnonymous(s) && s.Record.PhaseType != PhaseType.Voided)
        {
            var canAccept = (await ActionsAsync(ctx)).Any(a => a.Key == "anon_accept" && a.DisabledReason == null);
            panels.Add(new RecordPanel
            {
                Key = "anonymous", Title = "Anonymous report", Partial = "~/Modules/Ehs/Views/_AnonymousReport.cshtml", Placement = "top", Icon = "bi-incognito",
                Model = new AnonymousReportVm(ctx.Directory.Get(s.UserId("anon_accepted_by")),
                    s.DateTime("anon_accepted_at") is { } at ? ModuleKit.Utc(at) : null, canAccept, ctx.Records.TimeZone),
            });
        }
        if (await OshaPanelAsync(ctx) is { } osha) panels.Add(osha);
        return panels;
    }

    private async Task<RecordPanel?> OshaPanelAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var rule = OshaClock.RuleFor(s.KeywordKey("osha_severe_event"));
        if (rule == null || s.DateTime("osha_deadline") is not { } deadline || s.Record.PhaseType == PhaseType.Voided) return null;
        var alerts = await ctx.Db.Notifications.AsNoTracking()
            .Where(n => n.RecordId == s.Record.Id && n.Kind == NotificationKind.SafetyCritical)
            .Select(n => new { n.UserId, n.CreatedAt, n.AcknowledgedAt, n.Title }).ToListAsync();
        var actions = (await ActionsAsync(ctx)).Where(a => a.DisabledReason == null).Select(a => a.Key).ToHashSet();
        var occurred = ModuleKit.Utc(s.DateTime("occurred_at")!.Value);
        var vm = new OshaClockVm
        {
            Rule = rule,
            OccurredUtc = occurred,
            DeadlineUtc = ModuleKit.Utc(deadline),
            EnteredUtc = s.Record.CreatedAt,
            ReportedUtc = s.DateTime("osha_reported_at") is { } r ? ModuleKit.Utc(r) : null,
            ReportedBy = ctx.Directory.Get(s.UserId("osha_reported_by")),
            Reference = s.Text("osha_report_ref"),
            AckBy = ctx.Directory.Get(s.UserId("osha_ack_by")),
            AckUtc = s.DateTime("osha_ack_at") is { } a ? ModuleKit.Utc(a) : null,
            ViewerTz = ctx.Records.TimeZone,
            SiteTz = Sites.Tz(s.KeywordKey("site")),
            SiteName = Sites.Name(s.KeywordKey("site")),
            AlertRecipients = alerts.Select(x => x.UserId).Distinct().Count(),
            Escalations = alerts.Count(x => x.Title.StartsWith(IncidentAlerts.EscalationPrefix, StringComparison.Ordinal)),
            AlertsAcknowledged = alerts.Count(x => x.AcknowledgedAt != null),
            AlertsTotal = alerts.Count,
            CanAcknowledge = actions.Contains("osha_ack"),
            CanReport = actions.Contains("osha_report"),
        };
        return new RecordPanel { Key = "osha_clock", Title = "OSHA reporting clock", Partial = "~/Modules/Ehs/Views/_OshaClock.cshtml", Model = vm, Placement = "top", Icon = "bi-alarm" };
    }
}

public sealed record AnonymousReportVm(UserInfo? AcceptedBy, DateTime? AcceptedUtc, bool CanAccept, TimeZoneInfo ViewerTz);

public sealed class OshaClockVm
{
    public required OshaRule Rule { get; init; }
    public DateTime OccurredUtc { get; init; }
    public DateTime DeadlineUtc { get; init; }
    public DateTime EnteredUtc { get; init; }
    public DateTime? ReportedUtc { get; init; }
    public UserInfo? ReportedBy { get; init; }
    public string? Reference { get; init; }
    public UserInfo? AckBy { get; init; }
    public DateTime? AckUtc { get; init; }
    public required TimeZoneInfo ViewerTz { get; init; }
    public required TimeZoneInfo SiteTz { get; init; }
    public string SiteName { get; init; } = "";
    public int AlertRecipients { get; init; }
    public int Escalations { get; init; }
    public int AlertsAcknowledged { get; init; }
    public int AlertsTotal { get; init; }
    public bool CanAcknowledge { get; init; }
    public bool CanReport { get; init; }
    public OshaClockState State => OshaClock.State(DeadlineUtc, ReportedUtc, DateTime.UtcNow);
    public TimeSpan EntryDelay => EnteredUtc - OccurredUtc;
}

/// <summary>SAF-020 safety-critical alerts for OSHA clocks: who gets them and what they say.</summary>
public static class IncidentAlerts
{
    public const string StartedPrefix = "OSHA reporting clock started";
    public const string EscalationPrefix = "Escalation: OSHA report not acknowledged";
    public const string DueSoonPrefix = "OSHA report due in under 2 hours";
    public const string MissedPrefix = "OSHA reporting deadline missed";

    /// <summary>The EHS team, the area supervisor, and the reports-to chains of the reporter and the supervisor.</summary>
    public static List<int> Recipients(UserDirectory dir, RecordSnapshot s)
    {
        var ids = new List<int>();
        ids.AddRange(dir.ActiveMembers("ehs").Select(u => u.Id));
        var reporter = s.UserId("reported_by") ?? (dir.Get(s.Record.CreatedById) is { IsSystem: false } ? s.Record.CreatedById : null);
        var supervisor = s.UserId("supervisor");
        if (supervisor is { } sup) ids.Add(sup);
        ids.AddRange(ModuleKit.ReportsToChain(dir, reporter));
        ids.AddRange(ModuleKit.ReportsToChain(dir, supervisor));
        return ids.Where(id => dir.Get(id) is { IsActive: true }).Distinct().ToList();
    }

    public static string Summary(RecordSnapshot s, OshaRule rule, DateTime deadlineUtc)
    {
        var tz = Sites.Tz(s.KeywordKey("site"));
        var occurred = s.DateTime("occurred_at") is { } o ? Sites.Local(o, tz) : "unknown time";
        return $"{rule.EventLabel} at {Sites.Name(s.KeywordKey("site"))} ({s.Text("area") ?? "location not given"}) on {occurred}: {s.Record.Title}. " +
               $"Report to OSHA by {Sites.Local(deadlineUtc, tz)} ({rule.Cite}). Open {s.Record.Number} to acknowledge the clock and record the report.";
    }

    public static Task SendClockStartedAsync(IServiceProvider sp, RecordSnapshot s)
    {
        var rule = OshaClock.RuleFor(s.KeywordKey("osha_severe_event"));
        if (rule == null || s.DateTime("osha_deadline") is not { } deadline) return Task.CompletedTask;
        var dir = sp.GetRequiredService<UserDirectory>();
        var recipients = Recipients(dir, s);
        sp.GetRequiredService<NotificationService>().SafetyCritical(recipients,
            $"{StartedPrefix}: {s.Record.Number} ({rule.Hours}-hour report)", Summary(s, rule, ModuleKit.Utc(deadline)), s.Record.Id);
        sp.GetRequiredService<AuditService>().Record(s.Record.Id, "Safety-critical alert sent",
            $"OSHA {rule.Hours}-hour clock started; alerted {string.Join(", ", recipients.Select(dir.Name))} (SAF-020).");
        return Task.CompletedTask;
    }
}
