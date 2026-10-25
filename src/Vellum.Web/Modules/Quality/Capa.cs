using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// CAPA §12.4: Initiate → Initial Review → Root Cause &amp; Corrective Action Plan → Plan Approval → Implementation →
/// Effectiveness Check → Closed. Planned actions become Action Items on entering Implementation (CAPA-003, QE-002);
/// Effectiveness Check needs every Action Item closed (CAPA-004, INV-043); "not effective" reopens (INV-042).
/// </summary>
public sealed class CapaApp : IPackagedApp
{
    public string Key => "capa";
    public int Order => 30;

    public void Define(AppBuilder app)
    {
        app.Name("Corrective & Preventive Action")
            .Description("Investigate root causes, plan and verify corrective and preventive actions (CAPA, 8D, SCAR).")
            .Icon("bi-shield-check").Color("#7c3aed").Suite("Quality").Order(10).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("@engineering", AppRole.Author)
            .Grant("sortiz", AppRole.Manager, designer: true);

        app.Keywords("capa_type", "CAPA type", k => k
            .Add("corrective", "Corrective", "primary").Add("preventive", "Preventive", "info")
            .Add("eight_d", "8D", "secondary").Add("scar", "SCAR", "warning"));
        app.Keywords("capa_source", "CAPA source", k => k
            .Add("internal_audit", "Internal audit").Add("external_audit", "External audit").Add("customer_complaint", "Customer complaint")
            .Add("ncr", "Nonconformance").Add("quality_event", "Quality event").Add("lab_investigation", "Lab investigation")
            .Add("incident", "EHS incident").Add("risk_assessment", "Risk assessment").Add("management_review", "Management review")
            .Add("supplier", "Supplier issue"));
        app.Keywords("priority", "Priority", k => k
            .Add("low", "Low", "secondary").Add("medium", "Medium", "info").Add("high", "High", "warning").Add("critical", "Critical", "danger"));
        app.Keywords("root_cause_category", "Root cause category (6M)", k => k
            .Add("man", "People").Add("machine", "Machine / equipment").Add("method", "Method / process").Add("material", "Material")
            .Add("measurement", "Measurement").Add("environment", "Environment").Add("design", "Design").Add("supplier", "Supplier"));
        app.Keywords("effectiveness_result", "Effectiveness result", k => k
            .Add("effective", "Effective", "success").Add("not_effective", "Not effective", "danger"));

        app.Form("capa", "CAPA", f => f
            .Icon("bi-shield-check")
            .Description("Corrective or preventive action with root cause analysis and effectiveness check.")
            .Numbering("CAPA-")
            .Title("{subject}")
            .Section("problem", "Problem identification", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("A short, specific description of the problem");
                s.Keyword("capa_type", "Type", "capa_type").Required().Header().Default("corrective");
                s.Keyword("source", "Source", "capa_source").Required();
                s.Keyword("priority", "Priority", "priority").Required().Header().Default("medium");
                s.User("owner", "CAPA owner").Required().Header().Default("$me");
                s.Date("discovered_on", "Discovered on").Default("$today");
                s.Text("product", "Product / process affected", 8);
                s.Link("source_record", "Source record").Help("The audit finding, complaint, NCR or event that raised this CAPA.");
                s.TextArea("description", "Problem description").Required();
            }, icon: "bi-search")
            .Section("containment", "Containment", s =>
            {
                s.YesNo("containment_required", "Containment required?").Default("no");
                s.User("containment_owner", "Containment owner").VisibleWhen("containment_required=yes");
                s.Date("containment_date", "Contained on").VisibleWhen("containment_required=yes");
                s.TextArea("containment_actions", "Immediate actions taken (quantity and location of suspect product)").VisibleWhen("containment_required=yes");
            }, icon: "bi-bandaid")
            .Section("closure", "Close without action", s =>
            {
                s.Prompt("closure_prompt", "Use this only when Initial Review finds no action is needed. A justification is mandatory and kept in the audit trail.");
                s.TextArea("closure_justification", "Justification");
            }, icon: "bi-x-octagon", optional: true)
            .Section("root_cause", "Root cause analysis", s =>
            {
                s.Keyword("root_cause_category", "Root cause category", "root_cause_category").RequiredIn("root_cause_plan");
                s.TextArea("root_cause", "Root cause statement").RequiredIn("root_cause_plan");
            }, tab: "Investigation", icon: "bi-diagram-3")
            .Subform("whys", "5 Whys", s =>
            {
                s.Text("why", "Why?", 6);
                s.Text("because", "Because…", 6);
            }, tab: "Investigation", rowNoun: "why", maxRows: 7, icon: "bi-question-circle")
            .Section("plan", "Corrective action plan", s =>
            {
                s.TextArea("solution", "Develop solution").RequiredIn("root_cause_plan");
            }, tab: "Investigation", icon: "bi-signpost-split")
            .Subform("planned_actions", "Planned actions", s =>
            {
                s.Text("action", "Action", 5).Required();
                s.User("assignee", "Assign to", 3).Required();
                s.Number("days", "Days to complete", 2).Required().Min(1).Max(365).Decimals(0);
                s.YesNo("verify", "Verify?", 2);
            }, tab: "Investigation", rowNoun: "action", required: true, icon: "bi-list-check",
               description: "Each row becomes an Action Item when the CAPA enters Implementation.")
            .Section("effectiveness", "Effectiveness check", s =>
            {
                s.TextArea("effectiveness_criteria", "Success criteria").RequiredIn("root_cause_plan");
                s.Date("effectiveness_due", "Check on").Help("For example 90 days after implementation.");
                s.User("effectiveness_verifier", "Verified by").RequiredIn("implementation");
                s.Keyword("effectiveness_result", "Result", "effectiveness_result").RequiredIn("effectiveness_check");
                s.TextArea("effectiveness_evidence", "Evidence and conclusion").RequiredIn("effectiveness_check");
            }, tab: "Effectiveness", icon: "bi-graph-up-arrow")
            .Workflow("capa", "CAPA", w =>
            {
                w.Start("initiate", "Initiate").Purpose("Describe the problem, its source and any containment.")
                    .DueDays(3).Edit("problem", "containment").Hide("closure", "root_cause", "whys", "plan", "planned_actions", "effectiveness");
                w.Phase("initial_review", "Initial Review").Purpose("Confirm the CAPA is valid, set priority and owner, or close it without action.")
                    .AssignField("owner").DueDays(5).Edit("problem", "containment", "closure").Hide("root_cause", "whys", "plan", "planned_actions", "effectiveness");
                w.Phase("root_cause_plan", "Root Cause & Plan").Purpose("Find the root cause and plan the corrective actions.")
                    .AssignField("owner").DueDays(15).Edit("root_cause", "whys", "plan", "planned_actions", "effectiveness", "containment").ReadOnly("problem").Hide("closure");
                w.Phase("plan_approval", "Plan Approval").Purpose("Quality approves the root cause and action plan.")
                    .AssignGroup("qa-approvers").DueDays(5).ReadOnlyRest().Hide("closure");
                w.Phase("implementation", "Implementation").Purpose("Carry out the planned actions. Every action item must be closed before the effectiveness check.")
                    .AssignField("owner").DueDays(30).Edit("effectiveness").ReadOnlyRest().Hide("closure").MustComplete("action_item").Activities("action_item");
                w.Phase("effectiveness_check", "Effectiveness Check").Purpose("Verify the actions worked against the success criteria.")
                    .AssignField("effectiveness_verifier").DueFrom("effectiveness_due").DueDays(10).Edit("effectiveness").ReadOnlyRest().Hide("closure");
                w.Completed("closed", "Closed");
                w.Completed("closed_no_action", "Closed Without Action");
                w.Voided();

                w.Forward("initiate", "initial_review", label: "Submit for review");
                w.Void("initiate");
                w.Forward("initial_review", "root_cause_plan", label: "Accept: investigate");
                w.Forward("initial_review", "closed_no_action", label: "Close without action", sign: "Closed without action", comment: true);
                w.Back("initial_review", "initiate");
                w.Void("initial_review");
                w.Forward("root_cause_plan", "plan_approval", label: "Submit plan for approval");
                w.Back("root_cause_plan", "initial_review");
                w.Forward("plan_approval", "implementation", label: "Approve plan", sign: "Approved");
                w.Back("plan_approval", "root_cause_plan", label: "Reject: revise plan");
                w.Forward("implementation", "effectiveness_check", label: "Send to effectiveness check");
                w.Back("implementation", "root_cause_plan", label: "Back to planning");
                w.Forward("effectiveness_check", "closed", label: "Effective: close CAPA", sign: "Verified effective", when: "effectiveness_result=effective");
                w.Back("effectiveness_check", "root_cause_plan", label: "Not effective: reopen investigation");
                w.Back("effectiveness_check", "implementation");
            }));

