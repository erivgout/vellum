using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Modules.Supplier;

namespace Vellum.Modules.Risk;

/// <summary>The calculated state of a risk assessment.</summary>
public sealed record RiskState(int? Severity, int? Likelihood, int? InitialScore, int? ResidualSeverity, int? ResidualLikelihood, int? ResidualScore,
    bool HasMitigation, bool HasLinkedMitigation, int? AcceptedBy, int? AcceptedScore)
{
    public bool ResidualAssessed => ResidualScore != null;
    public int? CurrentScore => ResidualScore ?? InitialScore;
    public RiskBand? Initial => RiskMatrix.BandFor(InitialScore);
    public RiskBand? Residual => RiskMatrix.BandFor(ResidualScore);
    public RiskBand? Current => RiskMatrix.BandFor(CurrentScore);
    public bool AcceptedForCurrent => AcceptedBy != null && AcceptedScore != null && AcceptedScore == CurrentScore;
}

/// <summary>RSK-014 / INV-176 / SAF-045: rules shared by the behavior, the record panels, the heat-map page and the tests.</summary>
public static class RiskRules
{
    public const string Gate = "risk-acceptance";
    public const string AcceptanceMeaning = "Risk accepted";
    public const string AuthorityGroup = "qa-approvers";
    public const int ReviewWarningDays = 30;
    public static readonly string[] AcceptanceFields = ["accepted_by", "accepted_on", "accepted_score", "acceptance_rationale"];

    public static RiskState Evaluate(ValueBag main, IReadOnlyList<RowBag> mitigations, ConfigModel cfg)
    {
        int? V(string key) => RiskMatrix.Value(cfg.Keyword(main.Ref(key))?.Key);
        int? Product(int? a, int? b) => a is { } x && b is { } y ? RiskMatrix.Score(x, y) : null;
        var s = V("severity");
        var l = V("likelihood");
        var rs = V("residual_severity");
        var rl = V("residual_likelihood");
        return new RiskState(s, l, Product(s, l), rs, rl, Product(rs, rl),
            mitigations.Any(r => r.Values.Has("measure")),
            mitigations.Any(r => r.CreatedRecordId != null || r.Values.Link("linked_record") != null),
            main.Ref("accepted_by"), main.Number("accepted_score") is { } a ? (int)a : null);
    }

    public static string? AcceptanceStatus(RiskState s)
    {
        if (s.CurrentScore == null) return null;
        if (s.Current is { Acceptable: false })
            return s.AcceptedForCurrent ? "accepted" : !s.ResidualAssessed && s.HasLinkedMitigation ? "mitigation_linked" : "required";
        return s.Initial is { Acceptable: false } && s.ResidualAssessed ? "mitigated" : "not_required";
    }

    /// <summary>INV-176: why the assessment can't be completed yet (empty = it can).</summary>
    public static List<string> CompletionBlockers(RiskState s, bool acceptanceSigned)
    {
        var errors = new List<string>();
        if (s.CurrentScore == null) { errors.Add("Rate the severity and likelihood before completing the assessment."); return errors; }
        if (s.Current is { Acceptable: false } band && !(s.AcceptedForCurrent && acceptanceSigned))
        {
            if (s.ResidualAssessed)
                errors.Add($"The residual risk is {band.Label} ({s.CurrentScore}), which is unacceptable. It can be completed only after Quality accepts it with an e-signature (Accept risk), or after further mitigation lowers it (INV-176, SAF-045).");
            else if (!s.HasLinkedMitigation)
                errors.Add($"This {band.Label} risk ({s.CurrentScore}) can be completed only with a linked mitigation (an action item, CAPA or change record) or an e-signed risk acceptance (INV-176, SAF-045).");
        }
        if (s.ResidualAssessed && s.ResidualScore < s.InitialScore && !s.HasMitigation)
            errors.Add("The residual risk is lower than the initial risk, but no mitigation is recorded. Add the risk control measures that reduce it.");
        return errors;
    }

    public static string? ReviewState(DateOnly? reviewDate, DateOnly today) => reviewDate is not { } d ? null
        : d < today ? "overdue" : d <= today.AddDays(ReviewWarningDays) ? "due_soon" : "current";

    public static Task<bool> AcceptanceSignedAsync(VellumDbContext db, Guid recordId, int? acceptedBy) =>
        acceptedBy is not { } by ? Task.FromResult(false)
            : db.Signatures.AnyAsync(x => x.RecordId == recordId && !x.Superseded && x.Meaning == AcceptanceMeaning && x.SignerId == by);
}

