using System.Text.Json;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Engine.Views;
using Vellum.Infrastructure;

namespace Vellum.Modules.Insights;

/// <summary>
/// What the chart builder and alert editor may offer the current user: readable applications, their visible views,
/// and the chartable columns of each view's forms. Rendered server-side and as a JSON island for the form script.
/// </summary>
[ScopedService]
public sealed class BuilderCatalog(ConfigCache cfg, AccessService access, ViewService views)
{
    public sealed record ViewItem(string Key, string Name, string Category, List<string> Forms);
    public sealed record AppItem(string Key, string Name, List<ViewItem> Views);

    private List<AppItem>? _apps;

    public List<AppItem> Apps() => _apps ??= cfg.Current.Apps
        .Where(a => !a.IsSystem && !a.Disabled && access.CanOpenApp(a.Id))
        .Select(a => new AppItem(a.Key, a.Name, a.Views.Where(v => !v.Hidden)
            .Select(v => new ViewItem(v.Key, v.Name, System.Text.RegularExpressions.Regex.Replace(v.Category, @"^\d+[\.\)]\s*", ""), views.FormsOf(v).Select(f => f.Key).ToList()))
            .Where(v => v.Forms.Count > 0).ToList()))
        .Where(a => a.Views.Count > 0).ToList();

    public List<ChartColumn> Columns(string? app, string? view)
    {
        if (app == null || !cfg.Current.AppsByKey.TryGetValue(app, out var a) || !access.CanOpenApp(a.Id)) return [];
        var v = a.Views.FirstOrDefault(x => x.Key == view);
        return v == null ? [] : ChartColumns.For(views, v).Where(c => c.Field is not { Sensitive: true } || access.CanSeeSensitive(a.Id)).ToList();
    }

    /// <summary>JSON island: { apps, forms: { key: [ {key,label,kind} ] }, system: [...] }.</summary>
    public string Json()
    {
        var apps = Apps();
        var formKeys = apps.SelectMany(a => a.Views).SelectMany(v => v.Forms).Distinct().ToList();
        var forms = formKeys.Where(k => cfg.Current.FormsByKey.ContainsKey(k)).ToDictionary(k => k, k =>
        {
            var f = cfg.Current.FormsByKey[k];
            var canSensitive = access.CanSeeSensitive(f.AppId);
            return ChartColumns.For([f]).Where(c => c.Field != null && (!c.Field.Sensitive || canSensitive))
                .Select(c => new { key = c.Key, label = c.Label, kind = c.Kind }).ToList();
        });
        var system = ChartColumns.For([]).Select(c => new { key = c.Key, label = c.Label, kind = c.Kind });
        return JsonSerializer.Serialize(new
        {
            apps = apps.Select(a => new { key = a.Key, name = a.Name, views = a.Views.Select(v => new { key = v.Key, name = v.Name, category = v.Category, forms = v.Forms }) }),
            forms, system,
        });
    }

    public static string KindLabel(string kind) => kind switch
    {
        "date" => "date", "number" => "number", "keyword" or "keywords" => "keyword", "user" or "users" => "person", "bool" => "yes/no", _ => kind,
    };
}
