using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;

namespace Vellum.Pages.Records;

public sealed record LinkedRecord(Record Record, FormDefinition Form, PhaseDefinition? Phase, string Relation, bool Outgoing);

public sealed record TrackerStep(PhaseDefinition Phase, string State, string? Sub, PhaseType Type);

/// <summary>Everything the record page (and the New page) renders.</summary>
public sealed class RecordVm
{
    public Record? Record { get; init; }
    public bool IsNew => Record == null;
    public required FormDefinition Form { get; init; }
    public AppDefinition App => Form.App;
    public WorkflowDefinition? Workflow { get; init; }
    public PhaseDefinition? Phase { get; init; }
    public Record? Parent { get; init; }
    public required RecordData Data { get; init; }
    public List<Attachment> Attachments { get; init; } = [];
    public required ConfigModel Config { get; init; }
    public required UserDirectory Dir { get; init; }
    public required TimeZoneInfo Tz { get; init; }

    public bool Editing { get; init; }
    public bool Override { get; init; }
    public string? ReadOnlyReason { get; init; }
    public LockState? Lock { get; init; }
    public bool IsManager { get; init; }
    public bool CanSeeSensitive { get; init; }
    public bool CanComment { get; init; }
    public bool CanReassign { get; init; }
    public bool MeCertified { get; init; }
    public string? LocationName { get; init; }

    public List<FieldError> Errors { get; set; } = [];
    public List<string> RouteErrors { get; set; } = [];
    public int? OpenRouteId { get; set; }
    public string? OpenActionKey { get; set; }
    public List<string> ActionErrors { get; set; } = [];

    public List<RouteOption> Routes { get; init; } = [];
    public List<RecordAction> Actions { get; init; } = [];
    public List<RecordPanel> Panels { get; init; } = [];
    public List<FormDefinition> ActivityForms { get; init; } = [];
    public List<LinkedRecord> Linked { get; init; } = [];
    public List<PhaseEntry> PhaseEntries { get; init; } = [];
    public List<Assignment> CurrentAssignments { get; init; } = [];
    public List<Signature> Signatures { get; init; } = [];
    public string? CurrentHash { get; init; }
    public List<Comment> Comments { get; init; } = [];
    public List<AuditEntry> Audit { get; init; } = [];
    public List<EmailLogEntry> Emails { get; init; } = [];
    public IReadOnlyDictionary<Guid, string> LinkLabels { get; init; } = new Dictionary<Guid, string>();
    public List<TrackerStep> Tracker { get; init; } = [];
    public List<int> FieldHistoryKeys { get; init; } = [];

    public SectionState StateOf(SectionDefinition s)
    {
        if (Override) return SectionState.Editable;
        var probe = Record ?? new Record { PhaseType = Phase?.Type ?? PhaseType.Normal, WorkflowId = Workflow?.Id };
        var state = FormRules.StateOf(s, Phase, probe);
        return state == SectionState.Editable && !Editing ? SectionState.ReadOnly : state;
    }

    public IEnumerable<string> Tabs => Form.Sections.Where(s => StateOf(s) != SectionState.Hidden).Select(s => s.Tab).Distinct();

    public string? ErrorFor(string key, Guid? rowId = null) =>
        Errors.FirstOrDefault(e => e.FieldKey == key && e.RowId == rowId)?.Message;

    public bool TabHasError(string tab) =>
        Errors.Any(e => e.FieldKey != null && Form.Fields.FirstOrDefault(f => f.Key == e.FieldKey) is { } f &&
                        Form.Sections.FirstOrDefault(s => s.Key == f.SectionKey)?.Tab == tab) ||
        Errors.Any(e => Form.Sections.FirstOrDefault(s => s.Key == e.FieldKey)?.Tab == tab);

    /// <summary>Uploaded files of a field; derived PDF renditions are shown next to their original, not as uploads.</summary>
    public IEnumerable<Attachment> AttachmentsOf(string key) => Attachments.Where(a => a.FieldKey == key && a.RowId == null && !a.Removed && a.RenditionOfId == null);

    public int ActiveSignatures => Signatures.Count(s => !s.Superseded);
    public bool IsOverdue => Record is { PhaseType: PhaseType.Normal, DueDate: { } d } && d < BusinessCalendar.Today;
    public bool IsDueToday => Record is { PhaseType: PhaseType.Normal, DueDate: { } d } && d == BusinessCalendar.Today;
}

