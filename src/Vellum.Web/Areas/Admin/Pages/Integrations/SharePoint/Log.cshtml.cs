using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Engine.Security;
using Vellum.Modules.Admin;
using Vellum.Modules.Integrations.SharePoint;

namespace Vellum.Areas.Admin.Pages.Integrations.SharePoint;

/// <summary>Record identity shown in the log; null fields mean the record is restricted or deleted.</summary>
public sealed record SpRecordRef(Guid Id, string? Number, string? Revision, string? Title, bool Readable);

public sealed record SpJobRow(SharePointJob Job, SpRecordRef Record, string? RuleName, List<SharePointAttempt> Attempts);

public sealed record SpFileRow(SharePointPublication Pub, SpRecordRef Record, string Library, string Profile);

/// <summary>ADM-1005 / INT-306/307: the SharePoint queue (with retry) and the publication history across records.</summary>
public class LogModel(VellumDbContext db, AccessService access, SharePointAdminService admin) : AdminPageModel
{
    [FromQuery(Name = "view")] public string View { get; set; } = "queue";
    [FromQuery(Name = "status")] public string? Status { get; set; }
    [FromQuery(Name = "kind")] public string? Kind { get; set; }
    [FromQuery(Name = "q")] public string? Q { get; set; }
    [FromQuery(Name = "record")] public Guid? RecordFilter { get; set; }
    [FromQuery(Name = "p")] public int PageNo { get; set; } = 1;

    public Paged<SpJobRow>? Jobs { get; private set; }
    public Paged<SpFileRow>? Files { get; private set; }
    public int FailedCount { get; private set; }
    public int PendingCount { get; private set; }
    public SpRecordRef? FilteredRecord { get; private set; }

    public async Task OnGetAsync()
    {
        FailedCount = await db.Set<SharePointJob>().CountAsync(j => j.Status == SpJobStatus.Failed);
        PendingCount = await db.Set<SharePointJob>().CountAsync(j => j.Status == SpJobStatus.Pending);
        var matching = await MatchingRecordIdsAsync();
        if (View == "files") await LoadFilesAsync(matching);
        else await LoadJobsAsync(matching);
        if (RecordFilter is { } rf) FilteredRecord = (await RefsAsync([rf])).GetValueOrDefault(rf);
    }

    /// <summary>Records whose number matches the search, limited to what the administrator may read (INV-081).</summary>
    private async Task<List<Guid>?> MatchingRecordIdsAsync()
    {
        if (string.IsNullOrWhiteSpace(Q)) return null;
        var q = Q.Trim();
        return await access.Readable().Where(r => r.Number.Contains(q) || r.Title.Contains(q)).Select(r => r.Id).Take(500).ToListAsync();
    }

    private async Task<Dictionary<Guid, SpRecordRef>> RefsAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        var readable = await access.Readable().Where(r => list.Contains(r.Id)).Select(r => new { r.Id, r.Number, r.Revision, r.Title }).ToListAsync();
        return list.ToDictionary(id => id, id => readable.FirstOrDefault(r => r.Id == id) is { } r
            ? new SpRecordRef(id, r.Number, r.Revision, r.Title, true) : new SpRecordRef(id, null, null, null, false));
    }

    private async Task LoadJobsAsync(List<Guid>? matching)
    {
        var q = db.Set<SharePointJob>().AsNoTracking().AsQueryable();
        q = Status switch
        {
            "pending" => q.Where(j => j.Status == SpJobStatus.Pending),
            "retrying" => q.Where(j => j.Status == SpJobStatus.Pending && j.Attempts > 0),
            "failed" => q.Where(j => j.Status == SpJobStatus.Failed),
            "succeeded" => q.Where(j => j.Status == SpJobStatus.Succeeded),
            "skipped" => q.Where(j => j.Status == SpJobStatus.Skipped || j.Status == SpJobStatus.Cancelled),
            _ => q,
        };
        if (Kind == "publish") q = q.Where(j => j.Kind == SpJobKind.Publish);
        if (Kind == "withdraw") q = q.Where(j => j.Kind == SpJobKind.Withdraw);
        if (RecordFilter is { } rf) q = q.Where(j => j.RecordId == rf);
        if (matching != null) q = q.Where(j => matching.Contains(j.RecordId));
        var page = await q.OrderByDescending(j => j.Id).PageAsync(PageNo, 30);
        var ids = page.Items.Select(j => j.Id).ToList();
        var attempts = await db.Set<SharePointAttempt>().AsNoTracking().Where(a => ids.Contains(a.JobId)).OrderByDescending(a => a.Id).ToListAsync();
        var ruleIds = page.Items.Where(j => j.RuleId != null).Select(j => j.RuleId!.Value).Distinct().ToList();
        var rules = await db.Set<SharePointRule>().AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);
        var refs = await RefsAsync(page.Items.Select(j => j.RecordId));
        Jobs = new Paged<SpJobRow>(page.Items.Select(j => new SpJobRow(j, refs[j.RecordId], j.RuleId is { } r ? rules.GetValueOrDefault(r) : null,
            attempts.Where(a => a.JobId == j.Id).Take(10).ToList())).ToList(), page.Total, page.Page, page.PageSize);
    }

    private async Task LoadFilesAsync(List<Guid>? matching)
    {
        var q = db.Set<SharePointPublication>().AsNoTracking().AsQueryable();
        q = Status switch
        {
            "withdrawn" => q.Where(p => p.Status != SpPublicationStatus.Published),
            "all" => q,
            _ => q.Where(p => p.Status == SpPublicationStatus.Published),
        };
        if (RecordFilter is { } rf) q = q.Where(p => p.RecordId == rf);
        if (matching != null) q = q.Where(p => matching.Contains(p.RecordId));
        var page = await q.OrderByDescending(p => p.UpdatedAt ?? p.PublishedAt).ThenByDescending(p => p.Id).PageAsync(PageNo, 30);
        var libIds = page.Items.Select(p => p.LibraryId).Distinct().ToList();
        var libs = await db.Set<SharePointLibrary>().AsNoTracking().Include(l => l.Profile).Where(l => libIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var refs = await RefsAsync(page.Items.Select(p => p.RecordId));
        Files = new Paged<SpFileRow>(page.Items.Select(p => new SpFileRow(p, refs[p.RecordId], libs.GetValueOrDefault(p.LibraryId)?.Name ?? "Removed library",
            libs.GetValueOrDefault(p.LibraryId)?.Profile.Host ?? "")).ToList(), page.Total, page.Page, page.PageSize);
    }

    public const string Path = "/admin/integrations/sharepoint/log";

    /// <summary>Back to the same filters after a post (only a local query string is accepted).</summary>
    private static string Back(string? query) =>
        Path + (query is { Length: > 1 } q && q[0] == '?' && !q.Contains("//") && !q.Contains('\\') ? q : "");

    public async Task<IActionResult> OnPostRetryAsync(long id, string? back) => Done(await admin.RetryAsync(id), Back(back));
    public async Task<IActionResult> OnPostCancelAsync(long id, string? back) => Done(await admin.CancelAsync(id), Back(back));
    public async Task<IActionResult> OnPostRetryAllAsync(string? back) => Done(await admin.RetryAllFailedAsync(), Back(back));
    public async Task<IActionResult> OnPostProcessAsync(string? back) => Done(await admin.ProcessNowAsync(), Back(back));
}
