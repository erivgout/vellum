using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Modules.Change;

namespace Vellum.Modules.Inspections;

/// <summary>Reads profile and point data into the evaluator's types.</summary>
public static class InspectionData
{
    public static readonly string[] LimitKeys = ["lsl", "lcl", "target", "ucl", "usl", "tolerance"];

    public static NumericLimits? Limits(ValueBag v, ConfigModel cfg) =>
        InspectionEvaluator.ParseType(cfg.Keyword(v.Ref("numeric_type"))?.Key) is { } t
            ? new NumericLimits(t, v.Number("lsl"), v.Number("lcl"), v.Number("target"), v.Number("ucl"), v.Number("usl"), v.Number("tolerance"))
            : null;

    public static List<(string Keyword, InspectionResult Result, bool Disabled)> Keywords(RecordSnapshot profile) =>
        profile.Rows("keywords").Where(r => r.Values.Text("keyword") != null)
            .Select(r => (r.Values.Text("keyword")!, InspectionEvaluator.ParseResult(profile.KeywordKey(r, "kw_result")), r.Values.Bool("kw_disabled") == true)).ToList();

    /// <summary>"Specification" text shown on a sample row: the composite description, enabled keywords only (INS-012).</summary>
    public static string Spec(RecordSnapshot profile)
    {
        if (profile.KeywordKey("data_type") == "keyword")
            return string.Join(" · ", Keywords(profile).Where(k => !k.Disabled).Select(k => $"{k.Keyword} = {k.Result}"));
        return profile.Text("description") ?? (Limits(profile.Data.Main, profile.Config) is { } l ? InspectionEvaluator.Describe(l, profile.Text("unit")) : "");
    }

    public static string SamplingText(RecordSnapshot point) => point.KeywordKey("sampling") switch
    {
        "full" => "100 %",
        "quantity" => $"{point.Number("sample_qty") ?? 1:0} per {point.Number("per_qty")?.ToString("0") ?? "lot"}",
        "time" => $"{point.Number("sample_qty") ?? 1:0} every {point.Number("per_minutes") ?? 60:0} min",
        "none" => "No sampling",
        _ => "",
    };

    public static int TotalSamples(RecordSnapshot point, decimal lotQty, DateTime? start, DateTime? end) =>
        InspectionEvaluator.TotalSamples(point.KeywordKey("sampling"), point.Number("sample_qty"), point.Number("per_qty"), point.Number("per_minutes"), lotQty, start, end);
}

// ---------------------------------------------------------------------- criteria profiles

