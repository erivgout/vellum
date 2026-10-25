using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// Quality events §12.2: Initiate → Initial Review → Investigation Planning → Plan Approval → Active Investigation → Final Approval → Completed
/// (QE-102), with the Track &amp; Trend shortcut from Initial Review to Final Approval. Critical events need an escalation list whose members
/// are alerted on creation and every transition (QE-103). Plan rows become Action Items on Active Investigation (QE-104); Final Approval
/// needs every Action Item closed (QE-105); "Is a CAPA required?" gate (QE-005, INV-041); distribution list notified on completion (QE-106).
/// </summary>
public sealed class QualityEventsApp : IPackagedApp
{
    public string Key => "quality_events";
    public int Order => 36;

    public void Define(AppBuilder app)
    {
        app.Name("Quality Events")
            .Description("Report and investigate deviations from procedure, with criticality-based escalation and a CAPA decision.")
            .Icon("bi-lightning-charge").Color("#0284c7").Suite("Quality").Order(15).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@all-employees", AppRole.Author)
            .Grant("@quality", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        QualitySites.Keywords(app);
        QualityCosts.Keywords(app);
        app.Keywords("qe_criticality", "Event criticality", k => k
            .Add("critical", "Critical", "danger").Add("major", "Major", "warning").Add("minor", "Minor", "info"));
        app.Keywords("qe_event_type", "Quality event type", k => k
            .Add("procedure", "Procedure not followed").Add("documentation", "Documentation / GDP error").Add("equipment", "Equipment failure")
            .Add("environment", "Environmental excursion").Add("training", "Training gap").Add("material", "Material handling").Add("other", "Other"));
        app.Keywords("qe_product_impact", "Product impact", k => k
            .Add("none", "No product impact", "success").Add("potential", "Potential impact", "warning").Add("confirmed", "Confirmed impact", "danger"));

        app.Form("quality_event", "Quality Event", f =>
        {
            f.Icon("bi-lightning-charge")
                .Description("A deviation from procedure: what happened, its impact, the investigation and the CAPA decision.")
                .Numbering("QE-")
                .Title("{subject}")
                .Section("event", "Event information", s =>
                {
                    s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("For example: EO sterilizer load released before cycle records were reviewed");
                    s.Keyword("event_type", "Event type", "qe_event_type").Required();
                    s.Keyword("criticality", "Criticality", "qe_criticality").Required().Header().Default("minor")
                        .Help("Critical needs an escalation list. Everyone on it is alerted when the event is created and at every phase change (QE-103).");
                    s.User("owner", "Event owner").Required().Header().Default("$me");
                    s.Date("event_date", "Occurred on").Required().Default("$today");
                    s.Keyword("site", "Site", "quality_site");
                    s.Text("area", "Area / line", 4);
                    s.Text("product", "Product affected", 4);
                    s.Text("lot_number", "Lot / batch", 4);
                    s.TextArea("description", "What happened").Required();
                    s.TextArea("immediate_actions", "Immediate actions taken").Required();
                }, icon: "bi-lightning-charge")
                .Section("escalation", "Escalation", s =>
                {
                    s.Prompt("escalation_prompt", "Critical event: everyone on this list gets a safety-critical alert now and a notification at every phase change (QE-103).").VisibleWhen("criticality=critical");
                    s.Users("escalation_list", "Escalation notification list", 12).VisibleWhen("criticality=critical");
                }, icon: "bi-megaphone")
                .Section("impact", "Initial impact assessment", s =>
                {
                    s.Keyword("product_impact", "Product impact", "qe_product_impact").RequiredIn("initial_review");
                    s.YesNo("patient_safety_impact", "Potential patient safety impact?").RequiredIn("initial_review");
                    s.YesNo("regulatory_impact", "Regulatory reporting impact?");
                    s.TextArea("impact_summary", "Impact assessment").RequiredIn("initial_review");
                }, icon: "bi-activity")
                .Section("track_trend", "Track & Trend", s =>
                {
                    s.YesNo("track_trend", "Track & Trend only?").Default("no")
                        .Help("Yes skips the investigation and sends the event straight to Final Approval. A justification is required.");
                    s.TextArea("track_trend_justification", "Why no investigation is needed").VisibleWhen("track_trend=yes");
                }, icon: "bi-graph-up")
                .Subform("investigation_plan", "Investigation plan", s =>
                {
                    s.Text("action", "Investigation task", 5).Required();
                    s.User("assignee", "Assign to", 3).Required();
                    s.Number("days", "Days to complete", 2).Required().Min(1).Max(365).Decimals(0);
                    s.YesNo("verify", "Verify?", 2);
                }, tab: "Investigation", rowNoun: "task", required: true, icon: "bi-list-check",
                   description: "Each row becomes an Action Item when the plan is approved (QE-104).")
                .Section("investigation", "Investigation", s =>
                {
                    s.TextArea("investigation_summary", "Investigation summary").RequiredIn("active_investigation");
                    s.Keyword("root_cause_category", "Root cause category", "root_cause_category").RequiredIn("active_investigation");
                    s.TextArea("root_cause", "Root cause").RequiredIn("active_investigation");
                    s.TextArea("trend_analysis", "Trend analysis (similar events in the last 12 months)");
                }, tab: "Investigation", icon: "bi-search")
                .Section("capa_decision", "CAPA decision", s =>
                {
                    s.YesNo("capa_required", "Is a CAPA required?").Help("Yes: use \"Initiate CAPA\"; the event can't reach Final Approval until the CAPA exists. No: a justification is required (QE-005).");
                    s.TextArea("capa_justification", "Justification (no CAPA)").VisibleWhen("capa_required=no");
                    s.Link("capa_record", "Linked CAPA", "capa");
                }, tab: "Investigation", icon: "bi-shield-check")
                .Section("distribution", "Distribution", s =>
                {
                    s.Users("distribution_list", "Online distribution list", 12).Help("Notified when the event is completed (QE-106).");
                }, icon: "bi-people");
            QualityCosts.Define(f);
            f.Workflow("quality_event", "Quality event", w =>
            {
                w.Start("initiate", "Initiate").Purpose("Describe what happened and the immediate actions. Critical events need an escalation list.")
                    .DueDays(1).Edit("event", "escalation", "distribution")
                    .Hide("impact", "track_trend", "investigation_plan", "investigation", "capa_decision", "costs", "cost_summary");
                w.Phase("initial_review", "Initial Review").Purpose("Quality confirms the criticality and impact, then investigates or chooses Track & Trend only.")
                    .AssignGroup("qa-approvers").DueDays(2).Edit("event", "escalation", "impact", "track_trend", "capa_decision", "distribution")
                    .Hide("investigation_plan", "investigation", "costs", "cost_summary");
                w.Phase("investigation_planning", "Investigation Planning").Purpose("Plan the investigation tasks. Each becomes an Action Item when the plan is approved.")
                    .AssignField("owner").DueDays(5).Edit("investigation_plan", "impact", "escalation", "costs").ReadOnlyRest().Hide("investigation", "capa_decision");
                w.Phase("plan_approval", "Plan Approval").Purpose("Quality approves the investigation plan.")
                    .AssignGroup("qa-approvers").DueDays(3).ReadOnlyRest().Hide("investigation", "capa_decision");
                w.Phase("active_investigation", "Active Investigation").Purpose("Carry out the plan, find the root cause and decide whether a CAPA is needed. Every action item must be closed before Final Approval (QE-105).")
                    .AssignField("owner").DueDays(20).Edit("investigation", "capa_decision", "escalation", "costs", "distribution").ReadOnlyRest()
                    .MustComplete("action_item").Activities("action_item");
                w.Phase("final_approval", "Final Approval").Purpose("Quality approves the investigation and the CAPA decision.")
                    .AssignGroup("qa-approvers").DueDays(3).ReadOnlyRest();
                w.Completed("completed", "Completed");
                w.Voided();

                w.Forward("initiate", "initial_review", label: "Submit for review");
                w.Void("initiate");
                w.Forward("initial_review", "investigation_planning", label: "Investigate", when: "track_trend!=yes");
                w.Forward("initial_review", "final_approval", label: "Track & Trend: send to Final Approval", when: "track_trend=yes", comment: true);
                w.Back("initial_review", "initiate");
                w.Void("initial_review");
                w.Forward("investigation_planning", "plan_approval", label: "Submit plan for approval");
                w.Back("investigation_planning", "initial_review");
                w.Forward("plan_approval", "active_investigation", label: "Approve plan", sign: "Plan approved");
                w.Back("plan_approval", "investigation_planning", label: "Reject: revise plan");
                w.Forward("active_investigation", "final_approval", label: "Send to Final Approval");
                w.Back("active_investigation", "plan_approval");
                w.Forward("final_approval", "completed", label: "Approve and complete", sign: "Approved");
                w.Back("final_approval", "active_investigation", label: "Back to investigation");
                w.Back("final_approval", "initial_review", label: "Back to initial review");
            });
        });

        app.View("mine", "My quality events", v => v.Category("1. My work").Forms("quality_event").Mine()
            .Columns("number", "title", "phase", "due", "criticality", "event_type").Sort("due", false).Default());
        app.View("open", "Open quality events", v => v.Category("2. Quality events").Forms("quality_event").Open()
            .Columns("number", "title", "phase", "due", "criticality", "site", "owner"));
        app.View("critical", "Open critical events", v => v.Category("2. Quality events").Forms("quality_event").Open().Filter("criticality", "eq", "critical")
            .Columns("number", "title", "phase", "due", "owner", "escalation_list"));
        app.View("by_phase", "Open by phase", v => v.Category("2. Quality events").Forms("quality_event").Open()
            .Columns("number", "phase", "title", "due", "criticality", "owner").Sort("phase", false));
        app.View("overdue", "Overdue events", v => v.Category("2. Quality events").Forms("quality_event").Overdue()
            .Columns("number", "title", "phase", "due", "criticality", "owner").Sort("due", false));
        app.View("track_trend", "Track & Trend", v => v.Category("2. Quality events").Forms("quality_event").All().Filter("track_trend", "eq", "yes")
            .Columns("number", "title", "event_type", "site", "phase", "created").Sort("created"));
        app.View("all", "All quality events", v => v.Category("2. Quality events").Forms("quality_event").All()
            .Columns("number", "title", "phase", "criticality", "event_type", "site", "created").Sort("created"));
        app.View("completed", "Completed events", v => v.Category("3. Closed").Forms("quality_event").Completed()
            .Columns("number", "title", "criticality", "root_cause_category", "capa_required", "modified").Sort("modified"));
        app.View("voided", "Voided events", v => v.Category("3. Closed").Forms("quality_event").Voided().Columns("number", "title", "owner", "modified"));
        app.View("actions", "Open action items", v => v.Category("4. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee").Sort("due", false));
        app.View("actions_all", "All action items", v => v.Category("4. Action items").Forms("action_item").All()
            .Columns("number", "title", "phase", "assignee", "due_date", "completed_on").Sort("created"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        var records = d.Services.GetRequiredService<RecordService>();

        // 1. Open CRITICAL event in Active Investigation, escalation list alerted, action items running (one overdue), CAPA linked.
        var e1 = await d.CreateAsync("quality_event", new()
        {
            ["subject"] = "EO sterilization load released before cycle records were reviewed",
            ["event_type"] = "procedure", ["criticality"] = "critical", ["owner"] = d.User("jlee"), ["event_date"] = DemoContext.Days(-9),
            ["site"] = "kalamazoo", ["area"] = "Sterilization (EO chamber 2)", ["product"] = "ST-40 sterile tubing set", ["lot_number"] = "24-127",
            ["description"] = "Load EO2-0918 (lot 24-127, 2,400 units) was moved to finished goods and 600 units shipped before the cycle's parametric release record was reviewed. The humidity trace later showed a 12-minute excursion below 40% RH.",
            ["immediate_actions"] = "Remaining 1,800 units placed on hold in FG-2. Distributor asked to quarantine 600 units. Biological indicators sent for incubation.",
            ["escalation_list"] = new List<int> { d.User("dokafor"), d.User("sortiz"), d.User("rnovak"), d.User("lgarcia") },
            ["distribution_list"] = new List<int> { d.User("lgarcia"), d.User("ebaker") },
        }, "jlee", daysAgo: 9, rows: new()
        {
            ["costs"] = [new() { ["cost_category"] = "third_party", ["cost_date"] = DemoContext.Days(-7), ["cost_amount"] = 3800m, ["cost_note"] = "Expedited BI and sterility testing (contract lab)" }],
        });
        await d.RouteAsync(e1.Id, "initial_review");
        await d.UpdateAsync(e1.Id, new()
        {
            ["product_impact"] = "potential", ["patient_safety_impact"] = true, ["regulatory_impact"] = true,
            ["impact_summary"] = "Sterility assurance of lot 24-127 not demonstrated until BIs read out. 600 units at a distributor; none confirmed used on patients.",
        }, "sortiz");
        await d.RouteAsync(e1.Id, "investigation_planning", asUser: "sortiz");
        await SetPlanAsync(d, e1.Id,
        [
            ("Review EO2 humidity sensor calibration and alarm settings", "ebaker", 3m, false),
            ("Interview release staff and review the parametric release SOP", "jlee", 5m, false),
            ("Evaluate BI results and decide disposition of lot 24-127", "hmoreau", 7m, true),
        ]);
        await d.RouteAsync(e1.Id, "plan_approval", assignees: ["dokafor"]);
        await d.RouteAsync(e1.Id, "active_investigation", comment: "Approved. Keep the escalation list updated daily.");
        var items = await records.ChildrenAsync(e1.Id, "action_item");
        if (items.Count > 0)
        {
            await CapaApp.CompleteActionItemAsync(d, records, items[0].Id, "Humidity sensor RH-2 read 6% high; recalibrated. Low-RH alarm was disabled after the June PM; re-enabled.");
            if (items.Count > 1) await d.SetDueAsync(items[1].Id, DemoContext.Days(-1));
        }
        var capa = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Parametric release bypassed for EO sterilization loads",
            ["capa_type"] = "corrective", ["source"] = "quality_event", ["priority"] = "critical", ["owner"] = d.User("jlee"),
            ["risk_severity"] = "catastrophic", ["risk_occurrence"] = "unlikely",
            ["source_record"] = e1.Id, ["product"] = "EO sterilization, chamber 2",
            ["description"] = "QE found that loads can be moved to finished goods before parametric release review because the ERP status isn't interlocked with the sterilization record.",
        }, "jlee", daysAgo: 4);
        await LinkAsync(d, e1.Id, capa.Id, "capa_record");
        await d.UpdateAsync(e1.Id, new() { ["capa_required"] = true, ["investigation_summary"] = "Interim: humidity alarm disabled after PM; release step relies on manual check." }, "jlee");

        // 2. Completed via Track & Trend (minor GDP error).
        var e2 = await d.CreateAsync("quality_event", new()
        {
            ["subject"] = "Batch record entry corrected without single-line strike-through",
            ["event_type"] = "documentation", ["criticality"] = "minor", ["owner"] = d.User("pshah"), ["event_date"] = DemoContext.Days(-26),
            ["site"] = "kalamazoo", ["area"] = "Assembly line 1", ["product"] = "VX-200", ["lot_number"] = "VX2-24311",
            ["description"] = "An operator overwrote a torque reading in DHR VX2-24311 instead of using a single-line strike-through with initials and date.",
            ["immediate_actions"] = "Entry corrected per GDP with a late-entry note; value verified against the torque analyzer log.",
        }, "pshah", daysAgo: 26);
        await d.RouteAsync(e2.Id, "initial_review", assignees: ["sortiz"]);
        await d.UpdateAsync(e2.Id, new()
        {
            ["product_impact"] = "none", ["patient_safety_impact"] = false, ["regulatory_impact"] = false,
            ["impact_summary"] = "Value confirmed from the analyzer log; no product impact.",
            ["track_trend"] = true, ["track_trend_justification"] = "Isolated GDP error, value verified. Trended under GDP errors; 2 similar in 12 months (below the trigger of 5).",
            ["capa_required"] = false, ["capa_justification"] = "Below the GDP trend trigger; addressed through the monthly GDP refresher.",
        }, "sortiz");
        await d.RouteAsync(e2.Id, "final_approval", comment: "Track & Trend: isolated GDP error.", assignees: ["dokafor"]);
        await d.RouteAsync(e2.Id, "completed");

        // 3. Completed full investigation (major), no CAPA, with justification.
        var e3 = await d.CreateAsync("quality_event", new()
        {
            ["subject"] = "Cleanroom B differential pressure alarm during fill operation",
            ["event_type"] = "environment", ["criticality"] = "major", ["owner"] = d.User("akim"), ["event_date"] = DemoContext.Days(-48),
            ["site"] = "galway", ["area"] = "Cleanroom B", ["product"] = "CH-1020 catheter hubs", ["lot_number"] = "CH-0731",
            ["description"] = "Differential pressure between cleanroom B and the gowning room fell to 4 Pa (limit 10 Pa) for 18 minutes while filling.",
            ["immediate_actions"] = "Filling stopped; room recovered after door seal replaced. Batch CH-0731 held pending review.",
            ["distribution_list"] = new List<int> { d.User("lgarcia"), d.User("mchen") },
        }, "akim", daysAgo: 48);
        await d.RouteAsync(e3.Id, "initial_review", assignees: ["sortiz"]);
        await d.UpdateAsync(e3.Id, new()
        {
            ["product_impact"] = "potential", ["patient_safety_impact"] = false, ["impact_summary"] = "Settle plates and particle counts during the excursion need review before batch release.",
        }, "sortiz");
        await d.RouteAsync(e3.Id, "investigation_planning");
        await SetPlanAsync(d, e3.Id, [("Review EM data during the excursion", "hmoreau", 3m, false), ("Inspect and replace door seals on all cleanroom B doors", "ebaker", 5m, false)]);
        await d.RouteAsync(e3.Id, "plan_approval", assignees: ["sortiz"]);
        await d.RouteAsync(e3.Id, "active_investigation");
        foreach (var ai in await records.ChildrenAsync(e3.Id, "action_item"))
            await CapaApp.CompleteActionItemAsync(d, records, ai.Id, "Completed; results attached.");
        await d.UpdateAsync(e3.Id, new()
        {
            ["investigation_summary"] = "EM data within limits during the excursion (0 CFU settle plates, particles < ISO 7). Door seal on door B-3 had torn.",
            ["root_cause_category"] = "machine", ["root_cause"] = "Worn door seal on B-3; seals were not on the PM schedule.",
            ["trend_analysis"] = "First pressure excursion in cleanroom B in 24 months.",
            ["capa_required"] = false, ["capa_justification"] = "Single, explained occurrence. Door seals added to the quarterly PM (change CC-0412); no systemic issue.",
        }, "akim");
        await d.RouteAsync(e3.Id, "final_approval", assignees: ["dokafor"]);
        await d.RouteAsync(e3.Id, "completed", comment: "Batch CH-0731 released based on EM data.");

        // 4. In Plan Approval.
        var e4 = await d.CreateAsync("quality_event", new()
        {
            ["subject"] = "Expired calibration on caliper used for incoming inspection",
            ["event_type"] = "equipment", ["criticality"] = "major", ["owner"] = d.User("cwu"), ["event_date"] = DemoContext.Days(-5),
            ["site"] = "kalamazoo", ["area"] = "Receiving inspection",
            ["description"] = "Digital caliper CAL-118 was used for 14 incoming inspections after its calibration expired on the 1st.",
            ["immediate_actions"] = "Caliper removed and sent for calibration; the 14 lots identified.",
        }, "cwu", daysAgo: 5);
        await d.RouteAsync(e4.Id, "initial_review", assignees: ["sortiz"]);
        await d.UpdateAsync(e4.Id, new() { ["product_impact"] = "potential", ["patient_safety_impact"] = false, ["impact_summary"] = "14 lots inspected with an out-of-calibration caliper; as-found result pending." }, "sortiz");
        await d.RouteAsync(e4.Id, "investigation_planning");
        await SetPlanAsync(d, e4.Id, [("Get as-found calibration result for CAL-118", "ebaker", 5m, false), ("Re-inspect retained samples from the 14 lots", "cwu", 10m, true)]);
        await d.RouteAsync(e4.Id, "plan_approval", assignees: ["sortiz", "dokafor"]);

        // 5. Just reported by an operator.
        await d.CreateAsync("quality_event", new()
        {
            ["subject"] = "Wrong revision of work instruction posted at line 2",
            ["event_type"] = "documentation", ["criticality"] = "minor", ["owner"] = d.User("nflores"), ["event_date"] = DemoContext.Today,
            ["site"] = "kalamazoo", ["area"] = "Assembly line 2",
            ["description"] = "WI-ASM-014 rev B was posted at station 4; rev C has been effective since last week.",
            ["immediate_actions"] = "Replaced with rev C and told the shift lead.",
        }, "nflores");
    }

    private static async Task SetPlanAsync(DemoContext d, Guid id, (string Action, string User, decimal Days, bool Verify)[] rows)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(id);
        var data = new RecordData();
        var list = data.RowsOf("investigation_plan");
        foreach (var (action, user, days, verify) in rows)
        {
            var row = new RowBag { IsNew = true, Order = list.Count };
            row.Values.Set("action", Val.Of(action));
            row.Values.Set("assignee", Val.Ref(d.User(user)));
            row.Values.Set("days", Val.Of(days));
            row.Values.Set("verify", Val.Bool(verify));
            list.Add(row);
        }
        var res = await records.SaveAsync(snap, data, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException("Demo plan failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    /// <summary>Demo: link two records both ways and store the link in a link field.</summary>
    internal static async Task LinkAsync(DemoContext d, Guid from, Guid to, string field)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        await records.LinkAsync(from, to, LinkKind.Source);
        await d.Db.SaveChangesAsync();
        var snap = await records.LoadRequiredAsync(from);
        var res = await records.UpdateAsync(from, new() { [field] = snap.Links(field).Append(to).Distinct().ToList() });
        if (!res.Ok) throw new InvalidOperationException("Demo link failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }
}

public sealed class QualityEventBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["quality_event"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        // QE-103: a critical event can't be saved without its escalation list.
        if (ctx.KeywordKey("criticality") == "critical" && ctx.Proposed.Main.Refs("escalation_list").Count == 0)
            ctx.Error("escalation_list", "A critical event needs an escalation notification list (QE-103).");
        QualityCosts.Compute(ctx);
        return Task.CompletedTask;
    }

    /// <summary>QE-103 / SAF-020: safety-critical alert to the escalation list when a critical event is raised.</summary>
    public override Task OnCreatedAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        if (s.KeywordKey("criticality") == "critical")
            ctx.Get<NotificationService>().SafetyCritical(s.UserIds("escalation_list"), $"Critical quality event {s.Record.Number}: {s.Record.Title}",
                $"{s.Text("description")}\n\nImmediate actions: {s.Text("immediate_actions")}\nReported by {ctx.Me.Name}.", s.Record.Id);
        return Task.CompletedTask;
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.Kind is RouteKind.Forward or RouteKind.SpecialForward && s.KeywordKey("criticality") == "critical" && s.UserIds("escalation_list").Count == 0)
            ctx.Error("Add an escalation notification list: it's required for critical events (QE-103).");

        if (ctx.To.Key == "final_approval" && ctx.Kind == RouteKind.Forward)
        {
            if (ctx.From.Key == "initial_review" && string.IsNullOrWhiteSpace(s.Text("track_trend_justification")))
                ctx.Error("Explain why no investigation is needed before choosing Track & Trend.");
            // QE-105: no open action items when routing to Final Approval (either path).
            var open = await QualityLinks.OpenActionItemsAsync(ctx);
            if (open.Count > 0) ctx.Error($"Complete the open action items before Final Approval: {string.Join(", ", open)} (QE-105).");
            await QualityLinks.CheckRequiredGateAsync(ctx, "capa_required", "capa_justification", "capa_record", "capa", "CAPA");
        }
        if (ctx.To.Key == "completed")
            await QualityLinks.CheckRequiredGateAsync(ctx, "capa_required", "capa_justification", "capa_record", "capa", "CAPA");
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        // QE-104: plan rows become Action Items when the plan is approved.
        if (ctx.To.Key == "active_investigation" && ctx.From.Key == "plan_approval")
            await PlannedActions.CreateAsync(ctx, "investigation_plan", s.UserId("owner"));

        var notify = ctx.Get<NotificationService>();
        // QE-103: every transition of a critical event notifies the escalation list.
        if (s.KeywordKey("criticality") == "critical")
            foreach (var uid in s.UserIds("escalation_list").Where(u => u != ctx.Me.Id))
                notify.Notify(uid, NotificationKind.Escalation, $"Critical event {s.Record.Number} moved to {ctx.To.Name}",
                    $"{s.Record.Title}: {ctx.From.Name} → {ctx.To.Name} by {ctx.Me.Name}." + (string.IsNullOrWhiteSpace(ctx.Comment) ? "" : $" Comment: {ctx.Comment}"), s.Record.Id);
        // QE-106: the online distribution list is told when the event is completed.
        if (ctx.To.Key == "completed")
            foreach (var uid in s.UserIds("distribution_list"))
                notify.Notify(uid, NotificationKind.Info, $"Quality event {s.Record.Number} completed",
                    $"{s.Record.Title}. Root cause: {s.Text("root_cause") ?? "n/a (Track & Trend)"}. CAPA: {(s.Bool("capa_required") == true ? "required" : "not required")}.", s.Record.Id);
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        if (!ctx.IsOpen() || s.Bool("capa_required") != true || s.PhaseKey is not ("initial_review" or "active_investigation" or "final_approval") || !await ctx.CanActAsync())
            return [];
        if ((await QualityLinks.LinkedAsync(ctx, "capa_record", "capa")).Count > 0) return [];
        return [new RecordAction { Key = "qe-capa", Label = "Initiate CAPA", Icon = "bi-shield-plus", Group = "Create",
            Description = "Create a CAPA linked to this event. The event can't reach Final Approval until it exists (QE-005)." }];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "qe-capa" || (await ActionsAsync(ctx)).All(a => a.Key != actionKey)) return ActionOutcome.Fail("This action isn't available.");
        var s = ctx.Snapshot;
        var (rec, err) = await QualityLinks.CreateFromAsync(ctx, "capa", new()
        {
            ["subject"] = s.Text("subject"), ["capa_type"] = "corrective", ["source"] = "quality_event",
            ["priority"] = s.KeywordKey("criticality") switch { "critical" => "critical", "major" => "high", _ => "medium" },
            ["owner"] = s.UserId("owner") ?? ctx.Me.Id, ["source_record"] = s.Record.Id, ["product"] = s.Text("product"),
            ["description"] = $"{s.Text("description")}\n\nRoot cause: {s.Text("root_cause") ?? "under investigation"}",
        }, "capa_record");
        return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"{rec.Number} created and linked to {s.Record.Number}.", rec.Id);
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var capas = await QualityLinks.LinkedAsync(ctx, "capa_record", "capa");
        var model = new QeBannerModel(s.KeywordKey("criticality") == "critical", s.UserIds("escalation_list").Select(ctx.Directory.Get).OfType<Engine.Security.UserInfo>().ToList(),
            s.Bool("track_trend") == true, s.Bool("capa_required"), capas.Select(c => (c.Id, c.Number, c.Title)).ToList(), !ctx.IsOpen());
        var panels = new List<RecordPanel> { new() { Key = "qe-banner", Title = "Status", Placement = "top", Partial = "~/Modules/Quality/Views/_QeBanner.cshtml", Model = model } };
        if (QualityCosts.Panel(ctx) is { } costs) panels.Add(costs);
        return panels;
    }
}

public sealed record QeBannerModel(bool Critical, List<Engine.Security.UserInfo> Escalation, bool TrackTrend, bool? CapaRequired,
    List<(Guid Id, string Number, string Title)> Capas, bool Closed);
