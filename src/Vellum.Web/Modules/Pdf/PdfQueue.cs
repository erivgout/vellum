using System.Diagnostics;
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

namespace Vellum.Modules.Pdf;

/// <summary>
/// The rendition queue (NFR-005, NFR-025, PLT-804, PLT-807). Jobs are processed by <see cref="PdfWorker"/> (or the
/// scheduled task as a fallback); PDFs, images, text and already-converted files can also be processed inline, inside
/// the routing transaction, because they don't need LibreOffice. A job turns one original into one rendition
/// <see cref="Attachment"/>; conversions are cached by source checksum so unchanged files are only re-stamped.
/// </summary>
[ScopedService]
public sealed class PdfQueue(
    VellumDbContext db, ConfigCache cfg, CurrentUser me, UserDirectory dir, PdfRuleStore rules, PdfConverter converter,
    FileStore files, AuditService audit, NotificationService notify, IConfiguration config, ILogger<PdfQueue> log, IServiceProvider services)
{
    private readonly PdfOptions _options = PdfOptions.From(config);

    public PdfOptions Options => _options;

    // ------------------------------------------------------------------ enqueue

    /// <summary>
    /// Queue renditions of the current originals of <paramref name="fieldKeys"/>, stamped for the record's current phase.
    /// Originals whose current rendition already carries the same watermark are skipped unless <paramref name="force"/>.
    /// Pending jobs for the same original with another watermark are cancelled (superseded by this phase).
    /// </summary>
    public async Task<List<PdfJob>> EnqueueAsync(RecordSnapshot snap, IEnumerable<string> fieldKeys, string trigger, bool force = false)
    {
        var formRules = await rules.ForFormAsync(snap.Form.Key);
        var fields = fieldKeys.Distinct().ToList();
        var created = new List<PdfJob>();
        if (fields.Count == 0 || snap.Record.IsArchived && trigger != "demo") return created;
        var atts = await db.Attachments.Where(a => a.RecordId == snap.Record.Id && !a.Removed && fields.Contains(a.FieldKey)).ToListAsync();
        var watermark = WatermarkFor(snap, formRules);
        var ids = atts.Select(a => a.Id).ToList();
        var jobs = await db.Set<PdfJob>().Where(j => ids.Contains(j.AttachmentId)).OrderByDescending(j => j.Id).ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var field in fields)
            foreach (var o in RenditionService.OriginalsOf(atts, field))
            {
                var mine = jobs.Where(j => j.AttachmentId == o.Id).ToList();
                var current = RenditionService.CurrentOf(atts, o);
                var currentJob = current == null ? null : mine.FirstOrDefault(j => j.RenditionId == current.Id);
                if (!force && current != null && (currentJob?.Watermark ?? null) == watermark) continue;
                var pending = mine.Where(j => j.Status is PdfJobStatus.Queued or PdfJobStatus.Converting).ToList();
                if (!force && pending.Any(j => j.Watermark == watermark)) continue;
                foreach (var p in pending.Where(j => j.Status == PdfJobStatus.Queued))
                {
                    p.Status = PdfJobStatus.Cancelled;
                    p.CompletedAt = now;
                    p.LastError = "Superseded by a newer conversion request.";
                }
                var job = new PdfJob
                {
                    AttachmentId = o.Id, RecordId = o.RecordId, FieldKey = o.FieldKey, RowId = o.RowId, FileName = o.FileName, SourceSha = o.Sha256,
                    PhaseKey = snap.Phase?.Key, Watermark = watermark, Trigger = trigger, Status = PdfJobStatus.Queued,
                    NextAttemptAt = now, CreatedAt = now, RequestedById = me.IsSystem ? null : me.Id,
                };
                db.Set<PdfJob>().Add(job);
                created.Add(job);
            }
        if (created.Count > 0)
            audit.Record(snap.Record.Id, "PDF conversion queued",
                $"{string.Join(", ", created.Select(j => j.FileName))} queued for PDF rendition ({trigger}{(watermark != null ? $"; watermark \"{watermark}\"" : "")})",
                phaseName: snap.Phase?.Name);
        return created;
    }

    /// <summary>The phase watermark, tokens resolved (DOC-022).</summary>
    public string? WatermarkFor(RecordSnapshot snap, PdfFormRules? formRules)
    {
        var template = formRules?.Phase(snap.Phase?.Key)?.Watermark;
        if (string.IsNullOrWhiteSpace(template)) return null;
        var org = db.Settings.AsNoTracking().Select(s => s.OrganizationName).FirstOrDefault() ?? "";
        var text = PdfRuleStore.ResolveTokens(template, snap, dir, org, TimeZoneOf(snap.Record));
        return text.Length > 400 ? text[..400] : text;
    }

    private TimeZoneInfo TimeZoneOf(Record r)
    {
        var tz = r.LocationId is { } id ? db.Locations.AsNoTracking().Where(l => l.Id == id).Select(l => l.TimeZone).FirstOrDefault() : null;
        return RecordService.Tz(tz);
    }

    /// <summary>True when the job needs no LibreOffice: PDF, image, text or a cached conversion (fast enough to run inline).</summary>
    public async Task<bool> IsFastAsync(PdfJob job) =>
        PdfConverter.IsFast(job.FileName, null, []) || await db.Set<PdfConversion>().AnyAsync(c => c.SourceSha == job.SourceSha);

    /// <summary>
    /// Process freshly queued jobs now, in the caller's transaction, when they don't need LibreOffice. Others stay
    /// queued for the worker. Never throws: a failure is recorded on the job (and retried when transient).
    /// </summary>
    public async Task ProcessInlineAsync(IEnumerable<PdfJob> jobs)
    {
        var list = jobs.ToList();
        if (list.Count == 0) return;
        await db.SaveChangesAsync();
        foreach (var job in list)
            if (job.Status == PdfJobStatus.Queued && await IsFastAsync(job))
                await RunAsync(job, inline: true);
    }

    // ------------------------------------------------------------------ process

    /// <summary>Recover stale leases, then process up to <paramref name="max"/> due jobs one by one. Returns how many ran.</summary>
    public async Task<int> ProcessDueAsync(int max = 10, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await db.Set<PdfJob>().Where(j => j.Status == PdfJobStatus.Converting && j.LeaseUntil < now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PdfJobStatus.Queued).SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
        var due = await DueIdsAsync(max, ct);
        var n = 0;
        foreach (var id in due)
        {
            if (ct.IsCancellationRequested) break;
            if (await ProcessAsync(id, ct)) n++;
        }
        return n;
    }

    public Task<List<long>> DueIdsAsync(int max, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return db.Set<PdfJob>().AsNoTracking().Where(j => j.Status == PdfJobStatus.Queued && j.NextAttemptAt <= now)
            .OrderBy(j => j.NextAttemptAt).ThenBy(j => j.Id).Select(j => j.Id).Take(max).ToListAsync(ct);
    }

    /// <summary>Claim one queued job (atomically, so two workers never run it twice) and process it.</summary>
    public async Task<bool> ProcessAsync(long jobId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var lease = now.AddSeconds(_options.TimeoutSeconds + 120);
        var claimed = await db.Set<PdfJob>().Where(j => j.Id == jobId && j.Status == PdfJobStatus.Queued && j.NextAttemptAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PdfJobStatus.Converting).SetProperty(j => j.LeaseUntil, lease), ct);
        if (claimed == 0) return false;
        db.ChangeTracker.Clear();
        var job = await db.Set<PdfJob>().FirstAsync(j => j.Id == jobId, ct);
        // No transaction around the conversion itself (it can take a while); the writes are atomic in RunAsync.
        await RunAsync(job, inline: false, ct);
        return true;
    }

    private async Task RunAsync(PdfJob job, bool inline, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // Entities added before this attempt (other work in the same transaction) are never discarded on failure.
        var preexisting = db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        job.Status = PdfJobStatus.Converting;
        job.Attempts++;
        job.StartedAt = DateTime.UtcNow;
        try
        {
            var original = await db.Attachments.FirstOrDefaultAsync(a => a.Id == job.AttachmentId, ct);
            var record = await db.Records.FirstOrDefaultAsync(r => r.Id == job.RecordId, ct);
            if (original == null || original.Removed || record == null)
            {
                job.Status = PdfJobStatus.Cancelled;
                job.LastError = "The original file was removed before it was converted.";
                job.CompletedAt = DateTime.UtcNow;
                job.LeaseUntil = null;
                await db.SaveChangesAsync(ct);
                return;
            }
            var form = cfg.Current.FormsById[record.FormId];
            var formRules = await rules.ForFormAsync(form.Key);

            // 1. Base PDF: from the cache when this exact file was converted before (PLT-804), otherwise convert.
            byte[] basePdf;
            var cache = await db.Set<PdfConversion>().FirstOrDefaultAsync(c => c.SourceSha == job.SourceSha, ct);
            if (cache != null)
            {
                basePdf = await files.ReadVerifiedAsync(cache.PdfSha, ct);
                job.Converter = cache.Converter + " (cached)";
            }
            else
            {
                var source = await files.ReadVerifiedAsync(original.Sha256, ct);
                var output = await converter.ConvertAsync(source, original.FileName, original.ContentType, ct);
                basePdf = output.Pdf;
                var (pdfSha, pdfSize) = await files.SaveAsync(new MemoryStream(basePdf), ct);
                db.Set<PdfConversion>().Add(new PdfConversion
                {
                    SourceSha = job.SourceSha, PdfSha = pdfSha, PdfSize = pdfSize, Converter = output.Converter, Pages = output.Pages,
                    DurationMs = output.DurationMs, ConvertedAt = DateTime.UtcNow,
                });
                job.Converter = output.Converter;
            }

            // 2. Stamp the phase watermark (DOC-022). DRAFT/SUPERSEDED marking is applied when shown (SAF-043).
            var pdf = job.Watermark is { Length: > 0 } wm
                ? PdfKit.Stamp(basePdf, new WatermarkSpec(wm, formRules?.Phase(job.PhaseKey)?.Position ?? WatermarkPosition.Diagonal,
                    formRules?.Profile.Color ?? "#b91c1c", formRules?.Profile.Opacity ?? 18))
                : basePdf;

            // 3. Store it as the rendition (contract: same record, field and row; RenditionOfId = original), atomically with the job.
            var (sha, size) = await files.SaveAsync(new MemoryStream(pdf), ct);
            await Tx.RunAsync(db, async () =>
            {
                var rendition = new Attachment
                {
                    RecordId = original.RecordId, RowId = original.RowId, FieldKey = original.FieldKey, FileName = SourceTypes.RenditionName(original.FileName),
                    ContentType = "application/pdf", Sha256 = sha, Size = size, UploadedById = dir.SystemUserId, UploadedAt = DateTime.UtcNow,
                    RenditionOfId = original.Id,
                };
                db.Attachments.Add(rendition);
                audit.Record(record.Id, "PDF rendition created",
                    $"{rendition.FileName} ({size:N0} bytes, SHA-256 {sha[..12]}…) derived from {original.FileName} (SHA-256 {original.Sha256[..12]}…) by {job.Converter}" +
                    (job.Watermark != null ? $"; watermark \"{job.Watermark}\"" : ""),
                    fieldKey: original.FieldKey, fieldLabel: form.Fields.FirstOrDefault(f => f.Key == original.FieldKey)?.Label, phaseName: cfg.Current.Phase(record.PhaseId)?.Name);
                await db.SaveChangesAsync(ct);
                job.RenditionId = rendition.Id;
                job.Status = PdfJobStatus.Done;
                job.LastError = null;
                job.Permanent = false;
                job.CompletedAt = DateTime.UtcNow;
                job.DurationMs = (int)sw.ElapsedMilliseconds;
                job.LeaseUntil = null;
                await db.SaveChangesAsync(ct);
                return true;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAsync(job, ex, inline, sw.ElapsedMilliseconds, preexisting);
        }
    }

    private async Task FailAsync(PdfJob job, Exception ex, bool inline, long elapsedMs, HashSet<object> preexisting)
    {
        var (message, permanent) = ex switch
        {
            PdfProcessingException p => (p.Message, p.Permanent),
            FileIntegrityException f => ($"The original failed its integrity check and wasn't converted (INV-053): {f.Message}", true),
            _ => ($"Unexpected conversion error: {ex.Message}", false),
        };
        if (ex is not PdfProcessingException) log.LogError(ex, "PDF conversion of job {Job} ({File}) failed", job.Id, job.FileName);
        else log.LogWarning("PDF conversion of job {Job} ({File}) failed: {Message}", job.Id, job.FileName, message);

        // Discard partial work of this attempt, keep the job (re-attached if a failed transaction cleared the tracker).
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added && !ReferenceEquals(e.Entity, job) && !preexisting.Contains(e.Entity)).ToList())
            e.State = EntityState.Detached;
        if (db.Entry(job).State == EntityState.Detached)
        {
            db.Attach(job);
            db.Entry(job).State = EntityState.Modified;
        }
        var final = permanent || job.Attempts >= _options.MaxAttempts;
        job.LastError = message.Length > 2000 ? message[..2000] : message;
        job.Permanent = permanent;
        job.DurationMs = (int)elapsedMs;
        job.LeaseUntil = null;
        if (final)
        {
            job.Status = PdfJobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            job.Status = PdfJobStatus.Queued;
            job.NextAttemptAt = DateTime.UtcNow.AddSeconds(BackoffSeconds(job.Attempts, _options.BackoffSeconds));
        }

        var record = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.RecordId);
        if (record != null)
        {
            audit.Record(record.Id, final ? "PDF conversion failed" : "PDF conversion retry scheduled",
                $"{job.FileName}: {message}" + (final ? "" : $" Attempt {job.Attempts} of {_options.MaxAttempts}; next try {job.NextAttemptAt:HH:mm:ss} UTC."),
                fieldKey: job.FieldKey);
            // PLT-807: the person who caused the conversion is told (once on the first failure, again when it gives up).
            var who = job.RequestedById ?? (await db.Attachments.AsNoTracking().Where(a => a.Id == job.AttachmentId).Select(a => (int?)a.UploadedById).FirstOrDefaultAsync());
            if (who is { } uid && (job.Attempts == 1 || final))
                notify.Notify(uid, NotificationKind.Info,
                    final ? $"PDF conversion failed: {job.FileName}" : $"PDF conversion is being retried: {job.FileName}",
                    $"{record.Number}: {message}" + (final ? " Routing into phases that require the PDF stays blocked until it's fixed." : " Vellum retries automatically."),
                    record.Id);
            if (final)
            {
                var admins = await db.Users.AsNoTracking().Where(u => u.IsAdministrator && u.Status == UserStatus.Active && !u.IsSystemIdentity).Select(u => u.Id).ToListAsync();
                foreach (var a in admins.Where(a => a != who))
                    notify.Notify(a, NotificationKind.Escalation, $"PDF conversion failed: {job.FileName}", $"{record.Number}: {message}", record.Id, "/admin/pdf/queue?status=failed");
            }
        }
        try { await db.SaveChangesAsync(); }
        catch (Exception saveEx) when (!inline)
        {
            log.LogError(saveEx, "Couldn't record the failure of PDF job {Job}", job.Id);
            throw;
        }
    }

    /// <summary>NFR-025: exponential backoff after the n-th failed attempt (30 s, 1, 2, 4… minutes), capped at an hour.</summary>
    public static double BackoffSeconds(int attempts, int baseSeconds) =>
        Math.Min(3600, baseSeconds * Math.Pow(2, Math.Max(0, attempts - 1)));

    // ------------------------------------------------------------------ retry and sweep

    /// <summary>Queue a failed or stuck job again now (manual retry from the record or the admin queue).</summary>
    public async Task<bool> RetryAsync(long jobId)
    {
        var job = await db.Set<PdfJob>().FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null || job.Status is PdfJobStatus.Done or PdfJobStatus.Converting) return false;
        job.Status = PdfJobStatus.Queued;
        job.Attempts = 0;
        job.Permanent = false;
        job.NextAttemptAt = DateTime.UtcNow;
        job.Trigger = "retry";
        job.RequestedById = me.IsSystem ? job.RequestedById : me.Id;
        audit.Record(job.RecordId, "PDF conversion retried", $"{job.FileName}: queued again by {me.Name}", fieldKey: job.FieldKey);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// PLT-804 "convert on save in the phase": queue originals uploaded since <paramref name="since"/> into records whose
    /// current phase converts their field. Returns the number of jobs queued.
    /// </summary>
    public async Task<int> SweepUploadsAsync(DateTime since, CancellationToken ct = default)
    {
        var all = await rules.AllAsync();
        var forms = all.Where(r => r.Enabled && r.Profile.Fields.Any(f => f.Convert)).ToDictionary(r => r.FormKey);
        if (forms.Count == 0) return 0;
        var formIds = forms.Keys.Where(cfg.Current.FormsByKey.ContainsKey).Select(k => cfg.Current.FormsByKey[k].Id).ToList();
        var fresh = await (from a in db.Attachments.AsNoTracking()
                           join r in db.Records.AsNoTracking() on a.RecordId equals r.Id
                           where a.UploadedAt >= since && !a.Removed && a.RenditionOfId == null && formIds.Contains(r.FormId) && !r.IsArchived
                                 && !db.Set<PdfJob>().Any(j => j.AttachmentId == a.Id)
                           select new { a.RecordId, a.FieldKey, r.FormId, r.PhaseId }).Distinct().ToListAsync(ct);
        var n = 0;
        foreach (var g in fresh.GroupBy(x => x.RecordId))
        {
            var first = g.First();
            var formRules = forms[cfg.Current.FormsById[first.FormId].Key];
            var phaseKey = cfg.Current.Phase(first.PhaseId)?.Key;
            var fields = g.Select(x => x.FieldKey).Where(f => formRules.Converts(f, phaseKey)).Distinct().ToList();
            if (fields.Count == 0) continue;
            var snap = await db.Records.AnyAsync(r => r.Id == g.Key, ct) ? await LoadAsync(g.Key) : null;
            if (snap == null) continue;
            var jobs = await EnqueueAsync(snap, fields, "save");
            n += jobs.Count;
        }
        if (n > 0) await db.SaveChangesAsync(ct);
        return n;
    }

    private Task<RecordSnapshot?> LoadAsync(Guid id) => services.GetRequiredService<RecordService>().LoadAsync(id);
}
