using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Record = Vellum.Domain.Record;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure.Jobs;
using Vellum.Modules.Quality;

namespace Vellum.Tests;

/// <summary>Quality events and product safety suite invariants (§12, INV-041..046, INV-065/066, INV-118/119, QE-103, CMP-009).</summary>
[Collection("vellum")]
public class QualityTests(VellumFixture fx)
{
    private static RecordService Records(IServiceScope s) => s.ServiceProvider.GetRequiredService<RecordService>();

    private static async Task UpdateOkAsync(IServiceScope s, Guid id, Dictionary<string, object?> values)
    {
        s.Db().ChangeTracker.Clear();
        var res = await Records(s).UpdateAsync(id, values);
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => $"{e.FieldKey}: {e.Message}")));
    }

    private static void AssertOk(Engine.Workflow.RouteResult res) => Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));

    private static T Behavior<T>(IServiceScope s) where T : RecordBehavior =>
        s.ServiceProvider.GetServices<RecordBehavior>().OfType<T>().Single();

    private static async Task<RecordContext> ContextAsync(IServiceScope s, Guid id) => new(await s.LoadAsync(id), s.ServiceProvider);

    private static RowBag Row(RecordSnapshot snap, Dictionary<string, object?> values)
    {
        var row = new RowBag { IsNew = true };
        foreach (var (k, v) in values) row.Values.Set(k, FieldCodec.FromObject(snap.Field(k)!, v, snap.Config));
        return row;
    }

    // ------------------------------------------------------------------ quality events

    [Fact] // INV-041, QE-005, QE-105
    public async Task Quality_event_capa_gate_needs_a_linked_capa_or_a_justification()
    {
        using var s = fx.As("jlee");
        var qe = await s.CreateAsync("quality_event", new()
        {
            ["subject"] = "Gate test event", ["event_type"] = "procedure", ["criticality"] = "minor", ["owner"] = s.UserId("jlee"),
            ["description"] = "Step skipped.", ["immediate_actions"] = "Re-trained operator.",
        });
        AssertOk(await s.RouteAsync(qe.Id, "initial_review", assignees: [s.UserId("sortiz")]));

        using var qa = fx.As("sortiz");
        await UpdateOkAsync(qa, qe.Id, new()
        {
            ["product_impact"] = "none", ["patient_safety_impact"] = false, ["impact_summary"] = "None.",
            ["track_trend"] = true, ["track_trend_justification"] = "Isolated occurrence.", ["capa_required"] = true,
        });

        // QE-105: an open action item blocks Final Approval on the Track & Trend path too.
        var ai = await s.CreateAsync("action_item", new() { ["subject"] = "Check other shifts", ["assignee"] = s.UserId("akim"), ["due_date"] = BusinessCalendar.Today.AddDays(5) }, parent: qe.Id);
        var res = await qa.RouteAsync(qe.Id, "final_approval", comment: "Track & Trend");
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("QE-105"));
        AssertOk(await s.RouteAsync(ai.Id, "voided", comment: "Not needed"));

        // Yes without a linked CAPA: blocked.
        res = await qa.RouteAsync(qe.Id, "final_approval", comment: "Track & Trend");
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("INV-041"));

        // No without a justification: blocked.
        await UpdateOkAsync(qa, qe.Id, new() { ["capa_required"] = false });
        res = await qa.RouteAsync(qe.Id, "final_approval", comment: "Track & Trend");
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("justification"));

        // Yes + "Initiate CAPA": the linked CAPA exists, so the gate opens.
        await UpdateOkAsync(qa, qe.Id, new() { ["capa_required"] = true });
        var outcome = await Behavior<QualityEventBehavior>(qa).ExecuteAsync(await ContextAsync(qa, qe.Id), "qe-capa", new ActionInput());
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors ?? []));
        var snap = await qa.LoadAsync(qe.Id);
        var capaId = Assert.Single(snap.Links("capa_record"));
        Assert.True(await qa.Db().Links.AnyAsync(l => l.FromId == qe.Id && l.ToId == capaId && l.Kind == LinkKind.Source));
        AssertOk(await qa.RouteAsync(qe.Id, "final_approval", comment: "Track & Trend", assignees: [qa.UserId("dokafor")]));
    }

    [Fact] // QE-103, SAF-020
    public async Task Critical_events_need_an_escalation_list_that_is_alerted_on_every_transition()
    {
        using var s = fx.As("akim");
        var values = new Dictionary<string, object?>
        {
            ["subject"] = "Sterilizer interlock bypassed", ["event_type"] = "equipment", ["criticality"] = "critical", ["owner"] = s.UserId("akim"),
            ["description"] = "Interlock jumpered during maintenance.", ["immediate_actions"] = "Chamber locked out.",
        };
        var fail = await Records(s).CreateAsync(new CreateRequest { FormKey = "quality_event", Values = values });
        Assert.False(fail.Ok);
        Assert.Contains(fail.Errors, e => e.FieldKey == "escalation_list");

        values["escalation_list"] = new List<int> { s.UserId("dokafor"), s.UserId("rnovak") };
        var qe = await s.CreateAsync("quality_event", values);
        var alerted = await s.Db().Notifications.AsNoTracking().Where(n => n.RecordId == qe.Id && n.Kind == NotificationKind.SafetyCritical).Select(n => n.UserId).ToListAsync();
        Assert.Contains(s.UserId("dokafor"), alerted);
        Assert.Contains(s.UserId("rnovak"), alerted);

        AssertOk(await s.RouteAsync(qe.Id, "initial_review", assignees: [s.UserId("sortiz")]));
        var notified = await s.Db().Notifications.AsNoTracking().Where(n => n.RecordId == qe.Id && n.Kind == NotificationKind.Escalation).Select(n => n.UserId).ToListAsync();
        Assert.Contains(s.UserId("dokafor"), notified);
        Assert.Contains(s.UserId("rnovak"), notified);
    }

    // ------------------------------------------------------------------ lab investigations

    private static async Task<Record> NewLabAsync(IServiceScope s) => await s.CreateAsync("lab_investigation", new()
    {
        ["subject"] = "Assay OOS", ["result_type"] = "oos", ["product"] = "ST-40", ["batch_number"] = "B-" + Guid.NewGuid().ToString("N")[..5],
        ["test_name"] = "EO residual", ["specification"] = "≤ 4.0 mg", ["original_result"] = "5.8 mg",
        ["analyst"] = s.UserId("hmoreau"), ["lab_supervisor"] = s.UserId("sortiz"),
    });

    [Fact] // INV-044, LAB-002, LAB-003
    public async Task Without_a_lab_error_results_stand_and_closure_needs_the_full_investigation()
    {
        using var s = fx.As("hmoreau");
        var lab = await NewLabAsync(s);
        AssertOk(await s.RouteAsync(lab.Id, "lab_investigation"));

        using var sup = fx.As("sortiz");
        await UpdateOkAsync(sup, lab.Id, new()
        {
            ["chk_calculations"] = true, ["chk_method"] = true, ["chk_instrument"] = true, ["chk_standards"] = true, ["chk_sample_prep"] = true, ["chk_analyst"] = true,
            ["lab_error_identified"] = false, ["investigation_lead"] = sup.UserId("jlee"), ["lab_summary"] = "No assignable cause.", ["final_conclusion"] = "Pending.",
            ["ncr_required"] = false, ["ncr_justification"] = "n/a", ["capa_required"] = false, ["capa_justification"] = "n/a",
        });
        var snap = await sup.LoadAsync(lab.Id);
        Assert.False(snap.Bool("results_invalidated"));
        Assert.True(snap.Bool("full_investigation_required"));
        Assert.False((await sup.RouteAsync(lab.Id, "final_approval")).Ok); // the lab-error shortcut doesn't apply

        // A typed lab error invalidates the original result and opens the short path.
        await UpdateOkAsync(sup, lab.Id, new() { ["lab_error_identified"] = true, ["lab_error_type"] = "calculation" });
        snap = await sup.LoadAsync(lab.Id);
        Assert.True(snap.Bool("results_invalidated"));
        Assert.False(snap.Bool("full_investigation_required"));
        AssertOk(await sup.RouteAsync(lab.Id, "final_approval", assignees: [sup.UserId("dokafor")]));

        // If the answer is changed to "no lab error" behind the workflow's back, Closed is still refused.
        await UpdateOkAsync(sup, lab.Id, new() { ["lab_error_identified"] = false });
        Assert.False((await sup.LoadAsync(lab.Id)).Bool("results_invalidated"));
        using var qa = fx.As("dokafor");
        var res = await qa.RouteAsync(lab.Id, "closed");
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("INV-044"));
    }

    [Fact] // INV-045, LAB-004, LAB-005
    public async Task Retests_go_to_a_different_analyst_and_resamples_to_the_same_one()
    {
        using var s = fx.As("hmoreau");
        var lab = await NewLabAsync(s);
        AssertOk(await s.RouteAsync(lab.Id, "lab_investigation"));

        using var sup = fx.As("sortiz");
        var snap = await sup.LoadAsync(lab.Id);
        var bad = new RecordData();
        bad.RowsOf("testing_requests").Add(Row(snap, new() { ["test_kind"] = "retest", ["test_analyst"] = sup.UserId("hmoreau"), ["replicates"] = 3m }));
        var res = await Records(sup).SaveAsync(snap, bad);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("INV-045"));

        sup.Db().ChangeTracker.Clear();
        snap = await sup.LoadAsync(lab.Id);
        var good = new RecordData();
        good.RowsOf("testing_requests").Add(Row(snap, new() { ["test_kind"] = "retest", ["test_analyst"] = sup.UserId("jlee"), ["replicates"] = 3m }));
        var resample = Row(snap, new() { ["test_kind"] = "resample", ["test_analyst"] = sup.UserId("jlee"), ["replicates"] = 2m });
        resample.Order = 1;
        good.RowsOf("testing_requests").Add(resample);
        res = await Records(sup).SaveAsync(snap, good);
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
        snap = await sup.LoadAsync(lab.Id);
        Assert.Equal(sup.UserId("hmoreau"), snap.Rows("testing_requests").Single(r => snap.KeywordKey(r, "test_kind") == "resample").Values.Ref("test_analyst"));

        var created = await LabTesting.CreateTestsAsync(await ContextAsync(sup, lab.Id));
        Assert.Equal(2, created.Count);
        var tests = new List<RecordSnapshot>();
        foreach (var c in created) tests.Add(await sup.LoadAsync(c.Id));
        var retest = tests.Single(t => t.KeywordKey("test_kind") == "retest");
        Assert.Equal(sup.UserId("jlee"), retest.UserId("test_analyst"));
        Assert.Equal(sup.UserId("hmoreau"), tests.Single(t => t.KeywordKey("test_kind") == "resample").UserId("test_analyst"));

        // Domain rule: a re-test can never be given to the original analyst.
        sup.Db().ChangeTracker.Clear();
        var change = await Records(sup).UpdateAsync(retest.Record.Id, new() { ["test_analyst"] = sup.UserId("hmoreau") });
        Assert.False(change.Ok);
        Assert.Contains(change.Errors, e => e.Message.Contains("INV-045"));
    }

    // ------------------------------------------------------------------ complaints

    private static async Task<Record> NewComplaintAsync(IServiceScope s, int awarenessDaysAgo) => await s.CreateAsync("complaint", new()
    {
        ["subject"] = "Pump alarm complaint", ["category"] = "product_quality", ["owner"] = s.UserId("jlee"),
        ["awareness_date"] = BusinessCalendar.Today.AddDays(-awarenessDaysAgo), ["site_reported"] = "kalamazoo",
        ["description"] = "Alarm late.", ["product"] = "VX-200", ["patient_involved"] = false,
    });

    [Fact] // INV-046, CMP-003, CMP-004
    public async Task Complaints_leave_investigation_only_with_completed_decision_trees()
    {
        using var s = fx.As("jlee");
        var c = await NewComplaintAsync(s, 2);
        AssertOk(await s.RouteAsync(c.Id, "investigation"));
        var tree = Assert.Single(await Records(s).ChildrenAsync(c.Id, "mdr_decision_tree")); // started automatically
        await UpdateOkAsync(s, c.Id, new() { ["investigation_summary"] = "Reproduced.", ["conclusion"] = "confirmed" });

        var res = await s.RouteAsync(c.Id, "review", assignees: [s.UserId("sortiz")]);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains(tree.Number));

        await UpdateOkAsync(s, tree.Id, new() { ["q_death"] = false, ["q_serious_injury"] = false, ["q_malfunction"] = false, ["mdr_rationale"] = "No malfunction." });
        Assert.False((await s.LoadAsync(tree.Id)).Bool("mdr_reportable"));
        AssertOk(await s.RouteAsync(tree.Id, "approval"));
        using var ra = fx.As("rnovak");
        AssertOk(await ra.RouteAsync(tree.Id, "completed"));

        var snap = await s.LoadAsync(c.Id);
        Assert.Equal("not_reportable", snap.KeywordKey("mdr_decision"));
        Assert.Equal("none", snap.KeywordKey("clock_status"));
        AssertOk(await s.RouteAsync(c.Id, "review", assignees: [s.UserId("sortiz")]));
    }

    [Fact] // CMP-009, SAF-023, SAF-024
    public async Task Reporting_deadlines_run_from_awareness_with_the_right_calendar()
    {
        using var s = fx.As("rnovak");
        var cal = s.ServiceProvider.GetRequiredService<BusinessCalendar>();
        var aw = new DateOnly(2026, 11, 23); // Monday of Thanksgiving week

        Assert.Equal(new DateOnly(2026, 12, 23), Assert.Single(await ReportingClock.ComputeAsync(aw, "reportable_30", false, null, null, null, cal)).Due); // calendar days
        Assert.Equal(new DateOnly(2026, 12, 2), Assert.Single(await ReportingClock.ComputeAsync(aw, "reportable_5", false, null, null, null, cal)).Due);  // 5 working days, skips the holidays
        Assert.Equal(new DateOnly(2026, 11, 25), Assert.Single(await ReportingClock.ComputeAsync(aw, "not_reportable", true, "reportable_2", null, null, cal)).Due);
        Assert.Equal(new DateOnly(2026, 12, 3), Assert.Single(await ReportingClock.ComputeAsync(aw, "not_reportable", true, "reportable_10", null, null, cal)).Due);
        Assert.Equal(new DateOnly(2026, 12, 8), Assert.Single(await ReportingClock.ComputeAsync(aw, "not_reportable", true, "reportable_15", null, null, cal)).Due);
        Assert.Empty(await ReportingClock.ComputeAsync(aw, "not_reportable", false, "reportable_2", null, null, cal)); // not on the EU market
        var pending = await ReportingClock.ComputeAsync(aw, "pending", true, "pending", null, null, cal);
        Assert.All(pending, d => Assert.True(d.Provisional));
        Assert.Equal(2, pending.Count);

        var clock = await ReportingClock.ComputeAsync(aw, "reportable_30", false, null, null, null, cal);
        Assert.Equal("running", ReportingClock.Status(clock, aw));
        Assert.Equal("due_soon", ReportingClock.Status(clock, new DateOnly(2026, 12, 20)));
        Assert.Equal("missed", ReportingClock.Status(clock, new DateOnly(2026, 12, 24)));
        Assert.Equal("submitted", ReportingClock.Status(await ReportingClock.ComputeAsync(aw, "reportable_30", false, null, aw.AddDays(10), null, cal), new DateOnly(2027, 1, 5)));
    }

    [Fact] // CMP-009, SAF-020
    public async Task A_missed_reporting_deadline_raises_a_safety_critical_alert_to_regulatory()
    {
        Guid id;
        using (var s = fx.As("jlee"))
        {
            var c = await NewComplaintAsync(s, 35);
            id = c.Id;
            await UpdateOkAsync(s, id, new() { ["mdr_decision"] = "reportable_30" });
            var snap = await s.LoadAsync(id);
            Assert.Equal(BusinessCalendar.Today.AddDays(-5), snap.Date("report_due"));
            Assert.Equal("missed", snap.KeywordKey("clock_status"));
        }
        using var job = fx.Services.CreateScope();
        job.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("test");
        var clockJob = job.ServiceProvider.GetServices<IScheduledJob>().OfType<RegulatoryClockJob>().Single();
        await clockJob.RunAsync(new JobContext(job.ServiceProvider, true), CancellationToken.None);
        await clockJob.RunAsync(new JobContext(job.ServiceProvider, true), CancellationToken.None); // idempotent: no duplicate alert
        var rnovak = job.UserId("rnovak");
        var alerts = await job.Db().Notifications.AsNoTracking()
            .Where(n => n.RecordId == id && n.Kind == NotificationKind.SafetyCritical && n.UserId == rnovak && n.Title.StartsWith("MISSED")).ToListAsync();
        Assert.Single(alerts);
        Assert.True(await job.Db().Audit.AnyAsync(a => a.RecordId == id && a.Action == "Regulatory deadline missed"));
    }

    // ------------------------------------------------------------------ NCR and deviations

    private async Task<Record> NewNcrInDispositionAsync(string part, decimal qty, string type, decimal lineQty, string defect = "dimensional")
    {
        using var s = fx.As("jlee");
        var n = await s.CreateAsync("ncr", new()
        {
            ["subject"] = $"NCR for {part}", ["nc_type"] = "finished_product", ["severity"] = "minor", ["owner"] = s.UserId("jlee"),
            ["description"] = "Out of tolerance.", ["part_number"] = part, ["lot_number"] = "L-1", ["quantity_affected"] = qty,
            ["defect_codes"] = new[] { defect },
        }, rows: new() { ["dispositions"] = [new() { ["disposition_type"] = type, ["disposition_qty"] = lineQty, ["disposition_instructions"] = "Per SOP" }] });
        AssertOk(await s.RouteAsync(n.Id, "mrb_review"));
        await UpdateOkAsync(s, n.Id, new() { ["mrb_recommendation"] = "Agreed." });
        foreach (var voter in new[] { "jlee", "sortiz", "akim" })
        {
            using var v = fx.As(voter);
            AssertOk(await v.RouteAsync(n.Id, "disposition"));
        }
        Assert.Equal("disposition", (await s.LoadAsync(n.Id)).PhaseKey);
        return n;
    }

    [Fact] // INV-065, NC-002
    public async Task Disposition_quantities_must_add_up_before_leaving_disposition()
    {
        var n = await NewNcrInDispositionAsync("INV065-P1", 100m, "scrap", 60m);
        using var s = fx.As("jlee");
        var res = await s.RouteAsync(n.Id, "final_approval", assignees: [s.UserId("sortiz")]);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("INV-065"));

        var snap = await s.LoadAsync(n.Id);
        var data = new RecordData();
        data.RowsOf("dispositions").AddRange(snap.Rows("dispositions").Select(r => r.Clone()));
        var rework = Row(snap, new() { ["disposition_type"] = "rework", ["disposition_qty"] = 40m, ["disposition_instructions"] = "WI-RW-01" });
        rework.Order = 1;
        data.RowsOf("dispositions").Add(rework);
        Assert.True((await Records(s).SaveAsync(snap, data)).Ok);
        Assert.Equal(100m, (await s.LoadAsync(n.Id)).Number("disposed_total"));
        AssertOk(await s.RouteAsync(n.Id, "final_approval", assignees: [s.UserId("sortiz")]));
    }

    private async Task<(Guid Id, Engine.Workflow.RouteResult Activation)> NewDeviationAsync(string part, string defect)
    {
        using var s = fx.As("cwu");
        var dev = await s.CreateAsync("deviation", new()
        {
            ["subject"] = $"Deviation {part}", ["owner"] = s.UserId("cwu"), ["reviewer"] = s.UserId("akim"), ["deviation_type"] = "product",
            ["part_number"] = part, ["defect_code"] = defect, ["scope"] = "Zone C only.", ["requirement"] = "Drawing note 4", ["justification"] = "Cosmetic only.",
            ["risk_level"] = "low", ["fit_function_impact"] = false, ["patient_safety_impact"] = false, ["risk_summary"] = "None.",
            ["valid_from"] = BusinessCalendar.Today, ["valid_to"] = BusinessCalendar.Today.AddDays(30), ["max_quantity"] = 500m,
        });
        AssertOk(await s.RouteAsync(dev.Id, "review"));
        using (var r = fx.As("akim")) AssertOk(await r.RouteAsync(dev.Id, "initial_approval", assignees: [r.UserId("sortiz")]));
        using (var a = fx.As("sortiz")) AssertOk(await a.RouteAsync(dev.Id, "final_approval", assignees: [a.UserId("dokafor")]));
        using var f = fx.As("dokafor");
        return (dev.Id, await f.RouteAsync(dev.Id, "active"));
    }

    [Fact] // INV-118, NC-004, INV-119
    public async Task One_active_deviation_per_part_and_defect_and_use_as_is_needs_it()
    {
        var (first, activated) = await NewDeviationAsync("INV118-P1", "cosmetic");
        AssertOk(activated);
        var (_, second) = await NewDeviationAsync("INV118-P1", "cosmetic");
        Assert.False(second.Ok);
        Assert.Contains(second.Errors, e => e.Message.Contains("INV-118"));
        var (_, otherDefect) = await NewDeviationAsync("INV118-P1", "burr");
        AssertOk(otherDefect);

        using (var s = fx.As("jlee"))
        {
            var active = await s.ServiceProvider.GetRequiredService<ActiveDeviations>().ForPartAsync("INV118-P1");
            Assert.Equal(2, active.Count); // INV-119: the part shows both active deviations
        }

        var n = await NewNcrInDispositionAsync("INV118-P1", 10m, "use_as_is", 10m, defect: "cosmetic");
        using var owner = fx.As("jlee");
        AssertOk(await owner.RouteAsync(n.Id, "final_approval", assignees: [owner.UserId("sortiz")]));
        using (var qa = fx.As("sortiz")) AssertOk(await qa.RouteAsync(n.Id, "verification"));
        await UpdateOkAsync(owner, n.Id, new() { ["disposition_verified"] = true, ["verification_notes"] = "Used as is." });
        var res = await owner.RouteAsync(n.Id, "completed");
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("NC-004"));

        await UpdateOkAsync(owner, n.Id, new() { ["deviation_record"] = new List<Guid> { first } });
        AssertOk(await owner.RouteAsync(n.Id, "completed"));
    }

    [Fact] // DEV-002, DEV-003, INV-119
    public async Task Deviations_expire_at_their_end_date_and_the_owner_is_notified()
    {
        var (id, activated) = await NewDeviationAsync("DEV002-P1", "material");
        AssertOk(activated);
        using (var s = fx.As("cwu"))
            await UpdateOkAsync(s, id, new() { ["valid_from"] = BusinessCalendar.Today.AddDays(-20), ["valid_to"] = BusinessCalendar.Today.AddDays(-1) });

        using var job = fx.Services.CreateScope();
        job.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("test");
        await job.ServiceProvider.GetServices<IScheduledJob>().OfType<DeviationExpiryJob>().Single()
            .RunAsync(new JobContext(job.ServiceProvider, true), CancellationToken.None);

        var snap = await job.LoadAsync(id);
        Assert.Equal("expired", snap.PhaseKey);
        Assert.Equal("end_date", snap.KeywordKey("closure_reason"));
        Assert.Empty(await job.ServiceProvider.GetRequiredService<ActiveDeviations>().ForPartAsync("DEV002-P1"));
        var cwu = job.UserId("cwu");
        Assert.True(await job.Db().Notifications.AnyAsync(n => n.RecordId == id && n.UserId == cwu && n.Title.Contains("expired")));
    }

    // ------------------------------------------------------------------ RMA and CAPA

    [Fact] // INV-066, RMA-001, RMA-003
    public async Task Received_quantities_need_an_override_with_justification_above_authorization()
    {
        using var s = fx.As("jlee");
        var r = await s.CreateAsync("rma", new()
        {
            ["subject"] = "Return test", ["owner"] = s.UserId("jlee"), ["rma_reason"] = "defective", ["description"] = "Cracked.",
        }, rows: new() { ["rma_parts"] = [new() { ["part_number"] = "AS-1", ["qty_authorized"] = 4m, ["unit_value"] = 18.5m }] });
        var snap = await s.LoadAsync(r.Id);
        Assert.Equal(74m, snap.Rows("rma_parts").Single().Values.Number("line_total"));
        Assert.Equal(74m, snap.Number("authorized_total"));

        AssertOk(await s.RouteAsync(r.Id, "initial_approval", assignees: [s.UserId("sortiz")]));
        using (var qa = fx.As("sortiz")) AssertOk(await qa.RouteAsync(r.Id, "receiving_inspection"));
        snap = await s.LoadAsync(r.Id);
        var receipt = Assert.Single(snap.Rows("rma_receipts")); // parts list loaded (RMA-003)
        Assert.Equal("AS-1", receipt.Values.Text("rcv_part_number"));

        async Task<SaveResult> ReceiveAsync(decimal qty, bool? overrideYes = null, string? why = null)
        {
            s.Db().ChangeTracker.Clear();
            var current = await s.LoadAsync(r.Id);
            var data = new RecordData();
            var row = current.Rows("rma_receipts").Single().Clone();
            row.Values.Set("rcv_qty_received", Val.Of(qty));
            data.RowsOf("rma_receipts").Add(row);
            if (overrideYes != null) data.Main.Set("receiving_override", Val.Bool(overrideYes.Value));
            if (why != null) data.Main.Set("override_justification", Val.Of(why));
            return await Records(s).SaveAsync(current, data);
        }

        Assert.True((await ReceiveAsync(4m)).Ok);
        var over = await ReceiveAsync(5m);
        Assert.False(over.Ok);
        Assert.Contains(over.Errors, e => e.Message.Contains("INV-066"));
        Assert.False((await ReceiveAsync(5m, overrideYes: true)).Ok); // override without justification
        Assert.True((await ReceiveAsync(5m, overrideYes: true, why: "Customer sent an extra unit from the same lot.")).Ok);
    }

    [Fact] // CAPA-007
    public async Task Capa_priority_sets_the_default_phase_due_date()
    {
        using var s = fx.As("jlee");
        var cal = s.ServiceProvider.GetRequiredService<BusinessCalendar>();
        var capa = await s.CreateAsync("capa", new()
        {
            ["subject"] = "Priority due test", ["capa_type"] = "corrective", ["source"] = "internal_audit", ["priority"] = "critical",
            ["owner"] = s.UserId("jlee"), ["description"] = "Critical problem.",
        });
        var snap = await s.LoadAsync(capa.Id);
        Assert.Equal(await cal.AddAsync(BusinessCalendar.Today, 2, true), snap.Record.DueDate); // half of the 3-day Initiate phase, rounded up

        // A priority that contradicts the risk rating needs a justification.
        s.Db().ChangeTracker.Clear();
        var res = await Records(s).UpdateAsync(capa.Id, new() { ["risk_severity"] = "minor", ["risk_occurrence"] = "remote" });
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.FieldKey == "priority_justification");
        await UpdateOkAsync(s, capa.Id, new() { ["risk_severity"] = "minor", ["risk_occurrence"] = "remote", ["priority_justification"] = "Customer escalation." });
        Assert.Equal("low", (await s.LoadAsync(capa.Id)).KeywordKey("suggested_priority"));
    }
}
