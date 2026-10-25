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

namespace Vellum.Modules.Change;

/// <summary>Change template validation (CHG-001/002).</summary>
public sealed class ChangeTemplateBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["change_template"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        if (ChangeRules.FilingMismatch(ctx.KeywordKey("default_reg_impact"), ctx.KeywordKey("default_filing")) is { } msg)
            ctx.Error("default_filing", msg);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The change record: copy-on-create from its template (INV-147), approval coverage by type (INV-148), action plan →
/// Action Items (CHG-040), implementation and verification gates (INV-149), affected documents (INV-160) and the
/// regulatory completion gate (INV-161, SAF-022).
/// </summary>
public sealed class ChangeBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["change"];

    // ------------------------------------------------------------------ saving

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;

        // INV-147: the template is copied into the change when it's created (or re-picked in Draft), never referenced later.
        var templateId = main.Link("change_template");
        var before = ctx.Current?.Data.Main.Link("change_template");
        var inDraft = ctx.IsNew || ctx.Current?.PhaseKey == "draft";
        if (templateId is { } tid && (ctx.IsNew || (tid != before && inDraft)))
        {
            var tpl = await ctx.Services.GetRequiredService<RecordService>().LoadAsync(tid);
            if (tpl == null || tpl.Form.Key != "change_template")
                ctx.Error("change_template", "Pick a change template.");
            else if (tpl.Bool("enabled") == false)
                ctx.Error("change_template", $"The \"{tpl.Text("name")}\" template is disabled and can't be used for new changes (CHG-002).");
            else
                ApplyTemplate(ctx, tpl);
        }
        else if (templateId != before && !inDraft && ctx.Current != null)
        {
            ctx.Error("change_template", "The change template can only be changed in Draft.");
        }

        // Everyone listed in Approvals is assigned at once (CHG-021).
        var pool = ctx.Proposed.RowsOf("approvals").SelectMany(r => r.Values.Refs("approvers")).Distinct().ToList();
        ctx.Set("approver_pool", pool);

        // CHG-050: the filing follows the impact classification.
        var impact = ctx.KeywordKey("reg_impact");
        var filing = ctx.KeywordKey("filing");
        if (impact != null && filing == null && ChangeRules.FilingsFor(impact) is { Count: 1 } only)
        {
            ctx.Set("filing", only[0]);
            filing = only[0];
        }
        if (ChangeRules.FilingMismatch(impact, filing) is { } mismatch) ctx.Error("filing", mismatch);

        var today = BusinessCalendar.Today;
        var submitted = main.Date("submission_date");
        if (submitted > today) ctx.Error("submission_date", "The submission date can't be in the future.");
        if (main.Date("approval_date") is { } approved)
        {
            if (approved > today) ctx.Error("approval_date", "The agency approval date can't be in the future.");
            if (submitted is { } s && approved < s) ctx.Error("approval_date", "The agency approval date can't be before the submission date.");
        }
        if (main.Date("agency_response_date") is { } resp && submitted is { } sub && resp < sub)
            ctx.Error("agency_response_date", "The agency response can't be before the submission date.");

        // Segregation of duties (SAF-044): the verifier isn't the change owner.
        if (main.Bool("verification_required") == true && main.Ref("verifier") is { } v && v == main.Ref("owner"))
            ctx.Error("verifier", "The verification user must be someone other than the change owner (segregation of duties).");
    }

    /// <summary>SAF-044: every verified action has a verifier other than the person doing it.</summary>
    private static IEnumerable<string> ActionRowErrors(RecordSnapshot snap, string section, string actionKey, string assigneeKey, string verifyKey, string verifierKey)
    {
        var owner = snap.UserId("owner") ?? snap.Record.CreatedById;
        foreach (var row in snap.Rows(section).Where(r => r.CreatedRecordId == null && r.Values.Bool(verifyKey) == true))
        {
            var assignee = row.Values.Ref(assigneeKey) ?? owner;
            if (ModuleKit.ResolveVerifier(row.Values.Ref(verifierKey), assignee, owner, snap.UserId("verifier")) == null)
                yield return $"\"{row.Values.Text(actionKey)}\" needs a verifier other than the person doing it (SAF-044). Pick one in the Verifier column.";
        }
    }

    private static void ApplyTemplate(SavingContext ctx, RecordSnapshot tpl)
    {
        var main = ctx.Proposed.Main;
        var form = ctx.Form;
        var cfg = ctx.Config;
        ctx.Set("change_type", tpl.Text("name"));
        ctx.Set("all_approvers", tpl.Bool("all_approvers") == true);
        ctx.Set("ia_required", tpl.Bool("ia_required") == true);
        if (!main.Has("impact_level") && tpl.KeywordKey("default_impact") is { } imp) ctx.Set("impact_level", imp);
        if (!main.Has("verification_required")) ctx.Set("verification_required", tpl.Bool("verification_required") ?? true);
        if (!main.Has("verification_instructions") && tpl.Text("verification_instructions") is { } vi) ctx.Set("verification_instructions", vi);
        if (!main.Has("reg_impact") && tpl.KeywordKey("default_reg_impact") is { } ri) ctx.Set("reg_impact", ri);
        if (!main.Has("filing") && tpl.KeywordKey("default_filing") is { } fi) ctx.Set("filing", fi);

        var approvals = ctx.Proposed.RowsOf("approvals");
        if (approvals.Count == 0)
            foreach (var r in tpl.Rows("template_approvals"))
                approvals.Add(ModuleKit.NewRow(form, cfg, approvals.Count, new()
                {
                    ["approval_type"] = r.Values.Ref("approval_type") is { } k ? cfg.Keyword(k) : null,
                    ["approvers"] = r.Values.Refs("approvers").ToList(),
                }));
        var impacts = ctx.Proposed.RowsOf("impacts");
        if (impacts.Count == 0)
            foreach (var r in tpl.Rows("template_impacts"))
                impacts.Add(ModuleKit.NewRow(form, cfg, impacts.Count, new()
                {
                    ["area"] = r.Values.Ref("area") is { } k ? cfg.Keyword(k) : null,
                    ["assessor"] = r.Values.Ref("assessor"),
                    ["ia_due"] = BusinessCalendar.Today.AddDays(14),
                }));
        var plan = ctx.Proposed.RowsOf("plan");
        if (plan.Count == 0)
            foreach (var r in tpl.Rows("template_plan"))
                plan.Add(ModuleKit.NewRow(form, cfg, plan.Count, new()
                {
                    ["action"] = r.Values.Text("action"),
                    ["days"] = r.Values.Number("days") ?? 10m,
                    ["verify"] = r.Values.Bool("verify") == true,
                }));
    }

    // ------------------------------------------------------------------ routing

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.Kind is not (RouteKind.Forward or RouteKind.SpecialForward)) return;
        var snap = ctx.Snapshot;
        if (ctx.From.Key is "draft" or "review")
        {
            foreach (var e in ActionRowErrors(snap, "plan", "action", "assignee", "verify", "plan_verifier")) ctx.Error(e);
            foreach (var e in ActionRowErrors(snap, "post_impl", "post_action", "post_assignee", "post_verify", "post_verifier")) ctx.Error(e);
        }
        if (ctx.From.Key == "review" && ctx.To.Key == "approval")
        {
            var pool = snap.UserIds("approver_pool");
            if (pool.Count == 0) ctx.Error("Add at least one approval type with approvers before sending the change for approval.");
            foreach (var id in pool.Where(id => ctx.Directory.Get(id) is not { Assignable: true }))
                ctx.Error($"{ctx.Directory.Name(id)} can't be assigned any more. Replace them in Approvals.");
        }
        if (ctx.From.Key == "approval") await ValidateApprovalVoteAsync(ctx);
        if (ctx.From.Key == "implementation")
        {
            foreach (var (section, key, label) in new[] { ("plan", "action", "action plan"), ("post_impl", "post_action", "post-implementation") })
            {
                var pending = snap.Rows(section).Count(r => r.CreatedRecordId == null && r.Values.Has(key));
                if (pending > 0)
                    ctx.Error($"{pending} {label} row{(pending == 1 ? " hasn't" : "s haven't")} been turned into action items yet. Use Actions › Create post-implementation action items (INV-149).");
            }
        }
        if (ctx.From.Key == "verification" && string.IsNullOrWhiteSpace(snap.Text("verification_evidence")) && !ModuleKit.HasAttachment(snap, "verification_files"))
            ctx.Error("Record the objective evidence of verification before completing the change (INV-149).");
        if (ctx.To.Type == PhaseType.Completed)
            foreach (var b in await CompletionBlockersAsync(ctx, snap)) ctx.Error(b);
    }

    /// <summary>
    /// INV-148: each approval vote records the approver's types. Approvers whose types are all covered are released
    /// (unless the template requires every approver), so the phase's quorum is reached exactly when every type is covered.
    /// </summary>
    private static async Task ValidateApprovalVoteAsync(RoutingContext ctx)
    {
        var acting = await ctx.Access.ActingAssignmentAsync(ctx.Record.Id);
        if (acting == null) return;
        var requireAll = ctx.Snapshot.Bool("all_approvers") == true;
        var approved = await ApprovedByAsync(ctx.Db, ctx.Record.Id);
        approved.Add(acting.UserId);
        var coverage = ChangeRules.Coverage(Requirements(ctx.Snapshot), approved, requireAll);
        var others = await ctx.Db.Assignments.Where(a => a.PhaseEntryId == acting.PhaseEntryId && a.Id != acting.Id &&
            (a.Status == AssignmentStatus.Active || a.Status == AssignmentStatus.Waiting)).ToListAsync();
        var released = new List<int>();
        foreach (var o in others.Where(o => coverage.Complete || ChangeRules.IsReleasable(coverage, o.UserId, requireAll)))
        {
            o.Status = AssignmentStatus.Cancelled;
            o.CompletedAt = DateTime.UtcNow;
            released.Add(o.UserId);
        }
        if (!coverage.Complete && others.All(o => o.Status == AssignmentStatus.Cancelled))
        {
            var missing = coverage.Uncovered.Select(t => t.TypeLabel).ToList();
            ctx.Error(missing.Count == 0
                ? "The change has no approval types. Reject it back to Review and add the approvals."
                : $"The change can't leave Approval until {string.Join(", ", missing)} {(missing.Count == 1 ? "has" : "have")} an approval (INV-148), and nobody assigned can give it. Reject the change back to Review to correct the approvers.");
            return;
        }
        if (released.Count > 0)
            ctx.Get<AuditService>().Record(ctx.Record.Id, "Approvers released",
                $"{string.Join(", ", released.Select(ctx.Directory.Name))} no longer needed: " +
                (coverage.Complete ? "every approval type is approved." : "their approval types are already approved."),
                phaseName: ctx.From.Name);
    }

    public static async Task<HashSet<int>> ApprovedByAsync(VellumDbContext db, Guid recordId)
    {
        var sigs = await db.Signatures.AsNoTracking()
            .Where(s => s.RecordId == recordId && !s.Superseded && s.PhaseKey == "approval" && s.Meaning == "Approved")
            .Select(s => new { s.SignerId, s.OnBehalfOfId }).ToListAsync();
        return sigs.Select(s => s.OnBehalfOfId ?? s.SignerId).ToHashSet();
    }

    public static List<ApprovalRequirement> Requirements(RecordSnapshot snap) =>
        snap.Rows("approvals")
            .Select(r => (Type: snap.Config.Keyword(r.Values.Ref("approval_type")), Row: r))
            .Where(x => x.Type != null)
            .Select(x => new ApprovalRequirement(x.Type!.Key, x.Type.Label, x.Row.Values.Refs("approvers")))
            .ToList();

    /// <summary>Everything that stops the change from being Completed right now (INV-160, INV-161, CHG-031).</summary>
    public static async Task<List<string>> CompletionBlockersAsync(RecordContext ctx, RecordSnapshot snap)
    {
        var blockers = ChangeRules.RegulatoryBlockers(snap.KeywordKey("reg_impact"), snap.KeywordKey("filing"), snap.Date("submission_date"),
            snap.Date("agency_response_date"), snap.Date("approval_date"), ModuleKit.HasAttachment(snap, "approval_evidence"), BusinessCalendar.Today);

        if (snap.Bool("ia_required") == true)
        {
            var open = (await ModuleKit.ChildrenAsync(ctx, snap.Record.Id, "impact_assessment")).Where(c => c.Open).Select(c => c.Record.Number).ToList();
            if (open.Count > 0) blockers.Add($"This change type needs its impact assessments completed first: {string.Join(", ", open)} (CHG-031).");
        }
        blockers.AddRange(await DocumentBlockersAsync(ctx, snap));
        return blockers;
    }

    /// <summary>INV-160: linked affected documents must have reached their target state (no dependency on Document Control's form keys).</summary>
    public static async Task<List<string>> DocumentBlockersAsync(RecordContext ctx, RecordSnapshot snap)
    {
        var result = new List<string>();
        foreach (var d in await DocumentStatesAsync(ctx, snap))
            if (d.Linked && !d.Done) result.Add($"Affected document {d.Label}: {d.State.ToLowerInvariant()} (INV-160).");
        return result;
    }

    public sealed record DocumentState(string Action, string ActionLabel, string Label, Guid? Id, bool Linked, bool Done, string State);

    public static async Task<List<DocumentState>> DocumentStatesAsync(RecordContext ctx, RecordSnapshot snap)
    {
        var rows = snap.Rows("documents");
        var ids = rows.Select(r => r.Values.Link("document")).OfType<Guid>().ToList();
        var docs = await ctx.Db.Records.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync();
        var families = docs.Where(r => r.FamilyId != null).Select(r => r.FamilyId!.Value).Distinct().ToList();
        var revisions = await ctx.Db.Records.AsNoTracking().Where(r => r.FamilyId != null && families.Contains(r.FamilyId.Value))
            .Select(r => new { r.FamilyId, r.RevisionOrdinal, r.IsEffective, r.Revision }).ToListAsync();
        var list = new List<DocumentState>();
        foreach (var row in rows)
        {
            var action = snap.KeywordKey(row, "doc_action") ?? "change";
            var actionLabel = snap.Config.Keyword(row.Values.Ref("doc_action"))?.Label ?? "Change";
            var id = row.Values.Link("document");
            var doc = id is { } g ? docs.FirstOrDefault(x => x.Id == g) : null;
            var label = doc != null ? $"{doc.Number}{(doc.Revision != null ? " rev " + doc.Revision : "")} {doc.Title}" : row.Values.Text("doc_ref") ?? "(not linked)";
            if (doc == null) { list.Add(new(action, actionLabel, label, null, false, false, "Not linked")); continue; }
            var fam = doc.FamilyId is { } f ? revisions.Where(x => x.FamilyId == f).ToList() : [];
            bool done;
            string state;
            switch (action)
            {
                case "obsolete":
                    done = doc.IsArchived || doc.PhaseType == PhaseType.Obsolete || (fam.Count > 0 && !fam.Any(x => x.IsEffective));
                    state = done ? "Obsolete" : "Obsoletion pending";
                    break;
                case "new":
                    done = doc.IsEffective || fam.Any(x => x.IsEffective) || (doc.FamilyId == null && doc.PhaseType == PhaseType.Completed);
                    state = done ? "Effective" : "New document not yet effective";
                    break;
                default:
                    var newer = fam.Where(x => x.RevisionOrdinal > doc.RevisionOrdinal && x.IsEffective).ToList();
                    done = newer.Count > 0 || (doc.FamilyId == null && doc.PhaseType == PhaseType.Completed);
                    state = done ? $"Revised (rev {newer.FirstOrDefault()?.Revision ?? "new"} effective)" : "New revision not yet effective";
                    break;
            }
            list.Add(new(action, actionLabel, label, doc.Id, true, done, state));
        }
        return list;
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var snap = ctx.Snapshot;
        if (ctx.To.Key == "implementation")
        {
            var owner = snap.UserId("owner") ?? snap.Record.CreatedById;
            await ModuleKit.CreateActionItemsAsync(ctx, "plan", "action", "assignee", "days", "verify", "plan_verifier", owner, snap.UserId("verifier"), "Action plan");
            await ModuleKit.CreateActionItemsAsync(ctx, "post_impl", "post_action", "post_assignee", "post_days", "post_verify", "post_verifier", owner, snap.UserId("verifier"), "Post-implementation actions");
            if (snap.KeywordKey("filing") == "pas" && snap.Date("approval_date") == null)
                ctx.Get<NotificationService>().ToGroup("regulatory", NotificationKind.Info, $"{snap.Record.Number} needs PAS approval before it can be completed",
                    $"{snap.Record.Title} is in Implementation. Record the agency approval date and letter when FDA approves the supplement (CHG-051).", snap.Record.Id);
        }
        // SAF-022: no path (escalation, auto-route, administrative route) completes a change whose regulatory preconditions are unmet.
        if (ctx.To.Type == PhaseType.Completed && ctx.IsSystem)
        {
            var blockers = await CompletionBlockersAsync(ctx, snap);
            if (blockers.Count > 0) throw new WorkflowException(string.Join(" ", blockers));
        }
    }

    // ------------------------------------------------------------------ actions

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var list = new List<RecordAction>();
        var phase = ctx.Snapshot.PhaseKey;
        if (ctx.Record.PhaseType != PhaseType.Normal || !await ModuleKit.CanActAsync(ctx)) return list;
        if (phase is "draft" or "review" or "approval")
        {
            var pending = ctx.Snapshot.Rows("impacts").Count(r => r.CreatedRecordId == null);
            var incomplete = ctx.Snapshot.Rows("impacts").Count(r => r.CreatedRecordId == null && (r.Values.Ref("area") == null || r.Values.Ref("assessor") == null));
            list.Add(new RecordAction
            {
                Key = "assign_ia", Label = "Assign impact assessments", Icon = "bi-bullseye", Group = "Impact",
                Description = pending > 0 ? $"Creates {pending} linked impact assessment record{(pending == 1 ? "" : "s")} in Assessment (CHG-030)." : "Creates linked impact assessment records in Assessment.",
                DisabledReason = pending == 0 ? "Add impact assessment rows (Impact tab) and save first." : incomplete > 0 ? "Every impact assessment row needs an area and an assessor." : null,
            });
        }
        if (phase == "implementation")
        {
            var pending = ctx.Snapshot.Rows("post_impl").Count(r => r.CreatedRecordId == null);
            list.Add(new RecordAction
            {
                Key = "post_items", Label = "Create post-implementation action items", Icon = "bi-check2-all", Group = "Implementation",
                Description = "Turns new post-implementation rows into Action Items. They must be complete before Verification (CHG-044).",
                DisabledReason = pending == 0 ? "Add post-implementation actions (Implementation tab) and save first." : null,
            });
        }
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (ctx.Record.PhaseType != PhaseType.Normal || !await ModuleKit.CanActAsync(ctx))
            return ActionOutcome.Fail("Only the current assignee or an application manager can do this.");
        return actionKey switch
        {
            "assign_ia" => await AssignImpactAssessmentsAsync(ctx),
            "post_items" => await ModuleKit.InTransactionAsync(ctx, async () =>
            {
                var snap = ctx.Snapshot;
                var owner = snap.UserId("owner") ?? snap.Record.CreatedById;
                var n = await ModuleKit.CreateActionItemsAsync(ctx, "post_impl", "post_action", "post_assignee", "post_days", "post_verify", "post_verifier", owner, snap.UserId("verifier"), "Post-implementation actions");
                return n == 0 ? ActionOutcome.Fail("There are no new post-implementation actions.") : ActionOutcome.Ok($"{n} post-implementation action item{(n == 1 ? "" : "s")} created.");
            }),
            _ => ActionOutcome.Fail("This action isn't available."),
        };
    }

    private static Task<ActionOutcome> AssignImpactAssessmentsAsync(RecordContext ctx) => ModuleKit.InTransactionAsync(ctx, async () =>
    {
        if (ctx.Snapshot.PhaseKey is not ("draft" or "review" or "approval"))
            return ActionOutcome.Fail("Impact assessments are assigned in Draft, Review or Approval.");
        var calendar = ctx.Get<BusinessCalendar>();
        var snap = await ctx.Records.LoadRequiredAsync(ctx.Record.Id);
        var proposed = new RecordData();
        var rows = proposed.RowsOf("impacts");
        var created = new List<string>();
        foreach (var row in snap.Rows("impacts"))
        {
            var clone = row.Clone();
            if (row.CreatedRecordId == null)
            {
                if (row.Values.Ref("area") is not { } area || row.Values.Ref("assessor") is not { } assessor)
                    return ActionOutcome.Fail($"Impact assessment row {row.Order + 1} needs an area and an assessor.");
                var due = row.Values.Date("ia_due") is { } d && d >= BusinessCalendar.Today ? d : await calendar.AddAsync(BusinessCalendar.Today, 5, true);
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "impact_assessment",
                    StartPhaseKey = "assessment",
                    ParentId = snap.Record.Id,
                    SkipAccessCheck = true,
                    Assignees = [assessor],
                    DueDate = due,
                    Values = new()
                    {
                        ["area"] = area, ["assessor"] = assessor, ["due_date"] = due,
                        ["change_ref"] = $"{snap.Record.Number} {snap.Record.Title}",
                        ["instructions"] = $"Assess the impact of this change on {snap.Config.Keyword(area)?.Label.ToLowerInvariant()}.\n\nProposed change: {snap.Text("proposed_change")}",
                    },
                    CreateComment = $"Assigned from {snap.Record.Number} (CHG-030)",
                });
                if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                clone.CreatedRecordId = res.Record!.Id;
                created.Add(res.Record.Number);
            }
            rows.Add(clone);
        }
        if (created.Count == 0) return ActionOutcome.Fail("Every impact assessment is already assigned.");
        var save = await ctx.Records.SaveAsync(await ctx.Records.LoadRequiredAsync(snap.Record.Id), proposed, new SaveOptions { System = true });
        if (!save.Ok) return ActionOutcome.Fail(save.Errors.Select(e => e.Message).ToArray());
        return ActionOutcome.Ok($"Assigned {string.Join(", ", created)}.");
    });

    // ------------------------------------------------------------------ panels

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var snap = ctx.Snapshot;
        var panels = new List<RecordPanel>();
        var status = await ChangeStatusModel.BuildAsync(ctx, snap);
        if (status.Show)
            panels.Add(new RecordPanel { Key = "chg-status", Title = "Change status", Partial = "~/Modules/Change/Views/_ChangeStatus.cshtml", Model = status, Placement = "top" });
        var impact = await ImpactSummaryModel.BuildAsync(ctx, snap);
        panels.Add(new RecordPanel { Key = "chg-impact", Title = "Impact summary", Icon = "bi-bullseye", Partial = "~/Modules/Change/Views/_ImpactSummary.cshtml", Model = impact, Placement = "side", Order = 1 });
        return panels;
    }
}

