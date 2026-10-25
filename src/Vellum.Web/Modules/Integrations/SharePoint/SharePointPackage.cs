using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Packaging;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>
/// Installs SharePoint publishing as a hidden system package (no forms or views: configuration lives in its own tables), so it
/// gets the install-once demo hook. The demo publishes effective Document Control documents to a local-folder "Controlled
/// Documents" library with a mapping profile, and runs the queue once so publications exist straight after seeding.
/// </summary>
public sealed class SharePointPackage : IPackagedApp
{
    public string Key => "sharepoint-publishing";
    /// <summary>After Document Control (20) and every other module, so the demo documents exist.</summary>
    public int Order => 900;

    public void Define(AppBuilder app)
    {
        app.Name("SharePoint publishing")
            .Description("Publishes controlled files to SharePoint document libraries and withdraws superseded and obsolete ones (INT §6.3). Configured in Administration › Integrations › SharePoint.")
            .Icon("bi-cloud-arrow-up").Color("#0369a1").Suite("Integrations").Version("1.0.0").DefaultAccess(AppRole.None);
        app.App.IsSystem = true;
    }

    public const string DemoHost = "vellum-demo.local";

    public async Task SeedDemoAsync(DemoContext d)
    {
        var sp = d.Services;
        var db = sp.GetRequiredService<VellumDbContext>();
        if (!d.Config.FormsByKey.ContainsKey("document") || await db.Set<SharePointProfile>().AnyAsync(p => p.Host == DemoHost)) return;
        sp.GetRequiredService<CurrentUser>().UseSystem("SharePoint publishing demo");
        var audit = sp.GetRequiredService<AuditService>();
        var now = DateTime.UtcNow;

        var profile = new SharePointProfile
        {
            Name = "Vellum demo site (local folder)", Host = DemoHost, Connection = SpConnection.LocalFolder, Enabled = true,
            NotifyUserIds = [d.User("pshah")], CreatedAt = now, ModifiedAt = now,
            Libraries =
            [
                new SharePointLibrary
                {
                    Key = "controlled-documents", Name = "Controlled Documents", RelativeUrl = "/Controlled Documents",
                    SimulatedColumns = "Title\nDocumentNumber\nRevision\nDocumentType\nDocumentStatus\nOwner\nDepartment\nEffectiveDate:dateTime\nVellumRecord",
                },
            ],
        };
        db.Add(profile);
        var mapping = new SharePointMapping
        {
            FormKey = "document", Name = "Controlled document metadata", CreatedAt = now, ModifiedAt = now,
            Description = "Columns of the Controlled Documents library: number, revision, type, status, owner, department and effective date.",
            Entries =
            [
                new() { Source = "$title", Column = "Title", Order = 0 },
                new() { Source = "$number", Column = "DocumentNumber", Order = 1 },
                new() { Source = "$revision", Column = "Revision", Order = 2 },
                new() { Source = "doc_type", Column = "DocumentType", Order = 3 },
                new() { Source = "$status", Column = "DocumentStatus", Order = 4 },
                new() { Source = "owner", Column = "Owner", Order = 5 },
                new() { Source = "department", Column = "Department", Order = 6 },
                new() { Source = "$effective_date", Column = "EffectiveDate", Order = 7 },
                new() { Source = "$record_url", Column = "VellumRecord", Order = 8 },
            ],
        };
        db.Add(mapping);
        await db.SaveChangesAsync();
        var rule = new SharePointRule
        {
            Name = "Effective controlled documents", FormKey = "document", PhaseKey = "approved", OnEnter = true, OnSave = true,
            ProfileId = profile.Id, LibraryId = profile.Libraries[0].Id, FieldKeys = ["primary_file"],
            FolderTemplate = "{doc_type}", FileNameTemplate = "{number} - {title}", PreferRendition = true, FallbackToOriginal = true,
            MappingId = mapping.Id, EffectiveOnly = true, KeepVersionHistory = true, WithdrawAction = SpWithdrawAction.MarkObsolete,
            ObsoleteFolder = "Obsolete", Enabled = true, CreatedAt = now, ModifiedAt = now,
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        audit.Config("SharePointProfile", profile.Host, "Created", null, SharePointAdminService.Snap(profile));
        audit.Config("SharePointMapping", $"document:{mapping.Id}", "Created", null, SharePointAdminService.Snap(mapping));
        audit.Config("SharePointRule", $"document:approved:{rule.Id}", "Created", null, SharePointAdminService.Snap(rule));
        audit.Global("SharePoint publishing demo configured", $"{profile.Name}: {rule.Name}");
        await db.SaveChangesAsync();
        SharePointRuleCache.Invalidate();

        // Publish every document that is effective now, then run the queue once.
        var formId = d.Config.Form("document").Id;
        var effective = await db.Records.AsNoTracking().Where(r => r.FormId == formId && r.IsEffective && !r.IsArchived).OrderBy(r => r.Number).Select(r => r.Id).ToListAsync();
        var queue = sp.GetRequiredService<SharePointQueue>();
        foreach (var id in effective) await queue.PublishAsync(id, rule.Id, "Demo: effective documents at go-live");
        await db.SaveChangesAsync();
        await sp.GetRequiredService<SharePointPublisher>().ProcessDueAsync();
    }
}
