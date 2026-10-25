using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Modules.Training;

namespace Vellum.Modules.Documents;

internal static class ActionInputs
{
    public static int? User(string? s) => int.TryParse(s, out var i) && i > 0 ? i : null;
    public static List<int> Users(IEnumerable<string> s) => s.SelectMany(x => x.Split(',')).Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).Distinct().ToList();
    public static DateOnly? Date(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    public static int? Int(string? s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : null;
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>DOC-001..006: Document Type Profiles are maintained by application managers; at most one enabled profile per type (INV-101).</summary>
public sealed partial class DocTypeProfileBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => [DocKeys.Profile];

    [GeneratedRegex("^[A-Z0-9][A-Z0-9\\-/.]{0,11}$")]
    private static partial Regex PrefixPattern();

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var me = ctx.Services.GetRequiredService<CurrentUser>();
        var access = ctx.Services.GetRequiredService<AccessService>();
        var svc = ctx.Services.GetRequiredService<DocumentControlService>();
        if (!me.IsSystem && access.RoleFor(ctx.Form.AppId) < AppRole.Manager)
            ctx.Error(null, "Only Document Control application managers can maintain document type profiles (DOC-005).");

        if (ctx.Proposed.Main.Text("number_prefix") is { } prefix)
        {
            var norm = DocumentControlService.NormalizePrefix(prefix);
            if (!PrefixPattern().IsMatch(norm)) ctx.Error("number_prefix", "Use up to 12 letters, digits, dashes, slashes or dots, for example SOP-.");
            else if (norm != prefix) ctx.Set("number_prefix", norm);
        }

        // INV-101: a document type has at most one enabled profile.
        if (ctx.Proposed.Main.Bool("enabled") == true && ctx.KeywordKey("doc_type") is { } type)
        {
            var other = (await svc.ProfilesAsync()).FirstOrDefault(p => p.Enabled && p.TypeKey == type && p.Id != ctx.Current?.Record.Id);
            if (other != null)
                ctx.Error("enabled", $"{other.Number} ({other.Name}) is already the enabled profile for this type. Disable it first: a type can have only one enabled profile (INV-101).");
        }
    }
}