public sealed class RiskBannerVm
{
    public required RiskState State { get; init; }
    public string? Status { get; init; }
    public string? AcceptedBy { get; init; }
    public DateOnly? AcceptedOn { get; init; }
    public string? Rationale { get; init; }
    public bool Open { get; init; }
}

public sealed class RiskMatrixVm
{
    public required RiskState State { get; init; }
    public required string Title { get; init; }
}

/// <summary>
/// Risk assessments: matrix scores and levels (RSK-009), acceptance gate (RSK-014, INV-176, SAF-045), mitigations to
/// action items, the periodic review status, and the e-signed Accept risk action.
/// </summary>
public sealed class RiskAssessmentBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["risk_assessment"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        var gate = ctx.Services.GetRequiredService<ProtectedFieldGate>();
        if (!gate.IsOpen(RiskRules.Gate))
        {
            foreach (var key in RiskRules.AcceptanceFields)
            {
                if (ctx.IsNew) ctx.Set(key, null);
                else if (main.Canonical(key) != ctx.Current!.Data.Main.Canonical(key))
                    ctx.Error(key, "Risk acceptance can only be recorded with the e-signed Accept risk action.");
            }
        }

        var st = RiskRules.Evaluate(main, ctx.Proposed.RowsOf("mitigations"), ctx.Config);
        // An acceptance holds only for the score it was given at: a changed score clears it (SAF-045).
        if (st.AcceptedBy != null && !st.AcceptedForCurrent && !gate.IsOpen(RiskRules.Gate))
        {
            foreach (var key in RiskRules.AcceptanceFields) ctx.Set(key, null);
            st = st with { AcceptedBy = null, AcceptedScore = null };
        }
        ctx.Set("initial_score", st.InitialScore);
        ctx.Set("initial_level", st.Initial?.Key);
        ctx.Set("residual_score", st.ResidualScore);
        ctx.Set("residual_level", st.Residual?.Key);
        ctx.Set("current_score", st.CurrentScore);
        ctx.Set("current_level", st.Current?.Key);
        ctx.Set("acceptance_status", RiskRules.AcceptanceStatus(st));
        ctx.Set("review_state", ctx.Current?.Record.PhaseType == PhaseType.Voided ? null : RiskRules.ReviewState(main.Date("review_date"), BusinessCalendar.Today));

        if (st.ResidualScore > st.InitialScore)
            ctx.Error("residual_severity", "The residual risk is higher than the initial risk. Re-check the ratings, or assess the new hazard the control introduces separately.");
        if (main.Has("residual_severity") != main.Has("residual_likelihood"))
            ctx.Error("residual_likelihood", "Rate both the residual severity and the residual likelihood.");
        return Task.CompletedTask;
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        var st = RiskRules.Evaluate(s.Data.Main, s.Rows("mitigations"), s.Config);
        switch (ctx.To.Key)
        {
            case "mitigation" when !st.HasMitigation:
                ctx.Error("Add at least one mitigation before starting mitigation.");
                break;
            case "completed":
                var signed = await RiskRules.AcceptanceSignedAsync(ctx.Db, s.Record.Id, st.AcceptedBy);
                foreach (var e in RiskRules.CompletionBlockers(st, signed)) ctx.Error(e);
                if (s.Date("review_date") is { } d && d <= BusinessCalendar.Today) ctx.Error("Set the next periodic review date in the future.");
                break;
        }
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.To.Key == "mitigation") await CreateActionItemsAsync(ctx);
        // Routing back supersedes signatures; an acceptance whose signature is gone no longer counts (INV-052, SAF-045).
        if (ctx.Kind == RouteKind.Back && s.UserId("accepted_by") is { } by && !await RiskRules.AcceptanceSignedAsync(ctx.Db, s.Record.Id, by))
        {
            var gate = ctx.Get<ProtectedFieldGate>();
            var proposed = new RecordData();
            foreach (var key in RiskRules.AcceptanceFields) proposed.Main.Set(key, []);
            gate.Open(RiskRules.Gate);
            try
            {
                var res = await ctx.Records.SaveAsync(await ctx.Records.LoadRequiredAsync(s.Record.Id), proposed,
                    new SaveOptions { System = true, Reason = $"Routed back to {ctx.To.Name}: the e-signed risk acceptance was superseded" });
                if (!res.Ok) throw new WorkflowException(string.Join(" ", res.Errors.Select(e => e.Message)));
            }
            finally { gate.Close(); }
        }
    }

    /// <summary>Mitigation rows with an owner become action items when the assessment enters Mitigation (like CAPA-003).</summary>
    private static async Task CreateActionItemsAsync(RoutingContext ctx)
    {
        var calendar = ctx.Get<BusinessCalendar>();
        var snap = await ctx.Records.LoadRequiredAsync(ctx.Record.Id);
        var proposed = new RecordData();
        var updated = proposed.RowsOf("mitigations");
        var created = 0;
        foreach (var row in snap.Rows("mitigations"))
        {
            var clone = row.Clone();
            if (row.CreatedRecordId == null && row.Values.Link("linked_record") == null && row.Values.Text("measure") is { } measure &&
                row.Values.Ref("mit_owner") is { } owner)
            {
                var due = await calendar.AddAsync(BusinessCalendar.Today, (int)(row.Values.Number("days") ?? 20), true);
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "action_item", StartPhaseKey = "open", ParentId = snap.Record.Id, SkipAccessCheck = true, Assignees = [owner], DueDate = due,
                    Values = new()
                    {
                        ["subject"] = measure.Length > 200 ? measure[..200] : measure,
                        ["description"] = $"Risk control measure from {snap.Record.Number}: {snap.Record.Title}",
                        ["assignee"] = owner, ["due_date"] = due,
                    },
                    CreateComment = $"Created from the mitigations of {snap.Record.Number}",
                });
                if (!res.Ok) throw new WorkflowException("Couldn't create an action item: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                clone.CreatedRecordId = res.Record!.Id;
                created++;
            }
            updated.Add(clone);
        }
        if (created > 0)
        {
            var res = await ctx.Records.SaveAsync(await ctx.Records.LoadRequiredAsync(snap.Record.Id), proposed, new SaveOptions { System = true });
            if (!res.Ok) throw new WorkflowException(string.Join(" ", res.Errors.Select(e => e.Message)));
        }
    }

    public override Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var list = new List<RecordAction>();
        if (ctx.Me.IsSystem) return Task.FromResult<IEnumerable<RecordAction>>(list);
        var st = RiskRules.Evaluate(s.Data.Main, s.Rows("mitigations"), s.Config);
        if (s.PhaseKey is "assessment" or "mitigation" or "review" && st.Current is { Acceptable: false } band && !st.AcceptedForCurrent)
        {
            string? why = null;
            if (!ctx.Directory.IsMember(ctx.Me.Id, RiskRules.AuthorityGroup)) why = "Only Quality approvers have risk acceptance authority (SAF-045).";
            else if (s.UserId("owner") == ctx.Me.Id) why = "The risk owner can't accept their own risk (segregation of duties).";
            list.Add(new RecordAction
            {
                Key = "accept_risk", Label = "Accept risk", Icon = "bi-pen", Group = "Risk acceptance", Variant = "danger",
                Description = $"Accept this {band.Label} risk (score {st.CurrentScore}) as it stands. The acceptance is e-signed and recorded in the audit trail.",
                Confirm = "Accept an unacceptable risk only when further reduction isn't practicable and the benefits outweigh the residual risk (ISO 14971, SAF-045).",
                SignatureMeaning = RiskRules.AcceptanceMeaning, DisabledReason = why,
                Inputs = [new ActionField("rationale", "Benefit-risk rationale", "textarea", true, "Why the benefit outweighs the residual risk, and why further reduction isn't practicable.")],
            });
        }
        if (s.Record.PhaseType == PhaseType.Completed && ctx.Access.CanCreate(s.Form))
            list.Add(new RecordAction
            {
                Key = "reassess", Label = "Reassess risk", Icon = "bi-arrow-repeat", Group = "Create",
                Description = "Start a new assessment of this risk (periodic review or new information), linked to this one (RSK-008).",
            });
        return Task.FromResult<IEnumerable<RecordAction>>(list);
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        var s = ctx.Snapshot;
        switch (actionKey)
        {
            case "accept_risk":
            {
                var st = RiskRules.Evaluate(s.Data.Main, s.Rows("mitigations"), s.Config);
                if (!ctx.Directory.IsMember(ctx.Me.Id, RiskRules.AuthorityGroup)) return ActionOutcome.Fail("Only Quality approvers have risk acceptance authority (SAF-045).");
                if (s.UserId("owner") == ctx.Me.Id) return ActionOutcome.Fail("The risk owner can't accept their own risk (segregation of duties).");
                if (s.PhaseKey is not ("assessment" or "mitigation" or "review")) return ActionOutcome.Fail("The risk can't be accepted in this phase.");
                if (st.Current is not { Acceptable: false } band) return ActionOutcome.Fail("This risk is acceptable; no acceptance is needed.");
                if (st.AcceptedForCurrent) return ActionOutcome.Fail("This risk is already accepted.");
                var rationale = input.Get("rationale");
                if (string.IsNullOrWhiteSpace(rationale)) return ActionOutcome.Fail("Give the benefit-risk rationale.");
                return await SignedChange.ApplyAsync(ctx, new()
                {
                    ["accepted_by"] = ctx.Me.Id, ["accepted_on"] = BusinessCalendar.Today, ["accepted_score"] = (decimal)st.CurrentScore!.Value,
                    ["acceptance_rationale"] = rationale,
                }, RiskRules.AcceptanceMeaning, "Accept risk", input, $"Accepted the {band.Label} risk at score {st.CurrentScore}: {rationale}",
                    $"Risk accepted at score {st.CurrentScore} ({band.Label}).", RiskRules.Gate);
            }
            case "reassess":
            {
                if (s.Record.PhaseType != PhaseType.Completed || !ctx.Access.CanCreate(s.Form)) return ActionOutcome.Fail("You can't reassess this risk.");
                var m = s.Data.Main;
                var values = new Dictionary<string, object?>
                {
                    ["subject"] = s.Text("subject"), ["risk_category"] = s.KeywordKey("risk_category"), ["risk_source"] = s.KeywordKey("risk_source"),
                    ["owner"] = s.UserId("owner"), ["product"] = s.Text("product"), ["hazard"] = s.Text("hazard"), ["harm"] = s.Text("harm"),
                    ["previous_assessment"] = s.Record.Id,
                    ["existing_controls"] = string.Join("\n", new[] { s.Text("existing_controls") }
                        .Concat(s.Rows("mitigations").Select(r => r.Values.Text("measure")).Where(x => x != null).Select(x => "• " + x))
                        .Where(x => !string.IsNullOrWhiteSpace(x))),
                    ["severity"] = s.KeywordKey(m.Has("residual_severity") ? "residual_severity" : "severity"),
                    ["likelihood"] = s.KeywordKey(m.Has("residual_likelihood") ? "residual_likelihood" : "likelihood"),
                };
                if (m.Link("source_record") is { } src) values["source_record"] = src;
                Record? created = null;
                var outcome = await Tx.RunAsync(ctx.Db, async () =>
                {
                    var res = await ctx.Records.CreateAsync(new CreateRequest
                    {
                        FormKey = "risk_assessment", Values = values, CreateComment = $"Reassessment of {s.Record.Number} (RSK-008)",
                    });
                    if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                    created = res.Record;
                    await ctx.Records.LinkAsync(s.Record.Id, res.Record!.Id, LinkKind.Related);
                    await ctx.Db.SaveChangesAsync();
                    return ActionOutcome.Ok($"Reassessment {res.Record.Number} created.", res.Record.Id);
                }, o => o.Success);
                return outcome;
            }
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var st = RiskRules.Evaluate(s.Data.Main, s.Rows("mitigations"), s.Config);
        var panels = new List<RecordPanel>
        {
            new()
            {
                Key = "risk-banner", Title = "Risk acceptance", Placement = "top", Order = 1,
                Partial = "~/Modules/Risk/Views/_RiskBanner.cshtml",
                Model = new RiskBannerVm
                {
                    State = st, Status = s.KeywordKey("acceptance_status"), Open = s.Record.PhaseType == PhaseType.Normal,
                    AcceptedBy = s.UserId("accepted_by") is { } a ? ctx.Directory.Name(a) : null, AcceptedOn = s.Date("accepted_on"),
                    Rationale = s.Text("acceptance_rationale"),
                },
            },
        };
        if (st.InitialScore != null || st.ResidualScore != null)
            panels.Add(new RecordPanel
            {
                Key = "risk-matrix", Title = "Risk matrix", Icon = "bi-grid-3x3-gap", Placement = "side", Order = 1,
                Partial = "~/Modules/Risk/Views/_RiskMatrix.cshtml", Model = new RiskMatrixVm { State = st, Title = s.Record.Title },
            });
        return await Task.FromResult(panels);
    }
}
