using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;

namespace Vellum.Engine.Search;

public sealed record SearchHit(Guid Id, string Number, string Title, string? Revision, int AppId, int FormId, int? PhaseId, PhaseType PhaseType,
    bool IsEffective, bool IsArchived, DateTime ModifiedAt, string? Snippet);

public sealed record SearchGroup(AppDefinition App, List<SearchHit> Hits, int Total);

/// <summary>
/// Global search (PLT §2.15). Fully security-trimmed (PLT-1504, INV-081): results come only from records the user can open,
/// and snippets never come from sensitive fields the user can't read (SEC-520).
/// </summary>
public sealed class SearchService(VellumDbContext db, AccessService access, ConfigCache cfg)
{
    public async Task<List<SearchGroup>> SearchAsync(string text, int? appId = null, int? days = null, bool includeArchive = false, int perApp = 100)
    {
        text = text.Trim();
        if (text.Length < 2) return [];
        var config = cfg.Current;
        var q = access.Readable();
        if (!includeArchive) q = q.Where(r => !r.IsArchived);
        if (appId is { } a) q = q.Where(r => r.AppId == a);
        if (days is { } d) q = q.Where(r => r.ModifiedAt >= DateTime.UtcNow.AddDays(-d));

        // Fields that may provide matches/snippets: text-like and not sensitive (unless the user may read sensitive data).
        var sensitive = config.FormsById.Values.SelectMany(f => f.Fields.Where(x => x.Sensitive && !access.CanSeeSensitive(f.AppId)).Select(x => x.Key)).Distinct().ToList();

        var hits = await q.Where(r => r.Number.Contains(text) || r.Title.Contains(text) ||
                db.RecordValues.Any(v => v.RecordId == r.Id && v.Text != null && v.Text.Contains(text) && !sensitive.Contains(v.FieldKey)) ||
                db.Comments.Any(c => c.RecordId == r.Id && c.Text.Contains(text)))
            .OrderByDescending(r => r.ModifiedAt)
            .Take(500)
            .Select(r => new SearchHit(r.Id, r.Number, r.Title, r.Revision, r.AppId, r.FormId, r.PhaseId, r.PhaseType, r.IsEffective, r.IsArchived, r.ModifiedAt,
                db.RecordValues.Where(v => v.RecordId == r.Id && v.Text != null && v.Text.Contains(text) && !sensitive.Contains(v.FieldKey)).Select(v => v.Text).FirstOrDefault()
                ?? db.Comments.Where(c => c.RecordId == r.Id && c.Text.Contains(text)).Select(c => c.Text).FirstOrDefault()))
            .ToListAsync();

        return hits.GroupBy(h => h.AppId)
            .Where(g => config.AppsById.ContainsKey(g.Key))
            .Select(g => new SearchGroup(config.AppsById[g.Key], g.Take(perApp).Select(h => h with { Snippet = Excerpt(h.Snippet, text) }).ToList(), g.Count()))
            .OrderByDescending(g => g.Total)
            .ToList();
    }

    public static string? Excerpt(string? s, string term)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var i = s.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return s.Length > 160 ? s[..160] + "…" : s;
        var start = Math.Max(0, i - 60);
        var len = Math.Min(s.Length - start, 160);
        return (start > 0 ? "…" : "") + s.Substring(start, len) + (start + len < s.Length ? "…" : "");
    }
}