/// <summary>Impact assessments: keep the change reference and tell the change owner when one completes.</summary>
public sealed class ImpactAssessmentBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["impact_assessment"];

    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.Text("change_ref") != null || ctx.Record.ParentId is not { } pid) return;
        var parent = await ctx.Db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == pid);
        if (parent != null) await ctx.Records.UpdateAsync(ctx.Record.Id, new() { ["change_ref"] = $"{parent.Number} {parent.Title}" });
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Type != PhaseType.Completed || ctx.Record.ParentId is not { } pid) return;
        var parent = await ctx.Records.LoadAsync(pid);
        if (parent?.UserId("owner") is not { } owner || owner == ctx.Me.Id) return;
        ctx.Get<NotificationService>().Notify(owner, NotificationKind.Info, $"Impact assessment {ctx.Record.Number} completed",
            $"{ctx.Snapshot.Keyword("impact")?.Label ?? "Assessed"} impact on {ctx.Snapshot.Keyword("area")?.Label.ToLowerInvariant()} for {parent.Record.Number}.", pid);
    }
}

// ---------------------------------------------------------------------- panel models

public sealed class ChangeStatusModel
{
    public required RecordSnapshot Snap { get; init; }
    public required UserDirectory Dir { get; init; }
    public required TimeZoneInfo Tz { get; init; }
    public required ApprovalCoverage Coverage { get; init; }
    public bool RequireAll { get; init; }
    public bool InApproval => Snap.PhaseKey == "approval";
    public bool PastApproval { get; init; }
    /// <summary>Signer → signed at (UTC) for the current approval round.</summary>
    public Dictionary<int, DateTime> ApprovedAt { get; init; } = [];
    /// <summary>Approvers whose assignment was released because their types were covered.</summary>
    public HashSet<int> Released { get; init; } = [];
    public HashSet<int> Pending { get; init; } = [];
    public string? Filing { get; init; }
    public Keyword? FilingKeyword { get; init; }
    public List<string> RegulatoryBlockers { get; init; } = [];
    public List<string> CompletionBlockers { get; init; } = [];
    public bool ShowCompletion { get; init; }
    public DateOnly? CbeFrom { get; init; }
    public bool Show => (Coverage.Types.Count > 0 && Snap.PhaseKey != "draft") || Filing is "pas" or "cbe30" or "cbe0";

