using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;
using Vellum.Modules.Training;

namespace Vellum.Modules.Documents;

public sealed record TypeProfile(Guid Id, string Number, string TypeKey, string Name, string Prefix, RevisionFormat Format, int ReviewMonths,
    bool TrainingConsidered, bool Enabled);

/// <summary>An open item that counts toward the INV-103 exclusion rules.</summary>
public sealed record PendingItem(Guid Id, string Number, string? Revision, string FormKey, string FormName, string PhaseName)
{
    public string Describe => FormKey == DocKeys.Document ? $"revision {Revision} ({PhaseName})" : $"{Number}, a pending {FormName.ToLowerInvariant()} ({PhaseName})";
}

public sealed record RevisionRow(Record Record, PhaseDefinition? Phase, string? Reason, IReadOnlyList<Signature> Approvals, Guid? ChangeRequestId,
    string? ChangeRequestNumber, IReadOnlyList<Record> Reviews, IReadOnlyList<Record> ObsoleteRequests, bool Readable);

public sealed record GateStatus(bool Gated, int RequiredPercent, CourseStats Stats, bool HasCourse, Signature? Override)
{
    public bool Satisfied => !Gated || Override != null || (HasCourse && (Stats.Active == 0 || Stats.Percent >= RequiredPercent));
    public string Message => !HasCourse
        ? "Training-gated release: set up the training course first (Training › Set up training)."
        : $"Training-gated release: {Stats.Completed} of {Stats.Active} trainees have completed ({Stats.Percent}%); {RequiredPercent}% is required (TRN-071). Document Control can override with a justification.";
}

public sealed class ReviewInfo
{
    public required Record Doc { get; init; }
    public string? TypeKey { get; init; }
    public int? OwnerId { get; init; }
    public int Months { get; init; }
    public DateOnly? LastReviewed { get; init; }
    public Record? LastReview { get; init; }
    public Record? OpenReview { get; init; }
    public DateOnly? NextReview { get; init; }
    public int? DaysLeft => NextReview is { } d ? d.DayNumber - BusinessCalendar.Today.DayNumber : null;
    /// <summary>overdue | due30 | due60 | due90 | later | none</summary>
    public string Window => DaysLeft switch { null => "none", < 0 => "overdue", <= 30 => "due30", <= 60 => "due60", <= 90 => "due90", _ => "later" };
}

