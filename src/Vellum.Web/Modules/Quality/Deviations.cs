using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// Planned deviations §12.5: Draft → Review → Initial Approval → Final Approval → Active → Closed / Expired (DEV-002).
/// Validity limits (dates, quantity, lots) close the deviation automatically (DEV-002, <see cref="DeviationExpiryJob"/>);
/// at most one active deviation per part number + defect code (INV-118); the owner is notified on expiry (DEV-003).
/// </summary>
public sealed class DeviationApp : IPackagedApp
{
    public string Key => "deviations";
    public int Order => 32;

    public void Define(AppBuilder app)
    {
        app.Name("Planned Deviations")
            .Description("Approve temporary, controlled departures from a requirement, with validity limits that close them automatically.")
            .Icon("bi-sign-turn-right").Color("#d97706").Suite("Quality").Order(30).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("@engineering", AppRole.Author)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        DefectCodes.Keywords(app);
        app.Keywords("deviation_type", "Deviation type", k => k
            .Add("product", "Product / specification").Add("process", "Process / method").Add("material", "Material / component")
            .Add("documentation", "Documentation").Add("equipment", "Equipment / tooling"));
        app.Keywords("deviation_risk", "Deviation risk", k => k
            .Add("low", "Low", "success").Add("medium", "Medium", "warning").Add("high", "High", "danger"));
        app.Keywords("deviation_closure", "Deviation closure reason", k => k
            .Add("end_date", "End date reached", "secondary").Add("quantity_used", "Quantity limit used up", "info")
            .Add("lots_used", "Lot limit used up", "info").Add("closed_manual", "Closed by owner", "secondary"));

        app.Form("deviation", "Planned Deviation", f => f
            .Icon("bi-sign-turn-right")
            .Description("A temporary, approved departure from a requirement for a defined part, defect, period and quantity.")
            .Numbering("DEV-")
            .Title("{subject}")
            .Section("dev_info", "Deviation information", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("For example: Accept cosmetic scratches on manifold 40-1180 lot M-2291");
                s.User("owner", "Deviation owner").Required().Header().Default("$me");
                s.User("reviewer", "Technical reviewer").RequiredIn("draft").Help("Reviews the technical justification before quality approval.");
                s.Keyword("deviation_type", "Type", "deviation_type").RequiredIn("draft");
                s.Text("part_number", "Part number", 4).RequiredIn("draft").Header();
                s.Text("part_description", "Part description", 4);
                s.Keyword("defect_code", "Defect code", "ncr_defect_code").RequiredIn("draft").Header()
                    .Help("At most one deviation can be active for the same part number and defect code (INV-118).");
                s.Text("lot_numbers", "Lots / batches covered", 6);
                s.Link("source_record", "Source record", "ncr", "capa").Help("The NCR (Use As Is) or CAPA this deviation supports (DEV-004).");
                s.TextArea("scope", "Scope: what may differ, where and for what").RequiredIn("draft");
                s.Text("requirement", "Requirement deviated from", 6).RequiredIn("draft").Placeholder("Drawing 40-1180 rev D note 4: no visible scratches");
                s.Link("requirement_document", "Controlled document").Help("The specification, drawing or procedure being deviated from.");
                s.TextArea("justification", "Justification").RequiredIn("draft");
            }, icon: "bi-info-circle")
            .Section("dev_risk", "Risk assessment", s =>
            {
                s.Keyword("risk_level", "Risk", "deviation_risk").RequiredIn("draft", "review").Header();
                s.YesNo("fit_function_impact", "Impact on form, fit or function?").RequiredIn("review");
                s.YesNo("patient_safety_impact", "Any impact on patient safety?").RequiredIn("review");
                s.TextArea("risk_summary", "Risk evaluation and controls").RequiredIn("review");
            }, icon: "bi-exclamation-diamond")
            .Section("dev_limits", "Validity limits", s =>
            {
                s.Prompt("dev_limits_prompt", "The deviation closes automatically when the end date passes or a quantity or lot limit is used up (DEV-002).");
                s.Date("valid_from", "Valid from").Required().Default("$today");
                s.Date("valid_to", "Valid until").Required().Header();
                s.Number("max_quantity", "Maximum quantity").Min(0).Help("Leave empty for no quantity limit.");
                s.Number("max_lots", "Maximum lots").Min(0).Decimals(0).Help("Leave empty for no lot limit.");
            }, icon: "bi-calendar-range")
            .Section("dev_usage", "Usage", s =>
            {
                s.Number("quantity_used", "Quantity used").Min(0);
                s.Number("lots_used", "Lots used").Min(0).Decimals(0);
                s.TextArea("usage_notes", "Usage log (lot, quantity, date)");
            }, icon: "bi-box-seam")
            .Section("dev_verification", "Verification", s =>
            {
                s.TextArea("verification_notes", "Verification that the deviation was applied as approved");
                s.User("verified_by", "Verified by");
                s.Date("verified_on", "Verified on");
            }, icon: "bi-patch-check")
            .Section("dev_closure", "Closure", s =>
            {
                s.Keyword("closure_reason", "Closure reason", "deviation_closure").ReadOnly();
                s.Date("closed_on", "Closed on").ReadOnly();
                s.TextArea("closure_notes", "Closure notes");
            }, icon: "bi-flag")
            .Workflow("deviation", "Planned deviation", w =>
            {
                w.Start("draft", "Draft").Purpose("Describe the deviation, the part and defect it covers, the justification and the validity limits.")
                    .DueDays(5).Edit("dev_info", "dev_risk", "dev_limits").Hide("dev_usage", "dev_verification", "dev_closure");
                w.Phase("review", "Review").Purpose("Technical review of the justification and the risk assessment.")
                    .AssignField("reviewer").DueDays(3).Edit("dev_risk", "dev_limits").ReadOnlyRest().Hide("dev_usage", "dev_verification", "dev_closure");
                w.Phase("initial_approval", "Initial Approval").Purpose("Quality approves the deviation in principle.")
                    .AssignGroup("qa-approvers").DueDays(3).ReadOnlyRest().Hide("dev_usage", "dev_verification", "dev_closure");
                w.Phase("final_approval", "Final Approval").Purpose("A second quality approver activates the deviation. Only one deviation can be active per part number and defect code.")
                    .AssignGroup("qa-approvers").DueDays(3).ReadOnlyRest().Hide("dev_usage", "dev_verification", "dev_closure");
                w.Phase("active", "Active").Purpose("The deviation is in force. Record usage and verification; it closes automatically at its limits.")
                    .AssignField("owner").DueFrom("valid_to").Edit("dev_usage", "dev_verification").ReadOnlyRest().Hide("dev_closure");
                w.Completed("closed", "Closed");
                w.Completed("expired", "Expired");
                w.Voided();

                w.Forward("draft", "review", label: "Submit for review");
                w.Void("draft");
                w.Forward("review", "initial_approval", label: "Reviewed: send for approval");
                w.Back("review", "draft");
                w.Forward("initial_approval", "final_approval", label: "Approve", sign: "Approved");
                w.Back("initial_approval", "review");
                w.Forward("final_approval", "active", label: "Approve and activate", sign: "Approved");
                w.Back("final_approval", "initial_approval");
                w.Void("final_approval", sign: "Voided");
                w.Forward("active", "closed", label: "Close deviation", sign: "Closed", comment: true);
            }));

