using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

/// <summary>Header shown on every application-level designer page.</summary>
public sealed record DesignerHeaderVm(DesignTarget Target, string Active, int Changes, int Version);

/// <summary>
/// Base for designer pages. Every handler re-checks designer rights for the application on the server (DSN-001, INV-082);
/// the sidebar link and buttons are conveniences only.
/// </summary>
[Authorize]
public abstract class DesignerPageModel(DesignerStore store) : PageModel
{
    protected DesignerStore Store => store;

    [BindProperty(SupportsGet = true)] public string App { get; set; } = "";

    public DesignTarget T { get; protected set; } = null!;
    public List<ObjectChange> DraftChanges { get; protected set; } = [];
    public DesignerHeaderVm Header(string active) => new(T, active, DraftChanges.Count, store.Cfg.Version);

    /// <summary>Load the application (live + draft) and check designer rights. Returns an error result when not allowed.</summary>
    protected async Task<IActionResult?> OpenAsync()
    {
        var t = await store.OpenAsync(App);
        if (t == null) return store.Cfg.AppsByKey.ContainsKey(App) ? Forbid() : NotFound();
        T = t;
        if (t.HasDraft)
        {
            var doc = DesignDocs.Clone(t.Doc);
            DesignDocs.Reconcile(doc, t.LiveDoc);
            DraftChanges = DesignDiff.Compare(t.LiveDoc, doc);
        }
        return null;
    }

    protected async Task<T1?> ReadBodyAsync<T1>()
    {
        try { return await JsonSerializer.DeserializeAsync<T1>(Request.Body, DesignDocs.Json); }
        catch (JsonException) { return default; }
    }

    protected JsonResult Json(object? value, int status = 200) => new(value, DesignDocs.Json) { StatusCode = status };

    protected JsonResult Denied() => Json(new { ok = false, error = "You don't have designer rights for this application." }, 403);

    protected void Toast(string message, string kind = "ok")
    {
        TempData["Toast"] = message;
        TempData["ToastKind"] = kind;
    }

    protected string AppHref => $"/designer/apps/{App}";

    /// <summary>JSON for a <c>&lt;script type="application/json"&gt;</c> island (CSP-safe data hand-off to designer.js).</summary>
    public static string Island(object? value) => JsonSerializer.Serialize(value, DesignDocs.Json).Replace("</", "<\\/");
}
