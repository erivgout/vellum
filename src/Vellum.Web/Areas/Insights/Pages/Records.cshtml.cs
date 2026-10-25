using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Insights;

namespace Vellum.Areas.Insights.Pages;

/// <summary>
/// Drill-through list (RPT-403): the records behind a KPI, a chart bucket or a dashboard mark. The filter is re-run for
/// the viewing user, so a shared link never shows records they can't open (INV-081).
/// </summary>
public class RecordsModel(InsightsQueries queries, ChartStore store, ChartEngine engine, AccessService access, ConfigCache cfg,
    VellumDbContext db, UserDirectory dir, AuditService audit) : InsightsPageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public int? Chart { get; set; }
    [BindProperty(SupportsGet = true)] public string? Def { get; set; }
    [BindProperty(SupportsGet = true, Name = "c")] public string? Cat { get; set; }
    [BindProperty(SupportsGet = true, Name = "s")] public string? Ser { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public string Heading { get; private set; } = "Records";
    public List<string> Chips { get; } = [];
    public List<Record> Rows { get; private set; } = [];
    public Dictionary<Guid, List<int>> Assignees { get; private set; } = [];
    public int Total { get; private set; }
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
    public string? BackHref { get; private set; }
    public string? BackLabel { get; private set; }
    public string? Error { get; private set; }

    private async Task<IQueryable<Record>?> QueryAsync()
    {
        if (Chart != null || Def != null)
        {
            ChartDefinition? def;
            string title;
            if (Chart is { } id)
            {
                var c = await store.GetAsync(id);
                if (c == null) { Error = "This chart doesn't exist or isn't shared with you."; return null; }
                def = ChartDefinition.FromJson(c.DefinitionJson);
                title = c.Title;
                BackHref = $"/insights/charts/{c.Id}";
                BackLabel = c.Title;
            }
            else
            {
                def = ChartDefinition.FromToken(Def);
                title = "Chart preview";
                if (def == null) { Error = "The chart link is incomplete."; return null; }
            }
            var (ids, label, issues) = await engine.DrillAsync(def, Cat, Ser);
            if (issues.Count > 0) { Error = issues[0].Message; return null; }
            Heading = string.IsNullOrEmpty(label) ? title : label;
            Chips.Add(title);
            if (cfg.Current.AppsByKey.TryGetValue(def.App, out var app))
                Chips.Add($"{app.Name} › {app.Views.FirstOrDefault(v => v.Key == def.View)?.Name ?? def.View}");
            Chips.Add(RelativeDates.Describe(def, BusinessCalendar.Today));
            return access.Readable().Where(r => ids.Contains(r.Id));
        }
        var f = RecordFilter.Parse(Request.Query);
        Heading = f.Title ?? "Records";
        Chips.AddRange(queries.Describe(f));
        if (f.App != null) { BackHref = $"/insights/apps/{f.App}"; BackLabel = cfg.Current.AppsByKey.GetValueOrDefault(f.App)?.Name + " dashboard"; }
        return queries.Apply(f);
    }

    private IOrderedQueryable<Record> Order(IQueryable<Record> q) =>
        Request.Query["st"] == "overdue" ? q.OrderBy(r => r.DueDate).ThenBy(r => r.Number) : q.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Number);

    public async Task<IActionResult> OnGetAsync()
    {
        var q = await QueryAsync();
        if (q == null) return Page();
        Total = await q.CountAsync();
        P = Math.Clamp(P, 1, Pages);
        Rows = await Order(q).Skip((P - 1) * PageSize).Take(PageSize).AsNoTracking().ToListAsync();
        var ids = Rows.Select(r => r.Id).ToList();
        Assignees = (await db.Assignments.AsNoTracking().Where(a => ids.Contains(a.RecordId) && a.Status == AssignmentStatus.Active).Select(a => new { a.RecordId, a.UserId }).ToListAsync())
            .GroupBy(a => a.RecordId).ToDictionary(g => g.Key, g => g.Select(a => a.UserId).Distinct().ToList());
        return Page();
    }

    /// <summary>CSV of the drill-through list (security-trimmed, audited).</summary>
    public async Task<IActionResult> OnGetCsvAsync()
    {
        var q = await QueryAsync();
        if (q == null) return NotFound();
        var rows = await Order(q).Take(10_000).AsNoTracking().ToListAsync();
        var ids = rows.Select(r => r.Id).ToList();
        var assigned = (await db.Assignments.AsNoTracking().Where(a => ids.Contains(a.RecordId) && a.Status == AssignmentStatus.Active).Select(a => new { a.RecordId, a.UserId }).ToListAsync())
            .GroupBy(a => a.RecordId).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(a => dir.Name(a.UserId)).Distinct()));
        static string C(string? s) => s == null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder("Number,Title,Application,Type,Phase,Due,Assigned to,Created\r\n");
        var c = cfg.Current;
        foreach (var r in rows)
            sb.Append(string.Join(',', C(r.Number), C(r.Title), C(c.AppsById[r.AppId].Name), C(c.FormsById[r.FormId].Name), C(c.Phase(r.PhaseId)?.Name),
                r.DueDate?.ToString("yyyy-MM-dd") ?? "", C(assigned.GetValueOrDefault(r.Id)), r.CreatedAt.ToString("yyyy-MM-dd"))).Append("\r\n");
        audit.Global("Records exported", $"Insights › {Heading} ({string.Join(" · ", Chips)}): {rows.Count} rows exported to CSV");
        await db.SaveChangesAsync();
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"insights-records-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    public string PageLink(int page)
    {
        var parts = Request.Query.Where(kv => kv.Key is not ("p" or "handler")).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value.ToString())}").ToList();
        if (page > 1) parts.Add($"p={page}");
        return "/insights/records?" + string.Join("&", parts);
    }

    public string CsvLink()
    {
        var parts = Request.Query.Where(kv => kv.Key is not ("p" or "handler")).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value.ToString())}").ToList();
        parts.Add("handler=Csv");
        return "/insights/records?" + string.Join("&", parts);
    }
}
