using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Modules.Insights.Charts;

namespace Vellum.Modules.Insights;

/// <summary>
/// Chart templates and documents (RPT-101). Documents belong to their owner. Templates are shared, carry location
/// security (visible at their location and below; no location = everyone) and can be disabled. Creating templates
/// needs designer rights on some application (or administrator).
/// </summary>
[ScopedService]
public sealed class ChartStore(VellumDbContext db, CurrentUser me, AccessService access, NumberingService numbers, ChartEngine engine)
{
    public IQueryable<InsightsChart> Charts => db.Set<InsightsChart>();

    public bool CanManageTemplates => access.IsAdmin || access.IsAnyDesigner;

    public Task EnsureAsync() => InsightsSchema.EnsureAsync(db);

    public async Task<List<InsightsChart>> MineAsync()
    {
        await EnsureAsync();
        return await Charts.AsNoTracking().Where(c => c.OwnerId == me.Id && !c.IsTemplate).OrderByDescending(c => c.Pinned).ThenByDescending(c => c.ModifiedAt).ToListAsync();
    }

    public async Task<List<InsightsChart>> TemplatesAsync()
    {
        await EnsureAsync();
        var all = await Charts.AsNoTracking().Where(c => c.IsTemplate).OrderBy(c => c.Title).ToListAsync();
        var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id);
        return all.Where(c => CanSeeTemplate(c, locations)).ToList();
    }

    public async Task<List<InsightsChart>> PinnedAsync()
    {
        await EnsureAsync();
        return await Charts.AsNoTracking().Where(c => c.OwnerId == me.Id && c.Pinned && !c.IsTemplate).OrderByDescending(c => c.ModifiedAt).Take(6).ToListAsync();
    }

    /// <summary>A chart the current user may open (owner, administrator, or a template visible at their location).</summary>
    public async Task<InsightsChart?> GetAsync(int id, bool tracking = false)
    {
        await EnsureAsync();
        var q = tracking ? Charts : Charts.AsNoTracking();
        var c = await q.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return null;
        if (c.OwnerId == me.Id || access.IsAdmin) return c;
        if (c.IsTemplate && !c.Disabled)
        {
            var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id);
            if (CanSeeTemplate(c, locations)) return c;
        }
        return null;
    }

    public bool CanEdit(InsightsChart c) => c.OwnerId == me.Id || access.IsAdmin || (c.IsTemplate && CanManageTemplates && c.OwnerId == me.Id);

    private bool CanSeeTemplate(InsightsChart c, Dictionary<int, Location> locations)
    {
        if (c.OwnerId == me.Id || access.IsAdmin) return true;
        if (c.Disabled) return false;
        if (c.LocationId is not { } lid || !locations.TryGetValue(lid, out var loc)) return true;
        if (me.Info?.LocationId is not { } mine || !locations.TryGetValue(mine, out var my)) return false;
        return my.Path == loc.Path || my.Path.StartsWith(loc.Path + " : ", StringComparison.Ordinal);
    }

    /// <summary>Validate (INV-124) and save. Assigns the number on first save.</summary>
    public async Task<(InsightsChart? Chart, List<ChartIssue> Issues)> SaveAsync(InsightsChart? existing, string title, string? description, ChartDefinition def,
        bool isTemplate, int? locationId, bool disabled = false)
    {
        await EnsureAsync();
        var issues = new List<ChartIssue>();
        if (string.IsNullOrWhiteSpace(title)) issues.Add(new ChartIssue("Title", "Give the chart a title."));
        if (isTemplate && !CanManageTemplates) issues.Add(new ChartIssue("IsTemplate", "Only designers and administrators can save chart templates."));
        // Full validation against the view's columns, as the user who saves it.
        var probe = await engine.EvaluateAsync(def, "probe", title);
        issues.AddRange(probe.Issues);
        if (issues.Count > 0) return (null, issues);

        var now = DateTime.UtcNow;
        var c = existing ?? new InsightsChart { OwnerId = me.Id, CreatedAt = now, IsTemplate = isTemplate };
        if (existing == null)
        {
            var n = await numbers.NextAsync("insights-chart");
            c.Number = (isTemplate ? "CTT-" : "CHT-") + n.ToString("D5");
            c.LocationId = locationId ?? (isTemplate ? me.Info?.LocationId : null);
            db.Set<InsightsChart>().Add(c);
        }
        else if (isTemplate) c.LocationId = locationId;
        c.Title = title.Trim();
        c.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        c.AppKey = def.App;
        c.ViewKey = def.View;
        c.Type = def.Type;
        c.DefinitionJson = def.ToJson();
        c.Disabled = isTemplate && disabled;
        c.ModifiedAt = now;
        await db.SaveChangesAsync();
        InsightsCache.Forget(me.Id, "chart|" + c.Id);
        return (c, []);
    }

    /// <summary>RPT-101: a chart document starts with the template's values and can then be edited.</summary>
    public async Task<InsightsChart> FromTemplateAsync(InsightsChart template)
    {
        var n = await numbers.NextAsync("insights-chart");
        var now = DateTime.UtcNow;
        var c = new InsightsChart
        {
            Number = "CHT-" + n.ToString("D5"), OwnerId = me.Id, TemplateId = template.Id, Title = template.Title, Description = template.Description,
            AppKey = template.AppKey, ViewKey = template.ViewKey, Type = template.Type, DefinitionJson = template.DefinitionJson,
            CreatedAt = now, ModifiedAt = now,
        };
        db.Set<InsightsChart>().Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    public async Task<InsightsChart> DuplicateAsync(InsightsChart source)
    {
        var n = await numbers.NextAsync("insights-chart");
        var now = DateTime.UtcNow;
        var c = new InsightsChart
        {
            Number = "CHT-" + n.ToString("D5"), OwnerId = me.Id, TemplateId = source.IsTemplate ? source.Id : source.TemplateId,
            Title = source.Title + " (copy)", Description = source.Description, AppKey = source.AppKey, ViewKey = source.ViewKey, Type = source.Type,
            DefinitionJson = source.DefinitionJson, CreatedAt = now, ModifiedAt = now,
        };
        db.Set<InsightsChart>().Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>RPT-405: pin to (or unpin from) the homepage, shown as chart, table or both.</summary>
    public async Task<bool> PinAsync(int id, bool pin, string? mode = null)
    {
        var c = await Charts.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == me.Id && !x.IsTemplate);
        if (c == null) return false;
        c.Pinned = pin;
        if (mode is "chart" or "table" or "both") c.PinMode = mode;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var c = await Charts.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null || (c.OwnerId != me.Id && !access.IsAdmin)) return false;
        db.Set<InsightsChart>().Remove(c);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Evaluate a saved chart for the current user, cached for up to five minutes (NFR-006). Drill-through is kept (RPT-403).</summary>
    public Task<(ChartEngine.Evaluation Value, DateTime At)> EvaluateAsync(InsightsChart c, bool refresh, int width = 760, int height = 300) =>
        InsightsCache.GetAsync(me.Id, $"chart|{c.Id}|{c.ModifiedAt.Ticks}|{width}", refresh, () =>
            engine.EvaluateAsync(ChartDefinition.FromJson(c.DefinitionJson), $"chart-{c.Id}", c.Title,
                (cat, ser) => $"/insights/records?chart={c.Id}&c={Uri.EscapeDataString(cat)}" + (ser != null ? $"&s={Uri.EscapeDataString(ser)}" : ""), width, height));
}

