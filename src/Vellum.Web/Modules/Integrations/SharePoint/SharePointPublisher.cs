using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Files;

namespace Vellum.Modules.Integrations.SharePoint;

public sealed record SpOutcome(SpJobStatus Status, string Message)
{
    public static SpOutcome Done(string m) => new(SpJobStatus.Succeeded, m);
    public static SpOutcome Skip(string m) => new(SpJobStatus.Skipped, m);
}

public sealed record SpRunSummary(int Processed, int Succeeded, int Retrying, int Failed, int Skipped, int Reconciled)
{
    public override string ToString() =>
        $"Processed {Processed} job(s): {Succeeded} succeeded, {Skipped} skipped, {Retrying} will retry, {Failed} failed permanently." +
        (Reconciled > 0 ? $" Queued {Reconciled} withdrawal(s) of files whose records are no longer current." : "");
}

/// <summary>
/// Works the durable SharePoint queue (INT-304..308, NFR-025). Runs as the system identity, outside any record transaction,
/// so SharePoint being slow or down never affects viewing, editing or routing records. Transient failures retry with
/// exponential backoff; permanent ones notify the profile's accounts and stay in the log for an administrator to retry.
/// </summary>
[ScopedService]
public sealed class SharePointPublisher(
    VellumDbContext db, RecordService records, FileStore files, UserDirectory dir, AuditService audit, NotificationService notify,
    SharePointClientFactory factory, SharePointQueue queue, ConfigCache cfg, IConfiguration config, ILogger<SharePointPublisher> log)
{
    private DbSet<SharePointJob> Jobs => db.Set<SharePointJob>();
    private DbSet<SharePointPublication> Pubs => db.Set<SharePointPublication>();

    /// <summary>Exponential backoff: 1, 2, 4, 8… minutes (±15% jitter), capped at 6 hours, never sooner than Retry-After.</summary>
    public static TimeSpan Backoff(int attempts, TimeSpan? retryAfter = null, double? jitter = null)
    {
        var minutes = Math.Min(Math.Pow(2, Math.Max(0, attempts - 1)), 360);
        var delay = TimeSpan.FromMinutes(minutes * (jitter ?? 0.85 + Random.Shared.NextDouble() * 0.3));
        if (retryAfter is { } ra && ra > delay) delay = ra;
        return delay > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : delay;
    }

    private string RecordUrl(Guid id) => (config["Vellum:PublicUrl"]?.TrimEnd('/') ?? "") + $"/records/{id}";

    // ------------------------------------------------------------------ queue processing

    /// <summary>Process every due job (oldest first), after queueing withdrawals the reconciliation finds.</summary>
    public async Task<SpRunSummary> ProcessDueAsync(int max = 200, CancellationToken ct = default)
    {
        var reconciled = await ReconcileAsync(ct);
        var now = DateTime.UtcNow;
        var due = await Jobs.Where(j => j.Status == SpJobStatus.Pending && j.NextAttemptAt <= now).OrderBy(j => j.Id).Select(j => j.Id).Take(max).ToListAsync(ct);
        int ok = 0, retry = 0, failed = 0, skipped = 0;
        foreach (var id in due)
        {
            var status = await ProcessAsync(id, ct);
            switch (status)
            {
                case SpJobStatus.Succeeded: ok++; break;
                case SpJobStatus.Pending: retry++; break;
                case SpJobStatus.Failed: failed++; break;
                default: skipped++; break;
            }
        }
        return new SpRunSummary(due.Count, ok, retry, failed, skipped, reconciled);
    }

    /// <summary>Run one job now. Returns its new status (Pending = a retry is scheduled).</summary>
    public async Task<SpJobStatus> ProcessAsync(long jobId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        var job = await Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job == null || job.Status != SpJobStatus.Pending) return job?.Status ?? SpJobStatus.Cancelled;
        var sw = Stopwatch.StartNew();
        var now = DateTime.UtcNow;
        job.Attempts++;
        job.LastAttemptAt = now;
        await db.SaveChangesAsync(ct);

        SharePointException? error = null;
        SpOutcome? outcome = null;
        try
        {
            outcome = job.Kind == SpJobKind.Publish ? await PublishAsync(job, ct) : await WithdrawJobAsync(job, ct);
        }
        catch (SharePointException ex) { error = ex; }
        catch (FileIntegrityException ex) { error = new SharePointException(SpErrors.File, ex.Message, false, inner: ex); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(ex, "SharePoint job {Job} failed unexpectedly", jobId);
            error = new SharePointException(SpErrors.Unknown, ex.Message, true, inner: ex);
            db.ChangeTracker.Clear();
            job = await Jobs.FirstAsync(j => j.Id == jobId, ct);
        }

        now = DateTime.UtcNow;
        if (outcome != null)
        {
            job.Status = outcome.Status;
            job.Result = Clip(outcome.Message, 1000);
            job.ErrorCode = null;
            job.Error = null;
            job.CompletedAt = now;
        }
        else
        {
            job.ErrorCode = error!.Code;
            job.Error = Clip(error.Message, 2000);
            if (error.Retryable && job.Attempts < job.MaxAttempts)
            {
                job.Status = SpJobStatus.Pending;
                job.NextAttemptAt = now + Backoff(job.Attempts, error.RetryAfter);
            }
            else
            {
                job.Status = SpJobStatus.Failed;
                job.CompletedAt = now;
                await NotifyFailureAsync(job, error);
            }
        }
        db.Set<SharePointAttempt>().Add(new SharePointAttempt
        {
            JobId = job.Id, At = now, DurationMs = (int)sw.ElapsedMilliseconds, Success = outcome != null,
            ErrorCode = error?.Code, Message = Clip(outcome?.Message ?? error?.Message, 2000),
        });
        await db.SaveChangesAsync(ct);
        return job.Status;
    }

    private static string? Clip(string? s, int max) => s == null ? null : s.Length > max ? s[..(max - 1)] + "…" : s;

    /// <summary>INT-306: a permanent failure is emailed (and notified in-app) to the profile's accounts, or administrators.</summary>
    private async Task NotifyFailureAsync(SharePointJob job, SharePointException ex)
    {
        var rule = job.RuleId is { } rid ? await db.Set<SharePointRule>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid) : null;
        var profileId = rule?.ProfileId ?? await Pubs.Where(p => p.RecordId == job.RecordId).Select(p => (int?)p.ProfileId).FirstOrDefaultAsync();
        var profile = profileId is { } pid ? await db.Set<SharePointProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid) : null;
        var record = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.RecordId);
        var label = record != null ? record.Number + (record.Revision != null ? $" rev {record.Revision}" : "") : "a deleted record";
        var recipients = profile?.NotifyUserIds.Count > 0
            ? profile.NotifyUserIds
            : await db.Users.Where(u => u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity).Select(u => u.Id).ToListAsync();
        var what = job.Kind == SpJobKind.Publish ? "Publishing" : "Withdrawing the published files of";
        foreach (var uid in recipients.Distinct())
        {
            var admin = dir.Get(uid)?.IsAdmin == true;
            notify.Notify(uid, NotificationKind.Escalation, $"SharePoint {(job.Kind == SpJobKind.Publish ? "publishing" : "withdrawal")} failed: {label}",
                $"{what} {label} to {profile?.Name ?? "SharePoint"} failed after {job.Attempts} attempt(s) with {ex.Code}: {ex.Message} " +
                (admin ? "Retry it from Administration › Integrations › SharePoint › Publication log." : "An administrator can retry it once the cause is fixed."),
                record != null ? job.RecordId : null, admin ? $"/admin/integrations/sharepoint/log?status=failed" : null);
        }
        if (record != null)
            audit.Record(job.RecordId, job.Kind == SpJobKind.Publish ? "SharePoint publishing failed" : "SharePoint withdrawal failed",
                $"{ex.Code}: {Clip(ex.Message, 400)}", reason: job.Trigger);
    }

    // ------------------------------------------------------------------ publish (INT-304, INT-305, DOC-084)

    private sealed record FilePlan(Attachment Original, Attachment Source, string FileName, string PathKey);

    private async Task<SpOutcome> PublishAsync(SharePointJob job, CancellationToken ct)
    {
        var rule = job.RuleId is { } rid ? await db.Set<SharePointRule>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct) : null;
        if (rule is not { Enabled: true }) return SpOutcome.Skip("The publishing rule was deleted or disabled.");
        var profile = await db.Set<SharePointProfile>().AsNoTracking().Include(p => p.Libraries).FirstOrDefaultAsync(p => p.Id == rule.ProfileId, ct);
        if (profile is not { Enabled: true }) return SpOutcome.Skip("The SharePoint profile was deleted or disabled.");
        var lib = profile.Libraries.FirstOrDefault(l => l.Id == rule.LibraryId)
                  ?? throw new SharePointException(SpErrors.Config, $"Rule \"{rule.Name}\" points to a library that no longer exists.", false);

        var snap = await records.LoadAsync(job.RecordId);
        if (snap == null) return SpOutcome.Skip("The record no longer exists.");
        var r = snap.Record;
        if (snap.Form.Key != rule.FormKey) return SpOutcome.Skip("The rule is for another form.");
        if (rule.PhaseKey != "*" && snap.PhaseKey != rule.PhaseKey)
            return SpOutcome.Skip($"The record is in {snap.Phase?.Name ?? "no phase"}, not {PhaseName(snap, rule.PhaseKey)}; nothing was published.");
        if (r.IsArchived) return SpOutcome.Skip("The record is archived; archived content is never published (SAF-021).");
        if (snap.Form.RevisionControlled && rule.EffectiveOnly && !r.IsEffective)
            return SpOutcome.Skip($"Revision {r.Revision} isn't effective. Only the effective revision is published (SAF-043).");

        // Files: the PDF rendition when there is one, otherwise the original if the rule allows it.
        var plan = new List<FilePlan>();
        var folder = SharePointNaming.Folder(rule.FolderTemplate, snap, dir);
        foreach (var fieldKey in rule.FieldKeys)
        {
            foreach (var att in snap.Attachments.Where(a => a.FieldKey == fieldKey && a.RowId == null && !a.Removed && a.RenditionOfId == null).OrderBy(a => a.UploadedAt).ThenBy(a => a.Id))
            {
                var rendition = rule.PreferRendition ? await CurrentRenditionAsync(att.Id, ct) : null;
                var isPdf = att.ContentType == "application/pdf" || att.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                if (rendition == null && rule.PreferRendition && !isPdf && !rule.FallbackToOriginal)
                    throw new SharePointException(SpErrors.RenditionPending, $"{att.FileName} has no PDF rendition yet, and this rule doesn't publish originals. It will be retried.", true);
                var source = rendition ?? att;
                var ext = rendition != null ? ".pdf" : Path.GetExtension(att.FileName);
                var name = SharePointNaming.FitPath(folder, SharePointNaming.FileName(rule.FileNameTemplate, snap, dir, att.FileName, ext));
                plan.Add(new FilePlan(att, source, name, ""));
            }
        }
        if (plan.Count == 0) return SpOutcome.Skip($"No files to publish in {string.Join(", ", rule.FieldKeys.Select(k => snap.Field(k)?.Label ?? k))}.");
        plan = Disambiguate(plan, folder, lib.Id);

        // INV-163 / INT-305: decide, before touching SharePoint, whether every target may be written.
        var keys = plan.Select(p => p.PathKey).ToList();
        var atTargets = await Pubs.Where(p => p.LibraryId == lib.Id && p.Status == SpPublicationStatus.Published && keys.Contains(p.PathKey)).ToListAsync(ct);
        var holderIds = atTargets.Where(p => p.RecordId != r.Id).Select(p => p.RecordId).Distinct().ToList();
        var holders = await db.Records.AsNoTracking().Where(x => holderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var meCompleted = r.IsEffective || r.PhaseType == PhaseType.Completed;
        foreach (var other in atTargets.Where(p => p.RecordId != r.Id))
        {
            holders.TryGetValue(other.RecordId, out var h);
            var sameFamily = h != null && r.FamilyId != null && h.FamilyId == r.FamilyId;
            if (!sameFamily && h != null && !h.IsArchived)
                throw new SharePointException(SpErrors.Conflict, $"{other.FileName} in {lib.Name}/{other.FolderPath} is already published by {h.Number}. Change the folder or file name template so each record has its own file.", false);
            if (sameFamily && !h!.IsArchived && (h.IsEffective || h.PhaseType == PhaseType.Completed) && (!meCompleted || r.RevisionOrdinal < h.RevisionOrdinal))
                throw new SharePointException(SpErrors.NeverOverwrite,
                    $"{other.FileName} holds the completed revision {h.Revision}. Revision {r.Revision} is {(meCompleted ? "older" : "pending")}, so it can't overwrite it (DOC-084, INV-163).", false);
        }

        var client = factory.For(profile);
        var columns = await client.GetColumnsAsync(lib, ct);
        List<SharePointMappingEntry>? entries = null;
        if (rule.MappingId is { } mid)
        {
            var mapping = await db.Set<SharePointMapping>().AsNoTracking().Include(m => m.Entries).FirstOrDefaultAsync(m => m.Id == mid, ct)
                          ?? throw new SharePointException(SpErrors.Config, $"Rule \"{rule.Name}\" uses a mapping profile that no longer exists.", false);
            entries = mapping.Entries;
        }
        var labels = await records.LinkLabelsAsync(snap.Data.Main.Keys.SelectMany(k => snap.Data.Main[k]));

        int uploaded = 0, current = 0;
        var published = new List<SharePointPublication>();
        foreach (var f in plan)
        {
            var now = DateTime.UtcNow;
            var meta = SharePointMetadata.Build(snap, dir, entries, columns, new MetadataContext(f.FileName, now, RecordUrl(r.Id)), labels);
            if (meta.Problems.Count > 0) throw new SharePointException(SpErrors.Column, string.Join(" ", meta.Problems), false);
            var metaJson = JsonSerializer.Serialize(meta.Fields);
            var mine = atTargets.FirstOrDefault(p => p.RecordId == r.Id && p.PathKey == f.PathKey);
            if (mine != null && mine.Sha256 == f.Source.Sha256 && mine.MetadataJson == metaJson && mine.RuleId == rule.Id)
            {
                current++;
                published.Add(mine);
                continue;
            }
            var content = await files.ReadVerifiedAsync(f.Source.Sha256, ct);
            await client.EnsureFolderAsync(lib, folder, ct);
            var previous = mine ?? atTargets.FirstOrDefault(p => p.PathKey == f.PathKey && p.RecordId != r.Id);
            // INT-305 "replace": without version history, the old file is deleted so its versions go with it.
            if (!rule.KeepVersionHistory && previous?.RemoteId != null) await client.DeleteAsync(lib, previous.RemoteId, ct);
            var item = await client.UploadAsync(lib, folder, f.FileName, content, f.Source.ContentType, ct);
            if (meta.Fields.Count > 0) await client.SetFieldsAsync(lib, item, meta.Fields, ct);

            var pub = mine ?? new SharePointPublication { RecordId = r.Id, PublishedAt = now };
            pub.FamilyId = r.FamilyId;
            pub.Revision = r.Revision;
            pub.RevisionOrdinal = r.RevisionOrdinal;
            pub.RuleId = rule.Id;
            pub.ProfileId = profile.Id;
            pub.LibraryId = lib.Id;
            pub.FieldKey = f.Original.FieldKey;
            pub.AttachmentId = f.Source.Id;
            pub.OriginalAttachmentId = f.Source.Id != f.Original.Id ? f.Original.Id : null;
            pub.IsRendition = f.Source.Id != f.Original.Id;
            pub.Sha256 = f.Source.Sha256;
            pub.Size = f.Source.Size;
            pub.FolderPath = folder;
            pub.FileName = f.FileName;
            pub.PathKey = f.PathKey;
            pub.WebUrl = item.WebUrl;
            pub.RemoteId = item.Id;
            pub.MetadataJson = metaJson;
            pub.Status = SpPublicationStatus.Published;
            pub.JobId = job.Id;
            if (mine != null) { pub.UpdatedAt = now; db.Update(pub); }
            else Pubs.Add(pub);
            await db.SaveChangesAsync(ct);

            foreach (var replaced in atTargets.Where(p => p.PathKey == f.PathKey && p.RecordId != r.Id))
            {
                db.Attach(replaced);
                replaced.Status = SpPublicationStatus.Replaced;
                replaced.ReplacedById = pub.Id;
                replaced.WithdrawnAt = now;
                replaced.WithdrawnReason = $"Overwritten by revision {r.Revision}" + (rule.KeepVersionHistory ? " (kept in the library's version history)" : "");
                audit.Record(replaced.RecordId, "SharePoint file replaced", $"{replaced.FileName} in {lib.Name} now holds revision {r.Revision} of {r.Number}");
            }
            audit.Record(r.Id, "Published to SharePoint",
                $"{f.FileName}{(pub.IsRendition ? " (PDF rendition)" : "")} → {lib.Name}/{(folder.Length == 0 ? "" : folder + "/")} on {profile.Host}" +
                (r.Revision != null ? $", revision {r.Revision}" : ""), reason: job.Trigger, newValue: item.WebUrl ?? item.Path);
            await db.SaveChangesAsync(ct);
            published.Add(pub);
            uploaded++;
        }

        // Files of this record and rule that aren't produced any more (attachment removed or renamed) are withdrawn too,
        // so the library only shows the current content.
        var keep = published.Select(p => p.Id).ToHashSet();
        var stale = await Pubs.Where(p => p.RecordId == r.Id && p.RuleId == rule.Id && p.Status == SpPublicationStatus.Published).ToListAsync(ct);
        var withdrawn = 0;
        foreach (var p in stale.Where(p => !keep.Contains(p.Id)))
        {
            await WithdrawOneAsync(p, "No longer part of the record's published content", "Withdrawn", ct);
            withdrawn++;
        }
        // INT-305 / SAF-021: a new effective revision withdraws its predecessors' files.
        if (r.FamilyId != null && meCompleted)
        {
            var older = await Pubs.Where(p => p.FamilyId == r.FamilyId && p.RecordId != r.Id && p.RevisionOrdinal < r.RevisionOrdinal && p.Status == SpPublicationStatus.Published).ToListAsync(ct);
            foreach (var p in older)
            {
                await WithdrawOneAsync(p, $"Superseded by revision {r.Revision}", "Superseded", ct);
                withdrawn++;
            }
        }
        var msg = $"Published {uploaded} file(s) to {lib.Name}" + (folder.Length > 0 ? $"/{folder}" : "") + (current > 0 ? $"; {current} already up to date" : "") +
                  (withdrawn > 0 ? $"; withdrew {withdrawn} older file(s)" : "") + ".";
        return SpOutcome.Done(msg);
    }

    private static string PhaseName(RecordSnapshot snap, string key) =>
        snap.Workflow?.Phases.FirstOrDefault(p => p.Key == key)?.Name ?? key;

    /// <summary>Two attachments that end up with the same name get " (2)", " (3)"… so neither overwrites the other.</summary>
    private static List<FilePlan> Disambiguate(List<FilePlan> plan, string folder, int libraryId)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FilePlan>();
        foreach (var p in plan)
        {
            var name = p.FileName;
            var ext = Path.GetExtension(name);
            var stem = name[..^ext.Length];
            for (var i = 2; !used.Add(name); i++) name = $"{stem} ({i}){ext}";
            result.Add(p with { FileName = name, PathKey = SharePointNaming.PathKey(libraryId, folder, name) });
        }
        return result;
    }

    /// <summary>The rendition contract: an attachment whose RenditionOfId is the original, PDF, latest non-removed one wins.</summary>
    public Task<Attachment?> CurrentRenditionAsync(long originalId, CancellationToken ct = default) =>
        db.Attachments.AsNoTracking()
            .Where(a => a.RenditionOfId == originalId && !a.Removed && a.ContentType == "application/pdf")
            .OrderByDescending(a => a.UploadedAt).ThenByDescending(a => a.Id).FirstOrDefaultAsync(ct);

    // ------------------------------------------------------------------ withdraw (INT-308, SAF-021)

    private async Task<SpOutcome> WithdrawJobAsync(SharePointJob job, CancellationToken ct)
    {
        var pubs = await Pubs.Where(p => p.RecordId == job.RecordId && p.Status == SpPublicationStatus.Published).OrderBy(p => p.Id).ToListAsync(ct);
        if (pubs.Count == 0) return SpOutcome.Done("Nothing to withdraw: the record has no current SharePoint files.");
        var record = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.RecordId, ct);
        var status = record == null ? "Withdrawn" : SharePointNaming.Status(record, null, cfg.Current.FormsById[record.FormId]);
        var counts = new Dictionary<SpPublicationStatus, int>();
        foreach (var p in pubs)
        {
            var result = await WithdrawOneAsync(p, job.Trigger, status, ct);
            counts[result] = counts.GetValueOrDefault(result) + 1;
        }
        return SpOutcome.Done("Withdrew " + string.Join(", ", counts.Select(kv => $"{kv.Value} file(s) {Verb(kv.Key)}")) + ".");
    }

    private static string Verb(SpPublicationStatus s) => s switch
    {
        SpPublicationStatus.Removed => "removed",
        SpPublicationStatus.MarkedObsolete => "marked obsolete",
        SpPublicationStatus.Replaced => "already replaced by a newer revision",
        _ => "left in place",
    };

    /// <summary>Remove or mark obsolete one published file (per its rule), unless a newer publication already overwrote it.</summary>
    private async Task<SpPublicationStatus> WithdrawOneAsync(SharePointPublication p, string reason, string statusLabel, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var newer = await Pubs.AsNoTracking().Where(x => x.Id != p.Id && x.PathKey == p.PathKey && x.Status == SpPublicationStatus.Published).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (newer != null)
        {
            p.Status = SpPublicationStatus.Replaced;
            p.ReplacedById = newer.Id;
            p.WithdrawnAt = now;
            p.WithdrawnReason = Clip(reason, 500);
            await db.SaveChangesAsync(ct);
            return p.Status;
        }
        var rule = await db.Set<SharePointRule>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == p.RuleId, ct);
        var profile = await db.Set<SharePointProfile>().AsNoTracking().Include(x => x.Libraries).FirstOrDefaultAsync(x => x.Id == p.ProfileId, ct);
        var lib = profile?.Libraries.FirstOrDefault(l => l.Id == p.LibraryId);
        if (profile == null || lib == null)
            throw new SharePointException(SpErrors.Config, $"{p.FileName} was published through a profile or library that no longer exists; remove it from SharePoint by hand.", false);
        var client = factory.For(profile);
        var action = rule?.WithdrawAction ?? SpWithdrawAction.Delete;
        string detail;
        if (action == SpWithdrawAction.Delete || p.RemoteId == null)
        {
            var existed = p.RemoteId != null && await client.DeleteAsync(lib, p.RemoteId, ct);
            p.Status = SpPublicationStatus.Removed;
            detail = existed ? $"{p.FileName} deleted from {lib.Name}" : $"{p.FileName} was already gone from {lib.Name}";
        }
        else
        {
            var folder = string.IsNullOrWhiteSpace(rule!.ObsoleteFolder) ? p.FolderPath : SharePointNaming.NormalizeFolder(rule.ObsoleteFolder);
            var ext = Path.GetExtension(p.FileName);
            var stem = p.FileName[..^ext.Length];
            var newName = SharePointNaming.FitPath(folder, SharePointNaming.SanitizeName(
                $"{stem} ({(p.Revision != null ? "rev " + p.Revision + " " : "")}{statusLabel.ToLowerInvariant()}){ext}"));
            var moved = await client.MoveAsync(lib, p.RemoteId, folder, newName, ct);
            if (moved == null)
            {
                p.Status = SpPublicationStatus.Removed;
                detail = $"{p.FileName} was already gone from {lib.Name}";
            }
            else
            {
                p.Status = SpPublicationStatus.MarkedObsolete;
                p.RemoteId = moved.Id;
                p.WebUrl = moved.WebUrl ?? p.WebUrl;
                p.FolderPath = folder;
                p.FileName = newName;
                p.PathKey = SharePointNaming.PathKey(lib.Id, folder, newName);
                p.WithdrawnAt = now;
                p.WithdrawnReason = Clip(reason, 500);
                await db.SaveChangesAsync(ct);
                // Re-stamp the status columns so library views filtered on status drop the file.
                var snap = await records.LoadAsync(p.RecordId);
                if (snap != null)
                {
                    var entries = rule.MappingId is { } mid ? (await db.Set<SharePointMapping>().AsNoTracking().Include(m => m.Entries).FirstOrDefaultAsync(m => m.Id == mid, ct))?.Entries : null;
                    var columns = await client.GetColumnsAsync(lib, ct);
                    var meta = SharePointMetadata.Build(snap, dir, entries, columns, new MetadataContext(newName, now, RecordUrl(p.RecordId), statusLabel));
                    if (meta.Problems.Count == 0 && meta.Fields.Count > 0)
                    {
                        await client.SetFieldsAsync(lib, moved, meta.Fields, ct);
                        p.MetadataJson = JsonSerializer.Serialize(meta.Fields);
                    }
                }
                detail = $"{p.FileName} moved to {lib.Name}/{folder} and marked {statusLabel.ToLowerInvariant()}";
            }
        }
        p.WithdrawnAt = now;
        p.WithdrawnReason = Clip(reason, 500);
        if (await db.Records.AnyAsync(r => r.Id == p.RecordId, ct))
            audit.Record(p.RecordId, "Withdrawn from SharePoint", detail + $" (INT-308)", reason: Clip(reason, 400));
        await db.SaveChangesAsync(ct);
        return p.Status;
    }

    // ------------------------------------------------------------------ reconciliation (SAF-021 durable follow-up)

    /// <summary>
    /// Safety net for SAF-021: any file still marked published whose record is archived, voided, obsolete or deleted gets a
    /// withdrawal queued, even if the event that should have queued it was missed.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var stale = await Pubs.AsNoTracking().Where(p => p.Status == SpPublicationStatus.Published &&
                (!db.Records.Any(r => r.Id == p.RecordId) ||
                 db.Records.Any(r => r.Id == p.RecordId && (r.IsArchived || r.PhaseType == PhaseType.Voided || r.PhaseType == PhaseType.Obsolete))))
            .Select(p => p.RecordId).Distinct().ToListAsync(ct);
        var pending = await Jobs.Where(j => j.Kind == SpJobKind.Withdraw && j.Status == SpJobStatus.Pending && stale.Contains(j.RecordId)).Select(j => j.RecordId).ToListAsync(ct);
        var queued = 0;
        foreach (var id in stale.Except(pending))
        {
            await queue.WithdrawAsync(id, "Reconciliation: the record is no longer current");
            queued++;
        }
        if (queued > 0) await db.SaveChangesAsync(ct);
        return queued;
    }
}