        app.View("mine", "My deviations", v => v.Category("1. My work").Forms("deviation").Mine()
            .Columns("number", "title", "phase", "due", "part_number", "defect_code").Sort("due", false).Default());
        app.View("active", "Active deviations", v => v.Category("2. Deviations").Forms("deviation").Open().Phases("active")
            .Columns("number", "title", "part_number", "defect_code", "valid_to", "max_quantity", "quantity_used", "owner").Sort("valid_to", false));
        app.View("pending", "Awaiting approval", v => v.Category("2. Deviations").Forms("deviation").Open().Phases("review", "initial_approval", "final_approval")
            .Columns("number", "title", "phase", "due", "part_number", "risk_level", "assignees"));
        app.View("open", "All open deviations", v => v.Category("2. Deviations").Forms("deviation").Open()
            .Columns("number", "title", "phase", "due", "part_number", "defect_code", "owner"));
        app.View("by_part", "Open by part number", v => v.Category("2. Deviations").Forms("deviation").Open()
            .Columns("part_number", "number", "title", "phase", "defect_code", "valid_to").Sort("part_number", false));
        app.View("expired", "Expired deviations", v => v.Category("3. Closed").Forms("deviation").Phases("expired")
            .Columns("number", "title", "part_number", "closure_reason", "closed_on", "owner").Sort("closed_on"));
        app.View("closed", "Closed and expired", v => v.Category("3. Closed").Forms("deviation").Completed()
            .Columns("number", "title", "phase", "part_number", "closure_reason", "modified").Sort("modified"));
        app.View("voided", "Voided deviations", v => v.Category("3. Closed").Forms("deviation").Voided().Columns("number", "title", "owner", "modified"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        // 1. Active deviation used by NCR demo data (Use As Is on cosmetic scratches).
        var dev1 = await d.CreateAsync("deviation", new()
        {
            ["subject"] = "Accept light cosmetic scratches on manifold 40-1180 (non-patient-contact face)",
            ["owner"] = d.User("cwu"), ["reviewer"] = d.User("akim"), ["deviation_type"] = "product",
            ["part_number"] = "40-1180", ["part_description"] = "3-port manifold, PC", ["defect_code"] = "cosmetic",
            ["lot_numbers"] = "M-2291, M-2294",
            ["scope"] = "Scratches up to 3 mm long on the mounting face (zone C) only. Luer ports and fluid path must remain defect-free.",
            ["requirement"] = "Drawing 40-1180 rev D note 4: no visible scratches at arm's length",
            ["justification"] = "Zone C is not patient-contacting and is covered by the pump housing. Replacement stock is 6 weeks out; rejecting would stop VX-200 final assembly.",
            ["risk_level"] = "low", ["fit_function_impact"] = false, ["patient_safety_impact"] = false,
            ["risk_summary"] = "No effect on sealing or flow (verified on 10 samples, leak test 15 psi). Visual inspection of fluid path retained at 100%.",
            ["valid_from"] = DemoContext.Days(-12), ["valid_to"] = DemoContext.Days(48), ["max_quantity"] = 400m, ["max_lots"] = 2m,
        }, "cwu", daysAgo: 16);
        await d.RouteAsync(dev1.Id, "review");
        await d.RouteAsync(dev1.Id, "initial_approval");
        await d.RouteAsync(dev1.Id, "final_approval", asUser: "sortiz", comment: "Low risk; zone C only.");
        await d.RouteAsync(dev1.Id, "active", asUser: "dokafor");
        await d.UpdateAsync(dev1.Id, new() { ["quantity_used"] = 120m, ["lots_used"] = 1m, ["usage_notes"] = "M-2291: 120 pcs used in VX-200 build WO-55102." }, "cwu");

        // 2. Active deviation close to its end date.
        var dev2 = await d.CreateAsync("deviation", new()
        {
            ["subject"] = "Alternate carton supplier for ST-40 tubing sets during supplier qualification",
            ["owner"] = d.User("lgarcia"), ["reviewer"] = d.User("cwu"), ["deviation_type"] = "material",
            ["part_number"] = "PK-ST40-CTN", ["part_description"] = "ST-40 shipping carton", ["defect_code"] = "material",
            ["scope"] = "Use cartons from Midwest Corrugated (not yet on the approved supplier list) for ST-40 shipments.",
            ["requirement"] = "SOP-PUR-004: components from approved suppliers only",
            ["justification"] = "The approved carton supplier had a plant fire. Midwest Corrugated passed ISTA 2A testing; qualification audit is scheduled.",
            ["risk_level"] = "medium", ["fit_function_impact"] = false, ["patient_safety_impact"] = false,
            ["risk_summary"] = "Secondary packaging only; sterile barrier unaffected. ISTA 2A drop and vibration tests passed.",
            ["valid_from"] = DemoContext.Days(-27), ["valid_to"] = DemoContext.Days(3), ["max_lots"] = 12m,
        }, "lgarcia", daysAgo: 30);
        await d.RouteAsync(dev2.Id, "review");
        await d.RouteAsync(dev2.Id, "initial_approval");
        await d.RouteAsync(dev2.Id, "final_approval", asUser: "dokafor");
        await d.RouteAsync(dev2.Id, "active", asUser: "sortiz");
        await d.UpdateAsync(dev2.Id, new() { ["lots_used"] = 9m, ["usage_notes"] = "Carton lots MC-0901 to MC-0909 received and used." }, "lgarcia");

        // 3. Awaiting initial approval.
        var dev3 = await d.CreateAsync("deviation", new()
        {
            ["subject"] = "Extended cure time for UV adhesive on catheter hub bonding",
            ["owner"] = d.User("akim"), ["reviewer"] = d.User("jlee"), ["deviation_type"] = "process",
            ["part_number"] = "CH-1020", ["part_description"] = "Catheter hub assembly", ["defect_code"] = "functional",
            ["scope"] = "Increase UV cure from 8 s to 12 s on bonding station BS-2 while the lamp is replaced.",
            ["requirement"] = "WI-ASM-031 step 6: cure 8 s at 100 mW/cm²",
            ["justification"] = "Lamp output dropped to 70 mW/cm²; 12 s gives the same dose. Pull tests on 30 parts met the 15 N minimum.",
            ["risk_level"] = "low", ["fit_function_impact"] = false, ["patient_safety_impact"] = false,
            ["risk_summary"] = "Equivalent UV dose; pull strength 19.2 N mean (n=30, min 17.1 N).",
            ["valid_from"] = DemoContext.Today, ["valid_to"] = DemoContext.Days(21), ["max_quantity"] = 5000m,
        }, "akim", daysAgo: 3);
        await d.RouteAsync(dev3.Id, "review");
        await d.RouteAsync(dev3.Id, "initial_approval", assignees: ["sortiz"]);

        // 4. Expired deviation (closed by the scheduler at its end date).
        var dev4 = await d.CreateAsync("deviation", new()
        {
            ["subject"] = "Manual torque check while torque analyzer TA-02 is out for calibration",
            ["owner"] = d.User("ebaker"), ["reviewer"] = d.User("akim"), ["deviation_type"] = "equipment",
            ["part_number"] = "VX-200-HSG", ["part_description"] = "VX-200 housing assembly", ["defect_code"] = "documentation",
            ["scope"] = "Use calibrated click wrench CW-11 for the hourly torque check instead of analyzer TA-02.",
            ["requirement"] = "WI-ASM-014 step 9: torque check with analyzer TA-02",
            ["justification"] = "TA-02 is at the external calibration lab for 10 days.",
            ["risk_level"] = "low", ["fit_function_impact"] = false, ["patient_safety_impact"] = false,
            ["risk_summary"] = "CW-11 accuracy ±2% vs ±1% for TA-02; acceptable against the 2.4–2.8 N·m window.",
            ["valid_from"] = DemoContext.Days(-40), ["valid_to"] = DemoContext.Days(2),
        }, "ebaker", daysAgo: 42);
        await d.RouteAsync(dev4.Id, "review");
        await d.RouteAsync(dev4.Id, "initial_approval");
        await d.RouteAsync(dev4.Id, "final_approval", asUser: "sortiz");
        await d.RouteAsync(dev4.Id, "active", asUser: "dokafor");
        await d.UpdateAsync(dev4.Id, new() { ["valid_to"] = DemoContext.Days(-30), ["verification_notes"] = "Hourly checks recorded on the torque log; all within limits.", ["verified_by"] = d.User("jlee"), ["verified_on"] = DemoContext.Days(-31) }, "ebaker");
        d.Services.GetRequiredService<CurrentUser>().UseSystem("Planned deviation expiry");
        await DeviationExpiry.ExpireAsync(d.Services, dev4.Id, "end_date");

        // 5. Draft.
        await d.CreateAsync("deviation", new()
        {
            ["subject"] = "Use sterile tubing lot 24-131 with labels printed on backup printer",
            ["owner"] = d.User("lgarcia"), ["deviation_type"] = "documentation", ["part_number"] = "ST-40", ["defect_code"] = "labeling",
            ["valid_from"] = DemoContext.Today, ["valid_to"] = DemoContext.Days(14),
        }, "lgarcia", daysAgo: 1);
    }
}

/// <summary>Shared defect codes (NCRs and deviations).</summary>
public static class DefectCodes
{
    public static void Keywords(AppBuilder app) => app.Keywords("ncr_defect_code", "Defect code", k => k
        .Add("dimensional", "Dimensional out of tolerance").Add("cosmetic", "Cosmetic (scratch, blemish)").Add("burr", "Burr / sharp edge")
        .Add("contamination", "Contamination / particulate").Add("labeling", "Labeling error").Add("functional", "Functional failure")
        .Add("material", "Wrong or unqualified material").Add("documentation", "Missing or incorrect documentation").Add("packaging", "Packaging / seal failure"));
}

public sealed record ActiveDeviationInfo(Guid Id, string Number, string Title, string PartNumber, string? DefectKey, string? DefectLabel, DateOnly? ValidTo);

/// <summary>
/// INV-119: the derived "active deviation" view. A part shows the warning if and only if an Active deviation names it.
/// Other modules (materials, items, inspections) can use this to show "This item has an active deviation" (DEV-003, SAF-042).
/// </summary>
[ScopedService]
public sealed class ActiveDeviations(VellumDbContext db, ConfigCache cfg)
{
    public async Task<List<ActiveDeviationInfo>> ForPartAsync(string? partNumber, string? defectKey = null, Guid? excludeId = null)
    {
        if (string.IsNullOrWhiteSpace(partNumber) || !cfg.Current.FormsByKey.TryGetValue("deviation", out var form)) return [];
        var activeIds = form.Workflows.SelectMany(w => w.Phases).Where(p => p.Key == "active").Select(p => p.Id).ToList();
        var part = partNumber.Trim();
        var q = db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseId != null && activeIds.Contains(r.PhaseId.Value) &&
            db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "part_number" && v.Text == part));
        if (excludeId is { } ex) q = q.Where(r => r.Id != ex);
        if (defectKey != null)
        {
            var kid = cfg.Current.Keyword("ncr_defect_code", defectKey)?.Id ?? -1;
            q = q.Where(r => db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "defect_code" && v.RefId == kid));
        }
        var recs = await q.ToListAsync();
        var ids = recs.Select(r => r.Id).ToList();
        var vals = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && (v.FieldKey == "defect_code" || v.FieldKey == "valid_to")).ToListAsync();
        return recs.Select(r =>
        {
            var kw = cfg.Current.Keyword(vals.FirstOrDefault(v => v.RecordId == r.Id && v.FieldKey == "defect_code")?.RefId);
            var to = vals.FirstOrDefault(v => v.RecordId == r.Id && v.FieldKey == "valid_to")?.Date;
            return new ActiveDeviationInfo(r.Id, r.Number, r.Title, part, kw?.Key, kw?.Label, to is { } t ? DateOnly.FromDateTime(t) : null);
        }).ToList();
    }
}

