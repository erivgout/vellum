using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>Works the SharePoint publishing queue every minute (NFR-025: retries with exponential backoff).</summary>
public sealed class SharePointPublishingJob(SharePointPublisher publisher) : IScheduledJob
{
    public const string JobKey = "sharepoint-publishing";
    public string Key => JobKey;
    public string Name => "SharePoint publishing";
    public string Description => "Publishes controlled files to SharePoint, withdraws superseded and obsolete ones, and retries failures with backoff (INT-304..308).";
    public int DefaultIntervalMinutes => 1;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var summary = await publisher.ProcessDueAsync(ct: ct);
        ctx.Log(summary.ToString());
        return new JobResult(summary.Succeeded, summary.ToString());
    }
}
