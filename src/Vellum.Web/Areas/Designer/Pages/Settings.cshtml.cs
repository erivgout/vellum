using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Infrastructure.Ui;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

/// <summary>Application settings (DSN-101): name, description, icon, color, suite, default access, hide My Assignments.</summary>
public class SettingsModel(DesignerStore store) : DesignerPageModel(store)
{
    public const string Component = "settings";
    public LockResult Lock { get; private set; } = new(true);
    public bool ReadOnly => !Lock.Ok;
    public IEnumerable<string> Suites => UiContext.SuiteOrder.Select(s => s.Name).Concat(Store.Cfg.Apps.Select(a => a.Suite)).Append(T.Doc.Suite).Distinct();

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string Icon { get; set; } = "";
    [BindProperty] public string Color { get; set; } = "";
    [BindProperty] public string Suite { get; set; } = "";
    [BindProperty] public AppRole DefaultAccess { get; set; }
    [BindProperty] public bool HideMyAssignments { get; set; }
    [BindProperty] public bool ProtectFromUpgrade { get; set; }
    public List<string> Errors { get; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        Lock = await Store.AcquireAsync(T.AppKey, Component, "application settings");
        var d = T.Doc;
        Name = d.Name; Description = d.Description; Icon = d.Icon; Color = d.Color; Suite = d.Suite;
        DefaultAccess = d.DefaultAccess; HideMyAssignments = d.HideMyAssignments; ProtectFromUpgrade = d.ProtectFromUpgrade;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        Lock = await Store.AcquireAsync(T.AppKey, Component, "application settings");
        if (!Lock.Ok) { Errors.Add($"{Lock.HolderName} is editing these settings. Your changes weren't saved."); return Page(); }
        if (string.IsNullOrWhiteSpace(Name)) Errors.Add("The application needs a display name.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(Color ?? "", "^#[0-9a-fA-F]{6}$")) Errors.Add("Pick a color such as #7c3aed.");
        if (string.IsNullOrWhiteSpace(Icon) || !Icon.Trim().StartsWith("bi-")) Errors.Add("Pick an icon from the library.");
        if (string.IsNullOrWhiteSpace(Suite)) Errors.Add("Choose the suite the application appears in.");
        if (!Enum.IsDefined(DefaultAccess)) Errors.Add("Choose a default access level.");
        if (Errors.Count > 0) return Page();
        await Store.MutateAsync(T.AppKey, d =>
        {
            d.Name = Name.Trim(); d.Description = Description; d.Icon = Icon.Trim(); d.Color = Color!.ToLowerInvariant(); d.Suite = Suite.Trim();
            d.DefaultAccess = DefaultAccess; d.HideMyAssignments = HideMyAssignments; d.ProtectFromUpgrade = ProtectFromUpgrade;
        });
        Toast("Settings saved to the draft.");
        return Redirect($"{AppHref}/settings");
    }

    public async Task<IActionResult> OnPostReleaseAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        await Store.ReleaseAsync(T.AppKey, Component);
        return Redirect(AppHref);
    }

    public async Task<IActionResult> OnPostHeartbeatAsync()
    {
        if (await OpenAsync() is { } deny) return Denied();
        var l = await Store.AcquireAsync(T.AppKey, Component, "application settings");
        return Json(new { ok = l.Ok, holder = l.HolderName });
    }
}
