using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Modules.Admin;

namespace Vellum.Modules.Integrations.SharePoint;

public sealed class ProfileInput
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public SpConnection Connection { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public SpAuthMethod AuthMethod { get; set; }
    /// <summary>New client secret. Blank keeps the stored one.</summary>
    public string? Secret { get; set; }
    /// <summary>New certificate (.pfx bytes). Null keeps the stored one.</summary>
    public byte[]? Certificate { get; set; }
    public string? CertificatePassword { get; set; }
    public List<int> NotifyUserIds { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public bool SimulateOutage { get; set; }
}

public sealed class LibraryInput
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string RelativeUrl { get; set; } = "";
    public string? SimulatedColumns { get; set; }
}

public sealed class MappingInput
{
    public string FormKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<(string Source, string Column)> Entries { get; set; } = [];
}

public sealed class RuleInput
{
    public string Name { get; set; } = "";
    public string FormKey { get; set; } = "";
    public string PhaseKey { get; set; } = "";
    public bool OnEnter { get; set; } = true;
    public bool OnSave { get; set; }
    public int ProfileId { get; set; }
    public int LibraryId { get; set; }
    public List<string> FieldKeys { get; set; } = [];
    public string? FolderTemplate { get; set; }
    public string? FileNameTemplate { get; set; }
    public bool PreferRendition { get; set; } = true;
    public bool FallbackToOriginal { get; set; } = true;
    public int? MappingId { get; set; }
    public bool EffectiveOnly { get; set; } = true;
    public bool KeepVersionHistory { get; set; } = true;
    public SpWithdrawAction WithdrawAction { get; set; }
    public string? ObsoleteFolder { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed record MappingCheck(bool Ok, string Summary, List<string> Problems, IReadOnlyList<SpColumn>? Columns);

/// <summary>
/// Administration of SharePoint publishing: profiles and libraries (INT-301), mapping profiles (INT-302), publishing rules
/// (DSN-602, INT-304), connection tests and the publication log (ADM-1005). Administrators only; every change is written
/// to the configuration audit (DSN-1202) with a before/after snapshot that never includes a credential.
/// </summary>
[ScopedService]
public sealed partial class SharePointAdminService(
    VellumDbContext db, CurrentUser me, AuditService audit, ConfigCache cfg, SharePointSecrets secrets, SharePointClientFactory factory,
    SharePointQueue queue, TaskAdminService tasks, UserDirectory dir)
{
    private DbSet<SharePointProfile> Profiles => db.Set<SharePointProfile>();
    private DbSet<SharePointLibrary> Libraries => db.Set<SharePointLibrary>();
    private DbSet<SharePointMapping> Mappings => db.Set<SharePointMapping>();
    private DbSet<SharePointRule> Rules => db.Set<SharePointRule>();
    private DbSet<SharePointJob> Jobs => db.Set<SharePointJob>();

    private ConfigModel Cfg => cfg.Current;

    // ------------------------------------------------------------------ snapshots (no secrets, ever)

    public static string Snap(SharePointProfile p) => AdminJson.Of(new
    {
        p.Name, p.Host, connection = p.Connection, p.TenantId, p.ClientId, authMethod = p.AuthMethod,
        clientSecret = p.SecretProtected != null ? $"set {p.SecretSetAt:yyyy-MM-dd HH:mm} UTC" : "not set",
        certificate = p.CertificateThumbprint != null ? $"{p.CertificateThumbprint} (expires {p.CertificateExpiresAt:yyyy-MM-dd})" : "not set",
        notify = p.NotifyUserIds.Order().ToList(), p.Enabled, p.SimulateOutage,
        libraries = p.Libraries.OrderBy(l => l.Key).Select(l => new { key = l.Key, l.Name, l.RelativeUrl, l.SimulatedColumns }).ToList(),
    });

    public static string Snap(SharePointMapping m) => AdminJson.Of(new
    {
        m.Name, m.FormKey, m.Description,
        entries = m.Entries.OrderBy(e => e.Order).Select(e => new { key = e.Column, e.Source }).ToList(),
    });

    public static string Snap(SharePointRule r) => AdminJson.Of(new
    {
        r.Name, r.FormKey, r.PhaseKey, r.OnEnter, r.OnSave, r.ProfileId, r.LibraryId, fields = r.FieldKeys, r.FolderTemplate, r.FileNameTemplate,
        r.PreferRendition, r.FallbackToOriginal, r.MappingId, r.EffectiveOnly, r.KeepVersionHistory, withdrawAction = r.WithdrawAction, r.ObsoleteFolder, r.Enabled,
    });

    // ------------------------------------------------------------------ profiles (INT-301)

    [GeneratedRegex(@"^(?=.{3,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    public static string NormalizeHost(string? host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        if (h.StartsWith("https://")) h = h[8..];
        if (h.StartsWith("http://")) h = h[7..];
        var slash = h.IndexOf('/');
        return slash >= 0 ? h[..slash] : h;
    }

    public async Task<AdminResult> SaveProfileAsync(int? id, ProfileInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var errors = new List<string>();
        var host = NormalizeHost(input.Host);
        var name = input.Name.Trim();
        if (name.Length == 0) errors.Add("Give the profile a name.");
        if (name.Length > 120) errors.Add("The name can be at most 120 characters.");
        if (!HostPattern().IsMatch(host)) errors.Add("Enter the SharePoint host name only, for example contoso.sharepoint.com.");
        else if (await Profiles.AnyAsync(p => p.Host == host && p.Id != (id ?? 0))) errors.Add($"Another profile already uses {host}. Profiles are unique by host (INT-301).");
        var profile = id is { } pid ? await Profiles.Include(p => p.Libraries).FirstOrDefaultAsync(p => p.Id == pid) : null;
        if (id != null && profile == null) return AdminResult.Fail("That profile doesn't exist.");
        var notify = input.NotifyUserIds.Distinct().Where(u => dir.Get(u) is { IsActive: true }).ToList();
        if (input.NotifyUserIds.Distinct().Count() != notify.Count) errors.Add("Only active people can be notified of publishing failures.");

        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;
        if (input.Connection == SpConnection.Graph)
        {
            var tenant = (input.TenantId ?? "").Trim();
            if (!GuidPattern().IsMatch(tenant) && !HostPattern().IsMatch(tenant.ToLowerInvariant()))
                errors.Add("Enter the directory (tenant) ID: a GUID, or the tenant's domain such as contoso.onmicrosoft.com.");
            if (!GuidPattern().IsMatch((input.ClientId ?? "").Trim())) errors.Add("Enter the application (client) ID of the app registration: a GUID.");
            if (input.AuthMethod == SpAuthMethod.ClientSecret && string.IsNullOrWhiteSpace(input.Secret) && profile?.SecretProtected == null)
                errors.Add("Enter the client secret.");
            if (input.AuthMethod == SpAuthMethod.Certificate)
            {
                if (input.Certificate is { Length: > 0 } pfx)
                {
                    var (c, err) = SharePointSecrets.LoadCertificate(pfx, input.CertificatePassword);
                    if (err != null) errors.Add(err); else cert = c;
                }
                else if (profile?.CertificateProtected == null) errors.Add("Upload the certificate (.pfx with its private key).");
            }
        }
        if (errors.Count > 0) { cert?.Dispose(); return AdminResult.Fail(errors); }

        var before = profile != null ? Snap(profile) : null;
        var now = DateTime.UtcNow;
        profile ??= new SharePointProfile { CreatedAt = now };
        profile.Name = name;
        profile.Host = host;
        profile.Connection = input.Connection;
        profile.NotifyUserIds = notify;
        profile.Enabled = input.Enabled;
        profile.SimulateOutage = input.Connection == SpConnection.LocalFolder && input.SimulateOutage;
        profile.ModifiedAt = now;
        if (input.Connection == SpConnection.Graph)
        {
            profile.TenantId = input.TenantId!.Trim();
            profile.ClientId = input.ClientId!.Trim().ToLowerInvariant();
            profile.AuthMethod = input.AuthMethod;
            if (input.AuthMethod == SpAuthMethod.ClientSecret && !string.IsNullOrWhiteSpace(input.Secret))
            {
                profile.SecretProtected = secrets.Protect(input.Secret.Trim());
                profile.SecretSetAt = now;
            }
            if (cert != null)
            {
                profile.CertificateProtected = secrets.Protect(Convert.ToBase64String(input.Certificate!));
                profile.CertificatePasswordProtected = string.IsNullOrEmpty(input.CertificatePassword) ? null : secrets.Protect(input.CertificatePassword);
                profile.CertificateThumbprint = cert.Thumbprint;
                profile.CertificateSubject = cert.Subject;
                profile.CertificateExpiresAt = cert.NotAfter.ToUniversalTime();
                cert.Dispose();
            }
            // Keep only the credential the chosen method uses.
            if (input.AuthMethod == SpAuthMethod.ClientSecret) { profile.CertificateProtected = profile.CertificatePasswordProtected = null; profile.CertificateThumbprint = profile.CertificateSubject = null; profile.CertificateExpiresAt = null; }
            else { profile.SecretProtected = null; profile.SecretSetAt = null; }
        }
        else
        {
            profile.TenantId = profile.ClientId = profile.SecretProtected = profile.CertificateProtected = profile.CertificatePasswordProtected = null;
            profile.CertificateThumbprint = profile.CertificateSubject = null;
            profile.SecretSetAt = profile.CertificateExpiresAt = null;
        }
        if (profile.Id == 0) Profiles.Add(profile);
        await db.SaveChangesAsync();
        GraphCache.Shared.Forget(profile.Id);
        audit.Global(before == null ? "SharePoint profile created" : "SharePoint profile changed", $"{profile.Name} ({profile.Host})");
        audit.Config("SharePointProfile", profile.Host, before == null ? "Created" : "Changed", before, Snap(profile));
        await db.SaveChangesAsync();
        return AdminResult.Success(before == null ? $"Profile {profile.Name} created. Add its libraries, then test the connection." : $"Profile {profile.Name} saved.", profile.Id);
    }

    public async Task<AdminResult> DeleteProfileAsync(int id)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var profile = await Profiles.Include(p => p.Libraries).FirstOrDefaultAsync(p => p.Id == id);
        if (profile == null) return AdminResult.Fail("That profile doesn't exist.");
        var used = await Rules.Where(r => r.ProfileId == id).Select(r => r.Name).ToListAsync();
        if (used.Count > 0) return AdminResult.Fail($"Rules still publish through this profile: {string.Join(", ", used)}. Delete or move them first.");
        if (await db.Set<SharePointPublication>().AnyAsync(p => p.ProfileId == id && p.Status == SpPublicationStatus.Published))
            return AdminResult.Fail("Files published through this profile are still current. Withdraw them (or let their documents be superseded) before deleting the profile, so no uncontrolled copies are left behind (SAF-021).");
        var before = Snap(profile);
        // Module foreign keys never cascade (VellumDbContext forces Restrict), so the libraries go first.
        Libraries.RemoveRange(profile.Libraries);
        Profiles.Remove(profile);
        audit.Global("SharePoint profile deleted", $"{profile.Name} ({profile.Host})");
        audit.Config("SharePointProfile", profile.Host, "Deleted", before, null);
        await db.SaveChangesAsync();
        GraphCache.Shared.Forget(id);
        return AdminResult.Success($"Profile {profile.Name} deleted.");
    }

    // ------------------------------------------------------------------ libraries

    public async Task<AdminResult> SaveLibraryAsync(int profileId, int? libraryId, LibraryInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var profile = await Profiles.Include(p => p.Libraries).FirstOrDefaultAsync(p => p.Id == profileId);
        if (profile == null) return AdminResult.Fail("That profile doesn't exist.");
        var lib = libraryId is { } lid ? profile.Libraries.FirstOrDefault(l => l.Id == lid) : null;
        if (libraryId != null && lib == null) return AdminResult.Fail("That library doesn't exist.");
        var errors = new List<string>();
        var key = (input.Key ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) key = AdminText.Slug(input.Name);
        if (!AdminText.KeyPattern.IsMatch(key)) errors.Add("The identifier can use lower-case letters, digits, hyphens and underscores.");
        else if (profile.Libraries.Any(l => l.Key == key && l.Id != (libraryId ?? 0))) errors.Add($"This profile already has a library \"{key}\".");
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) errors.Add("Give the library a name.");
        var url = SharePointNaming.NormalizeRelativeUrl(input.RelativeUrl);
        if (url == "/") errors.Add("Enter the library's server-relative URL, for example /sites/QMS/Controlled Documents.");
        else if (url.Any(c => "\"*:<>?\\|#%".Contains(c))) errors.Add("The relative URL can't contain \" * : < > ? \\ | # or %.");
        var columns = string.IsNullOrWhiteSpace(input.SimulatedColumns) ? null : input.SimulatedColumns.Trim();
        if (columns != null)
        {
            var bad = LocalFolderSharePointClient.Columns(new SharePointLibrary { SimulatedColumns = columns })!.Where(c => !SharePointMetadata.IsValidColumnName(c.Name)).Select(c => c.Name).ToList();
            if (bad.Count > 0) errors.Add($"Column internal names use letters, digits and underscores only: {string.Join(", ", bad)}.");
        }
        if (errors.Count > 0) return AdminResult.Fail(errors);

        var before = Snap(profile);
        lib ??= new SharePointLibrary { ProfileId = profileId };
        lib.Key = key;
        lib.Name = name;
        lib.RelativeUrl = url;
        lib.SimulatedColumns = profile.Connection == SpConnection.LocalFolder ? columns : null;
        if (lib.Id == 0) profile.Libraries.Add(lib);
        profile.ModifiedAt = DateTime.UtcNow;
        audit.Global(libraryId == null ? "SharePoint library added" : "SharePoint library changed", $"{name} ({url}) on {profile.Host}");
        audit.Config("SharePointProfile", profile.Host, libraryId == null ? "Library added" : "Library changed", before, Snap(profile));
        await db.SaveChangesAsync();
        GraphCache.Shared.Forget(profileId);
        return AdminResult.Success(libraryId == null ? $"Library {name} added." : $"Library {name} saved.", lib.Id);
    }

    public async Task<AdminResult> DeleteLibraryAsync(int profileId, int libraryId)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var profile = await Profiles.Include(p => p.Libraries).FirstOrDefaultAsync(p => p.Id == profileId);
        var lib = profile?.Libraries.FirstOrDefault(l => l.Id == libraryId);
        if (profile == null || lib == null) return AdminResult.Fail("That library doesn't exist.");
        var used = await Rules.Where(r => r.LibraryId == libraryId).Select(r => r.Name).ToListAsync();
        if (used.Count > 0) return AdminResult.Fail($"Rules still publish to {lib.Name}: {string.Join(", ", used)}.");
        if (await db.Set<SharePointPublication>().AnyAsync(p => p.LibraryId == libraryId && p.Status == SpPublicationStatus.Published))
            return AdminResult.Fail($"{lib.Name} still holds current published files. They must be withdrawn first (SAF-021).");
        var before = Snap(profile);
        profile.Libraries.Remove(lib);
        Libraries.Remove(lib);
        audit.Global("SharePoint library removed", $"{lib.Name} ({lib.RelativeUrl}) on {profile.Host}");
        audit.Config("SharePointProfile", profile.Host, "Library removed", before, Snap(profile));
        await db.SaveChangesAsync();
        GraphCache.Shared.Forget(profileId);
        return AdminResult.Success($"Library {lib.Name} removed.");
    }

