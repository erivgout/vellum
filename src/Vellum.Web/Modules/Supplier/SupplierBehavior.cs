using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Modules.Supplier;

/// <summary>
/// Supplier profiles: signed approval status (SUP-001), certificate states (SUP-003), approved-material-only supplied
/// materials (INV-089) and scorecard weights totalling 100% (INV-094).
/// </summary>
public sealed class SupplierBehavior : RecordBehavior
{
    public const string Gate = "supplier-status";
    public const string ApproverGroup = "qa-approvers";
    public const int ExpiryWarningDays = 60;

    public override IReadOnlyCollection<string> Forms => ["supplier"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        var gate = ctx.Services.GetRequiredService<ProtectedFieldGate>();

        // SUP-001: the approval status only changes through the signed actions.
        if (ctx.IsNew)
        {
            if (!gate.IsOpen(Gate)) ctx.Set("approval_status", "prospective");
        }
        else if (!gate.IsOpen(Gate) && main.Ref("approval_status") != ctx.Current!.Data.Main.Ref("approval_status"))
            ctx.Error("approval_status", "The approval status can only be changed with the Approve, Disqualify or Deactivate actions.");

        // INV-094: scorecard weights total 100%.
        decimal W(string k) => main.Number(k) ?? 0m;
        if (new[] { "w_quality", "w_delivery", "w_response", "w_audit" }.Any(main.Has) &&
            Scoring.WeightError(W("w_quality"), W("w_delivery"), W("w_response"), W("w_audit")) is { } we)
            ctx.Error("w_quality", we);

        foreach (var row in ctx.Proposed.RowsOf("contacts"))
            if (row.Values.Text("email") is { } email && (!email.Contains('@') || email.Contains(' ')))
                ctx.Error("email", $"Contacts: \"{email}\" isn't a valid email address.", row.IsNew ? null : row.Id);

        ApplyCertificateStates(ctx, BusinessCalendar.Today);
        await CheckSuppliedMaterialsAsync(ctx);
    }

    /// <summary>SUP-003: per standard the latest certificate counts; the supplier shows the worst state.</summary>
    public static void ApplyCertificateStates(SavingContext ctx, DateOnly today)
    {
        var rows = ctx.Proposed.RowsOf("certifications");
        var latest = rows.Where(r => r.Values.Date("expires_on") != null)
            .GroupBy(r => r.Values.Ref("standard"))
            .ToDictionary(g => g.Key ?? 0, g => g.OrderByDescending(r => r.Values.Date("expires_on")).First().Id);
        var worst = rows.Count == 0 ? "none" : "valid";
        DateOnly? next = null;
        foreach (var row in rows)
        {
            var exp = row.Values.Date("expires_on");
            string state;
            if (exp == null) state = "valid";
            else if (latest.GetValueOrDefault(row.Values.Ref("standard") ?? 0) != row.Id) state = "superseded";
            else
            {
                state = CertState(exp.Value, today);
                if (next == null || exp < next) next = exp;
                if (Rank(state) > Rank(worst)) worst = state;
            }
            BehaviorHelpers.SetRow(ctx.Config, ctx.Form, row, "cert_state", state);
        }
        ctx.Set("cert_status", worst);
        ctx.Set("next_cert_expiry", next);
    }

    public static string CertState(DateOnly expires, DateOnly today) =>
        expires < today ? "expired" : expires <= today.AddDays(ExpiryWarningDays) ? "expiring" : "valid";

    private static int Rank(string s) => s switch { "expired" => 3, "expiring" => 2, "valid" => 1, _ => 0 };

