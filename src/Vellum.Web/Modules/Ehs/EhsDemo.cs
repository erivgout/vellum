using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Kit;
using Vellum.Packaging;

namespace Vellum.Modules.Ehs;

/// <summary>Demo data for Acme Medical Devices: incidents (mostly near misses and first aid), hours worked, JSAs and work permits.</summary>
public static class EhsDemo
{
    /// <summary>A UTC moment <paramref name="days"/> days and <paramref name="hours"/> hours ago, on a 5-minute boundary.</summary>
    private static DateTime Ago(double days, double hours = 0)
    {
        var t = DateTime.UtcNow.AddDays(-days).AddHours(-hours);
        return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute / 5 * 5, 0, DateTimeKind.Utc);
    }

    private static DateTime Local(DateOnly day, int hour, int minute, string site) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), Sites.Tz(site));

    public static async Task SeedAsync(DemoContext d)
    {
        await HoursAsync(d);
        await IncidentsAsync(d);
        await JsasAsync(d);
        await PermitsAsync(d);
    }

    // ------------------------------------------------------------------ hours worked (EHS-113)

    private static async Task HoursAsync(DemoContext d)
    {
        var first = new DateOnly(DemoContext.Today.Year, DemoContext.Today.Month, 1);
        (string Site, decimal Hours, decimal Employees, string Source)[] sites =
        [
            ("kzo", 68_450m, 412m, "Kalamazoo payroll (ADP) export incl. agency staff"),
            ("bos", 18_920m, 118m, "Boston payroll export"),
            ("gal", 38_360m, 251m, "Galway payroll (Core HR) export incl. contractors under direct supervision"),
        ];
        for (var i = 12; i >= 1; i--)
        {
            var month = first.AddMonths(-i);
            var seasonal = month.Month switch { 12 => 0.91m, 1 => 0.97m, 7 => 0.94m, 8 => 0.95m, _ => 1m };
            foreach (var (site, hours, employees, source) in sites)
            {
                var wobble = 1m + ((month.Month * 37 + site.Length * 11 + site[0]) % 9 - 4) / 100m;
                await d.CreateAsync("hours_worked", new()
                {
                    ["site"] = site, ["period"] = month,
                    ["hours"] = decimal.Round(hours * seasonal * wobble, 0) + 0.50m * (month.Month % 2),
                    ["employees"] = decimal.Round(employees * (1m + (month.Month % 4 - 1) / 100m), 0),
                    ["notes"] = source,
                }, "mchen", daysAgo: Math.Max(1, DemoContext.Today.DayNumber - month.AddMonths(1).AddDays(6).DayNumber));
            }
        }
    }

    // ------------------------------------------------------------------ incidents

    private static async Task CompleteActionsAsync(DemoContext d, Guid parent, string taken)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        foreach (var ai in await records.ChildrenAsync(parent, "action_item"))
        {
            if (ai.PhaseType != PhaseType.Normal) continue;
            var snap = await records.LoadRequiredAsync(ai.Id);
            var doer = d.Directory.Get(snap.UserId("assignee"))!.Username;
            await d.UpdateAsync(ai.Id, new() { ["action_taken"] = taken, ["completed_on"] = DemoContext.Days(-3) }, doer);
            if (snap.Bool("verification_required") == true)
            {
                await d.RouteAsync(ai.Id, "verification");
                var verifier = d.Directory.Get(snap.UserId("verifier"))!.Username;
                await d.UpdateAsync(ai.Id, new() { ["verification_result"] = "verified", ["verification_comments"] = "Checked on the floor during the weekly EHS walk." }, verifier);
            }
            await d.RouteAsync(ai.Id, "completed");
        }
    }

    private static async Task IncidentsAsync(DemoContext d)
    {
        var records = d.Services.GetRequiredService<RecordService>();

        // 1. Near miss, closed: forklift reversing towards the pedestrian walkway.
        var nm1 = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Forklift reversed across the pedestrian walkway at the shipping dock",
            ["incident_type"] = "near_miss", ["occurred_at"] = Ago(75, 10), ["site"] = "kzo", ["area"] = "Warehouse, shipping dock door 4",
            ["outcome"] = "none", ["potential_severity"] = "high", ["supervisor"] = d.User("lgarcia"),
            ["description"] = "A reach truck reversed out of aisle 12 across the marked walkway while an operator was walking to the break room. The operator stepped back; the truck stopped about 1 m away. The reversing alarm was working but is hard to hear over the dock compressor.",
            ["immediate_actions"] = "Driver stopped and checked on the operator. Supervisor coned off the crossing for the rest of the shift.",
            ["area_made_safe"] = true, ["suggestion"] = "Blue-spot lights on the trucks and a physical barrier where the walkway meets aisle 12.",
            ["people_involved"] = new[] { d.User("nflores") },
        }, "nflores", daysAgo: 75, rows: new()
        {
            ["witnesses"] = [new() { ["witness"] = "Noel Flores", ["statement"] = "I was walking to the break room inside the yellow lines when the truck started reversing. I didn't hear it until it was close." }],
        });
        await d.RouteAsync(nm1.Id, "investigation");
        await d.UpdateAsync(nm1.Id, new()
        {
            ["root_cause_category"] = "environment", ["root_cause"] = "The walkway crosses an active aisle with no physical separation, and dock compressor noise masks the reversing alarm.",
            ["contributing_factors"] = "Peak shipping hour; driver focused on the load.", ["corrective_action_required"] = true, ["reviewer"] = d.User("lgarcia"),
            ["recordable"] = false, ["recordability_notes"] = "Near miss, no injury.",
        }, "mchen");
        await AddActionsAsync(d, nm1.Id,
            ("Fit blue-spot warning lights to all 6 reach trucks", "ebaker", 15m),
            ("Install a swing gate and floor barrier where the walkway crosses aisle 12", "akim", 20m));
        await d.RouteAsync(nm1.Id, "corrective_actions");
        await CompleteActionsAsync(d, nm1.Id, "Installed and checked with the warehouse team; photos in the EHS share.");
        await d.RouteAsync(nm1.Id, "review");
        await d.UpdateAsync(nm1.Id, new() { ["review_comments"] = "Good catch by Noel. Barrier and lights verified on the floor.", ["lessons_learned"] = "Separate people and trucks physically where they cross; alarms alone aren't enough in noisy areas." }, "lgarcia");
        await d.RouteAsync(nm1.Id, "closed", comment: "Closed after floor verification.");

        // 2. Recordable lost-time injury, closed: back strain lifting resin bags.
        var inj = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Lower back strain lifting 25 kg resin bags into the hopper loader",
            ["incident_type"] = "injury", ["occurred_at"] = Ago(61, 10), ["site"] = "kzo", ["area"] = "Molding hall, Press 4 resin loader",
            ["outcome"] = "lost_time", ["supervisor"] = d.User("akim"),
            ["description"] = "A material handler felt sharp pain in the lower back while lifting a 25 kg polycarbonate bag from floor level into the hopper loader. He stopped work and reported to the supervisor.",
            ["immediate_actions"] = "Ice pack from first aid; taken to the occupational health clinic the same afternoon.",
            ["area_made_safe"] = true,
        }, "akim", daysAgo: 61);
        await d.RouteAsync(inj.Id, "investigation");
        await d.UpdateAsync(inj.Id, new()
        {
            ["injured_category"] = "employee", ["injured_name"] = "Marcus Reed", ["injured_job_title"] = "Material handler",
            ["body_part"] = new[] { "back" }, ["injury_nature"] = "strain", ["medical_treatment"] = "clinic", ["treatment_provider"] = "Bronson Occupational Health, Kalamazoo",
            ["recordable"] = true, ["osha_classification"] = "days_away", ["osha_case_type"] = "injury", ["days_away"] = 9m, ["days_restricted"] = 5m,
            ["recordability_notes"] = "Physician prescribed 9 days off work followed by 5 days of light duty: recordable, days-away case (29 CFR 1904.7(b)(3)).",
            ["root_cause_category"] = "method", ["root_cause"] = "Bags are lifted from floor level with a twist into the loader; no lift assist at this station.",
            ["corrective_action_required"] = true, ["reviewer"] = d.User("lgarcia"),
        }, "mchen");
        await AddActionsAsync(d, inj.Id,
            ("Install a vacuum lift assist at the Press 4 resin loading station", "ebaker", 20m),
            ("Store resin bags on a pallet raiser at waist height", "akim", 10m),
            ("Refresher training on manual handling for molding material handlers", "kpatel", 15m));
        await d.RouteAsync(inj.Id, "corrective_actions");
        await CompleteActionsAsync(d, inj.Id, "Done; lift assist commissioned and training records filed.");
        await d.RouteAsync(inj.Id, "review");
        await d.UpdateAsync(inj.Id, new() { ["review_comments"] = "Lift assist in use on all shifts. Marcus back on full duties.", ["lessons_learned"] = "Review every station where bags over 23 kg are lifted manually." }, "lgarcia");
        await d.RouteAsync(inj.Id, "closed");

        // 3. Recordable (other recordable), closed: laceration needing sutures in Boston.
        var cut = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Laceration to the left palm from a utility knife while opening a sterile barrier pouch case",
            ["incident_type"] = "injury", ["occurred_at"] = Ago(108, 10), ["site"] = "bos", ["area"] = "Receiving, kitting bench 2",
            ["outcome"] = "medical", ["supervisor"] = d.User("tbrooks"),
            ["description"] = "While cutting the strapping on a case of sterile barrier pouches the knife slipped and cut the operator's left palm. The cut needed four sutures at the urgent care clinic.",
            ["immediate_actions"] = "Pressure dressing applied by first aider; driven to urgent care.",
        }, "tbrooks", daysAgo: 108);
        await d.RouteAsync(cut.Id, "investigation");
        await d.UpdateAsync(cut.Id, new()
        {
            ["injured_category"] = "employee", ["injured_name"] = "Aisha Rahman", ["injured_job_title"] = "Kitting technician",
            ["body_part"] = new[] { "hand" }, ["injury_nature"] = "laceration", ["medical_treatment"] = "clinic", ["treatment_provider"] = "CareWell Urgent Care, Boston",
            ["recordable"] = true, ["osha_classification"] = "other_recordable", ["osha_case_type"] = "injury", ["days_away"] = 0m, ["days_restricted"] = 0m,
            ["recordability_notes"] = "Sutures are medical treatment beyond first aid; no days away or restriction.",
            ["root_cause_category"] = "machine", ["root_cause"] = "Open-blade utility knives are still used at receiving.",
            ["corrective_action_required"] = true, ["reviewer"] = d.User("dokafor"),
        }, "mchen");
        await AddActionsAsync(d, cut.Id, ("Replace all open-blade knives at receiving with auto-retracting safety cutters", "tbrooks", 7m));
        await d.RouteAsync(cut.Id, "corrective_actions");
        await CompleteActionsAsync(d, cut.Id, "24 safety cutters issued; old knives collected and destroyed.");
        await d.RouteAsync(cut.Id, "review");
        await d.UpdateAsync(cut.Id, new() { ["review_comments"] = "Safety cutters rolled out at both US sites.", ["lessons_learned"] = "Ban open-blade knives site-wide." }, "dokafor");
        await d.RouteAsync(cut.Id, "closed");

        // 4. Recordable DART case awaiting management review: wrist tendinitis (restricted work).
        var msd = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Wrist tendinitis from repetitive crimping of tubing connectors",
            ["incident_type"] = "injury", ["occurred_at"] = Ago(34, 10), ["site"] = "kzo", ["area"] = "Final assembly, ST-40 tubing set cell",
            ["outcome"] = "restricted", ["supervisor"] = d.User("akim"),
            ["description"] = "An assembler reported increasing pain in the right wrist over two weeks of manual crimping on the ST-40 cell. Diagnosed with tendinitis by the clinic.",
        }, "akim", daysAgo: 34);
        await d.RouteAsync(msd.Id, "investigation");
        await d.UpdateAsync(msd.Id, new()
        {
            ["injured_category"] = "employee", ["injured_name"] = "Hannah Walsh", ["injured_job_title"] = "Assembler",
            ["body_part"] = new[] { "wrist" }, ["injury_nature"] = "msd", ["medical_treatment"] = "clinic",
            ["recordable"] = true, ["osha_classification"] = "restricted", ["osha_case_type"] = "injury", ["days_away"] = 0m, ["days_restricted"] = 12m,
            ["recordability_notes"] = "Physician restricted repetitive hand work for 12 days: job transfer or restriction case.",
            ["root_cause_category"] = "method", ["root_cause"] = "Hand crimping at high cycle rate with no job rotation.",
            ["corrective_action_required"] = false, ["reviewer"] = d.User("lgarcia"),
            ["contributing_factors"] = "Pneumatic crimp tool already on order under CAPA; rotation introduced the same week.",
        }, "mchen");
        await d.RouteAsync(msd.Id, "review");

        // 5. First aid, closed: box cutter nick.
        var fa1 = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Small cut on a finger while opening a carton of labels",
            ["incident_type"] = "injury", ["occurred_at"] = Ago(41, 10), ["site"] = "kzo", ["area"] = "Packaging line 1",
            ["outcome"] = "first_aid", ["supervisor"] = d.User("lgarcia"),
            ["description"] = "Operator nicked the index finger with the edge of a carton flap. Cleaned and covered with a plaster from the first-aid kit; returned to work.",
            ["immediate_actions"] = "First aid: cleaned and plaster applied.",
        }, "nflores", daysAgo: 41);
        await d.RouteAsync(fa1.Id, "investigation");
        await d.UpdateAsync(fa1.Id, new()
        {
            ["injured_category"] = "employee", ["injured_name"] = "Noel Flores", ["injured_job_title"] = "Production operator",
            ["body_part"] = new[] { "hand" }, ["injury_nature"] = "laceration", ["medical_treatment"] = "first_aid",
            ["recordable"] = false, ["recordability_notes"] = "First aid only (29 CFR 1904.7(b)(5)(ii)): not recordable.",
            ["root_cause_category"] = "material", ["root_cause"] = "Sharp carton flap edge; gloves not required for this task.",
            ["corrective_action_required"] = false, ["reviewer"] = d.User("lgarcia"),
        }, "mchen");
        await d.RouteAsync(fa1.Id, "review");
        await d.UpdateAsync(fa1.Id, new() { ["review_comments"] = "Minor. Cut-resistant gloves now available at packaging." }, "lgarcia");
        await d.RouteAsync(fa1.Id, "closed");

        // 6. First aid, closed: IPA splash in Galway.
        var fa2 = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Isopropyl alcohol splashed into an eye while refilling a wash bottle",
            ["incident_type"] = "injury", ["occurred_at"] = Ago(23, 10), ["site"] = "gal", ["area"] = "QC laboratory, solvent cabinet",
            ["outcome"] = "first_aid",
            ["description"] = "While refilling a 500 mL wash bottle from a 5 L container, IPA splashed into the analyst's left eye. Eyewash used for 15 minutes; mild irritation that cleared within the hour.",
            ["immediate_actions"] = "Eyewash station for 15 minutes; checked by the site first aider.",
        }, "hmoreau", daysAgo: 23);
        await d.RouteAsync(fa2.Id, "investigation");
        await d.UpdateAsync(fa2.Id, new()
        {
            ["injured_category"] = "employee", ["injured_name"] = "Hannah Moreau", ["injured_job_title"] = "QC laboratory analyst",
            ["body_part"] = new[] { "eye" }, ["injury_nature"] = "chemical", ["medical_treatment"] = "first_aid",
            ["recordable"] = false, ["recordability_notes"] = "Eye flushing is first aid; no medical treatment.",
            ["root_cause_category"] = "method", ["root_cause"] = "Decanting from a 5 L container without a pump; safety glasses instead of goggles.",
            ["corrective_action_required"] = true, ["reviewer"] = d.User("dokafor"),
        }, "mchen");
        await AddActionsAsync(d, fa2.Id, ("Fit a dispensing pump to solvent containers and require goggles for decanting", "hmoreau", 10m));
        await d.RouteAsync(fa2.Id, "corrective_actions");
        await CompleteActionsAsync(d, fa2.Id, "Pump fitted; goggles at the solvent cabinet; LAB-SOP-031 updated.");
        await d.RouteAsync(fa2.Id, "review");
        await d.UpdateAsync(fa2.Id, new() { ["review_comments"] = "Simple, effective fix." }, "dokafor");
        await d.RouteAsync(fa2.Id, "closed");

        // 7. Environmental, closed: hydraulic oil leak at the dock.
        var env = await d.CreateAsync("incident", new()
        {
            ["subject"] = "About 20 L of hydraulic oil leaked from a dock leveller onto the yard",
            ["incident_type"] = "environmental", ["occurred_at"] = Ago(52, 10), ["site"] = "kzo", ["area"] = "Shipping yard, dock leveller 2",
            ["outcome"] = "environmental_release", ["supervisor"] = d.User("lgarcia"),
            ["description"] = "A hydraulic hose on dock leveller 2 failed. Roughly 20 L of oil reached the asphalt; none reached the storm drain 8 m away.",
            ["immediate_actions"] = "Spill kit deployed, drain covered, contaminated absorbent bagged as waste.", ["area_made_safe"] = true,
        }, "ebaker", daysAgo: 52);
        await d.RouteAsync(env.Id, "investigation");
        await UpdateWithRowsAsync(d, env.Id, new()
        {
            ["recordable"] = false, ["root_cause_category"] = "machine", ["root_cause"] = "Hose past its replacement age; leveller not in the PM program.",
            ["corrective_action_required"] = true, ["reviewer"] = d.User("lgarcia"),
        }, "mchen", rowsCosts: [("Spill kit refill and absorbents", 410m), ("Licensed waste disposal (2 drums)", 760m), ("Replacement hydraulic hose and labour", 520m)]);
        await AddActionsAsync(d, env.Id, ("Add all dock levellers to the preventive maintenance program", "ebaker", 15m));
        await d.RouteAsync(env.Id, "corrective_actions");
        await CompleteActionsAsync(d, env.Id, "Three levellers added to PM with 6-monthly hose inspection.");
        await d.RouteAsync(env.Id, "review");
        await UpdateWithRowsAsync(d, env.Id, new() { ["review_comments"] = "Contained well. No reportable release." }, "lgarcia");
        await d.RouteAsync(env.Id, "closed");

        // 8. Near miss in Corrective Actions: unguarded spindle after maintenance (one action overdue).
        var nm2 = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Lathe restarted after maintenance with the chuck guard interlock bypassed",
            ["incident_type"] = "near_miss", ["occurred_at"] = Ago(19, 10), ["site"] = "kzo", ["area"] = "Machining, CNC lathe L-3",
            ["outcome"] = "none", ["potential_severity"] = "catastrophic", ["supervisor"] = d.User("akim"),
            ["description"] = "After a spindle bearing repair the lathe was handed back to production with the chuck guard interlock jumpered out. The operator noticed the spindle would run with the guard open and stopped before machining.",
            ["immediate_actions"] = "Machine locked out; interlock jumper removed and function tested.", ["area_made_safe"] = true,
        }, "nflores", daysAgo: 19);
        await d.RouteAsync(nm2.Id, "investigation");
        await UpdateWithRowsAsync(d, nm2.Id, new()
        {
            ["recordable"] = false, ["root_cause_category"] = "method",
            ["root_cause"] = "The maintenance hand-back checklist doesn't require a functional test of safety interlocks.",
            ["corrective_action_required"] = true, ["reviewer"] = d.User("lgarcia"),
        }, "mchen", whys: [("Why did the lathe run with the guard open?", "The interlock was jumpered for fault finding."),
            ("Why wasn't the jumper removed?", "The repair ran over a shift change."), ("Why wasn't it caught at hand-back?", "The hand-back checklist has no interlock test.")]);
        await AddActionsAsync(d, nm2.Id,
            ("Add a safety interlock functional test to the maintenance hand-back checklist MNT-F-004", "ebaker", 5m),
            ("Introduce controlled bypass tags with shift-handover sign-off", "mchen", 15m));
        await d.RouteAsync(nm2.Id, "corrective_actions");
        var nm2Items = await records.ChildrenAsync(nm2.Id, "action_item");
        if (nm2Items.Count > 0)
        {
            await d.UpdateAsync(nm2Items[0].Id, new() { ["action_taken"] = "MNT-F-004 rev C issued with a mandatory interlock test.", ["completed_on"] = DemoContext.Days(-4) }, "ebaker");
            await d.RouteAsync(nm2Items[0].Id, "completed");
        }
        if (nm2Items.Count > 1) await d.SetDueAsync(nm2Items[1].Id, DemoContext.Days(-2));

        // 9. Near miss in Investigation (Boston cleanroom).
        var nm3 = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Unlabelled bottle of clear liquid found in the cleanroom B gowning area",
            ["incident_type"] = "near_miss", ["occurred_at"] = Ago(6, 10), ["site"] = "bos", ["area"] = "Cleanroom B, gowning room",
            ["outcome"] = "none", ["potential_severity"] = "moderate",
            ["description"] = "A 1 L wash bottle with no label was left on the gowning bench. It turned out to be 70% IPA, but someone could have used it to rinse their eyes or hands.",
            ["immediate_actions"] = "Bottle removed and disposed of; area checked for other unlabelled containers.",
        }, "dokafor", daysAgo: 6);
        await d.RouteAsync(nm3.Id, "investigation");

        // 10. Property damage in Investigation (Boston warehouse).
        var pd = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Forklift struck a pallet rack upright in the Boston warehouse",
            ["incident_type"] = "property_damage", ["occurred_at"] = Ago(4, 10), ["site"] = "bos", ["area"] = "Warehouse, rack row C, bay 7",
            ["outcome"] = "damage",
            ["description"] = "While turning into row C a counterbalance truck hit the front upright of bay 7. The upright is bent about 15 mm; the rack was loaded with finished goods.",
            ["immediate_actions"] = "Bay offloaded and taped off; rack inspector called.", ["area_made_safe"] = true,
        }, "tbrooks", daysAgo: 4);
        await d.RouteAsync(pd.Id, "investigation");

        // 11. Near miss in Report (Galway, just reported).
        await d.CreateAsync("incident", new()
        {
            ["subject"] = "Compressed air hose whipped loose from its coupling on line 1",
            ["incident_type"] = "near_miss", ["occurred_at"] = Ago(1, 6), ["site"] = "gal", ["area"] = "Line 1, sealing station",
            ["outcome"] = "none", ["potential_severity"] = "high",
            ["description"] = "A 6 bar air hose came off its quick coupling and whipped across the walkway at head height. Nobody was in the way.",
            ["immediate_actions"] = "Air isolated at the drop; hose and coupling tagged out.", ["suggestion"] = "Whip checks on all hoses over 10 mm bore.",
        }, "hmoreau", daysAgo: 1);

        // 12. Anonymous near miss (quick report form) with EHS.
        var me = d.Services.GetRequiredService<CurrentUser>();
        me.UseAnonymousReporter("Near-miss quick report");
        d.Db.ChangeTracker.Clear();
        var anon = await records.CreateAsync(new CreateRequest
        {
            FormKey = "incident", StartPhaseKey = "investigation", Assignees = [d.User("mchen")], SkipAccessCheck = true,
            CreatedById = d.Directory.AnonymousReporterId, CreatedAt = DateTime.UtcNow.AddDays(-3),
            CreateComment = "Anonymous near-miss report (quick report form)",
            Values = new()
            {
                ["subject"] = "Boxes stacked above shoulder height leaning out of the top shelf",
                ["incident_type"] = "near_miss", ["occurred_at"] = Ago(3, 2), ["site"] = "bos", ["area"] = "Warehouse, consumables shelving",
                ["outcome"] = "none", ["potential_severity"] = "moderate",
                ["description"] = "Cartons of gowning supplies are stacked three high on the top shelf and lean outwards. One fell when I took the box below it.",
                ["suggestion"] = "Keep the top shelf for light items only, or add a lip.", ["anonymous"] = true,
            },
        });
        if (!anon.Ok) throw new InvalidOperationException("Demo anonymous near miss failed: " + string.Join("; ", anon.Errors.Select(e => e.Message)));

        // 13. Serious injury today: in-patient hospitalization, 24-hour OSHA clock running, not yet acknowledged.
        var serious = await d.CreateAsync("incident", new()
        {
            ["subject"] = "Operator's forearm caught between the mold halves of Press 7 during a quality check",
            ["incident_type"] = "injury", ["occurred_at"] = Ago(0, 3.1), ["site"] = "kzo", ["area"] = "Molding hall, Press 7 (Engel 220 t)",
            ["outcome"] = "serious", ["osha_severe_event"] = "inpatient", ["supervisor"] = d.User("akim"),
            ["description"] = "During a part check the operator reached past the light curtain from the non-operator side while the press was closing. The forearm was caught between the mold halves before the press reversed. Taken by ambulance to Bronson Methodist Hospital and admitted overnight for observation and suspected fracture.",
            ["immediate_actions"] = "Emergency stop, first aid, 911 called. Press 7 locked out and the area barricaded; nothing moved for the investigation.",
            ["area_made_safe"] = true, ["people_involved"] = new[] { d.User("akim"), d.User("nflores") },
        }, "lgarcia", rows: new()
        {
            ["witnesses"] =
            [
                new() { ["witness"] = "Noel Flores", ["statement"] = "I was at Press 6. I heard a shout, hit the e-stop on Press 7 and called the supervisor." },
                new() { ["witness"] = "Alex Kim", ["statement"] = "When I arrived the press was open and stopped. The rear guard door was open." },
            ],
        });
        await d.RouteAsync(serious.Id, "investigation", comment: "Hospital admission confirmed by phone 30 minutes ago. OSHA 24-hour report required.");
    }

    /// <summary>Set investigation fields with optional 5-whys and cost rows.</summary>
    private static async Task UpdateWithRowsAsync(DemoContext d, Guid id, Dictionary<string, object?> values, string asUser,
        (string Why, string Because)[]? whys = null, (string Item, decimal Amount)[]? rowsCosts = null)
    {
        await d.UpdateAsync(id, values, asUser);
        if (whys == null && rowsCosts == null) return;
        var records = d.Services.GetRequiredService<RecordService>();
        d.As(asUser);
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(id);
        var data = new RecordData();
        if (whys != null)
            data.RowsOf("whys").AddRange(whys.Select((w, i) => new RowBag { IsNew = true, Order = i, Values = RowValues(snap, ("why", w.Why), ("because", w.Because)) }));
        if (rowsCosts != null)
            data.RowsOf("costs").AddRange(rowsCosts.Select((c, i) => new RowBag { IsNew = true, Order = i, Values = RowValues(snap, ("item", c.Item), ("amount", c.Amount)) }));
        var res = await records.SaveAsync(snap, data, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException("Demo row update failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    private static async Task AddActionsAsync(DemoContext d, Guid id, params (string Action, string Assignee, decimal Days)[] actions)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.As("mchen");
        d.Db.ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(id);
        var data = new RecordData();
        data.RowsOf("corrective_actions").AddRange(actions.Select((a, i) => new RowBag
        {
            IsNew = true, Order = i, Values = RowValues(snap, ("action", a.Action), ("assignee", d.User(a.Assignee)), ("days", a.Days), ("verify", false)),
        }));
        var res = await records.SaveAsync(snap, data, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException("Demo corrective actions failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    private static ValueBag RowValues(RecordSnapshot snap, params (string Key, object? Value)[] values)
    {
        var bag = new ValueBag();
        foreach (var (key, value) in values) bag.Set(key, FieldCodec.FromObject(snap.Form.Fields.First(f => f.Key == key), value, snap.Config));
        return bag;
    }

    // ------------------------------------------------------------------ JSAs

    private static Dictionary<string, object?> Step(string step, string hazard, int s1, int l1, string controls, int s2, int l2, string? justification = null) => new()
    {
        ["step"] = step, ["hazard"] = hazard, ["sev_i"] = $"sev{s1}", ["lik_i"] = $"lik{l1}", ["controls"] = controls,
        ["sev_r"] = $"sev{s2}", ["lik_r"] = $"lik{l2}", ["justification"] = justification,
    };

    private static async Task JsasAsync(DemoContext d)
    {
        var mold = await d.CreateAsync("jsa", new()
        {
            ["job_title"] = "Changing the injection mold on Press 7", ["site"] = "kzo", ["area"] = "Molding hall, Press 7 (Engel 220 t)", ["department"] = "Operations: molding",
            ["owner"] = d.User("akim"), ["reviewer"] = d.User("mchen"),
            ["required_ppe"] = "Safety shoes, safety glasses, cut-resistant gloves; heat-resistant gloves and face shield for hot connections.",
            ["scope"] = "Every planned mold change on Presses 5–8, by trained setters only. Not for mold repairs in the press.",
        }, "akim", daysAgo: 120, rows: new()
        {
            ["steps"] =
            [
                Step("Isolate the press and verify zero energy", "Unexpected start-up; stored hydraulic pressure", 5, 3, "Lockout per SOP-EHS-007, try-start, bleed and check accumulator gauge", 5, 1),
                Step("Rig the mold with the overhead crane", "Dropped load; crushing between mold and platen", 5, 2, "Certified rigging and slings inspected before use, exclusion zone, tag lines", 5, 1),
                Step("Disconnect water and heater lines", "Hot surfaces and water at 90 °C", 3, 4, "30-minute cool-down, heat-resistant gloves, face shield", 2, 2),
                Step("Clean the platens and locating ring", "Solvent vapour and skin contact", 3, 3, "Low-VOC cleaner, nitrile gloves, extraction on", 2, 2),
            ],
        });
        await d.RouteAsync(mold.Id, "review");
        await d.RouteAsync(mold.Id, "approved", comment: "Reviewed on the floor with the setters.");

        var gal = await d.CreateAsync("jsa", new()
        {
            ["job_title"] = "Adjusting the conveyor guarding on Galway line 2", ["site"] = "gal", ["area"] = "Line 2 outfeed conveyor", ["department"] = "Engineering",
            ["owner"] = d.User("akim"), ["reviewer"] = d.User("mchen"), ["required_ppe"] = "Safety shoes, safety glasses, gloves.",
        }, "akim", daysAgo: 45, rows: new()
        {
            ["steps"] =
            [
                Step("Isolate the conveyor drive", "Entanglement in the drive and nip points", 4, 3, "Lockout at the MCC, test start from the HMI", 4, 1),
                Step("Remove and refit guard panels", "Cuts from sheet edges; dropped panels", 2, 3, "Gloves, two-person handling of panels over 1 m", 2, 1),
                Step("Function test with guards in place", "Guard gap allows finger access", 3, 2, "Check gaps against ISO 13857 with the test finger before release", 3, 1),
            ],
        });
        await d.RouteAsync(gal.Id, "review");
        await d.RouteAsync(gal.Id, "approved");

        var ipa = await d.CreateAsync("jsa", new()
        {
            ["job_title"] = "IPA wipe-down of the cleanroom B fill line", ["site"] = "bos", ["area"] = "Cleanroom B, fill line", ["department"] = "Operations: aseptic",
            ["owner"] = d.User("akim"), ["reviewer"] = d.User("mchen"), ["required_ppe"] = "Cleanroom garments, sterile gloves, safety glasses.",
        }, "akim", daysAgo: 5, rows: new()
        {
            ["steps"] =
            [
                Step("Prepare 70 % IPA wipes", "Flammable liquid; eye splash", 3, 2, "Pre-saturated wipes, no open containers over 500 mL, safety glasses", 2, 1),
                Step("Wipe the fill heads above shoulder height", "Awkward posture and overreach", 2, 4, "Two-step platform, task rotation every 30 minutes", 2, 2),
                Step("Dispose of used wipes", "Flammable waste", 3, 2, "Self-closing waste bin, emptied each shift", 2, 1),
            ],
        });
        await d.RouteAsync(ipa.Id, "review");

        await d.CreateAsync("jsa", new()
        {
            ["job_title"] = "Replacing HEPA filter bank FB-2 on the cleanroom B air handler", ["site"] = "bos", ["area"] = "Plant room, AHU-02", ["department"] = "Facilities",
            ["owner"] = d.User("ebaker"), ["reviewer"] = d.User("mchen"), ["required_ppe"] = "Coveralls, FFP3 respirator, nitrile gloves, hard hat on the scaffold.",
            ["scope"] = "Planned filter changes on AHU-01 and AHU-02 (12 cells of 610 × 610 × 292 mm).",
        }, "ebaker", daysAgo: 2, rows: new()
        {
            ["steps"] =
            [
                Step("Work on top of the AHU-02 housing at 2.4 m", "Fall from height", 4, 3, "Mobile scaffold with guardrails; no ladders", 4, 1),
                Step("Remove the loaded filter cells", "Exposure to captured particulate", 2, 3, "Bag-in/bag-out, FFP3 respirator, coveralls", 2, 2),
                Step("Lift the new filter cells (18 kg) onto the scaffold", "Manual handling strain", 2, 2, "Scaffold hoist instead of a two-person lift; the hoist adds a pinch point", 3, 2,
                    "The hoist removes 12 repeated lifts at height. The pinch-point risk is controlled by training and gloves and is preferred by ergonomic assessment ERG-2026-04."),
            ],
        });
    }

    // ------------------------------------------------------------------ permits

    private static async Task PermitsAsync(DemoContext d)
    {
        var now = DateTime.UtcNow;

        // Hot work this morning, closed out.
        var hot = await d.CreateAsync("permit", new()
        {
            ["work_title"] = "Weld a bracket to the mezzanine handrail, bay 3", ["permit_type"] = "hot_work", ["site"] = "kzo", ["location"] = "Warehouse mezzanine, bay 3",
            ["receiver"] = d.User("ebaker"), ["issuer"] = d.User("mchen"), ["valid_from"] = Ago(0, 7), ["valid_to"] = Ago(0, -1),
            ["hazards"] = "Sparks and hot slag falling to the packing area below; fumes.",
        }, "ebaker", rows: new()
        {
            ["conditions"] =
            [
                new() { ["condition"] = "Combustibles removed or covered within 11 m (35 ft), including below the mezzanine", ["confirmed"] = true },
                new() { ["condition"] = "Fire extinguisher (CO₂ and water) at the work location", ["confirmed"] = true },
                new() { ["condition"] = "Fire watch during the work and for 60 minutes after", ["confirmed"] = true },
                new() { ["condition"] = "Smoke detectors in zone 4 isolated with the alarm company, restored after", ["confirmed"] = true },
            ],
        });
        await d.RouteAsync(hot.Id, "authorization");
        await d.RouteAsync(hot.Id, "active", comment: "Checked on site; fire watch Luis's team.");
        await d.UpdateAsync(hot.Id, new() { ["work_completed"] = true, ["area_left_safe"] = true, ["closeout_notes"] = "Fire watch completed 60 min; zone 4 detectors restored at 11:40." }, "ebaker");
        await d.RouteAsync(hot.Id, "closed");

        // Confined space entry, active now (Boston).
        var cs = await d.CreateAsync("permit", new()
        {
            ["work_title"] = "Internal inspection of WFI storage tank T-201", ["permit_type"] = "confined_space", ["site"] = "bos", ["location"] = "Utilities, WFI tank T-201 (5,000 L)",
            ["contractor"] = "Bay State Tank Services", ["receiver"] = d.User("akim"), ["issuer"] = d.User("mchen"),
            ["valid_from"] = now.AddHours(-2), ["valid_to"] = now.AddHours(6),
            ["hazards"] = "Oxygen-deficient atmosphere after nitrogen blanketing; hot surfaces; limited egress through a 500 mm manway.",
            ["o2_pct"] = 20.8m, ["lel_pct"] = 0m, ["h2s_ppm"] = 0m, ["co_ppm"] = 1m, ["tested_by"] = d.User("mchen"), ["tested_at"] = now.AddMinutes(-40),
        }, "akim", rows: new()
        {
            ["conditions"] =
            [
                new() { ["condition"] = "Tank drained, cooled below 40 °C and isolated (blanks on inlet, outlet and N₂ line)", ["confirmed"] = true },
                new() { ["condition"] = "Continuous forced ventilation and personal 4-gas monitors for entrants", ["confirmed"] = true },
                new() { ["condition"] = "Attendant at the manway at all times; rescue tripod and harness rigged", ["confirmed"] = true },
                new() { ["condition"] = "Entry log kept at the manway", ["confirmed"] = true },
            ],
        });
        await d.RouteAsync(cs.Id, "authorization");
        await d.RouteAsync(cs.Id, "active");

        // Contractor permit to work, active and expiring within the day (Galway).
        var roof = await d.CreateAsync("permit", new()
        {
            ["work_title"] = "Replace rooftop HVAC unit RTU-3 above the warehouse", ["permit_type"] = "ptw", ["site"] = "gal", ["location"] = "Warehouse roof, RTU-3",
            ["contractor"] = "Western HVAC Services Ltd", ["receiver"] = d.User("hmoreau"), ["issuer"] = d.User("mchen"),
            ["valid_from"] = now.AddDays(-4).AddHours(-2), ["valid_to"] = now.AddHours(18),
            ["hazards"] = "Work at height on the roof, crane lift over the yard, electrical isolation of the RTU.",
        }, "hmoreau", rows: new()
        {
            ["conditions"] =
            [
                new() { ["condition"] = "Roof edge protection installed; harnesses within 2 m of the edge", ["confirmed"] = true },
                new() { ["condition"] = "Crane lift plan LP-GAL-0412 approved; yard exclusion zone", ["confirmed"] = true },
                new() { ["condition"] = "RTU-3 isolated and locked off at DB-4", ["confirmed"] = true },
            ],
        });
        await d.RouteAsync(roof.Id, "authorization");
        await d.RouteAsync(roof.Id, "active");

        // Hot work requested for tomorrow, awaiting authorization.
        var tomorrow = DemoContext.Days(1);
        var braze = await d.CreateAsync("permit", new()
        {
            ["work_title"] = "Braze a leaking chilled-water joint at AHU-01", ["permit_type"] = "hot_work", ["site"] = "kzo", ["location"] = "Plant room 2, AHU-01 chilled-water coil",
            ["receiver"] = d.User("ebaker"), ["issuer"] = d.User("mchen"),
            ["valid_from"] = Local(tomorrow, 7, 0, "kzo"), ["valid_to"] = Local(tomorrow, 13, 0, "kzo"),
            ["hazards"] = "Open flame next to duct insulation; brazing fumes.",
        }, "ebaker", rows: new()
        {
            ["conditions"] =
            [
                new() { ["condition"] = "Duct insulation within 1 m protected with a welding blanket", ["confirmed"] = false },
                new() { ["condition"] = "Fire extinguisher and fire watch for 60 minutes after", ["confirmed"] = false },
            ],
        });
        await d.RouteAsync(braze.Id, "authorization");
    }
}
