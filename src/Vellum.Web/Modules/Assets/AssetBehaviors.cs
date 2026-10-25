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

namespace Vellum.Modules.Assets;

public sealed record CalHistoryRow(Guid Id, string Number, string Form, DateOnly? Date, string? FoundResult, string? LeftResult, PhaseType Phase, string? Type);
public sealed record ImpactRow(Guid Id, string Number, string Title, DateOnly Date, string? Result, PhaseType Phase);

/// <summary>Queries shared by the asset behaviors, the job and the home widget.</summary>
public static class AssetQueries
{
    private static string D(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// INV-115 / AST-012 / SAF-040: why a standard can't be used on <paramref name="calDate"/> (null when it can). A standard must be marked
    /// for use as a standard, not quarantined or retired, and have a passing calibration on or before that date that hadn't expired.
    /// </summary>
    public static async Task<string?> StandardProblemAsync(VellumDbContext db, ConfigModel cfg, Guid standardId, DateOnly calDate)
    {
        var rec = await db.Records.AsNoTracking().Where(r => r.Id == standardId).Select(r => new { r.FormId }).FirstOrDefaultAsync();
        if (rec == null || !cfg.FormsById.TryGetValue(rec.FormId, out var form) || form.Key != "asset") return "isn't an asset";
        var v = (await ModuleKit.MainValuesAsync(db, [standardId], "use_as_standard", "status", "cal_interval_days"))[standardId];
        if (v.Bool("use_as_standard") != true) return "isn't marked for use as a calibration standard";
        var status = cfg.Keyword(v.Ref("status"))?.Key;
        if (status == "quarantined") return "is quarantined (out of tolerance)";
        if (status == "retired") return "is retired";

        var history = await HistoryAsync(db, cfg, standardId, "calibration");
        var done = history.Where(h => h.Phase == PhaseType.Completed && h.Date is { } d && d <= calDate).ToList();
        var lastPass = done.Where(h => h.LeftResult == "pass").Select(h => h.Date!.Value).DefaultIfEmpty().Max();
        if (lastPass == default) return $"had no passing calibration on or before {D(calDate)}";
        if (done.FirstOrDefault(h => h.LeftResult == "fail" && h.Date > lastPass) is { } failed)
            return $"failed its calibration {failed.Number} on {D(failed.Date!.Value)}";
        var interval = (int)(v.Number("cal_interval_days") ?? 0);
        if (interval > 0 && lastPass.AddDays(interval) < calDate)
            return $"was overdue for calibration on {D(calDate)} (due {D(lastPass.AddDays(interval))})";
        return null;
    }

    /// <summary>Calibration or maintenance records of an asset, newest first.</summary>
    public static async Task<List<CalHistoryRow>> HistoryAsync(VellumDbContext db, ConfigModel cfg, Guid assetId, params string[] forms)
    {
        var formIds = forms.Select(k => cfg.FormsByKey.GetValueOrDefault(k)?.Id ?? -1).ToList();
        var recs = await db.Records.AsNoTracking().Where(r => r.ParentId == assetId && formIds.Contains(r.FormId) && r.PhaseType != PhaseType.Voided)
            .Select(r => new { r.Id, r.Number, r.FormId, r.PhaseType }).ToListAsync();
        var values = await ModuleKit.MainValuesAsync(db, recs.Select(r => r.Id).ToList(), "cal_date", "as_found_result", "as_left_result", "performed_on", "maintenance_type", "returned_to_service");
        return recs.Select(r =>
        {
            var v = values[r.Id];
            var key = cfg.FormsById[r.FormId].Key;
            return new CalHistoryRow(r.Id, r.Number, key, key == "calibration" ? v.Date("cal_date") : v.Date("returned_to_service") ?? v.Date("performed_on"),
                cfg.Keyword(v.Ref("as_found_result"))?.Key, cfg.Keyword(v.Ref("as_left_result"))?.Key, r.PhaseType, cfg.Keyword(v.Ref("maintenance_type"))?.Label);
        }).OrderByDescending(h => h.Date).ThenByDescending(h => h.Number).ToList();
    }

    /// <summary>
    /// AST-024 measurement impact: calibrations that used <paramref name="assetId"/> as a standard since its last passing calibration
    /// before <paramref name="until"/>. Security-trimmed (INV-081).
    /// </summary>
    public static async Task<(DateOnly? Since, List<ImpactRow> Rows)> ImpactAsync(VellumDbContext db, AccessService access, ConfigModel cfg,
        Guid assetId, DateOnly until, Guid? excludeCalibration)
    {
        var own = await HistoryAsync(db, cfg, assetId, "calibration");
        DateOnly? since = own.Where(h => h.Id != excludeCalibration && h.Phase == PhaseType.Completed && h.LeftResult == "pass" && h.Date < until)
            .Select(h => h.Date).Max();
        var calForm = cfg.FormsByKey.GetValueOrDefault("calibration")?.Id ?? -1;
        var usedIds = await db.RecordValues.AsNoTracking().Where(v => v.FieldKey == "standards" && v.RowId == null && v.RefGuid == assetId)
            .Select(v => v.RecordId).Distinct().ToListAsync();
        var recs = await access.Readable().AsNoTracking().Where(r => usedIds.Contains(r.Id) && r.FormId == calForm && r.PhaseType != PhaseType.Voided)
            .Select(r => new { r.Id, r.Number, r.Title, r.PhaseType }).ToListAsync();
        var values = await ModuleKit.MainValuesAsync(db, recs.Select(r => r.Id).ToList(), "cal_date", "as_left_result");
        var rows = recs.Select(r => (r, d: values[r.Id].Date("cal_date"), res: cfg.Keyword(values[r.Id].Ref("as_left_result"))?.Key))
            .Where(x => x.d is { } d && (since == null || d >= since) && d <= until)
            .Select(x => new ImpactRow(x.r.Id, x.r.Number, x.r.Title, x.d!.Value, x.res, x.r.PhaseType))
            .OrderBy(x => x.Date).ToList();
        return (since, rows);
    }

    /// <summary>AST-026: a calibration or maintenance record can't complete while someone else has the asset profile open for editing.</summary>
    public static async Task<string?> LockedByOtherAsync(VellumDbContext db, UserDirectory dir, CurrentUser me, Guid assetId)
    {
        var l = await db.Locks.AsNoTracking().FirstOrDefaultAsync(x => x.RecordId == assetId);
        return l != null && l.UserId != me.Id && l.LastActivityAt > DateTime.UtcNow.AddMinutes(-LockService.TimeoutMinutes)
            ? $"{dir.Name(l.UserId)} is editing the asset profile. Complete this record when they're done (AST-026)."
            : null;
    }

    public static string AssetRef(RecordSnapshot asset) => $"{asset.Text("asset_id")} · {asset.Text("description")}";
}

/// <summary>Asset profile: immutable unique ID (INV-022), schedule dates and status (AST-005, AST-027), quarantine and copy actions.</summary>
public sealed class AssetBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["asset"];