    /// <summary>INV-089: only approved, active material profiles can be listed as supplied materials.</summary>
    private static async Task CheckSuppliedMaterialsAsync(SavingContext ctx)
    {
        var rows = ctx.Proposed.RowsOf("supplied_materials");
        if (rows.Count == 0) return;
        var before = ctx.Current?.Data.RowsOf("supplied_materials").ToDictionary(r => r.Id) ?? [];
        var ids = rows.Select(r => r.Values.Link("material")).OfType<Guid>().ToList();
        var reader = ctx.Services.GetRequiredService<RecordReader>();
        var materials = (await reader.LoadManyAsync(ids, rows: false)).ToDictionary(m => m.Id);
        var seen = new HashSet<Guid>();
        foreach (var row in rows)
        {
            if (row.Values.Link("material") is not { } mid) continue;
            var rowId = row.IsNew ? (Guid?)null : row.Id;
            if (!seen.Add(mid)) { ctx.Error("material", "Supplied materials: the same material is listed twice.", rowId); continue; }
            var changed = !before.TryGetValue(row.Id, out var old) || old.Values.Link("material") != mid;
            if (!changed) continue;
            if (!materials.TryGetValue(mid, out var m) || m.Form.Key != "material")
                ctx.Error("material", "Supplied materials: choose a material profile.", rowId);
            else if (m.Record.PhaseType != PhaseType.Completed || m.PhaseKey != "approved")
                ctx.Error("material", $"Supplied materials: {m.Record.Number} {m.Text("part_number")} isn't approved yet ({m.Phase?.Name ?? "no phase"}). Only approved materials can be listed (INV-089).", rowId);
            else if (m.Bool("inactive") == true)
                ctx.Error("material", $"Supplied materials: {m.Text("part_number")} is inactive.", rowId);
        }
    }

    // ------------------------------------------------------------------ actions

    private static bool IsApprover(RecordContext ctx) =>
        !ctx.Me.IsSystem && ctx.Directory.IsMember(ctx.Me.Id, ApproverGroup); // Quality approval authority only (segregation from the supplier engineer)

    private static bool IsAuthor(RecordContext ctx) => ctx.Access.RoleFor(ctx.Record.AppId, "supplier") >= AppRole.Author;

