using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Engine.Security;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>
/// Base for every Administration Center page. The <c>Admin</c> policy already protects the area; every handler checks
/// <see cref="CurrentUser.IsAdmin"/> again on the server (SEC-519), and the services check it a third time.
/// </summary>
public abstract class AdminPageModel : PageModel
{
    public List<string> Errors { get; } = [];

    protected CurrentUser Me => HttpContext.RequestServices.GetRequiredService<CurrentUser>();

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        if (!Me.IsAdmin) context.Result = Forbid();
    }

    /// <summary>Redirect with a toast describing the outcome.</summary>
    protected IActionResult Done(AdminResult r, string url)
    {
        TempData["Toast"] = r.Ok ? r.Message ?? "Saved." : (r.Message != null ? r.Message + " " : "") + r.Error;
        TempData["ToastKind"] = r.Ok && r.Errors.Count == 0 ? "ok" : "bad";
        return Redirect(url);
    }

    protected IActionResult Toast(string message, string url, string kind = "ok")
    {
        TempData["Toast"] = message;
        TempData["ToastKind"] = kind;
        return Redirect(url);
    }

    /// <summary>The current URL with one query parameter replaced (or removed when null).</summary>
    public string With(string key, object? value) => AdminQuery.With(Request, key, value);

    protected string Here => Request.Path + Request.QueryString;
}

public static class AdminQuery
{
    public static string With(HttpRequest req, string key, object? value, params string[] drop)
    {
        var q = req.Query.Where(kv => !string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Key != "handler" &&
                                      !drop.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) && !(key != "p" && kv.Key == "p"))
            .SelectMany(kv => kv.Value.Select(v => (kv.Key, Value: v ?? "")))
            .ToList();
        if (value != null && value.ToString() is { Length: > 0 } s) q.Add((key, s));
        return req.Path + (q.Count > 0 ? "?" + string.Join("&", q.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}")) : "");
    }
}

/// <summary>Model of the shared pager partial.</summary>
public sealed record PagerModel(int Page, int Pages, int First, int Last, int Total, string Noun, Func<int, string> Link)
{
    public static PagerModel For<T>(Paged<T> p, string noun, HttpRequest req) =>
        new(p.Page, p.Pages, p.First, p.Last, p.Total, noun, i => AdminQuery.With(req, "p", i > 1 ? i : null));
}
