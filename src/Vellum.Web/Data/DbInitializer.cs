using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Packaging;

namespace Vellum.Data;

/// <summary>Creates/migrates the database, installs DB-level guards (triggers), seeds core data and packaged applications.</summary>
public sealed class DbInitializer(IServiceProvider services, ILogger<DbInitializer> log, IConfiguration config)
{
    public async Task InitializeAsync()
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<VellumDbContext>();

        if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
        await InstallGuardsAsync(db);

        if (!await db.Settings.AnyAsync())
        {
            log.LogInformation("Seeding core data");
            await CoreSeed.SeedAsync(db);
        }
        await CoreSeed.EnsureSystemIdentitiesAsync(db);
        sp.GetRequiredService<UserDirectory>().Invalidate();

        var installer = sp.GetRequiredService<PackagedAppInstaller>();
        var packages = sp.GetServices<IPackagedApp>().OrderBy(p => p.Order).ThenBy(p => p.Key).ToList();
        var installed = new List<IPackagedApp>();
        foreach (var pkg in packages)
            if (await installer.InstallAsync(pkg)) installed.Add(pkg);

        if (installed.Count > 0 || !await db.ConfigVersions.AnyAsync())
        {
            var next = (await db.ConfigVersions.MaxAsync(v => (int?)v.Number) ?? 0) + 1;
            db.ConfigVersions.Add(new ConfigVersion
            {
                Number = next, PublishedAt = DateTime.UtcNow,
                Note = installed.Count > 0 ? "Installed packaged applications: " + string.Join(", ", installed.Select(p => p.Key)) : "Initial configuration",
            });
            await db.SaveChangesAsync();
        }
        var cache = sp.GetRequiredService<ConfigCache>();
        cache.Invalidate();
        sp.GetRequiredService<UserDirectory>().Invalidate();

        var issues = ConfigValidator.Validate(cache.Current);
        foreach (var i in issues) log.LogWarning("Configuration: {Issue}", i);

        foreach (var pkg in installed)
        {
            using var baselineScope = services.CreateScope();
            try
            {
                await pkg.SeedBaselineAsync(new DemoContext(baselineScope.ServiceProvider));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Baseline data for {App} failed", pkg.Key);
            }
        }

        if (installed.Count > 0 && config.GetValue("Vellum:SeedDemoData", true))
        {
            foreach (var pkg in installed)
            {
                using var demoScope = services.CreateScope();
                try
                {
                    await pkg.SeedDemoAsync(new DemoContext(demoScope.ServiceProvider));
                    log.LogInformation("Seeded demo data for {App}", pkg.Key);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Demo data for {App} failed", pkg.Key);
                }
            }
        }
    }

    /// <summary>INV-050 / INV-052 enforced in the database itself: no UPDATE/DELETE on audit entries; signatures immutable.</summary>
    private static async Task InstallGuardsAsync(VellumDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR ALTER TRIGGER [trg_Audit_AppendOnly] ON [AuditEntries] INSTEAD OF UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51000, 'The audit trail is append-only (INV-050).', 1;
            END
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR ALTER TRIGGER [trg_ConfigAudit_AppendOnly] ON [ConfigAuditEntries] INSTEAD OF UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51001, 'The configuration audit trail is append-only (INV-145).', 1;
            END
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR ALTER TRIGGER [trg_Signatures_Immutable] ON [Signatures] AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT EXISTS (SELECT 1 FROM inserted)
                    THROW 51002, 'Electronic signatures can never be deleted (INV-052).', 1;
                IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.[Id] = d.[Id]
                           WHERE i.[RecordId] <> d.[RecordId] OR i.[SignerId] <> d.[SignerId] OR i.[SignerName] <> d.[SignerName]
                              OR i.[Meaning] <> d.[Meaning] OR i.[SignedAt] <> d.[SignedAt] OR i.[RecordHash] <> d.[RecordHash]
                              OR i.[Action] <> d.[Action] OR (d.[Superseded] = 1 AND i.[Superseded] = 0))
                    THROW 51003, 'Electronic signatures are immutable (INV-052).', 1;
            END
            """);
    }
}

/// <summary>A subset of publish-time configuration validation (DSN-1201). The Designer extends this.</summary>
public static class ConfigValidator
{
    public static List<string> Validate(ConfigModel cfg)
    {
        var issues = new List<string>();
        foreach (var form in cfg.FormsById.Values)
        {
            foreach (var f in form.Fields)
            {
                if (f.Type is FieldType.Keyword or FieldType.KeywordMulti && (f.KeywordList == null || !cfg.KeywordListsByKey.ContainsKey(f.KeywordList)))
                    issues.Add($"{form.Key}.{f.Key}: keyword list '{f.KeywordList}' doesn't exist.");
                if (form.Sections.All(s => s.Key != f.SectionKey)) issues.Add($"{form.Key}.{f.Key}: section '{f.SectionKey}' doesn't exist.");
                foreach (var lf in f.LinkForms.Where(lf => !cfg.FormsByKey.ContainsKey(lf)))
                    issues.Add($"{form.Key}.{f.Key}: link target form '{lf}' doesn't exist.");
            }
            foreach (var wf in form.Workflows)
            {
                foreach (var p in wf.Phases)
                {
                    var both = p.EditableSections.Intersect(p.ReadOnlySections).Where(s => s != "*").ToList();
                    if (both.Count > 0) issues.Add($"{form.Key}/{p.Key}: sections both editable and read-only: {string.Join(", ", both)} (INV-135).");
                    foreach (var s in p.EditableSections.Concat(p.ReadOnlySections).Concat(p.HiddenSections).Where(s => s != "*" && form.Sections.All(x => x.Key != s)))
                        issues.Add($"{form.Key}/{p.Key}: unknown section '{s}'.");
                    if (p.AssigneeField != null && form.Fields.All(f => f.Key != p.AssigneeField))
                        issues.Add($"{form.Key}/{p.Key}: assignee field '{p.AssigneeField}' doesn't exist.");
                    foreach (var g in p.MustCompleteForms.Concat(p.MustCreateForms).Where(k => !cfg.FormsByKey.ContainsKey(k)))
                        issues.Add($"{form.Key}/{p.Key}: gate form '{g}' doesn't exist.");
                    if (p.Type == PhaseType.Normal && !p.IsStart && wf.Routes.All(r => r.ToPhaseId != p.Id))
                        issues.Add($"{form.Key}/{p.Key}: phase can't be reached (DSN-612).");
                    if (p.Type == PhaseType.Normal && wf.Routes.All(r => r.FromPhaseId != p.Id))
                        issues.Add($"{form.Key}/{p.Key}: phase has no way out (DSN-612).");
                }
                foreach (var f in form.Fields)
                    foreach (var ph in f.RequiredInPhases.Where(ph => wf.Phases.All(p => p.Key != ph)))
                        issues.Add($"{form.Key}.{f.Key}: required-in phase '{ph}' doesn't exist in {wf.Key}.");
            }
        }
        foreach (var app in cfg.Apps)
            foreach (var v in app.Views)
                foreach (var k in v.Forms.Where(k => !cfg.FormsByKey.ContainsKey(k)))
                    issues.Add($"{app.Key} view {v.Key}: form '{k}' doesn't exist.");
        return issues;
    }
}
