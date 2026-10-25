using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Pages.Work;

public sealed record WorkItem(Record Record, FormDefinition Form, PhaseDefinition? Phase, int AssigneeId, bool Delegated, DateTime AssignedAt)
{
    public AppDefinition App => Form.App;
    public bool Overdue => Record.DueDate is { } d && d < BusinessCalendar.Today;
    public bool DueToday => Record.DueDate is { } d && d == BusinessCalendar.Today;
    public bool DueSoon => Record.DueDate is { } d && d >= BusinessCalendar.Today && d <= BusinessCalendar.Today.AddDays(7);
}

/// <summary>The current user's open assignments across every application (PLT-1402 Assignments widget, My Assignments).</summary>
[ScopedService]
public sealed class WorkService(VellumDbContext db, CurrentUser me, AccessService access, ConfigCache cfg)
{
    public async Task<List<WorkItem>> MyAssignmentsAsync()
    {
        var ids = access.DelegatorIds.Append(me.Id).ToList();
        var rows = await db.Assignments.AsNoTracking().Include(a => a.Record)
            .Where(a => ids.Contains(a.UserId) && a.Status == AssignmentStatus.Active && a.Record.PhaseType == PhaseType.Normal && !a.Record.IsArchived)
            .ToListAsync();
        return rows
            .Where(a => cfg.Current.FormsById.ContainsKey(a.Record.FormId))
            .GroupBy(a => a.RecordId).Select(g => g.OrderBy(a => a.UserId == me.Id ? 0 : 1).First())
            .Select(a => new WorkItem(a.Record, cfg.Current.FormsById[a.Record.FormId], cfg.Current.Phase(a.Record.PhaseId), a.UserId, a.UserId != me.Id, a.AssignedAt))
            .OrderBy(w => w.Record.DueDate ?? DateOnly.MaxValue).ThenBy(w => w.Record.Number)
            .ToList();
    }
}
