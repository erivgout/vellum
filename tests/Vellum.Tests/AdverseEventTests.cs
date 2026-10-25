using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Record = Vellum.Domain.Record;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.AdverseEvents;

namespace Vellum.Tests;

/// <summary>Adverse Event Reporting §12.8: AER-002/005/006/008..011, CMP-005, INV-023, INV-047, INV-048, INT-902.</summary>
[Collection("vellum")]
public class AdverseEventTests(VellumFixture fx)
{
    private const string Site = "boston";
    private const string Code = "1112223";
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static bool _setUp;

    // ------------------------------------------------------------------ helpers

    private static void AssertOk(Engine.Workflow.RouteResult res) => Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => $"{e.FieldKey}: {e.Message}")));

    private static async Task UpdateOkAsync(IServiceScope s, Guid id, Dictionary<string, object?> values)
    {
        s.Db().ChangeTracker.Clear();
        var res = await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, values);
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => $"{e.FieldKey}: {e.Message}")));
    }

    private async Task EnsureSetupAsync()
    {
        await SetupLock.WaitAsync();
        try
        {
            if (_setUp) return;
            using (var s = fx.As("rnovak"))
            {
                var setup = s.ServiceProvider.GetRequiredService<AerSetup>();
                if (await setup.ProfileAsync() == null)
                {
                    var res = await setup.SaveProfileAsync(new AerSetup.ProfileInput(true, 60, "Submitted to FDA", "Final acknowledgement confirmed", "Voided",
                        [s.UserId("sortiz")], AerProfileInfo.AllGroups));
                    Assert.True(res.Ok, string.Join("; ", res.Errors));
                }
                if (await setup.MappingForAsync(Site) == null)
                    await s.CreateAsync(AerKeys.Mapping, new()
                    {
                        ["map_location"] = Site, ["map_reporter_type"] = AerKeys.Manufacturer, ["map_mfr_code_mode"] = "basic", ["map_mfr_code"] = Code,
                        ["map_mfr_seq_format"] = "sequential", ["map_mfr_seq_initial"] = 1m,
                        ["map_mfr_name"] = "Acme Medical Devices, Inc.", ["map_mfr_address"] = "1 Main St, Boston, MA, USA",
                        ["map_contact_name"] = "Regulatory Affairs", ["map_contact_address"] = "1 Main St, Boston, MA, USA", ["map_contact_phone"] = "+1 617 555 0100",
                    });
                if ((await setup.ProductCodesAsync()).All(p => p.Code != "FRN"))
                    await s.CreateAsync(AerKeys.ProductCode, new() { ["pc_code"] = "FRN", ["pc_device_name"] = "Pump, Infusion", ["pc_class"] = "ii", ["pc_brand_names"] = "VX-200" });
            }
            await SetGatewayAsync("simulated", "accept");
            _setUp = true;
        }
        finally { SetupLock.Release(); }
    }

    private async Task SetGatewayAsync(string gateway, string outcome)
    {
        using var a = fx.As("admin");
        var res = await a.ServiceProvider.GetRequiredService<EmdrProfileService>().SaveAsync(new EmdrProfileService.ProfileInput("test", "Acme Medical Devices",
            "ACMETEST", "emdr@acme.example", null, null, null, null, gateway, outcome));
        Assert.True(res.Ok, string.Join("; ", res.Errors));
    }

    /// <summary>A complaint whose MDR tree has concluded "reportable (30-day)", with its tree ID.</summary>
    private async Task<(Guid Complaint, Guid Tree)> ReportableComplaintAsync(string subject)
    {
        Guid complaintId, treeId;
        using (var s = fx.As("jlee"))
        {
            var c = await s.CreateAsync("complaint", new()
            {
                ["subject"] = subject, ["category"] = "adverse_event", ["owner"] = s.UserId("jlee"), ["awareness_date"] = BusinessCalendar.Today.AddDays(-2),
                ["site_reported"] = Site, ["description"] = "Pump did not alarm on downstream occlusion; therapy was delayed and the patient needed a dose adjustment.",
                ["product"] = "VX-200 infusion pump", ["model"] = "VX-200-US", ["lot_number"] = "L-77", ["serial_number"] = "SN-77", ["failure_mode"] = "occlusion_alarm",
                ["device_returned"] = true, ["reporter_name"] = "A. Nurse", ["reporter_type"] = "hcp", ["reporter_organization"] = "General Hospital", ["reporter_country"] = "USA",
                ["patient_involved"] = true, ["patient_sex"] = "female", ["patient_age"] = 60m, ["injury"] = true, ["death"] = false, ["medical_intervention"] = true,
            });
            complaintId = c.Id;
            AssertOk(await s.RouteAsync(c.Id, "investigation"));
            await UpdateOkAsync(s, c.Id, new() { ["investigation_summary"] = "Returned pump reproduced a 95 s alarm latency.", ["conclusion"] = "confirmed" });
            treeId = (await s.ServiceProvider.GetRequiredService<RecordService>().ChildrenAsync(c.Id, "mdr_decision_tree")).Single().Id;
            await UpdateOkAsync(s, treeId, new() { ["q_death"] = false, ["q_serious_injury"] = true, ["q_remedial"] = false, ["mdr_rationale"] = "Serious injury." });
            AssertOk(await s.RouteAsync(treeId, "approval"));
        }
        using (var ra = fx.As("rnovak")) AssertOk(await ra.RouteAsync(treeId, "completed"));
        return (complaintId, treeId);
    }

    private async Task<Guid> CreateMedWatchAsync(Guid treeId)
    {
        using var s = fx.As("rnovak");
        var (rec, err) = await s.ServiceProvider.GetRequiredService<MedWatchFactory>().CreateFromTreeAsync(treeId);
        Assert.True(rec != null, err);
        return rec!.Id;
    }

    private async Task CompleteContentAsync(Guid mwId)
    {
        using var s = fx.As("rnovak");
        await UpdateOkAsync(s, mwId, new()
        {
            ["event_date"] = BusinessCalendar.Today.AddDays(-3), ["device_operator"] = "hcp", ["usage"] = "reuse", ["evaluated"] = "yes",
            ["evaluation_summary"] = "Alarm latency reproduced; force sensor offset found.",
        });
    }

    /// <summary>Draft → Approval → (sortiz approves) → Submitted to FDA.</summary>
    private async Task SubmitAsync(Guid mwId)
    {
        using (var s = fx.As("rnovak")) AssertOk(await s.RouteAsync(mwId, "approval"));
        using (var a = fx.As("sortiz"))
        {
            var approval = (await a.ServiceProvider.GetRequiredService<RecordService>().ChildrenAsync(mwId, AerKeys.Approver)).Single(r => r.PhaseType == PhaseType.Normal);
            AssertOk(await a.RouteAsync(approval.Id, "approved"));
        }
        using (var s = fx.As("rnovak")) AssertOk(await s.RouteAsync(mwId, "submitted"));
    }

    private async Task<EmdrSubmissionService.MonitorResult> MonitorAsync(DateTime nowUtc)
    {
        using var job = fx.Services.CreateScope();
        job.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("test");
        return await job.ServiceProvider.GetRequiredService<EmdrSubmissionService>().MonitorAsync(nowUtc);
    }

    private static EmdrReport SampleReport() => new()
    {
        ReportNumber = "1834567-2026-00042", ReportType = AerKeys.Initial, ReporterType = AerKeys.Manufacturer, LocalNumber = "MW-00042",
        ReportDate = new DateOnly(2026, 10, 20), EventDate = new DateOnly(2026, 10, 1), AwarenessDate = new DateOnly(2026, 10, 2),
        PatientIdentifier = "JD", PatientAge = 67, PatientAgeUnit = "years", PatientSex = "female", PatientWeight = 82.5m, PatientWeightUnit = "kg",
        EventKinds = ["adverse_event", "product_problem"], Outcomes = ["intervention"],
        EventDescription = "Pump did not alarm\u0001 on downstream occlusion & the <heparin> infusion stopped.",
        BrandName = "VX-200", CommonName = "Pump, Infusion", ProductCode = "FRN", ManufacturerName = "Acme", ManufacturerAddress = "Kalamazoo, MI",
        LotNumber = "VX2-24180", SerialNumber = "VX2-24180-0412", Udi = "(01)00812345000123(21)VX2-24180-0412", DeviceOperator = "hcp", DeviceAvailable = "yes",
        ReporterName = "Tom Becker", ReporterCountry = "USA", ReporterIsHcp = true, ContactName = "Regulatory Affairs", ContactPhone = "+1 269 555 0142",
        ReportSources = ["health_professional"], EventType = "serious_injury", Evaluated = "yes", Usage = "reuse",
        Codes = [("device_problem", "A16", "Protective Measures Problem"), ("health_effect", "E0101", null)],
    };

    // ------------------------------------------------------------------ generator and validator (no database)

    [Fact] // AER-008: FDA HL7 ICSR file
    public void Submission_file_is_well_formed_hl7_icsr_with_the_key_elements()
    {
        var env = new EmdrEnvelope($"VLM-{Guid.NewGuid():N}", new DateTime(2026, 10, 20, 14, 5, 0, DateTimeKind.Utc), "ACMETEST01", "ZZFDATST", false, "Acme Medical Devices", "emdr@acme.example", "CDRH");
        var bytes = EmdrXmlGenerator.Generate(SampleReport(), env);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var doc = XDocument.Load(reader); // throws when not well-formed
        XNamespace v3 = "urn:hl7-org:v3";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var root = doc.Root!;
        Assert.Equal(v3 + "PORR_IN049016UV", root.Name);
        Assert.Equal(env.MessageId, root.Element(v3 + "id")!.Attribute("extension")!.Value);
        Assert.Equal("PORR_IN049016UV", root.Element(v3 + "interactionId")!.Attribute("extension")!.Value);
        Assert.Equal("T", root.Element(v3 + "processingCode")!.Attribute("code")!.Value); // test account
        Assert.Equal("ZZFDATST", root.Element(v3 + "receiver")!.Descendants(v3 + "id").Single().Attribute("extension")!.Value);
        Assert.Equal("ACMETEST01", root.Element(v3 + "sender")!.Descendants(v3 + "id").Single().Attribute("extension")!.Value);

        var inv = root.Descendants(v3 + "investigationEvent").Single();
        Assert.Equal("1834567-2026-00042", inv.Element(v3 + "id")!.Attribute("extension")!.Value);
        Assert.Equal("20261001", inv.Element(v3 + "effectiveTime")!.Element(v3 + "low")!.Attribute("value")!.Value);
        Assert.Equal("Pump did not alarm on downstream occlusion & the <heparin> infusion stopped.", inv.Element(v3 + "text")!.Value); // control character dropped, markup escaped

        XElement Item(string code) => inv.Descendants(v3 + "observation").First(o => o.Element(v3 + "code")?.Attribute("code")?.Value == code);
        string Value(string code, string attr = "code") => Item(code).Element(v3 + "value")!.Attribute(attr)?.Value ?? Item(code).Element(v3 + "value")!.Value;
        Assert.Equal("FRN", Value("D2b"));
        Assert.Equal("F", Value("A3"));
        Assert.Equal("67", Value("A2", "value"));
        Assert.Equal("(01)00812345000123(21)VX2-24180-0412", Value("D4.UDI"));
        Assert.Equal("serious_injury", Value("H1"));
        Assert.Equal("1834567-2026-00042", Value("G9"));
        var codes = inv.Descendants(v3 + "observation").Where(o => o.Element(v3 + "code")?.Attribute("code")?.Value == "H6")
            .Select(o => o.Element(v3 + "value")!.Attribute("code")!.Value).ToList();
        Assert.Equal(["A16", "E0101"], codes);
        Assert.All(inv.Descendants(v3 + "value"), v => Assert.NotNull(v.Attribute(xsi + "type")));
        Assert.Equal(["A", "B", "D", "E", "G", "H"], inv.Elements(v3 + "component").Select(c => c.Element(v3 + "organizer")!.Element(v3 + "code")!.Attribute("code")!.Value));

        var prod = EmdrXmlGenerator.Build(SampleReport(), env with { Production = true, ReceiverRoutingId = "ZZFDA" });
        Assert.Equal("P", prod.Root!.Element(v3 + "processingCode")!.Attribute("code")!.Value);
    }

    [Fact] // AER-006 formats, INT-902 certificate characters, gateway availability
    public void Format_rules_reject_invalid_codes_and_certificate_characters()
    {
        Assert.True(EmdrValidator.IsValidImdrf("A", "A16"));
        Assert.True(EmdrValidator.IsValidImdrf("A", "A0401"));
        Assert.True(EmdrValidator.IsValidImdrf("E", "E010101"));
        Assert.False(EmdrValidator.IsValidImdrf("A", "E16"));
        Assert.False(EmdrValidator.IsValidImdrf("A", "A1"));
        Assert.False(EmdrValidator.IsValidImdrf("A", "A16X"));
        Assert.True(EmdrValidator.IsValidProcode("FRN"));
        Assert.False(EmdrValidator.IsValidProcode("FR1"));
        Assert.True(EmdrValidator.IsValidCode("1834567"));
        Assert.False(EmdrValidator.IsValidCode("18345"));
        Assert.Null(EmdrValidator.UdiProblem("(01)00812345000123(17)270630"));
        Assert.NotNull(EmdrValidator.UdiProblem("(01)123"));
        Assert.NotNull(EmdrValidator.UdiProblem("00812345000123"));
        Assert.Equal("1834567-2026-00007", ReportNumberService.Format("1834567", 2026, 7));
        Assert.Equal("1234567890-2026-0007", ReportNumberService.Format("1234567890", 2026, 7, userFacility: true));

        foreach (var ch in "(+<>;\"/\\")
            Assert.NotNull(EmdrProfileService.CertFieldProblem("Common name", $"acme{ch}emdr"));
        Assert.Null(EmdrProfileService.CertFieldProblem("Common name", "emdr.acme-medical.example"));
        Assert.Contains(EmdrProfileService.CertificateProblems(new("emdr.acme", "Acme", null, "Kalamazoo", "MI", "USA", "a@b.example")), e => e.Contains("two-letter"));

        var profile = new EmdrProfile();
        Assert.NotNull(new SimulatedEmdrGateway().UnavailableReason(profile, EnvironmentType.Production));
        Assert.Null(new SimulatedEmdrGateway().UnavailableReason(profile, EnvironmentType.Validation));
        Assert.NotNull(new As2EmdrGateway().UnavailableReason(profile, EnvironmentType.Validation));
        Assert.Null(new ManualEsgGateway().UnavailableReason(profile, EnvironmentType.Production));

        var mapping = new AerMappingInfo(Guid.Empty, "M", Site, AerKeys.Manufacturer, "advanced", "1834567", "sequential", 41, 2026, null, "sequential", 1, null,
            [("FPA", "3004512345")], null, null, null, null, null);
        Assert.Equal("3004512345", mapping.CodeFor(AerKeys.Manufacturer, "FPA"));
        Assert.Equal("1834567", mapping.CodeFor(AerKeys.Manufacturer, "FRN"));
        Assert.Equal(41, mapping.StartFor(AerKeys.Manufacturer, 2026));
        Assert.Equal(1, mapping.StartFor(AerKeys.Manufacturer, 2027)); // AER-005: the sequence resets each year
    }

    // ------------------------------------------------------------------ through the engine

    [Fact] // AER-002
    public async Task A_medwatch_needs_the_application_profile_first()
    {
        await EnsureSetupAsync();
        using var s = fx.As("rnovak");
        var profileId = (await s.ServiceProvider.GetRequiredService<AerSetup>().ProfileAsync())!.Id;
        await s.Db().Records.Where(r => r.Id == profileId).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsArchived, true)); // test-only: hide the profile
        try
        {
            using var t = fx.As("rnovak");
            var res = await t.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
            {
                FormKey = AerKeys.MedWatch, Values = new() { ["subject"] = "No profile", ["location"] = Site },
            });
            Assert.False(res.Ok);
            Assert.Contains(res.Errors, e => e.Message.Contains("AER-002"));
            Assert.Contains("AER-002", await t.ServiceProvider.GetRequiredService<MedWatchFactory>().BlockerForTreeAsync(Guid.NewGuid()) ?? "");
        }
        finally
        {
            await s.Db().Records.Where(r => r.Id == profileId).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsArchived, false));
        }
    }

    [Fact] // CMP-005, INV-047
    public async Task Create_medwatch_from_a_reportable_tree_carries_the_complaint_data_and_allows_one_initial_report()
    {
        await EnsureSetupAsync();
        var (complaintId, treeId) = await ReportableComplaintAsync("INV-047 complaint");

        using var s = fx.As("rnovak");
        var behavior = s.ServiceProvider.GetServices<RecordBehavior>().OfType<ComplaintMedWatchBehavior>().Single();
        var action = Assert.Single(await behavior.ActionsAsync(new RecordContext(await s.LoadAsync(treeId), s.ServiceProvider)));
        Assert.Null(action.DisabledReason);
        var outcome = await behavior.ExecuteAsync(new RecordContext(await s.LoadAsync(treeId), s.ServiceProvider), action.Key, new ActionInput());
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors ?? []));
        var mw = await s.LoadAsync(outcome.OpenRecordId!.Value);

        Assert.Equal(AerKeys.Initial, mw.KeywordKey("report_type"));
        Assert.Equal(Site, mw.KeywordKey("location"));
        Assert.Equal(complaintId, Assert.Single(mw.Links("complaint")));
        Assert.Equal(treeId, Assert.Single(mw.Links("mdr_tree")));
        Assert.Equal(BusinessCalendar.Today.AddDays(28), mw.Date("report_due")); // 30 days from awareness
        Assert.Equal("VX-200 infusion pump", mw.Text("brand_name"));
        Assert.Equal("FRN", mw.Text("product_code"));
        Assert.Equal("L-77", mw.Text("lot_number"));
        Assert.Equal("female", mw.KeywordKey("patient_sex"));
        Assert.Equal("serious_injury", mw.KeywordKey("event_type"));
        Assert.Contains("intervention", mw.KeywordKeys("outcomes"));
        Assert.Equal("A. Nurse", mw.Text("reporter_name"));
        Assert.Equal("Regulatory Affairs", mw.Text("contact_name")); // from the location's mapping
        Assert.Equal("A16", mw.Rows("problem_codes").Single().Values.Text("code"));
        Assert.Equal([s.UserId("sortiz")], mw.UserIds("approvers"));

        // INV-047: a second initial MedWatch for the same tree is refused (friendly check)…
        var again = await behavior.ActionsAsync(new RecordContext(await s.LoadAsync(complaintId), s.ServiceProvider));
        Assert.Empty(again);
        var (dup, err) = await s.ServiceProvider.GetRequiredService<MedWatchFactory>().CreateFromTreeAsync(treeId);
        Assert.Null(dup);
        Assert.Contains("INV-047", err);
        // …and by the database's filtered unique index itself.
        s.Db().ChangeTracker.Clear();
        s.Db().Set<MedWatchReport>().Add(new MedWatchReport { MedWatchId = Guid.NewGuid(), MdrTreeId = treeId, IsInitial = true, ReporterType = AerKeys.Manufacturer, CreatedAt = DateTime.UtcNow });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db().SaveChangesAsync());
        Assert.True(EmdrSubmissionService.Unique(ex));
        s.Db().ChangeTracker.Clear();
        // A supplemental-type or importer row for the same tree doesn't count.
        s.Db().Set<MedWatchReport>().Add(new MedWatchReport { MedWatchId = Guid.NewGuid(), MdrTreeId = treeId, IsInitial = false, ReporterType = AerKeys.Manufacturer, CreatedAt = DateTime.UtcNow });
        s.Db().Set<MedWatchReport>().Add(new MedWatchReport { MedWatchId = Guid.NewGuid(), MdrTreeId = treeId, IsInitial = true, ReporterType = AerKeys.Importer, CreatedAt = DateTime.UtcNow });
        await s.Db().SaveChangesAsync();
        s.Db().ChangeTracker.Clear();

        // Voiding the MedWatch frees the tree for a new initial report.
        AssertOk(await s.RouteAsync(mw.Record.Id, "voided", comment: "Created in error"));
        Assert.True((await s.Db().Set<MedWatchReport>().AsNoTracking().SingleAsync(x => x.MedWatchId == mw.Record.Id)).IsVoided);
        var (fresh, err2) = await s.ServiceProvider.GetRequiredService<MedWatchFactory>().CreateFromTreeAsync(treeId);
        Assert.True(fresh != null, err2);
    }

    [Fact] // AER-006, AER-007
    public async Task Missing_or_invalid_data_blocks_routing_with_field_level_errors()
    {
        await EnsureSetupAsync();
        var (_, treeId) = await ReportableComplaintAsync("AER-006 complaint");
        var mwId = await CreateMedWatchAsync(treeId);
        using var s = fx.As("rnovak");

        // Invalid formats never save.
        s.Db().ChangeTracker.Clear();
        var bad = await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(mwId, new() { ["product_code"] = "F1", ["reporter_email"] = "not-an-email" });
        Assert.False(bad.Ok);
        Assert.Contains(bad.Errors, e => e.FieldKey == "product_code");
        Assert.Contains(bad.Errors, e => e.FieldKey == "reporter_email");
        // With mapping on, people can't type the number parts the mapping assigns (AER-005).
        s.Db().ChangeTracker.Clear();
        var typed = await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(mwId, new() { ["mfr_code"] = "9999999" });
        Assert.False(typed.Ok);
        Assert.Contains(typed.Errors, e => e.FieldKey == "mfr_code");

        await UpdateOkAsync(s, mwId, new() { ["outcomes"] = new List<string> { "death", "intervention" }, ["udi"] = "(01)123" });
        var res = await s.RouteAsync(mwId, "approval");
        Assert.False(res.Ok);
        var fields = res.Errors.Where(e => e.FieldKey != null).Select(e => e.FieldKey).ToHashSet();
        Assert.Contains("device_operator", fields);  // D5 missing
        Assert.Contains("usage", fields);            // H8 missing
        Assert.Contains("death_date", fields);       // B2 death needs a date
        Assert.Contains("event_type", fields);       // H1 must agree with the death outcome
        Assert.Contains("udi", fields);              // malformed UDI
        Assert.All(res.Errors.Where(e => e.FieldKey != null), e => Assert.Contains("AER-R", e.Message));

        await UpdateOkAsync(s, mwId, new() { ["outcomes"] = new List<string> { "intervention" }, ["udi"] = null });
        await CompleteContentAsync(mwId);
        AssertOk(await s.RouteAsync(mwId, "approval"));

        // AER-007: an open action item created in Approval blocks submission; so does an approver who hasn't approved.
        var ai = await s.CreateAsync("action_item", new() { ["subject"] = "Check narrative", ["assignee"] = s.UserId("rnovak"), ["due_date"] = BusinessCalendar.Today.AddDays(3) }, parent: mwId);
        var blocked = await s.RouteAsync(mwId, "submitted");
        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Errors, e => e.Message.Contains(ai.Number));
        Assert.Contains(blocked.Errors, e => e.Message.Contains("MedWatch Approver") || e.Message.Contains("approver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // AER-005, AER-008, AER-009, INV-023, INV-048, CMP-009
    public async Task Submission_assigns_the_next_number_sends_the_file_and_completion_needs_all_three_acks()
    {
        await EnsureSetupAsync();
        await SetGatewayAsync("simulated", "accept");
        var (complaintId, treeId) = await ReportableComplaintAsync("Submission complaint");
        var mwId = await CreateMedWatchAsync(treeId);
        await CompleteContentAsync(mwId);
        int before;
        using (var s0 = fx.As("rnovak"))
            before = await s0.Db().Set<MedWatchReport>().Where(x => x.ReportCode == Code && x.ReportYear == BusinessCalendar.Today.Year && x.IsInitial)
                .MaxAsync(x => x.ReportSequence) ?? 0;
        await SubmitAsync(mwId);

        using var s = fx.As("rnovak");
        var mw = await s.LoadAsync(mwId);
        var expected = ReportNumberService.Format(Code, BusinessCalendar.Today.Year, before + 1);
        Assert.Equal(expected, mw.Text("report_number"));
        Assert.Equal(Code, mw.Text("mfr_code"));
        Assert.Equal("awaiting_ack", mw.KeywordKey("submission_status"));
        var file = Assert.Single(mw.AttachmentsOf("submission_file"));
        var xml = await s.Db().Set<EmdrSubmission>().AsNoTracking().SingleAsync(x => x.MedWatchId == mwId);
        Assert.Equal(file.Sha256, xml.FileSha256);
        Assert.Equal(EmdrStatus.Sent, xml.Status);
        Assert.Equal(EmdrSchema.RuleSetVersion, xml.RuleSetVersion);

        // Not before the acknowledgements arrive (INV-048).
        await UpdateOkAsync(s, mwId, new() { ["confirm_final_ack"] = true });
        var early = await s.RouteAsync(mwId, "completed");
        Assert.False(early.Ok);
        Assert.Contains(early.Errors, e => e.Message.Contains("ACK3") && e.Message.Contains("INV-048"));

        // The simulated gateway delivers ACK1-3 through the job.
        var mon = await MonitorAsync(DateTime.UtcNow.AddMinutes(4));
        Assert.True(mon.Acks >= 3);
        mw = await s.LoadAsync(mwId);
        Assert.Equal("acknowledged", mw.KeywordKey("submission_status"));
        Assert.NotNull(mw.DateTime("ack3_at"));
        var acks = await s.ServiceProvider.GetRequiredService<RecordService>().ChildrenAsync(mwId, AerKeys.Ack);
        Assert.Equal(3, acks.Count);

        // The complaint's FDA clock stops at the ACK2 receipt date (CMP-009).
        var complaint = await s.LoadAsync(complaintId);
        Assert.NotNull(complaint.Date("fda_submitted_on"));
        Assert.Contains(expected, complaint.Text("report_reference"));

        // Completion still needs the final confirmation (INV-048).
        await UpdateOkAsync(s, mwId, new() { ["confirm_final_ack"] = false });
        Assert.Contains((await s.RouteAsync(mwId, "completed")).Errors, e => e.Message.Contains("Confirm final acknowledgement"));
        await UpdateOkAsync(s, mwId, new() { ["confirm_final_ack"] = true });
        AssertOk(await s.RouteAsync(mwId, "completed"));

        // INV-023: the index refuses a second initial report with the same issued number.
        s.Db().ChangeTracker.Clear();
        s.Db().Set<MedWatchReport>().Add(new MedWatchReport { MedWatchId = Guid.NewGuid(), IsInitial = true, ReporterType = AerKeys.Manufacturer,
            ReportCode = Code, ReportYear = BusinessCalendar.Today.Year, ReportSequence = before + 1, CreatedAt = DateTime.UtcNow });
        Assert.True(EmdrSubmissionService.Unique(await Assert.ThrowsAsync<DbUpdateException>(() => s.Db().SaveChangesAsync())));
        s.Db().ChangeTracker.Clear();

        // AER-011: a supplemental report keeps the number and counts follow-ups.
        var factory = s.ServiceProvider.GetRequiredService<MedWatchFactory>();
        var (sup1, e1) = await factory.CreateSupplementalAsync(mwId, "additional_info", "New information");
        Assert.True(sup1 != null, e1);
        var sup = await s.LoadAsync(sup1!.Id);
        Assert.Equal(AerKeys.Supplemental, sup.KeywordKey("report_type"));
        Assert.Equal(expected, sup.Text("report_number"));
        Assert.Equal(1m, sup.Number("followup_number"));
        Assert.Equal(mwId, Assert.Single(sup.Links("original_medwatch")));
        var row = await s.Db().Set<MedWatchReport>().AsNoTracking().SingleAsync(x => x.MedWatchId == sup1.Id);
        Assert.False(row.IsInitial);
        Assert.Equal(before + 1, row.ReportSequence);
        s.Db().ChangeTracker.Clear();
        var bump = await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(sup1.Id, new() { ["mfr_sequence"] = 99m });
        Assert.False(bump.Ok); // the follow-up keeps the original number
        var (sup2, e2) = await factory.CreateSupplementalAsync(mwId, "correction", null);
        Assert.True(sup2 != null, e2);
        Assert.Equal(2m, (await s.LoadAsync(sup2!.Id)).Number("followup_number"));
        // Only completed reports have follow-ups.
        Assert.Null((await factory.CreateSupplementalAsync(sup1.Id, "correction", null)).Record);
    }

    [Fact] // AER-005 / INV-023: sequential numbers per code and year, in the submit transaction
    public async Task Sequential_numbers_continue_per_code_and_restart_each_year()
    {
        using var s = fx.As("rnovak");
        var numbers = s.ServiceProvider.GetRequiredService<ReportNumberService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => numbers.NextAsync(AerKeys.Manufacturer, "7654321", 2031, 1)); // only inside a transaction
        await using var tx = await s.Db().Database.BeginTransactionAsync();
        Assert.Equal(41, await numbers.NextAsync(AerKeys.Manufacturer, "7654321", 2031, 41));
        Assert.Equal(42, await numbers.NextAsync(AerKeys.Manufacturer, "7654321", 2031, 41));
        Assert.Equal(1, await numbers.NextAsync(AerKeys.Manufacturer, "7654321", 2032, 1));   // new year, new sequence
        Assert.Equal(1, await numbers.NextAsync(AerKeys.Importer, "7654321", 2031, 1));       // importer numbers are their own sequence
        await tx.RollbackAsync(); // a rolled-back submission gives its numbers back
        await using var tx2 = await s.Db().Database.BeginTransactionAsync();
        Assert.Equal(41, await numbers.NextAsync(AerKeys.Manufacturer, "7654321", 2031, 41));
        await tx2.RollbackAsync();
    }

    [Fact] // AER-008 (4), AER-010, SAF-020
    public async Task Rejections_and_overdue_acknowledgements_raise_safety_critical_alerts_and_an_urgent_task()
    {
        await EnsureSetupAsync();
        try
        {
            // Rejected by ACK3.
            await SetGatewayAsync("simulated", "reject_ack3");
            var (_, t1) = await ReportableComplaintAsync("Rejected submission complaint");
            var mw1 = await CreateMedWatchAsync(t1);
            await CompleteContentAsync(mw1);
            await SubmitAsync(mw1);
            await MonitorAsync(DateTime.UtcNow.AddMinutes(4));
            await MonitorAsync(DateTime.UtcNow.AddMinutes(5)); // idempotent: no second alert

            using var s = fx.As("rnovak");
            var sub = await s.Db().Set<EmdrSubmission>().AsNoTracking().SingleAsync(x => x.MedWatchId == mw1);
            Assert.Equal(EmdrStatus.Rejected, sub.Status);
            Assert.Equal("rejected", (await s.LoadAsync(mw1)).KeywordKey("submission_status"));
            var rnovak = s.UserId("rnovak");
            Assert.Single(await s.Db().Notifications.AsNoTracking().Where(n => n.RecordId == mw1 && n.UserId == rnovak && n.Kind == NotificationKind.SafetyCritical &&
                n.Title.StartsWith("eMDR submission rejected")).ToListAsync());
            var tasks = await s.ServiceProvider.GetRequiredService<RecordService>().ChildrenAsync(mw1, "action_item");
            Assert.Contains(tasks, t => t.Title.StartsWith("URGENT") && t.PhaseType == PhaseType.Normal);
            Assert.Contains(await s.ServiceProvider.GetRequiredService<RecordService>().ChildrenAsync(mw1, AerKeys.Ack),
                a => a.Title.StartsWith("ACK3"));
            await UpdateOkAsync(s, mw1, new() { ["confirm_final_ack"] = true });
            Assert.Contains((await s.RouteAsync(mw1, "completed")).Errors, e => e.Message.Contains("rejected"));
            // Correct and resubmit: the report keeps its number and gets a new submission.
            var number = (await s.LoadAsync(mw1)).Text("report_number");
            AssertOk(await s.RouteAsync(mw1, "draft", comment: "Fix and resubmit"));
            await SetGatewayAsync("simulated", "accept");
            foreach (var t in tasks.Where(t => t.PhaseType == PhaseType.Normal))
                await UpdateOkAsync(s, t.Id, new() { ["action_taken"] = "Corrected section D.", ["completed_on"] = BusinessCalendar.Today });
            using (var a = fx.As("rnovak"))
                foreach (var t in tasks.Where(t => t.PhaseType == PhaseType.Normal)) AssertOk(await a.RouteAsync(t.Id, "completed"));
            await SubmitAsync(mw1);
            var resub = await s.Db().Set<EmdrSubmission>().AsNoTracking().Where(x => x.MedWatchId == mw1).OrderBy(x => x.Attempt).ToListAsync();
            Assert.Equal(2, resub.Count);
            Assert.True(resub[0].Superseded);
            Assert.Equal(number, resub[1].ReportNumber);

            // Acknowledgements that never come.
            await SetGatewayAsync("simulated", "withhold_ack3");
            var (_, t2) = await ReportableComplaintAsync("Overdue submission complaint");
            var mw2 = await CreateMedWatchAsync(t2);
            await CompleteContentAsync(mw2);
            await SubmitAsync(mw2);
            await MonitorAsync(DateTime.UtcNow.AddMinutes(30)); // ACK1 and ACK2 only, still inside the 60-minute receipt delay
            Assert.Null((await s.Db().Set<EmdrSubmission>().AsNoTracking().SingleAsync(x => x.MedWatchId == mw2)).OverdueAlertedAt);
            await MonitorAsync(DateTime.UtcNow.AddMinutes(90));
            var late = await s.Db().Set<EmdrSubmission>().AsNoTracking().SingleAsync(x => x.MedWatchId == mw2);
            Assert.NotNull(late.Ack2At);
            Assert.Null(late.Ack3At);
            Assert.NotNull(late.OverdueAlertedAt);
            Assert.Equal("overdue", (await s.LoadAsync(mw2)).KeywordKey("submission_status"));
            Assert.Contains(await s.Db().Notifications.AsNoTracking().Where(n => n.RecordId == mw2 && n.Kind == NotificationKind.SafetyCritical).Select(n => n.Title).ToListAsync(),
                t => t.StartsWith("eMDR acknowledgement overdue"));
        }
        finally
        {
            await SetGatewayAsync("simulated", "accept");
        }
    }

    [Fact] // AER-008: the manual (ESG WebTrader) gateway
    public async Task Manual_gateway_records_the_upload_and_each_acknowledgement_by_hand()
    {
        await EnsureSetupAsync();
        try
        {
            await SetGatewayAsync("manual", "accept");
            var (_, treeId) = await ReportableComplaintAsync("Manual gateway complaint");
            var mwId = await CreateMedWatchAsync(treeId);
            await CompleteContentAsync(mwId);
            await SubmitAsync(mwId);

            using var s = fx.As("rnovak");
            Assert.Equal("awaiting_upload", (await s.LoadAsync(mwId)).KeywordKey("submission_status"));
            var behavior = s.ServiceProvider.GetServices<RecordBehavior>().OfType<MedWatchBehavior>().Single();
            async Task<ActionOutcome> Run(string key, Dictionary<string, string> values) =>
                await behavior.ExecuteAsync(new RecordContext(await s.LoadAsync(mwId), s.ServiceProvider), key,
                    new ActionInput { Values = values.ToDictionary(kv => kv.Key, kv => new[] { kv.Value }) });
            var tz = s.ServiceProvider.GetRequiredService<RecordService>().TimeZone;
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            Dictionary<string, string> At(DateTime local, Dictionary<string, string>? more = null)
            {
                var d = new Dictionary<string, string> { ["date"] = local.ToString("yyyy-MM-dd"), ["time"] = local.ToString("HH:mm") };
                foreach (var (k, v) in more ?? []) d[k] = v;
                return d;
            }
            Assert.False((await Run("aer-ack", At(now, new() { ["type"] = "ack1", ["result"] = "accepted" }))).Success); // upload first
            Assert.True((await Run("aer-upload", At(now))).Success);
            Assert.False((await Run("aer-ack", At(now, new() { ["type"] = "ack2", ["result"] = "accepted" }))).Success); // ACK1 first
            Assert.True((await Run("aer-ack", At(now, new() { ["type"] = "ack1", ["result"] = "accepted" }))).Success);
            Assert.True((await Run("aer-ack", At(now, new() { ["type"] = "ack2", ["result"] = "accepted", ["reference"] = "CORE-1" }))).Success);
            Assert.False((await Run("aer-ack", At(now, new() { ["type"] = "ack3", ["result"] = "rejected" }))).Success); // a rejection needs its error text
            Assert.True((await Run("aer-ack", At(now, new() { ["type"] = "ack3", ["result"] = "accepted" }))).Success);
            var acks = await s.ServiceProvider.GetRequiredService<RecordService>().ChildrenAsync(mwId, AerKeys.Ack);
            Assert.Equal(3, acks.Count);
            await UpdateOkAsync(s, mwId, new() { ["confirm_final_ack"] = true });
            AssertOk(await s.RouteAsync(mwId, "completed"));
        }
        finally
        {
            await SetGatewayAsync("simulated", "accept");
        }
    }

    [Fact] // INT-902
    public async Task The_emdr_profile_is_admin_only_environment_bound_and_keeps_secrets_encrypted()
    {
        await EnsureSetupAsync();
        using (var u = fx.As("rnovak"))
            Assert.False((await u.ServiceProvider.GetRequiredService<EmdrProfileService>().SaveAsync(new("test", "X", "Y", "z@z.example", null, null, null, null, "manual", "accept"))).Ok);

        using var a = fx.As("admin");
        var svc = a.ServiceProvider.GetRequiredService<EmdrProfileService>();
        Assert.True((await svc.SetPasswordAsync("S3cret-ESG-pass!")).Ok);
        var p = (await svc.CurrentAsync())!;
        Assert.NotNull(p.EsgPasswordProtected);
        Assert.DoesNotContain("S3cret-ESG-pass!", p.EsgPasswordProtected);
        Assert.Equal("S3cret-ESG-pass!", svc.RevealPasswordForGateway(p));

        var cert = new EmdrProfileService.CertificateInput("emdr.acme.example", "Acme Medical", "RA", "Kalamazoo", "MI", "US", "emdr@acme.example");
        Assert.False((await svc.GenerateCertificateAsync(cert with { CommonName = "acme<script>" }, acknowledged: true)).Ok);
        Assert.False((await svc.GenerateCertificateAsync(cert, acknowledged: false)).Ok); // the warning must be acknowledged
        Assert.True((await svc.GenerateCertificateAsync(cert, acknowledged: true)).Ok);
        a.Db().ChangeTracker.Clear();
        p = (await svc.CurrentAsync())!;
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", p.CertPem);
        Assert.DoesNotContain("PRIVATE KEY", p.PrivateKeyProtected);
        Assert.Null(p.CertFdaConfirmedAt);
        Assert.Contains("CN=emdr.acme.example", EmdrProfileService.Describe(p.CertPem)!.Subject);
        using (var x = svc.SigningCertificateForGateway(p)) Assert.True(x!.HasPrivateKey);
        Assert.False((await a.Db().Set<Notification>().AsNoTracking().AnyAsync(n => n.Body != null && n.Body.Contains("S3cret-ESG-pass!"))));
        Assert.False(await a.Db().ConfigAudit.AsNoTracking().AnyAsync(c => (c.After != null && c.After.Contains("S3cret")) || (c.Before != null && c.Before.Contains("S3cret"))));

        // One profile per environment (unique environment key).
        a.Db().ChangeTracker.Clear();
        a.Db().Set<EmdrProfile>().Add(new EmdrProfile { EnvironmentKey = p.EnvironmentKey, EnvironmentName = "dup", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => a.Db().SaveChangesAsync());
        a.Db().ChangeTracker.Clear();

        // The simulated gateway is refused in production.
        var settings = await a.Db().Settings.FirstAsync();
        var type = settings.EnvironmentType;
        await a.Db().Settings.ExecuteUpdateAsync(x => x.SetProperty(s => s.EnvironmentType, EnvironmentType.Production));
        try
        {
            using var b = fx.As("admin");
            var res = await b.ServiceProvider.GetRequiredService<EmdrProfileService>().SaveAsync(new("production", "Acme", "ACME", "e@acme.example", null, null, null, null, "simulated", "accept"));
            Assert.False(res.Ok);
            Assert.Contains(res.Errors, e => e.Contains("production", StringComparison.OrdinalIgnoreCase));
            // And this environment-bound profile isn't the production environment's profile.
            Assert.Null(await b.ServiceProvider.GetRequiredService<EmdrProfileService>().CurrentAsync());
        }
        finally
        {
            await a.Db().Settings.ExecuteUpdateAsync(x => x.SetProperty(s => s.EnvironmentType, type));
        }
    }

    [Fact] // AER-001: signature meanings from the profile; product-code import
    public async Task Profile_signature_meanings_apply_to_the_routes_and_product_codes_import()
    {
        await EnsureSetupAsync();
        using var s = fx.As("rnovak");
        var setup = s.ServiceProvider.GetRequiredService<AerSetup>();
        var p = (await setup.ProfileAsync())!;
        try
        {
            Assert.True((await setup.SaveProfileAsync(new(p.MappingEnabled, p.ReceiptDelayMinutes, "Sent to FDA under 21 CFR 803", p.SignComplete, p.SignVoid,
                p.DefaultApprovers, [.. p.MappedGroups]))).Ok);
            var cfg = s.ServiceProvider.GetRequiredService<ConfigCache>().Current;
            var wf = cfg.Form(AerKeys.MedWatch).Workflows.Single();
            Assert.Equal("Sent to FDA under 21 CFR 803", wf.Routes.Single(r => r.FromPhase.Key == "approval" && r.ToPhase.Key == "submitted").SignatureMeaning);
            Assert.True(await s.Db().ConfigAudit.AsNoTracking().AnyAsync(c => c.ObjectType == "Route" && c.After!.Contains("21 CFR 803")));
        }
        finally
        {
            Assert.True((await setup.SaveProfileAsync(new(p.MappingEnabled, p.ReceiptDelayMinutes, p.SignSubmit, p.SignComplete, p.SignVoid, p.DefaultApprovers, [.. p.MappedGroups]))).Ok);
        }

        var bad = await setup.ImportProductCodesAsync("code,device name\nFPA,\"Set, Administration, Intravascular\",2\nXX1,Bad code");
        Assert.False(bad.Ok);
        Assert.DoesNotContain(await setup.ProductCodesAsync(), c => c.Code == "FPA"); // nothing saved
        var ok = await setup.ImportProductCodesAsync("code,device name,class,regulation\nFPA,\"Set, Administration, Intravascular\",2,880.5440\nFRN,\"Pump, Infusion\",2,880.5725");
        Assert.True(ok.Ok, string.Join("; ", ok.Errors));
        Assert.Equal(1, ok.Created);
        var fpa = (await setup.ProductCodesAsync()).Single(c => c.Code == "FPA");
        Assert.Equal("Set, Administration, Intravascular", fpa.DeviceName);
        Assert.Equal("880.5440", fpa.Regulation);
    }
}