    /// <summary>"Test connection": sign in, resolve every library and check the columns of every mapping used with it.</summary>
    public async Task<(AdminResult Result, SpTestResult? Test, List<string> MappingProblems)> TestProfileAsync(int id)
    {
        if (!me.IsAdmin) return (AdminResult.Fail(AdminResult.NotAdmin), null, []);
        var profile = await Profiles.Include(p => p.Libraries).FirstOrDefaultAsync(p => p.Id == id);
        if (profile == null) return (AdminResult.Fail("That profile doesn't exist."), null, []);
        SpTestResult test;
        var problems = new List<string>();
        try
        {
            var client = factory.For(profile);
            test = await client.TestAsync(profile.Libraries.OrderBy(l => l.Name).ToList());
            if (test.Ok)
            {
                foreach (var rule in await Rules.AsNoTracking().Where(r => r.ProfileId == id && r.MappingId != null).ToListAsync())
                {
                    var lib = profile.Libraries.FirstOrDefault(l => l.Id == rule.LibraryId);
                    var mapping = await Mappings.AsNoTracking().Include(m => m.Entries).FirstOrDefaultAsync(m => m.Id == rule.MappingId);
                    if (lib == null || mapping == null) continue;
                    var columns = await client.GetColumnsAsync(lib);
                    problems.AddRange(SharePointMetadata.ColumnProblems(mapping.Entries.Select(e => e.Column), columns).Select(p => $"{mapping.Name} → {lib.Name}: {p}"));
                }
            }
        }
        catch (SharePointException ex)
        {
            test = new SpTestResult(false, $"{ex.Code}: {ex.Message}", []);
        }
        var ok = test.Ok && problems.Count == 0;
        profile.LastTestAt = DateTime.UtcNow;
        profile.LastTestOk = ok;
        var summary = test.Summary + (problems.Count > 0 ? $" {problems.Count} mapping problem(s)." : "");
        profile.LastTestSummary = summary.Length > 2000 ? summary[..2000] : summary;
        audit.Global("SharePoint connection tested", $"{profile.Name} ({profile.Host}): {(ok ? "OK" : "failed")}. {profile.LastTestSummary}");
        await db.SaveChangesAsync();
        return (ok ? AdminResult.Success("Connection OK. " + test.Summary) : AdminResult.Fail(summary), test, problems);
    }