/// <summary>
/// The controlled document: numbering from the type profile (DOC-010), workflow rules (DOC-031/033/060, SAF-044),
/// effectiveness and supersession (DOC-050..055), training linkage (DOC-061/063, TRN-071), draft visibility (DOC-080),
/// revision-control requests (DOC-090..107) and the record-page panels (DOC-016, DOC-043, DOC-067).
/// </summary>
public sealed class DocumentBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => [DocKeys.Document];

    private static DocumentControlService Svc(IServiceProvider sp) => sp.GetRequiredService<DocumentControlService>();

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var svc = Svc(ctx.Services);
        var type = ctx.KeywordKey("doc_type");
        if (ctx.IsNew && !svc.CreatingRevision)
        {
            await svc.EnsureDefaultProfilesAsync();
            if (type == null) ctx.Error("doc_type", "Choose the document type first. It sets the document number (DOC-010).");
            else if (await svc.ProfileForAsync(type) == null)
                ctx.Error("doc_type", $"There's no enabled Document Type Profile for {ctx.Config.Keyword("doc_type", type)?.Label ?? type}, so new documents of this type can't be created (DOC-003).");
        }
        if (!ctx.IsNew && ctx.Current!.KeywordKey("doc_type") is { } was && was != type)
            ctx.Error("doc_type", "The document type can't change after the document is created, because it set the document number (DOC-010).");
        if (ctx.Proposed.Main.Date("target_effective") is { } t && t < BusinessCalendar.Today && (ctx.IsNew || ctx.Current!.Date("target_effective") != t))
            ctx.Error("target_effective", "The target effective date can't be in the past.");
    }

    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        var svc = Svc(ctx.Services);
        var r = ctx.Record;
        if (r.RevisionOrdinal == 0 && !svc.CreatingRevision && await svc.ProfileForAsync(ctx.Snapshot.KeywordKey("doc_type")) is { } profile)
        {
            // DOC-010: number from the type profile's prefix with its own never-reused sequence (INV-020).
            var seq = await ctx.Get<NumberingService>().NextAsync("documents:" + profile.Prefix);
            var placeholder = r.Number;
            r.Number = profile.Prefix + seq.ToString("D5", CultureInfo.InvariantCulture);
            r.Sequence = seq;
            r.Revision = NumberingService.NextRevision(profile.Format, null) ?? "A";
            ctx.Get<AuditService>().Record(r.Id, "Number assigned", $"{r.Number} rev {r.Revision} from type profile {profile.Number} ({profile.Name}, DOC-010)",
                oldValue: placeholder, newValue: r.Number, recordVersion: r.Version);
            await ctx.Db.SaveChangesAsync();
        }
        await svc.SyncDraftReadersAsync(ctx.Snapshot);
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var svc = Svc(ctx.Services);
        var snap = ctx.Snapshot;
        if (ctx.From.Key == DocKeys.Draft && ctx.Kind == RouteKind.Forward)
        {
            // DOC-033 / DOC-060: the training decision is recorded before leaving Draft.
            var profile = await svc.ProfileForAsync(snap.KeywordKey("doc_type"), enabledOnly: false);
            var decision = snap.Bool("training_required");
            if (decision == null && profile?.TrainingConsidered != false)
                ctx.Error("Record the training decision: is training required for this revision? (DOC-033, DOC-060)");
            if (decision == false && string.IsNullOrWhiteSpace(snap.Text("training_justification")))
                ctx.Error("Explain why no training is needed for this revision. The justification is mandatory when training isn't required (DOC-060).");
            // SAF-044: author ≠ approver.
            if (snap.UserIds("reviewers").Contains(snap.Record.CreatedById))
                ctx.Error($"Reviewers must be independent of the author: remove {ctx.Directory.Name(snap.Record.CreatedById)} from Reviewers (SAF-044).");
            if (ctx.To.Key == DocKeys.AwaitingRelease)
            {
                // DOC-031: the minor-change shortcut needs the flag, and only applies to revisions of an approved document.
                if (snap.Bool("minor_change") != true)
                    ctx.Error("The minor-change shortcut is only available when the change is marked as minor or editorial (DOC-031).");
                else if (snap.Record.RevisionOrdinal == 0)
                    ctx.Error("The minor-change shortcut is only for revisions of an approved document. A new document needs full review and approval (DOC-031).");
            }
        }
        if (ctx.To.Key == DocKeys.Approved && ctx.Kind == RouteKind.Forward)
        {
            var planned = await svc.PlannedEffectiveAsync(snap);
            if (planned > BusinessCalendar.Today)
                ctx.Error($"This revision's effective date is {planned:dd MMM yyyy}. Use Release › Schedule release to sign the release now; Vellum makes it effective on that date (DOC-050).");
            var gate = await svc.GateAsync(snap);
            if (!gate.Satisfied) ctx.Error(gate.Message);
        }
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var svc = Svc(ctx.Services);
        var training = ctx.Get<TrainingService>();
        var snap = ctx.Snapshot;
        var r = snap.Record;
        switch (ctx.To.Key)
        {
            case DocKeys.AwaitingRelease:
                var created = await training.AssignForDocumentAsync(snap, $"{r.Number} rev {r.Revision} is awaiting release (DOC-063)");
                var planned = await svc.PlannedEffectiveAsync(snap);
                ctx.Db.Comments.Add(new Comment
                {
                    RecordId = r.Id, IsSystem = true, Kind = "system", At = DateTime.UtcNow, UserId = ctx.Me.IsSystem ? null : ctx.Me.Id,
                    Text = $"Planned effective date: {planned:dd MMM yyyy}." + (created > 0 ? $" {created} training assignment{(created == 1 ? "" : "s")} created (DOC-063)." : ""),
                });
                break;
            case DocKeys.Approved:
                await svc.MakeEffectiveAsync(ctx);
                break;
            case DocKeys.Obsolete:
                await svc.CompleteObsolescenceAsync(ctx, ctx.Comment);
                break;
            case DocKeys.Voided:
                await training.DisableCoursesForDocumentAsync(r.Id, $"{r.Number} rev {r.Revision} was voided (DOC-066)");
                break;
        }
        if (ctx.To.Type == PhaseType.Normal) await svc.SyncDraftReadersAsync(snap);
    }

    // ------------------------------------------------------------------ actions

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var svc = Svc(ctx.Services);
        var training = ctx.Get<TrainingService>();
        var snap = ctx.Snapshot;
        var r = snap.Record;
        var list = new List<RecordAction>();
        var owner = snap.UserId("owner");
        var effective = r.IsEffective && !r.IsArchived && snap.PhaseKey == DocKeys.Approved;

        if (effective && svc.CanRequest(snap))
        {
            list.Add(new RecordAction
            {
                Key = "request_change", Label = "Request change", Icon = "bi-pencil-square", Group = "Revision control",
                Description = "Raise a change request. Accepting it creates the next Draft revision (DOC-090..093).",
                DisabledReason = await svc.BlockerAsync(r, DocKeys.ChangeRequest),
                Inputs =
                [
                    new ActionField("summary", "What should change", Required: true, Help: "A short summary, for example: Add second-person label check to step 6.3."),
                    new ActionField("reason", "Reason for change", "textarea", Required: true, Help: "Becomes the new revision's reason for revision (DOC-017)."),
                    new ActionField("approver", "Accepted by", "user", Required: true, Default: owner?.ToString(CultureInfo.InvariantCulture)),
                    new ActionField("revision_due", "New revision due", "date", Default: ActionInputs.Iso(BusinessCalendar.Today.AddDays(30))),
                    new ActionField("assign_to", "Assign new revision to", "users", Required: true, Default: (owner ?? ctx.Me.Id).ToString(CultureInfo.InvariantCulture)),
                    new ActionField("notify", "Notify of new revision", "users"),
                ],
            });
            list.Add(new RecordAction
            {
                Key = "start_review", Label = "Start periodic review", Icon = "bi-calendar2-check", Group = "Revision control",
                Description = "Review whether the document is still accurate and current (DOC-073).",
                DisabledReason = await svc.BlockerAsync(r, DocKeys.PeriodicReview),
                Inputs =
                [
                    new ActionField("reviewer", "Reviewer", "user", Required: true, Default: owner?.ToString(CultureInfo.InvariantCulture)),
                    new ActionField("due", "Due date", "date", Required: true, Default: ActionInputs.Iso(BusinessCalendar.Today.AddDays(20))),
                ],
            });
        }
        if (effective && svc.CanObsolete)
            list.Add(new RecordAction
            {
                Key = "request_obsolete", Label = "Request obsolescence", Icon = "bi-archive", Group = "Revision control", Variant = "danger",
                Description = "Withdraw this document from use. Quality approves the request (DOC-101..103).",
                DisabledReason = await svc.BlockerAsync(r, DocKeys.ObsoleteRequest),
                Inputs = [new ActionField("reason", "Reason for obsolescence", "textarea", Required: true)],
            });
        if (r.IsArchived && r.ArchiveReason == DocKeys.ObsoleteReason && svc.CanRestore)
        {
            var pending = (await svc.PendingItemsAsync(r)).FirstOrDefault(i => i.FormKey == DocKeys.Document);
            list.Add(new RecordAction
            {
                Key = "restore", Label = "Restore as new draft", Icon = "bi-arrow-counterclockwise", Group = "Revision control",
                Description = "Bring this obsolete document back as a new Draft revision with its content (DOC-105/106). Superseded revisions can't be restored.",
                DisabledReason = pending == null ? null : $"Revision {pending.Revision} is already in progress ({pending.PhaseName}).",
                RequiresComment = true, CommentLabel = "Why is the document being restored?",
                Inputs =
                [
                    new ActionField("assignee", "Assign the draft to", "user", Required: true, Default: owner?.ToString(CultureInfo.InvariantCulture)),
                    new ActionField("due", "Draft due", "date", Required: true, Default: ActionInputs.Iso(BusinessCalendar.Today.AddDays(15))),
                ],
            });
        }

        // DOC-061: Set up training.
        if (snap.Bool("training_required") == true && !r.IsArchived && r.PhaseType is PhaseType.Normal or PhaseType.Completed &&
            (svc.IsManager || svc.IsDocController || svc.IsTrainingCoordinator || owner == ctx.Me.Id || await ctx.Access.ActingAssignmentAsync(r.Id) != null))
        {
            var courses = await training.CoursesForDocumentAsync(r.Id);
            if (courses.All(c => !c.Enabled))
                list.Add(new RecordAction
                {
                    Key = "setup_training", Label = "Set up training", Icon = "bi-mortarboard", Group = "Training",
                    Description = "Create the course for this revision, pre-filled from the document. Assignments are created when it reaches Awaiting Release (DOC-061, DOC-063).",
                    Inputs =
                    [
                        new ActionField("course_name", "Course name", Required: true, Default: r.Title),
                        new ActionField("course_type", "Course type", "select", Required: true, Options: [("online", "Online: read and acknowledge"), ("instructor_led", "Instructor-led")], Default: "online"),
                        new ActionField("due_days", "Due within (days)", "number", Required: true, Default: "14"),
                        new ActionField("expiration_months", "Expires after (months)", "number", Help: "Leave blank if the training doesn't expire."),
                        new ActionField("people", "Also assign", "users", Help: "Applicability starts from the document's department and sites; refine it on the course."),
                    ],
                });
        }

        // Release (DOC-050) and training-gated release override (TRN-071).
        if (snap.PhaseKey == DocKeys.AwaitingRelease && r.PhaseType == PhaseType.Normal)
        {
            var acting = await ctx.Access.ActingAssignmentAsync(r.Id) != null;
            var planned = await svc.PlannedEffectiveAsync(snap);
            if ((acting || svc.IsManager) && planned > BusinessCalendar.Today)
            {
                var scheduled = await svc.PhaseSignatureAsync(r.Id, DocKeys.SignReleased);
                list.Add(new RecordAction
                {
                    Key = "schedule_release", Label = "Schedule release", Icon = "bi-calendar-check", Group = "Release",
                    Description = $"Sign the release now. Vellum makes the revision effective on {planned:dd MMM yyyy}, once any training gate is met (DOC-050).",
                    SignatureMeaning = DocKeys.SignReleased,
                    DisabledReason = scheduled == null ? null : $"Release already signed by {scheduled.SignerName} on {scheduled.SignedAt:dd MMM yyyy}.",
                });
            }
            var gate = await svc.GateAsync(snap);
            if (gate.Gated && !gate.Satisfied && gate.Override == null && (svc.IsDocController || svc.IsManager))
                list.Add(new RecordAction
                {
                    Key = "override_gate", Label = "Override training gate", Icon = "bi-unlock", Group = "Release", Variant = "danger",
                    Description = gate.Message,
                    Confirm = "Releasing before training is complete means some people may use this revision untrained (hazard H2). Record why this is acceptable.",
                    RequiresComment = true, CommentLabel = "Justification",
                    SignatureMeaning = DocKeys.SignGateOverride,
                });
        }
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        var svc = Svc(ctx.Services);
        var snap = ctx.Snapshot;
        var r = snap.Record;
        var owner = snap.UserId("owner") ?? ctx.Me.Id;
        var today = BusinessCalendar.Today;
        switch (actionKey)
        {
            case "request_change":
            {
                if (!svc.CanRequest(snap)) return ActionOutcome.Fail("Only the document owner, Document Control or an application manager can request a change (DOC-090).");
                if (await svc.BlockerAsync(r, DocKeys.ChangeRequest) is { } block) return ActionOutcome.Fail(block);
                var summary = input.Get("summary");
                var reason = input.Get("reason");
                if (summary == null || reason == null) return ActionOutcome.Fail("Describe what should change and why.");
                var due = ActionInputs.Date(input.Get("revision_due"));
                if (due < today) return ActionOutcome.Fail("The new revision's due date can't be in the past.");
                var assign = ActionInputs.Users(input.GetAll("assign_to"));
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = DocKeys.ChangeRequest, ParentId = r.Id, SkipAccessCheck = true,
                    Values = new()
                    {
                        ["target_document"] = r.Id, ["summary"] = summary, ["reason"] = reason,
                        ["approver"] = ActionInputs.User(input.Get("approver")) ?? owner,
                        ["assign_to"] = assign.Count > 0 ? assign : [owner],
                        ["notify"] = ActionInputs.Users(input.GetAll("notify")),
                        ["revision_due"] = due ?? today.AddDays(30),
                    },
                    CreateComment = $"Raised on {r.Number} rev {r.Revision} (DOC-090)",
                });
                if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                return ActionOutcome.Ok($"Change request {res.Record!.Number} raised.", res.Record.Id);
            }
            case "start_review":
            {
                if (!svc.CanRequest(snap)) return ActionOutcome.Fail("Only the document owner, Document Control or an application manager can start a periodic review (DOC-073).");
                if (await svc.BlockerAsync(r, DocKeys.PeriodicReview) is { } block) return ActionOutcome.Fail(block);
                var reviewer = ActionInputs.User(input.Get("reviewer")) ?? owner;
                var due = ActionInputs.Date(input.Get("due")) ?? today.AddDays(20);
                if (due < today) return ActionOutcome.Fail("The due date can't be in the past.");
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = DocKeys.PeriodicReview, ParentId = r.Id, SkipAccessCheck = true, DueDate = due,
                    Values = new() { ["target_document"] = r.Id, ["reviewer"] = reviewer },
                    CreateComment = $"Periodic review of {r.Number} rev {r.Revision} (DOC-073)",
                });
                if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                return ActionOutcome.Ok($"Periodic review {res.Record!.Number} started.", res.Record.Id);
            }
            case "request_obsolete":
            {
                if (!svc.CanObsolete) return ActionOutcome.Fail("Only Document Control and application managers can request obsolescence (DOC-101).");
                if (await svc.BlockerAsync(r, DocKeys.ObsoleteRequest) is { } block) return ActionOutcome.Fail(block);
                var reason = input.Get("reason");
                if (reason == null) return ActionOutcome.Fail("Give the reason for obsolescence.");
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = DocKeys.ObsoleteRequest, ParentId = r.Id, SkipAccessCheck = true,
                    Values = new() { ["target_document"] = r.Id, ["reason"] = reason },
                    CreateComment = $"Obsolescence of {r.Number} rev {r.Revision} requested (DOC-102)",
                });
                if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                return ActionOutcome.Ok($"Obsolete request {res.Record!.Number} created. Send it for approval when it's ready.", res.Record.Id);
            }
            case "restore":
            {
                if (!(r.IsArchived && r.ArchiveReason == DocKeys.ObsoleteReason)) return ActionOutcome.Fail("Only manually obsoleted documents can be restored; superseded revisions can't (DOC-105).");
                if (!svc.CanRestore) return ActionOutcome.Fail("Only Document Control and application managers can restore documents (DOC-105).");
                if ((await svc.PendingItemsAsync(r)).FirstOrDefault(i => i.FormKey == DocKeys.Document) is { } p)
                    return ActionOutcome.Fail($"Revision {p.Revision} is already in progress ({p.PhaseName}).");
                var assignee = ActionInputs.User(input.Get("assignee"));
                var due = ActionInputs.Date(input.Get("due"));
                if (assignee == null || ctx.Directory.Get(assignee.Value) is not { IsActive: true }) return ActionOutcome.Fail("Choose an active person to assign the draft to (DOC-106).");
                if (due == null || due < today) return ActionOutcome.Fail("Choose a due date that isn't in the past.");
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("A comment explaining the restore is mandatory (DOC-106).");
                var rev = await Tx.RunAsync(ctx.Db, async () =>
                {
                    var created = await svc.CreateRevisionAsync(snap, ctx.Me.Id, [assignee.Value], due, $"Restored from obsolete revision {r.Revision}: {input.Comment!.Trim()}",
                        null, $"Restored from obsolete revision {r.Revision} by {ctx.Me.Name} (DOC-106)");
                    ctx.Get<AuditService>().Record(r.Id, "Restored", $"Restored as revision {created.Revision} (Draft), assigned to {ctx.Directory.Name(assignee.Value)}, due {due:dd MMM yyyy}",
                        reason: input.Comment, phaseName: snap.Phase?.Name);
                    ctx.Db.Comments.Add(new Comment { RecordId = r.Id, UserId = ctx.Me.Id, Kind = "restore", At = DateTime.UtcNow,
                        Text = $"Restored as revision {created.Revision}: {input.Comment!.Trim()}" });
                    await ctx.Db.SaveChangesAsync();
                    return created;
                });
                return ActionOutcome.Ok($"Restored as revision {rev.Revision} in Draft.", rev.Id);
            }
            case "setup_training":
            {
                var training = ctx.Get<TrainingService>();
                if (snap.Bool("training_required") != true) return ActionOutcome.Fail("Training isn't required for this revision.");
                if ((await training.CoursesForDocumentAsync(r.Id)).Any(c => c.Enabled)) return ActionOutcome.Fail("This revision already has a course.");
                var name = input.Get("course_name") ?? r.Title;
                var type = input.Get("course_type") is "instructor_led" ? "instructor_led" : "online";
                var dueDays = ActionInputs.Int(input.Get("due_days")) ?? 14;
                if (dueDays is < 0 or > 365) return ActionOutcome.Fail("Due within must be between 0 and 365 days.");
                var exp = ActionInputs.Int(input.Get("expiration_months"));
                var courseId = await Tx.RunAsync(ctx.Db, async () =>
                {
                    var id = await training.CreateCourseForDocumentAsync(snap, name, type, dueDays, exp is > 0 ? exp : null, ActionInputs.Users(input.GetAll("people")));
                    if (snap.PhaseKey == DocKeys.AwaitingRelease || r.IsEffective)
                        await training.AssignForDocumentAsync(snap, $"{r.Number} rev {r.Revision}: training set up (DOC-061)");
                    return id;
                });
                return ActionOutcome.Ok("Course created. Check its applicability rules.", courseId);
            }
            case "schedule_release":
            {
                var acting = await ctx.Access.ActingAssignmentAsync(r.Id);
                if (snap.PhaseKey != DocKeys.AwaitingRelease || (acting == null && !svc.IsManager)) return ActionOutcome.Fail("Only Document Control can release this revision.");
                var planned = await svc.PlannedEffectiveAsync(snap);
                if (await svc.PhaseSignatureAsync(r.Id, DocKeys.SignReleased) != null) return ActionOutcome.Fail("The release is already scheduled.");
                var sig = await ctx.Get<SignatureService>().SignAsync(snap, DocKeys.SignReleased, $"Schedule release for {planned:dd MMM yyyy}", input.Password, input.Certify,
                    acting != null && acting.UserId != ctx.Me.Id ? acting.UserId : null, ctx.Records.ComputeHash, input.Comment);
                if (!sig.Ok) return ActionOutcome.Fail(sig.Error!);
                ctx.Get<AuditService>().Record(r.Id, "Release scheduled", $"Becomes effective on {planned:dd MMM yyyy} (DOC-050)", phaseName: snap.Phase?.Name);
                await ctx.Db.SaveChangesAsync();
                return ActionOutcome.Ok($"Release signed. {r.Number} rev {r.Revision} becomes effective on {planned:dd MMM yyyy}.");
            }
            case "override_gate":
            {
                if (!(svc.IsDocController || svc.IsManager)) return ActionOutcome.Fail("Only Document Control can override the training gate (TRN-071).");
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("A justification is mandatory (TRN-071).");
                var gate = await svc.GateAsync(snap);
                if (!gate.Gated || gate.Satisfied) return ActionOutcome.Fail("The training gate is already met.");
                var sig = await ctx.Get<SignatureService>().SignAsync(snap, DocKeys.SignGateOverride, $"Release before training reached {gate.RequiredPercent}% ({gate.Stats.Percent}% complete)",
                    input.Password, input.Certify, null, ctx.Records.ComputeHash, input.Comment);
                if (!sig.Ok) return ActionOutcome.Fail(sig.Error!);
                ctx.Get<AuditService>().Record(r.Id, "Training gate overridden", $"{gate.Stats.Completed} of {gate.Stats.Active} trainees complete ({gate.Stats.Percent}%); {gate.RequiredPercent}% required (TRN-071)",
                    reason: input.Comment, phaseName: snap.Phase?.Name);
                await ctx.Db.SaveChangesAsync();
                return ActionOutcome.Ok("Training gate overridden. The revision can now be released.");
            }
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    // ------------------------------------------------------------------ panels

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var svc = Svc(ctx.Services);
        var training = ctx.Get<TrainingService>();
        var snap = ctx.Snapshot;
        var r = snap.Record;
        var tz = ctx.Records.TimeZone;
        var panels = new List<RecordPanel>();

        var banners = await BannersAsync(ctx, svc, snap);
        if (banners.Count > 0)
            panels.Add(new RecordPanel { Key = "doc-status", Title = "Status", Partial = "~/Modules/Documents/Views/_DocumentStatus.cshtml", Placement = "top", Model = new DocumentStatusModel(banners) });

        var history = await svc.RevisionHistoryAsync(r);
        panels.Add(new RecordPanel
        {
            Key = "revision-history", Title = "Revision history", Icon = "bi-clock-history", Order = 1, Count = history.Count,
            Partial = "~/Modules/Documents/Views/_RevisionHistory.cshtml", Model = new RevisionHistoryModel(r.Id, history, ctx.Directory, tz),
        });

        var courses = await training.CoursesForDocumentAsync(r.Id);
        if (snap.Bool("training_required") != null || courses.Count > 0)
        {
            var rows = new List<AssignmentRow>();
            foreach (var c in courses) rows.AddRange(await training.RowsAsync(training.AssignmentsOf(c.Id)));
            var detail = svc.IsManager || svc.IsDocController || svc.IsTrainingCoordinator || snap.UserId("owner") == ctx.Me.Id;
            var stats = await training.StatsAsync(courses.Select(c => c.Id).ToList());
            panels.Add(new RecordPanel
            {
                Key = "doc-training", Title = "Training", Icon = "bi-mortarboard", Order = 2, Count = stats.Active > 0 ? stats.Active : null,
                Partial = "~/Modules/Documents/Views/_DocumentTraining.cshtml",
                Model = new DocumentTrainingModel(r.Id, snap.Bool("training_required"), snap.Text("training_justification"), await svc.GateAsync(snap), courses, stats,
                    rows.OrderBy(x => x.Status == "Completed").ThenBy(x => x.Due).ToList(), detail, snap.PhaseKey == DocKeys.AwaitingRelease, ctx.Directory),
            });
        }

        var sigs = await ctx.Db.Signatures.AsNoTracking().Where(s => s.RecordId == r.Id && !s.Superseded).OrderBy(s => s.SignedAt).ToListAsync();
        if (sigs.Count > 0)
            panels.Add(new RecordPanel
            {
                Key = "approval-manifest", Title = "Approval manifest", Icon = "bi-patch-check", Placement = "side", Order = 1,
                Partial = "~/Modules/Documents/Views/_ApprovalManifest.cshtml", Model = new ApprovalManifestModel(r, sigs, tz),
            });

        if (await svc.ReviewInfoAsync(r) is { } info)
            panels.Add(new RecordPanel
            {
                Key = "review-card", Title = "Periodic review", Icon = "bi-calendar2-check", Placement = "side", Order = 2,
                Partial = "~/Modules/Documents/Views/_ReviewCard.cshtml", Model = new ReviewCardModel(info, ctx.Directory),
            });
        return panels;
    }

    private static async Task<List<DocBanner>> BannersAsync(RecordContext ctx, DocumentControlService svc, RecordSnapshot snap)
    {
        var r = snap.Record;
        var list = new List<DocBanner>();
        var current = await svc.EffectiveRevisionAsync(r.FamilyId);
        if (r.IsArchived && r.ArchiveReason == DocKeys.Superseded)
        {
            list.Add(current != null
                ? new DocBanner("warn", "bi-archive", $"Superseded: revision {current.Revision} is current",
                    $"Use revision {current.Revision}, effective {current.EffectiveDate:dd MMM yyyy}. This revision is kept for the record only (DOC-054, SAF-043).",
                    $"/records/{current.Id}", $"Open revision {current.Revision}")
                : new DocBanner("warn", "bi-archive", "Superseded revision", "This revision is kept for the record only (DOC-054)."));
        }
        else if (r.IsArchived && r.ArchiveReason == DocKeys.ObsoleteReason)
        {
            var or = await ctx.Db.Records.AsNoTracking().Where(x => x.ParentId == r.Id && x.FormId == ctx.Config.Form(DocKeys.ObsoleteRequest).Id && x.PhaseType == PhaseType.Completed)
                .OrderByDescending(x => x.ModifiedAt).FirstOrDefaultAsync();
            var replacement = or == null ? null : await ctx.Db.RecordValues.AsNoTracking().Where(v => v.RecordId == or.Id && v.FieldKey == "replacement" && v.RefGuid != null)
                .Select(v => v.RefGuid).FirstOrDefaultAsync();
            var repl = replacement is { } rid ? await ctx.Db.Records.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rid) : null;
            list.Add(new DocBanner("bad", "bi-slash-circle", "Obsolete: withdrawn from use",
                $"Made obsolete on {r.ArchivedAt:dd MMM yyyy}" + (or != null ? $" by {or.Number}" : "") + ". Don't use this document." +
                (repl != null ? $" It is replaced by {repl.Number}." : ""),
                repl != null ? $"/records/{repl.Id}" : or != null ? $"/records/{or.Id}" : null, repl != null ? $"Open {repl.Number}" : or != null ? $"Open {or.Number}" : null));
        }
        else if (!r.IsEffective && r.PhaseType == PhaseType.Normal)
        {
            list.Add(current != null && current.Id != r.Id
                ? new DocBanner("info", "bi-pencil", $"Draft of revision {r.Revision}: not for operational use",
                    $"Revision {current.Revision} stays in force (effective {current.EffectiveDate:dd MMM yyyy}) until this revision is released (SAF-043).",
                    $"/records/{current.Id}", $"Open revision {current.Revision}")
                : new DocBanner("info", "bi-pencil", "Draft: not yet in force", "This document isn't effective until Document Control releases it (DOC-080, SAF-043)."));
        }
        else if (r.PhaseType == PhaseType.Voided)
            list.Add(new DocBanner("bad", "bi-slash-circle", "Voided", "This revision was abandoned before release and is read-only (DOC-034)."));

        if (snap.PhaseKey == DocKeys.AwaitingRelease && r.PhaseType == PhaseType.Normal)
        {
            var planned = await svc.PlannedEffectiveAsync(snap);
            var signed = await svc.PhaseSignatureAsync(r.Id, DocKeys.SignReleased);
            list.Add(new DocBanner("violet", "bi-calendar-check",
                planned <= BusinessCalendar.Today ? "Ready to release" : $"Becomes effective {planned:dd MMM yyyy}",
                signed != null ? $"Release signed by {signed.SignerName}; Vellum makes it effective on {planned:dd MMM yyyy}." :
                planned <= BusinessCalendar.Today ? "The effective date has been reached. Document Control can release it now." :
                "Document Control can sign the release now (Release › Schedule release); it takes effect on that date."));
            var gate = await svc.GateAsync(snap);
            if (gate.Gated)
                list.Add(gate.Override != null
                    ? new DocBanner("warn", "bi-unlock", "Training gate overridden", $"Overridden by {gate.Override.SignerName}: {gate.Override.Comment}")
                    : new DocBanner(gate.Satisfied ? "ok" : "warn", "bi-mortarboard", gate.Satisfied ? "Training gate met" : "Waiting for training", gate.Message));
        }

        if (r.IsEffective)
            foreach (var item in await svc.PendingItemsAsync(r))
            {
                var (title, icon) = item.FormKey switch
                {
                    DocKeys.ObsoleteRequest => ("Obsoletion in progress", "bi-archive"),
                    DocKeys.ChangeRequest => ("Change requested", "bi-pencil-square"),
                    DocKeys.PeriodicReview => ("Periodic review in progress", "bi-calendar2-check"),
                    _ => ($"Revision {item.Revision} in progress", "bi-arrow-repeat"),
                };
                list.Add(new DocBanner(item.FormKey == DocKeys.ObsoleteRequest ? "warn" : "info", icon, title,
                    item.FormKey == DocKeys.Document ? $"The next revision is in {item.PhaseName}. This revision stays effective until it's released." : $"{item.Number} is in {item.PhaseName}.",
                    $"/records/{item.Id}", $"Open {(item.FormKey == DocKeys.Document ? "revision " + item.Revision : item.Number)}"));
            }
        return list;
    }
}