    public static async Task<ChangeStatusModel> BuildAsync(RecordContext ctx, RecordSnapshot snap)
    {
        var sigs = await ctx.Db.Signatures.AsNoTracking()
            .Where(s => s.RecordId == snap.Record.Id && !s.Superseded && s.PhaseKey == "approval" && s.Meaning == "Approved").ToListAsync();
        var approvedAt = sigs.GroupBy(s => s.OnBehalfOfId ?? s.SignerId).ToDictionary(g => g.Key, g => g.Min(s => s.SignedAt));
        var requireAll = snap.Bool("all_approvers") == true;
        var coverage = ChangeRules.Coverage(ChangeBehavior.Requirements(snap), approvedAt.Keys.ToHashSet(), requireAll);
        var released = new HashSet<int>();
        var pending = new HashSet<int>();
        if (snap.PhaseKey == "approval")
        {
            var entryId = await ctx.Db.PhaseEntries.AsNoTracking().Where(e => e.RecordId == snap.Record.Id && e.ExitedAt == null).OrderByDescending(e => e.Id).Select(e => e.Id).FirstOrDefaultAsync();
            var assignments = await ctx.Db.Assignments.AsNoTracking().Where(a => a.PhaseEntryId == entryId).ToListAsync();
            foreach (var a in assignments.Where(a => a.Status == AssignmentStatus.Cancelled && !approvedAt.ContainsKey(a.UserId))) released.Add(a.UserId);
            foreach (var a in assignments.Where(a => a.Status is AssignmentStatus.Active or AssignmentStatus.Waiting)) pending.Add(a.UserId);
        }
        var filing = snap.KeywordKey("filing");
        var phase = snap.PhaseKey;
        var showCompletion = phase is "implementation" or "verification";
        return new ChangeStatusModel
        {
            Snap = snap, Dir = ctx.Directory, Tz = ctx.Records.TimeZone, Coverage = coverage, RequireAll = requireAll,
            PastApproval = snap.Record.PhaseType != PhaseType.Normal || phase is "implementation" or "verification",
            ApprovedAt = approvedAt, Released = released, Pending = pending,
            Filing = filing, FilingKeyword = snap.Keyword("filing"),
            RegulatoryBlockers = ChangeRules.RegulatoryBlockers(snap.KeywordKey("reg_impact"), filing, snap.Date("submission_date"), snap.Date("agency_response_date"),
                snap.Date("approval_date"), ModuleKit.HasAttachment(snap, "approval_evidence"), BusinessCalendar.Today),
            CompletionBlockers = showCompletion ? await ChangeBehavior.CompletionBlockersAsync(ctx, snap) : [],
            ShowCompletion = showCompletion,
            CbeFrom = filing == "cbe30" ? ChangeRules.CbeEffectiveFrom(snap.Date("submission_date"), snap.Date("agency_response_date")) : null,
        };
    }
}

