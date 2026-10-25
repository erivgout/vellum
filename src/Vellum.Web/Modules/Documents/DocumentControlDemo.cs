using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Modules.Pdf;
using Vellum.Modules.Training;
using Vellum.Packaging;
using S = Vellum.Modules.Documents.DemoPdf.Section;

namespace Vellum.Modules.Documents;

/// <summary>
/// Demo data: twelve controlled documents of Acme Medical Devices (Kalamazoo, Boston, Galway) in every lifecycle state,
/// built through the real engine so signatures, audit trails, revision links and training assignments are genuine.
/// </summary>
public sealed partial class DocumentControlApp
{
    public async Task SeedDemoAsync(DemoContext d)
    {
        // 1. Cleanroom gowning: rev A effective 14 months ago, superseded by rev B (change request), course carried forward.
        var gownA = await NewAsync(d, "akim", 430, "procedure", "Cleanroom gowning and entry", "lgarcia", ["sortiz", "hmoreau"], "operations", ["kzo"],
            "Defines how personnel gown for and enter the ISO 7 cleanroom (Building 2, Kalamazoo) so product is protected from particulate and microbial contamination.",
            "New procedure for the cleanroom commissioned in 2025.", Gowning("A", false), training: true);
        var gownCourseA = await SetUpTrainingAsync(d, gownA, "lgarcia", "Cleanroom gowning and entry", 12, 30, ["ebaker", "hmoreau"]);
        await ApproveAsync(d, gownA, ["sortiz", "hmoreau"], "Template and numbering checked.");
        await CompleteAllAsync(d, gownCourseA);
        await d.RouteAsync(gownA, DocKeys.Approved, asUser: "pshah", comment: "Released after training completed.");
        var gownB = await ChangeAsync(d, gownA, "lgarcia", "lgarcia", "akim", 70,
            "Add glove change frequency and sticky-mat replacement",
            "EM trend review (Q2) showed glove-fingertip counts rising after 2 hours. Add a 2-hour glove change and daily sticky-mat replacement; align gowning order with the new airlock layout.",
            ["nflores"]);
        await ReplacePrimaryAsync(d, gownB, "akim", Gowning("B", true), "SOP cleanroom gowning rev B.pdf");
        await d.UpdateAsync(gownB, new() { ["training_required"] = true }, "akim");
        await ApproveAsync(d, gownB, ["sortiz", "hmoreau"], "Rev B formatting checked against SOP template T-001.");
        var gownCourseB = (await Training(d).CourseIdsForDocumentAsync(gownB)).Single();
        await CompleteAsync(d, gownCourseB, "akim", "ebaker", "hmoreau");
        await d.RouteAsync(gownB, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, gownA, effectiveDaysAgo: 420, archivedDaysAgo: 45);
        await BackdateAsync(d, gownB, effectiveDaysAgo: 45);
        await OverdueAsync(d, gownCourseB, "lgarcia", -6);

        // 2. Device history record review: rev A effective; rev B in Review with one of two reviewer signatures.
        var dhrA = await NewAsync(d, "jlee", 330, "procedure", "Device history record review", "sortiz", ["dokafor", "tbrooks"], "quality_assurance", ["kzo", "gal"],
            "Describes the review of device history records (DHR) before release of finished devices, as required by 21 CFR 820.184 and ISO 13485 clause 7.5.9.",
            "First issue: consolidates the Kalamazoo and Galway DHR checklists.", Dhr("A"), training: true);
        await SetUpTrainingAsync(d, dhrA, "sortiz", "Device history record review", null, null, ["jlee", "tbrooks"]);
        await ApproveAsync(d, dhrA, ["dokafor", "tbrooks"]);
        await CompleteAllAsync(d, (await Training(d).CourseIdsForDocumentAsync(dhrA)).Single());
        await d.RouteAsync(dhrA, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, dhrA, effectiveDaysAgo: 300);
        var dhrB = await ChangeAsync(d, dhrA, "sortiz", "sortiz", "jlee", 9,
            "Require electronic label reconciliation evidence in the DHR",
            "CAPA-00002 (mislabeled lot) showed the DHR review didn't check label reconciliation. Add the reconciliation printout and second-person check to the checklist.", ["pshah"]);
        await ReplacePrimaryAsync(d, dhrB, "jlee", Dhr("B"), "SOP DHR review rev B.pdf");
        await d.UpdateAsync(dhrB, new() { ["training_required"] = true }, "jlee");
        await d.RouteAsync(dhrB, DocKeys.DcReview);
        await d.RouteAsync(dhrB, DocKeys.InitialApproval, asUser: "pshah");
        await d.RouteAsync(dhrB, DocKeys.Review, asUser: "sortiz", comment: "Content matches the CAPA action. Over to reviewers.");
        await d.RouteAsync(dhrB, DocKeys.FinalApproval, asUser: "dokafor", comment: "Checklist item 7 is clear. Approved.");

        // 3. Complaint handling: effective, with a change request waiting for the owner's decision.
        var cmp = await NewAsync(d, "rnovak", 260, "procedure", "Complaint handling", "dokafor", ["sortiz", "jlee"], "regulatory", ["kzo", "bos", "gal"],
            "Defines how product complaints are received, evaluated for MDR / vigilance reportability, investigated and closed (21 CFR 820.198, EU MDR Article 87).",
            "Replaces the legacy complaint SOP after the move to Vellum.", Complaints(), training: true);
        await SetUpTrainingAsync(d, cmp, "dokafor", "Complaint handling and reportability", 24, 30, ["rnovak", "jlee", "tbrooks", "sortiz"]);
        await ApproveAsync(d, cmp, ["sortiz", "jlee"]);
        var cmpCourse = (await Training(d).CourseIdsForDocumentAsync(cmp)).Single();
        await CompleteAsync(d, cmpCourse, "jlee", "sortiz");
        await d.RouteAsync(cmp, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, cmp, effectiveDaysAgo: 200);
        await OverdueAsync(d, cmpCourse, "rnovak", -3);
        await d.CreateAsync(DocKeys.ChangeRequest, new()
        {
            ["target_document"] = cmp, ["summary"] = "Add the 2-day eMDR decision timer for serious injury complaints",
            ["reason"] = "Internal audit IA-2026-09 observed reportability decisions logged after 5 days. Add a 2 business-day decision target and escalation to Regulatory.",
            ["approver"] = d.User("dokafor"), ["assign_to"] = new List<int> { d.User("rnovak") }, ["notify"] = new List<int> { d.User("jlee") },
            ["revision_due"] = DemoContext.Days(25),
        }, "rnovak", parent: cmp, daysAgo: 2);

        // 4. Calibration control: periodic review is overdue (effective 25 months ago, 24-month cycle).
        var cal = await NewAsync(d, "hmoreau", 780, "procedure", "Calibration control", "lgarcia", ["ebaker", "sortiz"], "facilities", ["kzo", "gal"],
            "Defines calibration intervals, out-of-tolerance evaluation and recall of measuring equipment (ISO 13485 clause 7.6).",
            "First issue.", Calibration(), training: true);
        await SetUpTrainingAsync(d, cal, "lgarcia", "Calibration control", 24, 30, ["ebaker", "hmoreau"]);
        await ApproveAsync(d, cal, ["ebaker", "sortiz"]);
        await CompleteAllAsync(d, (await Training(d).CourseIdsForDocumentAsync(cal)).Single());
        await d.RouteAsync(cal, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, cal, effectiveDaysAgo: 760);

        // 5. Supplier qualification: review due next month, periodic review in progress.
        var sup = await NewAsync(d, "cwu", 720, "procedure", "Supplier qualification and monitoring", "sortiz", ["dokafor", "jlee"], "supply_chain", ["kzo", "bos", "gal"],
            "Defines how suppliers of production materials and services are evaluated, approved, monitored and disqualified (ISO 13485 clause 7.4.1).",
            "First issue.", Suppliers(), training: false, justification: "Applies only to Supplier Quality, who wrote it; no new tasks for others.");
        await ApproveAsync(d, sup, ["dokafor", "jlee"]);
        await d.RouteAsync(sup, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, sup, effectiveDaysAgo: 700);
        await d.CreateAsync(DocKeys.PeriodicReview, new() { ["target_document"] = sup, ["reviewer"] = d.User("sortiz") }, "sortiz", parent: sup, daysAgo: 3, due: DemoContext.Days(12));

        // 6. Quality policy: effective; annual read-and-understood for all employees.
        var pol = await NewAsync(d, "sortiz", 180, "policy", "Quality policy", "dokafor", ["rnovak", "lgarcia"], "quality", ["kzo", "bos", "gal", "sin"],
            "States Acme Medical Devices' commitment to safe, effective devices, regulatory compliance and continual improvement of the quality management system.",
            "Updated for the 2026 management review objectives.", Policy(), training: true);
        var polCourse = await CourseAsync(d, "kpatel", "Quality policy acknowledgment", pol, 12, 30, groups: ["all-employees"]);
        await ApproveAsync(d, pol, ["rnovak", "lgarcia"]);
        await CompleteAsync(d, polCourse, "dokafor", "sortiz", "jlee", "pshah", "tbrooks", "rnovak", "cwu", "akim", "lgarcia", "mchen", "hmoreau", "kpatel");
        await d.RouteAsync(pol, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, pol, effectiveDaysAgo: 150);
        await OverdueAsync(d, polCourse, "ebaker", -9);

        // 7. Torque verification WI: rev B (minor/editorial shortcut) supersedes rev A.
        var tqA = await NewAsync(d, "akim", 320, "work_instruction", "Torque verification at final assembly station 3", "lgarcia", ["jlee", "nflores"], "operations", ["kzo"],
            "Step-by-step torque verification of VX-200 infusion pump housing screws at final assembly station 3.",
            "New work instruction from CAPA-00001 (torque drift).", Torque("A", "Mountz MTD-50"), training: true);
        var tqCourse = await SetUpTrainingAsync(d, tqA, "lgarcia", "Torque verification at station 3", null, null, []);
        await ApproveAsync(d, tqA, ["jlee", "nflores"]);
        await CompleteAllAsync(d, tqCourse);
        await d.RouteAsync(tqA, DocKeys.Approved, asUser: "pshah");
        var tqB = await ChangeAsync(d, tqA, "lgarcia", "lgarcia", "akim", 25,
            "Correct the torque driver model reference",
            "The station now uses the Mountz MTD-50i (same range and accuracy). Editorial correction of the model number in step 2; no change to the method.", []);
        await ReplacePrimaryAsync(d, tqB, "akim", Torque("B", "Mountz MTD-50i"), "WI torque verification rev B.pdf");
        await d.UpdateAsync(tqB, new() { ["minor_change"] = true, ["training_required"] = false, ["training_justification"] = "Editorial correction of an equipment model number; the method is unchanged." }, "akim");
        await d.RouteAsync(tqB, DocKeys.AwaitingRelease, comment: "Editorial change only: equipment model number (DOC-031).");
        await d.RouteAsync(tqB, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, tqA, effectiveDaysAgo: 300, archivedDaysAgo: 20);
        await BackdateAsync(d, tqB, effectiveDaysAgo: 20);

        // 8. Bioburden test method: awaiting release, effective in 10 days, training-gated at 100%, release already signed.
        var tm = await NewAsync(d, "hmoreau", 21, "test_method", "Bioburden testing of sterile tubing sets", "sortiz", ["jlee", "dokafor"], "qc_laboratory", ["gal"],
            "Membrane-filtration method for determining bioburden on ST-40 sterile tubing sets before EO sterilization (ISO 11737-1).",
            "New method transferred from the contract laboratory.", Bioburden(), training: true,
            extra: new() { ["effective_delay"] = 10, ["training_gated"] = true, ["training_gate_percent"] = 100 });
        var tmCourse = await SetUpTrainingAsync(d, tm, "sortiz", "Bioburden testing (TM, membrane filtration)", 24, 30, ["jlee", "tbrooks"]);
        await ApproveAsync(d, tm, ["jlee", "dokafor"]);
        await CompleteAsync(d, tmCourse, "hmoreau");
        await ActAsync(d, tm, "schedule_release", "pshah");

        // 9. Internal audit program: new procedure in Document Control Review.
        var ia = await NewAsync(d, "tbrooks", 6, "procedure", "Internal audit program", "sortiz", ["jlee", "dokafor"], "quality_assurance", ["kzo", "bos", "gal"],
            "Defines the risk-based internal audit schedule, auditor qualification, audit conduct and follow-up of findings (ISO 13485 clause 8.2.4).",
            "New procedure: splits the audit program out of the QMS manual.", Audits(), training: true);
        await d.RouteAsync(ia, DocKeys.DcReview);

        // 10. VX-200 housing specification: draft.
        await NewAsync(d, "akim", 2, "specification", "VX-200 infusion pump housing specification", "lgarcia", ["jlee", "cwu"], "operations", ["kzo"],
            "Dimensional, material and cosmetic requirements for the VX-200 infusion pump front and rear housings (PC/ABS, UL 94 V-0).",
            "New specification for the VX-200 design transfer.", HousingSpec(), training: false, justification: "Specification for incoming inspection; covered by the inspection WI training.");

        // 11. Equipment calibration record form: effective.
        var frm = await NewAsync(d, "ebaker", 410, "form", "Equipment calibration record", "lgarcia", ["hmoreau"], "facilities", ["kzo", "gal"],
            "Form used to record as-found / as-left calibration readings, standards used and out-of-tolerance decisions.",
            "First issue.", CalForm(), training: null);
        await ApproveAsync(d, frm, ["hmoreau"]);
        await d.RouteAsync(frm, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, frm, effectiveDaysAgo: 400);

        // 12. Paper deviation log: obsolete (obsolete request accepted).
        var dev = await NewAsync(d, "jlee", 960, "form", "Paper deviation log", "sortiz", ["tbrooks"], "quality", ["kzo"],
            "Paper log for recording planned and unplanned deviations on the production floor.", "First issue.", DeviationLog(), training: null);
        await ApproveAsync(d, dev, ["tbrooks"]);
        await d.RouteAsync(dev, DocKeys.Approved, asUser: "pshah");
        await BackdateAsync(d, dev, effectiveDaysAgo: 940);
        var or = await d.CreateAsync(DocKeys.ObsoleteRequest, new()
        {
            ["target_document"] = dev, ["reason"] = "Deviations are now recorded electronically in Vellum Quality Events. The paper log is retired at all sites; remaining paper logs go to Records.",
        }, "pshah", parent: dev, daysAgo: 12);
        await d.RouteAsync(or.Id, "approval", asUser: "pshah", assignees: ["sortiz"]);
        await d.RouteAsync(or.Id, "accepted", asUser: "sortiz", comment: "Agreed. Electronic deviations have been live for 3 months.");

        // 13. Label printer setup WI: voided draft.
        var lbl = await NewAsync(d, "akim", 40, "work_instruction", "Label printer setup for ST-40 tubing sets", "lgarcia", ["jlee"], "operations", ["kzo"],
            "Setup of the Zebra ZT411 label printer for ST-40 tubing set labels.", "New work instruction.", LabelSetup(), training: false, justification: "Not yet decided.");
        await d.RouteAsync(lbl, DocKeys.Voided, asUser: "akim", comment: "Not needed: the label system vendor's validated setup procedure applies instead (see CAPA-00002).");

        await RestampRenditionsAsync(d);
    }