/// <summary>Common rules of Change Requests, Periodic Reviews and Obsolete Requests: created only from an effective document under INV-103.</summary>
public abstract class DocumentRequestBehavior : RecordBehavior
{
    protected abstract string FormKey { get; }
    public override IReadOnlyCollection<string> Forms => [FormKey];

    protected static DocumentControlService Svc(IServiceProvider sp) => sp.GetRequiredService<DocumentControlService>();

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        if (!ctx.IsNew) return;
        var target = ctx.Proposed.Main.Link("target_document");
        if (target == null)
        {
            ctx.Error(null, $"Start a {ctx.Form.Name.ToLowerInvariant()} from the document itself, using the Revision control menu of the effective revision.");
            return;
        }
        var svc = Svc(ctx.Services);
        var doc = await ctx.Services.GetRequiredService<VellumDbContext>().Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == target);
        if (doc == null || doc.FormId != svc.DocumentForm.Id) { ctx.Error(null, "That document doesn't exist."); return; }
        if (await svc.BlockerAsync(doc, FormKey) is { } block) { ctx.Error(null, block); return; }
        ctx.Set("doc_ref", DocumentControlService.DocRef(doc));
    }

    public override Task OnCreatedAsync(RecordContext ctx)
    {
        if (ctx.Record.ParentId == null || ctx.Record.ParentId != ctx.Snapshot.Data.Main.Link("target_document"))
            throw new WorkflowException("A revision-control request must be created as an activity of its target document.");
        return Task.CompletedTask;
    }

    protected static async Task<RecordSnapshot?> TargetAsync(RecordContext ctx) =>
        ctx.Snapshot.Data.Main.Link("target_document") is { } id ? await ctx.Records.LoadAsync(id) : null;

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var target = await TargetAsync(ctx);
        var doc = target?.Record;
        var readable = doc != null && await ctx.Access.CanReadAsync(doc.Id);
        var type = target?.Keyword("doc_type");
        return
        [
            new RecordPanel
            {
                Key = "request-target", Title = "Document", Placement = "top",
                Partial = "~/Modules/Documents/Views/_RequestTarget.cshtml",
                Model = new RequestTargetModel(readable ? doc : null, readable ? target!.Phase : null, type?.Label, type?.Color, target?.UserId("owner"), ctx.Directory, Note),
            },
        ];
    }

    protected abstract string Note { get; }
}

