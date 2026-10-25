using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Modules.Quality;

namespace Vellum.Modules.AdverseEvents;

/// <summary>
/// MedWatch server rules: AER-002 (profile first), AER-005 (report numbers), AER-006 (eMDR validation blocks routing with field-level
/// errors), AER-007 (action items complete before submission, via the Approval gate), AER-008 (submission on entry to Submitted to FDA),
/// INV-047 (regulatory index row), INV-048 (completion needs all three acknowledgements and the final confirmation) and AER-011.
/// </summary>
public sealed class MedWatchBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => [AerKeys.MedWatch];

    private static readonly string[] NumberParts = ["mfr_code", "mfr_year", "mfr_sequence", "importer_code", "importer_year", "importer_sequence"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        var setup = ctx.Services.GetRequiredService<AerSetup>();
        var guard = ctx.Services.GetRequiredService<AerWriteGuard>();
        var profile = await setup.ProfileAsync();
        if (ctx.IsNew && profile == null)
            ctx.Error(null, "Set up the Adverse Event Reporting application profile before creating MedWatch reports (AER-002). A manager does this on the setup page.");

        // Normalize and check formats that are never valid.
        if (m.Text("product_code") is { } pc && pc != pc.Trim().ToUpperInvariant()) ctx.Set("product_code", pc.Trim().ToUpperInvariant());
        if (m.Text("product_code") is { } pc2 && !EmdrValidator.IsValidProcode(pc2.Trim().ToUpperInvariant()))
            ctx.Error("product_code", "The product code is the three-letter FDA code, e.g. FRN.");
        foreach (var key in new[] { "mfr_code", "importer_code", "uf_code" })
            if (m.Text(key) is { } code && !EmdrValidator.IsValidCode(code.Trim()))
                ctx.Error(key, "Report number codes are the 7 to 10 digit FDA registration or FEI number.");
        if (EmdrValidator.EmailProblem(m.Text("reporter_email")) is { } emailErr) ctx.Error("reporter_email", emailErr);
        foreach (var row in ctx.Proposed.RowsOf("problem_codes"))
            if (row.Values.Text("code") is { } code && code != code.Trim().ToUpperInvariant())
                ctx.SetRow(row, "code", code.Trim().ToUpperInvariant());

        var reportType = ctx.KeywordKey("report_type") ?? AerKeys.Initial;
        var reporter = ctx.KeywordKey("reporter_type") ?? AerKeys.Manufacturer;
        bool Changed(string key) => ctx.Current == null ? m.Has(key) : ctx.Current.Data.Main.Canonical(key) != m.Canonical(key);

        if (!guard.SystemWrite)
        {
            // AER-005: with mapping on, the code and year (and a sequential number) are assigned on submission, not typed.
            if (profile?.MappingEnabled == true && reportType != AerKeys.Supplemental)
            {
                var (codeKey, yearKey, seqKey) = EmdrSubmissionService.NumberFields(reporter);
                var mapping = await setup.MappingForAsync(ctx.KeywordKey("location"));
                if (Changed(codeKey)) ctx.Error(codeKey, "MFR/importer mapping is on, so the code is assigned automatically when the report is submitted (AER-005).");
                if (Changed(yearKey)) ctx.Error(yearKey, "The year is assigned automatically when the report is submitted (AER-005).");
                if (mapping?.Sequential(reporter) != false && Changed(seqKey))
                    ctx.Error(seqKey, "This location uses sequential numbers: the sequence is generated when the report is submitted (AER-005).");
            }
            // AER-011: a follow-up carries the original report number.
            if (reportType == AerKeys.Supplemental && ctx.Current != null)
                foreach (var key in NumberParts.Where(Changed))
                    ctx.Error(key, "A supplemental report keeps the original report number.");
            // INV-047: a report created from an MDR decision keeps its report class and reporter type.
            if (ctx.Current is { } cur && cur.Links("mdr_tree").Count > 0)
            {
                var before = cur.KeywordKey("report_type") ?? AerKeys.Initial;
                if ((before == AerKeys.Supplemental) != (reportType == AerKeys.Supplemental))
                    ctx.Error("report_type", "A report created from an MDR decision can't switch between initial and supplemental. Create a supplemental report from the completed MedWatch instead.");
                if ((cur.KeywordKey("reporter_type") ?? AerKeys.Manufacturer) != reporter)
                    ctx.Error("reporter_type", "The reporter type of a report created from an MDR decision comes from the location's mapping and can't be changed.");
            }
        }

        // The display number from its parts (code-year-sequence).
        var (c, y, q) = EmdrSubmissionService.NumberFields(reporter);
        var number = m.Text(c)?.Trim() is { Length: > 0 } code2 && m.Number(y) is { } year && m.Number(q) is { } seq
            ? ReportNumberService.Format(code2, (int)year, (int)seq) : null;
        if (m.Text("report_number") != number) ctx.Set("report_number", number);
    }

    /// <summary>INV-047 / INV-023: the regulatory index row, in the create transaction.</summary>
    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var reportType = s.KeywordKey("report_type") ?? AerKeys.Initial;
        var original = s.Links("original_medwatch").Cast<Guid?>().FirstOrDefault();
        var row = new MedWatchReport
        {
            MedWatchId = s.Record.Id, ComplaintId = s.Links("complaint").Cast<Guid?>().FirstOrDefault(), MdrTreeId = s.Links("mdr_tree").Cast<Guid?>().FirstOrDefault(),
            IsInitial = reportType != AerKeys.Supplemental, ReporterType = s.KeywordKey("reporter_type") ?? AerKeys.Manufacturer,
            OriginalMedWatchId = reportType == AerKeys.Supplemental ? original : null,
            FollowUpNumber = reportType == AerKeys.Supplemental && s.Number("followup_number") is { } n ? (int)n : null, CreatedAt = DateTime.UtcNow,
        };
        if (row.OriginalMedWatchId is { } oid && await ctx.Db.Set<MedWatchReport>().AsNoTracking().FirstOrDefaultAsync(x => x.MedWatchId == oid) is { } o)
        {
            row.ReportCode = o.ReportCode;
            row.ReportYear = o.ReportYear;
            row.ReportSequence = o.ReportSequence;
            row.NumberAssignedAt = DateTime.UtcNow;
        }
        ctx.Db.Set<MedWatchReport>().Add(row);
        await ctx.Db.SaveChangesAsync();
    }

    /// <summary>AER-006 validation for the record's current data.</summary>
    public static async Task<List<EmdrIssue>> ValidateAsync(RecordContext ctx, string stage)
    {
        var s = ctx.Snapshot;
        var setup = ctx.Get<AerSetup>();
        var profile = await setup.ProfileAsync();
        var mapping = await setup.MappingForAsync(s.KeywordKey("location"));
        var codes = (await setup.ProductCodesAsync()).Select(p => p.Code).ToHashSet();
        string? duplicate = null;
        var reporter = s.KeywordKey("reporter_type") ?? AerKeys.Manufacturer;
        var (c, y, q) = EmdrSubmissionService.NumberFields(reporter);
        if (s.KeywordKey("report_type") != AerKeys.Supplemental && s.Text(c) is { } code && s.Number(y) is { } year && s.Number(q) is { } seq &&
            (profile?.MappingEnabled != true || mapping?.Sequential(reporter) == false))
        {
            int yy = (int)year, ss = (int)seq;
            var other = await ctx.Db.Set<MedWatchReport>().AsNoTracking()
                .Where(x => x.MedWatchId != s.Record.Id && x.IsInitial && x.ReporterType == reporter && x.ReportCode == code && x.ReportYear == yy && x.ReportSequence == ss)
                .Select(x => (Guid?)x.MedWatchId).FirstOrDefaultAsync();
            if (other != null) duplicate = await ctx.Db.Records.AsNoTracking().Where(r => r.Id == other).Select(r => r.Number).FirstOrDefaultAsync();
        }
        return EmdrValidator.Validate(s, new EmdrValidationContext(profile, mapping, codes, BusinessCalendar.Today, stage, duplicate));
    }

    /// <summary>MedWatch approver activities of the current Approval round.</summary>
    public static async Task<List<Record>> CurrentApprovalsAsync(RecordContext ctx)
    {
        if (!ctx.Config.FormsByKey.TryGetValue(AerKeys.Approver, out var form)) return [];
        var approvalPhase = ctx.Snapshot.Workflow?.Phases.FirstOrDefault(p => p.Key == "approval");
        var since = await ctx.Db.PhaseEntries.AsNoTracking().Where(e => e.RecordId == ctx.Record.Id && e.PhaseId == approvalPhase!.Id)
            .OrderByDescending(e => e.Id).Select(e => (DateTime?)e.EnteredAt).FirstOrDefaultAsync();
        if (since == null) return [];
        return await ctx.Db.Records.AsNoTracking().Include(r => r.Phase)
            .Where(r => r.ParentId == ctx.Record.Id && r.FormId == form.Id && r.CreatedAt >= since.Value.AddSeconds(-1)).OrderBy(r => r.CreatedAt).ToListAsync();
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.Kind is RouteKind.Forward && ctx.To.Key is "approval" or "submitted")
        {
            foreach (var i in (await ValidateAsync(ctx, ctx.To.Key == "approval" ? "approval" : "submit")).Where(i => i.Blocking))
            {
                if (i.FieldKey != null) ctx.Error(i.FieldKey, $"{i.Message} ({i.Rule})", i.RowId);
                else ctx.Error($"{i.Message} ({i.Rule})");
            }
        }
        if (ctx.To.Key == "submitted")
        {
            var approvals = await CurrentApprovalsAsync(ctx);
            var approved = approvals.Where(a => a.Phase?.Key == "approved").ToList();
            if (approvals.All(a => a.PhaseType == PhaseType.Voided) || approved.Count == 0)
                ctx.Error("No MedWatch approver has approved this report yet.");
            foreach (var a in approvals.Where(a => a.Phase?.Key == "changes_requested"))
                ctx.Error($"{ctx.Directory.Name(a.CreatedById)}'s approval {a.Number} requested changes. Send the report back to Draft, make the changes and send it for approval again.");
            var profiles = ctx.Get<EmdrProfileService>();
            var profile = await profiles.CurrentAsync();
            if (profile == null) ctx.Error("There's no eMDR profile for this environment. An administrator sets it up in Administration › eMDR gateway (INT-902).");
            else if (ctx.Get<EmdrGateways>().Get(profile.Gateway) is not { } gw) ctx.Error($"Unknown eMDR gateway '{profile.Gateway}'.");
            else if (gw.UnavailableReason(profile, (await profiles.SettingsAsync()).EnvironmentType) is { } why) ctx.Error(why);
        }
        if (ctx.To.Key == "completed")
            foreach (var p in await ctx.Get<EmdrSubmissionService>().CompletionProblemsAsync(s))
                ctx.Error($"{p} (INV-048)");
        if (ctx.Kind == RouteKind.Back && ctx.From.Key == "submitted")
        {
            var sub = await ctx.Get<EmdrSubmissionService>().CurrentAsync(s.Record.Id);
            if (sub?.Status == EmdrStatus.Accepted)
                ctx.Error("The FDA accepted this report. Send corrections or new information as a supplemental report once it's completed (AER-011).");
            else if (sub?.Status == EmdrStatus.Sent && sub.OverdueAlertedAt == null)
                ctx.Error("The FDA is still processing this submission. Wait for the acknowledgements, or for the receipt delay to pass, before resubmitting.");
        }
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        var audit = ctx.Get<AuditService>();
        switch (ctx.To.Key)
        {
            case "approval":
                foreach (var approver in s.UserIds("approvers").Distinct())
                {
                    var res = await ctx.Records.CreateAsync(new CreateRequest
                    {
                        FormKey = AerKeys.Approver, ParentId = s.Record.Id, SkipAccessCheck = true, Assignees = [approver],
                        Values = new() { ["subject"] = Cut($"Approve {s.Record.Number}: {s.Record.Title}", 200), ["approver"] = approver },
                        CreateComment = $"Created when {s.Record.Number} entered Approval (AER-003)",
                    });
                    if (!res.Ok) throw new WorkflowException("Couldn't create the MedWatch approver activity: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                }
                break;
            case "draft":
                await VoidOpenApprovalsAsync(ctx, $"{s.Record.Number} was sent back to Draft");
                break;
            case "submitted":
                await ctx.Get<EmdrSubmissionService>().SubmitAsync(s);
                break;
            case "voided":
                var row = await ctx.Db.Set<MedWatchReport>().FirstOrDefaultAsync(x => x.MedWatchId == s.Record.Id);
                if (row != null) row.IsVoided = true;
                await VoidOpenApprovalsAsync(ctx, $"{s.Record.Number} was voided");
                audit.Record(s.Record.Id, "MedWatch voided", row?.IsInitial == true && row.MdrTreeId != null
                    ? "The MDR decision can have a new initial MedWatch (INV-047)." : null);
                await ctx.Db.SaveChangesAsync();
                break;
            case "completed":
                audit.Record(s.Record.Id, "MedWatch completed", $"Report {s.Text("report_number")}: ACK1, ACK2 and ACK3 recorded and the final acknowledgement confirmed (INV-048).");
                await ctx.Db.SaveChangesAsync();
                break;
        }
    }

    private static async Task VoidOpenApprovalsAsync(RoutingContext ctx, string reason)
    {
        if (!ctx.Config.FormsByKey.TryGetValue(AerKeys.Approver, out var form)) return;
        var open = await ctx.Db.Records.AsNoTracking().Where(r => r.ParentId == ctx.Record.Id && r.FormId == form.Id && r.PhaseType == PhaseType.Normal).Select(r => r.Id).ToListAsync();
        foreach (var id in open)
        {
            var res = await ctx.Workflow.SystemRouteAsync(id, "voided", reason);
            if (!res.Ok) throw new WorkflowException("Couldn't void an open approver activity: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        }
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var list = new List<RecordAction>();
        if (s.PhaseKey == "submitted" && await ctx.CanActAsync())
        {
            var sub = await ctx.Get<EmdrSubmissionService>().CurrentAsync(s.Record.Id);
            var gw = ctx.Get<EmdrGateways>().Get(sub?.Gateway);
            var tz = ctx.Records.TimeZone;
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            if (sub != null && gw is { ManualAcknowledgements: true } && sub.Status == EmdrStatus.AwaitingUpload)
                list.Add(new RecordAction
                {
                    Key = "aer-upload", Label = "Record ESG upload", Icon = "bi-cloud-arrow-up", Group = "eMDR",
                    Description = $"After uploading {sub.FileName} with FDA ESG WebTrader, record when you uploaded it. Acknowledgements are tracked from then.",
                    Inputs =
                    [
                        new ActionField("date", "Uploaded on", "date", true, Default: nowLocal.ToString("yyyy-MM-dd")),
                        new ActionField("time", "At (24-hour, your time zone)", Required: true, Default: nowLocal.ToString("HH:mm"), Help: tz.DisplayName),
                        new ActionField("reference", "ESG WebTrader reference", Help: "Optional: the core ID or receipt shown by WebTrader."),
                    ],
                });
            if (sub != null && gw is { ManualAcknowledgements: true } && sub.Status == EmdrStatus.Sent)
            {
                var next = sub.Ack1At == null ? "ack1" : sub.Ack2At == null ? "ack2" : "ack3";
                list.Add(new RecordAction
                {
                    Key = "aer-ack", Label = $"Record {next.ToUpperInvariant()}", Icon = "bi-envelope-check", Group = "eMDR",
                    Description = "Record an acknowledgement the FDA sent for this submission. Each one becomes a linked eMDR Acknowledgement record (AER-008).",
                    Inputs =
                    [
                        new ActionField("type", "Acknowledgement", "select", true, Options: [("ack1", "ACK1 · ESG receipt (MDN)"), ("ack2", "ACK2 · CDRH receipt"), ("ack3", "ACK3 · Load and validation")], Default: next),
                        new ActionField("result", "Result", "select", true, Options: [("accepted", "Accepted"), ("rejected", "Rejected / error")], Default: "accepted"),
                        new ActionField("date", "Received on", "date", true, Default: nowLocal.ToString("yyyy-MM-dd")),
                        new ActionField("time", "At (24-hour, your time zone)", Required: true, Default: nowLocal.ToString("HH:mm"), Help: tz.DisplayName),
                        new ActionField("reference", "FDA reference", Help: "The core ID or acknowledgement number from the FDA's message."),
                        new ActionField("detail", "Detail or errors", "textarea", Help: "Copy any error text from ACK2 or ACK3."),
                    ],
                });
            }
            if (sub != null && gw is { ManualAcknowledgements: false } && sub.Status == EmdrStatus.Sent)
                list.Add(new RecordAction
                {
                    Key = "aer-poll", Label = "Check for acknowledgements", Icon = "bi-arrow-repeat", Group = "eMDR",
                    Description = $"Ask the {gw.Name} gateway for acknowledgements now instead of waiting for the next scheduled check.",
                });
        }
        if (s.Record.PhaseType == PhaseType.Completed && ctx.Access.CanCreate(s.Form))
            list.Add(new RecordAction
            {
                Key = "aer-supplemental", Label = "Create supplemental report", Icon = "bi-file-earmark-plus", Group = "Create",
                Description = "A follow-up report carrying this report's number and content. Change what's new and mark the sections changed (AER-011).",
                Inputs =
                [
                    new ActionField("followup", "Follow-up type (H2)", "select", true,
                        Options: ctx.Config.Options("aer_followup_type").Select(k => (k.Key, k.Label)).ToList(), Default: "additional_info"),
                    new ActionField("note", "What's new", "textarea"),
                ],
            });
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if ((await ActionsAsync(ctx)).All(a => a.Key != actionKey)) return ActionOutcome.Fail("This action isn't available.");
        var s = ctx.Snapshot;
        var subs = ctx.Get<EmdrSubmissionService>();
        var tz = ctx.Records.TimeZone;
        DateTime? When()
        {
            if (!DateOnly.TryParse(input.Get("date"), CultureInfo.InvariantCulture, out var d)) return null;
            if (!TimeOnly.TryParseExact(input.Get("time") ?? "", ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.ToDateTime(t), DateTimeKind.Unspecified), tz);
        }
        switch (actionKey)
        {
            case "aer-upload":
            {
                if (When() is not { } at) return ActionOutcome.Fail("Enter the upload date and time (24-hour, e.g. 14:05).");
                var r = await subs.RecordUploadAsync(s.Record.Id, at, input.Get("reference"));
                return r.Ok ? ActionOutcome.Ok(r.Message) : ActionOutcome.Fail(r.Errors.ToArray());
            }
            case "aer-ack":
            {
                if (When() is not { } at) return ActionOutcome.Fail("Enter when the acknowledgement was received (24-hour time, e.g. 14:05).");
                var type = input.Get("type");
                if (type is not ("ack1" or "ack2" or "ack3")) return ActionOutcome.Fail("Choose the acknowledgement.");
                var accepted = input.Get("result") != "rejected";
                if (!accepted && string.IsNullOrWhiteSpace(input.Get("detail"))) return ActionOutcome.Fail("Copy the FDA's error text into Detail or errors.");
                var r = await subs.RecordAckAsync(s.Record.Id, new GatewayAck(type, accepted, at, input.Get("detail")?.Trim(), input.Get("reference")?.Trim()),
                    $"Recorded by {ctx.Me.Name}");
                return r.Ok ? ActionOutcome.Ok(r.Message) : ActionOutcome.Fail(r.Errors.ToArray());
            }
            case "aer-poll":
            {
                var result = await subs.MonitorAsync(DateTime.UtcNow, onlyMedWatchId: s.Record.Id);
                var mine = result.Log.Count(l => l.StartsWith(s.Record.Number));
                return ActionOutcome.Ok(mine > 0 ? string.Join(" · ", result.Log.Where(l => l.StartsWith(s.Record.Number)).Select(l => l[(s.Record.Number.Length + 2)..]))
                    : "No new acknowledgements yet.");
            }
            case "aer-supplemental":
            {
                var (rec, err) = await ctx.Get<MedWatchFactory>().CreateSupplementalAsync(s.Record.Id, input.Get("followup"), input.Get("note"));
                return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"Supplemental report {rec.Number} created.", rec.Id);
            }
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var model = await MedWatchPanelModel.BuildAsync(ctx);
        return
        [
            new RecordPanel { Key = "aer-status", Title = "eMDR status", Placement = "top", Partial = "~/Modules/AdverseEvents/Views/_MedWatchStatus.cshtml", Model = model },
            new RecordPanel
            {
                Key = "aer-acks", Title = "Submissions & acknowledgements", Icon = "bi-envelope-check", Placement = "tab", Order = 10,
                Count = model.Acks.Count, Partial = "~/Modules/AdverseEvents/Views/_Acknowledgements.cshtml", Model = model,
            },
        ];
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>The MedWatch approver activity: tells the owner when the round is complete or changes are requested.</summary>
public sealed class MedWatchApproverBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => [AerKeys.Approver];

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.Record.ParentId is not { } pid || ctx.To.Type != PhaseType.Completed) return;
        var parent = await ctx.Records.LoadAsync(pid);
        if (parent != null && parent.UserId("owner") == ctx.Me.Id)
            ctx.Error("The report owner can't approve their own MedWatch (segregation of duties).");
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.Record.ParentId is not { } pid || ctx.To.Type != PhaseType.Completed) return;
        var parent = await ctx.Records.LoadAsync(pid);
        if (parent == null || parent.PhaseKey != "approval") return;
        var owner = parent.UserId("owner") ?? parent.Record.CreatedById;
        var notify = ctx.Get<NotificationService>();
        if (ctx.To.Key == "changes_requested")
        {
            notify.Notify(owner, NotificationKind.Info, $"Changes requested on {parent.Record.Number}",
                $"{ctx.Me.Name} requested changes: {ctx.Comment}. Send the report back to Draft to make them.", pid);
            return;
        }
        var pctx = new RecordContext(parent, ctx.Services);
        var round = await MedWatchBehavior.CurrentApprovalsAsync(pctx);
        if (round.Count > 0 && round.All(a => a.Id == ctx.Record.Id || a.Phase?.Key == "approved" || a.PhaseType == PhaseType.Voided))
            notify.Notify(owner, NotificationKind.Info, $"{parent.Record.Number} is approved",
                $"Every MedWatch approver has approved \"{parent.Record.Title}\". Submit it to the FDA before {parent.Date("report_due"):dd MMM yyyy}.", pid);
    }
}

