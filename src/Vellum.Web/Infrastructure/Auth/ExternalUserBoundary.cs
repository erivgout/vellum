using Vellum.Engine.Security;

namespace Vellum.Infrastructure.Auth;

/// <summary>
/// SEC-804 / INV-088: the hard boundary around external (supplier portal) users. It runs for every request right after
/// authentication and is deny-by-default: an external user reaches only the supplier portal (<c>/portal/…</c>) and the few
/// endpoints and static assets it needs. Every other page, API, search, export, file download and notification link answers
/// 403, whatever authorization the endpoint itself declares, so a new internal endpoint is closed to externals automatically.
/// Internal users are unaffected here; the portal pages themselves require the external claim (the "Portal" policy).
/// </summary>
public static class ExternalUserBoundary
{
    /// <summary>Audit source of everything an external user does (PLT-1001).</summary>
    public const string PortalSource = "Supplier portal";

    /// <summary>Path prefixes an external user may request. Everything else is refused.</summary>
    public static readonly string[] AllowedPrefixes =
    [
        "/portal",              // the supplier portal pages (and their file handlers)
        "/login",               // already signed in: redirects on to /portal
        "/logout",              // sign out (POST, antiforgery-checked by the endpoint)
        "/error",               // status-code re-execution renders the refusal itself
        "/api/session/status",  // idle-timeout warning (SEC-401)
        "/api/session/ping",    // "stay signed in"
        "/api/me/theme",        // light/dark preference
        "/lib", "/css", "/js", "/img", "/favicon.ico", // static assets
    ];

    public static bool IsAllowed(PathString path) =>
        AllowedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsExternal(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true && ctx.User.HasClaim(SessionDefaults.ExternalClaim, "1");

    public static IApplicationBuilder UseExternalUserBoundary(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (!IsExternal(ctx))
        {
            await next();
            return;
        }

        // Writes by external users are always attributed to the portal in the audit trail.
        ctx.RequestServices.GetRequiredService<CurrentUser>().Source = PortalSource;

        var path = ctx.Request.Path;
        if (!path.HasValue || path == "/")
        {
            ctx.Response.Redirect("/portal");
            return;
        }
        if (IsAllowed(path))
        {
            await next();
            return;
        }

        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Vellum.ExternalUserBoundary")
            .LogWarning("Refused {Method} {Path} for external user {UserId}", ctx.Request.Method, path.Value, ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden; // rendered by the status-code page (portal layout)
    });
}