/// <summary>DOC-090..093: accepting a change request creates the next Draft revision in the same transaction.</summary>
public sealed class ChangeRequestBehavior : DocumentRequestBehavior
{
    protected override string FormKey => DocKeys.ChangeRequest;
    protected override string Note => "Accepting this request creates the next Draft revision of the document, copying its content and files (DOC-093).";

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "accepted") return;
        var target = await TargetAsync(ctx);
        if (target == null) { ctx.Error("The target document no longer exists."); return; }
        if (await Svc(ctx.Services).BlockerAsync(target.Record, DocKeys.ChangeRequest, except: ctx.Record.Id) is { } block) ctx.Error(block);
        var assign = ctx.Snapshot.UserIds("assign_to");
        if (assign.Count == 0) ctx.Error("Choose who the new revision is assigned to.");
        foreach (var id in assign.Where(id => ctx.Directory.Get(id) is not { IsActive: true }))
            ctx.Error($"{ctx.Directory.Name(id)} is inactive and can't be assigned the new revision (INV-073).");
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "accepted") return;
        var svc = Svc(ctx.Services);
        var cr = ctx.Snapshot;
        var target = await TargetAsync(ctx) ?? throw new WorkflowException("The target document no longer exists.");
        var notifyIds = cr.UserIds("notify");
        var assign = cr.UserIds("assign_to");
        var rev = await svc.CreateRevisionAsync(target, cr.Record.CreatedById, assign, cr.Date("revision_due"), cr.Text("reason") ?? cr.Record.Title,
            cr.Record.Id, $"Revision created from change request {cr.Record.Number} (DOC-093)", extraReaders: notifyIds);
        var notify = ctx.Get<NotificationService>();
        foreach (var uid in notifyIds.Where(u => u != ctx.Me.Id))
            notify.Notify(uid, NotificationKind.Info, $"New revision {rev.Revision} of {rev.Number} started",
                $"{cr.Record.Number} was accepted: {cr.Record.Title}. Revision {rev.Revision} is in Draft with {string.Join(", ", assign.Select(ctx.Directory.Name))}.", rev.Id);
        var now = DateTime.UtcNow;
        ctx.Db.Comments.Add(new Comment { RecordId = target.Record.Id, UserId = ctx.Me.Id, IsSystem = true, Kind = "system", At = now,
            Text = $"Change request {cr.Record.Number} accepted: revision {rev.Revision} created in Draft." });
        ctx.Db.Comments.Add(new Comment { RecordId = cr.Record.Id, UserId = ctx.Me.Id, IsSystem = true, Kind = "system", At = now,
            Text = $"Created {rev.Number} rev {rev.Revision} in Draft, assigned to {string.Join(", ", assign.Select(ctx.Directory.Name))}." });
    }
}

