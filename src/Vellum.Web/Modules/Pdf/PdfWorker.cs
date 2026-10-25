using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Pdf;

/// <summary>
/// Background worker of the rendition queue (NFR-005): polls every few seconds, picks up new uploads in converting
/// phases (PLT-804) and runs due jobs, up to <c>Vellum:Pdf:MaxParallel</c> at a time, each in its own scope as the
/// system identity (SEC-806). Disabled with <c>Vellum:Pdf:Worker=false</c> or when the scheduler is disabled (tests).
/// </summary>
[BackgroundWorker]
public sealed class PdfWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<PdfWorker> log) : BackgroundService
{
    private DateTime _sweptUntil = DateTime.UtcNow.AddDays(-1);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var options = PdfOptions.From(config);
        if (!options.Worker) return;
        await Task.Delay(TimeSpan.FromSeconds(8), stop);
        while (!stop.IsCancellationRequested)
        {
            try { await TickAsync(options, stop); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "PDF worker tick failed"); }
            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stop);
        }
    }

    private async Task TickAsync(PdfOptions options, CancellationToken ct)
    {
        List<long> due;
        using (var scope = scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            if (!await sp.GetRequiredService<VellumDbContext>().Database.CanConnectAsync(ct)) return;
            sp.GetRequiredService<CurrentUser>().UseSystem("PDF renditions");
            var queue = sp.GetRequiredService<PdfQueue>();
            var now = DateTime.UtcNow;
            await queue.SweepUploadsAsync(_sweptUntil.AddSeconds(-30), ct);
            _sweptUntil = now;
            await sp.GetRequiredService<VellumDbContext>().Set<PdfJob>()
                .Where(j => j.Status == PdfJobStatus.Converting && j.LeaseUntil < now)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PdfJobStatus.Queued).SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
            due = await queue.DueIdsAsync(options.MaxParallel * 4, ct);
        }
        if (due.Count == 0) return;
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallel, CancellationToken = ct }, async (id, token) =>
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("PDF renditions");
            try { await scope.ServiceProvider.GetRequiredService<PdfQueue>().ProcessAsync(id, token); }
            catch (Exception ex) when (!token.IsCancellationRequested) { log.LogError(ex, "PDF job {Job} failed", id); }
        });
    }
}

/// <summary>
/// Scheduled fallback for the rendition queue (ADM §5.8): recovers stale jobs, queues uploads that the worker missed
/// and processes due jobs, with run history in Administration › Scheduled tasks.
/// </summary>
public sealed class PdfQueueJob(PdfQueue queue) : IScheduledJob
{
    public string Key => "pdf-renditions";
    public string Name => "PDF rendition queue";
    public string Description => "Converts attachments to PDF renditions, retries failed conversions with backoff and recovers stalled ones (PLT-804, NFR-025).";
    public int DefaultIntervalMinutes => 5;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var swept = await queue.SweepUploadsAsync(DateTime.UtcNow.AddHours(-6), ct);
        if (swept > 0) ctx.Log($"Queued {swept} new upload(s) for conversion.");
        var done = await queue.ProcessDueAsync(25, ct);
        return new JobResult(done, done == 0 ? "No conversions were due." : $"Processed {done} conversion job(s).");
    }
}