    private static readonly string[] NotCopied =
        ["asset_id", "serial_number", "status", "quarantine_reason", "last_cal_date", "next_cal_date", "last_cal_result", "last_pm_date", "next_pm_date"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        if (m.Text("asset_id") is { } raw && raw.Trim().ToUpperInvariant() is var id && id != raw) ctx.Set("asset_id", id);
        if (ctx.Current is { } cur && !string.Equals(cur.Text("asset_id"), m.Text("asset_id"), StringComparison.OrdinalIgnoreCase))
            ctx.Error("asset_id", "An asset ID can't change once the asset is registered (INV-022). Use Copy profile to register a similar asset under a new ID.");

        var calRequired = m.Bool("calibration_required") == true;
        var interval = (int)(m.Number("cal_interval_days") ?? 0);
        if (calRequired && interval <= 0) ctx.Error("cal_interval_days", "Give the calibration interval in days.");
        DateOnly? next = calRequired && interval > 0 && m.Date("last_cal_date") is { } last ? last.AddDays(interval) : null;
        ctx.Set("next_cal_date", next);

        var pmInterval = (int)(m.Number("pm_interval_days") ?? 0);
        if (m.Bool("pm_required") == true && pmInterval <= 0) ctx.Error("pm_interval_days", "Give the preventive maintenance interval in days.");
        ctx.Set("next_pm_date", m.Bool("pm_required") == true && pmInterval > 0 && m.Date("last_pm_date") is { } lastPm ? lastPm.AddDays(pmInterval) : null);

        var status = AssetStatus.Compute(ctx.KeywordKey("status"), calRequired, next, interval, BusinessCalendar.Today);
        ctx.Set("status", status);
        if (status != "quarantined" && m.Has("quarantine_reason")) ctx.Set("quarantine_reason", null);
        if (m.Bool("use_as_standard") == true && !calRequired)
            ctx.Error("calibration_required", "A calibration standard must itself be calibrated (INV-115).");
        return Task.CompletedTask;
    }