public sealed class DeviationBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["deviation"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        if (m.Date("valid_from") is { } from && m.Date("valid_to") is { } to && to < from)
            ctx.Error("valid_to", "The end date can't be before the start date.");
        if (m.Number("max_quantity") is { } maxQ && m.Number("quantity_used") is { } usedQ && usedQ > maxQ)
            ctx.Error("quantity_used", $"This deviation allows at most {maxQ:0.##}. Raise a new deviation for more.");
        if (m.Number("max_lots") is { } maxL && m.Number("lots_used") is { } usedL && usedL > maxL)
            ctx.Error("lots_used", $"This deviation covers at most {maxL:0} lots.");
        if (m.Text("part_number") is { } p && p != p.Trim()) ctx.Set("part_number", p.Trim());
        return Task.CompletedTask;
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "active") return;
        var s = ctx.Snapshot;
        if (s.Date("valid_to") is { } to && to < BusinessCalendar.Today)
            ctx.Error("The end date has passed. Change the validity limits before activating this deviation.");

        // SAF-044: final approval by someone other than the initial approver.
        var initialSigner = await ctx.Db.Signatures.AsNoTracking()
            .Where(x => x.RecordId == s.Record.Id && !x.Superseded && x.PhaseKey == "initial_approval").Select(x => (int?)x.SignerId).FirstOrDefaultAsync();
        if (initialSigner == ctx.Me.Id)
            ctx.Error("Final approval must come from a different approver than initial approval (segregation of duties).");

        // INV-118: one active deviation per part number + defect code. The application lock serializes concurrent activations
        // of the same key until this transaction commits, so two approvers can't both activate one.
        var part = s.Text("part_number");
        var defect = s.KeywordKey("defect_code");
        if (part == null || defect == null) { ctx.Error("A deviation needs a part number and a defect code before it can be activated."); return; }
        if (ctx.Db.Database.CurrentTransaction != null)
            await ctx.Db.Database.ExecuteSqlRawAsync(
                "DECLARE @r int; EXEC @r = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;",
                $"vellum-deviation:{part.ToUpperInvariant()}|{defect}");
        var clash = await ctx.Get<ActiveDeviations>().ForPartAsync(part, defect, s.Record.Id);
        if (clash.Count > 0)
            ctx.Error($"{clash[0].Number} is already active for part {part} and defect \"{clash[0].DefectLabel}\". Only one deviation can be active per part number and defect code (INV-118).");
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        var notify = ctx.Get<NotificationService>();
        if (ctx.To.Key == "active" && s.UserId("owner") is { } owner)
            notify.Notify(owner, NotificationKind.Info, $"Deviation {s.Record.Number} is active",
                $"Part {s.Text("part_number")} may be used under {s.Record.Number} until {s.Date("valid_to"):dd MMM yyyy}.", s.Record.Id);
        if (ctx.To.Key == "closed")
            await ctx.Records.UpdateAsync(s.Record.Id, new() { ["closure_reason"] = "closed_manual", ["closed_on"] = BusinessCalendar.Today });
        if (ctx.To.Key == "expired" && s.UserId("owner") is { } o)
        {
            // DEV-003: the active-deviation warning goes away with the phase change; tell the owner.
            notify.Notify(o, NotificationKind.Info, $"Deviation {s.Record.Number} has expired",
                $"{s.Record.Title}: {s.Keyword("closure_reason")?.Label ?? "validity limit reached"}. Part {s.Text("part_number")} no longer has an active deviation.", s.Record.Id);
        }
    }

    public override Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var today = BusinessCalendar.Today;
        var model = new DeviationStatusModel(
            s.Record.Number, s.PhaseKey, s.Phase?.Name, s.Text("part_number"), s.Keyword("defect_code")?.Label,
            s.Date("valid_from"), s.Date("valid_to"), s.Number("max_quantity"), s.Number("quantity_used"), s.Number("max_lots"), s.Number("lots_used"),
            s.Keyword("closure_reason")?.Label, s.Date("closed_on"), today);
        return Task.FromResult<IEnumerable<RecordPanel>>(
        [
            new RecordPanel { Key = "dev-status", Title = "Deviation status", Placement = "top", Partial = "~/Modules/Quality/Views/_DeviationStatus.cshtml", Model = model },
        ]);
    }
}

