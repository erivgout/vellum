using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;

namespace Vellum.Modules.Supplier;

/// <summary>
/// Supplier scorecards (SUP §15.5, simplified): weights total 100% (INV-094), not-applicable criteria share their weight
/// proportionally (INV-095), exact decimal scores and grade. Publishing updates the supplier's current rating.
/// </summary>
public sealed class ScorecardBehavior : RecordBehavior
{
    private static readonly string[] WeightKeys = ["w_quality", "w_delivery", "w_response", "w_audit"];

    public override IReadOnlyCollection<string> Forms => ["scorecard"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        var reader = ctx.Services.GetRequiredService<RecordReader>();
        var supplier = main.Link("supplier") is { } sid ? await reader.LoadAsync(sid, rows: false) : null;
        if (supplier != null && supplier.Form.Key != "supplier") { ctx.Error("supplier", "Choose a supplier profile."); supplier = null; }
        ctx.Set("supplier_name", supplier?.Text("name"));

        var start = main.Date("period_start");
        var end = main.Date("period_end");
        if (start != null && end != null && end < start) ctx.Error("period_end", "The period can't end before it starts.");
        ctx.Set("period", start != null && end != null && end >= start ? BehaviorHelpers.PeriodLabel(start.Value, end.Value) : null);

        // Weights: blank means "use the supplier's weights".
        var set = WeightKeys.Count(main.Has);
        if (set == 0)
        {
            ctx.Set("w_quality", supplier?.Number("w_quality") ?? 40m);
            ctx.Set("w_delivery", supplier?.Number("w_delivery") ?? 30m);
            ctx.Set("w_response", supplier?.Number("w_response") ?? 15m);
            ctx.Set("w_audit", supplier?.Number("w_audit") ?? 15m);
        }
        else if (set < WeightKeys.Length)
            ctx.Error("w_quality", "Enter all four criteria weights, or leave them all blank to use the supplier's weights.");

        var result = Scoring.Calculate(main.Number("ppm"), main.Number("otd_pct"), main.Number("responsiveness"), main.Number("audit_score"),
            main.Number("w_quality") ?? 0, main.Number("w_delivery") ?? 0, main.Number("w_response") ?? 0, main.Number("w_audit") ?? 0);
        foreach (var e in result.Errors) ctx.Error("w_quality", e); // INV-094
        ctx.Set("s_quality", result.Criteria[0].Score);
        ctx.Set("s_delivery", result.Criteria[1].Score);
        ctx.Set("s_response", result.Criteria[2].Score);
        ctx.Set("s_audit", result.Criteria[3].Score);
        ctx.Set("overall_score", result.Overall);
        ctx.Set("grade", result.Grade);

        // One scorecard per supplier and period start.
        if (supplier != null && start != null)
        {
            var db = ctx.Services.GetRequiredService<Data.VellumDbContext>();
            var selfId = ctx.Current?.Record.Id;
            var fid = ctx.Form.Id;
            var startDate = start.Value.ToDateTime(TimeOnly.MinValue);
            var supId = supplier.Id;
            var dup = await db.Records.AsNoTracking().AnyAsync(r => r.FormId == fid && r.Id != selfId && r.PhaseType != PhaseType.Voided &&
                db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "supplier" && v.RefGuid == supId) &&
                db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "period_start" && v.Date == startDate));
            if (dup) ctx.Error("period_start", $"{supplier.Text("name")} already has a scorecard for the period starting {start:dd MMM yyyy}.");
        }
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key == "published" && ctx.Snapshot.Number("overall_score") == null)
            ctx.Error("The overall score couldn't be calculated. Check the measures and that the weights total 100%.");
        return Task.CompletedTask;
    }

    /// <summary>Publishing updates the supplier's current rating when this is its latest period, and flags C/D grades.</summary>
    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "published") return;
        var s = ctx.Snapshot;
        if (s.Data.Main.Link("supplier") is not { } supplierId) return;
        var reader = ctx.Get<RecordReader>();
        var ids = await reader.LinkingTo(ctx.Db.Records.AsNoTracking(), "scorecard", "supplier", supplierId).ToListAsync();
        var latest = (await reader.LoadManyAsync(ids, rows: false))
            .Where(c => c.Record.PhaseType == PhaseType.Completed)
            .OrderByDescending(c => c.Date("period_end")).ThenByDescending(c => c.Record.Sequence).FirstOrDefault();
        if (latest?.Id == s.Record.Id)
        {
            var res = await ctx.Records.UpdateAsync(supplierId, new()
            {
                ["current_score"] = s.Number("overall_score"), ["current_grade"] = s.KeywordKey("grade"),
                ["current_period"] = s.Text("period"), ["last_scorecard"] = s.Record.Id,
            }, $"Scorecard {s.Record.Number} published");
            if (!res.Ok) throw new Engine.Workflow.WorkflowException("Couldn't update the supplier rating: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        }
        if (s.KeywordKey("grade") is "c" or "d")
            ctx.Get<NotificationService>().ToGroup("supplier-quality", NotificationKind.Info,
                $"{s.Text("supplier_name")} scored {s.Number("overall_score"):0.##} ({s.Keyword("grade")?.Label}) for {s.Text("period")}",
                s.KeywordKey("grade") == "d" ? "Grade D: issue a SCAR and consider conditional status." : "Grade C: agree an improvement plan.", s.Record.Id);
    }

    public override Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.PhaseKey != "draft" || ctx.Me.IsSystem) return Task.FromResult(Enumerable.Empty<RecordAction>());
        return Task.FromResult<IEnumerable<RecordAction>>(
        [
            new RecordAction
            {
                Key = "pull_receiving", Label = "Calculate from receiving", Icon = "bi-calculator", Group = "Scorecard",
                Description = "Fill in lots received, lots rejected and defective ppm from the supplier's closed receiving records in the period (RCV-008).",
            },
        ]);
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "pull_receiving") return ActionOutcome.Fail("This action isn't available.");
        var s = ctx.Snapshot;
        if (s.PhaseKey != "draft") return ActionOutcome.Fail("Only draft scorecards can be recalculated.");
        if (!(await ctx.Access.CanEditAsync(s.Record)).Allowed) return ActionOutcome.Fail("You can't edit this scorecard.");
        if (s.Data.Main.Link("supplier") is not { } supplierId || s.Date("period_start") is not { } start || s.Date("period_end") is not { } end)
            return ActionOutcome.Fail("Choose the supplier and the period first.");
        var stats = await ReceivingStatsAsync(ctx.Get<RecordReader>(), ctx.Db, supplierId, start, end);
        if (stats.Lots == 0) return ActionOutcome.Fail($"No closed receiving records for {s.Text("supplier_name")} between {start:dd MMM yyyy} and {end:dd MMM yyyy}.");
        return await SignedChange.ApplyAsync(ctx, new()
        {
            ["lots_received"] = (decimal)stats.Lots, ["lots_rejected"] = (decimal)stats.Rejected, ["ppm"] = stats.Ppm,
        }, null, "Calculate from receiving", input,
            $"Calculated from {stats.Lots} receiving record(s): {stats.Rejected} rejected, {stats.Ppm:0} ppm",
            $"{stats.Lots} lots, {stats.Rejected} rejected: {stats.Ppm:0} ppm.");
    }

    public sealed record ReceivingStats(int Lots, int Rejected, decimal Ppm);

    /// <summary>ppm = rejected quantity ÷ received quantity × 10⁶ over closed lots in the period (materials marked "do not rate" excluded).</summary>
    public static async Task<ReceivingStats> ReceivingStatsAsync(RecordReader reader, Data.VellumDbContext db, Guid supplierId, DateOnly start, DateOnly end)
    {
        var ids = await reader.LinkingTo(db.Records.AsNoTracking().Where(r => r.PhaseType == PhaseType.Completed), "receiving", "supplier", supplierId).ToListAsync();
        var lots = (await reader.LoadManyAsync(ids, rows: false)).Where(l => l.Date("received_on") is { } d && d >= start && d <= end).ToList();
        var materials = (await reader.LoadManyAsync(lots.Select(l => l.Link("material")).OfType<Guid>().Distinct().ToList(), rows: false))
            .ToDictionary(m => m.Id);
        lots = lots.Where(l => l.Link("material") is not { } m || !materials.TryGetValue(m, out var mat) || mat.Bool("do_not_rate") != true).ToList();
        var received = lots.Sum(l => l.Number("quantity") ?? 0m);
        var rejectedLots = lots.Where(l => l.PhaseKey == "rejected").ToList();
        var rejectedQty = rejectedLots.Sum(l => l.Number("qty_rejected") ?? l.Number("quantity") ?? 0m);
        var ppm = received == 0 ? 0m : decimal.Round(rejectedQty * 1_000_000m / received, 0, MidpointRounding.AwayFromZero);
        return new ReceivingStats(lots.Count, rejectedLots.Count, ppm);
    }

    public override Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var m = ctx.Snapshot.Data.Main;
        var result = Scoring.Calculate(m.Number("ppm"), m.Number("otd_pct"), m.Number("responsiveness"), m.Number("audit_score"),
            m.Number("w_quality") ?? 0, m.Number("w_delivery") ?? 0, m.Number("w_response") ?? 0, m.Number("w_audit") ?? 0);
        return Task.FromResult<IEnumerable<RecordPanel>>(
        [
            new RecordPanel
            {
                Key = "scr-breakdown", Title = "Score breakdown", Icon = "bi-bar-chart-steps", Placement = "side", Order = 1,
                Partial = "~/Modules/Supplier/Views/_ScoreBreakdown.cshtml", Model = result,
            },
        ]);
    }
}
