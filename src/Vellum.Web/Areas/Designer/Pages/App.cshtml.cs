using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

/// <summary>Application designer home (DSN-006): components by type with counts, new/copy/delete, draft state, locks, problems.</summary>
public class AppModel(DesignerStore store) : DesignerPageModel(store)
{
    public List<DesignIssue> Issues { get; private set; } = [];
    public List<DesignerLock> Locks { get; private set; } = [];
    public List<ConfigPublication> Publications { get; private set; } = [];
    public Dictionary<string, int> RecordsByForm { get; } = [];
    public bool IsAdmin => Store.Me.IsAdmin;
    public int MeId => Store.Me.Id;

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Key { get; set; }
    [BindProperty] public string? Kind { get; set; }
    [BindProperty] public bool Starter { get; set; }
    [BindProperty] public string? Category { get; set; }
    [BindProperty] public string? CopyFrom { get; set; }
    [BindProperty] public string? Form { get; set; }
    [BindProperty] public string? View { get; set; }
    [BindProperty] public string? Workflow { get; set; }
    [BindProperty] public string? Component { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        var (_, ctx, issues) = await Store.ValidateAsync(T);
        Issues = issues;
        Locks = await Store.ActiveLocksAsync(T.AppKey);
        Publications = await Store.Publications.AsNoTracking().Where(p => p.AppKey == T.AppKey).OrderByDescending(p => p.VersionNumber).Take(5).ToListAsync();
        if (T.Live != null)
            foreach (var f in T.Live.Forms) RecordsByForm[f.Key] = ctx.RecordsByForm.GetValueOrDefault(f.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostNewFormAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        if (string.IsNullOrWhiteSpace(Name)) { Toast("Give the form a name.", "bad"); return Redirect(AppHref); }
        var taken = Store.Cfg.FormsByKey.Keys.Concat(T.Doc.Forms.Select(f => f.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Key) && DesignDocs.KeyProblem(Key.Trim()) is { } kp) { Toast($"The design name {kp}.", "bad"); return Redirect(AppHref); }
        FormDoc? created = null;
        await Store.MutateAsync(T.AppKey, d => created = DesignerOps.NewForm(d, Name, Key ?? "", Kind is "activity" or "profile" ? Kind : "main", Starter, taken));
        Toast($"{created!.Name} added to the draft.");
        return Redirect($"{AppHref}/forms/{created.Key}");
    }

    public async Task<IActionResult> OnPostNewViewAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        if (string.IsNullOrWhiteSpace(Name)) { Toast("Give the view a name.", "bad"); return Redirect(AppHref); }
        ViewDoc? created = null;
        await Store.MutateAsync(T.AppKey, d => created = DesignerOps.NewView(d, Name, Key, Category, string.IsNullOrWhiteSpace(CopyFrom) ? null : CopyFrom));
        Toast(CopyFrom != null ? $"Copied into {created!.Name}." : $"{created!.Name} added to the draft.");
        return Redirect($"{AppHref}/views/{created.Key}");
    }

    public async Task<IActionResult> OnPostNewWorkflowAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        var form = T.Doc.Forms.FirstOrDefault(f => f.Key == Form);
        if (form == null) return NotFound();
        var lk = await Store.AcquireAsync(T.AppKey, $"form:{form.Key}", $"form {form.Name}");
        if (!lk.Ok) { Toast($"{lk.HolderName} is editing {form.Name}. Try again when they're done.", "bad"); return Redirect(AppHref); }
        WorkflowDoc? w = null;
        await Store.MutateAsync(T.AppKey, d => w = DesignerOps.AddWorkflow(d, form.Key, string.IsNullOrWhiteSpace(Name) ? form.Name : Name));
        Toast($"Workflow {w!.Name} added with a starter set of phases.");
        return Redirect($"{AppHref}/forms/{form.Key}/workflows/{w.Key}");
    }

    public async Task<IActionResult> OnPostDeleteFormAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        var form = T.Doc.Forms.FirstOrDefault(f => f.Key == Form);
        if (form == null) return Redirect(AppHref);
        var lk = await Store.AcquireAsync(T.AppKey, $"form:{form.Key}", $"form {form.Name}");
        if (!lk.Ok) { Toast($"{lk.HolderName} is editing {form.Name}.", "bad"); return Redirect(AppHref); }
        await Store.MutateAsync(T.AppKey, d => DesignerOps.DeleteForm(d, form.Key));
        await Store.ReleaseAsync(T.AppKey, $"form:{form.Key}");
        Toast(form.Id != null ? $"{form.Name} will be deleted when you publish. Publishing is blocked while records use it." : $"{form.Name} removed from the draft.");
        return Redirect(AppHref);
    }

    public async Task<IActionResult> OnPostDeleteWorkflowAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        var form = T.Doc.Forms.FirstOrDefault(f => f.Key == Form);
        if (form == null) return Redirect(AppHref);
        var lk = await Store.AcquireAsync(T.AppKey, $"form:{form.Key}", $"form {form.Name}");
        if (!lk.Ok) { Toast($"{lk.HolderName} is editing {form.Name}.", "bad"); return Redirect(AppHref); }
        await Store.MutateAsync(T.AppKey, d => DesignerOps.DeleteWorkflow(d, form.Key, Workflow ?? ""));
        Toast("Workflow removed from the draft.");
        return Redirect($"{AppHref}/forms/{form.Key}");
    }

    public async Task<IActionResult> OnPostDeleteViewAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        var v = T.Doc.Views.FirstOrDefault(x => x.Key == View);
        if (v == null) return Redirect(AppHref);
        var lk = await Store.AcquireAsync(T.AppKey, $"view:{v.Key}", $"view {v.Name}");
        if (!lk.Ok) { Toast($"{lk.HolderName} is editing {v.Name}.", "bad"); return Redirect(AppHref); }
        await Store.MutateAsync(T.AppKey, d => DesignerOps.DeleteView(d, v.Key));
        await Store.ReleaseAsync(T.AppKey, $"view:{v.Key}");
        Toast($"{v.Name} removed from the draft.");
        return Redirect(AppHref);
    }

    public async Task<IActionResult> OnPostDiscardAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        var isNew = T.IsNew;
        await Store.DiscardAsync(T.AppKey);
        Toast(isNew ? "The unpublished application was deleted." : "Draft discarded. The live configuration is unchanged.", "info");
        return Redirect(isNew ? "/designer" : AppHref);
    }

    public async Task<IActionResult> OnPostBreakLockAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        if (!Store.Me.IsAdmin) return Forbid();
        await Store.BreakAsync(T.AppKey, Component ?? "");
        Toast("Lock released. The other designer's unsaved changes won't be saved.", "info");
        return Redirect(AppHref);
    }

    public string LockLabel(string component) =>
        Locks.FirstOrDefault(l => l.Component == component && l.UserId != MeId) is { } l ? $"{l.UserName} is editing" : "";
}