public sealed record DeviationStatusModel(string Number, string PhaseKey, string? PhaseName, string? PartNumber, string? Defect,
    DateOnly? From, DateOnly? To, decimal? MaxQty, decimal? UsedQty, decimal? MaxLots, decimal? UsedLots, string? ClosureReason, DateOnly? ClosedOn, DateOnly Today)
{
    public int? DaysLeft => To is { } t ? t.DayNumber - Today.DayNumber : null;
    public int? QtyPercent => MaxQty is > 0 ? (int)Math.Min(100, Math.Round((UsedQty ?? 0) / MaxQty.Value * 100)) : null;
    public int? LotPercent => MaxLots is > 0 ? (int)Math.Min(100, Math.Round((UsedLots ?? 0) / MaxLots.Value * 100)) : null;
}

/// <summary>DEV-002 automatic closure on expiry (end date reached, quantity or lots used up).</summary>
public static class DeviationExpiry
{
    /// <summary>Why an active deviation should close now, or null.</summary>
    public static string? ReasonFor(RecordSnapshot s, DateOnly today)
    {
        if (s.Date("valid_to") is { } to && to < today) return "end_date";
        if (s.Number("max_quantity") is { } mq && mq > 0 && (s.Number("quantity_used") ?? 0) >= mq) return "quantity_used";
        if (s.Number("max_lots") is { } ml && ml > 0 && (s.Number("lots_used") ?? 0) >= ml) return "lots_used";
        return null;
    }