    /// <summary>
    /// Demo-only: renditions are stamped when a revision is released, with the real (today's) effective date; the demo then
    /// backdates effective dates. Re-stamp released revisions so their PDFs carry the dates shown on the records
    /// (DOC-022). Conversions are cached, so this only re-applies the watermark.
    /// </summary>
    private static async Task RestampRenditionsAsync(DemoContext d)
    {
        d.As("pshah");
        var queue = d.Services.GetRequiredService<PdfQueue>();
        var rs = d.Services.GetRequiredService<RecordService>();
        var formId = d.Config.Form(DocKeys.Document).Id;
        d.Db.ChangeTracker.Clear();
        var ids = await d.Db.Records.AsNoTracking().Where(r => r.FormId == formId && r.EffectiveDate != null && r.PhaseType == PhaseType.Completed)
            .Select(r => r.Id).ToListAsync();
        foreach (var id in ids)
        {
            d.Db.ChangeTracker.Clear();
            var snap = await rs.LoadRequiredAsync(id);
            var jobs = await queue.EnqueueAsync(snap, ["primary_file"], "demo");
            await queue.ProcessInlineAsync(jobs);
            await d.Db.SaveChangesAsync();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static TrainingService Training(DemoContext d) => d.Services.GetRequiredService<TrainingService>();

    private static async Task<Guid> NewAsync(DemoContext d, string author, int daysAgo, string type, string title, string owner, string[] reviewers,
        string dept, string[] sites, string purpose, string reason, IReadOnlyList<S> content, bool? training, string? justification = null,
        Dictionary<string, object?>? extra = null)
    {
        d.As(author);
        d.Db.ChangeTracker.Clear();
        var values = new Dictionary<string, object?>
        {
            ["doc_type"] = type, ["title"] = title, ["owner"] = d.User(owner), ["reviewers"] = reviewers.Select(d.User).ToList(),
            ["department"] = dept, ["sites"] = sites.ToList(), ["purpose"] = purpose, ["revision_reason"] = reason, ["training_required"] = training,
            ["training_justification"] = training == false ? justification ?? "No new or changed tasks." : null,
            ["distribution_groups"] = new List<string> { "quality" },
        };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        var res = await d.Services.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = DocKeys.Document, Values = values, SkipAccessCheck = true,
            CreatedAt = DateTime.UtcNow.AddDays(-daysAgo).AddHours(-Random.Shared.Next(1, 8)),
        });
        if (!res.Ok) throw new InvalidOperationException($"Demo document {title} failed: {string.Join("; ", res.Errors.Select(e => e.Message))}");
        // The controlled file is rendered once the number is known, so its header matches the record.
        await ReplacePrimaryAsync(d, res.Record!.Id, author, content, $"{res.Record.Number} {title}.pdf");
        return res.Record.Id;
    }

