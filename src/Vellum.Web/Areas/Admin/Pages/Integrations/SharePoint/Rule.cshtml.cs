using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Integrations.SharePoint;

namespace Vellum.Areas.Admin.Pages.Integrations.SharePoint;

public sealed class RuleForm
{
    public string Name { get; set; } = "";
    public string PhaseKey { get; set; } = "";
    public bool OnEnter { get; set; } = true;
    public bool OnSave { get; set; }
    public int LibraryId { get; set; }
    public List<string> FieldKeys { get; set; } = [];
    public string? FolderTemplate { get; set; }
    public string? FileNameTemplate { get; set; }
    public bool PreferRendition { get; set; } = true;
    public bool FallbackToOriginal { get; set; } = true;
    public int? MappingId { get; set; }
    public bool EffectiveOnly { get; set; } = true;
    public bool KeepVersionHistory { get; set; } = true;
    public SpWithdrawAction WithdrawAction { get; set; } = SpWithdrawAction.MarkObsolete;
    public string? ObsoleteFolder { get; set; } = "Obsolete";
    public bool Enabled { get; set; } = true;
}

/// <summary>A publishing rule (DSN-602 phase setting "SharePoint publishing": fields, library, folder rule, metadata mapping; INT-304/305/308).</summary>
public class RuleModel(VellumDbContext db, ConfigCache cfg, SharePointAdminService admin, AccessService access, RecordService records, UserDirectory dir) : AdminPageModel
{
    [BindProperty] public RuleForm Input { get; set; } = new();

    public SharePointRule? Rule { get; private set; }
    public FormDefinition? Form { get; private set; }
    public List<FormDefinition> Forms { get; private set; } = [];
    public List<SharePointLibrary> Libraries { get; private set; } = [];
    public List<SharePointMapping> Mappings { get; private set; } = [];
    public List<PhaseDefinition> Phases { get; private set; } = [];
    public List<FieldDefinition> AttachmentFields { get; private set; } = [];
    public int CurrentFiles { get; private set; }
    /// <summary>Token values of a readable sample record, for the live name preview.</summary>
    public Dictionary<string, string> Sample { get; private set; } = [];
    public string? SampleNumber { get; private set; }