    /// <summary>INV-022 in the database: claim the tag in the same transaction (primary key on the tag).</summary>
    public override Task OnCreatedAsync(RecordContext ctx)
    {
        ctx.Db.Set<AssetTagClaim>().Add(new AssetTagClaim { Tag = ctx.Snapshot.Text("asset_id")!, RecordId = ctx.Record.Id, ClaimedAt = DateTime.UtcNow });
        return Task.CompletedTask;
    }

    private static bool IsAssignee(RecordContext ctx) =>
        ctx.Snapshot.UserId("technician") == ctx.Me.Id || ctx.Access.RoleFor(ctx.Record.AppId) >= AppRole.Manager;

    private static string AssigneeRule(RecordContext ctx) =>
        $"Only the responsible technician ({ctx.Directory.Get(ctx.Snapshot.UserId("technician"))?.DisplayName ?? "not set"}) or an application manager can do this (INV-087).";

    private static async Task<string?> OpenChildAsync(RecordContext ctx, string formKey)
    {
        var fid = ctx.Config.Form(formKey).Id;
        return await ctx.Db.Records.AsNoTracking().Where(r => r.ParentId == ctx.Record.Id && r.FormId == fid && r.PhaseType == PhaseType.Normal)
            .Select(r => r.Number).FirstOrDefaultAsync();
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (ctx.Me.IsSystem) return [];
        var s = ctx.Snapshot;
        var status = s.KeywordKey("status");
        var retired = status == "retired";
        var author = ctx.Access.RoleFor(ctx.Record.AppId) >= AppRole.Author;
        var list = new List<RecordAction>();
        if (s.Bool("calibration_required") == true)
        {
            var open = await OpenChildAsync(ctx, "calibration");
            list.Add(new RecordAction
            {
                Key = "record_calibration", Label = "Record calibration", Icon = "bi-bullseye", Group = "Calibrate",
                Description = "Start a calibration with one reading per characteristic (AST-020).",
                DisabledReason = retired ? "Retired assets aren't calibrated." : !IsAssignee(ctx) ? AssigneeRule(ctx)
                    : s.Rows("characteristics").Count == 0 ? "Add at least one characteristic first (AST-011)." : open != null ? $"{open} is already in progress." : null,
            });
        }
        list.Add(new RecordAction
        {
            Key = "record_maintenance", Label = "Record maintenance", Icon = "bi-wrench-adjustable", Group = "Calibrate",
            Description = "Start a preventive, corrective or predictive maintenance record (AST-041).",
            DisabledReason = retired ? "Retired assets aren't maintained." : !IsAssignee(ctx) ? AssigneeRule(ctx) : null,
            Inputs = [new ActionField("type", "Maintenance type", "select", true, Options: [("preventive", "Preventive"), ("corrective", "Corrective (repair)"), ("predictive", "Predictive")], Default: "preventive")],
        });
        if (!retired && status != "quarantined")
            list.Add(new RecordAction
            {
                Key = "quarantine", Label = "Quarantine asset", Icon = "bi-sign-stop", Group = "Asset", Variant = "danger",
                Description = "Take the asset out of use, for example after damage or a suspect reading.",
                Confirm = "A quarantined asset can't be used to measure or as a standard until a passing calibration is recorded (SAF-040, AST-025).",
                RequiresComment = true, CommentLabel = "Reason for quarantine",
                DisabledReason = author ? null : "Only authors in Assets & Calibration can quarantine assets.",
            });
        if (!retired)
            list.Add(new RecordAction
            {
                Key = "retire", Label = "Retire asset", Icon = "bi-archive", Group = "Asset", Variant = "secondary",
                Description = "Permanently withdraw the asset. Its ID stays reserved (INV-022).",
                RequiresComment = true, CommentLabel = "Reason", Confirm = "Retired assets can't be calibrated, maintained or used to measure.",
                DisabledReason = author ? null : "Only authors in Assets & Calibration can retire assets.",
            });
        list.Add(new RecordAction
        {
            Key = "copy", Label = "Copy profile", Icon = "bi-copy", Group = "Asset",
            Description = "Register a similar asset: copies everything except the asset ID, serial number, status and history (AST-002).",
            Inputs = [new ActionField("new_id", "New asset ID", "text", true, "Must be unique across all sites (INV-022).")],
            DisabledReason = ctx.Access.CanCreate(ctx.Snapshot.Form) ? null : "You can't register assets.",
        });
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        var s = ctx.Snapshot;
        var actions = (await ActionsAsync(ctx)).ToDictionary(a => a.Key);
        if (!actions.TryGetValue(actionKey, out var action)) return ActionOutcome.Fail("This action isn't available.");
        if (action.DisabledReason != null) return ActionOutcome.Fail(action.DisabledReason);
        var notify = ctx.Get<NotificationService>();
        var audit = ctx.Get<AuditService>();
        var assetRef = AssetQueries.AssetRef(s);

        switch (actionKey)
        {
            case "record_calibration":
            {
                var rows = s.Rows("characteristics").Select(r => new Dictionary<string, object?>
                {
                    ["characteristic"] = r.Values.Text("characteristic"), ["unit"] = r.Values.Text("unit"), ["nominal"] = r.Values.Number("nominal"),
                    ["tol_minus"] = r.Values.Number("tol_minus"), ["tol_plus"] = r.Values.Number("tol_plus"),
                }).ToList();
                var external = !string.IsNullOrWhiteSpace(s.Text("cal_vendor"));
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "calibration", ParentId = s.Record.Id, SkipAccessCheck = true, Assignees = [ctx.Me.Id],
                    Values = new()
                    {
                        ["asset_ref"] = assetRef, ["cal_date"] = BusinessCalendar.Today, ["technician"] = ctx.Me.Id,
                        ["cal_type"] = external ? "external" : "internal", ["vendor"] = external ? s.Text("cal_vendor") : null,
                    },
                    RowValues = new() { ["readings"] = rows },
                    CreateComment = "Recorded from the asset profile (AST-020)",
                });
                return res.Ok ? ActionOutcome.Ok($"Calibration {res.Record!.Number} started.", res.Record.Id) : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            }
            case "record_maintenance":
            {
                var type = input.Get("type") is "corrective" or "predictive" ? input.Get("type")! : "preventive";
                List<Dictionary<string, object?>> tasks = type == "preventive"
                    ? [new() { ["task"] = "Inspect for damage, wear and leaks" }, new() { ["task"] = "Clean and lubricate as specified" }, new() { ["task"] = "Check safety devices and guards" }, new() { ["task"] = "Functional check after maintenance" }]
                    : [];
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "maintenance", ParentId = s.Record.Id, SkipAccessCheck = true, Assignees = [ctx.Me.Id],
                    Values = new() { ["asset_ref"] = assetRef, ["maintenance_type"] = type, ["performed_on"] = BusinessCalendar.Today, ["technician"] = ctx.Me.Id },
                    RowValues = new() { ["tasks"] = tasks },
                    CreateComment = "Recorded from the asset profile (AST-041)",
                });
                return res.Ok ? ActionOutcome.Ok($"Maintenance {res.Record!.Number} started.", res.Record.Id) : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            }
            case "quarantine":
            case "retire":
            {
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("Give a reason.");
                var quarantine = actionKey == "quarantine";
                return await Tx.RunAsync(ctx.Db, async () =>
                {
                    var res = await ctx.Records.UpdateAsync(s.Record.Id, quarantine
                        ? new() { ["status"] = "quarantined", ["quarantine_reason"] = input.Comment.Trim() }
                        : new() { ["status"] = "retired" }, input.Comment.Trim());
                    if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                    audit.Record(s.Record.Id, quarantine ? "Asset quarantined" : "Asset retired", $"{assetRef}: {input.Comment.Trim()}");
                    if (quarantine)
                    {
                        var title = $"{s.Text("asset_id")} quarantined: don't use it to measure";
                        var body = $"{assetRef} was quarantined by {ctx.Me.Name}: {input.Comment.Trim()}";
                        foreach (var uid in new[] { s.UserId("owner"), s.UserId("technician") }.OfType<int>().Distinct()) notify.Notify(uid, NotificationKind.Escalation, title, body, s.Record.Id);
                        notify.ToGroup("maintenance", NotificationKind.Escalation, title, body, s.Record.Id);
                    }
                    await ctx.Db.SaveChangesAsync();
                    return ActionOutcome.Ok(quarantine ? "Asset quarantined. It's released only by a passing calibration." : "Asset retired.");
                });
            }
            case "copy":
            {
                var newId = input.Get("new_id")?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(newId)) return ActionOutcome.Fail("Enter the new asset ID.");
                var values = new Dictionary<string, object?>();
                foreach (var f in s.Form.Fields.Where(f => FieldCodec.StoresValues(f.Type) && !NotCopied.Contains(f.Key)))
                    if (s.Form.Sections.First(x => x.Key == f.SectionKey) is { IsSubform: false } && s.Data.Main.Has(f.Key))
                        values[f.Key] = s.Data.Main[f.Key];
                values["asset_id"] = newId;
                var chars = s.Rows("characteristics").Select(r => r.Values.Keys.ToDictionary(k => k, k => (object?)r.Values[k])).ToList();
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "asset", Values = values, RowValues = new() { ["characteristics"] = chars },
                    CreateComment = $"Copied from {s.Record.Number} ({s.Text("asset_id")}) (AST-002)",
                });
                return res.Ok ? ActionOutcome.Ok($"Asset {newId} registered as {res.Record!.Number}.", res.Record.Id) : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            }
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var history = await AssetQueries.HistoryAsync(ctx.Db, ctx.Config, s.Record.Id, "calibration", "maintenance");
        var usedIn = 0;
        if (s.Bool("use_as_standard") == true)
            usedIn = await ctx.Db.RecordValues.AsNoTracking().Where(v => v.FieldKey == "standards" && v.RowId == null && v.RefGuid == s.Record.Id)
                .Select(v => v.RecordId).Distinct().CountAsync();
        var vm = new AssetPanelVm(s.Keyword("status"), s.Date("next_cal_date"), s.Date("last_cal_date"), s.Keyword("last_cal_result"),
            s.Bool("calibration_required") == true, (int)(s.Number("cal_interval_days") ?? 0), s.Date("next_pm_date"), history, usedIn, s.Bool("use_as_standard") == true);
        return [new RecordPanel { Key = "asset_status", Title = "Calibration status", Icon = "bi-speedometer2", Partial = "~/Modules/Assets/Views/_AssetPanel.cshtml", Model = vm, Placement = "side", Order = -10 }];
    }
}

