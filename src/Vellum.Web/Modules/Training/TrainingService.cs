using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;
using Vellum.Modules.Documents;

namespace Vellum.Modules.Training;

/// <summary>One applicability rule row (TRN-013). Values within a field are OR; filled fields are AND.</summary>
public sealed record ApplicabilityRule(IReadOnlyList<string> Departments, IReadOnlyList<string> Sites, IReadOnlyList<string> Groups, string? JobTitle)
{
    public bool IsEmpty => Departments.Count == 0 && Sites.Count == 0 && Groups.Count == 0 && string.IsNullOrWhiteSpace(JobTitle);

    public bool Matches(EmployeeAttributes e)
    {
        if (IsEmpty) return false;
        if (Departments.Count > 0 && !Departments.Any(d => string.Equals(d, e.Department, StringComparison.OrdinalIgnoreCase))) return false;
        if (Sites.Count > 0 && !Sites.Any(s => string.Equals(s, e.Site, StringComparison.OrdinalIgnoreCase))) return false;
        if (Groups.Count > 0 && !Groups.Any(g => e.Groups.Contains(g, StringComparer.OrdinalIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(JobTitle) && e.JobTitle?.Contains(JobTitle.Trim(), StringComparison.OrdinalIgnoreCase) != true) return false;
        return true;
    }

    /// <summary>INV-106: OR of rows; empty rows never match anyone.</summary>
    public static bool Applies(IEnumerable<ApplicabilityRule> rules, EmployeeAttributes e) => rules.Any(r => r.Matches(e));
}

public sealed record EmployeeAttributes(int UserId, string? Department, string? Site, string? JobTitle, IReadOnlyCollection<string> Groups);

public sealed record CourseInfo(Guid Id, string Number, string Name, bool Enabled, string TypeKey, int DueDays, int? ExpirationMonths, int? RetrainingDays,
    Guid? SourceDocumentId, IReadOnlyList<Guid> Prerequisites, int? OwnerId, bool AutomateNextRevision, RecordSnapshot Snapshot)
{
    public bool InstructorLed => TypeKey == "instructor_led";
}

public sealed record CourseStats(int Pending, int Completed, int Expired, int Voided, int Overdue)
{
    public int Active => Pending + Completed;
    public int Total => Pending + Completed + Expired + Voided;
    /// <summary>Completed share of current (pending + completed) assignments.</summary>
    public int Percent => Active == 0 ? 0 : (int)Math.Floor(Completed * 100.0 / Active);
    public static readonly CourseStats Empty = new(0, 0, 0, 0, 0);
}

public sealed record AssignmentRow(Guid Id, string Number, int? EmployeeId, Guid? CourseId, string CourseName, PhaseDefinition? Phase, PhaseType PhaseType,
    DateOnly? Due, DateTime CreatedAt, DateTime? CompletedAt, DateOnly? ExpiresOn, string? Reason)
{
    public bool Overdue => PhaseType == PhaseType.Normal && Due is { } d && d < BusinessCalendar.Today;
    public string Status => Phase?.Key switch
    {
        TrainingKeys.Completed => "Completed", TrainingKeys.Expired => "Expired", TrainingKeys.Voided => "Voided",
        _ => Overdue ? "Overdue" : "Pending",
    };
}

/// <summary>Training logic shared by behaviors, jobs, widgets and Document Control.</summary>
[ScopedService]
public sealed class TrainingService(
    VellumDbContext db, ConfigCache cfgCache, RecordService records, WorkflowEngine workflow, CurrentUser me, AccessService access,
    AuditService audit, UserDirectory dir)
{
    private ConfigModel Cfg => cfgCache.Current;
    private Dictionary<int, string>? _siteCodes;

    /// <summary>Set while an authorized behavior writes training records programmatically (e.g. Set up training from a document).</summary>
    public bool SystemWrite { get; private set; }

    public async Task<T> ElevatedAsync<T>(Func<Task<T>> work)
    {
        var before = SystemWrite;
        SystemWrite = true;
        try { return await work(); }
        finally { SystemWrite = before; }
    }

    public int CourseFormId => Cfg.Form(TrainingKeys.Course).Id;
    public int AssignmentFormId => Cfg.Form(TrainingKeys.Assignment).Id;
    public AppDefinition AppDef => Cfg.App(TrainingKeys.App);

    public bool IsCoordinator => me.IsSystem || access.RoleFor(AppDef.Id) >= AppRole.Manager;

    public PhaseDefinition Phase(string key) => Cfg.Form(TrainingKeys.Assignment).Workflows[0].Phases.First(p => p.Key == key);

    // ------------------------------------------------------------------ courses

    public async Task<CourseInfo?> CourseAsync(Guid id)
    {
        var snap = await records.LoadAsync(id);
        return snap == null || snap.Form.Key != TrainingKeys.Course ? null : ToCourse(snap);
    }

    public static CourseInfo ToCourse(RecordSnapshot s) => new(
        s.Record.Id, s.Record.Number, s.Text("course_name") ?? s.Record.Title, s.Bool("enabled") != false, s.KeywordKey("course_type") ?? "online",
        (int)(s.Number("due_days") ?? 14), s.Number("expiration_months") is { } m && m > 0 ? (int)m : null,
        s.Number("retraining_days") is { } rd && rd > 0 ? (int)rd : null, s.Data.Main.Link("source_document"), s.Links("prerequisites"),
        s.UserId("owner"), s.Bool("automate_next_revision") == true, s);

    /// <summary>Courses whose source is the given document revision (TRN-026).</summary>
    public Task<List<Guid>> CourseIdsForDocumentAsync(Guid docId)
    {
        var formId = CourseFormId;
        return db.RecordValues.AsNoTracking()
            .Where(v => v.FieldKey == "source_document" && v.RowId == null && v.RefGuid == docId && db.Records.Any(r => r.Id == v.RecordId && r.FormId == formId))
            .OrderBy(v => v.Id).Select(v => v.RecordId).Distinct().ToListAsync();
    }

    public async Task<List<CourseInfo>> CoursesForDocumentAsync(Guid docId)
    {
        var list = new List<CourseInfo>();
        foreach (var id in await CourseIdsForDocumentAsync(docId))
            if (await CourseAsync(id) is { } c) list.Add(c);
        return list;
    }

    public bool CanManageCourse(CourseInfo c) => IsCoordinator || (me.IsAuthenticated && c.OwnerId == me.Id);

    /// <summary>DOC-061: Set Up Training creates a course pre-filled from the document.</summary>
    public async Task<Guid> CreateCourseForDocumentAsync(RecordSnapshot doc, string name, string typeKey, int dueDays, int? expirationMonths, IReadOnlyList<int> people)
    {
        var rows = new List<Dictionary<string, object?>>();
        var dept = doc.KeywordKey("department");
        var sites = doc.KeywordKeys("sites").ToList();
        if (dept != null || sites.Count > 0)
            rows.Add(new() { ["departments"] = dept == null ? null : new[] { dept }, ["sites"] = sites.Count == 0 ? null : sites });
        var res = await ElevatedAsync(() => records.CreateAsync(new CreateRequest
        {
            FormKey = TrainingKeys.Course,
            SkipAccessCheck = true,
            Values = new()
            {
                ["course_name"] = name,
                ["overview"] = doc.Text("purpose") ?? $"Read and understand {doc.Record.Number} rev {doc.Record.Revision}: {doc.Record.Title}.",
                ["course_type"] = typeKey,
                ["evaluation"] = typeKey == "online" ? "acknowledgment" : "attendance",
                ["owner"] = doc.UserId("owner") ?? me.Id,
                ["source_document"] = doc.Record.Id,
                ["automate_next_revision"] = true,
                ["due_days"] = dueDays,
                ["expiration_months"] = expirationMonths,
                ["named_employees"] = people.Count == 0 ? null : people.ToList(),
                ["enabled"] = true,
            },
            RowValues = rows.Count == 0 ? null : new() { ["applicability"] = rows },
            CreateComment = $"Set up from {doc.Record.Number} rev {doc.Record.Revision} (DOC-061)",
        }));
        if (!res.Ok) throw new WorkflowException("Couldn't create the course: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        await CopyMaterialsAsync(doc, res.Record!.Id);
        await db.SaveChangesAsync();
        return res.Record.Id;
    }

    /// <summary>DOC-062: carry courses flagged "automate with next revision" forward to a new revision.</summary>
    public async Task<int> CarryForwardAsync(Guid fromDocId, RecordSnapshot newRevision)
    {
        var n = 0;
        foreach (var c in await CoursesForDocumentAsync(fromDocId))
        {
            if (!c.Enabled || !c.AutomateNextRevision) continue;
            var src = c.Snapshot;
            var values = new Dictionary<string, object?>();
            foreach (var f in src.Form.Fields.Where(f => FieldCodec.StoresValues(f.Type) && src.Form.Sections.First(s => s.Key == f.SectionKey).IsSubform == false))
                if (src.Data.Main.Has(f.Key)) values[f.Key] = src.Data.Main[f.Key];
            values["source_document"] = newRevision.Record.Id;
            values["source_ref"] = null;
            values["enabled"] = true;
            var rows = src.Rows("applicability").Select(r => r.Values.Keys.ToDictionary(k => k, k => (object?)r.Values[k])).ToList();
            var res = await ElevatedAsync(() => records.CreateAsync(new CreateRequest
            {
                FormKey = TrainingKeys.Course, SkipAccessCheck = true, Values = values,
                RowValues = rows.Count == 0 ? null : new() { ["applicability"] = rows },
                CreateComment = $"Automated with next revision: copied from {c.Number} for {newRevision.Record.Number} rev {newRevision.Record.Revision} (DOC-062)",
            }));
            if (!res.Ok) throw new WorkflowException($"Couldn't carry course {c.Number} forward: " + string.Join("; ", res.Errors.Select(e => e.Message)));
            await records.LinkAsync(c.Id, res.Record!.Id, LinkKind.Revision);
            n++;
        }
        return n;
    }

    /// <summary>Copy the document's controlled file(s) to the course materials (content-addressed: same SHA-256, no new blob).</summary>
    public async Task CopyMaterialsAsync(RecordSnapshot doc, Guid courseId)
    {
        var existing = await db.Attachments.Where(a => a.RecordId == courseId && a.FieldKey == "materials" && !a.Removed).ToListAsync();
        var files = doc.AttachmentsOf("primary_file").ToList();
        if (files.Count == 0) return;
        foreach (var old in existing.Where(e => files.All(f => f.Sha256 != e.Sha256)))
        {
            old.Removed = true;
            old.RemovedAt = DateTime.UtcNow;
            old.RemovedById = me.Id;
            audit.Record(courseId, "Attachment removed", $"{old.FileName} replaced by the current controlled file of {doc.Record.Number} rev {doc.Record.Revision}", fieldKey: "materials", fieldLabel: "Course materials");
        }
        foreach (var f in files.Where(f => existing.All(e => e.Sha256 != f.Sha256)))
        {
            db.Attachments.Add(new Attachment
            {
                RecordId = courseId, FieldKey = "materials", FileName = f.FileName, ContentType = f.ContentType, Sha256 = f.Sha256, Size = f.Size,
                UploadedById = f.UploadedById, UploadedAt = DateTime.UtcNow,
            });
            audit.Record(courseId, "Attachment added", $"{f.FileName} copied from {doc.Record.Number} rev {doc.Record.Revision} (SHA-256 {f.Sha256[..12]}…)",
                fieldKey: "materials", fieldLabel: "Course materials");
        }
    }

    /// <summary>DOC-066 / TRN-027: disable the document's courses and void their pending assignments.</summary>
    public async Task<int> DisableCoursesForDocumentAsync(Guid docId, string reason)
    {
        var n = 0;
        foreach (var c in await CoursesForDocumentAsync(docId))
        {
            if (c.Enabled)
            {
                var res = await ElevatedAsync(() => records.UpdateAsync(c.Id, new() { ["enabled"] = false }, reason));
                if (!res.Ok) throw new WorkflowException($"Couldn't disable course {c.Number}: " + string.Join("; ", res.Errors.Select(e => e.Message)));
            }
            n += await VoidPendingAsync(c.Id, reason);
        }
        return n;
    }

    // ------------------------------------------------------------------ applicability

    public static List<ApplicabilityRule> Rules(RecordSnapshot course, ConfigModel cfg) =>
        course.Rows("applicability").Select(r => new ApplicabilityRule(
            r.Values.Refs("departments").Select(id => cfg.Keyword(id)?.Label).OfType<string>().ToList(),
            r.Values.Refs("sites").Select(id => cfg.Keyword(id)?.Key).OfType<string>().ToList(),
            r.Values.Refs("groups").Select(id => cfg.Keyword(id)?.Key).OfType<string>().ToList(),
            r.Values.Text("job_title"))).ToList();

    public async Task<EmployeeAttributes> AttributesAsync(UserInfo u)
    {
        _siteCodes ??= await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Code);
        var site = u.LocationId is { } lid && _siteCodes.TryGetValue(lid, out var code) ? code : null;
        var groups = dir.GroupsOf(u.Id).Select(g => dir.Group(g)?.Key).OfType<string>().ToList();
        return new EmployeeAttributes(u.Id, u.Department, site, u.JobTitle, groups);
    }

    /// <summary>Active employees selected by the course's applicability rules plus its named employees.</summary>
    public async Task<List<UserInfo>> ApplicableAsync(CourseInfo course)
    {
        var rules = Rules(course.Snapshot, Cfg);
        var result = new List<UserInfo>();
        foreach (var u in dir.All.Where(u => u.IsActive))
            if (ApplicabilityRule.Applies(rules, await AttributesAsync(u))) result.Add(u);
        foreach (var id in course.Snapshot.UserIds("named_employees"))
            if (dir.Get(id) is { IsActive: true } u && result.All(x => x.Id != id)) result.Add(u);
        return result.OrderBy(u => u.DisplayName).ToList();
    }

    // ------------------------------------------------------------------ assignments

    private IQueryable<Record> Assignments() => db.Records.AsNoTracking().Where(r => r.FormId == AssignmentFormId);

    public IQueryable<Record> AssignmentsOf(Guid courseId) =>
        Assignments().Where(r => db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "course" && v.RefGuid == courseId));

    public IQueryable<Record> AssignmentsFor(int employeeId) =>
        Assignments().Where(r => db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "employee" && v.RefId == employeeId));

