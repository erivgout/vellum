using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Vellum.Infrastructure;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>A decrypted client credential, only ever held in memory while a token is requested.</summary>
public sealed record SpCredential(string TenantId, string ClientId, string? Secret, byte[]? CertificatePfx, string? CertificatePassword)
{
    /// <summary>Changes whenever the credential changes, so cached tokens are dropped after a rotation.</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        $"{TenantId}|{ClientId}|{Secret}|{(CertificatePfx is { } c ? Convert.ToBase64String(c) : "")}")))[..16];
}

/// <summary>
/// Encrypts SharePoint credentials at rest with ASP.NET Core Data Protection (INT-301). Values are written once and never
/// shown back. Production deployments must persist the Data Protection key ring (shared by every instance), otherwise
/// stored credentials can't be decrypted after a restart and have to be entered again.
/// </summary>
[ScopedService]
public sealed class SharePointSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Vellum.Integrations.SharePoint.Credentials.v1");

    public string Protect(string plain) => _protector.Protect(plain);

    public string Unprotect(string protectedValue)
    {
        try { return _protector.Unprotect(protectedValue); }
        catch (CryptographicException ex)
        {
            throw new SharePointException(SpErrors.Secret, "The stored credential can't be decrypted (the server's data-protection keys changed). Enter the secret or certificate again on the profile.", false, inner: ex);
        }
    }

    public SpCredential CredentialFor(SharePointProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.TenantId) || string.IsNullOrWhiteSpace(p.ClientId))
            throw new SharePointException(SpErrors.Config, "The profile has no tenant ID or client ID.", false);
        if (p.AuthMethod == SpAuthMethod.ClientSecret)
        {
            if (p.SecretProtected == null) throw new SharePointException(SpErrors.Config, "The profile has no client secret.", false);
            return new SpCredential(p.TenantId, p.ClientId, Unprotect(p.SecretProtected), null, null);
        }
        if (p.CertificateProtected == null) throw new SharePointException(SpErrors.Config, "The profile has no certificate.", false);
        var pfx = Convert.FromBase64String(Unprotect(p.CertificateProtected));
        var pw = p.CertificatePasswordProtected != null ? Unprotect(p.CertificatePasswordProtected) : null;
        return new SpCredential(p.TenantId, p.ClientId, null, pfx, pw);
    }

    /// <summary>Load and check an uploaded PKCS#12 certificate: it needs an RSA private key and must not be expired.</summary>
    public static (X509Certificate2? Cert, string? Error) LoadCertificate(byte[] pfx, string? password)
    {
        try
        {
            var cert = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
            if (cert.GetRSAPrivateKey() == null) return (null, "The certificate has no RSA private key. Upload the .pfx that includes the private key.");
            if (cert.NotAfter.ToUniversalTime() < DateTime.UtcNow) return (null, $"The certificate expired on {cert.NotAfter:dd MMM yyyy}.");
            return (cert, null);
        }
        catch (CryptographicException)
        {
            return (null, "The certificate couldn't be read. Check the file is a .pfx/.p12 and the password is right.");
        }
    }
}