public sealed record AssetPanelVm(Keyword? Status, DateOnly? NextCal, DateOnly? LastCal, Keyword? LastResult, bool CalRequired, int Interval,
    DateOnly? NextPm, List<CalHistoryRow> History, int UsedAsStandard, bool IsStandard);

/// <summary>
/// Calibration record: AST-022 per reading (INV-064), overall as-found / as-left (AST-023), standards in calibration (INV-115),
/// as-found failures need a CAPA or a justified bypass (INV-116), and completion updates the asset (AST-025, AST-027).
/// </summary>
public sealed class CalibrationBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["calibration"];

    private static string? Key(bool? pass) => pass switch { true => "pass", false => "fail", null => null };

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var found = new List<bool?>();
        var left = new List<bool?>();
        foreach (var row in ctx.Proposed.RowsOf("readings"))
        {
            var v = row.Values;
            var (nominal, minus, plus) = (v.Number("nominal"), v.Number("tol_minus"), v.Number("tol_plus"));
            foreach (var (input, dev, result, list) in new[] { ("found", "found_dev", "found_result", found), ("left", "left_dev", "left_result", left) })
            {
                if (nominal is { } n && minus is { } tm && plus is { } tp && tm >= 0 && tp >= 0 && v.Number(input) is { } reading)
                {
                    var r = CalibrationMath.Evaluate(reading, n, tm, tp);
                    ctx.SetRow(row, dev, r.Deviation);
                    ctx.SetRow(row, result, Key(r.Pass));
                    list.Add(r.Pass);
                }
                else
                {
                    ctx.SetRow(row, dev, null);
                    ctx.SetRow(row, result, null);
                    list.Add(null);
                }
            }
        }
        ctx.Set("as_found_result", Key(CalibrationMath.Overall(found)));
        ctx.Set("as_left_result", Key(CalibrationMath.Overall(left)));

        var calDate = ctx.Proposed.Main.Date("cal_date");
        if (calDate > BusinessCalendar.Today) ctx.Error("cal_date", "The calibration date can't be in the future.");
        if (calDate is { } d)
        {
            var db = ctx.Services.GetRequiredService<VellumDbContext>();
            foreach (var std in ctx.Proposed.Main.Links("standards"))
                if (await AssetQueries.StandardProblemAsync(db, ctx.Config, std, d) is { } problem)
                    ctx.Error("standards", $"{await LabelAsync(db, std)} {problem}. It can't be used as a standard (INV-115).");
        }
    }

    private static async Task<string> LabelAsync(VellumDbContext db, Guid id) =>
        await db.Records.AsNoTracking().Where(r => r.Id == id).Select(r => r.Title).FirstOrDefaultAsync() ?? "The standard";

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "completed") return;
        var s = ctx.Snapshot;
        var rows = s.Rows("readings");
        if (rows.Any(r => r.Values.Number("found") == null || r.Values.Number("left") == null))
            ctx.Error("Enter the as-found and as-left reading of every characteristic.");
        var standards = s.Links("standards");
        if (s.KeywordKey("cal_type") == "internal" && standards.Count == 0)
            ctx.Error("List the standards used for this in-house calibration (traceability, AST-028).");
        if (s.KeywordKey("cal_type") == "external" && string.IsNullOrWhiteSpace(s.Text("certificate_no")))
            ctx.Error("Enter the vendor's calibration certificate number.");
        if (s.Record.ParentId is { } assetId)
        {
            if (standards.Contains(assetId)) ctx.Error("An asset can't be its own calibration standard.");
            var asset = await ctx.Records.LoadAsync(assetId);
            if (asset?.KeywordKey("status") == "retired") ctx.Error("The asset is retired.");
            if (await AssetQueries.LockedByOtherAsync(ctx.Db, ctx.Directory, ctx.Me, assetId) is { } locked) ctx.Error(locked);
        }
        if (s.Date("cal_date") is { } d)
            foreach (var std in standards)
                if (await AssetQueries.StandardProblemAsync(ctx.Db, ctx.Config, std, d) is { } problem)
                    ctx.Error($"{await LabelAsync(ctx.Db, std)} {problem} (INV-115).");

        // INV-116 / AST-024: an as-found failure completes only with a linked CAPA or a justified bypass.
        if (s.KeywordKey("as_found_result") == "fail")
        {
            if (string.IsNullOrWhiteSpace(s.Text("impact_assessment")))
                ctx.Error("Record the measurement impact assessment for the as-found failure (AST-024).");
            var capa = s.Links("capa_link").FirstOrDefault();
            var capaExists = capa != Guid.Empty && await ctx.Db.Records.AnyAsync(r => r.Id == capa && r.PhaseType != PhaseType.Voided);
            var bypass = s.Bool("bypass_capa") == true && !string.IsNullOrWhiteSpace(s.Text("bypass_reason"));
            if (!capaExists && !bypass)
                ctx.Error("The as-found result failed: link a corrective action (CAPA) or choose \"Bypass corrective action\" and give the reason (INV-116).");
        }
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "completed" || ctx.Snapshot.Record.ParentId is not { } assetId) return;
        var s = ctx.Snapshot;
        var asset = await ctx.Records.LoadRequiredAsync(assetId);
        var calDate = s.Date("cal_date") ?? BusinessCalendar.Today;
        var leftFail = s.KeywordKey("as_left_result") == "fail";
        var foundFail = s.KeywordKey("as_found_result") == "fail";
        var isLatest = asset.Date("last_cal_date") is not { } last || calDate >= last;
        var values = new Dictionary<string, object?>();
        if (isLatest)
        {
            values["last_cal_date"] = calDate;
            values["last_cal_result"] = leftFail ? "fail" : "pass";
        }
        if (leftFail)
        {
            values["status"] = "quarantined";
            values["quarantine_reason"] = $"Out of tolerance after adjustment on {s.Record.Number} ({calDate:dd MMM yyyy}). Released only by a passing calibration (AST-025).";
        }
        else if (isLatest && asset.KeywordKey("status") == "quarantined")
        {
            values["status"] = "in_service"; // AST-025: a passing calibration releases the quarantine; the schedule then sets due/overdue.
            values["quarantine_reason"] = null;
        }
        var res = await ctx.Records.UpdateAsync(assetId, values, $"Calibration {s.Record.Number} completed");
        if (!res.Ok) throw new WorkflowException("Couldn't update the asset: " + string.Join("; ", res.Errors.Select(e => e.Message)));

        foreach (var std in s.Links("standards")) await ctx.Records.LinkAsync(s.Record.Id, std, LinkKind.Field, "standards");
        foreach (var capa in s.Links("capa_link")) await ctx.Records.LinkAsync(s.Record.Id, capa, LinkKind.Field, "capa_link");

        var notify = ctx.Get<NotificationService>();
        var assetRef = AssetQueries.AssetRef(asset);
        var people = new[] { asset.UserId("owner"), asset.UserId("technician") }.OfType<int>().Distinct().ToList();
        if (foundFail)
        {
            var title = $"As-found failure on {asset.Text("asset_id")}: review measurement impact";
            var body = $"{s.Record.Number} found {assetRef} out of tolerance. Measurements made with it since its last good calibration need review (AST-024).";
            foreach (var uid in people) notify.Notify(uid, NotificationKind.Escalation, title, body, s.Record.Id);
            notify.ToGroup("quality", NotificationKind.Escalation, title, body, s.Record.Id);
        }
        if (leftFail)
        {
            var title = $"{asset.Text("asset_id")} quarantined after a failed calibration";
            var body = $"{assetRef} is still out of tolerance after adjustment ({s.Record.Number}). Don't use it to measure until a passing calibration is recorded.";
            foreach (var uid in people) notify.Notify(uid, NotificationKind.Escalation, title, body, assetId);
            notify.ToGroup("maintenance", NotificationKind.Escalation, title, body, assetId);
        }
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        if (s.KeywordKey("as_found_result") != "fail" || s.Record.ParentId is not { } assetId) return [];
        var (since, rows) = await AssetQueries.ImpactAsync(ctx.Db, ctx.Access, ctx.Config, assetId, s.Date("cal_date") ?? BusinessCalendar.Today, s.Record.Id);
        return [new RecordPanel
        {
            Key = "impact", Title = "Measurement impact", Icon = "bi-diagram-3", Count = rows.Count, Order = 5,
            Partial = "~/Modules/Assets/Views/_Impact.cshtml", Model = new ImpactVm(since, s.Date("cal_date"), rows),
        }];
    }
}