    private static async Task ReplacePrimaryAsync(DemoContext d, Guid id, string asUser, IReadOnlyList<S> content, string fileName)
    {
        d.As(asUser);
        d.Db.ChangeTracker.Clear();
        var rs = d.Services.GetRequiredService<RecordService>();
        var snap = await rs.LoadRequiredAsync(id);
        var pdf = DemoPdf.Render(snap.Record.Number, snap.Record.Revision ?? "A", snap.Record.Title, snap.Keyword("doc_type")?.Label ?? "Document",
            d.Directory.Name(snap.UserId("owner")), content);
        var res = await rs.SaveAsync(snap, new RecordData(), new SaveOptions
        {
            System = true, Uploads = [("primary_file", fileName, "application/pdf", new MemoryStream(pdf))],
            RemoveAttachmentIds = snap.AttachmentsOf("primary_file").Select(a => a.Id).ToList(),
        });
        if (!res.Ok) throw new InvalidOperationException("Demo file replacement failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    /// <summary>Draft → Document Control Review → Initial Approval → Review (every reviewer signs) → Final Approval → Awaiting Release.</summary>
    private static async Task ApproveAsync(DemoContext d, Guid id, string[] reviewers, string? dcComment = null)
    {
        await d.RouteAsync(id, DocKeys.DcReview);
        await d.RouteAsync(id, DocKeys.InitialApproval, asUser: "pshah", comment: dcComment);
        await d.RouteAsync(id, DocKeys.Review);
        foreach (var r in reviewers) await d.RouteAsync(id, DocKeys.FinalApproval, asUser: r);
        await d.RouteAsync(id, DocKeys.AwaitingRelease);
    }

    /// <summary>Change request raised and accepted (DOC-093). Returns the new Draft revision.</summary>
    private static async Task<Guid> ChangeAsync(DemoContext d, Guid docId, string requester, string approver, string assignee, int daysAgo,
        string summary, string reason, string[] notify)
    {
        var cr = await d.CreateAsync(DocKeys.ChangeRequest, new()
        {
            ["target_document"] = docId, ["summary"] = summary, ["reason"] = reason, ["approver"] = d.User(approver),
            ["assign_to"] = new List<int> { d.User(assignee) }, ["notify"] = notify.Select(d.User).ToList(), ["revision_due"] = DemoContext.Days(30),
        }, requester, parent: docId, daysAgo: daysAgo);
        await d.RouteAsync(cr.Id, "accepted", asUser: approver, comment: "Accepted: the change is justified.");
        d.Db.ChangeTracker.Clear();
        var family = await d.Db.Records.AsNoTracking().Where(r => r.Id == docId).Select(r => r.FamilyId).FirstAsync();
        return await d.Db.Records.AsNoTracking().Where(r => r.FamilyId == family).OrderByDescending(r => r.RevisionOrdinal).Select(r => r.Id).FirstAsync();
    }

    private static async Task<Guid> SetUpTrainingAsync(DemoContext d, Guid docId, string asUser, string name, int? expirationMonths, int? retrainingDays, string[] people)
    {
        d.As(asUser);
        d.Db.ChangeTracker.Clear();
        var t = Training(d);
        var snap = await d.Services.GetRequiredService<RecordService>().LoadRequiredAsync(docId);
        var id = await t.CreateCourseForDocumentAsync(snap, name, "online", 14, expirationMonths, people.Select(d.User).ToList());
        if (retrainingDays != null) await t.ElevatedAsync(() => d.Services.GetRequiredService<RecordService>().UpdateAsync(id, new() { ["retraining_days"] = retrainingDays }));
        return id;
    }

    private static async Task<Guid> CourseAsync(DemoContext d, string asUser, string name, Guid docId, int? expirationMonths, int? retrainingDays, string[] groups)
    {
        d.As(asUser);
        d.Db.ChangeTracker.Clear();
        var res = await Training(d).ElevatedAsync(() => d.Services.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = TrainingKeys.Course, SkipAccessCheck = true,
            Values = new()
            {
                ["course_name"] = name, ["overview"] = "Read the quality policy and acknowledge that you understand how it applies to your work.",
                ["course_type"] = "online", ["evaluation"] = "acknowledgment", ["owner"] = d.User(asUser), ["source_document"] = docId,
                ["automate_next_revision"] = true, ["due_days"] = 14, ["expiration_months"] = expirationMonths, ["retraining_days"] = retrainingDays, ["enabled"] = true,
            },
            RowValues = new() { ["applicability"] = [new() { ["groups"] = groups.ToList() }] },
        }));
        if (!res.Ok) throw new InvalidOperationException("Demo course failed: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        return res.Record!.Id;
    }

    private static async Task CompleteAllAsync(DemoContext d, Guid courseId)
    {
        d.Db.ChangeTracker.Clear();
        var t = Training(d);
        foreach (var r in (await t.RowsAsync(t.AssignmentsOf(courseId))).Where(r => r.PhaseType == PhaseType.Normal))
            await d.RouteAsync(r.Id, TrainingKeys.Completed, asUser: d.Directory.Get(r.EmployeeId!.Value)!.Username);
    }

    private static async Task CompleteAsync(DemoContext d, Guid courseId, params string[] users)
    {
        d.Db.ChangeTracker.Clear();
        var t = Training(d);
        var rows = await t.RowsAsync(t.AssignmentsOf(courseId));
        foreach (var u in users)
            if (rows.FirstOrDefault(r => r.EmployeeId == d.User(u) && r.PhaseType == PhaseType.Normal) is { } row)
                await d.RouteAsync(row.Id, TrainingKeys.Completed, asUser: u);
    }

    /// <summary>Demo-only: move a pending assignment's due date into the past (not audited, like DemoContext.SetDueAsync).</summary>
    private static async Task OverdueAsync(DemoContext d, Guid courseId, string user, int days)
    {
        d.Db.ChangeTracker.Clear();
        var t = Training(d);
        var row = (await t.RowsAsync(t.AssignmentsOf(courseId))).FirstOrDefault(r => r.EmployeeId == d.User(user) && r.PhaseType == PhaseType.Normal);
        if (row == null) return;
        var due = DemoContext.Days(days);
        await d.SetDueAsync(row.Id, due);
        await d.Db.RecordValues.Where(v => v.RecordId == row.Id && v.FieldKey == "due_date" && v.RowId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Date, due.ToDateTime(TimeOnly.MinValue)));
    }

