using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Security;

namespace Vellum.Modules.Integrations.SharePoint;

public sealed record SpPanelFile(SharePointPublication Pub, string Library, string Host, string? Url);

public sealed record SpPanelJob(SharePointJob Job, string? RuleName);

/// <param name="State">published | pending | failed | withdrawn | none</param>
public sealed record SharePointPanelModel(string State, List<SpPanelFile> Current, List<SpPanelFile> History, SpPanelJob? Pending, SpPanelJob? Failed,
    string? WaitingNote, bool IsAdmin, Guid RecordId, TimeZoneInfo Tz);

/// <summary>
/// Hooks SharePoint publishing into every form that has a publishing rule (DSN-602, INT-304). Routing into or saving in a
/// configured phase only adds a row to the durable queue in the same transaction; nothing here calls SharePoint, so records
/// stay fully usable while it is down (NFR-025). When a revision is superseded, obsoleted or voided its files are queued for
/// withdrawal (INT-308, SAF-021). Also renders the "Published to SharePoint" card (INT-306/307).
/// </summary>
public sealed class SharePointBehavior(VellumDbContext db, ILogger<SharePointBehavior> log) : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => SharePointRuleCache.Forms(db);

    /// <summary>After the module's own behaviors, so a released revision is already effective and its predecessor archived.</summary>
    public override int Order => 1000;

    private IEnumerable<SharePointRule> RulesFor(string formKey) => SharePointRuleCache.Rules(db).Where(r => r.Enabled && r.FormKey == formKey);

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        try
        {
            var queue = ctx.Get<SharePointQueue>();
            foreach (var rule in RulesFor(ctx.Snapshot.Form.Key).Where(r => r.OnEnter && r.PhaseKey == ctx.To.Key))
                await queue.PublishAsync(ctx.Record.Id, rule.Id, $"Routed into {ctx.To.Name}");
            await QueueWithdrawalsAsync(ctx.Record, queue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NFR-025: never block a route because of publishing; the reconciliation sweep still withdraws stale files.
            log.LogWarning(ex, "Couldn't queue SharePoint publishing for {Record}", ctx.Record.Number);
        }
    }

    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        try
        {
            var queue = ctx.Get<SharePointQueue>();
            var phase = ctx.Snapshot.PhaseKey;
            foreach (var rule in RulesFor(ctx.Snapshot.Form.Key).Where(r => r.PhaseKey == "*" || (r.OnEnter && r.PhaseKey == phase)))
                await queue.PublishAsync(ctx.Record.Id, rule.Id, phase.Length > 0 ? $"Created in {ctx.Snapshot.Phase?.Name}" : "Created");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't queue SharePoint publishing for {Record}", ctx.Record.Number);
        }
    }

    /// <summary>
    /// "Saved within a configured phase" (INT-304). The queued job re-reads the record when it runs, so it publishes what was
    /// saved; if the save is then rejected the job finds nothing changed and leaves SharePoint untouched.
    /// </summary>
    public override async Task OnSavingAsync(SavingContext ctx)
    {
        if (ctx.IsNew || ctx.Current == null) return;
        try
        {
            var phase = ctx.Current.PhaseKey;
            var rules = RulesFor(ctx.Form.Key).Where(r => r.PhaseKey == "*" || (r.OnSave && r.PhaseKey == phase)).ToList();
            if (rules.Count == 0) return;
            var queue = ctx.Services.GetRequiredService<SharePointQueue>();
            foreach (var rule in rules)
                await queue.PublishAsync(ctx.Current.Record.Id, rule.Id, phase.Length > 0 ? $"Saved in {ctx.Current.Phase?.Name}" : "Saved");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't queue SharePoint publishing for {Record}", ctx.Current.Record.Number);
        }
    }

    /// <summary>The record or any revision of its family that is no longer current but still has files published.</summary>
    private async Task QueueWithdrawalsAsync(Record record, SharePointQueue queue)
    {
        var withFiles = await db.Set<SharePointPublication>().AsNoTracking()
            .Where(p => p.Status == SpPublicationStatus.Published && (p.RecordId == record.Id || (record.FamilyId != null && p.FamilyId == record.FamilyId)))
            .Select(p => p.RecordId).Distinct().ToListAsync();
        if (withFiles.Count == 0) return;
        var stale = await db.Records.AsNoTracking()
            .Where(r => withFiles.Contains(r.Id) && (r.IsArchived || r.PhaseType == PhaseType.Voided || r.PhaseType == PhaseType.Obsolete))
            .Select(r => new { r.Id, r.Revision, r.ArchiveReason, r.PhaseType }).ToListAsync();
        foreach (var s in stale)
        {
            var why = s.ArchiveReason == "superseded" ? $"Revision {s.Revision} superseded by revision {record.Revision}"
                : s.ArchiveReason == "obsolete" || s.PhaseType == PhaseType.Obsolete ? $"Revision {s.Revision} made obsolete"
                : s.PhaseType == PhaseType.Voided ? "Voided" : "Archived";
            await queue.WithdrawAsync(s.Id, why);
        }
    }

    // ------------------------------------------------------------------ panel (INT-306, INT-307)

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        try
        {
            var model = await BuildPanelAsync(ctx);
            if (model == null) return [];
            return
            [
                new RecordPanel
                {
                    Key = "sharepoint", Title = "Published to SharePoint", Icon = "bi-cloud-arrow-up", Placement = "side", Order = 50,
                    Partial = "~/Modules/Integrations/SharePoint/Views/_PublishedPanel.cshtml", Model = model,
                    Count = model.Current.Count > 0 ? model.Current.Count : null,
                },
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "SharePoint panel unavailable for {Record}", ctx.Record.Number);
            return [];
        }
    }

    private async Task<SharePointPanelModel?> BuildPanelAsync(RecordContext ctx)
    {
        var r = ctx.Record;
        var pubs = await db.Set<SharePointPublication>().AsNoTracking().Where(p => p.RecordId == r.Id).OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).Take(40).ToListAsync();
        var jobs = await db.Set<SharePointJob>().AsNoTracking().Where(j => j.RecordId == r.Id).OrderByDescending(j => j.Id).Take(20).ToListAsync();
        var rules = RulesFor(ctx.Snapshot.Form.Key).ToList();
        var inPhase = rules.Where(x => x.PhaseKey == "*" || x.PhaseKey == ctx.Snapshot.PhaseKey).ToList();
        if (pubs.Count == 0 && jobs.Count == 0 && inPhase.Count == 0) return null;

        var profileIds = pubs.Select(p => p.ProfileId).Concat(rules.Select(x => x.ProfileId)).Distinct().ToList();
        var profiles = await db.Set<SharePointProfile>().AsNoTracking().Include(p => p.Libraries).Where(p => profileIds.Contains(p.Id)).ToListAsync();
        var libs = profiles.SelectMany(p => p.Libraries).ToDictionary(l => l.Id);
        SpPanelFile File(SharePointPublication p)
        {
            var lib = libs.GetValueOrDefault(p.LibraryId);
            return new SpPanelFile(p, lib?.Name ?? "Library", lib?.Profile?.Host ?? profiles.FirstOrDefault(x => x.Id == p.ProfileId)?.Host ?? "",
                p.WebUrl is { } u && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u : null);
        }
        var current = pubs.Where(p => p.Status == SpPublicationStatus.Published).Select(File).ToList();
        var history = pubs.Where(p => p.Status != SpPublicationStatus.Published).Select(File).ToList();

        // The latest job for each (kind, rule) decides whether something is still pending or has failed.
        var latest = jobs.GroupBy(j => (j.Kind, j.RuleId)).Select(g => g.First()).ToList();
        string? RuleName(SharePointJob j) => j.RuleId is { } id ? rules.FirstOrDefault(x => x.Id == id)?.Name : null;
        var pendingJob = latest.Where(j => j.Status == SpJobStatus.Pending).OrderBy(j => j.NextAttemptAt).FirstOrDefault();
        var failedJob = latest.Where(j => j.Status == SpJobStatus.Failed).OrderByDescending(j => j.CompletedAt).FirstOrDefault();

        string? waiting = null;
        if (current.Count == 0 && pendingJob == null && failedJob == null && inPhase.Count > 0)
        {
            var skipped = latest.FirstOrDefault(j => j.Status == SpJobStatus.Skipped);
            waiting = skipped?.Result ?? (ctx.Snapshot.Form.RevisionControlled && inPhase.All(x => x.EffectiveOnly) && !r.IsEffective
                ? "Not published: only the effective revision is published (SAF-043)."
                : "Not published yet. It's published the next time the record enters or is saved in this phase.");
        }
        var state = failedJob != null ? "failed" : pendingJob != null ? "pending" : current.Count > 0 ? "published" : history.Count > 0 ? "withdrawn" : "none";
        var tz = Engine.Records.RecordService.Tz(ctx.Me.Info?.TimeZone);
        return new SharePointPanelModel(state, current, history,
            pendingJob != null ? new SpPanelJob(pendingJob, RuleName(pendingJob)) : null,
            failedJob != null ? new SpPanelJob(failedJob, RuleName(failedJob)) : null,
            waiting, ctx.Me.IsAdmin, r.Id, tz);
    }
}
