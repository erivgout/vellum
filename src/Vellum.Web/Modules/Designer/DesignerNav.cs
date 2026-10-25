using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Designer;

/// <summary>Sidebar link to the Designer for administrators and anyone with designer rights on at least one application (DSN-001).</summary>
public sealed class DesignerNav(CurrentUser me, AccessService access, UserDirectory dir) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        if (!me.IsAuthenticated || me.IsSystem) yield break;
        if (me.IsAdmin || access.IsAnyDesigner || dir.IsMember(me.Id, DesignerStore.CreatorsGroup))
            yield return new NavItem("Administration", "Designer", "/designer", "bi-vector-pen", Order: 20, ActivePrefix: "/designer");
    }
}
