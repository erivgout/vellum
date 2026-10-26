using AngleSharp.Dom;

namespace Vellum.Tests.E2e;

/// <summary>
/// The record page as a person uses it: create from /records/new, fill fields, save, send through the route dialog
/// (with e-signature), run behavior actions and comment. Mirrors what site.js does in the browser (for example, only
/// the chosen route panel's fields are posted).
/// </summary>
public static class RecordSteps
{
    /// <summary>Open the new-record page for a form.</summary>
    public static Task<Page> NewAsync(this Browser b, string formKey, Guid? parent = null) =>
        b.GoAsync($"/records/new?form={formKey}" + (parent != null ? $"&parent={parent}" : ""));

    /// <summary>The record form with every route-dialog panel left out (they belong to the Send step).</summary>
    public static FormFill RecordForm(this Page page) => page.Form("#record-form").Disable("[data-route-panel]");

    /// <summary>Fill and create a record; fails with the page's messages unless it lands on the new record.</summary>
    public static async Task<Page> CreateAsync(this Browser b, string formKey, Action<FormFill> fill, Guid? parent = null)
    {
        var page = await b.NewAsync(formKey, parent);
        var form = page.RecordForm();
        fill(form);
        var result = await form.SubmitAsync();
        if (!result.Url.AbsolutePath.StartsWith("/records/") || result.Url.AbsolutePath.StartsWith("/records/new"))
            throw new Xunit.Sdk.XunitException($"Creating a {formKey} record failed on {result.Path}: {string.Join(" | ", result.Errors)}\n{result.Excerpt()}");
        return result;
    }

    /// <summary>Change fields and press Save. Returns the page after the redirect (or the redisplayed form on errors).</summary>
    public static Task<Page> SaveAsync(this Page page, Action<FormFill> fill)
    {
        var form = page.RecordForm();
        fill(form);
        return form.SubmitAsync("[data-save-button]");
    }

    /// <summary>The route options offered in the Send dialog (labels).</summary>
    public static IReadOnlyList<string> RouteOptions(this Page page) =>
        page.All("[data-route-option] .nm").Select(e => Page.Squash(e.TextContent)).ToList();

    /// <summary>
    /// Send the record by choosing a route in the dialog. The label matches the option's name exactly, or else as a prefix.
    /// Signs with <paramref name="password"/> when the route needs an e-signature (and certifies on first signature).
    /// </summary>
    public static Task<Page> RouteAsync(this Page page, string option, string? password = TestHelpers.Password, string? comment = null,
        int[]? assignees = null, Action<FormFill>? fill = null)
    {
        var opts = page.All("[data-route-option]");
        var opt = opts.FirstOrDefault(o => Page.Squash(o.QuerySelector(".nm")!.TextContent).Equals(option, StringComparison.OrdinalIgnoreCase))
            ?? opts.FirstOrDefault(o => Page.Squash(o.QuerySelector(".nm")!.TextContent).StartsWith(option, StringComparison.OrdinalIgnoreCase))
            ?? throw new Xunit.Sdk.XunitException($"No route '{option}' on {page.Path}. Offered: {string.Join(", ", page.RouteOptions())}");
        var id = opt.GetAttribute("data-route-option")!;
        var form = page.Form("#record-form").Disable($"[data-route-panel]:not([data-route-panel='{id}'])");
        form.Set("routeId", id);
        var panel = page.One($"[data-route-panel='{id}']");
        if (comment != null) form.Set("comment", comment);
        if (assignees != null) form.Set("assignees", assignees.Select(a => a.ToString()).ToArray());
        if (panel.QuerySelector("input[name=password]") != null) form.Set("password", password);
        if (panel.QuerySelector("input[name=certify]") != null) form.Set("certify", "true");
        fill?.Invoke(form);
        return form.SubmitToAsync($"/records/{page.RecordId}?handler=Route");
    }

    /// <summary>Route and require success: back on the record page with no route errors.</summary>
    public static async Task<Page> RouteOkAsync(this Page page, string option, string? password = TestHelpers.Password, string? comment = null,
        int[]? assignees = null, Action<FormFill>? fill = null)
    {
        var result = await page.RouteAsync(option, password, comment, assignees, fill);
        var errors = result.RouteErrors().Concat(result.Errors).ToList();
        if (result.Status != System.Net.HttpStatusCode.OK)
            throw new Xunit.Sdk.XunitException($"Routing '{option}' from {page.Phase()} landed on HTTP {(int)result.Status} ({result.Path}).\n{result.Excerpt()}");
        if (result.Toast == null || errors.Count > 0)
            throw new Xunit.Sdk.XunitException($"Routing '{option}' from {page.Phase()} failed: {string.Join(" | ", errors)}\n{result.Excerpt()}");
        return result;
    }

    /// <summary>Errors shown in the Send dialog after a failed route.</summary>
    public static IReadOnlyList<string> RouteErrors(this Page page) =>
        page.All("#route-dialog .vl-banner-bad li").Select(e => Page.Squash(e.TextContent)).ToList();

    /// <summary>Run a behavior action from the record's Actions menu (its dialog form).</summary>
    public static Task<Page> ActionAsync(this Page page, string actionKey, Action<FormFill>? fill = null, string? password = null, string? comment = null)
    {
        var form = page.Form($"#action-{actionKey} form");
        if (comment != null) form.Set("comment", comment);
        if (password != null)
        {
            form.Set("password", password);
            if (page.Q($"#action-{actionKey} input[name=certify]") != null) form.Set("certify", "true");
        }
        fill?.Invoke(form);
        return form.SubmitAsync();
    }

    /// <summary>Actions offered in the record's Actions menu (keys).</summary>
    public static IReadOnlyList<string> Actions(this Page page) =>
        page.All("[data-bs-target^='#action-']").Select(e => e.GetAttribute("data-bs-target")![8..]).ToList();

    public static Task<Page> CommentAsync(this Page page, string text) =>
        page.Form($"form[action$='handler=Comment']").Set("text", text).SubmitAsync();

    /// <summary>Current phase name from the record header.</summary>
    public static string Phase(this Page page) => Page.Squash(page.Q(".vl-record-id .vl-chip")?.TextContent ?? "");

    /// <summary>Record number from the record header.</summary>
    public static string Number(this Page page) => Page.Squash(page.One(".vl-record-id .num").TextContent);

    public static string Title(this Page page) => Page.Squash(page.One(".vl-record-title").TextContent);

    /// <summary>The read-only display value of a field (by field key).</summary>
    public static string FieldText(this Page page, string fieldKey) =>
        Page.Squash(page.Q($"[data-ro-field='{fieldKey}']")?.TextContent ?? page.Q($"[name='f.{fieldKey}']")?.GetAttribute("value") ?? "");

    /// <summary>The value of an editable field input (by field key).</summary>
    public static string? FieldValue(this Page page, string fieldKey) => page.Q($"[name='f.{fieldKey}']") switch
    {
        AngleSharp.Html.Dom.IHtmlTextAreaElement t => t.TextContent,
        AngleSharp.Html.Dom.IHtmlSelectElement s => s.QuerySelector("option[selected]")?.GetAttribute("data-key"),
        IElement e => e.GetAttribute("value"),
        null => null,
    };
}