        app.View("mine", "My CAPAs", v => v.Category("1. My work").Forms("capa").Mine()
            .Columns("number", "title", "phase", "due", "priority", "capa_type").Sort("due", false).Default());
        app.View("open", "Open CAPAs", v => v.Category("2. CAPA").Forms("capa").Open()
            .Columns("number", "title", "phase", "due", "priority", "source", "owner"));
        app.View("by_phase", "Open by phase", v => v.Category("2. CAPA").Forms("capa").Open()
            .Columns("number", "phase", "title", "due", "owner").Sort("phase", false));
        app.View("overdue", "Overdue CAPAs", v => v.Category("2. CAPA").Forms("capa").Overdue()
            .Columns("number", "title", "phase", "due", "priority", "owner").Sort("due", false));
        app.View("critical", "High and critical", v => v.Category("2. CAPA").Forms("capa").Open().Filter("priority", "ne", "low").Filter("priority", "ne", "medium")
            .Columns("number", "title", "phase", "due", "priority", "owner"));
        app.View("effectiveness_due", "Effectiveness checks due", v => v.Category("2. CAPA").Forms("capa").Open().Phases("effectiveness_check")
            .Columns("number", "title", "effectiveness_due", "effectiveness_verifier", "due"));
        app.View("closed", "Closed CAPAs", v => v.Category("3. Closed").Forms("capa").Completed()
            .Columns("number", "title", "phase", "priority", "owner", "modified").Sort("modified"));
        app.View("voided", "Voided CAPAs", v => v.Category("3. Closed").Forms("capa").Voided().Columns("number", "title", "owner", "modified"));
        app.View("actions", "CAPA action items", v => v.Category("4. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        // 1. Closed CAPA with signatures and completed action items.
        var c1 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Torque drift on final assembly station 3",
            ["capa_type"] = "corrective", ["source"] = "internal_audit", ["priority"] = "high", ["owner"] = d.User("jlee"),
            ["product"] = "VX-200 infusion pump housing",
            ["description"] = "Audit IA-2026-04 found 6 of 40 sampled housings torqued below the 2.4 N·m minimum. The station's torque driver had drifted.",
            ["effectiveness_criteria"] = "Zero under-torque findings across 3 consecutive weekly audits of 40 units.",
            ["effectiveness_verifier"] = d.User("tbrooks"),
        }, "tbrooks", daysAgo: 64, rows: new()
        {
            ["planned_actions"] =
            [
                new() { ["action"] = "Recalibrate torque driver and add to 30-day calibration interval", ["assignee"] = d.User("ebaker"), ["days"] = 5m, ["verify"] = false },
                new() { ["action"] = "Add torque verification step to WI-ASM-014", ["assignee"] = d.User("akim"), ["days"] = 10m, ["verify"] = true },
            ],
        });
        await d.RouteAsync(c1.Id, "initial_review");
        await d.RouteAsync(c1.Id, "root_cause_plan");
        await d.UpdateAsync(c1.Id, new()
        {
            ["root_cause_category"] = "machine",
            ["root_cause"] = "The torque driver had no defined calibration interval, so drift went undetected for 4 months.",
            ["solution"] = "Put the driver on a 30-day calibration cycle and add a daily torque check to the work instruction.",
        }, "jlee");
        await d.RouteAsync(c1.Id, "plan_approval");
        await d.RouteAsync(c1.Id, "implementation", asUser: "sortiz", comment: "Plan approved. Good root cause work.");
        var records = d.Services.GetRequiredService<RecordService>();
        foreach (var ai in await records.ChildrenAsync(c1.Id, "action_item"))
        {
            var snap = await records.LoadRequiredAsync(ai.Id);
            var doer = d.Directory.Get(snap.UserId("assignee"))!.Username;
            await d.UpdateAsync(ai.Id, new() { ["action_taken"] = "Done as planned; records attached to the calibration log.", ["completed_on"] = DemoContext.Days(-20) }, doer);
            if (snap.Bool("verification_required") == true)
            {
                await d.RouteAsync(ai.Id, "verification");
                await d.UpdateAsync(ai.Id, new() { ["verification_result"] = "verified", ["verification_comments"] = "Checked WI-ASM-014 rev C on the line." }, "jlee");
            }
            await d.RouteAsync(ai.Id, "completed");
        }
        await d.RouteAsync(c1.Id, "effectiveness_check");
        await d.UpdateAsync(c1.Id, new() { ["effectiveness_result"] = "effective", ["effectiveness_evidence"] = "Three weekly audits (120 units) found no under-torque housings." }, "tbrooks");
        await d.RouteAsync(c1.Id, "closed");

