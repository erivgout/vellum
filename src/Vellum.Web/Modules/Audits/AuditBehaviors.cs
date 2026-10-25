using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Modules.Change;

namespace Vellum.Modules.Audits;

/// <summary>
/// Audit plans: "Schedule audit" (AUD-012) creates the audit in Scheduled and links it both ways; "Remove planned audit"
/// (AUD-013) voids the scheduled audit and its recommendations in the same transaction as the row removal (INV-165).
/// </summary>
public sealed class AuditPlanBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["audit_plan"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        if (main.Date("period_start") is { } ps && main.Date("period_end") is { } pe && pe < ps)
            ctx.Error("period_end", "The period can't end before it starts.");
        foreach (var row in ctx.Proposed.RowsOf("planned_audits"))
            if (row.Values.Date("planned_start") is { } s && row.Values.Date("planned_end") is { } e && e < s)
                ctx.Error("planned_end", $"{row.Values.Text("auditee")}: the planned end can't be before the start.", row.IsNew ? null : row.Id);

        if (ctx.Current == null) return;
        var kept = ctx.Proposed.RowsOf("planned_audits").Select(r => r.Id).ToHashSet();
        var removed = ctx.Current.Rows("planned_audits").Where(r => !kept.Contains(r.Id)).ToList();
        if (removed.Count == 0) return;
        if (ctx.Current.PhaseKey is not ("draft" or "approval"))
        {
            ctx.Error("planned_audits", "Planned audits can only be removed while the plan is in Draft or Approval (AUD-013).");
            return;
        }
        var db = ctx.Services.GetRequiredService<Vellum.Data.VellumDbContext>();
        foreach (var row in removed.Where(r => r.CreatedRecordId != null))
        {
            var audit = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == row.CreatedRecordId);
            if (audit != null && audit.PhaseType != PhaseType.Voided)
                ctx.Error("planned_audits", $"{row.Values.Text("auditee")} was scheduled as {audit.Number}. Use Actions › Remove planned audit: the audit, its checklist and recommendations are voided with it (INV-165).");
        }
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var list = new List<RecordAction>();
        var snap = ctx.Snapshot;
        if (snap.Record.PhaseType == PhaseType.Voided || !await CanManageAsync(ctx)) return list;
        var phase = snap.PhaseKey;
        if (phase is "active" or "completed")
        {
            var pending = snap.Rows("planned_audits").Where(r => r.CreatedRecordId == null).ToList();
            list.Add(new RecordAction
            {
                Key = "schedule", Label = "Schedule audit", Icon = "bi-calendar-plus", Group = "Audits",
                Description = "Creates the audit in Scheduled, assigned to its lead auditor, with the audit type's checklists (AUD-012).",
                DisabledReason = pending.Count == 0 ? "Every planned audit is already scheduled." : null,
                Inputs =
                [
                    new ActionField("row", "Planned audit", "select", true, Options: pending.Select(r => (r.Id.ToString(), Label(r))).ToList(),
                        Default: pending.FirstOrDefault()?.Id.ToString()),
                    new ActionField("lead", "Lead auditor", "user", false, "Defaults to the row's lead auditor, then the program lead."),
                ],
            });
        }
        if (phase is "draft" or "approval")
        {
            var scheduled = snap.Rows("planned_audits").Where(r => r.CreatedRecordId != null).ToList();
            if (scheduled.Count > 0)
                list.Add(new RecordAction
                {
                    Key = "remove", Label = "Remove planned audit", Icon = "bi-calendar-x", Group = "Audits", Variant = "danger",
                    Description = "Removes a planned audit that was already scheduled.",
                    Confirm = "The scheduled audit, its checklist and its recommendations will be voided (INV-165). Linked CAPAs are not voided.",
                    RequiresComment = true, CommentLabel = "Reason",
                    Inputs = [new ActionField("row", "Planned audit", "select", true, Options: scheduled.Select(r => (r.Id.ToString(), Label(r))).ToList())],
                });
        }
        return list;
    }

    private static string Label(RowBag r) =>
        $"{r.Values.Text("auditee")} · {r.Values.Date("planned_start")?.ToString("dd MMM yyyy") ?? "no date"}";

    private static async Task<bool> CanManageAsync(RecordContext ctx) =>
        await ModuleKit.CanActAsync(ctx) || ctx.Snapshot.UserId("lead_auditor") == ctx.Me.Id;

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (!await CanManageAsync(ctx)) return ActionOutcome.Fail("Only the plan's assignee, its lead auditor or an application manager can do this.");
        if (!Guid.TryParse(input.Get("row"), out var rowId)) return ActionOutcome.Fail("Choose a planned audit.");
        return actionKey switch
        {
            "schedule" => await ScheduleAsync(ctx, rowId, int.TryParse(input.Get("lead"), out var lead) ? lead : null),
            "remove" => await RemoveAsync(ctx, rowId, input.Comment),
            _ => ActionOutcome.Fail("This action isn't available."),
        };
    }

    /// <summary>AUD-012: create the audit in Scheduled from a planned row and link plan and audit both ways.</summary>
    public static Task<ActionOutcome> ScheduleAsync(RecordContext ctx, Guid rowId, int? leadOverride) => ModuleKit.InTransactionAsync(ctx, async () =>
    {
        var plan = await ctx.Records.LoadRequiredAsync(ctx.Record.Id);
        if (plan.PhaseKey is not ("active" or "completed")) return ActionOutcome.Fail("Audits are scheduled from Active or Completed plans (AUD-012).");
        var row = plan.Rows("planned_audits").FirstOrDefault(r => r.Id == rowId);
        if (row == null) return ActionOutcome.Fail("That planned audit no longer exists.");
        if (row.CreatedRecordId != null) return ActionOutcome.Fail("That planned audit is already scheduled.");

        var lead = leadOverride ?? row.Values.Ref("planned_lead") ?? plan.UserId("lead_auditor");
        if (lead is not { } leadId || ctx.Directory.Get(leadId) is not { Assignable: true }) return ActionOutcome.Fail("Choose an active lead auditor.");
        var calendar = ctx.Get<BusinessCalendar>();
        var start = row.Values.Date("planned_start") ?? BusinessCalendar.Today;
        var end = row.Values.Date("planned_end") ?? start;
        var due = end >= BusinessCalendar.Today ? end : await calendar.AddAsync(BusinessCalendar.Today, 10, true);
        var typeId = row.Values.Link("audit_type");
        var typeName = typeId is { } tid ? (await ctx.Records.LoadAsync(tid))?.Text("name") : null;
        var program = plan.KeywordKey("program");
        var entity = plan.Config.Keyword(row.Values.Ref("entity_kind"))?.Key;
        var auditee = row.Values.Text("auditee") ?? "Audit";

        var res = await ctx.Records.CreateAsync(new CreateRequest
        {
            FormKey = "audit",
            StartPhaseKey = "scheduled",
            SkipAccessCheck = true,
            Assignees = [leadId],
            DueDate = due,
            Values = new()
            {
                ["subject"] = typeName != null ? $"{auditee} – {typeName}" : auditee,
                ["audit_kind"] = program == "supplier" || entity == "supplier" ? "supplier" : "internal",
                ["audit_type"] = typeId,
                ["auditee"] = auditee,
                ["entity_kind"] = entity,
                ["lead_auditor"] = leadId,
                ["start_at"] = start.ToDateTime(new TimeOnly(13, 0), DateTimeKind.Utc),
                ["end_at"] = end.ToDateTime(new TimeOnly(21, 0), DateTimeKind.Utc),
                ["scope"] = plan.Text("scope"),
                ["audit_plan"] = plan.Record.Id,
                ["report_by"] = leadId,
            },
            CreateComment = $"Scheduled from audit plan {plan.Record.Number} (AUD-012)",
        });
        if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());

        var proposed = new RecordData();
        var rows = proposed.RowsOf("planned_audits");
        foreach (var r in plan.Rows("planned_audits"))
        {
            var clone = r.Clone();
            if (r.Id == rowId) clone.CreatedRecordId = res.Record!.Id;
            rows.Add(clone);
        }
        var save = await ctx.Records.SaveAsync(plan, proposed, new SaveOptions { System = true });
        if (!save.Ok) return ActionOutcome.Fail(save.Errors.Select(e => e.Message).ToArray());
        await ctx.Records.LinkAsync(plan.Record.Id, res.Record!.Id, LinkKind.Source);
        await ctx.Db.SaveChangesAsync();
        return ActionOutcome.Ok($"Scheduled {res.Record.Number} for {auditee}, assigned to {ctx.Directory.Name(leadId)}.");
    });

    /// <summary>INV-165: void the scheduled audit and its recommendations, then remove the row, all in one transaction.</summary>
    public static Task<ActionOutcome> RemoveAsync(RecordContext ctx, Guid rowId, string? reason) => ModuleKit.InTransactionAsync(ctx, async () =>
    {
        if (string.IsNullOrWhiteSpace(reason)) return ActionOutcome.Fail("Give a reason for removing the planned audit.");
        var plan = await ctx.Records.LoadRequiredAsync(ctx.Record.Id);
        if (plan.PhaseKey is not ("draft" or "approval")) return ActionOutcome.Fail("Planned audits can only be removed while the plan is in Draft or Approval (AUD-013).");
        var row = plan.Rows("planned_audits").FirstOrDefault(r => r.Id == rowId);
        if (row == null) return ActionOutcome.Fail("That planned audit no longer exists.");
        var voided = new List<string>();
        if (row.CreatedRecordId is { } auditId && await ctx.Db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == auditId) is { } audit)
        {
            if (audit.PhaseType == PhaseType.Completed)
                return ActionOutcome.Fail($"{audit.Number} is already completed and can't be voided. Keep the planned audit.");
            var why = $"Planned audit removed from {plan.Record.Number}: {reason}";
            foreach (var rec in await ctx.Records.ChildrenAsync(auditId, "recommendation"))
                if (rec.PhaseType == PhaseType.Normal)
                {
                    await Void(ctx, rec.Id, why);
                    voided.Add(rec.Number);
                }
            if (audit.PhaseType != PhaseType.Voided)
            {
                await Void(ctx, auditId, why);
                voided.Insert(0, audit.Number);
            }
        }
        var proposed = new RecordData();
        var rows = proposed.RowsOf("planned_audits");
        rows.AddRange(plan.Rows("planned_audits").Where(r => r.Id != rowId).Select(r => r.Clone()));
        var fresh = await ctx.Records.LoadRequiredAsync(plan.Record.Id);
        var save = await ctx.Records.SaveAsync(fresh, proposed, new SaveOptions { System = true, Reason = reason });
        if (!save.Ok) return ActionOutcome.Fail(save.Errors.Select(e => e.Message).ToArray());
        return ActionOutcome.Ok(voided.Count > 0 ? $"Planned audit removed; voided {string.Join(", ", voided)}." : "Planned audit removed.");
    });

    private static async Task Void(RecordContext ctx, Guid id, string reason)
    {
        var res = await ctx.Workflow.SystemRouteAsync(id, "voided", reason, comment: reason);
        if (!res.Ok) throw new WorkflowException(string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var model = await PlanProgressModel.BuildAsync(ctx, ctx.Snapshot);
        if (model.Rows.Count == 0) return [];
        return [new RecordPanel { Key = "aud-plan", Title = "Program progress", Icon = "bi-bar-chart-steps", Partial = "~/Modules/Audits/Views/_PlanProgress.cshtml", Model = model, Placement = "side" }];
    }
}

