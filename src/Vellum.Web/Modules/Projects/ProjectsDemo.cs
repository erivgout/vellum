using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Kit;
using Vellum.Packaging;

namespace Vellum.Modules.Projects;

/// <summary>Demo data: the VX-300 pump NPI and the Galway line 2 transfer, recurring EHS checks, and minuted meetings.</summary>
public static class ProjectsDemo
{
    private static async Task<Record> TaskAsync(DemoContext d, Guid parent, string subject, string assignee, string stage, int startDays, int dueDays,
        string? outcome = null, string? deliverable = null, string priority = "medium", string creator = "akim")
    {
        var t = await d.CreateAsync("task", new()
        {
            ["subject"] = subject, ["assignee"] = d.User(assignee), ["stage"] = stage, ["priority"] = priority,
            ["start_date"] = DemoContext.Days(startDays), ["due_date"] = DemoContext.Days(dueDays), ["deliverable"] = deliverable,
            ["percent_complete"] = outcome != null ? 100m : 0m,
        }, creator, parent: parent, daysAgo: Math.Max(0, -startDays));
        if (outcome != null)
        {
            await d.UpdateAsync(t.Id, new() { ["outcome"] = outcome }, assignee);
            await d.RouteAsync(t.Id, "completed", asUser: assignee);
        }
        return t;
    }

