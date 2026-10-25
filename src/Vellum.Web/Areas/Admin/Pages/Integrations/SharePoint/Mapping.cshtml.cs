using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Modules.Integrations.SharePoint;

namespace Vellum.Areas.Admin.Pages.Integrations.SharePoint;

public sealed record SpMapRow(string Source, string Template, string Column);

public sealed record MappingRowVm(SpMapRow Row, int Number, FormDefinition Form, List<FieldDefinition> Fields);

/// <summary>A SharePoint mapping profile (INT-302/303): form fields, system values or templates → column internal names.</summary>
public class MappingModel(VellumDbContext db, ConfigCache cfg, SharePointAdminService admin) : AdminPageModel
{
    public const string TemplateSource = "__template";

    public SharePointMapping? Mapping { get; private set; }
    public FormDefinition? Form { get; private set; }
    public List<FormDefinition> Forms { get; private set; } = [];
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public List<SpMapRow> Rows { get; private set; } = [];
    public List<SharePointLibrary> Libraries { get; private set; } = [];
    public List<SharePointRule> UsedBy { get; private set; } = [];
    public MappingCheck? Check { get; private set; }
    public int? CheckedLibraryId { get; private set; }

    public IEnumerable<FieldDefinition> Fields => Form == null ? [] : Form.Fields.Where(f => f.Type != FieldType.Prompt &&
        Form.Sections.FirstOrDefault(s => s.Key == f.SectionKey)?.IsSubform != true).OrderBy(f => f.Order);

    private async Task<bool> LoadAsync(string id, string? formKey)
    {
        var c = cfg.Current;
        Forms = c.Apps.Where(a => !a.IsSystem).OrderBy(a => a.Order).ThenBy(a => a.Name)
            .SelectMany(a => a.Forms.OrderBy(f => f.Order)).Where(f => SharePointAdminService.AttachmentFields(f).Any()).ToList();
        if (id != "new")
        {
            if (!int.TryParse(id, out var mid)) return false;
            Mapping = await db.Set<SharePointMapping>().AsNoTracking().Include(m => m.Entries).FirstOrDefaultAsync(m => m.Id == mid);
            if (Mapping == null) return false;
            formKey = Mapping.FormKey;
            Name = Mapping.Name;
            Description = Mapping.Description;
            Rows = Mapping.Entries.OrderBy(e => e.Order).Select(e => SharePointMetadata.IsTemplate(e.Source)
                ? new SpMapRow(TemplateSource, e.Source, e.Column) : new SpMapRow(e.Source, "", e.Column)).ToList();
            UsedBy = await db.Set<SharePointRule>().AsNoTracking().Where(r => r.MappingId == mid).OrderBy(r => r.Name).ToListAsync();
        }
        Form = formKey != null && c.FormsByKey.TryGetValue(formKey, out var f) ? f : null;
        Libraries = await db.Set<SharePointLibrary>().AsNoTracking().Include(l => l.Profile).OrderBy(l => l.Profile.Name).ThenBy(l => l.Name).ToListAsync();
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string id, string? form)
    {
        if (!await LoadAsync(id, form)) return NotFound();
        if (Mapping == null && Form != null && Rows.Count == 0)
        {
            // A sensible starting point: identity of the record and its revision.
            Name = $"{Form.Name} metadata";
            Rows = [new("$title", "", "Title"), new("$number", "", "DocumentNumber"), new("$status", "", "Status")];
            if (Form.RevisionControlled) Rows.Insert(2, new("$revision", "", "Revision"));
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string id, string formKey, string name, string? description, string[] source, string[] template, string[] column)
    {
        var rows = new List<SpMapRow>();
        for (var i = 0; i < Math.Max(source.Length, column.Length); i++)
            rows.Add(new SpMapRow(source.ElementAtOrDefault(i) ?? "", template.ElementAtOrDefault(i) ?? "", column.ElementAtOrDefault(i) ?? ""));
        var entries = rows.Select(r => (r.Source == TemplateSource ? r.Template : r.Source, r.Column)).ToList();
        int? mid = id == "new" ? null : int.TryParse(id, out var m) ? m : -1;
        var res = await admin.SaveMappingAsync(mid, new MappingInput { FormKey = formKey, Name = name, Description = description, Entries = entries });
        if (res.Ok) return Done(res, $"/admin/integrations/sharepoint/mappings/{res.Id}");
        Errors.AddRange(res.Errors);
        if (!await LoadAsync(id, formKey)) return NotFound();
        Name = name;
        Description = description;
        Rows = rows.Where(r => r.Source.Length > 0 || r.Column.Length > 0 || r.Template.Length > 0).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var res = await admin.DeleteMappingAsync(id);
        return Done(res, res.Ok ? "/admin/integrations/sharepoint" : $"/admin/integrations/sharepoint/mappings/{id}");
    }

    public async Task<IActionResult> OnPostCheckAsync(int id, int libraryId)
    {
        if (!await LoadAsync(id.ToString(), null)) return NotFound();
        Check = await admin.CheckMappingAsync(id, libraryId);
        CheckedLibraryId = libraryId;
        return Page();
    }
}
