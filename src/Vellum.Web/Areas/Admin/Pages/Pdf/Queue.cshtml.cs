using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Pdf;

namespace Vellum.Areas.Admin.Pages.Pdf;

public sealed record PdfJobRow(PdfJob Job, Record? Record);

/// <summary>NFR-025 / PLT-807: the conversion queue with failures, retries and run times. Administrators can retry or process now.</summary>
public class QueueModel(VellumDbContext db, PdfQueue queue) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public List<PdfJobRow> Rows { get; private set; } = [];
    public Dictionary<string, int> Counts { get; } = [];
    public int Total { get; private set; }
    public const int PageSize = 50;

    public static readonly (string Key, string Label)[] Filters =
        [("all", "All"), ("pending", "Queued or converting"), ("failed", "Failed"), ("done", "Done"), ("cancelled", "Cancelled")];

    private IQueryable<PdfJob> Filter(IQueryable<PdfJob> q, string? status) => status switch
    {
        "pending" => q.Where(j => j.Status == PdfJobStatus.Queued || j.Status == PdfJobStatus.Converting),
        "failed" => q.Where(j => j.Status == PdfJobStatus.Failed),
        "done" => q.Where(j => j.Status == PdfJobStatus.Done),
        "cancelled" => q.Where(j => j.Status == PdfJobStatus.Cancelled),
        _ => q,
    };

    public async Task OnGetAsync()
    {
        var jobs = db.Set<PdfJob>().AsNoTracking();
        foreach (var (key, _) in Filters) Counts[key] = await Filter(jobs, key).CountAsync();
        var q = Filter(jobs, Status);
        Total = await q.CountAsync();
        P = Math.Max(1, P);
        var page = await q.OrderByDescending(j => j.Id).Skip((P - 1) * PageSize).Take(PageSize).ToListAsync();
        var ids = page.Select(j => j.RecordId).Distinct().ToList();
        var records = await db.Records.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        Rows = page.Select(j => new PdfJobRow(j, records.GetValueOrDefault(j.RecordId))).ToList();
    }

    public async Task<IActionResult> OnPostRetryAsync(long id)
    {
        var ok = await queue.RetryAsync(id);
        return Toast(ok ? "Queued again. The worker picks it up within a few seconds." : "That conversion can't be retried.", "/admin/pdf/queue?status=pending", ok ? "ok" : "bad");
    }

    public async Task<IActionResult> OnPostProcessAsync()
    {
        var n = await queue.ProcessDueAsync(10, HttpContext.RequestAborted);
        return Toast(n == 0 ? "No conversions were due." : $"Processed {n} conversion{(n == 1 ? "" : "s")}.", "/admin/pdf/queue");
    }
}