    /// <summary>Demo-only: spread effective dates over the past so review schedules and history look lived-in.</summary>
    private static async Task BackdateAsync(DemoContext d, Guid id, int effectiveDaysAgo, int? archivedDaysAgo = null)
    {
        var eff = DemoContext.Days(-effectiveDaysAgo);
        await d.Db.Records.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.EffectiveDate, eff));
        if (archivedDaysAgo is { } a)
            await d.Db.Records.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ArchivedAt, DateTime.UtcNow.AddDays(-a)));
    }

    private static Task ActAsync(DemoContext root, Guid id, string action, string asUser) =>
        DemoActions.RunAsync<DocumentBehavior>(root, id, action, asUser);

    // ------------------------------------------------------------------ content

    private static S[] Gowning(string rev, bool b) =>
    [
        new("1. Purpose", "Protect product made in the ISO 7 cleanroom (Building 2, Kalamazoo) from particulate and microbial contamination by defining how personnel gown and enter."),
        new("2. Scope", "Everyone entering the cleanroom: operators, engineers, QC, maintenance and visitors. Escorted visitors follow the same gowning sequence."),
        new("3. Responsibilities", "- Production manager: owns this procedure and the gowning qualification records.\n- Every entrant: follows the sequence below and reports any breach.\n- QC: monitors gowning by contact plates during environmental monitoring."),
        new("4. Gowning sequence", "- Remove jewellery, watches and cosmetics; store personal items in the locker room.\n- Step over the bench into the grey zone; put on shoe covers one foot at a time.\n- Wash hands for 20 seconds; dry with a lint-free towel.\n- Put on hood, then coverall, then boots, touching only the inside surfaces.\n- Put on sterile gloves; pull cuffs over the coverall sleeves.\n- Check yourself in the mirror; ask a colleague to check the back.\n- Enter through the airlock; wait for the green interlock light." +
            (b ? "\n- Change gloves at least every 2 hours and after touching any non-sanitized surface.\n- Replace the airlock sticky mat at the start of every shift (log on FRM-00001 area log)." : "")),
        new("5. Breaches", "If a garment tears or is contaminated, leave the cleanroom by the exit airlock and re-gown completely. Record the breach in Vellum Quality Events."),
        new("6. Records", "Gowning qualification records and contact plate results are kept for 5 years." + (b ? " Revision B adds the glove change frequency and daily sticky-mat replacement (CR from EM trend review Q2)." : "")),
    ];

    private static S[] Dhr(string rev) =>
    [
        new("1. Purpose", "Ensure each lot of finished devices is manufactured according to the device master record before release (21 CFR 820.184)."),
        new("2. Scope", "VX-200 infusion pumps (Kalamazoo) and ST-40 sterile tubing sets (Galway)."),
        new("3. Procedure", "- Confirm the DHR contains dates of manufacture, quantities made and released, and acceptance records.\n- Verify all inspections and tests passed and are signed by trained personnel.\n- Check nonconformances are dispositioned and linked.\n- Verify the primary identification label and UDI for each unit or lot." +
            (rev == "B" ? "\n- Attach the electronic label reconciliation printout; a second person verifies issued = used + returned + destroyed." : "") +
            "\n- Sign the DHR review in the batch record; QA release follows."),
        new("4. Records", "DHRs are retained for the design life of the device plus 2 years, and at least 2 years from release."),
    ];

    private static S[] Complaints() =>
    [
        new("1. Purpose", "Receive, document, evaluate, investigate and close complaints, and decide reportability to FDA (eMDR) and EU competent authorities (vigilance) on time."),
        new("2. Intake", "- Log every complaint in Vellum within 1 business day of receipt, from any channel (phone, email, field service, distributor).\n- Record device, lot / serial, event description, patient involvement and reporter details."),
        new("3. Reportability", "- Regulatory Affairs decides reportability. Deaths and serious injuries: report within 30 calendar days (5 days for events needing remedial action).\n- EU: serious incidents within 15 days; serious public health threats within 2 days."),
        new("4. Investigation and closure", "- Investigate unless a documented rationale shows it isn't needed.\n- Link CAPAs where the root cause is systemic.\n- Close with a response to the complainant."),
    ];

    private static S[] Calibration() =>
    [
        new("1. Purpose", "Keep monitoring and measuring equipment calibrated, traceable to national standards and fit for use (ISO 13485 clause 7.6)."),
        new("2. Calibration intervals", "- Intervals are set per asset in Vellum Assets, based on manufacturer advice, use and history.\n- Torque drivers on assembly stations: 30 days. Balances: 6 months. Pressure gauges: 12 months."),
        new("3. Out of tolerance", "- Tag the instrument 'Do not use' and quarantine it.\n- Evaluate product measured since the last good calibration; open a nonconformance when product may be affected.\n- Record as-found and as-left readings on FRM-00001."),
        new("4. Records", "Certificates and FRM-00001 records are kept for the life of the asset plus 2 years."),
    ];

    private static S[] Suppliers() =>
    [
        new("1. Purpose", "Select, approve and monitor suppliers based on their ability to meet requirements and the risk of what they supply."),
        new("2. Classification", "- Critical: affects device safety or performance (resins, sterilization, PCBAs).\n- Major: affects quality but is verified on receipt.\n- Minor: indirect materials and services."),
        new("3. Approval", "- Critical suppliers: on-site or remote audit, quality agreement and first-article inspection.\n- Major: questionnaire and certification review."),
        new("4. Monitoring", "Quarterly scorecards (quality, delivery, responsiveness). Two consecutive red scores trigger a SCAR and re-evaluation."),
    ];

    private static S[] Policy() =>
    [
        new("Our commitment", "Acme Medical Devices designs and makes infusion therapy products that clinicians and patients rely on. We commit to safe and effective devices, to meeting applicable regulatory requirements, and to maintaining the effectiveness of our quality management system."),
        new("How we live it", "- Patient safety comes first in every decision.\n- We get it right the first time and fix root causes, not symptoms.\n- Everyone can stop work when quality or safety is at risk.\n- We measure our quality objectives and review them at every management review."),
        new("Responsibility", "Every employee is responsible for quality in their own work. Managers make sure their teams understand this policy and have the training and resources they need."),
    ];

    private static S[] Torque(string rev, string driver) =>
    [
        new("1. Equipment", $"- Calibrated torque driver {driver}, set to 2.6 N-m.\n- Torque analyzer TA-3 (daily check).\n- VX-200 housing fixture F-0213."),
        new("2. Daily check", "- At shift start, run the driver on the torque analyzer 5 times. Readings must be 2.4 to 2.8 N-m.\n- Record readings on the station log. If any reading is out of range, stop and call maintenance."),
        new("3. Verification", "- Sample 5 housings per hour. Check each of the 6 screws with the audit wrench.\n- Any screw below 2.4 N-m: quarantine the hour's production and notify the shift lead."),
    ];

    private static S[] Bioburden() =>
    [
        new("1. Principle", "Microorganisms are extracted from the product by shaking in peptone-saline, collected by membrane filtration and counted after incubation."),
        new("2. Sample", "10 tubing sets per lot, taken before EO sterilization. Apply the validated recovery factor of 1.43."),
        new("3. Method", "- Extract each sample in 500 mL 0.1% peptone saline with 0.1% polysorbate 80; shake 15 minutes at 200 rpm.\n- Filter through 0.45 um membranes; plate on TSA (30-35 deg C, 3 days) and SDA (20-25 deg C, 5 days).\n- Count colonies; multiply by the recovery factor."),
        new("4. Acceptance", "Alert level 100 CFU per device; action level 200 CFU per device. Action-level results open a lab investigation."),
    ];

    private static S[] Audits() =>
    [
        new("1. Program", "Every QMS process is audited at least every 12 months; higher-risk processes (complaints, CAPA, sterilization) every 6 months."),
        new("2. Auditors", "Auditors are trained to ISO 19011 and never audit their own work."),
        new("3. Findings", "Findings are raised in Vellum Audits; major findings require a CAPA within 10 business days."),
    ];

    private static S[] HousingSpec() =>
    [
        new("1. Material", "PC/ABS blend, UL 94 V-0 at 1.5 mm, color Acme Grey (RAL 7035 equivalent)."),
        new("2. Critical dimensions", "- Screw boss inner diameter 2.45 +/- 0.03 mm.\n- Wall thickness 2.0 +/- 0.1 mm.\n- Flatness of mating face 0.15 mm max."),
        new("3. Cosmetic", "No sink marks, flow lines or flash on A-surfaces when viewed at 45 cm under 1000 lux."),
    ];

    private static S[] CalForm() =>
    [
        new("Record fields", "- Asset ID, description, location.\n- Standard used (ID, certificate, due date).\n- As-found and as-left readings with tolerance.\n- Pass / out of tolerance; impact assessment reference.\n- Technician signature and date; reviewer signature and date."),
    ];

    private static S[] DeviationLog() =>
    [
        new("Record fields", "- Date, line, product and lot.\n- Description of the deviation and immediate action.\n- QA disposition and signature."),
    ];

    private static S[] LabelSetup() =>
    [
        new("1. Setup", "- Load the 4 x 2 in polyester label stock.\n- Calibrate media from the printer menu.\n- Load the ST-40 template from the label server."),
    ];
}