/// <summary>Sidebar section "Insights", visible to every signed-in user.</summary>
public sealed class InsightsNav(CurrentUser me) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        if (!me.IsAuthenticated || me.IsSystem) yield break;
        yield return new NavItem("Insights", "Dashboards", "/insights", "bi-speedometer2", 10, ActivePrefix: "/insights/apps");
        yield return new NavItem("Insights", "Charts", "/insights/charts", "bi-bar-chart-line", 20);
        yield return new NavItem("Insights", "Alerts", "/insights/alerts", "bi-bell", 30);
    }
}

public sealed record PinnedChart(InsightsChart Chart, ChartEngine.Evaluation Result, DateTime At);

/// <summary>RPT-405 / ADM-601: the user's pinned charts on the homepage.</summary>
public sealed class PinnedChartsWidget(ChartStore store, CurrentUser me) : IHomeWidget
{
    public string Key => "insights-pinned";
    public int Order => 40;
    public string Column => "main";

    public async Task<HomeWidgetModel?> BuildAsync()
    {
        if (!me.IsAuthenticated) return null;
        var pinned = await store.PinnedAsync();
        if (pinned.Count == 0) return null;
        var list = new List<PinnedChart>();
        foreach (var c in pinned)
        {
            var (r, at) = await store.EvaluateAsync(c, false, 420, 230);
            list.Add(new PinnedChart(c, r, at));
        }
        return new HomeWidgetModel("~/Modules/Insights/Views/_PinnedCharts.cshtml", list);
    }
}