/// <summary>INV-068 limit order, applicable limits per numeric type and the composite description (INS-001/002).</summary>
public sealed class CriteriaProfileBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["criteria_profile"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        switch (ctx.KeywordKey("data_type"))
        {
            case "numeric":
                var type = InspectionEvaluator.ParseType(ctx.KeywordKey("numeric_type"));
                if (type == null)
                {
                    ctx.Error("numeric_type", "Choose the numeric type: RANGE, TARGET, MIN or MAX.");
                    break;
                }
                var applicable = InspectionEvaluator.ApplicableLimits(type.Value);
                foreach (var k in InspectionData.LimitKeys.Where(k => !applicable.Contains(k) && main.Has(k))) ctx.Set(k, null);
                var limits = InspectionData.Limits(main, ctx.Config)!;
                foreach (var e in InspectionEvaluator.ValidateLimits(limits)) ctx.Error("limits", e);
                ctx.Set("description", InspectionEvaluator.Describe(limits, main.Text("unit")));
                break;
            case "keyword":
                foreach (var k in InspectionData.LimitKeys.Append("numeric_type").Append("unit").Where(main.Has)) ctx.Set(k, null);
                var rows = ctx.Proposed.RowsOf("keywords");
                var enabled = rows.Where(r => r.Values.Bool("kw_disabled") != true && r.Values.Text("keyword") != null).ToList();
                if (enabled.Count == 0) ctx.Error("keywords", "Add at least one enabled keyword with its result.");
                foreach (var r in rows.Where(r => ctx.KeywordKey(r, "kw_result") == "pending"))
                    ctx.Error("kw_result", $"\"{r.Values.Text("keyword")}\": the result must be Pass, Warn or Fail.", r.IsNew ? null : r.Id);
                foreach (var dup in rows.GroupBy(r => r.Values.Text("keyword")?.Trim().ToLowerInvariant()).Where(g => g.Key != null && g.Count() > 1))
                    ctx.Error("keywords", $"The keyword \"{dup.First().Values.Text("keyword")}\" is listed more than once.");
                ctx.Set("description", "Keyword: " + string.Join(" · ", enabled.Select(r => $"{r.Values.Text("keyword")} → {ctx.Config.Keyword(r.Values.Ref("kw_result"))?.Label}")));
                break;
        }
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------- inspection points

/// <summary>Points: enabled profiles only, CIDs default to ⟨point number⟩-⟨row⟩ and are unique, default failure classes (INS-004).</summary>
public sealed class InspectionPointBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["inspection_point"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        if (ctx.IsNew && !main.Has("failure_classes")) ctx.Set("failure_classes", new List<string> { "critical", "major" });
        if (ctx.KeywordKey("sampling") is "quantity" or "time" && !main.Has("sample_qty")) ctx.Set("sample_qty", 1m);

        var records = ctx.Services.GetRequiredService<RecordService>();
        var before = ctx.Current?.Rows("criteria").ToDictionary(r => r.Id, r => r.Values.Link("profile")) ?? [];
        var rows = ctx.Proposed.RowsOf("criteria");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rid = row.IsNew ? (Guid?)null : row.Id;
            if (ctx.Current != null && row.Values.Text("cid") == null) row.SetRow(ctx.Form, ctx.Config, "cid", $"{ctx.Current.Record.Number}-{i + 1}");
            if (row.Values.Link("profile") is not { } pid) { ctx.Error("profile", $"Criterion {i + 1}: choose a criteria profile.", rid); continue; }
            if (before.TryGetValue(row.Id, out var old) && old == pid) continue;
            var profile = await records.LoadAsync(pid);
            if (profile == null || profile.Form.Key != "criteria_profile") ctx.Error("profile", $"Criterion {i + 1}: pick a criteria profile.", rid);
            else if (profile.Bool("enabled") == false) ctx.Error("profile", $"Criterion {i + 1}: {profile.Record.Number} {profile.Text("name")} is disabled and can't be picked (INS-004).", rid);
        }
        foreach (var dup in rows.Where(r => r.Values.Text("cid") != null).GroupBy(r => r.Values.Text("cid")!.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            ctx.Error("criteria", $"The CID \"{dup.Key}\" is used more than once.");
    }

    /// <summary>The number exists only after creation: fill the default CIDs then.</summary>
    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        var rows = ctx.Snapshot.Rows("criteria");
        if (rows.All(r => r.Values.Text("cid") != null)) return;
        var proposed = new RecordData();
        var list = proposed.RowsOf("criteria");
        for (var i = 0; i < rows.Count; i++)
        {
            var c = rows[i].Clone();
            if (c.Values.Text("cid") == null) c.SetRow(ctx.Snapshot.Form, ctx.Config, "cid", $"{ctx.Record.Number}-{i + 1}");
            list.Add(c);
        }
        var res = await ctx.Records.SaveAsync(await ctx.Records.LoadRequiredAsync(ctx.Record.Id), proposed, new SaveOptions { System = true });
        if (!res.Ok) throw new WorkflowException(string.Join("; ", res.Errors.Select(e => e.Message)));
    }
}

// ---------------------------------------------------------------------- inspection plans