    /// <summary>Append a gate review row (keeping the existing rows).</summary>
    private static async Task GateReviewAsync(DemoContext d, Guid project, string stage, int daysAgo, string decision, string summary, string? conditions = null)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.As("akim");
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(project);
        var data = new RecordData();
        var rows = data.RowsOf("gate_reviews");
        rows.AddRange(snap.Rows("gate_reviews").Select(r => r.Clone()));
        var bag = new ValueBag();
        foreach (var (k, v) in new (string, object?)[] { ("gate", stage), ("review_date", DemoContext.Days(-daysAgo)), ("decision", decision), ("summary", summary), ("conditions", conditions) })
            bag.Set(k, FieldCodec.FromObject(snap.Form.Fields.First(f => f.Key == k), v, snap.Config));
        rows.Add(new RowBag { IsNew = true, Order = rows.Count, Values = bag });
        var res = await records.SaveAsync(snap, data, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException("Demo gate review failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    /// <summary>Every gate approver signs the gate (voting, 100 %).</summary>
    private static async Task PassGateAsync(DemoContext d, Guid project, string next, params string[] approvers)
    {
        foreach (var a in approvers) await d.RouteAsync(project, next, asUser: a);
    }

    public static async Task SeedAsync(DemoContext d)
    {
        await VxAsync(d);
        await GalwayAsync(d);
        await RecurringAsync(d);
        await MeetingsAsync(d);
    }

    private static async Task<Record> VxAsync(DemoContext d)
    {
        var vx = await d.CreateAsync("project", new()
        {
            ["project_name"] = "VX-300 large-volume infusion pump", ["project_code"] = "NPI-2026-03", ["project_type"] = "npi",
            ["product"] = "VX-300 dual-channel LVP with wireless drug library", ["site"] = "kzo",
            ["project_manager"] = d.User("akim"), ["sponsor"] = d.User("lgarcia"),
            ["gate_approvers"] = new[] { d.User("akim"), d.User("lgarcia"), d.User("sortiz") },
            ["team"] = new[] { d.User("jlee"), d.User("cwu"), d.User("rnovak"), d.User("ebaker"), d.User("pshah") },
            ["start_date"] = DemoContext.Days(-150), ["target_launch"] = DemoContext.Days(210),
            ["objectives"] = "Launch a dual-channel large-volume pump to replace the VX-200, with a wireless drug library, ±5 % flow accuracy and a 30 % lower pumping-mechanism cost. 510(k) with VX-200 as the predicate.",
            ["business_case"] = "Replaces 18,000 installed VX-200 channels reaching end of support in 2028. Payback 2.6 years at the target cost.",
            ["status_summary"] = "Development on plan except the second-source pump head (supplier audit slipped two weeks). Pilot build 1 scheduled.",
        }, "akim", daysAgo: 150);

        await TaskAsync(d, vx.Id, "Voice of customer summary from 14 hospital pump users", "jlee", "concept", -148, -125, "VOC report VOC-VX300-01 approved; top needs: dual channel, fewer nuisance alarms.", "VOC-VX300-01");
        await TaskAsync(d, vx.Id, "Business case and target cost model", "akim", "concept", -148, -122, "Approved by the portfolio board.", "BC-NPI-2026-03");
        await GateReviewAsync(d, vx.Id, "concept", 118, "go", "VOC, business case and regulatory pathway (510(k), predicate VX-200) reviewed.");
        await PassGateAsync(d, vx.Id, "feasibility", "akim", "lgarcia", "sortiz");

        await TaskAsync(d, vx.Id, "Design input requirements DIR-VX300 rev A", "jlee", "feasibility", -115, -80, "DIR-VX300 rev A approved with 212 requirements.", "DIR-VX300 rev A");
        await TaskAsync(d, vx.Id, "Preliminary hazard analysis (ISO 14971)", "jlee", "feasibility", -110, -70, "PHA-VX300 complete; 4 hazards rated high, all with planned design controls.", "PHA-VX300");
        await TaskAsync(d, vx.Id, "Pump-head supplier RFQ and capability survey", "cwu", "feasibility", -100, -60, "Two suppliers surveyed: Precision Fluidics (primary) and Hartwell (second source).");
        await GateReviewAsync(d, vx.Id, "feasibility", 52, "go_conditions", "Design inputs approved, PHA complete, two pump-head suppliers surveyed.", "Second-source pump head qualified before the DV build.");
        await PassGateAsync(d, vx.Id, "development", "akim", "lgarcia", "sortiz");

        await TaskAsync(d, vx.Id, "Software requirements specification SRS-VX300 (IEC 62304 class C)", "jlee", "development", -50, -20, "SRS-VX300 rev B baselined.", "SRS-VX300 rev B");
        await TaskAsync(d, vx.Id, "Regulatory strategy and FDA pre-submission request", "rnovak", "development", -48, -15, "Q-Sub Q26-0114 submitted; meeting granted for next month.", "Q26-0114");
        await TaskAsync(d, vx.Id, "Design FMEA for the pumping mechanism", "akim", "development", -30, 12, priority: "high");
        var second = await TaskAsync(d, vx.Id, "Qualify the second-source pump-head supplier (Feasibility gate condition)", "cwu", "development", -45, -6, priority: "high");
        await TaskAsync(d, vx.Id, "Pilot build 1: 10 units on the Kalamazoo pilot line", "ebaker", "development", -10, 30);
        await d.CommentAsync(second.Id, "Hartwell audit moved to next Tuesday; PPAP level 3 package expected the week after.", "cwu");
        return vx;
    }

    private static async Task GalwayAsync(DemoContext d)
    {
        var gal = await d.CreateAsync("project", new()
        {
            ["project_name"] = "Galway line 2 transfer: ST-40 sterile tubing sets", ["project_code"] = "TRF-2026-01", ["project_type"] = "transfer",
            ["product"] = "ST-40 sterile tubing set (transfer from Kalamazoo line 6)", ["site"] = "gal",
            ["project_manager"] = d.User("akim"), ["sponsor"] = d.User("dokafor"),
            ["gate_approvers"] = new[] { d.User("akim"), d.User("dokafor"), d.User("hmoreau") },
            ["team"] = new[] { d.User("hmoreau"), d.User("kpatel"), d.User("pshah"), d.User("cwu") },
            ["start_date"] = DemoContext.Days(-200), ["target_launch"] = DemoContext.Days(45),
            ["objectives"] = "Transfer ST-40 production to a new line 2 in Galway with equivalent process capability (Cpk ≥ 1.33 on seal strength) and no change to the device master record beyond the site.",
            ["business_case"] = "Frees Kalamazoo line 6 for VX-300 consumables and brings EU supply closer to customers.",
            ["status_summary"] = "Validation: IQ and OQ complete. PQ lot 1 passed; lots 2 and 3 this month. DMR transfer behind schedule.",
        }, "akim", daysAgo: 200);

        await TaskAsync(d, gal.Id, "Transfer business case and capacity model", "akim", "concept", -198, -185, "Approved by the operations leadership team.");
        await GateReviewAsync(d, gal.Id, "concept", 184, "go", "Business case and capacity model approved.");
        await PassGateAsync(d, gal.Id, "feasibility", "akim", "dokafor", "hmoreau");

        await TaskAsync(d, gal.Id, "Equipment specification and supplier selection for line 2", "akim", "feasibility", -180, -140, "Sealer and leak tester ordered from the Kalamazoo line 6 suppliers.");
        await TaskAsync(d, gal.Id, "Process FMEA transfer review", "hmoreau", "feasibility", -175, -135, "PFMEA-ST40 rev G reviewed for the Galway layout; 3 new failure modes added.", "PFMEA-ST40 rev G");
        await GateReviewAsync(d, gal.Id, "feasibility", 130, "go", "Equipment specified, PFMEA reviewed for the new site.");
        await PassGateAsync(d, gal.Id, "development", "akim", "dokafor", "hmoreau");

        await TaskAsync(d, gal.Id, "Install and commission line 2 equipment", "akim", "development", -125, -70, "Line 2 commissioned; FAT and SAT reports filed.");
        await TaskAsync(d, gal.Id, "Draft line 2 work instructions", "pshah", "development", -100, -65, "WI-GAL-L2-001 to 009 released as drafts for validation.");
        await GateReviewAsync(d, gal.Id, "development", 60, "go", "Line installed and commissioned; work instructions ready for validation.");
        await PassGateAsync(d, gal.Id, "validation", "akim", "dokafor", "hmoreau");

        await TaskAsync(d, gal.Id, "IQ of the line 2 sealer and leak tester", "hmoreau", "validation", -58, -40, "IQ-GAL-L2-01 approved.", "IQ-GAL-L2-01");
        await TaskAsync(d, gal.Id, "OQ of the line 2 sealer at worst-case temperature and pressure", "hmoreau", "validation", -40, -20, "OQ-GAL-L2-01 approved; seal strength Cpk 1.71 at the corners of the window.", "OQ-GAL-L2-01");
        await TaskAsync(d, gal.Id, "PQ: three consecutive production lots", "hmoreau", "validation", -15, 14, priority: "high");
        await TaskAsync(d, gal.Id, "Operator training and qualification for line 2", "kpatel", "validation", -20, 7);
        await TaskAsync(d, gal.Id, "Transfer DMR, labeling and UDI records to the Galway site", "pshah", "validation", -30, -3, priority: "high");
    }

    private static async Task<Record> ScheduleAsync(DemoContext d, Dictionary<string, object?> values, string owner, DateOnly start, int daysAgo)
    {
        values["start_date"] = start;
        values["owner"] = d.User(owner);
        return await d.CreateAsync("recurring_task", values, owner, daysAgo: daysAgo);
    }

    /// <summary>Run the generator as the scheduler would have on <paramref name="day"/>.</summary>
    private static async Task GenerateOnAsync(DemoContext d, Guid id, DateOnly day)
    {
        d.Services.GetRequiredService<CurrentUser>().UseSystem("Recurring task generator");
        await d.Services.GetRequiredService<RecurringTaskGenerator>().GenerateAsync(id, day);
    }

    private static async Task CompleteOpenTasksAsync(DemoContext d, Guid parent, string outcome, int keepOpen = 1)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        var tasks = (await records.ChildrenAsync(parent, "task")).Where(t => t.PhaseType == PhaseType.Normal).ToList();
        foreach (var t in tasks.Take(Math.Max(0, tasks.Count - keepOpen)))
        {
            var assignee = d.Directory.Get((await records.LoadRequiredAsync(t.Id)).UserId("assignee"))!.Username;
            await d.UpdateAsync(t.Id, new() { ["outcome"] = outcome }, assignee);
            await d.RouteAsync(t.Id, "completed", asUser: assignee);
        }
    }

    private static async Task RecurringAsync(DemoContext d)
    {
        var today = DemoContext.Today;

        // Weekly, with one missed scheduler run caught up (TSK-005).
        var eyewash = await ScheduleAsync(d, new()
        {
            ["subject"] = "Eyewash stations and safety showers check, Kalamazoo", ["recurrence_status"] = "active",
            ["description"] = "Activate all 9 plumbed eyewash stations and 3 safety showers for 3 minutes; check flow, temperature (16–38 °C), signage and access (ANSI Z358.1). Record on form EHS-F-021.",
            ["assignee"] = d.User("mchen"), ["priority"] = "high", ["frequency"] = "weekly", ["interval"] = 1m, ["end_rule"] = "never", ["due_days"] = 1m,
        }, "mchen", today.AddDays(-35), 40);
        foreach (var offset in new[] { -35, -28, -21, -7, 0 }) await GenerateOnAsync(d, eyewash.Id, today.AddDays(offset)); // the -14 run was missed
        await CompleteOpenTasksAsync(d, eyewash.Id, "All stations flowed clear within temperature range; EHS-F-021 filed.");

        // Monthly in Galway.
        var start = new DateOnly(today.AddMonths(-5).Year, today.AddMonths(-5).Month, 3);
        var extinguishers = await ScheduleAsync(d, new()
        {
            ["subject"] = "Monthly fire extinguisher inspection, Galway", ["recurrence_status"] = "active",
            ["description"] = "Visual check of all 46 extinguishers: in place, seal intact, gauge in the green, label legible (IS 291). Tag and date each one.",
            ["assignee"] = d.User("hmoreau"), ["frequency"] = "monthly", ["interval"] = 1m, ["end_rule"] = "never", ["due_days"] = 5m,
        }, "mchen", start, 160);
        for (var k = 0; k <= 5; k++)
        {
            var occ = Recurrence.Occurrence("monthly", 1, start, k);
            if (occ > today) break;
            await GenerateOnAsync(d, extinguishers.Id, occ);
        }
        await CompleteOpenTasksAsync(d, extinguishers.Id, "All extinguishers inspected and tagged; one replaced (low pressure).");

        // Quarterly in Boston, ending after eight occurrences.
        var lighting = await ScheduleAsync(d, new()
        {
            ["subject"] = "Quarterly emergency lighting test, Boston", ["recurrence_status"] = "active",
            ["description"] = "30-second functional test of every emergency light and exit sign (NFPA 101 7.9.3); annual 90-minute test in Q4.",
            ["assignee"] = d.User("ebaker"), ["frequency"] = "monthly", ["interval"] = 3m, ["end_rule"] = "after", ["end_after"] = 8m, ["due_days"] = 10m,
        }, "mchen", today.AddDays(-100), 105);
        await GenerateOnAsync(d, lighting.Id, today.AddDays(-100));
        await GenerateOnAsync(d, lighting.Id, Recurrence.Occurrence("monthly", 3, today.AddDays(-100), 1));
        await CompleteOpenTasksAsync(d, lighting.Id, "All 64 fittings passed; two batteries replaced.");

        // A pilot that ended by itself after three occurrences (TSK-004).
        var pilotStart = today.AddDays(-125);
        var pilot = await ScheduleAsync(d, new()
        {
            ["subject"] = "Forklift pre-use checklist audit (pilot), Kalamazoo", ["recurrence_status"] = "active",
            ["description"] = "Audit 10 completed forklift pre-use checklists against the trucks' actual condition.",
            ["assignee"] = d.User("lgarcia"), ["frequency"] = "monthly", ["interval"] = 1m, ["end_rule"] = "after", ["end_after"] = 3m, ["due_days"] = 7m,
        }, "mchen", pilotStart, 130);
        for (var k = 0; k < 3; k++) await GenerateOnAsync(d, pilot.Id, Recurrence.Occurrence("monthly", 1, pilotStart, k));
        await CompleteOpenTasksAsync(d, pilot.Id, "Checklists matched truck condition; pilot conclusions to the EHS committee.", keepOpen: 0);
    }

    private static DateTime Local(DateOnly day, int hour, string site) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, 0)), Sites.Tz(site));

    private static async Task MeetingsAsync(DemoContext d)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        var vx = await d.Db.Records.Where(r => r.Title == "VX-300 large-volume infusion pump").Select(r => r.Id).FirstOrDefaultAsync();
        var gal = await d.Db.Records.Where(r => r.Title == "Galway line 2 transfer: ST-40 sterile tubing sets").Select(r => r.Id).FirstOrDefaultAsync();

        var review = await d.CreateAsync("meeting", new()
        {
            ["subject"] = "VX-300 design review: pump-head mechanism", ["meeting_type"] = "design_review",
            ["starts_at"] = Local(DemoContext.Days(-20), 14, "kzo"), ["duration_min"] = 90m, ["location"] = "Kalamazoo, Huron conference room / Teams",
            ["chair"] = d.User("akim"), ["minute_taker"] = d.User("jlee"),
            ["invitees"] = new[] { d.User("akim"), d.User("jlee"), d.User("cwu"), d.User("sortiz"), d.User("ebaker") },
            ["project"] = vx == Guid.Empty ? null : vx,
        }, "akim", daysAgo: 25, rows: new()
        {
            ["agenda"] =
            [
                new() { ["topic"] = "Prototype B occlusion test results", ["presenter"] = d.User("jlee"), ["minutes"] = "Downstream occlusion detected in 38–52 s at 25 mL/h; target ≤ 60 s met. Upstream detection still marginal at low flow.", ["decision"] = "Keep design B; tune the upstream sensor threshold." },
                new() { ["topic"] = "Second-source pump head", ["presenter"] = d.User("cwu"), ["minutes"] = "Hartwell samples dimensionally conforming; material cert for the PEEK rotor outstanding.", ["decision"] = "Proceed to supplier audit." },
                new() { ["topic"] = "Design for service", ["presenter"] = d.User("ebaker"), ["minutes"] = "Pump-head swap in under 10 minutes demonstrated without tools." },
            ],
            ["actions"] =
            [
                new() { ["action"] = "Run a 72-hour occlusion endurance test on prototype B at 1 mL/h", ["assignee"] = d.User("jlee"), ["due"] = DemoContext.Days(10), ["agenda_ref"] = "1" },
                new() { ["action"] = "Get Hartwell's PEEK rotor material certification and PPAP level 3 quote", ["assignee"] = d.User("cwu"), ["due"] = DemoContext.Days(5), ["agenda_ref"] = "2" },
            ],
        });
        await d.RouteAsync(review.Id, "minutes");
        await d.UpdateAsync(review.Id, new() { ["absentees"] = new[] { d.User("sortiz") }, ["attendees"] = new[] { d.User("akim"), d.User("jlee"), d.User("cwu"), d.User("ebaker") } }, "jlee");
        await d.RouteAsync(review.Id, "completed");

        var committee = await d.CreateAsync("meeting", new()
        {
            ["subject"] = "Monthly EHS committee, Kalamazoo", ["meeting_type"] = "ehs_committee",
            ["starts_at"] = Local(DemoContext.Days(-9), 9, "kzo"), ["duration_min"] = 60m, ["location"] = "Kalamazoo, training room 2",
            ["chair"] = d.User("mchen"), ["minute_taker"] = d.User("kpatel"),
            ["invitees"] = new[] { d.User("mchen"), d.User("lgarcia"), d.User("akim"), d.User("ebaker"), d.User("nflores"), d.User("kpatel") },
        }, "mchen", daysAgo: 12, rows: new()
        {
            ["agenda"] =
            [
                new() { ["topic"] = "Incident review: shipping dock near miss and Press 4 back strain", ["presenter"] = d.User("mchen"), ["minutes"] = "Both closed with verified actions. Lift assist working well; operators asked for the same at Press 6.", ["decision"] = "Extend the lift assist to Press 6." },
                new() { ["topic"] = "Near-miss reporting rate", ["presenter"] = d.User("mchen"), ["minutes"] = "8 near misses this quarter versus 3 last quarter after the quick-report form went live. Second shift still under-reporting." },
                new() { ["topic"] = "Permit audit: hot work and confined space", ["presenter"] = d.User("ebaker"), ["minutes"] = "10 of 10 permits complete; one fire watch signed 15 minutes early.", ["decision"] = "Fire watch duration reminder at toolbox talks." },
            ],
            ["actions"] =
            [
                new() { ["action"] = "Add forklift and pedestrian separation for the whole dock to the Q1 capex plan", ["assignee"] = d.User("lgarcia"), ["due"] = DemoContext.Days(20), ["agenda_ref"] = "1" },
                new() { ["action"] = "Run a near-miss reporting campaign on second shift", ["assignee"] = d.User("mchen"), ["due"] = DemoContext.Days(14), ["agenda_ref"] = "2" },
                new() { ["action"] = "Install a lift assist at the Press 6 resin loader", ["assignee"] = d.User("ebaker"), ["due"] = DemoContext.Days(30), ["agenda_ref"] = "1" },
            ],
        });
        await d.RouteAsync(committee.Id, "minutes");
        await d.UpdateAsync(committee.Id, new() { ["absentees"] = new[] { d.User("nflores") }, ["attendees"] = new[] { d.User("mchen"), d.User("lgarcia"), d.User("akim"), d.User("ebaker"), d.User("kpatel") } }, "kpatel");
        await d.RouteAsync(committee.Id, "completed");

        await d.CreateAsync("meeting", new()
        {
            ["subject"] = "Galway line 2 PQ readiness review", ["meeting_type"] = "gate_review",
            ["starts_at"] = Local(DemoContext.Days(6), 10, "gal"), ["duration_min"] = 60m, ["location"] = "Galway, Corrib meeting room / Teams",
            ["chair"] = d.User("akim"), ["minute_taker"] = d.User("hmoreau"),
            ["invitees"] = new[] { d.User("akim"), d.User("dokafor"), d.User("hmoreau"), d.User("kpatel"), d.User("pshah") },
            ["project"] = gal == Guid.Empty ? null : gal,
        }, "akim", daysAgo: 3, rows: new()
        {
            ["agenda"] =
            [
                new() { ["topic"] = "PQ lot 1 results and lots 2–3 plan", ["presenter"] = d.User("hmoreau") },
                new() { ["topic"] = "Operator qualification status", ["presenter"] = d.User("kpatel") },
                new() { ["topic"] = "DMR and labeling transfer recovery plan", ["presenter"] = d.User("pshah") },
            ],
        });
    }
}
