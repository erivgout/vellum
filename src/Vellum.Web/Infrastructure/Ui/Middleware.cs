namespace Vellum.Infrastructure.Ui;

public static class MiddlewareExtensions
{
    /// <summary>NFR-037 / SEC-903: strict CSP (no inline script), framing blocked by default, no sniffing.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; frame-src 'self'; frame-ancestors 'self'; form-action 'self'; base-uri 'self'; object-src 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "same-origin";
        h["X-Frame-Options"] = "SAMEORIGIN";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        await next();
    });

    /// <summary>SEC-203: a forced password change blocks everything else until done.</summary>
    public static IApplicationBuilder UseMustChangePassword(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.HasClaim("must_change_password", "1"))
        {
            var p = ctx.Request.Path;
            if (!p.StartsWithSegments("/account") && !p.StartsWithSegments("/logout") && !p.StartsWithSegments("/lib") &&
                !p.StartsWithSegments("/css") && !p.StartsWithSegments("/js") && !p.StartsWithSegments("/img") && !p.StartsWithSegments("/api/session"))
            {
                ctx.Response.Redirect("/account/password?forced=1");
                return;
            }
        }
        await next();
    });
}
