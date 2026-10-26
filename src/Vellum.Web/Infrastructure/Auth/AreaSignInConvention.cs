using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Vellum.Infrastructure.Auth;

/// <summary>
/// Area pages need a signed-in user like every other page: <c>AuthorizeFolder("/")</c> covers only the core pages, so without
/// this a signed-out visitor following a link to an area page (the near-miss report, the OSHA log, the heat map) got "not found"
/// instead of the sign-in page and back (SEC-106). Pages meant for anonymous visitors opt out with
/// <c>AllowAnonymousToAreaPage</c> (AllowAnonymous wins).
/// </summary>
public sealed class AreaSignInConvention : IPageApplicationModelConvention
{
    public void Apply(PageApplicationModel model)
    {
        if (model.AreaName != null) model.EndpointMetadata.Add(new AuthorizeAttribute());
    }
}
