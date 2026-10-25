using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

public sealed class FormSaveRequest
{
    public FormDoc? Form { get; set; }
    public Renames? Renames { get; set; }
}

/// <summary>
/// Visual form designer (DSN §3.3–3.4): tabs → sections/subforms → fields on the 12-column grid with live preview.
/// The browser edits a copy of the form document; Save writes it into the draft (never the live configuration).
/// </summary>
public class FormModel(DesignerStore store) : DesignerPageModel(store)
{
    [BindProperty(SupportsGet = true)] public string Form { get; set; } = "";
    public FormDoc F { get; private set; } = null!;
    public LockResult Lock { get; private set; } = new(true);
    public bool ReadOnly => !Lock.Ok;
    public string Component => $"form:{Form}";
    public string IslandJson { get; private set; } = "{}";
    public string Base => $"{AppHref}/forms/{Form}";

    public async Task<IActionResult> OnGetAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        var f = T.Doc.Forms.FirstOrDefault(x => x.Key == Form);
        if (f == null) return NotFound();
        F = f;
        Lock = await Store.AcquireAsync(T.AppKey, Component, $"form {f.Name}");
        var (_, ctx, issues) = await Store.ValidateAsync(T);
        IslandJson = Island(await BuildIslandAsync(f, ctx, issues));
        return Page();
    }

    private Task<object> BuildIslandAsync(FormDoc f, DesignContext ctx, List<DesignIssue> issues)
    {
        var cfg = Store.Cfg;
        var data = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (f.Id != null)
            foreach (var x in f.AllFields.Where(x => x.Id != null)) data[x.Key] = ctx.DataFor(f.Id, x.Key);
        var forms = T.Doc.Forms.Select(x => new { key = x.Key, name = x.Name, app = T.Doc.Name, isActivity = x.IsActivity })
            .Concat(cfg.FormsByKey.Values.Where(x => x.AppId != T.Doc.Id).OrderBy(x => x.App.Order).ThenBy(x => x.Order)
                .Select(x => new { key = x.Key, name = x.Name, app = x.App.Name, isActivity = x.IsActivity }))
            .ToList();
        return Task.FromResult<object>(new
        {
            app = T.AppKey, appName = T.Doc.Name, color = T.Doc.Color,
            form = f,
            readOnly = ReadOnly,
            lockHolder = ReadOnly ? Lock.HolderName : null,
            urls = new
            {
                save = $"{Base}?handler=Save", heartbeat = $"{Base}?handler=Heartbeat", workflows = $"{Base}/workflows/",
                newWorkflow = $"{AppHref}?handler=NewWorkflow", app = AppHref,
            },
            data,
            keywordLists = cfg.KeywordListsByKey.Values.OrderBy(l => l.Name).Select(l => new
            {
                key = l.Key, name = l.Name, options = l.Keywords.Where(k => !k.Disabled).Select(k => new { key = k.Key, label = k.Label, color = k.Color }),
            }),
            forms,
            types = Enum.GetValues<FieldType>().Select(t => new { value = t.ToString(), label = DesignDocs.TypeLabel(t), icon = DesignDocs.TypeIcon(t), width = DesignDocs.DefaultWidth(t) }),
            issues = FormIssues(issues, f.Key),
            systemColumns = Vellum.Engine.Views.ViewService.SystemColumns.Keys,
        });
    }

    private static List<DesignIssue> FormIssues(IEnumerable<DesignIssue> issues, string key) =>
        issues.Where(i => i.Path == $"form:{key}" || i.Path.StartsWith($"form:{key}/section:") || i.Path.StartsWith($"form:{key}/field:")).ToList();

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (await OpenAsync() is not null) return Denied();
        var req = await ReadBodyAsync<FormSaveRequest>();
        if (req?.Form == null) return Json(new { ok = false, error = "The form couldn't be read. Reload the page and try again." }, 400);
        var current = T.Doc.Forms.FirstOrDefault(x => x.Key == Form);
        if (current == null) return Json(new { ok = false, error = "This form is no longer in the draft. Reload the page." }, 404);
        var lk = await Store.AcquireAsync(T.AppKey, Component, $"form {current.Name}");
        if (!lk.Ok) return Json(new { ok = false, error = $"{lk.HolderName} is editing this form, so your changes weren't saved (DSN-004)." }, 409);

        var incoming = req.Form;
        incoming.Key = (incoming.Key ?? "").Trim();
        if (current.Id != null) incoming.Key = current.Key; // design names of published forms are fixed (INV-130)
        else if (incoming.Key != current.Key)
        {
            if (DesignDocs.KeyProblem(incoming.Key) is { } kp) return Json(new { ok = false, error = $"The form's design name {kp}." }, 400);
            if (Store.Cfg.FormsByKey.ContainsKey(incoming.Key) || T.Doc.Forms.Any(x => x != current && x.Key.Equals(incoming.Key, StringComparison.OrdinalIgnoreCase)))
                return Json(new { ok = false, error = $"Another form already uses the design name '{incoming.Key}'." }, 400);
        }
        // Published sections and fields keep their design names (records and rules refer to them).
        var liveForm = T.LiveDoc?.Forms.FirstOrDefault(x => x.Id == current.Id && x.Id != null);
        if (liveForm != null)
        {
            foreach (var s in incoming.Sections.Where(s => s.Id != null))
                if (liveForm.Sections.FirstOrDefault(l => l.Id == s.Id) is { } ls) s.Key = ls.Key;
            foreach (var x in incoming.AllFields.Where(x => x.Id != null))
                if (liveForm.AllFields.FirstOrDefault(l => l.Id == x.Id) is { } lx) x.Key = lx.Key;
        }

        FormDoc saved = null!;
        await Store.MutateAsync(T.AppKey, d => saved = DesignerOps.ReplaceForm(d, Form, incoming, req.Renames));
        var t = await Store.OpenAsync(T.AppKey);
        var (_, _, issues) = await Store.ValidateAsync(t!);
        var stored = t!.Doc.Forms.FirstOrDefault(x => x.Key == saved.Key) ?? saved;
        return Json(new
        {
            ok = true, form = stored, issues = FormIssues(issues, stored.Key),
            redirect = stored.Key != Form ? $"{AppHref}/forms/{stored.Key}" : null,
        });
    }

    public async Task<IActionResult> OnPostHeartbeatAsync()
    {
        if (await OpenAsync() is not null) return Denied();
        var name = T.Doc.Forms.FirstOrDefault(x => x.Key == Form)?.Name ?? Form;
        var l = await Store.AcquireAsync(T.AppKey, Component, $"form {name}");
        return Json(new { ok = l.Ok, holder = l.HolderName });
    }

    public async Task<IActionResult> OnPostReleaseAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        await Store.ReleaseAsync(T.AppKey, Component);
        return Redirect(AppHref);
    }
}