    /// <summary>INV-109: is there a pending assignment of this course for this employee (other than <paramref name="except"/>)?</summary>
    public Task<bool> HasPendingAsync(int employeeId, Guid courseId, Guid? except = null) =>
        AssignmentsOf(courseId).AnyAsync(r => r.PhaseType == PhaseType.Normal && r.Id != except &&
            db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "employee" && v.RefId == employeeId));

    /// <summary>
    /// Create assignments in bulk (TRN-041). Skips employees who already have a pending assignment (INV-109), who are inactive,
    /// or (unless <paramref name="forceRetraining"/>) who have already completed the course.
    /// </summary>
    public async Task<(List<Record> Created, List<string> Skipped)> AssignAsync(Guid courseId, IEnumerable<int> userIds, DateOnly? due, string reason,
        bool forceRetraining = false)
    {
        var course = await CourseAsync(courseId) ?? throw new WorkflowException("That course doesn't exist.");
        if (!course.Enabled) throw new WorkflowException($"{course.Number} is disabled, so it can't get new assignments (TRN-027).");
        var dueDate = due ?? BusinessCalendar.Today.AddDays(course.DueDays);
        var rows = await RowsAsync(AssignmentsOf(courseId));
        var created = new List<Record>();
        var skipped = new List<string>();
        foreach (var uid in userIds.Distinct())
        {
            var u = dir.Get(uid);
            if (u is not { IsActive: true }) { skipped.Add($"{dir.Name(uid)}: not an active employee"); continue; }
            var mine = rows.Where(r => r.EmployeeId == uid).ToList();
            if (mine.Any(r => r.PhaseType == PhaseType.Normal)) { skipped.Add($"{u.DisplayName}: already has a pending assignment"); continue; }
            if (!forceRetraining && mine.Any(r => r.Phase?.Key == TrainingKeys.Completed)) { skipped.Add($"{u.DisplayName}: already completed"); continue; }
            var res = await ElevatedAsync(() => records.CreateAsync(new CreateRequest
            {
                FormKey = TrainingKeys.Assignment, SkipAccessCheck = true, CreateComment = reason,
                Values = new() { ["employee"] = uid, ["course"] = courseId, ["due_date"] = dueDate, ["reason"] = reason, ["source_doc"] = course.SourceDocumentId },
            }));
            if (res.Ok) created.Add(res.Record!);
            else skipped.Add($"{u.DisplayName}: {string.Join("; ", res.Errors.Select(e => e.Message))}");
        }
        return (created, skipped);
    }

    /// <summary>DOC-063: when a revision enters Awaiting Release, assign its courses to everyone they apply to.</summary>
    public async Task<int> AssignForDocumentAsync(RecordSnapshot doc, string reason)
    {
        if (doc.Bool("training_required") != true) return 0;
        var n = 0;
        foreach (var c in await CoursesForDocumentAsync(doc.Record.Id))
        {
            if (!c.Enabled) continue;
            await CopyMaterialsAsync(doc, c.Id);
            var people = await ApplicableAsync(c);
            var done = await RowsAsync(AssignmentsOf(c.Id));
            var targets = people.Where(p => done.All(d => d.EmployeeId != p.Id || d.PhaseType == PhaseType.Voided)).Select(p => p.Id);
            var (created, _) = await AssignAsync(c.Id, targets, null, reason);
            n += created.Count;
        }
        await db.SaveChangesAsync();
        return n;
    }

    public async Task<int> VoidPendingAsync(Guid courseId, string reason)
    {
        var ids = await AssignmentsOf(courseId).Where(r => r.PhaseType == PhaseType.Normal).Select(r => r.Id).ToListAsync();
        foreach (var id in ids)
        {
            var res = await workflow.SystemRouteAsync(id, TrainingKeys.Voided, reason);
            if (!res.Ok) throw new WorkflowException("Couldn't void an assignment: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        }
        return ids.Count;
    }

    public async Task<List<AssignmentRow>> RowsAsync(IQueryable<Record> query)
    {
        var recs = await query.OrderBy(r => r.CreatedAt).ToListAsync();
        var ids = recs.Select(r => r.Id).ToList();
        if (ids.Count == 0) return [];
        var values = await db.RecordValues.AsNoTracking()
            .Where(v => ids.Contains(v.RecordId) && v.RowId == null && (v.FieldKey == "employee" || v.FieldKey == "course" || v.FieldKey == "course_name" || v.FieldKey == "reason" || v.FieldKey == "due_date"))
            .ToListAsync();
        var completedPhase = Phase(TrainingKeys.Completed).Id;
        var expiredPhase = Phase(TrainingKeys.Expired).Id;
        var completions = await db.PhaseEntries.AsNoTracking().Where(e => ids.Contains(e.RecordId) && e.PhaseId == completedPhase)
            .GroupBy(e => e.RecordId).Select(g => new { g.Key, At = g.Min(e => e.EnteredAt) }).ToDictionaryAsync(x => x.Key, x => x.At);
        var courseIds = values.Where(v => v.FieldKey == "course" && v.RefGuid != null).Select(v => v.RefGuid!.Value).Distinct().ToList();
        var expMonths = await db.RecordValues.AsNoTracking().Where(v => courseIds.Contains(v.RecordId) && v.RowId == null && v.FieldKey == "expiration_months")
            .ToDictionaryAsync(v => v.RecordId, v => v.Number);
        return recs.Select(r =>
        {
            var vs = values.Where(v => v.RecordId == r.Id).ToList();
            var courseId = vs.FirstOrDefault(v => v.FieldKey == "course")?.RefGuid;
            DateTime? done = completions.TryGetValue(r.Id, out var at) ? at : null;
            var months = courseId is { } cid && expMonths.TryGetValue(cid, out var m) && m > 0 ? (int?)m : null;
            return new AssignmentRow(r.Id, r.Number, vs.FirstOrDefault(v => v.FieldKey == "employee")?.RefId, courseId,
                vs.FirstOrDefault(v => v.FieldKey == "course_name")?.Text ?? r.Title, Cfg.Phase(r.PhaseId), r.PhaseType,
                r.DueDate ?? (vs.FirstOrDefault(v => v.FieldKey == "due_date")?.Date is { } dd ? DateOnly.FromDateTime(dd) : null), r.CreatedAt, done,
                done is { } d && months is { } mm && r.PhaseId != null && (r.PhaseId == completedPhase || r.PhaseId == expiredPhase) ? DateOnly.FromDateTime(d).AddMonths(mm) : null,
                vs.FirstOrDefault(v => v.FieldKey == "reason")?.Text);
        }).ToList();
    }

    public async Task<CourseStats> StatsAsync(IReadOnlyCollection<Guid> courseIds)
    {
        if (courseIds.Count == 0) return CourseStats.Empty;
        var rows = new List<AssignmentRow>();
        foreach (var id in courseIds) rows.AddRange(await RowsAsync(AssignmentsOf(id)));
        return new CourseStats(
            rows.Count(r => r.PhaseType == PhaseType.Normal),
            rows.Count(r => r.Phase?.Key == TrainingKeys.Completed),
            rows.Count(r => r.Phase?.Key == TrainingKeys.Expired),
            rows.Count(r => r.PhaseType == PhaseType.Voided),
            rows.Count(r => r.Overdue));
    }

    public async Task<CourseStats> StatsForDocumentAsync(Guid docId) => await StatsAsync(await CourseIdsForDocumentAsync(docId));

    /// <summary>INV-107: prerequisite courses the employee hasn't completed (or whose completion has expired).</summary>
    public async Task<List<string>> MissingPrerequisitesAsync(CourseInfo course, int employeeId)
    {
        var missing = new List<string>();
        foreach (var pid in course.Prerequisites)
        {
            var pre = await CourseAsync(pid);
            if (pre == null) continue;
            var rows = await RowsAsync(AssignmentsOf(pid).Where(r => db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "employee" && v.RefId == employeeId)));
            var valid = rows.Any(r => r.Phase?.Key == TrainingKeys.Completed && (r.ExpiresOn == null || r.ExpiresOn >= BusinessCalendar.Today));
            if (!valid) missing.Add($"{pre.Number} {pre.Name}");
        }
        return missing;
    }

    /// <summary>Home widget: the user's own pending assignments and recent completions.</summary>
    public async Task<(List<AssignmentRow> Pending, int CompletedThisYear, int Expiring)> MineAsync(int userId)
    {
        var rows = await RowsAsync(AssignmentsFor(userId));
        var yearAgo = DateTime.UtcNow.AddYears(-1);
        var soon = BusinessCalendar.Today.AddDays(60);
        return (rows.Where(r => r.PhaseType == PhaseType.Normal).OrderBy(r => r.Due).ToList(),
            rows.Count(r => r.Phase?.Key == TrainingKeys.Completed && r.CompletedAt >= yearAgo),
            rows.Count(r => r.Phase?.Key == TrainingKeys.Completed && r.ExpiresOn is { } e && e <= soon));
    }
}
