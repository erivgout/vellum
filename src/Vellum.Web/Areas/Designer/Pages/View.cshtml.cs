using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Engine.Views;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

public sealed class ViewSaveRequest
{
    public ViewDoc? View { get; set; }
}

/// <summary>View designer (DSN-702..706): columns, filters, sort and paging, with a live preview against live data.</summary>
public class ViewDesignerModel(DesignerStore store, ViewService views, UserDirectory dir) : DesignerPageModel(store)
{
    [BindProperty(SupportsGet = true)] public string ViewKey { get; set; } = "";
    public ViewDoc V { get; private set; } = null!;
    public LockResult Lock { get; private set; } = new(true);
    public bool ReadOnly => !Lock.Ok;
    public string Component => $"view:{ViewKey}";
    public string IslandJson { get; private set; } = "{}";
    public string Base => $"{AppHref}/views/{ViewKey}";

    public async Task<IActionResult> OnGetAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        V = T.Doc.Views.FirstOrDefault(v => v.Key == ViewKey)!;
        if (V == null) return NotFound();
        Lock = await Store.AcquireAsync(T.AppKey, Component, $"view {V.Name}");
        var (_, _, issues) = await Store.ValidateAsync(T);
        var cfg = Store.Cfg;
        object FormInfo(FormDoc? d, FormDefinition? l, string app, bool mine)
        {
            if (d != null)
                return new
                {
                    key = d.Key, name = d.Name, app, mine, isActivity = d.IsActivity,
                    fields = d.MainFields.Where(x => x.Type != FieldType.Prompt).Select(x => new { key = x.Key, label = x.Label, type = x.Type.ToString(), list = x.KeywordList }),
                    phases = d.Workflows.SelectMany(w => w.Phases).Select(p => new { key = p.Key, name = p.Name, type = p.Type.ToString() }).DistinctBy(p => p.key),
                };
            var subs = l!.Sections.Where(s => s.IsSubform).Select(s => s.Key).ToHashSet();
            return new
            {
                key = l.Key, name = l.Name, app, mine, isActivity = l.IsActivity,
                fields = l.Fields.Where(x => x.Type != FieldType.Prompt && !subs.Contains(x.SectionKey)).Select(x => new { key = x.Key, label = x.Label, type = x.Type.ToString(), list = x.KeywordList }),
                phases = l.Workflows.SelectMany(w => w.Phases).Select(p => new { key = p.Key, name = p.Name, type = p.Type.ToString() }).DistinctBy(p => p.key),
            };
        }
        var forms = T.Doc.Forms.Select(f => FormInfo(f, null, T.Doc.Name, true))
            .Concat(cfg.FormsByKey.Values.Where(f => f.AppId != T.Doc.Id && !f.App.IsSystem).OrderBy(f => f.App.Order).ThenBy(f => f.Order).Select(f => FormInfo(null, f, f.App.Name, false)))
            .ToList();
        IslandJson = Island(new
        {
            app = T.AppKey,
            view = V,
            readOnly = ReadOnly,
            canPreview = T.Live != null,
            urls = new { save = $"{Base}?handler=Save", preview = $"{Base}?handler=Preview", heartbeat = $"{Base}?handler=Heartbeat", live = T.Live != null && V.Id != null ? $"/apps/{T.AppKey}?view={V.Key}" : null },
            forms,
            systemColumns = ViewService.SystemColumns.Select(kv => new { key = kv.Key, label = kv.Value, sortable = kv.Key is not ("assignees" or "status" or "location" or "form" or "creator") }),
            keywordLists = cfg.KeywordListsByKey.Values.ToDictionary(l => l.Key, l => l.Keywords.Where(k => !k.Disabled).Select(k => new { key = k.Key, label = k.Label, color = k.Color })),
            categories = T.Doc.Views.Select(v => v.Category).Distinct().OrderBy(c => c),
            statuses = new[] { ("open", "Open records"), ("completed", "Completed"), ("voided", "Voided"), ("closed", "Closed (completed or voided)"), ("all", "All (not archived)"), ("effective", "Effective revisions"), ("archived", "Archived") }
                .Select(s => new { key = s.Item1, label = s.Item2 }),
            issues = issues.Where(i => i.Path == $"view:{V.Key}").ToList(),
        });
        return Page();
    }

    /// <summary>Run the unsaved view against live data, security-trimmed to the designer (INV-081).</summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (await OpenAsync() is not null) return Denied();
        if (T.Live == null) return Json(new { ok = false, error = "Publish the application once to preview data." });
        var req = await ReadBodyAsync<ViewSaveRequest>();
        if (req?.View == null) return Json(new { ok = false, error = "The view couldn't be read." }, 400);
        var probe = new AppDoc { Views = [req.View] };
        DesignerStore.Sanitize(probe);
        var def = DesignDocs.ToDefinition(probe.Views[0], T.Live.Id);
        def.PageSize = Math.Clamp(def.PageSize, 5, 500);
        try
        {
            var result = await views.RunAsync(new ViewQuery { View = def, PageSize = 8 });
            return Json(new
            {
                ok = true, total = result.Total,
                columns = result.Columns.Select(c => new { key = c.Key, label = c.Label, numeric = c.Field?.Type is FieldType.Number or FieldType.Currency }),
                rows = result.Rows.Select(r => new
                {
                    id = r.Record.Id,
                    cells = result.Columns.Select(c => r.Cells.TryGetValue(c.Key, out var cell) ? new
                    {
                        text = cell.Kind is "users" ? string.Join(", ", cell.UserIds.Select(dir.Name)) : cell.Text, kind = cell.Kind, color = cell.Color,
                    } : null),
                }),
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return Json(new { ok = false, error = "The preview couldn't run this view yet: " + ex.Message });
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (await OpenAsync() is not null) return Denied();
        var req = await ReadBodyAsync<ViewSaveRequest>();
        var current = T.Doc.Views.FirstOrDefault(v => v.Key == ViewKey);
        if (req?.View == null || current == null) return Json(new { ok = false, error = "The view couldn't be read. Reload the page." }, 400);
        var lk = await Store.AcquireAsync(T.AppKey, Component, $"view {current.Name}");
        if (!lk.Ok) return Json(new { ok = false, error = $"{lk.HolderName} is editing this view, so your changes weren't saved (DSN-004)." }, 409);
        var incoming = req.View;
        incoming.Key = (incoming.Key ?? "").Trim();
        if (current.Id != null) incoming.Key = current.Key;
        else if (incoming.Key != current.Key)
        {
            if (DesignDocs.KeyProblem(incoming.Key) is { } kp) return Json(new { ok = false, error = $"The view's design name {kp}." }, 400);
            if (T.Doc.Views.Any(v => v != current && v.Key.Equals(incoming.Key, StringComparison.OrdinalIgnoreCase)))
                return Json(new { ok = false, error = $"Another view already uses the design name '{incoming.Key}'." }, 400);
        }
        await Store.MutateAsync(T.AppKey, d => DesignerOps.ReplaceView(d, ViewKey, incoming));
        var t = await Store.OpenAsync(T.AppKey);
        var (_, _, issues) = await Store.ValidateAsync(t!);
        var stored = t!.Doc.Views.First(v => v.Key == incoming.Key);
        return Json(new
        {
            ok = true, view = stored, issues = issues.Where(i => i.Path == $"view:{stored.Key}").ToList(),
            redirect = stored.Key != ViewKey ? $"{AppHref}/views/{stored.Key}" : null,
        });
    }

    public async Task<IActionResult> OnPostHeartbeatAsync()
    {
        if (await OpenAsync() is not null) return Denied();
        var name = T.Doc.Views.FirstOrDefault(v => v.Key == ViewKey)?.Name ?? ViewKey;
        var l = await Store.AcquireAsync(T.AppKey, Component, $"view {name}");
        return Json(new { ok = l.Ok, holder = l.HolderName });
    }
}