/// <summary>
/// Shared Document Control logic: type profiles (DOC-001), the INV-103 exclusion rules, revision creation (DOC-093/106),
/// release and supersession in one transaction (DOC-052/053, INV-100), obsolescence (DOC-103), draft visibility (DOC-080)
/// and the periodic review schedule (DOC-071/074).
/// </summary>
[ScopedService]
public sealed class DocumentControlService(
    VellumDbContext db, ConfigCache cfgCache, RecordService records, WorkflowEngine workflow, CurrentUser me, AccessService access,
    AuditService audit, NotificationService notify, UserDirectory dir, TrainingService training, IServiceScopeFactory scopes)
{
    /// <summary>The six packaged type profiles (DOC-001, DOC-006).</summary>
    public static readonly (string Type, string Name, string Prefix, string Format, int Months, bool Training, string Usage)[] DefaultProfiles =
    [
        ("procedure", "Standard operating procedure", "SOP-", "alphabetic", 24, true, "How a process is carried out across departments: responsibilities, steps and records."),
        ("policy", "Policy", "POL-", "alphabetic", 36, true, "Management commitments and principles, such as the quality policy."),
        ("work_instruction", "Work instruction", "WI-", "alphabetic", 24, true, "Step-by-step instructions for one task at one workstation, with pictures where helpful."),
        ("test_method", "Test method", "TM-", "alphabetic", 24, true, "Laboratory or inspection methods: equipment, sample preparation, acceptance criteria."),
        ("specification", "Specification", "SPEC-", "numeric_1", 36, false, "Product, component and material requirements. Training isn't considered by default."),
        ("form", "Form or template", "FRM-", "alphabetic", 36, false, "Blank forms, logs and templates used to create quality records."),
    ];

    /// <summary>
    /// Installs the packaged type profiles the first time a document is created in a tenant without any profiles, as the system
    /// identity in its own scope (packaged apps have no baseline-data hook; demo data is optional). Call outside a transaction.
    /// </summary>
    public async Task EnsureDefaultProfilesAsync()
    {
        var formId = Cfg.Form(DocKeys.Profile).Id;
        if (await db.Records.AnyAsync(r => r.FormId == formId)) return;
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<CurrentUser>().UseSystem("Document Control default type profiles");
        var rs = sp.GetRequiredService<RecordService>();
        foreach (var p in DefaultProfiles)
            await rs.CreateAsync(new CreateRequest
            {
                FormKey = DocKeys.Profile,
                Values = new()
                {
                    ["doc_type"] = p.Type, ["profile_name"] = p.Name, ["number_prefix"] = p.Prefix, ["revision_format"] = p.Format,
                    ["review_months"] = p.Months, ["training_considered"] = p.Training, ["enabled"] = true, ["usage"] = p.Usage,
                },
                CreateComment = "Packaged default profile (DOC-001)",
            });
    }

    private ConfigModel Cfg => cfgCache.Current;

    /// <summary>Set while this service creates a revision, so creation rules for brand-new documents (DOC-003) don't apply.</summary>
    public bool CreatingRevision { get; private set; }

    public FormDefinition DocumentForm => Cfg.Form(DocKeys.Document);
    public AppDefinition AppDef => Cfg.App(DocKeys.App);
    public PhaseDefinition DocPhase(string key) => DocumentForm.Workflows[0].Phases.First(p => p.Key == key);

    // ------------------------------------------------------------------ permissions

    public bool IsManager => me.IsSystem || access.RoleFor(AppDef.Id) >= AppRole.Manager;
    public bool IsDocController => me.IsAuthenticated && !me.IsSystem && dir.IsMember(me.Id, DocKeys.DocControllers);
    public bool IsTrainingCoordinator => me.IsAuthenticated && (dir.IsMember(me.Id, DocKeys.TrainingCoordinators) || training.IsCoordinator);

    /// <summary>DOC-090 / DOC-073: owners, Document Control, application managers and administrators.</summary>
    public bool CanRequest(RecordSnapshot doc) => IsManager || IsDocController || doc.UserId("owner") == me.Id;
    /// <summary>DOC-101: "Who can obsolete" is Document Control (plus managers and administrators).</summary>
    public bool CanObsolete => IsManager || IsDocController;
    /// <summary>DOC-105: "Who can restore" is Document Control (plus managers and administrators).</summary>
    public bool CanRestore => IsManager || IsDocController;

    // ------------------------------------------------------------------ type profiles

    public async Task<List<TypeProfile>> ProfilesAsync()
    {
        var form = Cfg.Form(DocKeys.Profile);
        var recs = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id && !r.IsArchived).OrderBy(r => r.Sequence).ToListAsync();
        var ids = recs.Select(r => r.Id).ToList();
        var vals = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null).ToListAsync();
        return recs.Select(r =>
        {
            var bag = vals.Where(v => v.RecordId == r.Id).ToList();
            RecordValue? V(string k) => bag.FirstOrDefault(v => v.FieldKey == k);
            var format = Cfg.Keyword(V("revision_format")?.RefId)?.Key switch
            {
                "numeric_0" => RevisionFormat.NumericFrom0, "numeric_1" => RevisionFormat.NumericFrom1, _ => RevisionFormat.Alphabetic,
            };
            return new TypeProfile(r.Id, r.Number, Cfg.Keyword(V("doc_type")?.RefId)?.Key ?? "", V("profile_name")?.Text ?? r.Title,
                NormalizePrefix(V("number_prefix")?.Text), format, (int)(V("review_months")?.Number ?? 0), V("training_considered")?.RefId != 0,
                V("enabled")?.RefId == 1);
        }).ToList();
    }

    public static string NormalizePrefix(string? p) => (p ?? "").Trim().ToUpperInvariant();

    /// <summary>The enabled profile of a type; with <paramref name="enabledOnly"/> false, falls back to the latest disabled one.</summary>
    public async Task<TypeProfile?> ProfileForAsync(string? typeKey, bool enabledOnly = true)
    {
        if (typeKey == null) return null;
        var all = (await ProfilesAsync()).Where(p => p.TypeKey == typeKey).ToList();
        return all.FirstOrDefault(p => p.Enabled) ?? (enabledOnly ? null : all.LastOrDefault());
    }

    // ------------------------------------------------------------------ INV-103

    public async Task<List<PendingItem>> PendingItemsAsync(Record doc)
    {
        var family = doc.FamilyId ?? doc.Id;
        var famIds = await db.Records.AsNoTracking().Where(r => r.FamilyId == family).Select(r => r.Id).ToListAsync();
        var reqForms = DocKeys.RequestForms.Select(k => Cfg.Form(k).Id).ToList();
        var docForm = DocumentForm.Id;
        var open = await db.Records.AsNoTracking()
            .Where(r => r.PhaseType == PhaseType.Normal && !r.IsArchived &&
                        ((r.ParentId != null && famIds.Contains(r.ParentId.Value) && reqForms.Contains(r.FormId)) || (r.FamilyId == family && r.FormId == docForm)))
            .OrderBy(r => r.CreatedAt).ToListAsync();
        return open.Select(r =>
        {
            var f = Cfg.FormsById[r.FormId];
            return new PendingItem(r.Id, r.Number, r.Revision, f.Key, f.Name, Cfg.Phase(r.PhaseId)?.Name ?? "");
        }).ToList();
    }

    /// <summary>
    /// DOC-097 / INV-103: why a request of <paramref name="requestForm"/> can't be raised on <paramref name="doc"/> right now (null = allowed).
    /// <paramref name="except"/> excludes the request being checked itself.
    /// </summary>
    public async Task<string?> BlockerAsync(Record doc, string requestForm, Guid? except = null)
    {
        if (!doc.IsEffective || doc.IsArchived || Cfg.Phase(doc.PhaseId)?.Key != DocKeys.Approved)
            return "Requests can only be raised on the effective revision of an approved document (INV-103).";
        var blocking = requestForm switch
        {
            DocKeys.ChangeRequest => new[] { DocKeys.ChangeRequest, DocKeys.ObsoleteRequest, DocKeys.Document },
            DocKeys.ObsoleteRequest => new[] { DocKeys.ChangeRequest, DocKeys.PeriodicReview, DocKeys.ObsoleteRequest, DocKeys.Document },
            _ => new[] { DocKeys.ChangeRequest, DocKeys.ObsoleteRequest },
        };
        var item = (await PendingItemsAsync(doc)).FirstOrDefault(i => i.Id != except && blocking.Contains(i.FormKey));
        return item == null ? null : $"Blocked by {item.Describe}. Complete or void it first (DOC-097, INV-103).";
    }

    public static string DocRef(Record doc) => $"{doc.Number} rev {doc.Revision} · {doc.Title}";

    // ------------------------------------------------------------------ revisions (DOC-093, DOC-106)

    public async Task<Record> CreateRevisionAsync(RecordSnapshot source, int createdById, IReadOnlyList<int> assignees, DateOnly? due, string reason,
        Guid? changeRequestId, string comment, IEnumerable<int>? extraReaders = null)
    {
        var family = source.Record.FamilyId ?? source.Record.Id;
        var latest = await db.Records.AsNoTracking().Where(r => r.FamilyId == family).OrderByDescending(r => r.RevisionOrdinal).FirstAsync();
        var profile = await ProfileForAsync(source.KeywordKey("doc_type"), enabledOnly: false);
        var nextRev = NumberingService.NextRevision(profile?.Format ?? RevisionFormat.Alphabetic, latest.Revision)
                      ?? NumberingService.NextAlpha(latest.Revision ?? "A");
        var data = source.Data.Clone();
        foreach (var k in new[] { "training_required", "training_justification", "target_effective", "minor_change", "change_request", "revision_reason" })
            data.Main.Clear(k);
        var values = new Dictionary<string, object?>
        {
            ["revision_reason"] = reason, ["change_request"] = changeRequestId, ["minor_change"] = false,
        };

        CreateResult res;
        CreatingRevision = true;
        try
        {
            res = await records.CreateAsync(new CreateRequest
            {
                FormKey = DocKeys.Document, Data = data, Values = values,
                Number = source.Record.Number, Sequence = source.Record.Sequence, FamilyId = family, Revision = nextRev,
                RevisionOrdinal = latest.RevisionOrdinal + 1, CreatedAt = DateTime.UtcNow, CreatedById = createdById,
                Assignees = assignees, DueDate = due, SkipAccessCheck = true, LocationId = source.Record.LocationId, CreateComment = comment,
            });
        }
        finally { CreatingRevision = false; }
        if (!res.Ok) throw new WorkflowException("Couldn't create the new revision: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        var rev = res.Record!;

        // Originals only: renditions are re-derived for the new revision (the conversion cache avoids reconverting, PLT-804).
        foreach (var a in source.Attachments.Where(a => !a.Removed && a.RowId == null && a.RenditionOfId == null))
        {
            db.Attachments.Add(new Attachment
            {
                RecordId = rev.Id, FieldKey = a.FieldKey, FileName = a.FileName, ContentType = a.ContentType, Sha256 = a.Sha256, Size = a.Size,
                UploadedById = a.UploadedById, UploadedAt = DateTime.UtcNow,
            });
            audit.Record(rev.Id, "Attachment added", $"{a.FileName} copied from revision {source.Record.Revision} (SHA-256 {a.Sha256[..12]}…)",
                fieldKey: a.FieldKey, fieldLabel: source.Form.Fields.FirstOrDefault(f => f.Key == a.FieldKey)?.Label);
        }
        await records.LinkAsync(source.Record.Id, rev.Id, LinkKind.Revision);
        if (changeRequestId is { } cr) await records.LinkAsync(cr, rev.Id, LinkKind.Revision);
        await db.SaveChangesAsync();

        var snap = await records.LoadRequiredAsync(rev.Id);
        await training.CarryForwardAsync(source.Record.Id, snap);
        await SyncDraftReadersAsync(snap, extraReaders);
        await db.SaveChangesAsync();
        return rev;
    }

    // ------------------------------------------------------------------ release (DOC-050..055, INV-100)

    /// <summary>
    /// Make the routed revision effective and supersede the previous effective revision in the same transaction (DOC-053).
    /// The old flag is cleared and saved before the new one is set, so the filtered unique index (INV-100) never sees two.
    /// </summary>
    public async Task MakeEffectiveAsync(RoutingContext ctx)
    {
        var r = ctx.Record;
        var now = DateTime.UtcNow;
        var today = BusinessCalendar.Today;
        var previous = await db.Records.Where(x => x.FamilyId == r.FamilyId && x.Id != r.Id && x.IsEffective).ToListAsync();
        foreach (var p in previous)
        {
            p.IsEffective = false;
            p.IsArchived = true;
            p.ArchiveReason = DocKeys.Superseded;
            p.ArchivedAt = now;
            p.ModifiedAt = now;
            p.ModifiedById = me.Id;
            p.Version++;
            audit.Record(p.Id, "Superseded", $"Revision {p.Revision} superseded by revision {r.Revision} and moved to the archive (DOC-053)",
                oldValue: "Effective", newValue: "Archived (superseded)", recordVersion: p.Version);
            db.Comments.Add(new Comment { RecordId = p.Id, UserId = me.IsSystem ? null : me.Id, IsSystem = true, Kind = "system", At = now,
                Text = $"Superseded by revision {r.Revision}, effective {today:dd MMM yyyy}." });
        }
        await db.SaveChangesAsync();

        r.IsEffective = true;
        r.EffectiveDate = today;
        audit.Record(r.Id, "Effective", $"Revision {r.Revision} effective {today:dd MMM yyyy}" +
            (previous.Count > 0 ? $", superseding revision {string.Join(", ", previous.Select(p => p.Revision))}" : ""),
            newValue: "Effective", phaseName: ctx.To.Name);
        await db.SaveChangesAsync();

        var snap = ctx.Snapshot;
        await SyncDraftReadersAsync(snap);
        foreach (var p in previous) await ArchiveActivitiesAsync(p.Id, DocKeys.Superseded, $"Revision {p.Revision} was superseded by revision {r.Revision}");

        NotifyDistribution(snap, $"{r.Number} rev {r.Revision} is now effective",
            $"{r.Title} became effective on {today:dd MMM yyyy}." + (previous.Count > 0 ? $" It supersedes revision {string.Join(", ", previous.Select(p => p.Revision))}; stop using printed copies of the old revision." : ""));
        await db.SaveChangesAsync();
    }

    /// <summary>DOC-103: the document entered Obsolete. Archive it with its activities, disable its training and notify distribution.</summary>
    public async Task CompleteObsolescenceAsync(RoutingContext ctx, string? reason)
    {
        var r = ctx.Record;
        var now = DateTime.UtcNow;
        r.IsEffective = false;
        r.IsArchived = true;
        r.ArchiveReason = DocKeys.ObsoleteReason;
        r.ArchivedAt = now;
        audit.Record(r.Id, "Obsoleted", $"Revision {r.Revision} is obsolete and moved to the archive (DOC-103)", reason: reason,
            oldValue: "Effective", newValue: "Archived (obsolete)");
        await db.SaveChangesAsync();
        await ArchiveActivitiesAsync(r.Id, DocKeys.ObsoleteReason, $"{r.Number} was made obsolete");
        await training.DisableCoursesForDocumentAsync(r.Id, $"{r.Number} rev {r.Revision} is obsolete (DOC-066)");
        NotifyDistribution(ctx.Snapshot, $"{r.Number} is obsolete", $"{r.Title} (rev {r.Revision}) has been withdrawn from use. Remove any printed copies." +
            (reason != null ? $" Reason: {reason}" : ""));
        await db.SaveChangesAsync();
    }

    /// <summary>DOC-053 / DOC-103: activities travel to the archive with their document. Open ones are voided first.</summary>
    public async Task ArchiveActivitiesAsync(Guid parentId, string reason, string why)
    {
        var openIds = await db.Records.AsNoTracking().Where(c => c.ParentId == parentId && !c.IsArchived && c.PhaseType == PhaseType.Normal && c.WorkflowId != null)
            .Select(c => c.Id).ToListAsync();
        foreach (var id in openIds)
        {
            var res = await workflow.SystemRouteAsync(id, "voided", why);
            if (!res.Ok) throw new WorkflowException("Couldn't void an open activity: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        }
        var now = DateTime.UtcNow;
        foreach (var c in await db.Records.Where(c => c.ParentId == parentId && !c.IsArchived).ToListAsync())
        {
            c.IsArchived = true;
            c.ArchiveReason = reason;
            c.ArchivedAt = now;
            audit.Record(c.Id, "Archived", $"Moved to the archive with its document: {why}");
        }
        await db.SaveChangesAsync();
    }

    public void NotifyDistribution(RecordSnapshot snap, string title, string body)
    {
        var ids = snap.UserIds("distribution").ToHashSet();
        foreach (var g in snap.KeywordKeys("distribution_groups"))
            foreach (var u in dir.ActiveMembers(g)) ids.Add(u.Id);
        if (snap.UserId("owner") is { } o) ids.Add(o);
        foreach (var id in ids.Where(i => i != me.Id)) notify.Notify(id, NotificationKind.Info, title, body, snap.Record.Id);
    }

    // ------------------------------------------------------------------ draft visibility (DOC-080)

    /// <summary>
    /// Drafts and pending revisions get a record readers list (SEC-507): owner, reviewers, Document Control, Quality Approvers and
    /// Training Coordinators, plus creator, assignees and managers (always). The list is removed when the revision becomes effective.
    /// </summary>
    public async Task SyncDraftReadersAsync(RecordSnapshot snap, IEnumerable<int>? extraUsers = null)
    {
        var r = snap.Record;
        var rows = await db.RecordAccess.Where(a => a.RecordId == r.Id && a.Level == RecordAccessLevel.Reader).ToListAsync();
        if (r.IsEffective || r.IsArchived)
        {
            if (rows.Count == 0) return;
            db.RecordAccess.RemoveRange(rows);
            audit.Record(r.Id, "Visibility changed", "Readers list removed: the revision is visible to every Document Control reader (DOC-080)");
            return;
        }
        var users = new HashSet<int>(snap.UserIds("reviewers"));
        if (snap.UserId("owner") is { } owner) users.Add(owner);
        foreach (var u in extraUsers ?? []) users.Add(u);
        var groups = new[] { DocKeys.DocControllers, DocKeys.QaApprovers, DocKeys.TrainingCoordinators }.Select(dir.Group).OfType<GroupInfo>().ToList();
        var added = new List<string>();
        foreach (var u in users.Where(u => rows.All(x => x.PrincipalKind != PrincipalKind.User || x.PrincipalId != u)))
        {
            db.RecordAccess.Add(new RecordAccess { RecordId = r.Id, PrincipalKind = PrincipalKind.User, PrincipalId = u, Level = RecordAccessLevel.Reader });
            added.Add(dir.Name(u));
        }
        foreach (var g in groups.Where(g => rows.All(x => x.PrincipalKind != PrincipalKind.Group || x.PrincipalId != g.Id)))
        {
            db.RecordAccess.Add(new RecordAccess { RecordId = r.Id, PrincipalKind = PrincipalKind.Group, PrincipalId = g.Id, Level = RecordAccessLevel.Reader });
            added.Add(g.Name);
        }
        if (added.Count > 0)
            audit.Record(r.Id, "Visibility changed", $"Draft readable by {string.Join(", ", added)} (plus author, assignees and managers) until it becomes effective (DOC-080)");
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ awaiting release

    public Task<PhaseEntry?> CurrentEntryAsync(Guid id) =>
        db.PhaseEntries.AsNoTracking().Where(e => e.RecordId == id && e.ExitedAt == null).OrderByDescending(e => e.Id).FirstOrDefaultAsync();

    /// <summary>DOC-050/051: later of (entry into Awaiting Release + delay) and the target effective date.</summary>
    public async Task<DateOnly?> PlannedEffectiveAsync(RecordSnapshot snap)
    {
        if (snap.PhaseKey != DocKeys.AwaitingRelease) return null;
        var entry = await CurrentEntryAsync(snap.Record.Id);
        var planned = DateOnly.FromDateTime(entry?.EnteredAt ?? DateTime.UtcNow).AddDays((int)(snap.Number("effective_delay") ?? 0));
        if (snap.Date("target_effective") is { } t && t > planned) planned = t;
        return planned;
    }

    /// <summary>A valid signature with this meaning captured in the current Awaiting Release entry.</summary>
    public async Task<Signature?> PhaseSignatureAsync(Guid id, string meaning)
    {
        var entry = await CurrentEntryAsync(id);
        if (entry == null) return null;
        return await db.Signatures.AsNoTracking()
            .Where(s => s.RecordId == id && !s.Superseded && s.Meaning == meaning && s.PhaseKey == DocKeys.AwaitingRelease && s.SignedAt >= entry.EnteredAt)
            .OrderByDescending(s => s.SignedAt).FirstOrDefaultAsync();
    }

    /// <summary>TRN-071: training-gated release status.</summary>
    public async Task<GateStatus> GateAsync(RecordSnapshot snap)
    {
        var gated = snap.Bool("training_required") == true && snap.Bool("training_gated") == true;
        var pct = (int)(snap.Number("training_gate_percent") ?? 100);
        if (!gated) return new GateStatus(false, pct, CourseStats.Empty, false, null);
        var courses = await training.CourseIdsForDocumentAsync(snap.Record.Id);
        var stats = await training.StatsAsync(courses);
        return new GateStatus(true, pct, stats, courses.Count > 0, await PhaseSignatureAsync(snap.Record.Id, DocKeys.SignGateOverride));
    }

    // ------------------------------------------------------------------ periodic review schedule (DOC-071, DOC-074)

    public async Task<List<ReviewInfo>> ReviewScheduleAsync(IReadOnlyCollection<Record> docs)
    {
        if (docs.Count == 0) return [];
        var ids = docs.Select(d => d.Id).ToList();
        var vals = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && (v.FieldKey == "doc_type" || v.FieldKey == "owner")).ToListAsync();
        var prForm = Cfg.Form(DocKeys.PeriodicReview);
        var completedPhase = prForm.Workflows[0].Phases.First(p => p.Key == "completed").Id;
        var reviews = await db.Records.AsNoTracking().Where(r => r.FormId == prForm.Id && r.ParentId != null && ids.Contains(r.ParentId.Value)).ToListAsync();
        var reviewIds = reviews.Select(r => r.Id).ToList();
        var nextDates = await db.RecordValues.AsNoTracking().Where(v => reviewIds.Contains(v.RecordId) && v.RowId == null && v.FieldKey == "next_review_date").ToListAsync();
        var completions = await db.PhaseEntries.AsNoTracking().Where(e => reviewIds.Contains(e.RecordId) && e.PhaseId == completedPhase).ToListAsync();
        var profiles = await ProfilesAsync();
        return docs.Select(d =>
        {
            var type = Cfg.Keyword(vals.FirstOrDefault(v => v.RecordId == d.Id && v.FieldKey == "doc_type")?.RefId)?.Key;
            var months = (profiles.FirstOrDefault(p => p.TypeKey == type && p.Enabled) ?? profiles.LastOrDefault(p => p.TypeKey == type))?.ReviewMonths ?? 0;
            var last = reviews.Where(r => r.ParentId == d.Id && r.PhaseId == completedPhase)
                .Select(r => (Review: r, At: completions.Where(e => e.RecordId == r.Id).Select(e => (DateTime?)e.EnteredAt).Max()))
                .Where(x => x.At != null).OrderByDescending(x => x.At).FirstOrDefault();
            DateOnly? next;
            if (last.Review != null)
            {
                var chosen = nextDates.FirstOrDefault(v => v.RecordId == last.Review.Id)?.Date;
                next = chosen is { } c ? DateOnly.FromDateTime(c) : months > 0 ? DateOnly.FromDateTime(last.At!.Value).AddMonths(months) : null;
            }
            else next = months > 0 && d.EffectiveDate is { } eff ? eff.AddMonths(months) : null;
            return new ReviewInfo
            {
                Doc = d, TypeKey = type, OwnerId = vals.FirstOrDefault(v => v.RecordId == d.Id && v.FieldKey == "owner")?.RefId, Months = months,
                LastReview = last.Review, LastReviewed = last.At is { } la ? DateOnly.FromDateTime(la) : null,
                OpenReview = reviews.FirstOrDefault(r => r.ParentId == d.Id && r.PhaseType == PhaseType.Normal && !r.IsArchived),
                NextReview = next,
            };
        }).ToList();
    }

    public async Task<ReviewInfo?> ReviewInfoAsync(Record doc) =>
        doc.IsEffective ? (await ReviewScheduleAsync([doc])).FirstOrDefault() : null;

    // ------------------------------------------------------------------ revision history (DOC-016/017)

    public async Task<List<RevisionRow>> RevisionHistoryAsync(Record doc)
    {
        var family = doc.FamilyId ?? doc.Id;
        var revs = await db.Records.AsNoTracking().Where(r => r.FamilyId == family).OrderByDescending(r => r.RevisionOrdinal).ToListAsync();
        var ids = revs.Select(r => r.Id).ToList();
        var readable = await access.Readable().Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync();
        var vals = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && (v.FieldKey == "revision_reason" || v.FieldKey == "change_request")).ToListAsync();
        var crIds = vals.Where(v => v.FieldKey == "change_request" && v.RefGuid != null).Select(v => v.RefGuid!.Value).ToList();
        var crNumbers = await db.Records.AsNoTracking().Where(r => crIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number);
        var sigs = await db.Signatures.AsNoTracking().Where(s => ids.Contains(s.RecordId) && !s.Superseded).OrderBy(s => s.SignedAt).ToListAsync();
        var prForm = Cfg.Form(DocKeys.PeriodicReview).Id;
        var orForm = Cfg.Form(DocKeys.ObsoleteRequest).Id;
        var children = await db.Records.AsNoTracking().Where(r => r.ParentId != null && ids.Contains(r.ParentId.Value) && (r.FormId == prForm || r.FormId == orForm))
            .OrderBy(r => r.CreatedAt).ToListAsync();
        return revs.Select(r =>
        {
            var cr = vals.FirstOrDefault(v => v.RecordId == r.Id && v.FieldKey == "change_request")?.RefGuid;
            return new RevisionRow(r, Cfg.Phase(r.PhaseId), vals.FirstOrDefault(v => v.RecordId == r.Id && v.FieldKey == "revision_reason")?.Text,
                sigs.Where(s => s.RecordId == r.Id).ToList(), cr, cr is { } c && crNumbers.TryGetValue(c, out var n) ? n : null,
                children.Where(c => c.ParentId == r.Id && c.FormId == prForm).ToList(), children.Where(c => c.ParentId == r.Id && c.FormId == orForm).ToList(),
                readable.Contains(r.Id));
        }).ToList();
    }

    public Task<Record?> EffectiveRevisionAsync(Guid? familyId) =>
        familyId == null ? Task.FromResult<Record?>(null) : db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.FamilyId == familyId && r.IsEffective);
}
