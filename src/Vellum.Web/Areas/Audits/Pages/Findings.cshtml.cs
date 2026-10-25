using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Audits;

namespace Vellum.Areas.Audits.Pages;

public sealed record FollowUpRef(Record Record, PhaseDefinition? Phase, string Kind);

public sealed record AuditFinding(Record Audit, string? Auditee, DateTime? AuditDate, int Number, string? Clause, string? Question, string? Comments,
    Keyword Classification, List<FollowUpRef> FollowUps);

/// <summary>AUD-032: findings across audits by clause, classification and auditee, plus overdue findings (follow-ups past due).</summary>
[Authorize]
public class FindingsModel(VellumDbContext db, AccessService access, ConfigCache cfg) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? C { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<AuditFinding> All { get; private set; } = [];
    public List<AuditFinding> Shown { get; private set; } = [];
    public List<(string Key, Dictionary<string, int> Counts)> ByClause { get; private set; } = [];
    public List<(string Key, Dictionary<string, int> Counts)> ByAuditee { get; private set; } = [];
    public List<(AuditFinding Finding, FollowUpRef Up)> Overdue { get; private set; } = [];
    public int AuditCount { get; private set; }
    public ConfigModel Config => cfg.Current;

    public static readonly string[] Columns = ["major_nc", "minor_nc", "observation", "ofi"];

    public async Task<IActionResult> OnGetAsync()
    {
        var c = cfg.Current;
        if (!c.AppsByKey.TryGetValue("audits", out var app) || !access.CanOpenApp(app.Id) || !c.FormsByKey.TryGetValue("audit", out var auditForm)) return NotFound();

        var audits = await access.Readable().AsNoTracking().Where(r => r.FormId == auditForm.Id && r.PhaseType != PhaseType.Voided).ToListAsync();
        AuditCount = audits.Count;
        var ids = audits.Select(a => a.Id).ToList();
        var rows = await db.SubformRows.AsNoTracking().Where(r => ids.Contains(r.RecordId) && r.SectionKey == "checklist").ToListAsync();
        string[] rowKeys = ["question", "clause", "classification", "comments"];
        var values = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId != null && rowKeys.Contains(v.FieldKey)).ToListAsync();
        string[] mainKeys = ["auditee", "start_at"];
        var main = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && mainKeys.Contains(v.FieldKey)).ToListAsync();

        // Follow-ups: CAPAs linked by finding row, recommendations carrying the finding row (both security-trimmed).
        var links = await db.Links.AsNoTracking().Where(l => ids.Contains(l.FromId) && l.Kind == LinkKind.Source && l.FieldKey != null && l.FieldKey.StartsWith("finding:")).ToListAsync();
        var recForm = c.FormsByKey.GetValueOrDefault("recommendation");
        var recs = recForm == null ? [] : await db.Records.AsNoTracking().Where(r => r.ParentId != null && ids.Contains(r.ParentId.Value) && r.FormId == recForm.Id && r.PhaseType != PhaseType.Voided).ToListAsync();
        var recIds = recs.Select(r => r.Id).ToList();
        var recRows = await db.RecordValues.AsNoTracking().Where(v => recIds.Contains(v.RecordId) && v.FieldKey == "finding_row" && v.RowId == null).ToListAsync();
        var upIds = links.Select(l => l.ToId).Concat(recIds).ToList();
        var readable = await access.Readable().AsNoTracking().Where(r => upIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var followByRow = new Dictionary<Guid, List<FollowUpRef>>();
        void Add(Guid row, Record r, string kind)
        {
            if (!followByRow.TryGetValue(row, out var list)) followByRow[row] = list = [];
            list.Add(new FollowUpRef(r, c.Phase(r.PhaseId), kind));
        }
        foreach (var l in links)
            if (Guid.TryParseExact(l.FieldKey!["finding:".Length..], "N", out var rid) && readable.TryGetValue(l.ToId, out var rec)) Add(rid, rec, "CAPA");
        foreach (var v in recRows)
            if (Guid.TryParse(v.Text, out var rid) && readable.TryGetValue(v.RecordId, out var rec)) Add(rid, rec, "Recommendation");

        var byAudit = audits.ToDictionary(a => a.Id);
        foreach (var row in rows.OrderBy(r => r.Order))
        {
            var rv = values.Where(v => v.RowId == row.Id).ToList();
            var cls = c.Keyword(rv.FirstOrDefault(v => v.FieldKey == "classification")?.RefId);
            if (cls == null || !AuditRules.FindingClasses.Contains(cls.Key)) continue;
            var audit = byAudit[row.RecordId];
            All.Add(new AuditFinding(audit,
                main.FirstOrDefault(v => v.RecordId == audit.Id && v.FieldKey == "auditee")?.Text,
                main.FirstOrDefault(v => v.RecordId == audit.Id && v.FieldKey == "start_at")?.Date,
                row.Order + 1, rv.FirstOrDefault(v => v.FieldKey == "clause")?.Text?.Trim(), rv.FirstOrDefault(v => v.FieldKey == "question")?.Text,
                rv.FirstOrDefault(v => v.FieldKey == "comments")?.Text, cls, followByRow.GetValueOrDefault(row.Id) ?? []));
        }
        All = All.OrderBy(f => Array.IndexOf(AuditRules.SeverityOrder, f.Classification.Key)).ThenByDescending(f => f.AuditDate).ThenBy(f => f.Number).ToList();

        Shown = All.Where(f => (string.IsNullOrEmpty(C) || f.Classification.Key == C) &&
            (string.IsNullOrWhiteSpace(Q) || $"{f.Clause} {f.Question} {f.Comments} {f.Auditee} {f.Audit.Number}".Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        ByClause = Group(Shown, f => f.Clause ?? "(no clause)").OrderBy(x => x.Item1, NaturalComparer.Instance).ToList();
        ByAuditee = Group(Shown, f => f.Auditee ?? "(no auditee)");
        var today = BusinessCalendar.Today;
        Overdue = All.SelectMany(f => f.FollowUps.Where(u => u.Record.PhaseType == PhaseType.Normal && u.Record.DueDate < today).Select(u => (f, u)))
            .OrderBy(x => x.u.Record.DueDate).ToList();
        return Page();
    }

    private static List<(string, Dictionary<string, int>)> Group(IEnumerable<AuditFinding> list, Func<AuditFinding, string> key) =>
        list.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, Columns.ToDictionary(c => c, c => g.Count(f => f.Classification.Key == c))))
            .OrderByDescending(x => x.Item2["major_nc"] * 100 + x.Item2["minor_nc"] * 10 + x.Item2.Values.Sum()).ThenBy(x => x.Key, NaturalComparer.Instance).ToList();

    public string Link(string? c = null, string? q = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(c)) parts.Add("c=" + Uri.EscapeDataString(c));
        var qq = q ?? Q;
        if (!string.IsNullOrWhiteSpace(qq)) parts.Add("q=" + Uri.EscapeDataString(qq));
        return "/audits/findings" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
    }
}

/// <summary>Numeric-aware ordering (e.g. §7.5.2 before §7.5.10, INS-006 style natural sort).</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var cmp = string.CompareOrdinal(a, b);
                if (cmp != 0) return cmp;
                continue;
            }
            var ci = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (ci != 0) return ci;
            i++; j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
