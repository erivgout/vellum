using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Packaging;

namespace Vellum.Modules.AdverseEvents;

/// <summary>
/// Demo data for Adverse Event Reporting, created through the real engine: the application profile, product codes, mappings for the three
/// sites, an eMDR profile on the simulated gateway, and MedWatch reports linked to the demo complaints in every interesting state.
/// </summary>
public static class AerDemo
{
    public static async Task SeedAsync(DemoContext d)
    {
        var sp = d.Services;
        if (!d.Config.FormsByKey.ContainsKey("complaint")) return;
        var setup = sp.GetRequiredService<AerSetup>();
        var factory = sp.GetRequiredService<MedWatchFactory>();
        var subs = sp.GetRequiredService<EmdrSubmissionService>();
        var records = sp.GetRequiredService<RecordService>();
        var year = BusinessCalendar.Today.Year;

        // ---- setup: application profile, product codes, mappings (AER-001)
        d.As("rnovak");
        var saved = await setup.SaveProfileAsync(new AerSetup.ProfileInput(true, AerProfileInfo.DefaultReceiptDelayMinutes, AerProfileInfo.DefaultSignSubmit,
            AerProfileInfo.DefaultSignComplete, AerProfileInfo.DefaultSignVoid, [d.User("sortiz")], AerProfileInfo.AllGroups));
        if (!saved.Ok) throw new InvalidOperationException("Demo profile: " + string.Join("; ", saved.Errors));

        await d.CreateAsync(AerKeys.ProductCode, new() { ["pc_code"] = "FRN", ["pc_device_name"] = "Pump, Infusion", ["pc_class"] = "ii", ["pc_regulation"] = "880.5725",
            ["pc_panel"] = "General Hospital", ["pc_brand_names"] = "VX-200 infusion pump, VX-200" }, "rnovak");
        await d.CreateAsync(AerKeys.ProductCode, new() { ["pc_code"] = "FPA", ["pc_device_name"] = "Set, Administration, Intravascular", ["pc_class"] = "ii", ["pc_regulation"] = "880.5440",
            ["pc_panel"] = "General Hospital", ["pc_brand_names"] = "ST-40, administration set" }, "rnovak");
        await d.CreateAsync(AerKeys.ProductCode, new() { ["pc_code"] = "FMF", ["pc_device_name"] = "Syringe, Piston", ["pc_class"] = "ii", ["pc_regulation"] = "880.5860",
            ["pc_panel"] = "General Hospital" }, "rnovak");

        Dictionary<string, object?> Contact() => new()
        {
            ["map_mfr_name"] = "Acme Medical Devices, Inc.", ["map_mfr_address"] = "4100 Portage Road, Kalamazoo, MI 49001, USA",
            ["map_contact_name"] = "Regulatory Affairs (Riley Novak)", ["map_contact_address"] = "4100 Portage Road, Kalamazoo, MI 49001, USA",
            ["map_contact_phone"] = "+1 269 555 0142",
        };
        var kzo = Contact();
        kzo["map_location"] = "kalamazoo"; kzo["map_reporter_type"] = AerKeys.Manufacturer; kzo["map_mfr_code_mode"] = "basic"; kzo["map_mfr_code"] = "1834567";
        kzo["map_mfr_seq_format"] = "sequential"; kzo["map_mfr_seq_initial"] = 41m; kzo["map_mfr_seq_initial_year"] = (decimal)year;
        await d.CreateAsync(AerKeys.Mapping, kzo, "rnovak");
        var bos = Contact();
        bos["map_location"] = "boston"; bos["map_reporter_type"] = AerKeys.Manufacturer; bos["map_mfr_code_mode"] = "advanced"; bos["map_mfr_code"] = "1834567";
        bos["map_mfr_seq_format"] = "sequential"; bos["map_mfr_seq_initial"] = 1m;
        await d.CreateAsync(AerKeys.Mapping, bos, "rnovak", rows: new()
        {
            ["map_code_rules"] = [new() { ["rule_product_code"] = "FPA", ["rule_code"] = "3004512345", ["rule_note"] = "Administration sets are made and registered in Galway" }],
        });
        var gal = Contact();
        gal["map_location"] = "galway"; gal["map_reporter_type"] = AerKeys.Manufacturer; gal["map_mfr_code_mode"] = "basic"; gal["map_mfr_code"] = "3004512345";
        gal["map_mfr_seq_format"] = "sequential"; gal["map_mfr_seq_initial"] = 1m;
        gal["map_mfr_name"] = "Acme Medical Devices Ireland Ltd."; gal["map_mfr_address"] = "Parkmore West Business Park, Galway, H91 X2Y3, Ireland";
        await d.CreateAsync(AerKeys.Mapping, gal, "rnovak");

        // ---- eMDR profile on the simulated gateway (INT-902). This demo environment isn't production.
        d.As("admin");
        var emdr = sp.GetRequiredService<EmdrProfileService>();
        var p = await emdr.SaveAsync(new EmdrProfileService.ProfileInput("test", "Acme Medical Devices, Inc.", "ACMEMDTEST01", "emdr@acme-medical.example",
            "https://vellum.acme-medical.example/emdr/ack", null, null, null, "simulated", "accept"));
        if (!p.Ok) throw new InvalidOperationException("Demo eMDR profile: " + string.Join("; ", p.Errors));
        await emdr.GenerateCertificateAsync(new EmdrProfileService.CertificateInput("emdr.acme-medical.example", "Acme Medical Devices Inc", "Regulatory Affairs",
            "Kalamazoo", "Michigan", "US", "emdr@acme-medical.example"), acknowledged: true);
        await emdr.ConfirmFdaHasCertificateAsync();

        var complaint = d.Config.Form("complaint");
        var tree = d.Config.Form("mdr_decision_tree");
        async Task<Guid?> TreeOf(string titleStart)
        {
            var c = await d.Db.Records.AsNoTracking().Where(r => r.FormId == complaint.Id && r.Title.StartsWith(titleStart)).Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
            return c == null ? null : await d.Db.Records.AsNoTracking().Where(r => r.ParentId == c && r.FormId == tree.Id).Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
        }
        async Task<Guid> CreateAsync(Guid treeId)
        {
            d.As("rnovak");
            d.Db.ChangeTracker.Clear();
            var (rec, err) = await factory.CreateFromTreeAsync(treeId);
            return rec?.Id ?? throw new InvalidOperationException("Demo MedWatch: " + err);
        }
        async Task ApproveAsync(Guid mwId)
        {
            foreach (var a in await records.ChildrenAsync(mwId, AerKeys.Approver))
                if (a.PhaseType == PhaseType.Normal) await d.RouteAsync(a.Id, "approved", comment: "Report content checked against the complaint file and investigation.");
        }
        async Task BackdateAsync(Guid mwId, DateTime sentUtc, string? outcome = null)
        {
            // Demo only: pretend the file went out earlier (and choose what the simulated FDA says), so acknowledgements are due now.
            await d.Db.Set<EmdrSubmission>().Where(x => x.MedWatchId == mwId && !x.Superseded).ExecuteUpdateAsync(x => x
                .SetProperty(s => s.SentAt, sentUtc).SetProperty(s => s.CreatedAt, sentUtc.AddMinutes(-2)).SetProperty(s => s.SimulatedOutcome, s => outcome ?? s.SimulatedOutcome));
            await d.Db.RecordValues.Where(v => v.RecordId == mwId && v.FieldKey == "submitted_at" && v.RowId == null).ExecuteUpdateAsync(x => x.SetProperty(v => v.Date, sentUtc));
        }
        async Task MonitorAsync()
        {
            sp.GetRequiredService<CurrentUser>().UseSystem("eMDR acknowledgements");
            d.Db.ChangeTracker.Clear();
            await subs.MonitorAsync(DateTime.UtcNow);
        }

        // 1. Completed initial report (30-day malfunction) with ACK1-3: CMP-00005, numbered 1834567-YYYY-00041.
        if (await TreeOf("Occlusion alarm delayed on VX-200") is { } t5)
        {
            var mw = await CreateAsync(t5);
            await d.UpdateAsync(mw, new()
            {
                ["event_date"] = DemoContext.Days(-37), ["device_operator"] = "hcp", ["reporter_name"] = "Home-care nurse (name withheld at reporter's request)",
                ["reporter_country"] = "USA", ["reporter_is_hcp"] = true, ["reporter_occupation"] = "Registered nurse, home infusion",
                ["evaluated"] = "yes", ["usage"] = "reuse", ["manufacture_date"] = DemoContext.Days(-120), ["device_available"] = "yes",
                ["evaluation_summary"] = "Returned pump VX2-24180-0377 reproduced a 118 s downstream occlusion alarm latency. Force sensor lot FS-0824 had a +0.6 N zero offset (see the 8D CAPA).",
                ["patient_sex"] = "female", ["patient_age"] = 54m, ["patient_age_unit"] = "years",
            }, "rnovak");
            await d.RouteAsync(mw, "approval");
            await ApproveAsync(mw);
            await d.RouteAsync(mw, "submitted", asUser: "rnovak");
            await BackdateAsync(mw, DateTime.UtcNow.AddDays(-8).AddHours(-3));
            await MonitorAsync();
            await d.UpdateAsync(mw, new() { ["confirm_final_ack"] = true, ["completion_notes"] = "ACK3 shows the report loaded without errors." }, "rnovak");
            await d.RouteAsync(mw, "completed", asUser: "rnovak");

            // 2. Supplemental #1 (device evaluation) in Submitted to FDA: ACK1 and ACK2 arrived, ACK3 never did → overdue alert (AER-010).
            d.As("rnovak");
            d.Db.ChangeTracker.Clear();
            var (sup, err) = await factory.CreateSupplementalAsync(mw, "device_evaluation", "Supplier failure analysis of force sensor lot FS-0824 received.");
            if (sup == null) throw new InvalidOperationException("Demo supplemental: " + err);
            await d.UpdateAsync(sup.Id, new()
            {
                ["changed_sections"] = new List<string> { "h" },
                ["evaluation_summary"] = "Supplier failure analysis (report FA-2291) confirmed a calibration step was skipped for force sensor lot FS-0824, causing the +0.6 N zero offset. All 231 pumps in the affected serial range have been reworked.",
                ["remedial_actions"] = new List<string> { "repair", "inspection" },
            }, "rnovak");
            await d.RouteAsync(sup.Id, "approval");
            await ApproveAsync(sup.Id);
            await d.RouteAsync(sup.Id, "submitted", asUser: "rnovak");
            await BackdateAsync(sup.Id, DateTime.UtcNow.AddDays(-3), "withhold_ack3");
            await MonitorAsync();
        }

        // 3. 5-day report in Approval, deadline already missed: CMP-00002 (Galway particulate).
        if (await TreeOf("Particulate found in ST-40") is { } t2)
        {
            var mw = await CreateAsync(t2);
            await d.UpdateAsync(mw, new()
            {
                ["event_date"] = DemoContext.Days(-13), ["device_operator"] = "hcp", ["reporter_is_hcp"] = false, ["evaluated"] = "yes", ["usage"] = "initial",
                ["evaluation_summary"] = "Three returned sets from lot 24-127 contain black particulate (0.2–0.5 mm) in the fluid path. FTIR matches the EO sterilizer door gasket.",
                ["remedial_actions"] = new List<string> { "recall", "notification" }, ["expiration_date"] = DemoContext.Days(700),
                ["mfr_narrative"] = "Lot 24-127 recalled from distribution on " + DemoContext.Days(-8).ToString("dd MMM yyyy") + "; customers notified (correction and removal report pending).",
            }, "rnovak");
            await d.RouteAsync(mw, "approval");
        }

        // 4. Draft created from CMP-00001 (heparin delay, 30-day report due in 3 days), still missing data: shows eMDR validation.
        if (await TreeOf("VX-200 did not alarm on downstream occlusion") is { } t1)
        {
            var mw = await CreateAsync(t1);
            await d.UpdateAsync(mw, new() { ["event_date"] = DemoContext.Days(-27), ["patient_weight"] = 82m }, "rnovak");
        }
        await MonitorAsync();
    }
}
