using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.AdverseEvents;

public sealed record EmdrResult(bool Ok, IReadOnlyList<string> Errors, string? Message = null)
{
    public static EmdrResult Success(string message) => new(true, [], message);
    public static EmdrResult Fail(params string[] errors) => new(false, errors);
}

public sealed record CertificateDetails(string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, string Thumbprint, bool SelfSigned)
{
    public bool Expired => NotAfter < DateTime.UtcNow;
    public bool ExpiresSoon => !Expired && NotAfter < DateTime.UtcNow.AddDays(60);
}

/// <summary>
/// INT-902: the eMDR profile. One per environment, bound to the environment it was created in (a copied database doesn't carry a
/// working profile into another environment), and never promoted with configuration. Administrators only. The ESG password and
/// the certificate's private key are encrypted with ASP.NET Core Data Protection and are never displayed or returned.
/// </summary>
[ScopedService]
public sealed class EmdrProfileService(VellumDbContext db, IDataProtectionProvider protection, CurrentUser me, AuditService audit)
{
    /// <summary>Characters the certificate fields reject (INT-902).</summary>
    public const string ForbiddenCertChars = "(+<>;\"/\\";
    private const string Purpose = "Vellum.AdverseEvents.EmdrProfile.v1";

    private IDataProtector Protector => protection.CreateProtector(Purpose);

    public sealed record FdaDefaults(string RoutingId, string EsgUrl, string Center);

    /// <summary>FDA defaults per account type. They can be overridden when the FDA's ESG account letter says otherwise.</summary>
    public static FdaDefaults Defaults(string accountType) => accountType == "production"
        ? new("ZZFDA", "https://esg.fda.gov", "CDRH")
        : new("ZZFDATST", "https://esgtest.fda.gov", "CDRH");

    public static string EnvironmentKey(SystemSettings s) => $"{s.EnvironmentType}:{s.EnvironmentName}".ToLowerInvariant();

    public async Task<SystemSettings> SettingsAsync() => await db.Settings.AsNoTracking().FirstAsync();

    /// <summary>The profile of this environment, or null.</summary>
    public async Task<EmdrProfile?> CurrentAsync()
    {
        var key = EnvironmentKey(await SettingsAsync());
        return await db.Set<EmdrProfile>().FirstOrDefaultAsync(p => p.EnvironmentKey == key);
    }

    /// <summary>Profiles bound to other environments (for example in a database copied from production). They're never used here.</summary>
    public async Task<List<EmdrProfile>> ForeignAsync()
    {
        var key = EnvironmentKey(await SettingsAsync());
        return await db.Set<EmdrProfile>().AsNoTracking().Where(p => p.EnvironmentKey != key).ToListAsync();
    }

    public static string? CertFieldProblem(string label, string? value, bool required = false, int max = 64)
    {
        if (string.IsNullOrWhiteSpace(value)) return required ? $"{label} is required." : null;
        var bad = value.Where(ch => ForbiddenCertChars.Contains(ch)).Distinct().ToList();
        if (bad.Count > 0) return $"{label} can't contain {string.Join(" ", bad.Select(c => $"“{c}”"))}. Certificate fields reject ( + < > ; \" / \\.";
        if (value.Trim().Length > max) return $"{label} can be at most {max} characters.";
        return null;
    }

    public sealed record ProfileInput(string AccountType, string SenderOrganization, string SenderRoutingId, string SenderEmail, string? AcknowledgementUrl,
        string? FdaRoutingId, string? EsgUrl, string? SubmissionCenter, string Gateway, string SimulatedOutcome);

