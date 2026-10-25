using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;
using Vellum.Modules.Quality;

namespace Vellum.Modules.AdverseEvents;

/// <summary>One row of the complaint → MedWatch data mapping (CMP-003), shown on the setup page.</summary>
public sealed record ComplaintMappingRow(string Group, string ComplaintData, string MedWatchItem);

/// <summary>
/// CMP-005: creates a MedWatch from a reportable MDR decision tree, carrying the mapped complaint data; AER-011: creates supplemental
/// reports from a completed MedWatch. INV-047 is checked first for a friendly message and enforced by the database index.
/// </summary>
[ScopedService]
public sealed class MedWatchFactory(VellumDbContext db, ConfigCache cfg, RecordService records, AccessService access, AerSetup setup,
    BusinessCalendar calendar, CurrentUser me, AuditService audit, AerWriteGuard guard)
{
    private ConfigModel Cfg => cfg.Current;

    public static readonly IReadOnlyList<ComplaintMappingRow> Mapping =
    [
        new("event", "Description, awareness date, site where reported", "B5 description, G4 date received, location"),
        new("event", "MDR tree answers (death, serious injury, malfunction) and rationale", "H1 event type, B1 type of report, H10 narrative"),
        new("event", "Injury, death, medical intervention; reporter type", "B2 outcomes, G3 report source"),
        new("patient", "Patient identifier, age, sex", "A1, A2, A3"),
        new("device", "Product, model, lot, serial, UDI, device returned", "D1 brand, D4 identifiers, D9 availability, D2/D2b via the product code table"),
        new("reporter", "Reporter name, organization, phone, email, country, reporter type", "E1 to E3"),
        new("evaluation", "Investigation summary, failure mode, device returned", "H3 evaluation, Annex A device problem code"),
    ];

    /// <summary>Failure modes that map to an IMDRF Annex A level-1 term. The user reviews and refines the codes.</summary>
    public static readonly IReadOnlyDictionary<string, (string Code, string Term)> FailureModeCodes = new Dictionary<string, (string, string)>
    {
        ["occlusion_alarm"] = ("A16", "Protective Measures Problem"),
        ["mechanical"] = ("A05", "Mechanical Problem"),
        ["battery"] = ("A07", "Electrical/Electronic Property Problem"),
        ["contamination"] = ("A18", "Contamination/Decontamination Problem"),
        ["label"] = ("A21", "Labelling, Instructions for Use or Training Problem"),
        ["other"] = ("A26", "Appropriate Term/Code Not Available"),
    };

    private FormDefinition? MedWatchForm => Cfg.FormsByKey.GetValueOrDefault(AerKeys.MedWatch);

    /// <summary>Why "Create MedWatch" isn't possible for this tree right now, or null.</summary>
    public async Task<string?> BlockerForTreeAsync(Guid treeId)
    {
        if (MedWatchForm is not { } form) return "Adverse Event Reporting isn't installed.";
        if (!access.CanCreate(form)) return "You don't have permission to create MedWatch reports.";
        if (await setup.ProfileAsync() == null) return "Set up the Adverse Event Reporting application profile first (AER-002).";
        var tree = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == treeId);
        if (tree == null || tree.PhaseType == PhaseType.Voided) return "The MDR decision tree is voided.";
        var reportable = await db.RecordValues.AsNoTracking().AnyAsync(v => v.RecordId == treeId && v.RowId == null && v.FieldKey == "mdr_reportable" && v.RefId == 1);
        if (!reportable) return "The MDR decision tree hasn't concluded the event is reportable.";
        var existing = await ExistingInitialAsync(treeId);
        return existing != null ? $"{existing} is already the initial MedWatch for this MDR decision (INV-047)." : null;
    }

    /// <summary>The non-voided initial / 5-day MedWatch (MFR numbering) of a tree, if any.</summary>
    public async Task<string?> ExistingInitialAsync(Guid treeId)
    {
        var id = await db.Set<MedWatchReport>().AsNoTracking()
            .Where(x => x.MdrTreeId == treeId && x.IsInitial && !x.IsVoided && x.ReporterType == AerKeys.Manufacturer).Select(x => (Guid?)x.MedWatchId).FirstOrDefaultAsync();
        return id == null ? null : await db.Records.AsNoTracking().Where(r => r.Id == id).Select(r => r.Number).FirstOrDefaultAsync();
    }

    public async Task<(Record? Record, string? Error)> CreateFromTreeAsync(Guid treeId)
    {
        if (await BlockerForTreeAsync(treeId) is { } blocker) return (null, blocker);
        var tree = await records.LoadRequiredAsync(treeId);
        if (tree.Record.ParentId is not { } complaintId) return (null, "The MDR decision tree isn't attached to a complaint.");
        var c = await records.LoadRequiredAsync(complaintId);
        var profile = (await setup.ProfileAsync())!;
        var location = c.KeywordKey("site_reported");
        var mapping = await setup.MappingForAsync(location);
        var fiveDay = tree.KeywordKey("mdr_report_type") == "five_day";
        var awareness = c.Date("awareness_date");
        var deadline = (await ReportingClock.ComputeAsync(awareness, fiveDay ? "reportable_5" : "reportable_30", false, null, null, null, calendar))
            .FirstOrDefault(d => d.Jurisdiction == "FDA");
        var owner = me.IsSystem ? c.UserId("owner") ?? c.Record.CreatedById : me.Id;

        var v = new Dictionary<string, object?>
        {
            ["subject"] = Cut($"{(fiveDay ? "5-day" : "30-day")} MedWatch: {c.Text("subject")}", 200),
            ["owner"] = owner, ["report_type"] = fiveDay ? AerKeys.FiveDay : AerKeys.Initial, ["location"] = location,
            ["awareness_date"] = awareness, ["report_due"] = deadline?.Due, ["report_rule"] = deadline?.Rule,
            ["complaint"] = c.Record.Id, ["mdr_tree"] = tree.Record.Id, ["reporter_type"] = mapping?.ReporterType ?? AerKeys.Manufacturer,
            ["approvers"] = profile.DefaultApprovers.Where(a => a != owner).ToList(), ["report_date"] = BusinessCalendar.Today,
            ["mfr_name"] = mapping?.MfrName, ["mfr_address"] = mapping?.MfrAddress, ["contact_name"] = mapping?.ContactName,
            ["contact_address"] = mapping?.ContactAddress, ["contact_phone"] = mapping?.ContactPhone,
        };
        var injury = c.Bool("injury") == true;
        var death = c.Bool("death") == true;
        var patient = c.Bool("patient_involved") == true;
        if (profile.Maps("event"))
        {
            v["event_description"] = c.Text("description");
            var kinds = new List<string>();
            if (patient && (injury || death || c.Bool("medical_intervention") == true)) kinds.Add("adverse_event");
            if (tree.Bool("q_malfunction") == true || c.KeywordKey("failure_mode") is { } fm && fm != "no_failure") kinds.Add("product_problem");
            v["event_kind"] = kinds.Count > 0 ? kinds : new List<string> { "product_problem" };
            v["event_type"] = tree.Bool("q_death") == true ? "death" : tree.Bool("q_serious_injury") == true ? "serious_injury" : tree.Bool("q_malfunction") == true ? "malfunction" : "other";
            var outcomes = new List<string>();
            if (death) outcomes.Add("death");
            if (c.Bool("medical_intervention") == true) outcomes.Add("intervention");
            else if (injury) outcomes.Add("other_serious");
            if (outcomes.Count > 0) v["outcomes"] = outcomes;
            v["report_sources"] = new List<string>
            {
                c.KeywordKey("reporter_type") switch { "hcp" or "biomed" => "health_professional", "distributor" => "distributor", "sales" => "company_rep", "patient" => "consumer", _ => "other" },
            };
            if (tree.Text("mdr_rationale") is { } rationale) v["mfr_narrative"] = $"MDR reportability ({tree.Record.Number}): {rationale}";
        }
        if (profile.Maps("patient") && patient)
        {
            v["patient_identifier"] = c.Text("patient_identifier") is { } pid ? Cut(pid, 10) : null;
            v["patient_age"] = c.Number("patient_age");
            v["patient_age_unit"] = c.Number("patient_age") != null ? "years" : null;
            v["patient_sex"] = c.KeywordKey("patient_sex");
        }
        if (profile.Maps("device"))
        {
            v["brand_name"] = c.Text("product");
            v["model_number"] = c.Text("model");
            v["lot_number"] = c.Text("lot_number");
            v["serial_number"] = c.Text("serial_number");
            v["udi"] = c.Text("udi");
            v["device_available"] = c.Bool("device_returned") switch { true => "yes", false => "no", _ => "unknown" };
            if (AerSetup.MatchDevice(await setup.ProductCodesAsync(), c.Text("product")) is { } pc)
            {
                v["product_code"] = pc.Code;
                v["common_name"] = pc.DeviceName;
            }
        }
        if (profile.Maps("reporter"))
        {
            v["reporter_name"] = c.Text("reporter_name");
            v["reporter_address"] = c.Text("reporter_organization");
            v["reporter_phone"] = c.Text("reporter_phone");
            v["reporter_email"] = c.Text("reporter_email");
            v["reporter_country"] = c.Text("reporter_country");
            var rt = c.KeywordKey("reporter_type");
            if (rt != null)
            {
                v["reporter_is_hcp"] = rt is "hcp" or "biomed";
                if (rt is "hcp" or "biomed") v["reporter_occupation"] = c.Keyword("reporter_type")?.Label;
            }
        }
        var codes = new List<Dictionary<string, object?>>();
        if (profile.Maps("evaluation"))
        {
            v["evaluation_summary"] = c.Text("investigation_summary");
            v["evaluated"] = c.Bool("device_returned") == true && c.Text("investigation_summary") != null ? "yes" : c.Bool("device_returned") == false ? "not_returned" : null;
            if (c.KeywordKey("failure_mode") is { } mode && FailureModeCodes.TryGetValue(mode, out var code))
                codes.Add(new() { ["code_type"] = "device_problem", ["code"] = code.Code, ["term"] = code.Term });
        }

        try
        {
            return await Tx.RunAsync(db, async () =>
            {
                var res = await records.CreateAsync(new CreateRequest
                {
                    FormKey = AerKeys.MedWatch, Values = v.Where(kv => kv.Value != null).ToDictionary(), SkipAccessCheck = true,
                    RowValues = codes.Count > 0 ? new() { ["problem_codes"] = codes } : null,
                    CreateComment = $"Created from {tree.Record.Number} for complaint {c.Record.Number} (CMP-005)",
                });
                if (!res.Ok) throw new WorkflowException("Couldn't create the MedWatch: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                var mw = res.Record!;
                await records.LinkAsync(c.Record.Id, mw.Id, LinkKind.Source);
                await records.LinkAsync(tree.Record.Id, mw.Id, LinkKind.Source);
                audit.Record(c.Record.Id, "MedWatch created", $"{mw.Number} ({(fiveDay ? "5-day" : "30-day")} report) created from {tree.Record.Number}");
                db.Comments.Add(new Comment { RecordId = c.Record.Id, IsSystem = true, Kind = "system", At = DateTime.UtcNow, UserId = me.IsSystem ? null : me.Id,
                    Text = $"Created MedWatch {mw.Number} from {tree.Record.Number}: {mw.Title}" });
                await db.SaveChangesAsync();
                return ((Record?)mw, (string?)null);
            });
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return (null, ex.Message);
        }
        catch (DbUpdateException ex) when (EmdrSubmissionService.Unique(ex))
        {
            db.ChangeTracker.Clear();
            return (null, $"An initial MedWatch already exists for {tree.Record.Number}. Only one initial (or 5-day) report is allowed per MDR decision unless it's voided (INV-047).");
        }
    }

    /// <summary>Fields a supplemental report doesn't copy from the report it follows.</summary>
    private static readonly HashSet<string> NotCopied =
    [
        "subject", "report_type", "original_medwatch", "followup_number", "followup_types", "changed_sections", "report_due", "report_rule", "report_date",
        "owner", "submission_status", "gateway", "submitted_at", "ack1_at", "ack2_at", "ack3_at", "confirm_final_ack", "completion_notes", "report_number",
    ];

    public async Task<(Record? Record, string? Error)> CreateSupplementalAsync(Guid medWatchId, string? followupType, string? note)
    {
        if (MedWatchForm is not { } form) return (null, "Adverse Event Reporting isn't installed.");
        if (!access.CanCreate(form)) return (null, "You don't have permission to create MedWatch reports.");
        var src = await records.LoadRequiredAsync(medWatchId);
        if (src.Record.PhaseType != PhaseType.Completed) return (null, "Supplemental reports are created from a completed MedWatch (AER-011).");
        var rootId = src.Links("original_medwatch").Cast<Guid?>().FirstOrDefault() ?? src.Record.Id;
        var root = rootId == src.Record.Id ? src : await records.LoadRequiredAsync(rootId);
        var next = (await db.Set<MedWatchReport>().Where(x => x.OriginalMedWatchId == rootId && !x.IsVoided).MaxAsync(x => x.FollowUpNumber) ?? 0) + 1;

        var values = new Dictionary<string, object?>();
        foreach (var f in form.Fields.Where(f => FieldCodec.StoresValues(f.Type) && !NotCopied.Contains(f.Key) && form.Sections.First(s => s.Key == f.SectionKey) is { IsSubform: false }))
            if (src.Data.Main.Has(f.Key)) values[f.Key] = src.Data.Main[f.Key].ToList();
        values["subject"] = Cut($"Supplemental #{next}: {root.Text("subject")}", 200);
        values["report_type"] = AerKeys.Supplemental;
        values["original_medwatch"] = rootId;
        values["followup_number"] = (decimal)next;
        if (followupType != null) values["followup_types"] = new List<string> { followupType };
        values["owner"] = me.IsSystem ? src.UserId("owner") : me.Id;
        values["report_date"] = BusinessCalendar.Today;
        values["report_due"] = BusinessCalendar.Today.AddDays(30);
        values["report_rule"] = "Supplemental report: 30 calendar days from receiving the new information (21 CFR 803.56)";
        if (src.UserIds("approvers") is { Count: > 0 } approvers && values["owner"] is int o) values["approvers"] = approvers.Where(a => a != o).ToList();
        var rows = src.Rows("problem_codes").Select(r => new Dictionary<string, object?>
        {
            ["code_type"] = r.Values["code_type"].ToList(), ["code"] = r.Values.Text("code"), ["term"] = r.Values.Text("term"),
        }).ToList();
        try
        {
            using (guard.Allow())
                return await Tx.RunAsync(db, async () =>
                {
                    var res = await records.CreateAsync(new CreateRequest
                    {
                        FormKey = AerKeys.MedWatch, Values = values, SkipAccessCheck = true, RowValues = rows.Count > 0 ? new() { ["problem_codes"] = rows } : null,
                        CreateComment = $"Supplemental report #{next} to {root.Text("report_number") ?? root.Record.Number} (AER-011)" + (string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}"),
                    });
                    if (!res.Ok) throw new WorkflowException("Couldn't create the supplemental report: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                    await records.LinkAsync(src.Record.Id, res.Record!.Id, LinkKind.Source);
                    audit.Record(src.Record.Id, "Supplemental report created", $"{res.Record.Number} (follow-up #{next})", reason: note);
                    await db.SaveChangesAsync();
                    return ((Record?)res.Record, (string?)null);
                });
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return (null, ex.Message);
        }
        catch (DbUpdateException ex) when (EmdrSubmissionService.Unique(ex))
        {
            db.ChangeTracker.Clear();
            return (null, $"Follow-up #{next} was just created by someone else. Reload and try again.");
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