/// <summary>Plans: only Active points can be added, steps default to the row order (INS-005).</summary>
public sealed class InspectionPlanBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["inspection_plan"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var records = ctx.Services.GetRequiredService<RecordService>();
        var before = ctx.Current?.Rows("points").ToDictionary(r => r.Id, r => r.Values.Link("point")) ?? [];
        var rows = ctx.Proposed.RowsOf("points");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rid = row.IsNew ? (Guid?)null : row.Id;
            if (row.Values.Number("step") == null) row.SetRow(ctx.Form, ctx.Config, "step", (decimal)(i + 1));
            if (row.Values.Link("point") is not { } pid) { ctx.Error("point", $"Row {i + 1}: choose an inspection point.", rid); continue; }
            if (before.TryGetValue(row.Id, out var old) && old == pid) continue;
            var point = await records.LoadAsync(pid);
            if (point == null || point.Form.Key != "inspection_point") ctx.Error("point", $"Row {i + 1}: pick an inspection point.", rid);
            else if (point.KeywordKey("status") != "active")
                ctx.Error("point", $"Row {i + 1}: {point.Record.Number} {point.Text("name")} is {point.Keyword("status")?.Label.ToLowerInvariant()} and can't be added (INS-005).", rid);
        }
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.Kind != RouteKind.Forward || ctx.To.Key is not ("approve" or "active")) return;
        foreach (var w in await PointWarningsAsync(ctx, ctx.Snapshot)) ctx.Error(w);
    }

    public static async Task<List<string>> PointWarningsAsync(RecordContext ctx, RecordSnapshot plan)
    {
        var list = new List<string>();
        foreach (var row in plan.Rows("points"))
            if (row.Values.Link("point") is { } pid && await ctx.Records.LoadAsync(pid) is { } p && p.KeywordKey("status") != "active")
                list.Add($"Step {row.Values.Number("step"):0}: {p.Record.Number} {p.Text("name")} is {p.Keyword("status")?.Label.ToLowerInvariant()}.");
        return list;
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var warnings = await PointWarningsAsync(ctx, ctx.Snapshot);
        var form = ctx.Config.FormsByKey.GetValueOrDefault("inspection_record");
        var recordIds = form == null ? [] : await ctx.Db.RecordValues.AsNoTracking()
            .Where(v => v.FieldKey == "plan" && v.RowId == null && v.RefGuid == ctx.Record.Id && ctx.Db.Records.Any(r => r.Id == v.RecordId && r.FormId == form.Id))
            .Select(v => v.RecordId).ToListAsync();
        var recs = await ctx.Access.Readable().AsNoTracking().Where(r => recordIds.Contains(r.Id)).OrderByDescending(r => r.CreatedAt).Take(8).ToListAsync();
        var results = await ctx.Db.RecordValues.AsNoTracking().Where(v => recordIds.Contains(v.RecordId) && v.RowId == null && v.FieldKey == "result").ToListAsync();
        var model = new PlanStatusModel
        {
            Warnings = warnings, Records = recs, Total = recordIds.Count, Config = ctx.Config,
            Results = results.ToDictionary(v => v.RecordId, v => ctx.Config.Keyword(v.RefId)),
            Active = ctx.Snapshot.PhaseKey == "active",
        };
        return [new RecordPanel { Key = "ins-plan", Title = "Inspection records", Icon = "bi-clipboard2-data", Partial = "~/Modules/Inspections/Views/_PlanStatus.cshtml", Model = model, Placement = "side" }];
    }
}

public sealed class PlanStatusModel
{
    public required List<string> Warnings { get; init; }
    public required List<Record> Records { get; init; }
    public required Dictionary<Guid, Keyword?> Results { get; init; }
    public required ConfigModel Config { get; init; }
    public int Total { get; init; }
    public bool Active { get; init; }
}

// ---------------------------------------------------------------------- inspection records

public sealed record SampleLine(Guid Id, string Number, string Serial, InspectionResult Result, bool HasWarn, PhaseType PhaseType, PhaseDefinition? Phase, Guid? PointId);

public sealed record PointLine(Guid PointId, string Number, string Name, int Step, string Sampling, PointProgress Progress, List<SampleLine> Samples);

/// <summary>Sampling progress of an inspection record: totals per point from the sampling rule and the samples taken (INS-010, INS-015).</summary>
public sealed class InspectionProgress
{
    public List<PointLine> Points { get; init; } = [];
    public List<SampleLine> Samples { get; init; } = [];
    public List<SampleLine> AdHoc { get; init; } = [];
    public InspectionResult Result { get; init; }
    public int Total => Points.Sum(p => p.Progress.Total);
    public int Remaining => Points.Sum(p => p.Progress.Remaining);
    public int Failed => Samples.Count(s => s.PhaseType != PhaseType.Voided && s.Result == InspectionResult.Fail);