public sealed record ImpactVm(DateOnly? Since, DateOnly? Until, List<ImpactRow> Rows);

/// <summary>Maintenance record: corrective work puts the asset under maintenance; completion updates the PM schedule and status.</summary>
public sealed class MaintenanceBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["maintenance"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        if (m.Date("returned_to_service") is { } back && m.Date("performed_on") is { } on && back < on)
            ctx.Error("returned_to_service", "The asset can't return to service before the maintenance was performed.");
        if (m.Date("returned_to_service") > BusinessCalendar.Today) ctx.Error("returned_to_service", "The return-to-service date can't be in the future.");
        return Task.CompletedTask;
    }

    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.KeywordKey("maintenance_type") != "corrective" || ctx.Record.ParentId is not { } assetId) return;
        var asset = await ctx.Records.LoadRequiredAsync(assetId);
        if (asset.KeywordKey("status") is "quarantined" or "retired" or "maintenance") return;
        await ctx.Records.UpdateAsync(assetId, new() { ["status"] = "maintenance" }, $"Corrective maintenance {ctx.Record.Number} started");
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key == "completed" && ctx.Snapshot.Record.ParentId is { } assetId &&
            await AssetQueries.LockedByOtherAsync(ctx.Db, ctx.Directory, ctx.Me, assetId) is { } locked)
            ctx.Error(locked);
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Type == PhaseType.Normal || ctx.Snapshot.Record.ParentId is not { } assetId) return;
        var s = ctx.Snapshot;
        var asset = await ctx.Records.LoadRequiredAsync(assetId);
        var values = new Dictionary<string, object?>();
        var status = asset.KeywordKey("status");
        if (ctx.To.Key == "completed")
        {
            if (s.KeywordKey("maintenance_type") == "preventive" && s.Date("returned_to_service") is { } back &&
                (asset.Date("last_pm_date") is not { } last || back >= last))
                values["last_pm_date"] = back;
            if (s.KeywordKey("result") == "failed")
            {
                values["status"] = "quarantined";
                values["quarantine_reason"] = $"Failed maintenance {s.Record.Number}: {s.Text("work_performed")}";
            }
            else if (status == "maintenance") values["status"] = "in_service";
        }
        else if (status == "maintenance") values["status"] = "in_service"; // voided corrective work
        if (values.Count > 0) await ctx.Records.UpdateAsync(assetId, values, $"Maintenance {s.Record.Number} {ctx.To.Name.ToLowerInvariant()}");
    }
}
