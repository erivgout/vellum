using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Engine.Workflow;

public sealed class RouteOption
{
    public required RouteDefinition Route { get; init; }
    public PhaseDefinition To => Route.ToPhase;
    public RouteKind Kind => Route.Kind;
    public required string Label { get; init; }
    public string? SignatureMeaning => Route.SignatureMeaning;
    public bool RequireComment => Route.RequireComment || Route.Kind == RouteKind.Void || Route.Kind == RouteKind.Back;
    public IReadOnlyList<UserInfo> Candidates { get; init; } = [];
    public IReadOnlyList<int> Defaults { get; init; } = [];
    public bool AssigneesEditable { get; init; }
    public DateOnly? DefaultDue { get; init; }
    public bool DueEditable { get; init; }
    /// <summary>Problems that will block this route right now (missing fields, open activities…).</summary>
    public List<string> Blockers { get; init; } = [];
    /// <summary>Voting / sequential progress note, e.g. "Your approval is 2 of 3 needed".</summary>
    public string? Note { get; init; }
    /// <summary>The route keeps the record in its phase (sequential hand-off or a vote short of quorum).</summary>
    public bool StaysInPhase { get; init; }
}

public sealed class RouteRequest
{
    public required Guid RecordId { get; init; }
    public required int RouteId { get; init; }
    public int? ExpectedVersion { get; init; }
    public IReadOnlyList<int> AssigneeIds { get; init; } = [];
    public DateOnly? DueDate { get; init; }
    public string? Comment { get; init; }
    public string? Password { get; init; }
    public bool Certify { get; init; }
    /// <summary>Field changes to save as part of the route (one transaction).</summary>
    public RecordData? Proposed { get; init; }
    public SaveOptions? SaveOptions { get; init; }
}

public sealed record RouteResult(bool Ok, IReadOnlyList<FieldError> Errors, string? Message = null, bool Moved = false)
{
    public static RouteResult Fail(params string[] errors) => new(false, errors.Select(e => new FieldError(null, e)).ToList());
    public static RouteResult Fail(IReadOnlyList<FieldError> errors) => new(false, errors);
}