public sealed class ImpactSummaryModel
{
    public required RecordSnapshot Snap { get; init; }
    public required List<ChildInfo> Assessments { get; init; }
    public required List<ChangeBehavior.DocumentState> Documents { get; init; }
    public Keyword? Highest { get; init; }
    public int Open => Assessments.Count(a => a.Open);
    public int ActionItemsOpen { get; init; }
    public int ActionItemsTotal { get; init; }

    private static readonly string[] Rank = ["high", "medium", "low", "na"];

    public static async Task<ImpactSummaryModel> BuildAsync(RecordContext ctx, RecordSnapshot snap)
    {
        var ias = await ModuleKit.ChildrenAsync(ctx, snap.Record.Id, "impact_assessment", "impact", "area", "assessor");
        var impacts = ias.Where(a => a.Record.PhaseType != PhaseType.Voided).Select(a => ctx.Config.Keyword(a.Ref("impact"))).OfType<Keyword>().ToList();
        var highest = impacts.OrderBy(k => Array.IndexOf(Rank, k.Key) is var i && i < 0 ? 99 : i).FirstOrDefault();
        var items = await ModuleKit.ChildrenAsync(ctx, snap.Record.Id, "action_item");
        return new ImpactSummaryModel
        {
            Snap = snap, Assessments = ias.Where(a => a.Record.PhaseType != PhaseType.Voided).ToList(), Documents = await ChangeBehavior.DocumentStatesAsync(ctx, snap),
            Highest = highest, ActionItemsTotal = items.Count(i => i.Record.PhaseType != PhaseType.Voided), ActionItemsOpen = items.Count(i => i.Open),
        };
    }
}
