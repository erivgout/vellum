using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Audits;

/// <summary>Sidebar links to the Change, Audits and Inspections insight pages (shown only to users who can open the application).</summary>
public sealed class QualityInsightsNav(AccessService access, ConfigCache cfg) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        bool Can(string app) => cfg.Current.AppsByKey.TryGetValue(app, out var a) && !a.Disabled && access.CanOpenApp(a.Id);
        if (Can("change")) yield return new NavItem("Insights", "Change timeliness", "/change/timeliness", "bi-hourglass-split", 40);
        if (Can("audits"))
        {
            yield return new NavItem("Insights", "Audit findings", "/audits/findings", "bi-flag", 41);
            yield return new NavItem("Insights", "Audit program", "/audits/program", "bi-calendar2-week", 42);
        }
    }
}