    // ------------------------------------------------------------------ mapping profiles (INT-302)

    public async Task<AdminResult> SaveMappingAsync(int? id, MappingInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var errors = new List<string>();
        if (!Cfg.FormsByKey.TryGetValue(input.FormKey ?? "", out var form)) return AdminResult.Fail("Choose the form this mapping is for.");
        var mapping = id is { } mid ? await Mappings.Include(m => m.Entries).FirstOrDefaultAsync(m => m.Id == mid) : null;
        if (id != null && mapping == null) return AdminResult.Fail("That mapping profile doesn't exist.");
        if (mapping != null && mapping.FormKey != form.Key && await Rules.AnyAsync(r => r.MappingId == mapping.Id))
            errors.Add("Rules use this mapping, so its form can't change.");
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) errors.Add("Give the mapping profile a name.");
        var entries = input.Entries.Select(e => (Source: (e.Source ?? "").Trim(), Column: (e.Column ?? "").Trim()))
            .Where(e => e.Source.Length > 0 || e.Column.Length > 0).ToList();
        if (entries.Count == 0) errors.Add("Map at least one column.");
        for (var i = 0; i < entries.Count; i++)
        {
            var (source, column) = entries[i];
            var row = $"Row {i + 1}: ";
            if (!SharePointMetadata.IsValidColumnName(column)) errors.Add(row + "enter the column's internal name (letters, digits and underscores; spaces are _x0020_).");
            else if (SharePointMetadata.IsSystemColumn(column)) errors.Add(row + $"\"{column}\" is a SharePoint system column and can't be mapped (INT-302).");
            if (SharePointMetadata.SourceError(source, form) is { } err) errors.Add(row + err);
        }
        foreach (var dup in entries.GroupBy(e => e.Column, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1))
            errors.Add($"Column \"{dup.Key}\" is mapped more than once; each column takes one value.");
        if (errors.Count > 0) return AdminResult.Fail(errors);

