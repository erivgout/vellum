using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Security;

namespace Vellum.Infrastructure.Auth;

public static class SessionDefaults
{
    public const string Scheme = "Vellum";
    public const string Cookie = "vellum.sid";
}

/// <summary>SEC §4.4: random 256-bit session IDs held server-side; idle and absolute timeouts; revocation.</summary>
public sealed class SessionService(VellumDbContext db, IHttpContextAccessor http)
{
    public async Task<string> StartAsync(User user)
    {
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var id = Base64Url(RandomNumberGenerator.GetBytes(32));
        var ctx = http.HttpContext!;
        var now = DateTime.UtcNow;
        db.Sessions.Add(new UserSession
        {
            Id = id, UserId = user.Id, CreatedAt = now, LastSeenAt = now, AbsoluteExpiresAt = now.AddHours(settings.SessionAbsoluteHours),
            Ip = ctx.Connection.RemoteIpAddress?.ToString(), UserAgent = ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 400)] : null,
        });
        await db.SaveChangesAsync();
        ctx.Response.Cookies.Append(SessionDefaults.Cookie, id, new CookieOptions
        {
            HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, IsEssential = true, Path = "/",
        });
        return id;
    }

    public async Task EndAsync(string? reason = "Signed out")
    {
        var ctx = http.HttpContext!;
        if (ctx.Request.Cookies.TryGetValue(SessionDefaults.Cookie, out var id))
        {
            await db.Sessions.Where(s => s.Id == id && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow).SetProperty(x => x.RevokedReason, reason));
            ctx.Response.Cookies.Delete(SessionDefaults.Cookie);
        }
    }

    /// <summary>SEC-403: end every session of a user (or of everyone).</summary>
    public Task<int> RevokeAllAsync(int? userId, string reason) =>
        db.Sessions.Where(s => s.RevokedAt == null && (userId == null || s.UserId == userId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow).SetProperty(x => x.RevokedReason, reason));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Validates the session cookie on every request and populates <see cref="CurrentUser"/>.</summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    VellumDbContext db, CurrentUser me)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionDefaults.Cookie, out var sid) || string.IsNullOrEmpty(sid))
            return AuthenticateResult.NoResult();

        var session = await db.Sessions.Include(s => s.User).FirstOrDefaultAsync(s => s.Id == sid);
        if (session == null || session.RevokedAt != null) return Fail("Session ended");
        var settings = await db.Settings.AsNoTracking().FirstAsync();
        var now = DateTime.UtcNow;
        if (session.AbsoluteExpiresAt < now || session.LastSeenAt < now.AddMinutes(-settings.SessionIdleMinutes))
        {
            session.RevokedAt = now;
            session.RevokedReason = "Timed out";
            await db.SaveChangesAsync();
            return Fail("Session timed out");
        }
        if (session.User.Status != UserStatus.Active || session.User.IsSystemIdentity)
        {
            session.RevokedAt = now;
            session.RevokedReason = "Account not active"; // SEC-405
            await db.SaveChangesAsync();
            return Fail("Account not active");
        }

        // Pings from the idle-timeout script don't count as activity unless the user chose to stay signed in.
        var isPassive = Request.Path.StartsWithSegments("/api/session/status");
        if (!isPassive && session.LastSeenAt < now.AddSeconds(-30))
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync();
        }

        me.SignIn(session.UserId, sid, Context.Connection.RemoteIpAddress?.ToString());
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new(ClaimTypes.Name, session.User.DisplayName),
        };
        if (session.User.IsAdministrator) claims.Add(new Claim(ClaimTypes.Role, "admin"));
        if (session.User.MustChangePassword) claims.Add(new Claim("must_change_password", "1"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SessionDefaults.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SessionDefaults.Scheme));

        AuthenticateResult Fail(string why)
        {
            Response.Cookies.Delete(SessionDefaults.Cookie);
            return AuthenticateResult.Fail(why);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Request.Path.StartsWithSegments("/api"))
        {
            Response.StatusCode = 401;
            return Task.CompletedTask;
        }
        var returnUrl = Request.Path + Request.QueryString;
        Response.Redirect("/login" + (returnUrl is "/" or "" ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/forbidden");
        return Task.CompletedTask;
    }
}
