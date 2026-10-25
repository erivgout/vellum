using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Modules.Integrations.SharePoint;

namespace Vellum.Areas.Admin.Pages.Integrations.SharePoint;

public sealed class ProfileForm
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public SpConnection Connection { get; set; } = SpConnection.Graph;
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public SpAuthMethod AuthMethod { get; set; } = SpAuthMethod.Certificate;
    public string? Secret { get; set; }
    public string? CertificatePassword { get; set; }
    public List<int> NotifyUserIds { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public bool SimulateOutage { get; set; }
}

public sealed record LibraryFieldsVm(SharePointLibrary Lib, SpConnection Connection, bool IsNew = false);

/// <summary>A SharePoint profile (INT-301): connection, credentials (write-only), notify list, libraries and the connection test.</summary>
public class ProfileModel(VellumDbContext db, SharePointAdminService admin, SharePointClientFactory factory) : AdminPageModel
{
    [BindProperty] public ProfileForm Input { get; set; } = new();
    [BindProperty] public LibraryInput Lib { get; set; } = new();

    public SharePointProfile? Profile { get; private set; }
    public bool IsNew => Profile == null;
    public List<(SharePointRule Rule, string Form)> Rules { get; private set; } = [];
    public int CurrentFiles { get; private set; }
    public SpTestResult? Test { get; private set; }
    public List<string> MappingProblems { get; private set; } = [];
    public int? EditLibraryId { get; private set; }
    public string LocalRoot => factory.LocalRoot;

    private async Task<bool> LoadAsync(string id, bool fillInput)
    {
        if (id == "new") { Profile = null; return true; }
        if (!int.TryParse(id, out var pid)) return false;
        Profile = await db.Set<SharePointProfile>().AsNoTracking().Include(p => p.Libraries).FirstOrDefaultAsync(p => p.Id == pid);
        if (Profile == null) return false;
        var rules = await db.Set<SharePointRule>().AsNoTracking().Where(r => r.ProfileId == pid).OrderBy(r => r.Name).ToListAsync();
        var cfg = HttpContext.RequestServices.GetRequiredService<Engine.ConfigCache>().Current;
        Rules = rules.Select(r => (r, cfg.FormsByKey.GetValueOrDefault(r.FormKey)?.Name ?? r.FormKey)).ToList();
        CurrentFiles = await db.Set<SharePointPublication>().CountAsync(p => p.ProfileId == pid && p.Status == SpPublicationStatus.Published);
        if (fillInput)
            Input = new ProfileForm
            {
                Name = Profile.Name, Host = Profile.Host, Connection = Profile.Connection, TenantId = Profile.TenantId, ClientId = Profile.ClientId,
                AuthMethod = Profile.AuthMethod, NotifyUserIds = Profile.NotifyUserIds, Enabled = Profile.Enabled, SimulateOutage = Profile.SimulateOutage,
            };
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string id, int? library)
    {
        if (!await LoadAsync(id, fillInput: true)) return NotFound();
        if (IsNew) Input = new ProfileForm { NotifyUserIds = [Me.Id] };
        EditLibraryId = library;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string id, IFormFile? certificate)
    {
        int? pid = id == "new" ? null : int.TryParse(id, out var p) ? p : -1;
        byte[]? cert = null;
        if (certificate is { Length: > 0 })
        {
            if (certificate.Length > 64 * 1024) Errors.Add("The certificate file is too large; upload the .pfx (PKCS#12) file only.");
            else
            {
                using var ms = new MemoryStream();
                await certificate.CopyToAsync(ms);
                cert = ms.ToArray();
            }
        }
        if (Errors.Count == 0)
        {
            var res = await admin.SaveProfileAsync(pid, new ProfileInput
            {
                Name = Input.Name, Host = Input.Host, Connection = Input.Connection, TenantId = Input.TenantId, ClientId = Input.ClientId,
                AuthMethod = Input.AuthMethod, Secret = Input.Secret, Certificate = cert, CertificatePassword = Input.CertificatePassword,
                NotifyUserIds = Input.NotifyUserIds, Enabled = Input.Enabled, SimulateOutage = Input.SimulateOutage,
            });
            if (res.Ok) return Done(res, $"/admin/integrations/sharepoint/profiles/{res.Id}");
            Errors.AddRange(res.Errors);
        }
        if (!await LoadAsync(id, fillInput: false)) return NotFound();
        Input.Secret = null;
        Input.CertificatePassword = null;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var res = await admin.DeleteProfileAsync(id);
        return Done(res, res.Ok ? "/admin/integrations/sharepoint" : $"/admin/integrations/sharepoint/profiles/{id}");
    }

    public async Task<IActionResult> OnPostLibraryAsync(int id, int? libraryId)
    {
        var res = await admin.SaveLibraryAsync(id, libraryId, Lib);
        if (res.Ok) return Done(res, $"/admin/integrations/sharepoint/profiles/{id}#libraries");
        Errors.AddRange(res.Errors);
        if (!await LoadAsync(id.ToString(), fillInput: true)) return NotFound();
        EditLibraryId = libraryId ?? 0;
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveLibraryAsync(int id, int libraryId) =>
        Done(await admin.DeleteLibraryAsync(id, libraryId), $"/admin/integrations/sharepoint/profiles/{id}#libraries");

    public async Task<IActionResult> OnPostTestAsync(int id)
    {
        var (res, test, problems) = await admin.TestProfileAsync(id);
        if (!await LoadAsync(id.ToString(), fillInput: true)) return NotFound();
        Test = test ?? new SpTestResult(false, res.Error, []);
        MappingProblems = problems;
        if (test == null) Errors.AddRange(res.Errors);
        return Page();
    }
}