    public static async Task<InspectionProgress> ComputeAsync(IServiceProvider sp, Guid? recordId, ValueBag data)
    {
        var records = sp.GetRequiredService<RecordService>();
        var db = sp.GetRequiredService<VellumDbContext>();
        var cfg = sp.GetRequiredService<ConfigCache>().Current;
        var samples = new List<SampleLine>();
        if (recordId is { } rid && cfg.FormsByKey.TryGetValue("inspection_sample", out var sampleForm))
        {
            var kids = await db.Records.AsNoTracking().Where(r => r.ParentId == rid && r.FormId == sampleForm.Id).OrderBy(r => r.CreatedAt).ToListAsync();
            var ids = kids.Select(k => k.Id).ToList();
            var values = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) &&
                ((v.RowId == null && (v.FieldKey == "point" || v.FieldKey == "sample_result" || v.FieldKey == "serial")) || (v.RowId != null && v.FieldKey == "result"))).ToListAsync();
            var warn = cfg.Keyword("ins_result", "warn")?.Id;
            foreach (var k in kids)
            {
                var mine = values.Where(v => v.RecordId == k.Id).ToList();
                samples.Add(new SampleLine(k.Id, k.Number, mine.FirstOrDefault(v => v.FieldKey == "serial")?.Text ?? k.Number,
                    InspectionEvaluator.ParseResult(cfg.Keyword(mine.FirstOrDefault(v => v.FieldKey == "sample_result" && v.RowId == null)?.RefId)?.Key),
                    mine.Any(v => v.RowId != null && v.RefId == warn), k.PhaseType, cfg.Phase(k.PhaseId), mine.FirstOrDefault(v => v.FieldKey == "point")?.RefGuid));
            }
        }

        var points = new List<PointLine>();
        if (data.Link("plan") is { } planId && await records.LoadAsync(planId) is { } plan && plan.Form.Key == "inspection_plan")
        {
            var lot = data.Number("lot_qty") ?? 0;
            foreach (var row in plan.Rows("points").OrderBy(r => r.Values.Number("step") ?? r.Order + 1).ThenBy(r => r.Order))
            {
                if (row.Values.Link("point") is not { } pid || await records.LoadAsync(pid) is not { } point) continue;
                var total = InspectionData.TotalSamples(point, lot, data.DateTime("lot_start"), data.DateTime("lot_end"));
                var mine = samples.Where(s => s.PointId == pid).ToList();
                var done = mine.Count(s => s.PhaseType == PhaseType.Completed);
                points.Add(new PointLine(pid, point.Record.Number, point.Text("name") ?? point.Record.Title, (int)(row.Values.Number("step") ?? row.Order + 1),
                    InspectionData.SamplingText(point), new PointProgress(point.Record.Number, total, done), mine));
            }
        }
        var planned = points.Select(p => p.PointId).ToHashSet();
        var result = InspectionEvaluator.RecordResult(points.Select(p => p.Progress).ToList(),
            samples.Where(s => s.PhaseType != PhaseType.Voided).Select(s => s.Result));
        return new InspectionProgress
        {
            Points = points, Samples = samples, Result = result,
            AdHoc = samples.Where(s => s.PointId is not { } p || !planned.Contains(p)).ToList(),
        };
    }
}

