using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Search;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure.Auth;
using Vellum.Infrastructure.Files;

namespace Vellum.Infrastructure.Api;

/// <summary>JSON endpoints used by the UI (lookups, command palette, locks, notifications, session keep-alive, file download).</summary>
public static class Endpoints
{
    public static void MapVellumApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var post = api.MapGroup("").AddEndpointFilter(async (ctx, next) =>
        {
            // CSRF protection for JSON posts: the layout exposes the token in a meta tag; JS sends it as a header.
            var af = ctx.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            try { await af.ValidateRequestAsync(ctx.HttpContext); }
            catch (AntiforgeryValidationException) { return Results.StatusCode(400); }
            return await next(ctx);
        });

        api.MapGet("/lookup/users", (string? q, UserDirectory dir) =>
        {
            var term = (q ?? "").Trim();
            return dir.Assignable
                .Where(u => term.Length == 0 || u.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) || u.Username.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Take(30).Select(u => new { id = u.Id, name = u.DisplayName, initials = u.Initials, title = u.JobTitle, hue = u.Hue });
        });

        api.MapGet("/lookup/records", async (string? q, string? forms, AccessService access, ConfigCache cfg) =>
        {
            var term = (q ?? "").Trim();
            var query = access.Readable().Where(r => !r.IsArchived);
            if (!string.IsNullOrEmpty(forms))
            {
                var ids = forms.Split(',').Select(k => cfg.Current.FormsByKey.GetValueOrDefault(k.Trim())?.Id).OfType<int>().ToList();
                query = query.Where(r => ids.Contains(r.FormId));
            }
            if (term.Length > 0) query = query.Where(r => r.Number.Contains(term) || r.Title.Contains(term));
            var rows = await query.OrderByDescending(r => r.ModifiedAt).Take(30)
                .Select(r => new { r.Id, r.Number, r.Title, r.FormId, r.PhaseId, r.Revision, r.IsEffective }).ToListAsync();
            return rows.Select(r => new
            {
                id = r.Id, number = r.Number, title = r.Title, revision = r.Revision, effective = r.IsEffective,
                form = cfg.Current.FormsById[r.FormId].Name, icon = cfg.Current.FormsById[r.FormId].App.Icon,
                phase = cfg.Current.Phase(r.PhaseId)?.Name,
            });
        });

