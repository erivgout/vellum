using Vellum.Infrastructure;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>Creates the <see cref="ISharePointClient"/> for a profile's connection type.</summary>
[ScopedService]
public sealed class SharePointClientFactory(SharePointSecrets secrets, IWebHostEnvironment env, IConfiguration config)
{
    /// <summary>One pooled HttpClient for every Graph call of the process (connections recycled so DNS changes are seen).</summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(100),
        DefaultRequestHeaders = { { "User-Agent", "Vellum-eQMS/1.0 (SharePoint publishing)" } },
    };

    /// <summary>Tests can replace the transport (and options) used for Graph profiles.</summary>
    public static Func<SharePointProfile, SpCredential, ISharePointClient>? GraphOverride { get; set; }

    public string LocalRoot => Path.GetFullPath(config["Vellum:SharePoint:LocalRoot"] ?? Path.Combine(env.ContentRootPath, "App_Data", "sharepoint"));

    public int MaxAttempts => Math.Clamp(config.GetValue("Vellum:SharePoint:MaxAttempts", 8), 1, 50);

    public ISharePointClient For(SharePointProfile profile) => profile.Connection switch
    {
        SpConnection.LocalFolder => new LocalFolderSharePointClient(profile, LocalRoot),
        _ => GraphOverride is { } o ? o(profile, secrets.CredentialFor(profile)) : new GraphSharePointClient(profile, secrets.CredentialFor(profile), Http),
    };
}