    public async Task<EmdrResult> SaveAsync(ProfileInput i)
    {
        if (!me.IsAdmin) return EmdrResult.Fail("Only administrators can change the eMDR profile.");
        var settings = await SettingsAsync();
        var errors = new List<string>();
        if (i.AccountType is not ("test" or "production")) errors.Add("Choose the account type: test or production.");
        if (string.IsNullOrWhiteSpace(i.SenderOrganization)) errors.Add("Sender organization is required.");
        if (string.IsNullOrWhiteSpace(i.SenderRoutingId)) errors.Add("Sender routing ID is required (from your FDA ESG account).");
        else if (i.SenderRoutingId.Any(char.IsWhiteSpace)) errors.Add("The sender routing ID can't contain spaces.");
        if (EmdrValidator.EmailProblem(i.SenderEmail) != null || string.IsNullOrWhiteSpace(i.SenderEmail)) errors.Add("Enter the sender's email address.");
        foreach (var (label, url) in new[] { ("Acknowledgment URL", i.AcknowledgementUrl), ("ESG URL", i.EsgUrl) })
            if (!string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
                errors.Add($"{label} must be an https:// address.");
        if (i.Gateway is not ("manual" or "simulated" or "as2")) errors.Add("Choose a gateway.");
        if (i.Gateway == "simulated" && settings.EnvironmentType == EnvironmentType.Production)
            errors.Add("The simulated gateway can't be used in a production environment.");
        if (i.Gateway == "as2") errors.Add(EmdrGateways.As2NotAvailable);
        if (i.SimulatedOutcome is not ("accept" or "reject_ack3" or "withhold_ack3")) errors.Add("Choose what the simulated gateway returns.");
        if (errors.Count > 0) return new EmdrResult(false, errors);

        var p = await CurrentAsync();
        var before = p == null ? null : Redacted(p);
        var isNew = p == null;
        if (p == null)
        {
            p = new EmdrProfile { EnvironmentKey = EnvironmentKey(settings), EnvironmentName = settings.EnvironmentName, CreatedAt = DateTime.UtcNow };
            db.Set<EmdrProfile>().Add(p);
        }
        var d = Defaults(i.AccountType);
        p.AccountType = i.AccountType;
        p.SenderOrganization = i.SenderOrganization.Trim();
        p.SenderRoutingId = i.SenderRoutingId.Trim();
        p.SenderEmail = i.SenderEmail.Trim();
        p.AcknowledgementUrl = Blank(i.AcknowledgementUrl);
        p.FdaRoutingId = Blank(i.FdaRoutingId) is { } r && r != d.RoutingId ? r : null;
        p.EsgUrl = Blank(i.EsgUrl) is { } e && e != d.EsgUrl ? e : null;
        p.SubmissionCenter = Blank(i.SubmissionCenter) is { } c && c != d.Center ? c : null;
        p.Gateway = i.Gateway;
        p.SimulatedOutcome = i.SimulatedOutcome;
        Touch(p);
        audit.Config("eMDR profile", p.EnvironmentKey, isNew ? "Created" : "Changed", before, Redacted(p));
        audit.Global(isNew ? "eMDR profile created" : "eMDR profile changed", $"Environment {settings.EnvironmentName}: {p.AccountType} account, {p.Gateway} gateway (INT-902)");
        await db.SaveChangesAsync();
        return EmdrResult.Success(isNew ? "eMDR profile created for this environment." : "eMDR profile saved.");
    }

    public sealed record CertificateInput(string? CommonName, string? Organization, string? OrganizationalUnit, string? Locality, string? State, string? Country, string? Email);

    public static List<string> CertificateProblems(CertificateInput c)
    {
        var errors = new[]
        {
            CertFieldProblem("Common name", c.CommonName, true), CertFieldProblem("Organization", c.Organization, true),
            CertFieldProblem("Organizational unit", c.OrganizationalUnit), CertFieldProblem("City / locality", c.Locality, true),
            CertFieldProblem("State / province", c.State, true), CertFieldProblem("Email", c.Email, true, 128),
        }.OfType<string>().ToList();
        if (CertFieldProblem("Country", c.Country, true) is { } ce) errors.Add(ce);
        else if (c.Country!.Trim().Length != 2 || !c.Country.Trim().All(char.IsAsciiLetter)) errors.Add("Country is the two-letter code, for example US.");
        if (c.Email != null && EmdrValidator.EmailProblem(c.Email) != null) errors.Add("Enter a valid certificate email address.");
        return errors;
    }

    /// <summary>
    /// Generate a new encryption and signing certificate (RSA 3072, SHA-256, valid two years). The public certificate is kept for
    /// download; the private key is encrypted with Data Protection. Submissions fail until the FDA has installed the new certificate.
    /// </summary>
    public async Task<EmdrResult> GenerateCertificateAsync(CertificateInput c, bool acknowledged)
    {
        if (!me.IsAdmin) return EmdrResult.Fail("Only administrators can generate the eMDR certificate.");
        var p = await CurrentAsync();
        if (p == null) return EmdrResult.Fail("Save the eMDR profile first.");
        if (!acknowledged) return EmdrResult.Fail("Confirm that you understand submissions will fail until the FDA has the new certificate.");
        var errors = CertificateProblems(c);
        if (errors.Count > 0) return new EmdrResult(false, errors);

        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(c.CommonName!.Trim());
        dn.AddOrganizationName(c.Organization!.Trim());
        if (!string.IsNullOrWhiteSpace(c.OrganizationalUnit)) dn.AddOrganizationalUnitName(c.OrganizationalUnit.Trim());
        dn.AddLocalityName(c.Locality!.Trim());
        dn.AddStateOrProvinceName(c.State!.Trim());
        dn.AddCountryOrRegion(c.Country!.Trim().ToUpperInvariant());
        dn.AddEmailAddress(c.Email!.Trim());

        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest(dn.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));

        var before = Redacted(p);
        var previous = p.CertThumbprint;
        p.CertCommonName = c.CommonName.Trim();
        p.CertOrganization = c.Organization.Trim();
        p.CertOrganizationalUnit = Blank(c.OrganizationalUnit);
        p.CertLocality = c.Locality.Trim();
        p.CertState = c.State.Trim();
        p.CertCountry = c.Country.Trim().ToUpperInvariant();
        p.CertEmail = c.Email.Trim();
        p.CertPem = cert.ExportCertificatePem();
        p.CertThumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
        p.CertNotBefore = cert.NotBefore.ToUniversalTime();
        p.CertNotAfter = cert.NotAfter.ToUniversalTime();
        p.CertGeneratedAt = DateTime.UtcNow;
        p.CertFdaConfirmedAt = null;
        p.PrivateKeyProtected = Protector.Protect(rsa.ExportPkcs8PrivateKeyPem());
        Touch(p);
        audit.Config("eMDR profile", p.EnvironmentKey, "Encryption certificate generated", before, Redacted(p));
        audit.Global("eMDR certificate generated", $"New certificate SHA-256 {p.CertThumbprint[..16]}…" + (previous != null ? $" replaces {previous[..16]}…" : "") +
            ". Submissions fail until the FDA installs it (INT-902).");
        await db.SaveChangesAsync();
        return EmdrResult.Success("New certificate generated. Send the public certificate to the FDA ESG: submissions will fail until the FDA has it.");
    }