        // 2. In implementation with open action items (one overdue).
        var c2 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Mislabeled lot shipped to distributor (wrong expiry date)",
            ["capa_type"] = "corrective", ["source"] = "customer_complaint", ["priority"] = "critical", ["owner"] = d.User("jlee"),
            ["product"] = "Sterile tubing set ST-40",
            ["description"] = "Distributor reported lot 24-118 labeled with a 2029 expiry instead of 2027. Label template variable was not updated after the shelf-life change.",
            ["containment_required"] = true, ["containment_owner"] = d.User("lgarcia"), ["containment_date"] = DemoContext.Days(-30),
            ["containment_actions"] = "Quarantined 1,240 units at the Kalamazoo warehouse; distributor placed 300 units on hold.",
            ["effectiveness_criteria"] = "No label/expiry mismatches in 100% label verification for 60 days.",
            ["effectiveness_verifier"] = d.User("sortiz"),
        }, "jlee", daysAgo: 34, rows: new()
        {
            ["planned_actions"] =
            [
                new() { ["action"] = "Lock expiry field in label template to the shelf-life master data", ["assignee"] = d.User("akim"), ["days"] = 7m, ["verify"] = true },
                new() { ["action"] = "Add second-person label verification to packaging SOP", ["assignee"] = d.User("pshah"), ["days"] = 10m, ["verify"] = false },
                new() { ["action"] = "Notify affected customers and reconcile returned stock", ["assignee"] = d.User("rnovak"), ["days"] = 5m, ["verify"] = false },
            ],
        });
        await d.RouteAsync(c2.Id, "initial_review");
        await d.RouteAsync(c2.Id, "root_cause_plan");
        await d.UpdateAsync(c2.Id, new()
        {
            ["root_cause_category"] = "method",
            ["root_cause"] = "The shelf-life change was made in the product master but the label template used a hard-coded expiry offset.",
            ["solution"] = "Drive the expiry from master data, add a second-person check and notify customers.",
        }, "jlee");
        await d.RouteAsync(c2.Id, "plan_approval");
        await d.RouteAsync(c2.Id, "implementation", asUser: "dokafor");
        var items = await records.ChildrenAsync(c2.Id, "action_item");
        if (items.Count > 0) await d.SetDueAsync(items[0].Id, DemoContext.Days(-3));
        if (items.Count > 2)
        {
            await d.UpdateAsync(items[2].Id, new() { ["action_taken"] = "All 4 distributors notified; 300 units returned and scrapped.", ["completed_on"] = DemoContext.Days(-2) }, "rnovak");
            await d.RouteAsync(items[2].Id, "completed");
        }

        // 3. Awaiting plan approval (assigned to the approvers).
        var c3 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Particulate contamination in cleanroom B fill line",
            ["capa_type"] = "corrective", ["source"] = "quality_event", ["priority"] = "high", ["owner"] = d.User("akim"),
            ["description"] = "Environmental monitoring showed ISO 7 excursions on 3 consecutive days at the fill line.",
            ["effectiveness_criteria"] = "Particle counts within ISO 7 limits for 30 consecutive days.",
        }, "akim", daysAgo: 12, rows: new()
        {
            ["planned_actions"] = [new() { ["action"] = "Replace HEPA filter bank FB-2", ["assignee"] = d.User("ebaker"), ["days"] = 3m, ["verify"] = true }],
            ["whys"] =
            [
                new() { ["why"] = "Why were particle counts high?", ["because"] = "Airflow at the fill line dropped 22%." },
                new() { ["why"] = "Why did airflow drop?", ["because"] = "HEPA filter bank FB-2 was loaded beyond its rating." },
                new() { ["why"] = "Why wasn't it replaced?", ["because"] = "The PM task used pressure differential limits from the old filter model." },
            ],
        });
        await d.RouteAsync(c3.Id, "initial_review");
        await d.RouteAsync(c3.Id, "root_cause_plan");
        await d.UpdateAsync(c3.Id, new()
        {
            ["root_cause_category"] = "machine", ["root_cause"] = "Preventive maintenance limits weren't updated for the new filter model.",
            ["solution"] = "Replace the filter bank and correct the PM differential-pressure limit.",
        }, "akim");
        await d.RouteAsync(c3.Id, "plan_approval", assignees: ["sortiz", "dokafor"]);

        // 4. New CAPA in initial review, overdue.
        var c4 = await d.CreateAsync("capa", new()
        {
            ["subject"] = "Supplier certificate of analysis missing for resin lot",
            ["capa_type"] = "scar", ["source"] = "supplier", ["priority"] = "medium", ["owner"] = d.User("cwu"),
            ["description"] = "Three resin deliveries from Polymera arrived without CoAs. Receiving released them under deviation.",
        }, "cwu", daysAgo: 9);
        await d.RouteAsync(c4.Id, "initial_review");
        await d.SetDueAsync(c4.Id, DemoContext.Days(-2));

        // 5. Draft preventive CAPA.
        await d.CreateAsync("capa", new()
        {
            ["subject"] = "Preventive: extend operator training on new labeling system",
            ["capa_type"] = "preventive", ["source"] = "management_review", ["priority"] = "low", ["owner"] = d.User("kpatel"),
            ["description"] = "Management review identified the labeling system rollout as a training risk for second-shift operators.",
        }, "kpatel", daysAgo: 2);
    }
}