/// <summary>DOC-073..076: completing a periodic review resets the schedule, logs a review comment on the document and starts follow-up requests.</summary>
public sealed class PeriodicReviewBehavior : DocumentRequestBehavior
{
    protected override string FormKey => DocKeys.PeriodicReview;
    protected override string Note => "Completing the review sets the document's next review date and adds a review entry to its comment log (DOC-075).";

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        await base.OnSavingAsync(ctx);
        if (ctx.Proposed.Main.Date("next_review_date") is { } d && d <= BusinessCalendar.Today && ctx.Current?.Date("next_review_date") != d)
            ctx.Error("next_review_date", "The next review date must be in the future.");
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "completed") return;
        var target = await TargetAsync(ctx);
        if (target == null || !target.Record.IsEffective) { ctx.Error("The document is no longer effective, so this review can't be completed. Void it instead."); return; }
        if (ctx.Snapshot.KeywordKey("outcome") == "obsolete" && await Svc(ctx.Services).BlockerAsync(target.Record, DocKeys.ObsoleteRequest, except: ctx.Record.Id) is { } block)
            ctx.Error("Outcome Obsolete would start an obsolete request, but it's blocked: " + block);
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "completed") return;
        var svc = Svc(ctx.Services);
        var pr = ctx.Snapshot;
        var target = await TargetAsync(ctx) ?? throw new WorkflowException("The target document no longer exists.");
        var doc = target.Record;
        var outcome = pr.Keyword("outcome");
        var info = await svc.ReviewInfoAsync(doc);
        var now = DateTime.UtcNow;
        var text = $"Periodic review {pr.Record.Number} completed by {ctx.Me.Name}: {outcome?.Label}. {pr.Text("review_comments")}" +
                   (info?.NextReview is { } next ? $" Next review due {next:dd MMM yyyy}." : "");
        ctx.Db.Comments.Add(new Comment { RecordId = doc.Id, UserId = ctx.Me.IsSystem ? null : ctx.Me.Id, IsSystem = true, Kind = "review", At = now, Text = text });
        ctx.Get<AuditService>().Record(doc.Id, "Periodic review completed", $"{pr.Record.Number}: {outcome?.Label}", reason: pr.Text("review_comments"),
            newValue: info?.NextReview is { } nd ? $"Next review {nd:dd MMM yyyy}" : null);

        var owner = target.UserId("owner") ?? ctx.Me.Id;
        if (outcome?.Key == "change_required")
        {
            if (await svc.BlockerAsync(doc, DocKeys.ChangeRequest) is { } block)
                ctx.Db.Comments.Add(new Comment { RecordId = pr.Record.Id, IsSystem = true, Kind = "system", At = now, Text = "No change request was started: " + block });
            else
            {
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = DocKeys.ChangeRequest, ParentId = doc.Id, SkipAccessCheck = true,
                    Values = new()
                    {
                        ["target_document"] = doc.Id, ["summary"] = $"Update after periodic review {pr.Record.Number}",
                        ["reason"] = pr.Text("review_comments") ?? "Periodic review found that changes are required.",
                        ["approver"] = owner, ["assign_to"] = new List<int> { owner },
                    },
                    CreateComment = $"Started by periodic review {pr.Record.Number} (DOC-076)",
                });
                if (!res.Ok) throw new WorkflowException("Couldn't start the change request: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                await ctx.Records.LinkAsync(pr.Record.Id, res.Record!.Id, LinkKind.Source);
            }
        }
        else if (outcome?.Key == "obsolete")
        {
            var res = await ctx.Records.CreateAsync(new CreateRequest
            {
                FormKey = DocKeys.ObsoleteRequest, ParentId = doc.Id, SkipAccessCheck = true,
                Values = new() { ["target_document"] = doc.Id, ["reason"] = pr.Text("review_comments") ?? "Periodic review concluded the document is no longer needed." },
                CreateComment = $"Started by periodic review {pr.Record.Number} (DOC-076)",
            });
            if (!res.Ok) throw new WorkflowException("Couldn't start the obsolete request: " + string.Join("; ", res.Errors.Select(e => e.Message)));
            await ctx.Records.LinkAsync(pr.Record.Id, res.Record!.Id, LinkKind.Source);
        }
    }
}

/// <summary>DOC-101..103: accepting an obsolete request moves the document to Obsolete and the archive in the same transaction.</summary>
public sealed class ObsoleteRequestBehavior : DocumentRequestBehavior
{
    protected override string FormKey => DocKeys.ObsoleteRequest;
    protected override string Note => "Accepting this request makes the document obsolete: it leaves every effective view, its training is disabled and its distribution list is notified (DOC-103).";

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.Kind != RouteKind.Forward) return;
        var target = await TargetAsync(ctx);
        if (target == null) { ctx.Error("The target document no longer exists."); return; }
        if (await Svc(ctx.Services).BlockerAsync(target.Record, DocKeys.ObsoleteRequest, except: ctx.Record.Id) is { } block) ctx.Error(block);
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "accepted") return;
        var target = await TargetAsync(ctx) ?? throw new WorkflowException("The target document no longer exists.");
        var res = await ctx.Workflow.SystemRouteAsync(target.Record.Id, DocKeys.Obsolete, $"Obsolete request {ctx.Record.Number} accepted",
            comment: ctx.Snapshot.Text("reason"));
        if (!res.Ok) throw new WorkflowException("Couldn't make the document obsolete: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }
}