/// <summary>
/// Audits: checklist copy-on-create from the audit type, scores (INV-067), no removal of questions once scheduled (AUD-023),
/// the summary (AUD-028), required questions before leaving Scheduled (AUD-022), and raising CAPAs and recommendations (AUD-025).
/// </summary>
public sealed class AuditBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["audit"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        var records = ctx.Services.GetRequiredService<RecordService>();
        var rows = ctx.Proposed.RowsOf("checklist");

        // Copy-on-create: the audit type's evaluation and default checklists are copied, not referenced.
        var typeId = main.Link("audit_type");
        var inDraft = ctx.IsNew || ctx.Current?.PhaseKey is "draft" or "scheduled";
        RecordSnapshot? type = typeId is { } tid ? await records.LoadAsync(tid) : null;
        if (type != null && type.Form.Key != "audit_type") type = null;
        if (type != null && inDraft && (ctx.IsNew || typeId != ctx.Current?.Data.Main.Link("audit_type")))
        {
            if (ctx.IsNew && type.Bool("enabled") == false) ctx.Error("audit_type", $"The audit type \"{type.Text("name")}\" is disabled.");
            if (!main.Has("evaluation") && type.KeywordKey("evaluation") is { } ev) ctx.Set("evaluation", ev);
            if (rows.Count == 0)
                foreach (var cid in type.Links("checklists"))
                    if (await records.LoadAsync(cid) is { } tpl && tpl.Form.Key == "checklist_template" && tpl.Bool("enabled") != false)
                        AppendChecklist(ctx.Form, ctx.Config, rows, tpl);
        }

        // AUD-023: once scheduled, questions can't be removed (neutralize them instead).
        if (ctx.Current is { } cur && cur.PhaseKey != "draft")
        {
            var kept = rows.Select(r => r.Id).ToHashSet();
            var gone = cur.Rows("checklist").Where(r => !kept.Contains(r.Id)).ToList();
            if (gone.Count > 0)
                ctx.Error("checklist", $"Checklist questions can't be removed once the audit is scheduled ({string.Join("; ", gone.Select(g => g.Values.Text("question")).Take(3))}). Neutralize them instead: clear Req. and Out of (AUD-023).");
        }

        // INV-067: 0 ≤ score ≤ out of.
        foreach (var row in rows)
            if (AuditRules.ScoreError(row.Values.Number("out_of"), row.Values.Number("score")) is { } err)
                ctx.Error("score", $"Question {row.Order + 1}: {err}", row.IsNew ? null : row.Id);

        // AUD-028: the summary is recalculated on every save.
        var scoring = ctx.KeywordKey("evaluation") != "normal";
        var summary = AuditRules.Summarize(Questions(ctx.Config, rows), scoring,
            type?.Number("acceptable_pct") ?? 85, type?.Number("marginal_pct") ?? 70);
        ctx.Set("total_score", scoring ? summary.Total : null);
        ctx.Set("max_score", scoring ? summary.Max : null);
        ctx.Set("score_pct", summary.Percent);
        ctx.Set("answered", rows.Count == 0 ? null : $"{summary.Answered} of {summary.Questions}");
        ctx.Set("count_major", summary.Count("major_nc"));
        ctx.Set("count_minor", summary.Count("minor_nc"));
        ctx.Set("count_observation", summary.Count("observation"));
        ctx.Set("count_ofi", summary.Count("ofi"));
        ctx.Set("count_conforming", summary.Count("conforming"));
        ctx.Set("has_nc", summary.Nonconformances > 0);
        ctx.Set("suggested_result", summary.Answered > 0 ? summary.SuggestedResult : null);

        // AUD-021: the Scheduled due date is the audit's end, unless that's empty or past (then the phase settings apply).
        var end = main.DateTime("end_at");
        if (end is { } e && main.DateTime("start_at") is { } s && e < s) ctx.Error("end_at", "The audit can't end before it starts.");
        ctx.Set("scheduled_due", end is { } ed && DateOnly.FromDateTime(ed) >= BusinessCalendar.Today ? DateOnly.FromDateTime(ed) : null);
    }

    public static List<QuestionScore> Questions(ConfigModel cfg, IEnumerable<RowBag> rows) =>
        rows.Select(r => new QuestionScore(cfg.Keyword(r.Values.Ref("classification"))?.Key, r.Values.Number("out_of"), r.Values.Number("score"), r.Values.Bool("required") == true)).ToList();

    public static void AppendChecklist(FormDefinition form, ConfigModel cfg, List<RowBag> rows, RecordSnapshot tpl)
    {
        foreach (var q in tpl.Rows("questions"))
            rows.Add(ModuleKit.NewRow(form, cfg, rows.Count, new()
            {
                ["question"] = q.Values.Text("question"),
                ["clause"] = q.Values.Text("clause"),
                ["out_of"] = q.Values.Number("out_of"),
                ["required"] = q.Values.Bool("required") == true,
            }));
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.From.Key == "scheduled" && ctx.Kind == RouteKind.Forward)
        {
            // AUD-022: required questions answered before the audit can leave Scheduled.
            var scoring = ctx.Snapshot.KeywordKey("evaluation") != "normal";
            var rows = ctx.Snapshot.Rows("checklist");
            if (rows.Count == 0) ctx.Error("Add the checklist questions before submitting the report.");
            var open = rows.Where(r => r.Values.Bool("required") == true &&
                !AuditRules.IsAnswered(Questions(ctx.Config, [r])[0], scoring)).ToList();
            if (open.Count > 0)
                ctx.Error($"Answer every required question first ({open.Count} open: {string.Join("; ", open.Take(4).Select(r => $"Q{r.Order + 1} {r.Values.Text("clause")}".Trim()))}{(open.Count > 4 ? "…" : "")}). Required questions need a classification{(scoring ? " and a score" : "")} (AUD-022).");
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ actions

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var list = new List<RecordAction>();
        var snap = ctx.Snapshot;
        if (snap.Record.PhaseType == PhaseType.Voided || !await CanActAsync(ctx)) return list;
        var phase = snap.PhaseKey;
        if (phase is "draft" or "scheduled")
        {
            var templates = await ChecklistOptionsAsync(ctx);
            list.Add(new RecordAction
            {
                Key = "add_checklist", Label = "Add checklist questions", Icon = "bi-card-checklist", Group = "Checklist",
                Description = "Copies the questions of an enabled checklist template into this audit.",
                DisabledReason = templates.Count == 0 ? "There are no enabled checklist templates." : null,
                Inputs = [new ActionField("checklist", "Checklist template", "select", true, Options: templates)],
            });
        }
        if (phase is "scheduled" or "report_approval" or "completed")
        {
            var follow = await FindingsModel.FollowUpsAsync(ctx, snap.Record.Id);
            var ncRows = snap.Rows("checklist").Where(r => snap.KeywordKey(r, "classification") is "minor_nc" or "major_nc" && !follow.Capas.ContainsKey(r.Id)).ToList();
            var recRows = snap.Rows("checklist").Where(r => AuditRules.FindingClasses.Contains(snap.KeywordKey(r, "classification")) && !follow.Recommendations.ContainsKey(r.Id)).ToList();
            list.Add(new RecordAction
            {
                Key = "raise_capa", Label = "Raise CAPA", Icon = "bi-shield-check", Group = "Findings",
                Description = "Creates a CAPA pre-filled from a nonconformance and links it to the finding (AUD-025).",
                DisabledReason = ncRows.Count == 0 ? "No minor or major nonconformances without a CAPA." : null,
                Inputs =
                [
                    new ActionField("row", "Nonconformance", "select", true, Options: ncRows.Select(r => (r.Id.ToString(), RowLabel(snap, r))).ToList(), Default: ncRows.FirstOrDefault()?.Id.ToString()),
                    new ActionField("owner", "CAPA owner", "user", true, "Usually the auditee's process owner."),
                    new ActionField("priority", "Priority", "select", true, Options: [("low", "Low"), ("medium", "Medium"), ("high", "High"), ("critical", "Critical")],
                        Default: ncRows.Any(r => snap.KeywordKey(r, "classification") == "major_nc") ? "high" : "medium"),
                ],
            });
            list.Add(new RecordAction
            {
                Key = "raise_rec", Label = "Raise recommendation", Icon = "bi-lightbulb", Group = "Findings",
                Description = "Creates a recommendation from an observation or opportunity for improvement, assigned and linked to the finding (AUD-025).",
                DisabledReason = recRows.Count == 0 ? "No findings without a recommendation." : null,
                Inputs =
                [
                    new ActionField("row", "Finding", "select", true, Options: recRows.Select(r => (r.Id.ToString(), RowLabel(snap, r))).ToList(), Default: recRows.FirstOrDefault()?.Id.ToString()),
                    new ActionField("assignee", "Assign to", "user", true),
                    new ActionField("due", "Due date", "date", true, Default: BusinessCalendar.Today.AddDays(30).ToString("yyyy-MM-dd")),
                ],
            });
        }
        return list;
    }

    private static async Task<bool> CanActAsync(RecordContext ctx) =>
        await ModuleKit.CanActAsync(ctx) || ctx.Snapshot.UserId("lead_auditor") == ctx.Me.Id;

    public static string RowLabel(RecordSnapshot snap, RowBag r)
    {
        var q = r.Values.Text("question") ?? "";
        if (q.Length > 70) q = q[..67] + "…";
        var cls = snap.Config.Keyword(r.Values.Ref("classification"))?.Label;
        return $"Q{r.Order + 1}{(r.Values.Text("clause") is { } c ? " " + c : "")} – {q}{(cls != null ? $" ({cls})" : "")}";
    }

    private static async Task<List<(string, string)>> ChecklistOptionsAsync(RecordContext ctx)
    {
        if (!ctx.Config.FormsByKey.TryGetValue("checklist_template", out var form)) return [];
        var disabled = await ctx.Db.RecordValues.AsNoTracking().Where(v => v.FieldKey == "enabled" && v.RowId == null && v.RefId == 0).Select(v => v.RecordId).ToListAsync();
        var list = await ctx.Access.Readable().Where(r => r.FormId == form.Id && !disabled.Contains(r.Id)).OrderBy(r => r.Sequence)
            .Select(r => new { r.Id, r.Number, r.Title }).ToListAsync();
        return list.Select(r => (r.Id.ToString(), $"{r.Number} {r.Title}")).ToList();
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (ctx.Record.PhaseType == PhaseType.Voided || !await CanActAsync(ctx))
            return ActionOutcome.Fail("Only the audit's assignee, its lead auditor or an application manager can do this.");
        return actionKey switch
        {
            "add_checklist" => await AddChecklistAsync(ctx, input.Get("checklist")),
            "raise_capa" => await RaiseCapaAsync(ctx, input),
            "raise_rec" => await RaiseRecommendationAsync(ctx, input),
            _ => ActionOutcome.Fail("This action isn't available."),
        };
    }

    private static Task<ActionOutcome> AddChecklistAsync(RecordContext ctx, string? checklist) => ModuleKit.InTransactionAsync(ctx, async () =>
    {
        if (ctx.Snapshot.PhaseKey is not ("draft" or "scheduled")) return ActionOutcome.Fail("Checklists are added in Draft or Scheduled.");
        if (!Guid.TryParse(checklist, out var id) || await ctx.Records.LoadAsync(id) is not { } tpl || tpl.Form.Key != "checklist_template")
            return ActionOutcome.Fail("Choose a checklist template.");
        if (tpl.Bool("enabled") == false) return ActionOutcome.Fail("Disabled checklists can't be used (AUD-001).");
        var snap = await ctx.Records.LoadRequiredAsync(ctx.Record.Id);
        var proposed = new RecordData();
        var rows = proposed.RowsOf("checklist");
        rows.AddRange(snap.Rows("checklist").Select(r => r.Clone()));
        var before = rows.Count;
        AppendChecklist(snap.Form, snap.Config, rows, tpl);
        var save = await ctx.Records.SaveAsync(snap, proposed, new SaveOptions { System = true });
        if (!save.Ok) return ActionOutcome.Fail(save.Errors.Select(e => e.Message).ToArray());
        return ActionOutcome.Ok($"Added {rows.Count - before} questions from {tpl.Record.Number}.");
    });

    private static Task<ActionOutcome> RaiseCapaAsync(RecordContext ctx, ActionInput input) => ModuleKit.InTransactionAsync(ctx, async () =>
    {
        var snap = ctx.Snapshot;
        if (!ctx.Config.FormsByKey.ContainsKey("capa")) return ActionOutcome.Fail("The CAPA application isn't installed.");
        if (!Guid.TryParse(input.Get("row"), out var rowId) || snap.Rows("checklist").FirstOrDefault(r => r.Id == rowId) is not { } row)
            return ActionOutcome.Fail("Choose a nonconformance.");
        var cls = snap.KeywordKey(row, "classification");
        if (cls is not ("minor_nc" or "major_nc")) return ActionOutcome.Fail("CAPAs are raised for minor or major nonconformances.");
        var follow = await FindingsModel.FollowUpsAsync(ctx, snap.Record.Id);
        if (follow.Capas.ContainsKey(rowId)) return ActionOutcome.Fail("That finding already has a CAPA.");
        if (!int.TryParse(input.Get("owner"), out var owner) || ctx.Directory.Get(owner) is not { Assignable: true }) return ActionOutcome.Fail("Choose the CAPA owner.");
        var priority = input.Get("priority") is "low" or "medium" or "high" or "critical" ? input.Get("priority") : "medium";

        var clause = row.Values.Text("clause");
        var question = row.Values.Text("question") ?? "";
        var subject = $"Audit finding{(clause != null ? " " + clause : "")}: {question}";
        if (subject.Length > 200) subject = subject[..197] + "…";
        var external = snap.KeywordKey("audit_kind") is not ("internal" or null);
        var res = await ctx.Records.CreateAsync(new CreateRequest
        {
            FormKey = "capa",
            SkipAccessCheck = true,
            Values = new()
            {
                ["subject"] = subject,
                ["capa_type"] = snap.KeywordKey("audit_kind") == "supplier" && ctx.Config.Keyword("capa_type", "scar") != null ? "scar" : "corrective",
                ["source"] = external ? "external_audit" : "internal_audit",
                ["priority"] = priority,
                ["owner"] = owner,
                ["discovered_on"] = snap.DateTime("start_at") is { } st ? DateOnly.FromDateTime(st) : BusinessCalendar.Today,
                ["product"] = snap.Text("auditee"),
                ["source_record"] = snap.Record.Id,
                ["description"] = $"{snap.Config.Keyword(row.Values.Ref("classification"))?.Label} raised in audit {snap.Record.Number} ({snap.Text("auditee")})" +
                                  $"{(clause != null ? " against " + clause : "")}.\n\nQuestion: {question}\nFinding: {row.Values.Text("comments") ?? "(see audit)"}",
            },
            CreateComment = $"Raised from a finding of audit {snap.Record.Number} (AUD-025)",
        });
        if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
        await ctx.Records.LinkAsync(snap.Record.Id, res.Record!.Id, LinkKind.Source, FindingsModel.LinkKey(rowId));
        await ctx.Db.SaveChangesAsync();
        await ctx.Records.AddCommentAsync(snap.Record.Id, $"Raised {res.Record.Number} for finding Q{row.Order + 1}{(clause != null ? " (" + clause + ")" : "")}.", "system", system: true);
        return ActionOutcome.Ok($"CAPA {res.Record.Number} raised and linked to the finding.");
    });

    private static Task<ActionOutcome> RaiseRecommendationAsync(RecordContext ctx, ActionInput input) => ModuleKit.InTransactionAsync(ctx, async () =>
    {
        var snap = ctx.Snapshot;
        if (!Guid.TryParse(input.Get("row"), out var rowId) || snap.Rows("checklist").FirstOrDefault(r => r.Id == rowId) is not { } row)
            return ActionOutcome.Fail("Choose a finding.");
        if (!AuditRules.FindingClasses.Contains(snap.KeywordKey(row, "classification"))) return ActionOutcome.Fail("Recommendations are raised for findings, not conforming questions.");
        var follow = await FindingsModel.FollowUpsAsync(ctx, snap.Record.Id);
        if (follow.Recommendations.ContainsKey(rowId)) return ActionOutcome.Fail("That finding already has a recommendation.");
        if (!int.TryParse(input.Get("assignee"), out var assignee) || ctx.Directory.Get(assignee) is not { Assignable: true }) return ActionOutcome.Fail("Choose who the recommendation is assigned to.");
        if (!DateOnly.TryParse(input.Get("due"), System.Globalization.CultureInfo.InvariantCulture, out var due) || due < BusinessCalendar.Today)
            return ActionOutcome.Fail("Choose a due date that isn't in the past.");
        var question = row.Values.Text("question") ?? "";
        var subject = $"Recommendation: {question}";
        if (subject.Length > 200) subject = subject[..197] + "…";
        var res = await ctx.Records.CreateAsync(new CreateRequest
        {
            FormKey = "recommendation",
            StartPhaseKey = "open",
            ParentId = snap.Record.Id,
            SkipAccessCheck = true,
            Assignees = [assignee],
            DueDate = due,
            Values = new()
            {
                ["subject"] = subject, ["clause"] = row.Values.Text("clause"), ["assignee"] = assignee, ["due_date"] = due,
                ["finding"] = $"{snap.Config.Keyword(row.Values.Ref("classification"))?.Label}: {row.Values.Text("comments") ?? question}",
                ["finding_row"] = rowId.ToString(),
            },
            CreateComment = $"Raised from a finding of audit {snap.Record.Number} (AUD-025)",
        });
        if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
        return ActionOutcome.Ok($"Recommendation {res.Record!.Number} raised and assigned to {ctx.Directory.Name(assignee)}.");
    });

    // ------------------------------------------------------------------ panels

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var snap = ctx.Snapshot;
        var panels = new List<RecordPanel>();
        var model = await FindingsModel.BuildAsync(ctx, snap);
        if (snap.Rows("checklist").Count > 0 && snap.PhaseKey != "draft")
            panels.Add(new RecordPanel { Key = "aud-summary", Title = "Audit summary", Partial = "~/Modules/Audits/Views/_AuditSummary.cshtml", Model = model, Placement = "top" });
        if (model.Findings.Count > 0)
            panels.Add(new RecordPanel { Key = "aud-findings", Title = "Findings", Icon = "bi-flag", Partial = "~/Modules/Audits/Views/_Findings.cshtml", Model = model, Placement = "tab", Count = model.Findings.Count });
        return panels;
    }
}