        api.MapGet("/search/quick", async (string? q, SearchService search, ConfigCache cfg, AccessService access) =>
        {
            var term = (q ?? "").Trim();
            var apps = cfg.Current.Apps.Where(a => !a.IsSystem && access.CanOpenApp(a.Id) &&
                (term.Length == 0 || a.Name.Contains(term, StringComparison.OrdinalIgnoreCase))).Take(6)
                .Select(a => new { kind = "app", label = a.Name, sub = a.Suite, href = $"/apps/{a.Key}", icon = a.Icon, color = a.Color });
            var views = term.Length < 2 ? [] : cfg.Current.Apps.Where(a => access.CanOpenApp(a.Id))
                .SelectMany(a => a.Views.Where(v => !v.Hidden && v.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Select(v => new { kind = "view", label = v.Name, sub = a.Name, href = $"/apps/{a.Key}?view={v.Key}", icon = "bi-table", color = a.Color }))
                .Take(5);
            var groups = term.Length < 2 ? [] : await search.SearchAsync(term, perApp: 5);
            var records = groups.SelectMany(g => g.Hits.Select(h => new
            {
                kind = "record", label = $"{h.Number} · {h.Title}", sub = $"{cfg.Current.FormsById[h.FormId].Name} · {cfg.Current.Phase(h.PhaseId)?.Name}",
                href = $"/records/{h.Id}", icon = g.App.Icon, color = g.App.Color,
            })).Take(8);
            return new { items = apps.Concat(views).Concat(records) };
        });

        // SEC-401: the idle-timeout script asks how long is left without counting as activity…
        api.MapGet("/session/status", async (CurrentUser me, VellumDbContext db) =>
        {
            var s = await db.Sessions.AsNoTracking().FirstAsync(x => x.Id == me.SessionId);
            var settings = await db.Settings.AsNoTracking().FirstAsync();
            var idleLeft = (s.LastSeenAt.AddMinutes(settings.SessionIdleMinutes) - DateTime.UtcNow).TotalSeconds;
            var absLeft = (s.AbsoluteExpiresAt - DateTime.UtcNow).TotalSeconds;
            return new { secondsLeft = (int)Math.Max(0, Math.Min(idleLeft, absLeft)), idleMinutes = settings.SessionIdleMinutes };
        });
        // …and "Stay signed in" counts as activity (the auth handler refreshes LastSeenAt).
        post.MapPost("/session/ping", () => Results.Ok(new { ok = true }));

        post.MapPost("/records/{id:guid}/lock", async (Guid id, LockService locks, AccessService access, VellumDbContext db) =>
        {
            var r = await db.Records.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (r == null || !(await access.CanEditAsync(r)).Allowed) return Results.Forbid();
            var s = await locks.AcquireAsync(id);
            return Results.Ok(new { mine = s.Mine, holder = s.HolderName, since = s.Since });
        });
        post.MapPost("/records/{id:guid}/unlock", async (Guid id, LockService locks) =>
        {
            await locks.ReleaseAsync(id);
            return Results.Ok();
        });

        api.MapGet("/notifications", async (CurrentUser me, VellumDbContext db) =>
        {
            var items = await db.Notifications.AsNoTracking().Where(n => n.UserId == me.Id).OrderByDescending(n => n.CreatedAt).Take(12)
                .Select(n => new { n.Id, n.Title, n.Body, n.Url, kind = n.Kind.ToString(), n.CreatedAt, read = n.ReadAt != null, ack = n.AcknowledgedAt != null })
                .ToListAsync();
            var unread = await db.Notifications.CountAsync(n => n.UserId == me.Id && n.ReadAt == null);
            return new { unread, items = items.Select(n => new { n.Id, n.Title, n.Body, n.Url, n.kind, ago = Ui.Ui.Ago(n.CreatedAt), n.read, n.ack }) };
        });
        post.MapPost("/notifications/{id:long}/read", async (long id, CurrentUser me, VellumDbContext db) =>
        {
            await db.Notifications.Where(n => n.Id == id && n.UserId == me.Id && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow));
            return Results.Ok();
        });
        post.MapPost("/notifications/read-all", async (CurrentUser me, VellumDbContext db) =>
        {
            // Safety-critical alerts stay unread until acknowledged (SAF-020).
            await db.Notifications.Where(n => n.UserId == me.Id && n.ReadAt == null && n.Kind != NotificationKind.SafetyCritical)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow));
            return Results.Ok();
        });
        post.MapPost("/notifications/{id:long}/ack", async (long id, CurrentUser me, VellumDbContext db, AuditService audit) =>
        {
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.Id);
            if (n == null) return Results.NotFound();
            n.AcknowledgedAt ??= DateTime.UtcNow;
            n.ReadAt ??= DateTime.UtcNow;
            audit.Record(n.RecordId, "Alert acknowledged", $"{me.Name} acknowledged \"{n.Title}\" (in-app)");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        post.MapPost("/favorites/toggle", async (FavoriteRequest req, CurrentUser me, VellumDbContext db) =>
        {
            var f = await db.Favorites.FirstOrDefaultAsync(x => x.UserId == me.Id && x.Kind == req.Kind && x.RefKey == req.RefKey);
            if (f != null) db.Favorites.Remove(f);
            else db.Favorites.Add(new Favorite { UserId = me.Id, Kind = req.Kind, RefKey = req.RefKey, Label = req.Label, At = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return Results.Ok(new { favorite = f == null });
        });

        // Attachment download: read access re-checked, checksum verified (INV-053), audited (PLT-1001).
        app.MapGet("/files/{id:long}", async (long id, bool? inline, VellumDbContext db, AccessService access, FileStore files, AuditService audit,
            [Microsoft.AspNetCore.Mvc.FromServices] IEnumerable<IAttachmentPolicy> policies) =>
        {
            var att = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
            if (att == null || !await access.CanReadAsync(att.RecordId)) return Results.NotFound();
            foreach (var policy in policies)
            {
                var decision = await policy.DecideAsync(att, inline == true);
                if (decision.RedirectUrl != null) return Results.Redirect(decision.RedirectUrl);
                if (!decision.Allowed) return Results.NotFound();
            }
            byte[] bytes;
            try { bytes = await files.ReadVerifiedAsync(att.Sha256); }
            catch (FileIntegrityException ex)
            {
                audit.Record(att.RecordId, "File integrity failure", ex.Message);
                await db.SaveChangesAsync();
                return Results.Problem(ex.Message, statusCode: 409, title: "File failed its integrity check");
            }
            audit.Record(att.RecordId, inline == true ? "Attachment viewed" : "Attachment downloaded", $"{att.FileName} (SHA-256 {att.Sha256[..12]}…)", fieldKey: att.FieldKey);
            await db.SaveChangesAsync();
            return inline == true && att.ContentType is "application/pdf" or "image/png" or "image/jpeg" or "image/gif" or "text/plain"
                ? Results.File(bytes, att.ContentType)
                : Results.File(bytes, att.ContentType, att.FileName);
        }).RequireAuthorization();
    }

    public sealed record FavoriteRequest(string Kind, string RefKey, string Label);

    private static string Csv(string? s) => CsvCell.Of(s, quote: true);

    /// <summary>PLT-1007: audit trail export in human- and machine-readable form (exports are themselves audited).</summary>
    public static void MapExports(this WebApplication app)
    {
        app.MapGet("/records/{id:guid}/audit.{format}", async (Guid id, string format, VellumDbContext db, AccessService access, AuditService audit) =>
        {
            if (!await access.CanReadAsync(id)) return Results.NotFound();
            var rec = await db.Records.AsNoTracking().FirstAsync(r => r.Id == id);
            var entries = await db.Audit.AsNoTracking().Where(a => a.RecordId == id).OrderBy(a => a.Seq).ToListAsync();
            audit.Record(id, "Audit trail exported", $"Exported {entries.Count} entries as {format.ToUpperInvariant()}");
            await db.SaveChangesAsync();
            var name = $"{rec.Number}-audit-trail.{format}";
            if (format == "json")
                return Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(entries.Select(a => new
                {
                    a.Seq, at = a.At.ToString("O") + "Z", actor = a.UserName, actorKind = a.ActorKind.ToString(), onBehalfOf = a.OnBehalfOfName, a.Source,
                    a.Action, field = a.FieldLabel ?? a.FieldKey, oldValue = a.OldValue, newValue = a.NewValue, a.Detail, a.Reason, phase = a.PhaseName,
                    a.RecordVersion, a.PrevHash, a.Hash,
                }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "application/json", name);
            if (format != "csv") return Results.NotFound();
            var sb = new System.Text.StringBuilder("Seq,UTC time,Actor,Actor kind,On behalf of,Source,Action,Field,Old value,New value,Detail,Reason,Phase,Record version,Previous hash,Hash\r\n");
            foreach (var a in entries)
                sb.AppendJoin(',', a.Seq, a.At.ToString("yyyy-MM-dd HH:mm:ss.fff"), Csv(a.UserName), a.ActorKind, Csv(a.OnBehalfOfName), a.Source, Csv(a.Action),
                    Csv(a.FieldLabel ?? a.FieldKey), Csv(a.OldValue), Csv(a.NewValue), Csv(a.Detail), Csv(a.Reason), Csv(a.PhaseName), a.RecordVersion, a.PrevHash, a.Hash).Append("\r\n");
            return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", name);
        }).RequireAuthorization();

        // PLT-1103: export a view (security-trimmed, audited as a global event).
        app.MapGet("/apps/{key}/export.csv", async (string key, string? view, string? q, string? sort, bool? desc, ConfigCache cfg, AccessService access,
            Engine.Views.ViewService views, AuditService audit, VellumDbContext db, HttpContext http) =>
        {
            if (!cfg.Current.AppsByKey.TryGetValue(key, out var a) || !access.CanOpenApp(a.Id)) return Results.NotFound();
            var v = a.Views.FirstOrDefault(x => x.Key == view) ?? a.Views.FirstOrDefault(x => x.IsDefault) ?? a.Views.FirstOrDefault();
            if (v == null) return Results.NotFound();
            var filters = http.Request.Query.Where(kv => kv.Key.StartsWith("f.")).ToDictionary(kv => kv.Key[2..], kv => kv.Value.ToString());
            var result = await views.ExportAsync(v, q, filters, sort, desc);
            var sb = new System.Text.StringBuilder(string.Join(',', result.Columns.Select(c => Csv(c.Label))) + "\r\n");
            var dir = http.RequestServices.GetRequiredService<UserDirectory>();
            foreach (var row in result.Rows)
                sb.Append(string.Join(',', result.Columns.Select(c => row.Cells.TryGetValue(c.Key, out var cell)
                    ? Csv(cell.UserIds.Count > 0 ? string.Join("; ", cell.UserIds.Select(dir.Name)) : cell.Text) : ""))).Append("\r\n");
            audit.Global("View exported", $"{a.Name} › {v.Name}: {result.Rows.Count} rows exported to CSV");
            await db.SaveChangesAsync();
            return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"{a.Key}-{v.Key}-{DateTime.UtcNow:yyyyMMdd}.csv");
        }).RequireAuthorization();

        app.MapPost("/api/me/theme", async (ThemeRequest req, CurrentUser me, VellumDbContext db, IAntiforgery af, HttpContext http) =>
        {
            await af.ValidateRequestAsync(http);
            var theme = req.Theme is "light" or "dark" ? req.Theme : "auto";
            await db.Users.Where(u => u.Id == me.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Theme, theme));
            return Results.Ok();
        }).RequireAuthorization();

        app.MapPost("/logout", async (SessionService sessions, IAntiforgery af, HttpContext http, VellumDbContext db, CurrentUser me) =>
        {
            await af.ValidateRequestAsync(http);
            if (me.IsAuthenticated)
                db.SecurityEvents.Add(new SecurityEvent { At = DateTime.UtcNow, Kind = "Logout", Success = true, UserId = me.Id, Username = me.Info?.Username, Ip = me.Ip });
            await db.SaveChangesAsync();
            await sessions.EndAsync();
            return Results.Redirect("/login?signedout=1");
        });
    }

    public sealed record ThemeRequest(string Theme);
}
