using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Portal;

/// <summary>Per-request data for the supplier portal layout: the organization, the supplier company and the open request count.</summary>
[ScopedService]
public sealed class PortalShell(VellumDbContext db, CurrentUser me, PortalService portal)
{
    private SystemSettings? _settings;
    private string? _company;
    private List<PortalItem>? _items;

    public CurrentUser Me => me;
    public UserInfo? User => me.Info;
    public SystemSettings Settings => _settings ??= db.Settings.AsNoTracking().First();
    public string OrganizationName => Settings.OrganizationName;
    public bool IsProduction => Settings.EnvironmentType == EnvironmentType.Production;

    /// <summary>Two-letter mark for the organization (used as its logo in the portal header).</summary>
    public string OrganizationMark =>
        string.Concat(OrganizationName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public async Task<string> CompanyAsync() =>
        _company ??= me.ExternalOrgId is { } org ? await portal.SupplierNameAsync(org) : "No company linked";

    public async Task<List<PortalItem>> ItemsAsync() => _items ??= me.IsExternal ? await portal.ListAsync() : [];

    public async Task<int> ActionNeededAsync() => (await ItemsAsync()).Count(i => i.State.ActionNeeded);
}