    /// <summary>Record the reason and route to Expired as the system, in one transaction. Never blocked by open fields (PLT-651, audited).</summary>
    public static async Task ExpireAsync(IServiceProvider sp, Guid id, string reason)
    {
        var db = sp.GetRequiredService<VellumDbContext>();
        var records = sp.GetRequiredService<RecordService>();
        var workflow = sp.GetRequiredService<WorkflowEngine>();
        var cfg = sp.GetRequiredService<ConfigCache>().Current;
        var label = cfg.Keyword("deviation_closure", reason)?.Label ?? reason;
        db.ChangeTracker.Clear();
        await Tx.RunAsync(db, async () =>
        {
            var upd = await records.UpdateAsync(id, new() { ["closure_reason"] = reason, ["closed_on"] = BusinessCalendar.Today }, $"Automatic expiry: {label}");
            if (!upd.Ok) throw new WorkflowException(string.Join("; ", upd.Errors.Select(e => e.Message)));
            var res = await workflow.SystemRouteAsync(id, "expired", $"Validity limit reached: {label} (DEV-002)", skipValidation: true);
            if (!res.Ok) throw new WorkflowException(string.Join("; ", res.Errors.Select(e => e.Message)));
            return true;
        });
    }
}

/// <summary>DEV-002/DEV-003: closes active deviations at their limits and notifies the owner. Catches up after downtime (it looks at state, not ticks).</summary>
public sealed class DeviationExpiryJob(VellumDbContext db, ConfigCache cfg, RecordService records, IServiceProvider services) : IScheduledJob
{
    public string Key => "quality-deviation-expiry";
    public string Name => "Planned deviation expiry";
    public string Description => "Closes active planned deviations when their end date passes or their quantity or lot limit is used up, and notifies the owner (DEV-002, DEV-003).";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("deviation", out var form)) return new JobResult(0, "Planned deviations aren't installed.");
        var activeIds = form.Workflows.SelectMany(w => w.Phases).Where(p => p.Key == "active").Select(p => p.Id).ToList();
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseId != null && activeIds.Contains(r.PhaseId.Value))
            .Select(r => r.Id).ToListAsync(ct);
        var today = BusinessCalendar.Today;
        var closed = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(id);
            if (DeviationExpiry.ReasonFor(snap, today) is not { } reason) continue;
            try
            {
                await DeviationExpiry.ExpireAsync(services, id, reason);
                ctx.Log($"{snap.Record.Number}: expired ({reason}).");
                closed++;
            }
            catch (Exception ex)
            {
                ctx.Log($"{snap.Record.Number}: couldn't expire: {ex.Message}");
            }
        }
        return new JobResult(closed, $"Checked {ids.Count} active deviation(s); {closed} expired.");
    }
}
