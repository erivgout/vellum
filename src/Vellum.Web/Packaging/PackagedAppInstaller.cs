using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;

namespace Vellum.Packaging;

/// <summary>Installs packaged application templates into the configuration store (DSN-901).</summary>
public sealed class PackagedAppInstaller(VellumDbContext db, AuditService audit, UserDirectory dir, ILogger<PackagedAppInstaller> log)
{
    public async Task<bool> InstallAsync(IPackagedApp pkg)
    {
        if (await db.Apps.AnyAsync(a => a.Key == pkg.Key)) return false;
        var b = new AppBuilder(pkg.Key);
        pkg.Define(b);

        await UpsertKeywordListsAsync(b.Lists);

        var app = b.App;
        foreach (var fb in b.Forms)
        {
            var form = fb.Form;
            var order = 0;
            foreach (var sb in fb.Sections)
            {
                form.Sections.Add(sb.Section);
                foreach (var f in sb.Fields)
                {
                    f.Order = order++;
                    form.Fields.Add(f);
                }
            }
            var dupFields = form.Fields.GroupBy(f => f.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupFields.Count > 0) throw new InvalidOperationException($"Form '{form.Key}' defines fields more than once: {string.Join(", ", dupFields)}.");
            foreach (var wb in fb.Workflows)
            {
                foreach (var pb in wb.Phases) wb.Workflow.Phases.Add(pb.Phase);
                form.Workflows.Add(wb.Workflow);
            }
            app.Forms.Add(form);
        }
        foreach (var vb in b.Views) app.Views.Add(vb.View);
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        foreach (var fb in b.Forms)
            foreach (var wb in fb.Workflows)
            {
                var wf = wb.Workflow;
                var order = 0;
                foreach (var r in wb.Routes)
                {
                    var from = wf.Phases.FirstOrDefault(p => p.Key == r.From) ?? throw new InvalidOperationException($"{fb.Form.Key}/{wf.Key}: unknown phase '{r.From}'.");
                    var to = wf.Phases.FirstOrDefault(p => p.Key == r.To) ?? throw new InvalidOperationException($"{fb.Form.Key}/{wf.Key}: unknown phase '{r.To}'.");
                    if (r.Kind is RouteKind.Forward && to.Type is not (PhaseType.Normal or PhaseType.Completed))
                        throw new InvalidOperationException($"{fb.Form.Key}/{wf.Key}: forward target '{to.Key}' must be a normal or completed phase (DSN-610).");
                    wf.Routes.Add(new RouteDefinition
                    {
                        FromPhaseId = from.Id, ToPhaseId = to.Id, Kind = r.Kind, SignatureMeaning = r.Sign, Label = r.Label, Condition = r.When,
                        RequireComment = r.Comment, AllowedPrincipals = [.. r.Allowed], Order = order++,
                    });
                }
                if (!wf.Phases.Any(p => p.IsStart)) throw new InvalidOperationException($"{fb.Form.Key}/{wf.Key} has no starting phase.");
            }

        foreach (var (principal, role, designer, formKey) in b.Grants)
        {
            int id;
            PrincipalKind kind;
            if (principal.StartsWith('@'))
            {
                var key = principal[1..];
                var group = await db.Groups.FirstOrDefaultAsync(g => g.Key == key);
                if (group == null)
                {
                    group = new Group { Key = key, Name = string.Join(' ', key.Split('-', '_').Select(w => char.ToUpperInvariant(w[0]) + w[1..])) };
                    db.Groups.Add(group);
                    await db.SaveChangesAsync();
                    dir.Invalidate();
                }
                (id, kind) = (group.Id, PrincipalKind.Group);
            }
            else
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Username == principal);
                if (user == null) { log.LogWarning("Grant for unknown user {User} in {App} skipped", principal, app.Key); continue; }
                (id, kind) = (user.Id, PrincipalKind.User);
            }
            db.Grants.Add(new AppRoleGrant { AppId = app.Id, PrincipalKind = kind, PrincipalId = id, Role = role, IsDesigner = designer, FormKey = formKey });
        }

        audit.Config("Application", app.Key, "Installed package", null, JsonSerializer.Serialize(new
        {
            app.Name, app.PackageVersion, Forms = b.Forms.Select(f => f.Form.Key), Views = b.Views.Select(v => v.View.Key),
        }));
        await db.SaveChangesAsync();
        log.LogInformation("Installed packaged application {App} ({Name})", app.Key, app.Name);
        return true;
    }

    private async Task UpsertKeywordListsAsync(IEnumerable<KeywordListBuilder> lists)
    {
        foreach (var lb in lists)
        {
            var list = await db.KeywordLists.Include(l => l.Keywords).FirstOrDefaultAsync(l => l.Key == lb.Key)
                       ?? db.KeywordLists.Local.FirstOrDefault(l => l.Key == lb.Key);
            if (list == null)
            {
                list = new KeywordList { Key = lb.Key, Name = lb.Name };
                db.KeywordLists.Add(list);
            }
            foreach (var item in lb.Items)
                if (list.Keywords.All(k => k.Key != item.Key))
                    list.Keywords.Add(new Keyword { Key = item.Key, Label = item.Label, Color = item.Color, Order = list.Keywords.Count });
            await db.SaveChangesAsync();
            foreach (var (child, parent) in lb.ParentOf)
            {
                var c = list.Keywords.First(k => k.Key == child);
                c.ParentId = list.Keywords.FirstOrDefault(k => k.Key == parent)?.Id;
            }
        }
        await db.SaveChangesAsync();
    }
}
