using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Modules.Supplier;

namespace Vellum.Modules.Risk;

/// <summary>Calculated values of one worksheet row.</summary>
public sealed record FmeaRowCalc(int? Rpn, int? RevisedRpn, decimal? Reduction, bool SeverityFlag, bool RpnFlag)
{
    public bool Flagged => SeverityFlag || RpnFlag;
}

/// <summary>FMEA arithmetic and the completion rule, shared by the behavior, panels and tests.</summary>
public static class FmeaRules
{
    public const int DefaultSeverityThreshold = 9;
    public const int DefaultRpnThreshold = 100;

    /// <summary>INV-178: RPN = S × O × D; RPN′ = S′ × O′ × D′; reduction % = (RPN − RPN′) ÷ RPN × 100, rounded to 0.1.</summary>
    public static FmeaRowCalc Calculate(decimal? s, decimal? o, decimal? d, decimal? rs, decimal? ro, decimal? rd, int severityThreshold, int rpnThreshold)
    {
        int? rpn = s is { } a && o is { } b && d is { } c ? (int)(a * b * c) : null;
        int? rev = rs is { } x && ro is { } y && rd is { } z ? (int)(x * y * z) : null;
        decimal? reduction = rpn is > 0 && rev is { } r ? BehaviorHelpers.Round((rpn.Value - r) * 100m / rpn.Value, 1) : null;
        return new FmeaRowCalc(rpn, rev, reduction, s >= severityThreshold, rpn >= rpnThreshold);
    }

    public static FmeaRowCalc Calculate(ValueBag row, int severityThreshold, int rpnThreshold) =>
        Calculate(row.Number("severity"), row.Number("occurrence"), row.Number("detection"),
            row.Number("rev_severity"), row.Number("rev_occurrence"), row.Number("rev_detection"), severityThreshold, rpnThreshold);

    public static (int Severity, int Rpn) Thresholds(ValueBag main) =>
        ((int)(main.Number("severity_threshold") ?? DefaultSeverityThreshold), (int)(main.Number("rpn_threshold") ?? DefaultRpnThreshold));

    public static bool Handled(ValueBag row) =>
        !string.IsNullOrWhiteSpace(row.Text("recommended_action")) || !string.IsNullOrWhiteSpace(row.Text("justification"));

    /// <summary>INV-179: every row over the severity or RPN threshold has a recommended action or a justification.</summary>
    public static List<string> CompletionBlockers(ValueBag main, IReadOnlyList<RowBag> rows)
    {
        var (sevT, rpnT) = Thresholds(main);
        var errors = new List<string>();
        if (rows.Count == 0) errors.Add("Add at least one failure mode to the worksheet.");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i].Values;
            var c = Calculate(row, sevT, rpnT);
            if (!c.Flagged || Handled(row)) continue;
            var why = c.SeverityFlag && c.RpnFlag ? $"severity {row.Number("severity"):0} and RPN {c.Rpn}" : c.SeverityFlag ? $"severity {row.Number("severity"):0} ≥ {sevT}" : $"RPN {c.Rpn} ≥ {rpnT}";
            errors.Add($"Worksheet row {i + 1} ({row.Text("failure_mode")}): {why} needs a recommended action or a justification (INV-179).");
        }
        return errors;
    }
}

public sealed class FmeaSummaryVm
{
    public int Rows { get; init; }
    public int Flagged { get; init; }
    public int OpenFlags { get; init; }
    public int? MaxRpn { get; init; }
    public decimal? AvgReduction { get; init; }
    public int ActionItems { get; init; }
    public int SeverityThreshold { get; init; }
    public int RpnThreshold { get; init; }
    public List<ParetoRow> Pareto { get; init; } = [];
    public string Title { get; init; } = "";
}

