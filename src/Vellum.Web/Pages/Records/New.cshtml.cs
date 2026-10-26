using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Pages.Records;

public class NewModel(ConfigCache cfg, AccessService access, RecordService records, RecordVmBuilder builder, VellumDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Form { get; set; } = "";
    [BindProperty(SupportsGet = true)] public Guid? Parent { get; set; }

    public RecordVm Vm { get; private set; } = null!;

    private async Task<(FormDefinition? Form, Record? Parent, IActionResult? Fail)> ResolveAsync()
    {
        if (!cfg.Current.FormsByKey.TryGetValue(Form, out var form) || form.Disabled) return (null, null, NotFound());
        Record? parent = null;
        if (Parent is { } pid)
        {
            if (!await access.CanReadAsync(pid)) return (null, null, NotFound());
            parent = await db.Records.AsNoTracking().FirstAsync(r => r.Id == pid);
            // Only the parent's activity forms for its current phase (PLT-640); any other form would borrow the parent's rights.
            var parentForm = cfg.Current.FormsById[parent.FormId];
            if (parent.IsArchived || !cfg.Current.ActivityFormsFor(parentForm, cfg.Current.Phase(parent.PhaseId)).Any(f => f.Id == form.Id))
                return (null, null, BadRequest($"{form.Name} records can't be created from this record."));
        }
        // PLT-642: assignees (and managers) of the parent may create its activities even without author rights on the activity form,
        // as long as they can read that form (the same rule as the record page's Create menu).
        var viaParent = parent != null && access.RoleFor(form.AppId, form.Key) >= AppRole.Reader &&
            (await access.ActingAssignmentAsync(parent.Id) != null || access.RoleFor(parent.AppId) >= AppRole.Manager);
        if (!access.CanCreate(form) && !viaParent) return (null, null, Forbid());
        if (form.IsActivity && parent == null)
        {
            // PLT-205: an activity needs its parent (for example a stale bookmark to the bare form).
            TempData["Toast"] = $"{form.Name} records are created from their parent record: open it and choose New activity.";
            TempData["ToastKind"] = "info";
            return (null, null, Redirect($"/apps/{form.App.Key}"));
        }
        return (form, parent, null);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var (form, parent, fail) = await ResolveAsync();
        if (fail != null) return fail;
        var data = new RecordData();
        Prefill(form!, data);
        Vm = await builder.BuildNewAsync(form!, data, parent);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (form, parent, fail) = await ResolveAsync();
        if (fail != null) return fail;
        var start = form!.Workflows.FirstOrDefault(w => !w.Disabled)?.Phases.FirstOrDefault(p => p.IsStart);
        var errors = new List<FieldError>();
        var data = records.ParseInput(form, start, null, Request.Form, errors);
        if (errors.Count == 0)
        {
            var files = Request.Form.Files.Where(f => f.Name.StartsWith("file.") && f.Length > 0)
                .Select(f => (f.Name[5..], f.FileName, string.IsNullOrEmpty(f.ContentType) ? "application/octet-stream" : f.ContentType, f.OpenReadStream())).ToList();
            var viaParent = parent != null;
            var res = await records.CreateAsync(new CreateRequest { FormKey = form.Key, ParentId = parent?.Id, Data = data, Files = files, SkipAccessCheck = viaParent && !access.CanCreate(form) });
            if (res.Ok)
            {
                TempData["Toast"] = $"{res.Record!.Number} created.";
                return Redirect($"/records/{res.Record.Id}");
            }
            errors.AddRange(res.Errors);
        }
        Vm = await builder.BuildNewAsync(form, data, parent, errors);
        return Page();
    }

    /// <summary>Show default values on the blank form, and copy a parent's subject into a new activity (PLT-707 style inheritance).</summary>
    private void Prefill(FormDefinition form, RecordData data)
    {
        foreach (var f in form.Fields.Where(f => f.DefaultValue != null && FieldCodec.StoresValues(f.Type)))
        {
            var dv = f.DefaultValue!;
            object? v = dv switch
            {
                "$me" => access.UserId,
                "$today" => BusinessCalendar.Today,
                "$now" => DateTime.UtcNow,
                _ when dv.StartsWith("$today+") => BusinessCalendar.Today.AddDays(int.Parse(dv[7..])),
                _ => dv,
            };
            try { data.Main.Set(f.Key, FieldCodec.FromObject(f, v, cfg.Current)); } catch (ArgumentException) { }
        }
    }
}