/// <summary>
/// Inspection records: only for Active plans (INV-166); sample counts and the result once remaining samples = 0 (INV-168);
/// samples created per point; follow-up action items (INS-018).
/// </summary>
public sealed class InspectionRecordBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["inspection_record"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        var records = ctx.Services.GetRequiredService<RecordService>();
        var planId = main.Link("plan");
        var plan = planId is { } pid ? await records.LoadAsync(pid) : null;
        if (plan == null || plan.Form.Key != "inspection_plan")
        {
            ctx.Error("plan", "Choose an inspection plan.");
            return;
        }
        // INV-166: new inspection records reference only Active plans (pending records of a closed plan can still be finished).
        if ((ctx.IsNew || planId != ctx.Current?.Data.Main.Link("plan")) && plan.PhaseKey != "active")
            ctx.Error("plan", $"{plan.Record.Number} {plan.Text("name")} is {plan.Phase?.Name.ToLowerInvariant()}: inspection records can only be created for Active plans (INV-166).");
        if (!ctx.IsNew && planId != ctx.Current?.Data.Main.Link("plan") && ctx.Current != null &&
            await ctx.Services.GetRequiredService<VellumDbContext>().Records.AnyAsync(r => r.ParentId == ctx.Current.Record.Id))
            ctx.Error("plan", "The plan can't be changed once samples exist.");
        ctx.Set("plan_name", plan.Text("name"));
        if (main.DateTime("lot_start") is { } s && main.DateTime("lot_end") is { } e && e < s) ctx.Error("lot_end", "The lot can't end before it starts.");

        var progress = await InspectionProgress.ComputeAsync(ctx.Services, ctx.Current?.Record.Id, main);
        ctx.Set("total_samples", (decimal)progress.Total);
        ctx.Set("remaining_samples", (decimal)progress.Remaining);
        ctx.Set("failed_samples", (decimal)progress.Failed);
        ctx.Set("result", InspectionEvaluator.Key(progress.Result));
        ctx.Set("result_note", progress.Result switch
        {
            InspectionResult.Pending => progress.Points.Count == 0 ? "The plan has no inspection points." : $"Pending: {progress.Remaining} of {progress.Total} samples remaining.",
            InspectionResult.Fail => $"Fail: {progress.Failed} sample{(progress.Failed == 1 ? "" : "s")} failed.",
            _ => $"Pass: all {progress.Total} samples taken, none failed.",
        });
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Type == PhaseType.Completed && ctx.Snapshot.KeywordKey("result") is not ("pass" or "fail"))
            ctx.Error($"The result isn't defined yet: {ctx.Snapshot.Number("remaining_samples") ?? 0:0} samples remain (INV-168).");
        return Task.CompletedTask;
    }

    /// <summary>Recalculate a record after one of its samples changed (same transaction).</summary>
    public static async Task RecomputeAsync(RecordContext ctx, Guid recordId)
    {
        var snap = await ctx.Records.LoadAsync(recordId);
        if (snap == null || snap.Form.Key != "inspection_record" || snap.Record.PhaseType != PhaseType.Normal) return;
        var res = await ctx.Records.SaveAsync(snap, new RecordData(), new SaveOptions { System = true });
        if (!res.Ok) throw new WorkflowException(string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var list = new List<RecordAction>();
        if (ctx.Record.PhaseType != PhaseType.Normal || !await ModuleKit.CanActAsync(ctx)) return list;
        if (ctx.Snapshot.PhaseKey == "inspect")
        {
            var progress = await InspectionProgress.ComputeAsync(ctx.Services, ctx.Record.Id, ctx.Snapshot.Data.Main);
            var next = progress.Points.FirstOrDefault(p => p.Progress.Remaining > 0);
            list.Add(new RecordAction
            {
                Key = "create_sample", Label = "Create sample", Icon = "bi-plus-circle", Group = "Inspect",
                Description = "Creates the next sample for a point, with the point's criteria loaded (INS-011, INS-012).",
                DisabledReason = progress.Points.Count == 0 ? "The plan has no inspection points." : null,
                Inputs =
                [
                    new ActionField("point", "Inspection point", "select", true, Options: progress.Points
                        .Select(p => (p.PointId.ToString(), $"Step {p.Step} · {p.Name} ({(p.Progress.Remaining > 0 ? $"{p.Progress.Remaining} remaining" : "ad-hoc")})")).ToList(),
                        Default: (next ?? progress.Points.FirstOrDefault())?.PointId.ToString()),
                    new ActionField("serial", "Serial / sample ID", "text", true, Default: $"{ctx.Snapshot.Text("lot_number")}-{progress.Samples.Count + 1:000}"),
                ],
            });
        }
        list.Add(new RecordAction
        {
            Key = "follow_up", Label = "Create follow-up action", Icon = "bi-check2-square", Group = "Inspect",
            Description = "Creates an Action Item linked to this inspection record (INS-018).",
            Inputs =
            [
                new ActionField("subject", "Action", "text", true, Default: $"Follow up on lot {ctx.Snapshot.Text("lot_number")}"),
                new ActionField("assignee", "Assign to", "user", true),
                new ActionField("due", "Due date", "date", true, Default: BusinessCalendar.Today.AddDays(14).ToString("yyyy-MM-dd")),
            ],
        });
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (ctx.Record.PhaseType != PhaseType.Normal || !await ModuleKit.CanActAsync(ctx))
            return ActionOutcome.Fail("Only the current assignee or an application manager can do this.");
        return actionKey switch
        {
            "create_sample" => await CreateSampleAsync(ctx, input.Get("point"), input.Get("serial")),
            "follow_up" => await InspectionFollowUp.ActionItemAsync(ctx, input),
            _ => ActionOutcome.Fail("This action isn't available."),
        };
    }

    private static Task<ActionOutcome> CreateSampleAsync(RecordContext ctx, string? pointRaw, string? serial) => ModuleKit.InTransactionAsync(ctx, async () =>
    {
        if (ctx.Snapshot.PhaseKey != "inspect") return ActionOutcome.Fail("Samples are created while the record is in Inspect.");
        if (string.IsNullOrWhiteSpace(serial)) return ActionOutcome.Fail("Enter the serial or sample ID.");
        var progress = await InspectionProgress.ComputeAsync(ctx.Services, ctx.Record.Id, ctx.Snapshot.Data.Main);
        if (!Guid.TryParse(pointRaw, out var pointId) || progress.Points.All(p => p.PointId != pointId))
            return ActionOutcome.Fail("Choose one of the plan's inspection points.");
        var res = await ctx.Records.CreateAsync(new CreateRequest
        {
            FormKey = "inspection_sample",
            ParentId = ctx.Record.Id,
            SkipAccessCheck = true,
            Values = new() { ["serial"] = serial.Trim(), ["point"] = pointId, ["taken_by"] = ctx.Me.Id, ["taken_on"] = DateTime.UtcNow },
            CreateComment = $"Sample of {ctx.Record.Number}",
        });
        if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
        return ActionOutcome.Ok($"Sample {res.Record!.Number} created. Enter the measurements and save.", res.Record.Id);
    });

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var progress = await InspectionProgress.ComputeAsync(ctx.Services, ctx.Record.Id, ctx.Snapshot.Data.Main);
        return [new RecordPanel
        {
            Key = "ins-results", Title = "Inspection results", Partial = "~/Modules/Inspections/Views/_InspectionResults.cshtml", Placement = "top",
            Model = new InspectionResultsModel { Progress = progress, Snap = ctx.Snapshot },
        }];
    }
}