/// <summary>Builds <see cref="RecordVm"/> from the engine. Every list here is already permission-checked.</summary>
[ScopedService]
public sealed class RecordVmBuilder(
    VellumDbContext db, ConfigCache cfg, UserDirectory dir, CurrentUser me, AccessService access, RecordService records,
    WorkflowEngine workflow, LockService locks, BehaviorRegistry behaviors, IServiceProvider services)
{
    public async Task<RecordVm> BuildAsync(RecordSnapshot snap, RecordData? posted = null, bool overrideMode = false, bool readOnly = false)
    {
        var r = snap.Record;
        var can = await access.CanEditAsync(r);
        var isManager = access.RoleFor(r.AppId) >= AppRole.Manager;
        var lockState = await locks.GetAsync(r.Id);
        var lockedByOther = lockState.HolderId != null && !lockState.Mine && !lockState.Expired;
        var editing = !readOnly && ((can.Allowed && !lockedByOther) || (overrideMode && isManager && !r.IsArchived));
        var ctx = new RecordContext(snap, services);

        var actions = new List<RecordAction>();
        var panels = new List<RecordPanel>();
        foreach (var b in behaviors.For(snap.Form))
        {
            actions.AddRange(await b.ActionsAsync(ctx));
            panels.AddRange(await b.PanelsAsync(ctx));
        }

        var phase = snap.Phase;
        var activityForms = new List<FormDefinition>();
        if (phase is { Type: PhaseType.Normal } && !r.IsArchived)
        {
            var allowed = snap.Config.ActivityFormsFor(snap.Form).Where(f => phase.ActivityForms.Count == 0 || phase.ActivityForms.Contains(f.Key));
            var acting = await access.ActingAssignmentAsync(r.Id);
            if (acting != null || isManager)
                activityForms = allowed.Where(f => access.RoleFor(f.AppId, f.Key) >= AppRole.Reader).ToList();
        }

        var linksOut = await db.Links.AsNoTracking().Include(l => l.To).Where(l => l.FromId == r.Id).ToListAsync();
        var linksIn = await db.Links.AsNoTracking().Include(l => l.From).Where(l => l.ToId == r.Id).ToListAsync();
        var readable = await access.Readable().Where(x => linksOut.Select(l => l.ToId).Contains(x.Id) || linksIn.Select(l => l.FromId).Contains(x.Id) || x.Id == r.ParentId)
            .Select(x => x.Id).ToListAsync();
        var linked = new List<LinkedRecord>();
        foreach (var l in linksOut.Where(l => readable.Contains(l.ToId)))
            linked.Add(new LinkedRecord(l.To, cfg.Current.FormsById[l.To.FormId], cfg.Current.Phase(l.To.PhaseId),
                l.Kind switch { LinkKind.Activity => "Activity", LinkKind.Revision => "Revision", LinkKind.Source => "Created", LinkKind.Field => snap.Form.Fields.FirstOrDefault(f => f.Key == l.FieldKey)?.Label ?? "Linked", _ => "Related" }, true));
        foreach (var l in linksIn.Where(l => readable.Contains(l.FromId)))
            linked.Add(new LinkedRecord(l.From, cfg.Current.FormsById[l.From.FormId], cfg.Current.Phase(l.From.PhaseId),
                l.Kind switch { LinkKind.Activity => "Parent", LinkKind.Revision => "Revision", LinkKind.Source => "Source", _ => "Referenced by" }, false));

        var entries = await db.PhaseEntries.AsNoTracking().Include(e => e.Phase).Where(e => e.RecordId == r.Id).OrderBy(e => e.Id).ToListAsync();
        var current = entries.LastOrDefault(e => e.ExitedAt == null);
        var assignments = current == null ? [] : await db.Assignments.AsNoTracking().Where(a => a.PhaseEntryId == current.Id && a.Status != AssignmentStatus.Cancelled).OrderBy(a => a.Order).ToListAsync();

        var data = posted ?? snap.Data;
        var allValues = data.Main.Keys.SelectMany(k => data.Main[k]).Concat(data.Rows.Values.SelectMany(rows => rows.SelectMany(x => x.Values.Keys.SelectMany(k => x.Values[k]))));
        var labels = await records.LinkLabelsAsync(allValues);
        var auditTrail = await db.Audit.AsNoTracking().Where(a => a.RecordId == r.Id).OrderByDescending(a => a.Seq).Take(500).ToListAsync();
        if (!access.CanSeeSensitive(r.AppId)) AuditService.MaskSensitive(auditTrail, snap.Form); // SEC-520

        return new RecordVm
        {
            Record = r,
            Form = snap.Form,
            Workflow = snap.Workflow,
            Phase = phase,
            Parent = r.ParentId is { } pid && readable.Contains(pid) ? await db.Records.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid) : null,
            Data = data,
            Attachments = snap.Attachments,
            Config = cfg.Current,
            Dir = dir,
            Tz = records.TimeZone,
            Editing = editing,
            Override = overrideMode && isManager && !r.IsArchived,
            ReadOnlyReason = editing ? null : lockedByOther ? $"{lockState.HolderName} is editing this record." : can.Reason,
            Lock = lockState,
            IsManager = isManager,
            CanSeeSensitive = access.CanSeeSensitive(r.AppId),
            CanComment = access.RoleFor(r.AppId) >= AppRole.Reader,
            CanReassign = r.PhaseType == PhaseType.Normal && (isManager || await access.ActingAssignmentAsync(r.Id) != null),
            LocationName = r.LocationId is { } locId ? await db.Locations.Where(l => l.Id == locId).Select(l => l.Name).FirstOrDefaultAsync() : null,
            MeCertified = await db.Users.Where(u => u.Id == me.Id).Select(u => u.ESignatureCertifiedAt != null).FirstOrDefaultAsync(),
            Routes = await workflow.OptionsAsync(snap),
            Actions = actions,
            Panels = panels.OrderBy(p => p.Order).ToList(),
            ActivityForms = activityForms,
            Linked = linked,
            PhaseEntries = entries,
            CurrentAssignments = assignments,
            Signatures = await db.Signatures.AsNoTracking().Where(s => s.RecordId == r.Id).OrderBy(s => s.SignedAt).ToListAsync(),
            CurrentHash = records.ComputeHash(snap),
            Comments = await db.Comments.AsNoTracking().Include(c => c.User).Where(c => c.RecordId == r.Id).OrderByDescending(c => c.At).Take(200).ToListAsync(),
            Audit = auditTrail,
            Emails = await db.EmailLog.AsNoTracking().Where(e => e.RecordId == r.Id).OrderByDescending(e => e.At).Take(100).ToListAsync(),
            LinkLabels = labels,
            Tracker = BuildTracker(snap, entries),
        };
    }

    public async Task<RecordVm> BuildNewAsync(FormDefinition form, RecordData data, Record? parent, List<FieldError>? errors = null)
    {
        var wf = form.Workflows.FirstOrDefault(w => !w.Disabled);
        var start = wf?.Phases.FirstOrDefault(p => p.IsStart);
        var labels = await records.LinkLabelsAsync(data.Main.Keys.SelectMany(k => data.Main[k]));
        return new RecordVm
        {
            Form = form, Workflow = wf, Phase = start, Parent = parent, Data = data, Config = cfg.Current, Dir = dir, Tz = records.TimeZone,
            Editing = true, IsManager = access.RoleFor(form.AppId) >= AppRole.Manager, CanSeeSensitive = access.CanSeeSensitive(form.AppId),
            Errors = errors ?? [], LinkLabels = labels,
            Tracker = wf == null ? [] : MainPath(wf, start).Select(p => new TrackerStep(p, p == start ? "current" : "upcoming", null, p.Type)).ToList(),
        };
    }

    private static IEnumerable<PhaseDefinition> MainPath(WorkflowDefinition wf, PhaseDefinition? current)
    {
        var firstDone = wf.Phases.FirstOrDefault(p => p.Type == PhaseType.Completed);
        return wf.Phases.Where(p => p.Type == PhaseType.Normal || p == firstDone || p == current);
    }

    private static List<TrackerStep> BuildTracker(RecordSnapshot snap, List<PhaseEntry> entries)
    {
        var wf = snap.Workflow;
        var current = snap.Phase;
        if (wf == null || current == null) return [];
        var visited = entries.Select(e => e.PhaseId).ToHashSet();
        var lastExit = entries.Where(e => e.ExitedAt != null).GroupBy(e => e.PhaseId).ToDictionary(g => g.Key, g => g.Max(e => e.ExitedAt!.Value));
        var terminal = current.Type != PhaseType.Normal;
        var path = wf.Phases.Where(p =>
            p.Id == current.Id ||
            (p.Type == PhaseType.Normal && (!terminal || visited.Contains(p.Id))) ||
            (!terminal && p.Type == PhaseType.Completed && p == wf.Phases.First(x => x.Type == PhaseType.Completed)))
            .OrderBy(p => p.Order).ToList();
        var today = BusinessCalendar.Today;
        return path.Select(p =>
        {
            if (p.Id == current.Id)
            {
                string? sub = null;
                var state = "current";
                if (terminal) state = current.Type == PhaseType.Voided ? "terminal void" : "done";
                else if (snap.Record.DueDate is { } d)
                {
                    var days = d.DayNumber - today.DayNumber;
                    sub = days < 0 ? $"Overdue {-days}d" : days == 0 ? "Due today" : $"Due {d:dd MMM}";
                    state += days < 0 ? " overdue" : days == 0 ? " today" : "";
                }
                return new TrackerStep(p, state, terminal ? entries.LastOrDefault()?.EnteredAt.ToString("dd MMM") : sub, p.Type);
            }
            if (visited.Contains(p.Id) && (p.Order < current.Order || terminal))
                return new TrackerStep(p, "done", lastExit.TryGetValue(p.Id, out var at) ? at.ToString("dd MMM") : null, p.Type);
            return new TrackerStep(p, "upcoming", null, p.Type);
        }).ToList();
    }
}

/// <summary>One field to render (main form or a subform row).</summary>
public sealed record FieldCtx(RecordVm Vm, FieldDefinition F, ValueBag Bag, string Name, string Id, bool Editable, Guid? RowId = null, bool Compact = false)
{
    public IReadOnlyList<Val> Values => Bag[F.Key];
    public string? Error => Vm.ErrorFor(F.Key, RowId);
    public bool IsRequired => F.Required || (Vm.Phase != null && F.RequiredInPhases.Contains(Vm.Phase.Key));
    public bool Hidden => F.Sensitive && !Vm.CanSeeSensitive;
    public string Display => FieldCodec.Display(F, Values, Vm.Config, Vm.Dir, Vm.Tz, Vm.LinkLabels);
}