    public async Task<EmdrResult> ConfirmFdaHasCertificateAsync()
    {
        if (!me.IsAdmin) return EmdrResult.Fail("Only administrators can do this.");
        var p = await CurrentAsync();
        if (p?.CertPem == null) return EmdrResult.Fail("There's no certificate to confirm.");
        p.CertFdaConfirmedAt = DateTime.UtcNow;
        Touch(p);
        audit.Global("eMDR certificate confirmed", $"Recorded that the FDA has installed certificate {p.CertThumbprint?[..16]}…");
        await db.SaveChangesAsync();
        return EmdrResult.Success("Recorded that the FDA has the current certificate.");
    }

    public async Task<EmdrResult> SetPasswordAsync(string? password)
    {
        if (!me.IsAdmin) return EmdrResult.Fail("Only administrators can do this.");
        var p = await CurrentAsync();
        if (p == null) return EmdrResult.Fail("Save the eMDR profile first.");
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 256) return EmdrResult.Fail("Enter the ESG password (8 to 256 characters).");
        p.EsgPasswordProtected = Protector.Protect(password);
        p.EsgPasswordSetAt = DateTime.UtcNow;
        Touch(p);
        audit.Config("eMDR profile", p.EnvironmentKey, "ESG password replaced", null, "(encrypted, not recorded)");
        audit.Global("eMDR ESG password replaced", "Stored encrypted (Data Protection); never displayed.");
        await db.SaveChangesAsync();
        return EmdrResult.Success("ESG password stored (encrypted).");
    }

    public async Task<EmdrResult> ClearPasswordAsync()
    {
        if (!me.IsAdmin) return EmdrResult.Fail("Only administrators can do this.");
        var p = await CurrentAsync();
        if (p?.EsgPasswordProtected == null) return EmdrResult.Fail("No ESG password is stored.");
        p.EsgPasswordProtected = null;
        p.EsgPasswordSetAt = null;
        Touch(p);
        audit.Config("eMDR profile", p.EnvironmentKey, "ESG password removed", "(encrypted)", null);
        audit.Global("eMDR ESG password removed");
        await db.SaveChangesAsync();
        return EmdrResult.Success("ESG password removed.");
    }

    public async Task<EmdrResult> SetFdaCertificateAsync(string? pem)
    {
        if (!me.IsAdmin) return EmdrResult.Fail("Only administrators can do this.");
        var p = await CurrentAsync();
        if (p == null) return EmdrResult.Fail("Save the eMDR profile first.");
        if (string.IsNullOrWhiteSpace(pem)) return EmdrResult.Fail("Paste the FDA's certificate (PEM, starting with -----BEGIN CERTIFICATE-----).");
        if (Describe(pem) == null) return EmdrResult.Fail("That isn't a readable PEM certificate.");
        var before = Redacted(p);
        p.FdaCertificatePem = pem.Trim();
        Touch(p);
        audit.Config("eMDR profile", p.EnvironmentKey, "FDA certificate replaced", before, Redacted(p));
        await db.SaveChangesAsync();
        return EmdrResult.Success("FDA certificate saved.");
    }

    /// <summary>For a gateway implementation only (the AS2 seam). Never display the result.</summary>
    public string? RevealPasswordForGateway(EmdrProfile p) => p.EsgPasswordProtected == null ? null : Protector.Unprotect(p.EsgPasswordProtected);

    /// <summary>For a gateway implementation only (the AS2 seam): the certificate with its private key.</summary>
    public X509Certificate2? SigningCertificateForGateway(EmdrProfile p)
    {
        if (p.CertPem == null || p.PrivateKeyProtected == null) return null;
        return X509Certificate2.CreateFromPem(p.CertPem, Protector.Unprotect(p.PrivateKeyProtected));
    }

    public static CertificateDetails? Describe(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return null;
        try
        {
            using var c = X509Certificate2.CreateFromPem(pem);
            return new CertificateDetails(c.Subject, c.Issuer, c.NotBefore.ToUniversalTime(), c.NotAfter.ToUniversalTime(),
                c.GetCertHashString(HashAlgorithmName.SHA256), c.Subject == c.Issuer);
        }
        catch (CryptographicException) { return null; }
    }

    private void Touch(EmdrProfile p)
    {
        p.UpdatedAt = DateTime.UtcNow;
        p.UpdatedById = me.Id;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The profile as recorded in the configuration audit trail: no secrets, no key material.</summary>
    private static string Redacted(EmdrProfile p) => JsonSerializer.Serialize(new
    {
        p.EnvironmentName, p.AccountType, p.SenderOrganization, p.SenderRoutingId, p.SenderEmail, p.AcknowledgementUrl, p.FdaRoutingId, p.EsgUrl,
        p.SubmissionCenter, p.Gateway, p.SimulatedOutcome, Certificate = p.CertThumbprint, CertificateSubject = p.CertCommonName,
        FdaCertificate = Describe(p.FdaCertificatePem)?.Thumbprint, EsgPassword = p.EsgPasswordProtected != null ? "(set)" : null,
    });
}