/// <summary>CMP-005: "Create MedWatch" on a reportable MDR decision tree (and on its complaint), and the linked reports on both.</summary>
public sealed class ComplaintMedWatchBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["complaint", "mdr_decision_tree"];
    public override int Order => 50;

    private static async Task<List<Guid>> ReportableTreesAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.Form.Key == "mdr_decision_tree") return [ctx.Record.Id];
        if (!ctx.Config.FormsByKey.TryGetValue("mdr_decision_tree", out var tf)) return [];
        return await ctx.Db.Records.AsNoTracking().Where(r => r.ParentId == ctx.Record.Id && r.FormId == tf.Id && r.PhaseType != PhaseType.Voided &&
                ctx.Db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "mdr_reportable" && v.RefId == 1))
            .OrderBy(r => r.CreatedAt).Select(r => r.Id).ToListAsync();
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (!ctx.Config.FormsByKey.ContainsKey(AerKeys.MedWatch)) return [];
        var s = ctx.Snapshot;
        // CMP-005: available in the tree's Open phase once it concludes reportable (and after approval), and on a complaint whose tree is reportable.
        var eligible = s.Form.Key == "mdr_decision_tree" ? s.PhaseKey is "open" or "completed" : s.Record.PhaseType == PhaseType.Normal;
        if (!eligible) return [];
        var factory = ctx.Get<MedWatchFactory>();
        var list = new List<RecordAction>();
        foreach (var treeId in await ReportableTreesAsync(ctx))
        {
            var blocker = await factory.BlockerForTreeAsync(treeId);
            if (blocker != null && blocker.Contains("INV-047")) continue; // already has its MedWatch: shown in the panel
            var treeNumber = treeId == ctx.Record.Id ? null : await ctx.Db.Records.AsNoTracking().Where(r => r.Id == treeId).Select(r => r.Number).FirstOrDefaultAsync();
            list.Add(new RecordAction
            {
                Key = $"aer-create-{treeId:N}", Label = "Create MedWatch" + (treeNumber != null ? $" ({treeNumber})" : ""), Icon = "bi-file-earmark-medical",
                Group = "Regulatory", DisabledReason = blocker,
                Description = "Start the FDA MedWatch (3500A) report in Adverse Event Reporting with the mapped complaint data (CMP-005).",
            });
        }
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        var action = (await ActionsAsync(ctx)).FirstOrDefault(a => a.Key == actionKey && a.DisabledReason == null);
        if (action == null || !Guid.TryParseExact(actionKey["aer-create-".Length..], "N", out var treeId)) return ActionOutcome.Fail("This action isn't available.");
        var (rec, err) = await ctx.Get<MedWatchFactory>().CreateFromTreeAsync(treeId);
        return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"MedWatch {rec.Number} created. Review the mapped data, then complete the report.", rec.Id);
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        if (!ctx.Config.FormsByKey.ContainsKey(AerKeys.MedWatch)) return [];
        var id = ctx.Record.Id;
        var q = ctx.Db.Set<MedWatchReport>().AsNoTracking();
        q = ctx.Snapshot.Form.Key == "complaint" ? q.Where(x => x.ComplaintId == id) : q.Where(x => x.MdrTreeId == id);
        var rows = await q.ToListAsync();
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.MedWatchId).ToList();
        var recs = await ctx.Access.Readable().AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync();
        if (recs.Count == 0) return [];
        var subs = await ctx.Db.Set<EmdrSubmission>().AsNoTracking().Where(x => ids.Contains(x.MedWatchId) && !x.Superseded).ToListAsync();
        var items = recs.OrderBy(r => r.CreatedAt).Select(r =>
        {
            var row = rows.First(x => x.MedWatchId == r.Id);
            var sub = subs.Where(x => x.MedWatchId == r.Id).OrderByDescending(x => x.Attempt).FirstOrDefault();
            return new LinkedMedWatch(r.Id, r.Number, r.Title, ctx.Config.Phase(r.PhaseId)?.Name, r.PhaseType,
                row.ReportCode != null && row.ReportYear != null && row.ReportSequence != null ? ReportNumberService.Format(row.ReportCode, row.ReportYear.Value, row.ReportSequence.Value) : null,
                row.IsInitial ? "Initial" : $"Follow-up #{row.FollowUpNumber}", sub?.Status, sub?.Ack1At != null, sub?.Ack2At != null, sub?.Ack3At != null);
        }).ToList();
        return [new RecordPanel { Key = "aer-linked", Title = "MedWatch reports", Icon = "bi-file-earmark-medical", Placement = "side", Order = 15, Count = items.Count,
            Partial = "~/Modules/AdverseEvents/Views/_LinkedMedWatch.cshtml", Model = items }];
    }
}

public sealed record LinkedMedWatch(Guid Id, string Number, string Title, string? Phase, PhaseType PhaseType, string? ReportNumber, string Kind,
    string? SubmissionStatus, bool Ack1, bool Ack2, bool Ack3);
