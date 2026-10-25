using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>ADM §5.1 System Preferences, with compliance-sensitive settings marked and guarded (SAF-010/011).</summary>
public class SettingsModel(VellumDbContext db, SettingsAdminService settings) : AdminPageModel
{
    [BindProperty] public SettingsInput Input { get; set; } = new();
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public string? Password { get; set; }

    public SystemSettings Current { get; private set; } = null!;
    public DateTime? LastChanged { get; private set; }
    public string? LastChangedBy { get; private set; }

    private async Task LoadAsync(bool keepInput = false)
    {
        Current = await db.Settings.AsNoTracking().FirstAsync();
        if (!keepInput) Input = SettingsInput.From(Current);
        var last = await db.ConfigAudit.AsNoTracking().Where(c => c.ObjectType == "SystemSettings").OrderByDescending(c => c.Id).FirstOrDefaultAsync();
        LastChanged = last?.At;
        LastChangedBy = last?.UserName;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        var res = await settings.SaveAsync(Input, Reason, Password);
        if (res.Ok) return Done(res, "/admin/settings");
        await LoadAsync(keepInput: true);
        Errors.AddRange(res.Errors);
        Password = null;
        return Page();
    }

    public static SensitiveSetting? Sensitive(string key) => ComplianceCatalog.Find(key);

    /// <summary>Unsaved changes after a failed save (the form shows the entered values, not the stored ones).</summary>
    public List<SettingChange> Pending()
    {
        var proposed = new SystemSettings();
        SettingsInput.From(Current).ApplyTo(proposed);
        Input.ApplyTo(proposed);
        return SettingsAdminService.Changes(Current, proposed);
    }
}