/// <summary>
/// FMEA: calculated RPNs (INV-178), the threshold rule at completion (INV-179), "Create action items" from recommended
/// actions (FMEA-007) and the Pareto / summary panels.
/// </summary>
public sealed class FmeaBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["fmea"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        var (sevT, rpnT) = FmeaRules.Thresholds(main);
        var rows = ctx.Proposed.RowsOf("worksheet");
        int flagged = 0, open = 0, max = 0;
        var reductions = new List<decimal>();
        foreach (var row in rows)
        {
            // Always recalculated, on every write path, so stored values can't drift from S, O and D (INV-178).
            var c = FmeaRules.Calculate(row.Values, sevT, rpnT);
            BehaviorHelpers.SetRow(ctx.Config, ctx.Form, row, "rpn", c.Rpn);
            BehaviorHelpers.SetRow(ctx.Config, ctx.Form, row, "rev_rpn", c.RevisedRpn);
            BehaviorHelpers.SetRow(ctx.Config, ctx.Form, row, "reduction", c.Reduction);
            BehaviorHelpers.SetRow(ctx.Config, ctx.Form, row, "attention",
                c.SeverityFlag && c.RpnFlag ? "Severity + RPN" : c.SeverityFlag ? $"Severity ≥ {sevT}" : c.RpnFlag ? $"RPN ≥ {rpnT}" : null);
            if (c.Flagged) { flagged++; if (!FmeaRules.Handled(row.Values)) open++; }
            if (c.Rpn is { } r && r > max) max = r;
            if (c.Reduction is { } red) reductions.Add(red);
            var partial = new[] { "rev_severity", "rev_occurrence", "rev_detection" }.Count(row.Values.Has);
            if (partial is > 0 and < 3)
                ctx.Error("rev_severity", $"Worksheet: rate all of S′, O′ and D′ for “{row.Values.Text("failure_mode")}”, or leave them all blank.", row.IsNew ? null : row.Id);
        }
        ctx.Set("rows_total", (decimal)rows.Count);
        ctx.Set("flagged_rows", (decimal)flagged);
        ctx.Set("open_flags", (decimal)open);
        ctx.Set("max_rpn", rows.Count == 0 ? null : max);
        ctx.Set("avg_reduction", reductions.Count == 0 ? null : BehaviorHelpers.Round(reductions.Average(), 1));
        return Task.CompletedTask;
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key == "completed")
            foreach (var e in FmeaRules.CompletionBlockers(ctx.Snapshot.Data.Main, ctx.Snapshot.Rows("worksheet"))) ctx.Error(e);
        return Task.CompletedTask;
    }

    private static List<RowBag> Eligible(RecordSnapshot s) => s.Rows("worksheet")
        .Where(r => r.CreatedRecordId == null && !string.IsNullOrWhiteSpace(r.Values.Text("recommended_action")) && r.Values.Ref("responsible") != null)
        .ToList();

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        if (s.PhaseKey != "assigned" || ctx.Me.IsSystem) return [];
        if (await ctx.Access.ActingAssignmentAsync(s.Record.Id) == null && ctx.Access.RoleFor(s.Record.AppId) < AppRole.Manager) return [];
        var eligible = Eligible(s).Count;
        return
        [
            new RecordAction
            {
                Key = "create_actions", Label = "Create action items", Icon = "bi-check2-square", Group = "Create",
                Description = eligible == 0 ? null : $"Turn {eligible} recommended action{(eligible == 1 ? "" : "s")} into action items assigned to the responsible people (FMEA-007).",
                DisabledReason = eligible == 0 ? "Every recommended action with a responsible person already has an action item. Add a recommended action and a responsible person to a row first." : null,
                Inputs =
                [
                    new ActionField("days", "Due in (days)", "number", true, "Calendar days from today.", Default: "30"),
                    new ActionField("verify", "Verification by the FMEA owner?", "yesno", false, "The owner verifies each action's effectiveness before it closes.", Default: "yes"),
                ],
            },
        ];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "create_actions") return ActionOutcome.Fail("This action isn't available.");
        var s = ctx.Snapshot;
        if (s.PhaseKey != "assigned") return ActionOutcome.Fail("Action items are created while the FMEA is Assigned.");
        if (await ctx.Access.ActingAssignmentAsync(s.Record.Id) == null && ctx.Access.RoleFor(s.Record.AppId) < AppRole.Manager)
            return ActionOutcome.Fail("Only the FMEA owner (or an application manager) can create its action items.");
        if (!int.TryParse(input.Get("days"), out var days) || days is < 1 or > 365) return ActionOutcome.Fail("Enter a number of days between 1 and 365.");
        var verify = input.Get("verify") != "no";
        var eligible = Eligible(s);
        if (eligible.Count == 0) return ActionOutcome.Fail("There are no recommended actions waiting for an action item.");
        var owner = s.UserId("owner");
        var due = BusinessCalendar.Today.AddDays(days);
        try
        {
            return await Tx.RunAsync(ctx.Db, async () =>
            {
                var proposed = new RecordData();
                var rows = proposed.RowsOf("worksheet");
                var created = new List<string>();
                var all = s.Rows("worksheet");
                for (var i = 0; i < all.Count; i++)
                {
                    var row = all[i];
                    var clone = row.Clone();
                    if (eligible.Contains(row))
                    {
                        var responsible = row.Values.Ref("responsible")!.Value;
                        var withVerify = verify && owner != null && owner != responsible;
                        var action = row.Values.Text("recommended_action")!;
                        var res = await ctx.Records.CreateAsync(new CreateRequest
                        {
                            FormKey = "action_item", StartPhaseKey = "open", ParentId = s.Record.Id, SkipAccessCheck = true, Assignees = [responsible], DueDate = due,
                            Values = new()
                            {
                                ["subject"] = action.Length > 200 ? action[..200] : action,
                                ["description"] = $"Recommended action from {s.Record.Number}, worksheet row {i + 1}: {row.Values.Text("item_function")} / {row.Values.Text("failure_mode")} " +
                                                  $"(S{row.Values.Number("severity"):0} × O{row.Values.Number("occurrence"):0} × D{row.Values.Number("detection"):0} = RPN {row.Values.Number("rpn"):0}).",
                                ["assignee"] = responsible, ["due_date"] = due,
                                ["verification_required"] = withVerify, ["verifier"] = withVerify ? owner : null,
                                ["verification_instructions"] = withVerify ? "Confirm the action was implemented, then re-rate S′, O′ and D′ on the FMEA worksheet." : null,
                            },
                            CreateComment = $"Created from FMEA {s.Record.Number} (FMEA-007)",
                        });
                        if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                        clone.CreatedRecordId = res.Record!.Id;
                        created.Add(res.Record.Number);
                    }
                    rows.Add(clone);
                }
                var save = await ctx.Records.SaveAsync(await ctx.Records.LoadRequiredAsync(s.Record.Id), proposed, new SaveOptions { System = true });
                if (!save.Ok) return ActionOutcome.Fail(save.Errors.Select(e => e.Message).ToArray());
                return ActionOutcome.Ok($"Created {created.Count} action item{(created.Count == 1 ? "" : "s")}: {string.Join(", ", created)}.");
            }, o => o.Success);
        }
        catch (WorkflowException ex)
        {
            return ActionOutcome.Fail(ex.Message);
        }
    }

    public static FmeaSummaryVm Summary(RecordSnapshot s)
    {
        var (sevT, rpnT) = FmeaRules.Thresholds(s.Data.Main);
        var rows = s.Rows("worksheet");
        var pareto = new List<ParetoRow>();
        var calcs = new List<FmeaRowCalc>();
        for (var i = 0; i < rows.Count; i++)
        {
            var v = rows[i].Values;
            var c = FmeaRules.Calculate(v, sevT, rpnT);
            calcs.Add(c);
            if (c.Rpn is { } rpn)
                pareto.Add(new ParetoRow(i + 1, $"{v.Text("item_function")}: {v.Text("failure_mode")}", (int)v.Number("severity")!, (int)v.Number("occurrence")!,
                    (int)v.Number("detection")!, rpn, c.RevisedRpn, c.Flagged, FmeaRules.Handled(v)));
        }
        var reductions = calcs.Where(c => c.Reduction != null).Select(c => c.Reduction!.Value).ToList();
        return new FmeaSummaryVm
        {
            Rows = rows.Count, Flagged = calcs.Count(c => c.Flagged),
            OpenFlags = rows.Where((r, i) => calcs[i].Flagged && !FmeaRules.Handled(r.Values)).Count(),
            MaxRpn = calcs.Max(c => c.Rpn), AvgReduction = reductions.Count == 0 ? null : BehaviorHelpers.Round(reductions.Average(), 1),
            ActionItems = rows.Count(r => r.CreatedRecordId != null), SeverityThreshold = sevT, RpnThreshold = rpnT, Pareto = pareto, Title = s.Record.Title,
        };
    }

    public override Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var vm = Summary(ctx.Snapshot);
        var list = new List<RecordPanel>
        {
            new()
            {
                Key = "fmea-summary", Title = "FMEA summary", Icon = "bi-clipboard-data", Placement = "side", Order = 1,
                Partial = "~/Modules/Risk/Views/_FmeaSummary.cshtml", Model = vm,
            },
        };
        if (vm.Pareto.Count > 0)
            list.Add(new RecordPanel
            {
                Key = "fmea-pareto", Title = "Pareto", Icon = "bi-bar-chart-line", Placement = "tab", Order = 1, Count = vm.Pareto.Count,
                Partial = "~/Modules/Risk/Views/_FmeaPareto.cshtml", Model = vm,
            });
        return Task.FromResult<IEnumerable<RecordPanel>>(list);
    }
}