    private async Task<bool> LoadAsync(string id, string? formKey, bool fillInput)
    {
        var c = cfg.Current;
        Forms = c.Apps.Where(a => !a.IsSystem).OrderBy(a => a.Order).ThenBy(a => a.Name)
            .SelectMany(a => a.Forms.OrderBy(f => f.Order)).Where(f => SharePointAdminService.AttachmentFields(f).Any()).ToList();
        if (id != "new")
        {
            if (!int.TryParse(id, out var rid)) return false;
            Rule = await db.Set<SharePointRule>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid);
            if (Rule == null) return false;
            formKey = Rule.FormKey;
            CurrentFiles = await db.Set<SharePointPublication>().CountAsync(p => p.RuleId == rid && p.Status == SpPublicationStatus.Published);
            if (fillInput)
                Input = new RuleForm
                {
                    Name = Rule.Name, PhaseKey = Rule.PhaseKey, OnEnter = Rule.OnEnter, OnSave = Rule.OnSave, LibraryId = Rule.LibraryId, FieldKeys = Rule.FieldKeys,
                    FolderTemplate = Rule.FolderTemplate, FileNameTemplate = Rule.FileNameTemplate, PreferRendition = Rule.PreferRendition,
                    FallbackToOriginal = Rule.FallbackToOriginal, MappingId = Rule.MappingId, EffectiveOnly = Rule.EffectiveOnly,
                    KeepVersionHistory = Rule.KeepVersionHistory, WithdrawAction = Rule.WithdrawAction, ObsoleteFolder = Rule.ObsoleteFolder, Enabled = Rule.Enabled,
                };
        }
        Form = formKey != null && c.FormsByKey.TryGetValue(formKey, out var f) ? f : null;
        Libraries = await db.Set<SharePointLibrary>().AsNoTracking().Include(l => l.Profile).OrderBy(l => l.Profile.Name).ThenBy(l => l.Name).ToListAsync();
        if (Form == null) return true;
        Mappings = await db.Set<SharePointMapping>().AsNoTracking().Where(m => m.FormKey == Form.Key).OrderBy(m => m.Name).ToListAsync();
        Phases = Form.Workflows.Where(w => !w.Disabled).SelectMany(w => w.Phases).Where(p => p.Type != PhaseType.Voided).OrderBy(p => p.Order)
            .DistinctBy(p => p.Key).ToList();
        AttachmentFields = SharePointAdminService.AttachmentFields(Form).ToList();
        await LoadSampleAsync();
        return true;
    }

    private async Task LoadSampleAsync()
    {
        var q = access.Readable().Where(r => r.FormId == Form!.Id && !r.IsArchived);
        if (Form!.RevisionControlled) q = q.OrderByDescending(r => r.IsEffective).ThenByDescending(r => r.ModifiedAt);
        else q = q.OrderByDescending(r => r.ModifiedAt);
        var id = await q.Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
        if (id == null) return;
        var snap = await records.LoadAsync(id.Value);
        if (snap == null) return;
        SampleNumber = snap.Record.Number + (snap.Record.Revision != null ? $" rev {snap.Record.Revision}" : "");
        foreach (var (token, _) in SharePointNaming.SystemTokens) Sample[token] = SharePointNaming.TokenValue(token, snap, dir, "procedure.pdf") ?? "";
        foreach (var f in Form.Fields.Where(x => x.Type is not (FieldType.Prompt or FieldType.Attachment)))
            Sample[f.Key] = SharePointNaming.TokenValue(f.Key, snap, dir) ?? "";
    }

    public async Task<IActionResult> OnGetAsync(string id, string? form)
    {
        if (!await LoadAsync(id, form, fillInput: true)) return NotFound();
        if (Rule == null && Form != null)
        {
            var completed = Phases.FirstOrDefault(p => p.Type == PhaseType.Completed);
            Input = new RuleForm
            {
                Name = $"Publish {Form.Name.ToLowerInvariant()}s" , PhaseKey = Form.IsNonWorkflow ? "*" : completed?.Key ?? Phases.FirstOrDefault()?.Key ?? "",
                FieldKeys = AttachmentFields.Take(1).Select(x => x.Key).ToList(), LibraryId = Libraries.FirstOrDefault()?.Id ?? 0,
                FolderTemplate = Form.Fields.Any(x => x.Key == "doc_type") ? "{doc_type}" : "", FileNameTemplate = "{number} - {title}",
                MappingId = Mappings.FirstOrDefault()?.Id, EffectiveOnly = Form.RevisionControlled,
            };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string id, string formKey)
    {
        var lib = await db.Set<SharePointLibrary>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == Input.LibraryId);
        int? rid = id == "new" ? null : int.TryParse(id, out var r) ? r : -1;
        var res = await admin.SaveRuleAsync(rid, new RuleInput
        {
            Name = Input.Name, FormKey = formKey, PhaseKey = Input.PhaseKey, OnEnter = Input.OnEnter, OnSave = Input.OnSave,
            ProfileId = lib?.ProfileId ?? 0, LibraryId = Input.LibraryId, FieldKeys = Input.FieldKeys, FolderTemplate = Input.FolderTemplate,
            FileNameTemplate = Input.FileNameTemplate, PreferRendition = Input.PreferRendition, FallbackToOriginal = Input.FallbackToOriginal,
            MappingId = Input.MappingId, EffectiveOnly = Input.EffectiveOnly, KeepVersionHistory = Input.KeepVersionHistory,
            WithdrawAction = Input.WithdrawAction, ObsoleteFolder = Input.ObsoleteFolder, Enabled = Input.Enabled,
        });
        if (res.Ok) return Done(res, $"/admin/integrations/sharepoint/rules/{res.Id}");
        Errors.AddRange(res.Errors);
        if (!await LoadAsync(id, formKey, fillInput: false)) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var res = await admin.DeleteRuleAsync(id);
        return Done(res, res.Ok ? "/admin/integrations/sharepoint" : $"/admin/integrations/sharepoint/rules/{id}");
    }

    public async Task<IActionResult> OnPostPublishExistingAsync(int id) =>
        Done(await admin.PublishExistingAsync(id), $"/admin/integrations/sharepoint/rules/{id}");
}