    public override Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var list = new List<RecordAction>();
        if (ctx.Me.IsSystem || (!IsAuthor(ctx) && !IsApprover(ctx))) return Task.FromResult<IEnumerable<RecordAction>>(list);
        var s = ctx.Snapshot;
        var status = s.KeywordKey("approval_status");
        var approver = IsApprover(ctx);
        const string notApprover = "Only Quality approvers can approve or disqualify suppliers.";
        var requal = DefaultRequalification(s.KeywordKey("risk_class"), BusinessCalendar.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (status is "prospective" or "conditional")
            list.Add(new RecordAction
            {
                Key = "approve", Label = "Approve supplier", Icon = "bi-patch-check", Group = "Approval", Variant = "success",
                Description = "Add the supplier to the approved supplier list after reviewing the qualification evidence.",
                SignatureMeaning = "Approved supplier", DisabledReason = approver ? null : notApprover,
                Inputs =
                [
                    new ActionField("scope", "Approved scope", "textarea", true, "What the supplier is approved to supply.", Default: s.Text("scope")),
                    new ActionField("requalify_on", "Requalify by", "date", true, "Defaults from the risk class.", Default: requal),
                ],
            });
        if (status == "prospective")
            list.Add(new RecordAction
            {
                Key = "approve_conditional", Label = "Approve conditionally", Icon = "bi-patch-exclamation", Group = "Approval", Variant = "warning",
                Description = "Approve with restrictions (for example 100% inspection or an open SCAR). Every lot is inspected while conditional.",
                SignatureMeaning = "Conditionally approved supplier", DisabledReason = approver ? null : notApprover,
                Inputs =
                [
                    new ActionField("scope", "Approved scope", "textarea", true, Default: s.Text("scope")),
                    new ActionField("conditions", "Conditions of approval", "textarea", true, "What must happen before full approval."),
                    new ActionField("requalify_on", "Requalify by", "date", true, Default: BusinessCalendar.Today.AddMonths(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ],
            });
        if (status is "approved" or "conditional")
            list.Add(new RecordAction
            {
                Key = "disqualify", Label = "Disqualify supplier", Icon = "bi-x-octagon", Group = "Approval", Variant = "danger",
                Description = "Remove the supplier from the approved supplier list. Receiving from it is blocked.",
                SignatureMeaning = "Disqualified supplier", RequiresComment = true, CommentLabel = "Reason for disqualification",
                DisabledReason = approver ? null : notApprover,
            });
        if (status is "approved" or "conditional" or "prospective")
            list.Add(new RecordAction
            {
                Key = "deactivate", Label = "Set inactive", Icon = "bi-pause-circle", Group = "Approval", Variant = "secondary",
                Description = "The supplier no longer supplies us. It's hidden from receiving and left out of rating (SUP-002).",
                RequiresComment = true, CommentLabel = "Reason", DisabledReason = IsAuthor(ctx) ? null : "You need author access to Supplier Quality.",
            });
        if (status is "disqualified" or "inactive")
            list.Add(new RecordAction
            {
                Key = "requalify", Label = "Reopen qualification", Icon = "bi-arrow-repeat", Group = "Approval",
                Description = "Move the supplier back to Prospective so it can be requalified and approved again.",
                RequiresComment = true, CommentLabel = "Why requalify", DisabledReason = IsAuthor(ctx) ? null : "You need author access to Supplier Quality.",
            });
        if (ctx.Config.FormsByKey.TryGetValue("scorecard", out var scorecard) && ctx.Access.CanCreate(scorecard) && status is "approved" or "conditional")
        {
            var (qs, qe) = LastQuarter(BusinessCalendar.Today);
            list.Add(new RecordAction
            {
                Key = "create_scorecard", Label = "New scorecard", Icon = "bi-speedometer2", Group = "Create",
                Description = "Start a draft scorecard for this supplier. It uses the supplier's criteria weights.",
                Inputs =
                [
                    new ActionField("period_start", "Period start", "date", true, Default: qs.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    new ActionField("period_end", "Period end", "date", true, Default: qe.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ],
            });
        }
        return Task.FromResult<IEnumerable<RecordAction>>(list);
    }

    public static (DateOnly Start, DateOnly End) LastQuarter(DateOnly today)
    {
        var currentStart = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return (currentStart.AddMonths(-3), currentStart.AddDays(-1));
    }

    public static DateOnly DefaultRequalification(string? riskClass, DateOnly from) => riskClass switch
    {
        "critical" => from.AddYears(1),
        "high" => from.AddYears(2),
        _ => from.AddYears(3),
    };

    /// <summary>What has to be in place before a supplier can be approved.</summary>
    public static List<string> ApprovalBlockers(RecordSnapshot s, bool conditional)
    {
        var errors = new List<string>();
        if (s.Rows("contacts").Count == 0) errors.Add("Add at least one supplier contact.");
        if (!s.Data.Main.Has("qual_methods") || s.Date("qual_date") == null)
            errors.Add("Record the qualification methods and date on the Qualification tab.");
        if (string.IsNullOrWhiteSpace(s.Text("qual_summary"))) errors.Add("Write the qualification summary.");
        if (s.Rows("supplied_materials").Count == 0) errors.Add("List at least one approved material the supplier supplies.");
        var cert = s.KeywordKey("cert_status");
        if (cert == "expired") errors.Add("A certificate has expired. Record the renewed certificate before approving (SUP-003).");
        if (!conditional && s.KeywordKey("risk_class") is "high" or "critical")
        {
            if (s.Bool("quality_agreement") != true) errors.Add("High and critical suppliers need a quality agreement before full approval. Approve conditionally instead, or record the agreement.");
            if (cert is not ("valid" or "expiring")) errors.Add("High and critical suppliers need a valid quality system certificate before full approval.");
        }
        return errors;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        var s = ctx.Snapshot;
        var status = s.KeywordKey("approval_status");
        var name = s.Text("name") ?? s.Record.Title;
        var today = BusinessCalendar.Today;
        switch (actionKey)
        {
            case "approve" or "approve_conditional":
            {
                if (!IsApprover(ctx)) return ActionOutcome.Fail("Only Quality approvers can approve suppliers.");
                var conditional = actionKey == "approve_conditional";
                if (conditional ? status != "prospective" : status is not ("prospective" or "conditional"))
                    return ActionOutcome.Fail("The supplier can't be approved from its current status.");
                var blockers = ApprovalBlockers(s, conditional);
                if (blockers.Count > 0) return ActionOutcome.Fail([.. blockers]);
                if (!DateOnly.TryParse(input.Get("requalify_on"), CultureInfo.InvariantCulture, out var requal) || requal <= today)
                    return ActionOutcome.Fail("Choose a requalification date in the future.");
                var scope = input.Get("scope");
                if (string.IsNullOrWhiteSpace(scope)) return ActionOutcome.Fail("Describe the approved scope.");
                var conditions = conditional ? input.Get("conditions") : null;
                if (conditional && string.IsNullOrWhiteSpace(conditions)) return ActionOutcome.Fail("State the conditions of approval.");
                return await SignedChange.ApplyAsync(ctx, new()
                {
                    ["approval_status"] = conditional ? "conditional" : "approved", ["approved_on"] = today, ["requalify_on"] = requal,
                    ["scope"] = scope, ["conditions"] = conditions,
                }, conditional ? "Conditionally approved supplier" : "Approved supplier", conditional ? "Approve conditionally" : "Approve supplier",
                    input, conditional ? $"Conditionally approved; requalify by {requal:dd MMM yyyy}. Conditions: {conditions}" : $"Approved for the approved supplier list; requalify by {requal:dd MMM yyyy}",
                    conditional ? $"{name} is conditionally approved." : $"{name} is on the approved supplier list.", Gate);
            }
            case "disqualify":
                if (!IsApprover(ctx)) return ActionOutcome.Fail("Only Quality approvers can disqualify suppliers.");
                if (status is not ("approved" or "conditional")) return ActionOutcome.Fail("Only approved or conditional suppliers can be disqualified.");
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("Give the reason for disqualification.");
                return await SignedChange.ApplyAsync(ctx, new() { ["approval_status"] = "disqualified" }, "Disqualified supplier", "Disqualify supplier",
                    input, "Disqualified", $"{name} is disqualified. Receiving from it is now blocked.", Gate);
            case "deactivate":
                if (!IsAuthor(ctx)) return ActionOutcome.Fail("You need author access to Supplier Quality.");
                if (status is not ("approved" or "conditional" or "prospective")) return ActionOutcome.Fail("The supplier is already inactive or disqualified.");
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("Give a reason.");
                return await SignedChange.ApplyAsync(ctx, new() { ["approval_status"] = "inactive" }, null, "Set inactive", input, "Set inactive",
                    $"{name} is inactive.", Gate);
            case "requalify":
                if (!IsAuthor(ctx)) return ActionOutcome.Fail("You need author access to Supplier Quality.");
                if (status is not ("disqualified" or "inactive")) return ActionOutcome.Fail("Only disqualified or inactive suppliers can be requalified.");
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("Explain why the supplier is being requalified.");
                return await SignedChange.ApplyAsync(ctx, new() { ["approval_status"] = "prospective", ["approved_on"] = null }, null, "Reopen qualification",
                    input, "Requalification opened", $"{name} is back in qualification.", Gate);
            case "create_scorecard":
            {
                if (!ctx.Config.FormsByKey.TryGetValue("scorecard", out var sf) || !ctx.Access.CanCreate(sf))
                    return ActionOutcome.Fail("You can't create supplier scorecards.");
                if (!DateOnly.TryParse(input.Get("period_start"), CultureInfo.InvariantCulture, out var ps) ||
                    !DateOnly.TryParse(input.Get("period_end"), CultureInfo.InvariantCulture, out var pe))
                    return ActionOutcome.Fail("Enter the period start and end dates.");
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "scorecard",
                    Values = new() { ["supplier"] = s.Record.Id, ["period_start"] = ps, ["period_end"] = pe },
                    CreateComment = $"Started from supplier {s.Record.Number}",
                });
                return res.Ok
                    ? ActionOutcome.Ok($"Scorecard {res.Record!.Number} created. Enter the measures, or calculate them from receiving.", res.Record.Id)
                    : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            }
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    // ------------------------------------------------------------------ panels

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var panels = new List<RecordPanel>();
        var vm = await SupplierPanels.PerformanceAsync(ctx);
        panels.Add(new RecordPanel
        {
            Key = "sup-rating", Title = "Supplier rating", Icon = "bi-speedometer2", Placement = "side", Order = 1,
            Partial = "~/Modules/Supplier/Views/_SupplierRating.cshtml", Model = vm,
        });
        panels.Add(new RecordPanel
        {
            Key = "sup-performance", Title = "Scorecards", Icon = "bi-graph-up", Placement = "tab", Order = 1, Count = vm.Points.Count,
            Partial = "~/Modules/Supplier/Views/_SupplierPerformance.cshtml", Model = vm,
        });
        var lots = await SupplierPanels.ReceivingAsync(ctx, "supplier");
        panels.Add(new RecordPanel
        {
            Key = "sup-receiving", Title = "Receiving", Icon = "bi-box-arrow-in-down", Placement = "tab", Order = 2, Count = lots.Lots.Count,
            Partial = "~/Modules/Supplier/Views/_ReceivingHistory.cshtml", Model = lots,
        });
        var history = await SupplierPanels.StatusHistoryAsync(ctx);
        panels.Add(new RecordPanel
        {
            Key = "sup-approval", Title = "Approval history", Icon = "bi-clock-history", Placement = "tab", Order = 3, Count = history.Entries.Count,
            Partial = "~/Modules/Supplier/Views/_ApprovalHistory.cshtml", Model = history,
        });
        return panels;
    }
}
