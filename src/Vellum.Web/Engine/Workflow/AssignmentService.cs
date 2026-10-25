using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Engine.Workflow;

/// <summary>Phase entry, assignment rules (PLT-620..623) and due dates (PLT-630/631).</summary>
public sealed class AssignmentService(VellumDbContext db, ConfigCache cfg, CurrentUser me, UserDirectory dir, AccessService access, BusinessCalendar calendar)
{
    /// <summary>
    /// Candidate assignees for entering <paramref name="phase"/>: who may be picked, who is preselected, and whether
    /// the router may change the choice. Inactive users are never candidates (INV-073); candidates can read the record (INV-080).
    /// </summary>
    public Task<(IReadOnlyList<UserInfo> Candidates, IReadOnlyList<int> Defaults, bool Editable)> CandidatesAsync(RecordSnapshot snap, PhaseDefinition phase)
    {
        IReadOnlyList<UserInfo> FromField() => phase.AssigneeField is { } f
            ? snap.Data.Main.Refs(f).Select(dir.Get).OfType<UserInfo>().Where(u => u.Assignable).ToList()
            : [];

        (IReadOnlyList<UserInfo>, IReadOnlyList<int>, bool) result;
        switch (phase.Assignment)
        {
            case AssignmentMode.Field:
                var forced = FromField();
                result = forced.Count > 0
                    ? (forced, forced.Select(u => u.Id).ToList(), false)
                    : (Readers(snap.Form), [], true); // field empty: fall back to the directory (PLT-622) with an explicit pick
                break;
            case AssignmentMode.Group:
                var members = phase.AssigneeGroup is { } g ? dir.ActiveMembers(g).ToList() : [];
                result = phase.AssignAllMembers
                    ? (members, members.Select(u => u.Id).ToList(), false)
                    : (members, (FromField().Where(u => members.Any(m => m.Id == u.Id)).Select(u => u.Id).ToList() is { Count: > 0 } picked ? picked : members.Select(u => u.Id).ToList()), true);
                break;
            case AssignmentMode.Creator:
                var creator = dir.Get(snap.Record.CreatedById);
                result = creator is { Assignable: true }
                    ? ([creator], [creator.Id], false)
                    : (Readers(snap.Form), [], true);
                break;
            default:
                result = (Readers(snap.Form), FromField().Select(u => u.Id).ToList(), true);
                break;
        }
        return Task.FromResult(result);
    }

    private List<UserInfo> Readers(FormDefinition form) =>
        dir.Assignable.Where(u => access.UserCanReadForm(u.Id, form)).ToList();

    public async Task<DateOnly?> DefaultDueAsync(PhaseDefinition phase, ValueBag data)
    {
        if (phase.Type != PhaseType.Normal) return null;
        if (phase.DueDateField is { } f && data.Date(f) is { } fromField) return fromField;
        return await calendar.AddAsync(BusinessCalendar.Today, phase.DueDays ?? 5, phase.BusinessDays);
    }

    /// <summary>Close nothing, open a new phase entry with assignments. Callers close the previous entry first.</summary>
    public PhaseEntry EnterPhase(Record r, PhaseDefinition phase, IReadOnlyList<int> assigneeIds, DateOnly? due, string action, string? comment)
    {
        var now = DateTime.UtcNow;
        var entry = new PhaseEntry
        {
            RecordId = r.Id,
            PhaseId = phase.Id,
            PhaseName = phase.Name,
            EnteredAt = now,
            EnteredById = me.Id,
            DueDate = phase.Type == PhaseType.Normal ? due : null,
            Comment = action == "create" ? null : comment,
            ConfigVersion = cfg.Current.Version,
        };
        db.PhaseEntries.Add(entry);

        r.PhaseId = phase.Id;
        r.PhaseType = phase.Type;
        r.DueDate = entry.DueDate;
        r.ConfigVersion = cfg.Current.Version;

        if (phase.Type == PhaseType.Normal)
        {
            if (assigneeIds.Count == 0)
                throw new WorkflowException($"{phase.Name} needs at least one assignee (INV-072).");
            for (var i = 0; i < assigneeIds.Count; i++)
            {
                var u = dir.Get(assigneeIds[i]);
                if (u is not { Assignable: true }) throw new WorkflowException($"{u?.DisplayName ?? "That user"} is inactive and can't be assigned (INV-073).");
                db.Assignments.Add(new Assignment
                {
                    RecordId = r.Id,
                    PhaseEntry = entry,
                    UserId = u.Id,
                    Order = i,
                    Status = phase.Routing == RoutingType.Sequential && i > 0 ? AssignmentStatus.Waiting : AssignmentStatus.Active,
                    AssignedAt = now,
                    AssignedById = me.Id,
                });
            }
        }
        return entry;
    }
}

public sealed class WorkflowException(string message) : Exception(message);