public sealed class InspectionResultsModel
{
    public required InspectionProgress Progress { get; init; }
    public required RecordSnapshot Snap { get; init; }
}

// ---------------------------------------------------------------------- inspection samples

/// <summary>
/// Samples: criteria loaded from the point (INS-012), each criterion evaluated (INV-069), the sample result (INV-167),
/// forced review (INV-169), and the parent record recalculated in the same transaction (INV-168).
/// </summary>
public sealed class InspectionSampleBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["inspection_sample"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var records = ctx.Services.GetRequiredService<RecordService>();
        var main = ctx.Proposed.Main;
        if (main.Link("point") is not { } pid || await records.LoadAsync(pid) is not { } point || point.Form.Key != "inspection_point")
        {
            ctx.Error("point", "Choose the inspection point this sample is for.");
            return;
        }
        ctx.Set("point_name", point.Text("name"));
        var rows = ctx.Proposed.RowsOf("measurements");
        var pointChanged = ctx.Current != null && ctx.Current.Data.Main.Link("point") != pid;
        if (pointChanged && ctx.Current!.PhaseKey != "inspect") { ctx.Error("point", "The point can only be changed during Inspect."); return; }

        // Criteria of the point, keyed by CID, with their profiles.
        var criteria = new List<(string Cid, string Class, RecordSnapshot Profile, string? Reference)>();
        var pointRows = point.Rows("criteria");
        for (var i = 0; i < pointRows.Count; i++)
        {
            var c = pointRows[i];
            if (c.Values.Link("profile") is not { } prof || await records.LoadAsync(prof) is not { } profile) continue;
            criteria.Add((c.Values.Text("cid") ?? $"{point.Record.Number}-{i + 1}", point.KeywordKey(c, "class") ?? "minor", profile, c.Values.Text("reference")));
        }

        if (rows.Count == 0 || pointChanged)
        {
            rows.Clear();
            foreach (var c in criteria)
                rows.Add(ModuleKit.NewRow(ctx.Form, ctx.Config, rows.Count, new()
                {
                    ["cid"] = c.Cid, ["criterion"] = c.Profile.Text("name"), ["class"] = c.Class,
                    ["spec"] = InspectionData.Spec(c.Profile) + (c.Reference != null ? $" ({c.Reference})" : ""),
                }));
        }

        var outcomes = new List<CriterionOutcome>();
        var summary = new List<string>();
        foreach (var row in rows)
        {
            var rid = row.IsNew ? (Guid?)null : row.Id;
            var cid = row.Values.Text("cid");
            var crit = criteria.FirstOrDefault(c => string.Equals(c.Cid, cid, StringComparison.OrdinalIgnoreCase));
            if (cid == null || crit.Profile == null)
            {
                ctx.Error("measurements", "Measurement rows come from the inspection point and can't be added by hand.");
                continue;
            }
            InspectionResult result;
            if (crit.Profile.KeywordKey("data_type") == "keyword")
            {
                var options = InspectionData.Keywords(crit.Profile);
                var observed = row.Values.Text("observed");
                result = InspectionEvaluator.EvaluateKeyword(options, observed);
                if (observed != null && result == InspectionResult.Pending)
                    ctx.Error("observed", $"{cid}: \"{observed}\" isn't one of {string.Join(", ", options.Where(o => !o.Disabled).Select(o => o.Keyword))}.", rid);
            }
            else
            {
                result = InspectionData.Limits(crit.Profile.Data.Main, crit.Profile.Config) is { } limits
                    ? InspectionEvaluator.Evaluate(limits, row.Values.Number("measured"))
                    : InspectionResult.Pending;
            }
            row.SetRow(ctx.Form, ctx.Config, "result", InspectionEvaluator.Key(result));
            row.SetRow(ctx.Form, ctx.Config, "class", crit.Class);
            outcomes.Add(new CriterionOutcome(crit.Class, result));
            if (result is InspectionResult.Warn or InspectionResult.Fail)
            {
                var value = row.Values.Number("measured")?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? row.Values.Text("observed");
                summary.Add($"{cid} {crit.Profile.Text("name")}: {value}{(crit.Profile.Text("unit") is { } u ? " " + u : "")} → {result}" +
                            $" ({ctx.Config.Keyword("ins_class", crit.Class)?.Label}){(row.Values.Text("remarks") is { } rm ? ". " + rm : "")}");
            }
        }

        var failureClasses = point.KeywordKeys("failure_classes").ToList();
        var sample = InspectionEvaluator.SampleResult(outcomes, failureClasses);
        ctx.Set("sample_result", InspectionEvaluator.Key(sample));
        ctx.Set("review_needed", InspectionEvaluator.ReviewRequired(point.KeywordKey("review_for"), outcomes.Select(o => o.Result), sample));
        ctx.Set("summary", summary.Count == 0 ? null : string.Join("\n", summary));
    }

    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        // Samples belong to an open inspection record, for one of its plan's points.
        if (ctx.Record.ParentId is not { } parentId || await ctx.Records.LoadAsync(parentId) is not { } parent || parent.Form.Key != "inspection_record")
            throw new WorkflowException("Samples are created from an inspection record.");
        if (parent.PhaseKey != "inspect") throw new WorkflowException($"{parent.Record.Number} is in {parent.Phase?.Name}: samples can only be added while it's in Inspect.");
        await InspectionRecordBehavior.RecomputeAsync(ctx, parentId);
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.Kind != RouteKind.Forward) return Task.CompletedTask;
        var snap = ctx.Snapshot;
        if (snap.KeywordKey("sample_result") is "pending" or null)
            ctx.Error("Enter every measurement first: the sample result is still pending.");
        // INV-169: a sample that meets its point's review condition never goes straight from Inspect to Close.
        if (ctx.From.Key == "inspect" && ctx.To.Type == PhaseType.Completed && snap.Bool("review_needed") == true)
            ctx.Error("This sample meets its point's review condition and must go to Review before it can be closed (INV-169).");
        return Task.CompletedTask;
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.Record.ParentId is { } parentId) await InspectionRecordBehavior.RecomputeAsync(ctx, parentId);
    }

    public override Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var snap = ctx.Snapshot;
        var flagged = snap.Rows("measurements").Count(r => snap.KeywordKey(r, "result") is "warn" or "fail");
        if (snap.Record.PhaseType == PhaseType.Voided || flagged == 0) return Task.FromResult(Enumerable.Empty<RecordAction>());
        var kinds = new List<(string, string)>();
        if (ctx.Config.FormsByKey.ContainsKey("ncr")) kinds.Add(("ncr", "Nonconformance (NCR)"));
        if (ctx.Config.FormsByKey.ContainsKey("capa")) kinds.Add(("capa", "CAPA"));
        kinds.Add(("action_item", "Action item"));
        var worst = snap.Rows("measurements").Where(r => snap.KeywordKey(r, "result") == "fail").Select(r => r.Values.Text("criterion")).FirstOrDefault()
                    ?? snap.Rows("measurements").Where(r => snap.KeywordKey(r, "result") == "warn").Select(r => r.Values.Text("criterion")).FirstOrDefault();
        IEnumerable<RecordAction> list =
        [
            new RecordAction
            {
                Key = "follow_up", Label = "Create follow-up", Icon = "bi-arrow-up-right-square", Group = "Follow-up",
                Description = "Creates a linked NCR, CAPA or action item from this sample's warnings and failures (INS-018).",
                Inputs =
                [
                    new ActionField("kind", "Create", "select", true, Options: kinds, Default: kinds[0].Item1),
                    new ActionField("subject", "Subject", "text", true, Default: $"{(snap.KeywordKey("sample_result") == "fail" ? "Failed" : "Out-of-control")} {worst} on sample {snap.Text("serial")}"),
                    new ActionField("owner", "Owner / assignee", "user", true),
                    new ActionField("due", "Due date (action item)", "date", false, Default: BusinessCalendar.Today.AddDays(14).ToString("yyyy-MM-dd")),
                ],
            },
        ];
        return Task.FromResult(list);
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "follow_up") return ActionOutcome.Fail("This action isn't available.");
        if (!await ModuleKit.CanActAsync(ctx) && ctx.Snapshot.UserId("taken_by") != ctx.Me.Id)
            return ActionOutcome.Fail("Only the sample's assignee, the inspector or an application manager can create follow-ups.");
        return await InspectionFollowUp.FromSampleAsync(ctx, input);
    }

    public override Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var snap = ctx.Snapshot;
        IEnumerable<RecordPanel> list = snap.Rows("measurements").Count == 0 ? [] :
        [
            new RecordPanel { Key = "ins-sample", Title = "Sample result", Partial = "~/Modules/Inspections/Views/_SampleResult.cshtml", Model = snap, Placement = "top" },
        ];
        return Task.FromResult(list);
    }
}