/// <summary>Recommendations: segregation of duties between the assignee and the verifier.</summary>
public sealed class RecommendationBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["recommendation"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        if (m.Bool("verification_required") == true && m.Ref("verifier") is { } v && v == m.Ref("assignee"))
            ctx.Error("verifier", "The verifier must be someone other than the person acting on the recommendation (SAF-044).");
        if (ctx.IsNew && m.Date("due_date") is { } d && d < BusinessCalendar.Today)
            ctx.Error("due_date", "The due date can't be in the past.");
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------- panel models

public sealed record FindingFollowUp(Record Record, PhaseDefinition? Phase, string Kind);

public sealed record FindingRow(RowBag Row, int Number, Keyword? Classification, List<FindingFollowUp> FollowUps);

public sealed class FindingsModel
{
    public required RecordSnapshot Snap { get; init; }
    public required AuditSummary Summary { get; init; }
    public required List<FindingRow> Findings { get; init; }
    public bool Scoring { get; init; }

    public static string LinkKey(Guid rowId) => "finding:" + rowId.ToString("N");

    public sealed record FollowUps(Dictionary<Guid, Record> Capas, Dictionary<Guid, Record> Recommendations);

    /// <summary>CAPAs (source links keyed by finding row) and recommendations (activities with the finding row) of an audit.</summary>
    public static async Task<FollowUps> FollowUpsAsync(RecordContext ctx, Guid auditId)
    {
        var links = await ctx.Db.Links.AsNoTracking().Where(l => l.FromId == auditId && l.Kind == LinkKind.Source && l.FieldKey != null && l.FieldKey.StartsWith("finding:")).ToListAsync();
        var ids = links.Select(l => l.ToId).ToList();
        var capas = await ctx.Db.Records.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var byRow = new Dictionary<Guid, Record>();
        foreach (var l in links)
            if (Guid.TryParseExact(l.FieldKey!["finding:".Length..], "N", out var rid) && capas.TryGetValue(l.ToId, out var rec)) byRow[rid] = rec;
        var recs = new Dictionary<Guid, Record>();
        foreach (var c in await ModuleKit.ChildrenAsync(ctx, auditId, "recommendation", "finding_row"))
            if (Guid.TryParse(c.Text("finding_row"), out var rid) && c.Record.PhaseType != PhaseType.Voided) recs[rid] = c.Record;
        return new FollowUps(byRow, recs);
    }

    public static async Task<FindingsModel> BuildAsync(RecordContext ctx, RecordSnapshot snap)
    {
        var scoring = snap.KeywordKey("evaluation") != "normal";
        var rows = snap.Rows("checklist");
        var follow = await FollowUpsAsync(ctx, snap.Record.Id);
        var upIds = follow.Capas.Values.Concat(follow.Recommendations.Values).Select(c => c.Id).ToList();
        var readable = await ctx.Access.Readable().Where(r => upIds.Contains(r.Id)).Select(r => r.Id).ToListAsync();
        var findings = rows.Where(r => AuditRules.FindingClasses.Contains(snap.KeywordKey(r, "classification")))
            .Select(r =>
            {
                var ups = new List<FindingFollowUp>();
                if (follow.Capas.TryGetValue(r.Id, out var capa) && readable.Contains(capa.Id)) ups.Add(new(capa, ctx.Config.Phase(capa.PhaseId), "CAPA"));
                if (follow.Recommendations.TryGetValue(r.Id, out var rec) && readable.Contains(rec.Id)) ups.Add(new(rec, ctx.Config.Phase(rec.PhaseId), "Recommendation"));
                return new FindingRow(r, r.Order + 1, snap.Config.Keyword(r.Values.Ref("classification")), ups);
            })
            .OrderBy(f => Array.IndexOf(AuditRules.SeverityOrder, f.Classification?.Key ?? "")).ThenBy(f => f.Number).ToList();
        RecordSnapshot? type = snap.Links("audit_type").FirstOrDefault() is { } tid && tid != Guid.Empty ? await ctx.Records.LoadAsync(tid) : null;
        return new FindingsModel
        {
            Snap = snap, Scoring = scoring, Findings = findings,
            Summary = AuditRules.Summarize(AuditBehavior.Questions(snap.Config, rows), scoring, type?.Number("acceptable_pct") ?? 85, type?.Number("marginal_pct") ?? 70),
        };
    }
}

public sealed record PlannedAuditStatus(string Auditee, DateOnly? PlannedStart, Record? Audit, PhaseDefinition? Phase, Keyword? Result, string State, string Tone);

public sealed class PlanProgressModel
{
    public required List<PlannedAuditStatus> Rows { get; init; }
    public int Scheduled => Rows.Count(r => r.Audit != null && r.Audit.PhaseType != PhaseType.Voided);
    public int Completed => Rows.Count(r => r.Audit?.PhaseType == PhaseType.Completed);
    public int Late => Rows.Count(r => r.Tone == "bad");

    public static async Task<PlanProgressModel> BuildAsync(RecordContext ctx, RecordSnapshot snap)
    {
        var rows = snap.Rows("planned_audits");
        var ids = rows.Select(r => r.CreatedRecordId).OfType<Guid>().ToList();
        var audits = await ctx.Db.Records.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var results = await ctx.Db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && v.FieldKey == "result").ToListAsync();
        var today = BusinessCalendar.Today;
        var list = rows.OrderBy(r => r.Values.Date("planned_start")).Select(r =>
        {
            var start = r.Values.Date("planned_start");
            var audit = r.CreatedRecordId is { } id && audits.TryGetValue(id, out var a) ? a : null;
            var result = audit == null ? null : ctx.Config.Keyword(results.FirstOrDefault(v => v.RecordId == audit.Id)?.RefId);
            var (state, tone) = audit switch
            {
                null when start < today => ("Not scheduled · overdue", "bad"),
                null when start <= today.AddDays(30) => ("Schedule soon", "warn"),
                null => ("Planned", "neutral"),
                { PhaseType: PhaseType.Completed } => ("Completed", "ok"),
                { PhaseType: PhaseType.Voided } => ("Voided", "neutral"),
                _ => (ctx.Config.Phase(audit.PhaseId)?.Name ?? "Open", audit.DueDate < today ? "bad" : "info"),
            };
            return new PlannedAuditStatus(r.Values.Text("auditee") ?? "", start, audit, audit == null ? null : ctx.Config.Phase(audit.PhaseId), result, state, tone);
        }).ToList();
        return new PlanProgressModel { Rows = list };
    }
}