/// <summary>
/// Workflow execution (PLT §2.6). Routing is all-or-nothing: field save, validation, gates, signature, transition,
/// behaviors and notifications commit together or not at all (INV-093).
/// </summary>
public sealed class WorkflowEngine(
    VellumDbContext db,
    ConfigCache cfgCache,
    CurrentUser me,
    AccessService access,
    AuditService audit,
    RecordService records,
    AssignmentService assignments,
    SignatureService signatures,
    NotificationService notify,
    BehaviorRegistry behaviors,
    UserDirectory dir,
    IServiceProvider services)
{
    private ConfigModel Cfg => cfgCache.Current;

    private async Task<SystemSettings> SettingsAsync() => await db.Settings.AsNoTracking().FirstAsync();

    public string LabelFor(RouteDefinition r, SystemSettings s) => r.Label ?? r.Kind switch
    {
        RouteKind.Forward => $"{s.LabelForward} to {r.ToPhase.Name}",
        RouteKind.Back => $"{s.LabelBack} to {r.ToPhase.Name}",
        RouteKind.Void => s.LabelVoid,
        _ => $"Skip to {r.ToPhase.Name}",
    };

    /// <summary>Routes the current user can take now, with assignee pickers and blockers worked out.</summary>
    public async Task<List<RouteOption>> OptionsAsync(RecordSnapshot snap)
    {
        var result = new List<RouteOption>();
        var wf = snap.Workflow;
        var phase = snap.Phase;
        if (wf == null || phase == null || phase.Type != PhaseType.Normal || snap.Record.IsArchived) return result;
        var acting = await access.ActingAssignmentAsync(snap.Record.Id);
        if (acting == null) return result;
        var settings = await SettingsAsync();
        var entryAssignments = await CurrentAssignmentsAsync(snap.Record.Id);

        foreach (var route in wf.Routes.Where(r => r.FromPhaseId == phase.Id))
        {
            if (!FormRules.Matches(route.Condition, snap.Data.Main, snap.Form, Cfg)) continue;
            if (route.Kind == RouteKind.SpecialForward && !CanUseSpecial(route)) continue;

            var blockers = new List<string>();
            if (route.Kind is RouteKind.Forward or RouteKind.SpecialForward)
            {
                blockers.AddRange(records.RequiredErrors(snap).Select(e => e.Message));
                blockers.AddRange(await GateErrorsAsync(snap, phase));
            }

            string? note = null;
            var stays = false;
            if (route.Kind == RouteKind.Forward && phase.Routing == RoutingType.Voting)
            {
                var total = entryAssignments.Count(a => a.Status != AssignmentStatus.Cancelled);
                var approvals = entryAssignments.Count(a => a.Vote == VoteDecision.Approve);
                var needed = NeededVotes(total, phase.QuorumPercent);
                stays = approvals + 1 < needed;
                note = stays ? $"Your approval counts as {approvals + 1} of {needed} needed. The record moves on when quorum is reached."
                             : $"Your approval completes the quorum ({needed} of {total}).";
            }
            if (route.Kind == RouteKind.Forward && phase.Routing == RoutingType.Sequential &&
                entryAssignments.Any(a => a.Status == AssignmentStatus.Waiting))
            {
                stays = true;
                var next = entryAssignments.Where(a => a.Status == AssignmentStatus.Waiting).OrderBy(a => a.Order).First();
                note = $"Hands the record to the next reviewer in sequence: {dir.Name(next.UserId)}.";
            }

            IReadOnlyList<UserInfo> cands = [];
            IReadOnlyList<int> defaults = [];
            var editable = false;
            DateOnly? due = null;
            if (!stays && route.ToPhase.Type == PhaseType.Normal)
            {
                (cands, defaults, editable) = await assignments.CandidatesAsync(snap, route.ToPhase);
                due = await assignments.DefaultDueAsync(route.ToPhase, snap.Data.Main);
            }

            result.Add(new RouteOption
            {
                Route = route,
                Label = LabelFor(route, settings),
                Candidates = cands,
                Defaults = defaults,
                AssigneesEditable = editable,
                DefaultDue = due,
                DueEditable = route.ToPhase.AssignerCanChangeDue,
                Blockers = blockers,
                Note = note,
                StaysInPhase = stays,
            });
        }
        return result;
    }

    private bool CanUseSpecial(RouteDefinition r) =>
        me.IsAdmin || (me.Info is { } u && r.AllowedPrincipals.Any(p => p.StartsWith('@') ? dir.IsMember(u.Id, p[1..]) : string.Equals(p, u.Username, StringComparison.OrdinalIgnoreCase)));

    private static int NeededVotes(int total, int quorumPercent) =>
        Math.Max(1, (int)Math.Ceiling(total * Math.Clamp(quorumPercent, 0, 100) / 100.0));

    private Task<List<Assignment>> CurrentAssignmentsAsync(Guid recordId) =>
        db.Assignments.Where(a => a.RecordId == recordId &&
            a.PhaseEntryId == db.PhaseEntries.Where(e => e.RecordId == recordId && e.ExitedAt == null).OrderByDescending(e => e.Id).Select(e => e.Id).FirstOrDefault())
            .ToListAsync();

    /// <summary>PLT-640 / INV-075: activity gates of the phase being left. Voided activities count as complete.</summary>
    public async Task<List<string>> GateErrorsAsync(RecordSnapshot snap, PhaseDefinition phase)
    {
        var errors = new List<string>();
        if (phase.MustCreateForms.Count == 0 && phase.MustCompleteForms.Count == 0) return errors;
        var children = await db.Records.AsNoTracking().Where(r => r.ParentId == snap.Record.Id)
            .Select(r => new { r.FormId, r.PhaseType, r.Number }).ToListAsync();
        foreach (var key in phase.MustCreateForms)
        {
            if (!Cfg.FormsByKey.TryGetValue(key, out var f)) continue;
            if (!children.Any(c => c.FormId == f.Id && c.PhaseType != PhaseType.Voided))
                errors.Add($"Create at least one {f.Name} before leaving {phase.Name}.");
        }
        foreach (var key in phase.MustCompleteForms)
        {
            if (!Cfg.FormsByKey.TryGetValue(key, out var f)) continue;
            var open = children.Where(c => c.FormId == f.Id && c.PhaseType == PhaseType.Normal).Select(c => c.Number).ToList();
            if (open.Count > 0)
                errors.Add($"Complete the open {f.Name.ToLowerInvariant()}{(open.Count > 1 ? "s" : "")} first: {string.Join(", ", open)}.");
        }
        return errors;
    }

    public async Task<RouteResult> RouteAsync(RouteRequest req)
    {
        var snap = await records.LoadAsync(req.RecordId);
        if (snap == null) return RouteResult.Fail("This record no longer exists.");
        var route = snap.Workflow?.Routes.FirstOrDefault(r => r.Id == req.RouteId);
        if (route == null || route.FromPhaseId != snap.Record.PhaseId)
            return RouteResult.Fail("That route isn't available any more. The record may have moved on; reload to see its current phase.");
        if (req.ExpectedVersion is { } ev && ev != snap.Record.Version)
            return RouteResult.Fail("Someone else changed this record after you opened it. Reload, then route again.");

        var acting = await access.ActingAssignmentAsync(snap.Record.Id);
        if (acting == null) return RouteResult.Fail("Only the current assignee (or their active delegate) can route this record.");
        if (route.Kind == RouteKind.SpecialForward && !CanUseSpecial(route)) return RouteResult.Fail("You aren't allowed to use this route.");
        var requireComment = route.RequireComment || route.Kind is RouteKind.Void or RouteKind.Back;
        if (requireComment && string.IsNullOrWhiteSpace(req.Comment))
            return RouteResult.Fail(route.Kind == RouteKind.Void ? "Give a reason for voiding." : "Add a comment explaining this route.");
        var lockState = await db.Locks.AsNoTracking().FirstOrDefaultAsync(l => l.RecordId == snap.Record.Id);
        if (lockState != null && lockState.UserId != me.Id && lockState.LastActivityAt > DateTime.UtcNow.AddMinutes(-LockService.TimeoutMinutes))
            return RouteResult.Fail($"{dir.Name(lockState.UserId)} is editing this record. Try again when they're done.");

        var onBehalfOf = acting.UserId != me.Id ? acting.UserId : (int?)null;

        try
        {
            return await Tx.RunAsync(db, async () =>
            {
                // 1. Save field edits made together with the route.
                if (req.Proposed != null)
                {
                    var save = await records.SaveAsync(snap, req.Proposed, req.SaveOptions ?? new SaveOptions());
                    if (!save.Ok) return RouteResult.Fail(save.Errors);
                    snap = await records.LoadRequiredAsync(req.RecordId);
                }

                // 2. Validate (before any signature is collected, SEC-607).
                var fieldErrors = new List<FieldError>();
                var errors = new List<string>();
                if (!FormRules.Matches(route.Condition, snap.Data.Main, snap.Form, Cfg))
                    errors.Add("This route doesn't apply to the record's current data.");
                if (route.Kind is RouteKind.Forward or RouteKind.SpecialForward)
                {
                    fieldErrors.AddRange(records.RequiredErrors(snap));
                    errors.AddRange(await GateErrorsAsync(snap, route.FromPhase));
                }
                var rctx = new RoutingContext(snap, services, route) { Comment = req.Comment };
                foreach (var b in behaviors.For(snap.Form)) await b.ValidateRouteAsync(rctx);
                errors.AddRange(rctx.Errors);
                if (fieldErrors.Count > 0 || errors.Count > 0)
                    return RouteResult.Fail([.. fieldErrors, .. errors.Select(e => new FieldError(null, e))]);

                // 3. Routing-type semantics of the phase being left.
                var phase = route.FromPhase;
                var current = await CurrentAssignmentsAsync(snap.Record.Id);
                var mine = current.First(a => a.Id == acting.Id);
                var settings = await SettingsAsync();
                var label = LabelFor(route, settings);

                var stays = false;
                string? stayMessage = null;
                if (route.Kind == RouteKind.Forward && phase.Routing == RoutingType.Voting)
                {
                    var total = current.Count(a => a.Status != AssignmentStatus.Cancelled);
                    var needed = NeededVotes(total, phase.QuorumPercent);
                    var approvals = current.Count(a => a.Vote == VoteDecision.Approve) + 1;
                    if (approvals < needed) { stays = true; stayMessage = $"Approval recorded ({approvals} of {needed} needed)."; }
                }
                if (route.Kind == RouteKind.Forward && phase.Routing == RoutingType.Sequential &&
                    current.Any(a => a.Status == AssignmentStatus.Waiting))
                {
                    stays = true;
                    stayMessage = "Handed to the next reviewer.";
                }

                // 4. Resolve the target's assignees and due date.
                var target = route.ToPhase;
                List<int> chosen = [];
                DateOnly? due = null;
                if (!stays && target.Type == PhaseType.Normal)
                {
                    var (cands, defaults, editable) = await assignments.CandidatesAsync(snap, target);
                    chosen = editable && req.AssigneeIds.Count > 0 ? req.AssigneeIds.Distinct().ToList() : defaults.ToList();
                    var invalid = chosen.Where(id => cands.All(c => c.Id != id)).ToList();
                    if (invalid.Count > 0)
                        return RouteResult.Fail($"{string.Join(", ", invalid.Select(dir.Name))} can't be assigned to {target.Name}.");
                    if (chosen.Count == 0)
                        return RouteResult.Fail(cands.Count == 0
                            ? $"Nobody can be assigned to {target.Name}. Ask an administrator to check the phase's assignment rule."
                            : $"Choose who {target.Name} goes to.");
                    due = target.AssignerCanChangeDue && req.DueDate is { } d ? d : await assignments.DefaultDueAsync(target, snap.Data.Main);
                    if (due < BusinessCalendar.Today) return RouteResult.Fail("The due date can't be in the past.");
                }

                // 5. Electronic signature (two-component re-authentication, INV-113).
                if (route.SignatureMeaning is { } meaning)
                {
                    var sig = await signatures.SignAsync(snap, meaning, label, req.Password, req.Certify, onBehalfOf, records.ComputeHash, req.Comment);
                    if (!sig.Ok) return RouteResult.Fail(sig.Error!);
                }

                var r = snap.Record;
                var now = DateTime.UtcNow;
                mine.Status = AssignmentStatus.Completed;
                mine.CompletedAt = now;
                if (phase.Routing == RoutingType.Voting)
                {
                    mine.Vote = route.Kind == RouteKind.Back || route.Kind == RouteKind.Void ? VoteDecision.Reject : VoteDecision.Approve;
                    mine.VoteComment = req.Comment;
                    audit.Record(r.Id, "Vote", $"{(mine.Vote == VoteDecision.Approve ? "Approved" : "Rejected")} in {phase.Name}", reason: req.Comment,
                        phaseName: phase.Name, onBehalfOfId: onBehalfOf);
                }

                if (stays)
                {
                    if (phase.Routing == RoutingType.Sequential)
                    {
                        var next = current.Where(a => a.Status == AssignmentStatus.Waiting).OrderBy(a => a.Order).First();
                        next.Status = AssignmentStatus.Active;
                        next.AssignedAt = now;
                        notify.Assigned(r, snap.Form, phase, [next.UserId], r.DueDate);
                        audit.Record(r.Id, "Handed off", $"Sequential review passed to {dir.Name(next.UserId)}", reason: req.Comment,
                            phaseName: phase.Name, onBehalfOfId: onBehalfOf);
                    }
                    if (!string.IsNullOrWhiteSpace(req.Comment))
                        db.Comments.Add(new Comment { RecordId = r.Id, UserId = me.Id, Text = req.Comment!, Kind = "route", At = now });
                    r.Version++;
                    r.ModifiedAt = now;
                    r.ModifiedById = me.Id;
                    await ReleaseLockAsync(r.Id);
                    await db.SaveChangesAsync();
                    return new RouteResult(true, [], stayMessage, Moved: false);
                }

                // 6. Transition.
                foreach (var a in current.Where(a => a.Id != mine.Id && a.Status is AssignmentStatus.Active or AssignmentStatus.Waiting))
                    a.Status = AssignmentStatus.Cancelled;
                var entry = await db.PhaseEntries.Where(e => e.RecordId == r.Id && e.ExitedAt == null).OrderByDescending(e => e.Id).FirstAsync();
                entry.ExitedAt = now;
                entry.ExitedById = me.Id;
                entry.ExitAction = route.Kind switch { RouteKind.Back => "back", RouteKind.Void => "void", RouteKind.SpecialForward => "special", _ => "forward" };
                entry.Comment = req.Comment;

                if (route.Kind == RouteKind.Back)
                    await SupersedeSignaturesAsync(r.Id, target.Order, $"Routed back to {target.Name}");

                assignments.EnterPhase(r, target, chosen, due, entry.ExitAction, req.Comment);
                r.Version++;
                r.ModifiedAt = now;
                r.ModifiedById = me.Id;

                audit.Record(r.Id, route.Kind switch { RouteKind.Back => "Routed back", RouteKind.Void => "Voided", _ => "Routed" },
                    $"{phase.Name} → {target.Name}",
                    reason: req.Comment, phaseName: target.Name, recordVersion: r.Version, onBehalfOfId: onBehalfOf,
                    oldValue: phase.Name,
                    newValue: chosen.Count > 0 ? $"Assigned to {string.Join(", ", chosen.Select(dir.Name))}; due {due:dd MMM yyyy}" : target.Type.ToString());
                db.Comments.Add(new Comment
                {
                    RecordId = r.Id, UserId = me.Id, IsSystem = string.IsNullOrWhiteSpace(req.Comment), Kind = "route", At = now,
                    Text = $"{label}" + (onBehalfOf != null ? $" (on behalf of {dir.Name(onBehalfOf)})" : "") +
                           (string.IsNullOrWhiteSpace(req.Comment) ? "" : $": {req.Comment}"),
                });

                // 7. Behaviors run in the same transaction (INV-040).
                await db.SaveChangesAsync();
                var after = await records.LoadRequiredAsync(r.Id);
                var done = new RoutingContext(after, services, route) { Comment = req.Comment };
                foreach (var b in behaviors.For(after.Form)) await b.OnRoutedAsync(done);

                // 8. Notify.
                notify.Assigned(r, snap.Form, target, chosen.Where(id => id != me.Id), due);
                await ReleaseLockAsync(r.Id);
                await db.SaveChangesAsync();
                return new RouteResult(true, [], target.Type == PhaseType.Normal ? $"Sent to {target.Name}." : $"{snap.Form.Name} is now {target.Name}.", Moved: true);
            }, res => res.Ok);
        }
        catch (WorkflowException ex)
        {
            return RouteResult.Fail(ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RouteResult.Fail("Someone else changed this record at the same time. Reload and try again.");
        }
    }

    /// <summary>
    /// Route without a human assignee: escalations, auto-routes and behaviors (PLT-650). Never signs (SEC-609).
    /// Required-field and gate validation still apply unless <paramref name="skipValidation"/> is set, which is audited (PLT-651).
    /// </summary>
    public async Task<RouteResult> SystemRouteAsync(Guid recordId, string toPhaseKey, string reason, IReadOnlyList<int>? assignees = null,
        DateOnly? due = null, bool skipValidation = false, string? comment = null)
    {
        var snap = await records.LoadRequiredAsync(recordId);
        var wf = snap.Workflow ?? throw new WorkflowException("Record has no workflow.");
        var target = wf.Phases.FirstOrDefault(p => p.Key == toPhaseKey) ?? throw new WorkflowException($"Unknown phase '{toPhaseKey}'.");
        var from = snap.Phase!;
        if (from.Id == target.Id) return new RouteResult(true, [], null, false);

        if (!skipValidation && target.Type is PhaseType.Normal or PhaseType.Completed && target.Order > from.Order)
        {
            var errs = records.RequiredErrors(snap).Select(e => e.Message).Concat(await GateErrorsAsync(snap, from)).ToList();
            if (errs.Count > 0) return RouteResult.Fail(errs.ToArray());
        }

        return await Tx.RunAsync(db, async () =>
        {
            var r = snap.Record;
            var now = DateTime.UtcNow;
            List<int> chosen = [];
            DateOnly? dueDate = null;
            if (target.Type == PhaseType.Normal)
            {
                var (_, defaults, _) = await assignments.CandidatesAsync(snap, target);
                chosen = (assignees ?? defaults).ToList();
                if (chosen.Count == 0) chosen = [r.CreatedById];
                dueDate = due ?? await assignments.DefaultDueAsync(target, snap.Data.Main);
            }
            var open = await db.Assignments.Where(a => a.RecordId == r.Id && (a.Status == AssignmentStatus.Active || a.Status == AssignmentStatus.Waiting)).ToListAsync();
            foreach (var a in open) a.Status = AssignmentStatus.Cancelled;
            var entry = await db.PhaseEntries.Where(e => e.RecordId == r.Id && e.ExitedAt == null).OrderByDescending(e => e.Id).FirstOrDefaultAsync();
            if (entry != null)
            {
                entry.ExitedAt = now;
                entry.ExitedById = me.Id;
                entry.ExitAction = me.IsSystem ? "system" : "auto";
                entry.Comment = comment ?? reason;
            }
            if (target.Order < from.Order) await SupersedeSignaturesAsync(r.Id, target.Order, reason);
            assignments.EnterPhase(r, target, chosen, dueDate, "system", comment ?? reason);
            r.Version++;
            r.ModifiedAt = now;
            r.ModifiedById = me.Id;
            audit.Record(r.Id, "System route", $"{from.Name} → {target.Name}", reason: reason + (skipValidation ? " (validation skipped by configuration)" : ""),
                phaseName: target.Name, recordVersion: r.Version);
            db.Comments.Add(new Comment { RecordId = r.Id, UserId = me.IsSystem ? null : me.Id, IsSystem = true, Kind = "system", At = now, Text = $"Moved to {target.Name}: {reason}" });
            await db.SaveChangesAsync();

            var route = wf.Routes.FirstOrDefault(x => x.FromPhaseId == from.Id && x.ToPhaseId == target.Id)
                        ?? new RouteDefinition { FromPhase = from, ToPhase = target, FromPhaseId = from.Id, ToPhaseId = target.Id, Kind = RouteKind.Forward, Workflow = wf };
            var after = await records.LoadRequiredAsync(r.Id);
            var ctx = new RoutingContext(after, services, route) { Comment = comment, IsSystem = true };
            foreach (var b in behaviors.For(after.Form)) await b.OnRoutedAsync(ctx);
            notify.Assigned(r, snap.Form, target, chosen.Where(id => id != me.Id), dueDate);
            await db.SaveChangesAsync();
            return new RouteResult(true, [], $"Moved to {target.Name}.", true);
        }, res => res.Ok);
    }

    /// <summary>PLT-607: reassign the current phase.</summary>
    public async Task<RouteResult> ReassignAsync(Guid recordId, IReadOnlyList<int> userIds, string? comment)
    {
        var snap = await records.LoadRequiredAsync(recordId);
        var r = snap.Record;
        if (snap.Phase is not { Type: PhaseType.Normal } phase) return RouteResult.Fail("Only open records can be reassigned.");
        var isManager = access.RoleFor(r.AppId) >= AppRole.Manager;
        var acting = await access.ActingAssignmentAsync(r.Id);
        if (!isManager && acting == null) return RouteResult.Fail("Only the current assignee or an application manager can reassign.");
        if (userIds.Count == 0) return RouteResult.Fail("Choose at least one person.");
        var (cands, _, _) = await assignments.CandidatesAsync(snap, phase);
        var invalid = userIds.Where(id => cands.All(c => c.Id != id) && dir.Get(id) is not { Assignable: true }).ToList();
        if (invalid.Count > 0) return RouteResult.Fail($"{string.Join(", ", invalid.Select(dir.Name))} can't be assigned.");

        return await Tx.RunAsync(db, async () =>
        {
            var entry = await db.PhaseEntries.Where(e => e.RecordId == r.Id && e.ExitedAt == null).OrderByDescending(e => e.Id).FirstAsync();
            var open = await db.Assignments.Where(a => a.PhaseEntryId == entry.Id && (a.Status == AssignmentStatus.Active || a.Status == AssignmentStatus.Waiting)).ToListAsync();
            var before = string.Join(", ", open.Select(a => dir.Name(a.UserId)));
            foreach (var a in open) a.Status = AssignmentStatus.Cancelled;
            var now = DateTime.UtcNow;
            for (var i = 0; i < userIds.Count; i++)
                db.Assignments.Add(new Assignment
                {
                    RecordId = r.Id, PhaseEntryId = entry.Id, UserId = userIds[i], Order = i, AssignedAt = now, AssignedById = me.Id,
                    Status = phase.Routing == RoutingType.Sequential && i > 0 ? AssignmentStatus.Waiting : AssignmentStatus.Active,
                });
            r.Version++;
            r.ModifiedAt = now;
            audit.Record(r.Id, "Reassigned", $"{phase.Name} reassigned", reason: comment, oldValue: before,
                newValue: string.Join(", ", userIds.Select(dir.Name)), phaseName: phase.Name);
            db.Comments.Add(new Comment { RecordId = r.Id, UserId = me.Id, IsSystem = true, Kind = "route", At = now,
                Text = $"Reassigned to {string.Join(", ", userIds.Select(dir.Name))}" + (string.IsNullOrWhiteSpace(comment) ? "" : $": {comment}") });
            notify.Assigned(r, snap.Form, phase, userIds.Where(id => id != me.Id), r.DueDate);
            await db.SaveChangesAsync();
            return new RouteResult(true, [], "Reassigned.", false);
        }, res => res.Ok);
    }

    /// <summary>PLT-1902: administrative Global Route (reason required, recorded as an override).</summary>
    public async Task<RouteResult> GlobalRouteAsync(Guid recordId, string toPhaseKey, string reason, IReadOnlyList<int>? assignees, DateOnly? due)
    {
        var snap = await records.LoadRequiredAsync(recordId);
        if (access.RoleFor(snap.Record.AppId) < AppRole.Manager) return RouteResult.Fail("Only application managers can use Global Route.");
        if (string.IsNullOrWhiteSpace(reason)) return RouteResult.Fail("Give a reason for the administrative route.");
        audit.Record(recordId, "Administrative override", $"Global Route to {toPhaseKey}", reason: reason);
        return await SystemRouteAsync(recordId, toPhaseKey, $"Global Route by {me.Name}: {reason}", assignees, due, skipValidation: false);
    }

    /// <summary>DOC-042 / INV-052: routing back supersedes signatures of the target phase and later. Never deletes them.</summary>
    private async Task SupersedeSignaturesAsync(Guid recordId, int fromOrder, string why)
    {
        var sigs = await db.Signatures.Where(s => s.RecordId == recordId && !s.Superseded && s.PhaseOrder >= fromOrder).ToListAsync();
        foreach (var s in sigs)
        {
            s.Superseded = true;
            s.SupersededAt = DateTime.UtcNow;
            audit.Record(recordId, "Signature superseded", $"{s.SignerName} \"{s.Meaning}\" in {s.PhaseName} superseded", reason: why);
        }
    }

    private async Task ReleaseLockAsync(Guid recordId)
    {
        var l = await db.Locks.FirstOrDefaultAsync(x => x.RecordId == recordId && x.UserId == me.Id);
        if (l != null) db.Locks.Remove(l);
    }
}