/// <summary>INS-018: follow-up NCR / CAPA / action item from a sample, or an action item from a record, linked back.</summary>
public static class InspectionFollowUp
{
    public static Task<ActionOutcome> ActionItemAsync(RecordContext ctx, ActionInput input) => ModuleKit.InTransactionAsync(ctx, async () =>
    {
        if (!int.TryParse(input.Get("assignee") ?? input.Get("owner"), out var assignee) || ctx.Directory.Get(assignee) is not { Assignable: true })
            return ActionOutcome.Fail("Choose who the action is assigned to.");
        if (!DateOnly.TryParse(input.Get("due"), System.Globalization.CultureInfo.InvariantCulture, out var due) || due < BusinessCalendar.Today)
            return ActionOutcome.Fail("Choose a due date that isn't in the past.");
        var subject = input.Get("subject") ?? $"Follow up on {ctx.Record.Number}";
        var res = await ctx.Records.CreateAsync(new CreateRequest
        {
            FormKey = "action_item", StartPhaseKey = "open", ParentId = ctx.Record.Id, SkipAccessCheck = true, Assignees = [assignee], DueDate = due,
            Values = new()
            {
                ["subject"] = subject.Length > 200 ? subject[..200] : subject, ["assignee"] = assignee, ["due_date"] = due,
                ["description"] = $"Follow-up from {ctx.Record.Number}: {ctx.Record.Title}" + (ctx.Snapshot.Text("summary") is { } s ? $"\n\n{s}" : ""),
            },
            CreateComment = $"Follow-up from {ctx.Record.Number} (INS-018)",
        });
        return res.Ok ? ActionOutcome.Ok($"Action item {res.Record!.Number} created.") : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
    });