        var before = mapping != null ? Snap(mapping) : null;
        var now = DateTime.UtcNow;
        mapping ??= new SharePointMapping { CreatedAt = now };
        mapping.Name = name;
        mapping.FormKey = form.Key;
        mapping.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        mapping.ModifiedAt = now;
        db.RemoveRange(mapping.Entries);
        mapping.Entries = entries.Select((e, i) => new SharePointMappingEntry { Source = e.Source, Column = e.Column, Order = i }).ToList();
        if (mapping.Id == 0) Mappings.Add(mapping);
        await db.SaveChangesAsync();
        audit.Global(before == null ? "SharePoint mapping created" : "SharePoint mapping changed", $"{mapping.Name} ({form.Name})");
        audit.Config("SharePointMapping", $"{form.Key}:{mapping.Id}", before == null ? "Created" : "Changed", before, Snap(mapping));
        await db.SaveChangesAsync();
        return AdminResult.Success($"Mapping profile {mapping.Name} saved.", mapping.Id);
    }

    public async Task<AdminResult> DeleteMappingAsync(int id)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var mapping = await Mappings.Include(m => m.Entries).FirstOrDefaultAsync(m => m.Id == id);
        if (mapping == null) return AdminResult.Fail("That mapping profile doesn't exist.");
        var used = await Rules.Where(r => r.MappingId == id).Select(r => r.Name).ToListAsync();
        if (used.Count > 0) return AdminResult.Fail($"Rules use this mapping: {string.Join(", ", used)}. Choose another mapping for them first.");
        var before = Snap(mapping);
        db.RemoveRange(mapping.Entries);
        Mappings.Remove(mapping);
        audit.Global("SharePoint mapping deleted", mapping.Name);
        audit.Config("SharePointMapping", $"{mapping.FormKey}:{mapping.Id}", "Deleted", before, null);
        await db.SaveChangesAsync();
        return AdminResult.Success($"Mapping profile {mapping.Name} deleted.");
    }

    /// <summary>Check a mapping's columns against a library's live schema (INT-302: mapped columns must exist).</summary>
    public async Task<MappingCheck> CheckMappingAsync(int mappingId, int libraryId)
    {
        if (!me.IsAdmin) return new MappingCheck(false, AdminResult.NotAdmin, [], null);
        var mapping = await Mappings.AsNoTracking().Include(m => m.Entries).FirstOrDefaultAsync(m => m.Id == mappingId);
        var lib = await Libraries.AsNoTracking().Include(l => l.Profile).FirstOrDefaultAsync(l => l.Id == libraryId);
        if (mapping == null || lib == null) return new MappingCheck(false, "Choose a library to check against.", [], null);
        try
        {
            var columns = await factory.For(lib.Profile).GetColumnsAsync(lib);
            var problems = SharePointMetadata.ColumnProblems(mapping.Entries.Select(e => e.Column), columns);
            var summary = columns == null
                ? $"{lib.Name} has no simulated columns, so any column is accepted."
                : problems.Count == 0 ? $"All {mapping.Entries.Count} mapped columns exist in {lib.Name}." : $"{problems.Count} problem(s) against {lib.Name}.";
            return new MappingCheck(problems.Count == 0, summary, problems, columns);
        }
        catch (SharePointException ex)
        {
            return new MappingCheck(false, $"{ex.Code}: {ex.Message}", [], null);
        }
    }

    // ------------------------------------------------------------------ rules (DSN-602, INT-304)

    public FormDefinition? Form(string? key) => key != null && Cfg.FormsByKey.TryGetValue(key, out var f) ? f : null;

    public static IEnumerable<FieldDefinition> AttachmentFields(FormDefinition form) =>
        form.Fields.Where(f => f.Type == FieldType.Attachment && form.Sections.FirstOrDefault(s => s.Key == f.SectionKey)?.IsSubform != true);

    public async Task<AdminResult> SaveRuleAsync(int? id, RuleInput input)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var errors = new List<string>();
        var form = Form(input.FormKey);
        if (form == null) return AdminResult.Fail("Choose the form to publish from.");
        var rule = id is { } rid ? await Rules.FirstOrDefaultAsync(r => r.Id == rid) : null;
        if (id != null && rule == null) return AdminResult.Fail("That rule doesn't exist.");
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) errors.Add("Give the rule a name.");

        var phaseKey = (input.PhaseKey ?? "").Trim();
        if (form.IsNonWorkflow) phaseKey = "*";
        else if (!form.Workflows.SelectMany(w => w.Phases).Any(p => p.Key == phaseKey)) errors.Add($"Choose a phase of {form.Name}.");
        if (!form.IsNonWorkflow && !input.OnEnter && !input.OnSave) errors.Add("Choose when to publish: when records enter the phase, when they're saved in it, or both.");

        var attachmentFields = AttachmentFields(form).Select(f => f.Key).ToHashSet();
        var fields = input.FieldKeys.Distinct().ToList();
        if (fields.Count == 0) errors.Add("Choose at least one attachment field to publish.");
        foreach (var f in fields.Where(f => !attachmentFields.Contains(f))) errors.Add($"\"{f}\" isn't a main-form attachment field of {form.Name}.");

        var profile = await Profiles.AsNoTracking().Include(p => p.Libraries).FirstOrDefaultAsync(p => p.Id == input.ProfileId);
        if (profile == null) errors.Add("Choose the SharePoint profile.");
        else if (profile.Libraries.All(l => l.Id != input.LibraryId)) errors.Add($"Choose one of {profile.Name}'s libraries.");
        if (input.MappingId is { } mid && !await Mappings.AnyAsync(m => m.Id == mid && m.FormKey == form.Key)) errors.Add($"The mapping profile must be one for {form.Name}.");

        foreach (var (label, template) in new[] { ("folder", input.FolderTemplate), ("file name", input.FileNameTemplate) })
        {
            var unknown = SharePointNaming.TokensOf(template).Where(t => SharePointNaming.SystemTokens.All(s => s.Token != t) && form.Fields.All(f => f.Key != t)).ToList();
            if (unknown.Count > 0) errors.Add($"Unknown token{(unknown.Count > 1 ? "s" : "")} in the {label}: {string.Join(", ", unknown.Select(u => "{" + u + "}"))}.");
        }
        if ((input.FolderTemplate ?? "").Length > 400 || (input.FileNameTemplate ?? "").Length > 200) errors.Add("The folder or file name is too long.");
        if (errors.Count > 0) return AdminResult.Fail(errors);

        var before = rule != null ? Snap(rule) : null;
        var now = DateTime.UtcNow;
        rule ??= new SharePointRule { CreatedAt = now };
        rule.Name = name;
        rule.FormKey = form.Key;
        rule.PhaseKey = phaseKey;
        rule.OnEnter = form.IsNonWorkflow || input.OnEnter;
        rule.OnSave = form.IsNonWorkflow || input.OnSave;
        rule.ProfileId = input.ProfileId;
        rule.LibraryId = input.LibraryId;
        rule.FieldKeys = fields;
        rule.FolderTemplate = (input.FolderTemplate ?? "").Trim().Trim('/');
        rule.FileNameTemplate = string.IsNullOrWhiteSpace(input.FileNameTemplate) ? null : input.FileNameTemplate.Trim();
        rule.PreferRendition = input.PreferRendition;
        rule.FallbackToOriginal = input.FallbackToOriginal;
        rule.MappingId = input.MappingId;
        rule.EffectiveOnly = input.EffectiveOnly;
        rule.KeepVersionHistory = input.KeepVersionHistory;
        rule.WithdrawAction = input.WithdrawAction;
        rule.ObsoleteFolder = input.WithdrawAction == SpWithdrawAction.MarkObsolete && !string.IsNullOrWhiteSpace(input.ObsoleteFolder)
            ? SharePointNaming.NormalizeFolder(input.ObsoleteFolder) : null;
        rule.Enabled = input.Enabled;
        rule.ModifiedAt = now;
        if (rule.Id == 0) Rules.Add(rule);
        await db.SaveChangesAsync();
        audit.Global(before == null ? "SharePoint publishing rule created" : "SharePoint publishing rule changed", $"{rule.Name}: {form.Name} › {PhaseLabel(form, rule.PhaseKey)}");
        audit.Config("SharePointRule", $"{form.Key}:{rule.PhaseKey}:{rule.Id}", before == null ? "Created" : "Changed", before, Snap(rule));
        await db.SaveChangesAsync();
        SharePointRuleCache.Invalidate();
        var warn = form.RevisionControlled && !rule.EffectiveOnly ? " Pending revisions will be published too: they never overwrite an effective revision's file (INV-163)." : "";
        return AdminResult.Success($"Rule {rule.Name} saved.{warn}", rule.Id);
    }

    public static string PhaseLabel(FormDefinition form, string phaseKey) =>
        phaseKey == "*" ? "Any save" : form.Workflows.SelectMany(w => w.Phases).FirstOrDefault(p => p.Key == phaseKey)?.Name ?? phaseKey;

    public async Task<AdminResult> SetRuleEnabledAsync(int id, bool enabled)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var rule = await Rules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule == null) return AdminResult.Fail("That rule doesn't exist.");
        if (rule.Enabled == enabled) return AdminResult.Success("No changes.");
        var before = Snap(rule);
        rule.Enabled = enabled;
        rule.ModifiedAt = DateTime.UtcNow;
        audit.Global(enabled ? "SharePoint publishing rule enabled" : "SharePoint publishing rule disabled", rule.Name);
        audit.Config("SharePointRule", $"{rule.FormKey}:{rule.PhaseKey}:{rule.Id}", enabled ? "Enabled" : "Disabled", before, Snap(rule));
        await db.SaveChangesAsync();
        SharePointRuleCache.Invalidate();
        return AdminResult.Success(enabled ? $"{rule.Name} is enabled." : $"{rule.Name} is disabled. Records won't be published by it until you enable it again; files already published stay.");
    }

    public async Task<AdminResult> DeleteRuleAsync(int id)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var rule = await Rules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule == null) return AdminResult.Fail("That rule doesn't exist.");
        if (await db.Set<SharePointPublication>().AnyAsync(p => p.RuleId == id && p.Status == SpPublicationStatus.Published))
            return AdminResult.Fail("Files published by this rule are still current. Disable the rule instead; its files are still withdrawn when their documents are superseded or obsoleted (SAF-021).");
        var before = Snap(rule);
        Rules.Remove(rule);
        audit.Global("SharePoint publishing rule deleted", rule.Name);
        audit.Config("SharePointRule", $"{rule.FormKey}:{rule.PhaseKey}:{rule.Id}", "Deleted", before, null);
        await db.SaveChangesAsync();
        SharePointRuleCache.Invalidate();
        return AdminResult.Success($"Rule {rule.Name} deleted.");
    }

    /// <summary>Queue every record currently in the rule's phase (go-live, or after changing the folder or mapping).</summary>
    public async Task<AdminResult> PublishExistingAsync(int id)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var rule = await Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        var form = rule != null ? Form(rule.FormKey) : null;
        if (rule == null || form == null) return AdminResult.Fail("That rule doesn't exist.");
        if (!rule.Enabled) return AdminResult.Fail("Enable the rule first.");
        var q = db.Records.AsNoTracking().Where(r => r.FormId == form.Id && !r.IsArchived);
        if (rule.PhaseKey != "*")
        {
            var phaseIds = form.Workflows.SelectMany(w => w.Phases).Where(p => p.Key == rule.PhaseKey).Select(p => (int?)p.Id).ToList();
            q = q.Where(r => phaseIds.Contains(r.PhaseId));
        }
        if (form.RevisionControlled && rule.EffectiveOnly) q = q.Where(r => r.IsEffective);
        var ids = await q.OrderBy(r => r.Number).Select(r => r.Id).Take(2000).ToListAsync();
        foreach (var rid in ids) await queue.PublishAsync(rid, rule.Id, "Published existing records (administrator)");
        audit.Global("SharePoint: existing records queued", $"{rule.Name}: {ids.Count} record(s)");
        await db.SaveChangesAsync();
        return AdminResult.Success(ids.Count == 0 ? "No records are in that phase right now." : $"Queued {Infrastructure.Ui.Ui.Plural(ids.Count, "record")} for publishing. They're processed within a minute, or use Process queue now.");
    }

    // ------------------------------------------------------------------ log (ADM-1005)

    public async Task<AdminResult> RetryAsync(long jobId)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var job = await Jobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null) return AdminResult.Fail("That queue item doesn't exist.");
        if (job.Status is not (SpJobStatus.Failed or SpJobStatus.Skipped or SpJobStatus.Cancelled)) return AdminResult.Fail("Only failed, skipped or cancelled items can be retried.");
        Requeue(job);
        audit.Global("SharePoint publication retried", $"Queue item #{job.Id} ({job.Kind}) for record {await NumberAsync(job.RecordId)}");
        await db.SaveChangesAsync();
        return AdminResult.Success($"Queue item #{job.Id} will be retried within a minute.");
    }

    public async Task<AdminResult> RetryAllFailedAsync()
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var failed = await Jobs.Where(j => j.Status == SpJobStatus.Failed).ToListAsync();
        if (failed.Count == 0) return AdminResult.Success("Nothing has failed.");
        foreach (var j in failed) Requeue(j);
        audit.Global("SharePoint publications retried", $"{failed.Count} failed queue item(s)");
        await db.SaveChangesAsync();
        return AdminResult.Success($"{Infrastructure.Ui.Ui.Plural(failed.Count, "failed item")} will be retried.");
    }

    public async Task<AdminResult> CancelAsync(long jobId)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var job = await Jobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null) return AdminResult.Fail("That queue item doesn't exist.");
        if (job.Status != SpJobStatus.Pending) return AdminResult.Fail("Only pending items can be cancelled.");
        if (job.Kind == SpJobKind.Withdraw) return AdminResult.Fail("Withdrawals can't be cancelled: superseded and obsolete files must leave SharePoint (SAF-021).");
        job.Status = SpJobStatus.Cancelled;
        job.CompletedAt = DateTime.UtcNow;
        job.Result = $"Cancelled by {me.Name}";
        audit.Global("SharePoint publication cancelled", $"Queue item #{job.Id} for record {await NumberAsync(job.RecordId)}");
        await db.SaveChangesAsync();
        return AdminResult.Success($"Queue item #{job.Id} cancelled.");
    }

    private void Requeue(SharePointJob job)
    {
        job.Status = SpJobStatus.Pending;
        job.Attempts = 0;
        job.MaxAttempts = factory.MaxAttempts;
        job.NextAttemptAt = DateTime.UtcNow;
        job.CompletedAt = null;
        job.Trigger = job.Trigger.StartsWith("Retry: ", StringComparison.Ordinal) ? job.Trigger : ("Retry: " + job.Trigger)[..Math.Min(200, job.Trigger.Length + 7)];
    }

    private async Task<string> NumberAsync(Guid id) => await db.Records.Where(r => r.Id == id).Select(r => r.Number).FirstOrDefaultAsync() ?? id.ToString()[..8];

    /// <summary>Run the publishing job now through the scheduler (lease, system identity and run history apply).</summary>
    public Task<AdminResult> ProcessNowAsync() =>
        me.IsAdmin ? tasks.RunNowAsync(SharePointPublishingJob.JobKey) : Task.FromResult(AdminResult.Fail(AdminResult.NotAdmin));
}