public sealed class CapaBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["capa"];

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        // CAPA-002: closing without action needs a justification (kept in the audit trail).
        if (ctx.To.Key == "closed_no_action" && string.IsNullOrWhiteSpace(ctx.Snapshot.Text("closure_justification")))
            ctx.Error("Add a justification in \"Close without action\" before closing this CAPA without action.");
        // INV-042: a CAPA that isn't effective never closes.
        if (ctx.To.Key == "closed" && ctx.Snapshot.KeywordKey("effectiveness_result") != "effective")
            ctx.Error("A CAPA can only close when the effectiveness check result is Effective.");
        // SAF-044: implementer and effectiveness verifier must differ.
        if (ctx.To.Key == "effectiveness_check" && ctx.Snapshot.UserId("effectiveness_verifier") is { } v && v == ctx.Snapshot.UserId("owner"))
            ctx.Error("The effectiveness verifier must be someone other than the CAPA owner (segregation of duties).");
        return Task.CompletedTask;
    }

    /// <summary>CAPA-003 / QE-002: planned actions become Action Items (in Open) when the CAPA enters Implementation.</summary>
    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "implementation") return;
        var calendar = ctx.Get<BusinessCalendar>();
        var snap = ctx.Snapshot;
        var rows = snap.Rows("planned_actions");
        var proposed = new RecordData();
        var updated = proposed.RowsOf("planned_actions");
        var created = 0;
        foreach (var row in rows)
        {
            var clone = row.Clone();
            if (row.CreatedRecordId == null && row.Values.Text("action") is { } action && row.Values.Ref("assignee") is { } assignee)
            {
                var due = await calendar.AddAsync(BusinessCalendar.Today, (int)(row.Values.Number("days") ?? 10), true);
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "action_item",
                    StartPhaseKey = "open",
                    ParentId = snap.Record.Id,
                    SkipAccessCheck = true,
                    Assignees = [assignee],
                    DueDate = due,
                    Values = new()
                    {
                        ["subject"] = action,
                        ["description"] = $"Planned action from {snap.Record.Number}: {snap.Record.Title}",
                        ["assignee"] = assignee,
                        ["due_date"] = due,
                        ["verification_required"] = row.Values.Bool("verify") == true,
                        ["verifier"] = row.Values.Bool("verify") == true ? snap.UserId("owner") : null,
                    },
                    CreateComment = $"Created automatically from the planned actions of {snap.Record.Number} (QE-002)",
                });
                if (!res.Ok) throw new WorkflowException("Couldn't create an action item: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                clone.CreatedRecordId = res.Record!.Id;
                created++;
            }
            updated.Add(clone);
        }
        if (created > 0)
        {
            var fresh = await ctx.Records.LoadRequiredAsync(snap.Record.Id);
            await ctx.Records.SaveAsync(fresh, proposed, new SaveOptions { System = true });
        }
    }
}