    public static async Task<ActionOutcome> FromSampleAsync(RecordContext ctx, ActionInput input)
    {
        var kind = input.Get("kind");
        if (kind == "action_item") return await ActionItemAsync(ctx, input);
        if (kind is not ("ncr" or "capa") || !ctx.Config.FormsByKey.TryGetValue(kind, out var form)) return ActionOutcome.Fail("Choose what to create.");
        if (!int.TryParse(input.Get("owner"), out var owner) || ctx.Directory.Get(owner) is not { Assignable: true }) return ActionOutcome.Fail("Choose the owner.");
        return await ModuleKit.InTransactionAsync(ctx, async () =>
        {
            var snap = ctx.Snapshot;
            var parent = snap.Record.ParentId is { } pid ? await ctx.Records.LoadAsync(pid) : null;
            var subject = input.Get("subject") ?? $"Inspection failure on {snap.Text("serial")}";
            if (subject.Length > 200) subject = subject[..197] + "…";
            var description = $"Raised from inspection sample {snap.Record.Number} ({snap.Text("point_name")})" +
                              (parent != null ? $", lot {parent.Text("lot_number")} of {parent.Text("plan_name")}" : "") + ".\n\n" + (snap.Text("summary") ?? "");
            var classes = snap.Rows("measurements").Where(r => snap.KeywordKey(r, "result") == "fail").Select(r => snap.KeywordKey(r, "class")).ToList();
            var values = new Dictionary<string, object?>();
            void Try(string key, object? value)
            {
                if (value == null || form.Fields.FirstOrDefault(f => f.Key == key) is not { } f) return;
                var ok = value switch
                {
                    string => f.Type is FieldType.Text or FieldType.TextArea || (f.Type is FieldType.Keyword && f.KeywordList != null && ctx.Config.Keyword(f.KeywordList, (string)value) != null),
                    int => f.Type is FieldType.User or FieldType.UserMulti,
                    Guid => f.Type == FieldType.Link,
                    decimal => f.Type is FieldType.Number,
                    DateOnly => f.Type == FieldType.Date,
                    _ => false,
                };
                if (ok && !values.ContainsKey(key)) values[key] = value;
            }
            Try("subject", subject);
            Try("title", subject);
            Try("description", description);
            Try("problem_description", description);
            Try("nonconformance", description);
            Try("owner", owner);
            Try("product", parent?.Text("plan_name"));
            Try("lot_number", parent?.Text("lot_number"));
            Try("source_record", snap.Record.Id);
            if (kind == "capa")
            {
                Try("capa_type", "corrective");
                Try("source", "ncr");
                Try("priority", classes.Contains("critical") ? "critical" : classes.Contains("major") ? "high" : "medium");
                Try("discovered_on", BusinessCalendar.Today);
            }
            var res = await ctx.Records.CreateAsync(new CreateRequest { FormKey = kind, SkipAccessCheck = true, Values = values, CreateComment = $"Raised from inspection sample {snap.Record.Number} (INS-018)" });
            if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            await ctx.Records.LinkAsync(snap.Record.Id, res.Record!.Id, LinkKind.Source);
            await ctx.Db.SaveChangesAsync();
            await ctx.Records.AddCommentAsync(snap.Record.Id, $"Raised {form.Name} {res.Record.Number} from this sample.", "system", system: true);
            return ActionOutcome.Ok($"{form.Name} {res.Record.Number} created and linked to this sample.");
        });
    }
}
