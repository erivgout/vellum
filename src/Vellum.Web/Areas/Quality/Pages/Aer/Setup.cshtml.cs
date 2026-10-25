using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Modules.AdverseEvents;

namespace Vellum.Areas.Quality.Pages.Aer;

/// <summary>AER-001/002: the application profile, MFR/importer mappings by location, product codes (with import) and the eMDR rule set (AER-006).</summary>
[Authorize]
public class SetupModel(AerSetup setup, AccessService access, ConfigCache cfg, EmdrProfileService profiles, CurrentUser me) : PageModel
{
    public AerProfileInfo? Profile { get; private set; }
    public List<AerMappingInfo> Mappings { get; private set; } = [];
    public List<AerProductCode> ProductCodes { get; private set; } = [];
    public EmdrProfile? Emdr { get; private set; }
    public List<string> Errors { get; } = [];
    public bool IsAdmin => me.IsAdmin;

    [BindProperty] public bool MappingEnabled { get; set; }
    [BindProperty] public int ReceiptDelayMinutes { get; set; } = AerProfileInfo.DefaultReceiptDelayMinutes;
    [BindProperty] public string SignSubmit { get; set; } = AerProfileInfo.DefaultSignSubmit;
    [BindProperty] public string SignComplete { get; set; } = AerProfileInfo.DefaultSignComplete;
    [BindProperty] public string SignVoid { get; set; } = AerProfileInfo.DefaultSignVoid;
    [BindProperty] public List<int> Approvers { get; set; } = [];
    [BindProperty] public List<string> Groups { get; set; } = [.. AerProfileInfo.AllGroups];
    [BindProperty] public string? Csv { get; set; }

    private bool Allowed => cfg.Current.AppsByKey.TryGetValue(AerKeys.App, out _) && setup.CanManage;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Allowed) return cfg.Current.AppsByKey.ContainsKey(AerKeys.App) ? Forbid() : NotFound();
        await LoadAsync(fillInputs: true);
        return Page();
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        if (!Allowed) return Forbid();
        var res = await setup.SaveProfileAsync(new AerSetup.ProfileInput(MappingEnabled, ReceiptDelayMinutes, SignSubmit ?? "", SignComplete ?? "", SignVoid ?? "",
            Approvers, Groups));
        if (!res.Ok)
        {
            Errors.AddRange(res.Errors);
            await LoadAsync(fillInputs: false);
            return Page();
        }
        TempData["Toast"] = "Application profile saved.";
        return Redirect("/quality/aer/setup");
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (!Allowed) return Forbid();
        var res = await setup.ImportProductCodesAsync(Csv);
        if (!res.Ok)
        {
            Errors.AddRange(res.Errors);
            await LoadAsync(fillInputs: true);
            return Page();
        }
        TempData["Toast"] = $"Product codes imported: {res.Created} new, {res.Updated} updated.";
        return Redirect("/quality/aer/setup#product-codes");
    }

    private async Task LoadAsync(bool fillInputs)
    {
        Profile = await setup.ProfileAsync();
        Mappings = await setup.MappingsAsync();
        ProductCodes = await setup.ProductCodesAsync();
        Emdr = await profiles.CurrentAsync();
        if (fillInputs && Profile != null)
        {
            MappingEnabled = Profile.MappingEnabled;
            ReceiptDelayMinutes = Profile.ReceiptDelayMinutes;
            SignSubmit = Profile.SignSubmit;
            SignComplete = Profile.SignComplete;
            SignVoid = Profile.SignVoid;
            Approvers = [.. Profile.DefaultApprovers];
            Groups = [.. Profile.MappedGroups];
        }
    }

    public string LocationLabel(string key) => cfg.Current.Keyword("quality_site", key)?.Label ?? key;
}
