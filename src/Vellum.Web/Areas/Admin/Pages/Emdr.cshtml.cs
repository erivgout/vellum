using System.Text;
using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Data;
using Vellum.Modules.AdverseEvents;

namespace Vellum.Areas.Admin.Pages;

/// <summary>
/// INT-902: the eMDR gateway profile of this environment. Environment-bound and never promoted. Certificate fields reject
/// ( + &lt; &gt; ; " / \; generating a certificate warns that submissions fail until the FDA has it; secrets are encrypted with
/// Data Protection and never shown again.
/// </summary>
public class EmdrModel(EmdrProfileService profiles, EmdrGateways gateways, AuditService audit, VellumDbContext db) : AdminPageModel
{
    public EmdrProfile? Profile { get; private set; }
    public List<EmdrProfile> Foreign { get; private set; } = [];
    public SystemSettings Settings { get; private set; } = null!;
    public IReadOnlyList<IEmdrGateway> Gateways => gateways.All;
    public CertificateDetails? Ours { get; private set; }
    public CertificateDetails? Fda { get; private set; }
    public bool JustGenerated { get; private set; }

    [BindProperty] public string AccountType { get; set; } = "test";
    [BindProperty] public string SenderOrganization { get; set; } = "";
    [BindProperty] public string SenderRoutingId { get; set; } = "";
    [BindProperty] public string SenderEmail { get; set; } = "";
    [BindProperty] public string? AcknowledgementUrl { get; set; }
    [BindProperty] public string? FdaRoutingId { get; set; }
    [BindProperty] public string? EsgUrl { get; set; }
    [BindProperty] public string? SubmissionCenter { get; set; }
    [BindProperty] public string Gateway { get; set; } = "manual";
    [BindProperty] public string SimulatedOutcome { get; set; } = "accept";

    [BindProperty] public string? CertCommonName { get; set; }
    [BindProperty] public string? CertOrganization { get; set; }
    [BindProperty] public string? CertOrganizationalUnit { get; set; }
    [BindProperty] public string? CertLocality { get; set; }
    [BindProperty] public string? CertState { get; set; }
    [BindProperty] public string? CertCountry { get; set; }
    [BindProperty] public string? CertEmail { get; set; }
    [BindProperty] public bool AcknowledgeCertificateWarning { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync(fill: true);
        JustGenerated = TempData["EmdrCertGenerated"] is true;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var res = await profiles.SaveAsync(new EmdrProfileService.ProfileInput(AccountType, SenderOrganization ?? "", SenderRoutingId ?? "", SenderEmail ?? "",
            AcknowledgementUrl, FdaRoutingId, EsgUrl, SubmissionCenter, Gateway, SimulatedOutcome));
        return await AfterAsync(res, keepProfileInputs: true);
    }

    public async Task<IActionResult> OnPostCertificateAsync()
    {
        var res = await profiles.GenerateCertificateAsync(new EmdrProfileService.CertificateInput(CertCommonName, CertOrganization, CertOrganizationalUnit,
            CertLocality, CertState, CertCountry, CertEmail), AcknowledgeCertificateWarning);
        if (res.Ok) TempData["EmdrCertGenerated"] = true;
        return await AfterAsync(res, keepCertInputs: true, anchor: "certificate");
    }

    public async Task<IActionResult> OnPostConfirmFdaAsync() => await AfterAsync(await profiles.ConfirmFdaHasCertificateAsync(), anchor: "certificate");

    public async Task<IActionResult> OnPostPasswordAsync(string? password) => await AfterAsync(await profiles.SetPasswordAsync(password), anchor: "secrets");

    public async Task<IActionResult> OnPostClearPasswordAsync() => await AfterAsync(await profiles.ClearPasswordAsync(), anchor: "secrets");

    public async Task<IActionResult> OnPostFdaCertificateAsync(string? pem) => await AfterAsync(await profiles.SetFdaCertificateAsync(pem), anchor: "fda-cert");

    /// <summary>Download our public certificate (to send to the FDA ESG). Never the private key.</summary>
    public async Task<IActionResult> OnGetCertificateAsync(string? which)
    {
        var p = await profiles.CurrentAsync();
        var pem = which == "fda" ? p?.FdaCertificatePem : p?.CertPem;
        if (pem == null) return NotFound();
        audit.Global("eMDR certificate downloaded", which == "fda" ? "FDA ESG certificate" : $"Public certificate {p!.CertThumbprint?[..16]}…");
        await db.SaveChangesAsync();
        var name = which == "fda" ? "fda-esg-certificate.pem" : $"emdr-{(p!.AccountType == "production" ? "prod" : "test")}-{p.CertThumbprint?[..8].ToLowerInvariant()}.pem";
        return File(Encoding.ASCII.GetBytes(pem), "application/x-pem-file", name);
    }

    private async Task<IActionResult> AfterAsync(EmdrResult res, bool keepProfileInputs = false, bool keepCertInputs = false, string? anchor = null)
    {
        if (res.Ok) return Toast(res.Message ?? "Saved.", "/admin/emdr" + (anchor != null ? "#" + anchor : ""));
        Errors.AddRange(res.Errors);
        await LoadAsync(fill: !keepProfileInputs, fillCert: !keepCertInputs);
        return Page();
    }

    private async Task LoadAsync(bool fill, bool fillCert = true)
    {
        Settings = await profiles.SettingsAsync();
        Profile = await profiles.CurrentAsync();
        Foreign = await profiles.ForeignAsync();
        Ours = EmdrProfileService.Describe(Profile?.CertPem);
        Fda = EmdrProfileService.Describe(Profile?.FdaCertificatePem);
        var p = Profile;
        if (fill)
        {
            AccountType = p?.AccountType ?? (Settings.EnvironmentType == EnvironmentType.Production ? "production" : "test");
            SenderOrganization = p?.SenderOrganization ?? Settings.OrganizationName;
            SenderRoutingId = p?.SenderRoutingId ?? "";
            SenderEmail = p?.SenderEmail ?? "";
            AcknowledgementUrl = p?.AcknowledgementUrl;
            var d = EmdrProfileService.Defaults(AccountType);
            FdaRoutingId = p?.FdaRoutingId ?? d.RoutingId;
            EsgUrl = p?.EsgUrl ?? d.EsgUrl;
            SubmissionCenter = p?.SubmissionCenter ?? d.Center;
            Gateway = p?.Gateway ?? "manual";
            SimulatedOutcome = p?.SimulatedOutcome ?? "accept";
        }
        if (fillCert)
        {
            CertCommonName = p?.CertCommonName;
            CertOrganization = p?.CertOrganization ?? Settings.OrganizationName.Replace(",", "").Replace(".", "");
            CertOrganizationalUnit = p?.CertOrganizationalUnit ?? "Regulatory Affairs";
            CertLocality = p?.CertLocality;
            CertState = p?.CertState;
            CertCountry = p?.CertCountry ?? "US";
            CertEmail = p?.CertEmail ?? p?.SenderEmail;
        }
    }
}
