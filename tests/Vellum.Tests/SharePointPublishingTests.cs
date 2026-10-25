using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure.Files;
using Vellum.Modules.Documents;
using Vellum.Modules.Integrations.SharePoint;
using Record = Vellum.Domain.Record;
using static Vellum.Tests.DocumentControlTests;

namespace Vellum.Tests;

/// <summary>
/// SharePoint publishing (INT §6.3, DOC-084, SAF-021/043, NFR-025) against the real engine and Document Control, using the
/// local-folder connection so the published files and their metadata sidecars can be inspected.
/// </summary>
[Collection("vellum")]
public class SharePointPublishingTests(VellumFixture fx)
{
    private const string Columns = "Title\nDocumentNumber\nRevision\nDocumentType\nDocumentStatus\nDepartment\nEffectiveDate:dateTime";

    /// <summary>A profile + library + mapping + rule of its own (unique host). Disposing disables its rules so later tests aren't affected.</summary>
    private sealed class Setup(VellumFixture fx, int profileId, int libraryId, int ruleId, string host, int? mappingId) : IAsyncDisposable
    {
        public int ProfileId { get; } = profileId;
        public int LibraryId { get; } = libraryId;
        public int RuleId { get; } = ruleId;
        public string Host { get; } = host;
        public int? MappingId { get; } = mappingId;
        public List<int> ExtraRules { get; } = [];
        public string Library => Path.Combine(fx.SharePointRoot, Host, "Controlled Documents");

        public async ValueTask DisposeAsync()
        {
            using var s = fx.As("admin");
            var admin = s.ServiceProvider.GetRequiredService<SharePointAdminService>();
            foreach (var id in ExtraRules.Append(RuleId)) await admin.SetRuleEnabledAsync(id, false);
        }
    }

    private async Task<Setup> ConfigureAsync(Action<RuleInput>? rule = null, string? columns = Columns, bool mapping = true, bool outage = false)
    {
        using var s = fx.As("admin");
        var admin = s.ServiceProvider.GetRequiredService<SharePointAdminService>();
        var host = "t" + Guid.NewGuid().ToString("N")[..10] + ".test.local";
        var p = await admin.SaveProfileAsync(null, new ProfileInput
        {
            Name = "Test " + host, Host = host, Connection = SpConnection.LocalFolder, NotifyUserIds = [s.UserId("pshah")], SimulateOutage = outage,
        });
        Assert.True(p.Ok, p.Error);
        var l = await admin.SaveLibraryAsync(p.Id!.Value, null, new LibraryInput { Key = "docs", Name = "Controlled Documents", RelativeUrl = "/Controlled Documents", SimulatedColumns = columns });
        Assert.True(l.Ok, l.Error);
        int? mid = null;
        if (mapping)
        {
            var m = await admin.SaveMappingAsync(null, new MappingInput
            {
                FormKey = DocKeys.Document, Name = "Mapping " + host,
                Entries = [("$title", "Title"), ("$number", "DocumentNumber"), ("$revision", "Revision"), ("doc_type", "DocumentType"),
                           ("$status", "DocumentStatus"), ("department", "Department"), ("$effective_date", "EffectiveDate")],
            });
            Assert.True(m.Ok, m.Error);
            mid = m.Id;
        }
        var input = new RuleInput
        {
            Name = "Rule " + host, FormKey = DocKeys.Document, PhaseKey = DocKeys.Approved, OnEnter = true, OnSave = true,
            ProfileId = p.Id.Value, LibraryId = l.Id!.Value, FieldKeys = ["primary_file"], FolderTemplate = "{doc_type}",
            FileNameTemplate = "{number} - {title}", MappingId = mid, WithdrawAction = SpWithdrawAction.Delete,
        };
        rule?.Invoke(input);
        var r = await admin.SaveRuleAsync(null, input);
        Assert.True(r.Ok, r.Error);
        return new Setup(fx, p.Id.Value, l.Id.Value, r.Id!.Value, host, mid);
    }

    /// <summary>Process the pending queue items of these records, as the scheduled job would (system identity).</summary>
    private async Task<List<SharePointJob>> RunAsync(params Guid[] recordIds)
    {
        using var s = fx.Services.CreateScope();
        s.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("SharePoint test");
        var db = s.Db();
        var ids = await db.Set<SharePointJob>().Where(j => recordIds.Contains(j.RecordId) && j.Status == SpJobStatus.Pending).OrderBy(j => j.Id).Select(j => j.Id).ToListAsync();
        var publisher = s.ServiceProvider.GetRequiredService<SharePointPublisher>();
        foreach (var id in ids) await publisher.ProcessAsync(id);
        db.ChangeTracker.Clear();
        return await db.Set<SharePointJob>().AsNoTracking().Where(j => ids.Contains(j.Id)).OrderBy(j => j.Id).ToListAsync();
    }

    private async Task<List<SharePointJob>> JobsAsync(Guid recordId)
    {
        using var s = fx.Services.CreateScope();
        return await s.Db().Set<SharePointJob>().AsNoTracking().Where(j => j.RecordId == recordId).OrderBy(j => j.Id).ToListAsync();
    }

    private async Task<List<SharePointPublication>> PubsAsync(Guid recordId)
    {
        using var s = fx.Services.CreateScope();
        return await s.Db().Set<SharePointPublication>().AsNoTracking().Where(p => p.RecordId == recordId).OrderBy(p => p.Id).ToListAsync();
    }

    private static Dictionary<string, JsonElement> Sidecar(string file) =>
        JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file + LocalFolderSharePointClient.SidecarSuffix)).GetProperty("fields")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private async Task<Record> ReleasedDocAsync(string title)
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, title)).Id;
        await ReleaseAsync(fx, id);
        using var c = fx.As("pshah");
        return await c.Db().Records.AsNoTracking().FirstAsync(r => r.Id == id);
    }

    private async Task<SharePointPanelModel?> PanelAsync(Guid id, string user = "pshah")
    {
        using var s = fx.As(user);
        var snap = await s.LoadAsync(id);
        var behavior = s.ServiceProvider.GetServices<RecordBehavior>().OfType<SharePointBehavior>().Single();
        var panels = await behavior.PanelsAsync(new RecordContext(snap, s.ServiceProvider));
        return panels.SingleOrDefault()?.Model as SharePointPanelModel;
    }

    /// <summary>New Draft revision of an effective document, ready to send through the minor-change shortcut (DOC-031).</summary>
    private async Task<Guid> NewRevisionAsync(Guid fromId, byte[]? newPrimary = null)
    {
        Guid revId;
        using (var dc = fx.As("pshah"))
        {
            var svc = dc.ServiceProvider.GetRequiredService<DocumentControlService>();
            revId = (await svc.CreateRevisionAsync(await dc.LoadAsync(fromId), dc.UserId("pshah"), [dc.UserId("jlee")], null,
                "Test revision", null, "Test revision")).Id;
        }
        using (var author = fx.As("jlee"))
        {
            var records = author.ServiceProvider.GetRequiredService<RecordService>();
            Assert.True((await records.UpdateAsync(revId, new()
            {
                ["minor_change"] = true, ["training_required"] = false, ["training_justification"] = "Editorial change.",
            })).Ok);
            if (newPrimary != null)
            {
                var snap = await author.LoadAsync(revId);
                var save = await records.SaveAsync(snap, new RecordData(), new SaveOptions
                {
                    System = true, Uploads = [("primary_file", "revised.pdf", "application/pdf", new MemoryStream(newPrimary))],
                    RemoveAttachmentIds = snap.AttachmentsOf("primary_file").Select(a => a.Id).ToList(),
                });
                Assert.True(save.Ok, string.Join("; ", save.Errors.Select(e => e.Message)));
            }
            Ok(await author.RouteAsync(revId, DocKeys.AwaitingRelease, comment: "Editorial change"));
        }
        using (var dc = fx.As("pshah")) Ok(await dc.RouteAsync(revId, DocKeys.Approved));
        return revId;
    }

    // ------------------------------------------------------------------ INT-304, INT-303, INT-307

    [Fact] // INT-304, INT-303, INT-307, DOC-084, SAF-043
    public async Task Routing_into_the_phase_publishes_the_file_with_mapped_metadata_and_saving_republishes()
    {
        await using var cfg = await ConfigureAsync();
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "SP gowning: entry/exit")).Id;

        // Not effective yet: the record page says why nothing is published, and no work was queued for the earlier phases.
        await ToAwaitingReleaseAsync(fx, id);
        Assert.Empty(await JobsAsync(id));
        using (var dc = fx.As("pshah")) Ok(await dc.RouteAsync(id, DocKeys.Approved));

        var jobs = await JobsAsync(id);
        var queued = Assert.Single(jobs);
        Assert.Equal(SpJobStatus.Pending, queued.Status);
        Assert.Equal("Routed into Approved", queued.Trigger);
        Assert.Equal("pending", (await PanelAsync(id))!.State); // INT-306 indicator

        var done = Assert.Single(await RunAsync(id));
        Assert.True(done.Status == SpJobStatus.Succeeded, done.Error);

        Record doc;
        using (var c = fx.As("pshah")) doc = await c.Db().Records.AsNoTracking().FirstAsync(r => r.Id == id);
        // INT-304: ":" and "/" aren't allowed in SharePoint names and became underscores; the missing folder was created.
        var file = Path.Combine(cfg.Library, "Procedure", $"{doc.Number} - SP gowning_ entry_exit.pdf");
        Assert.True(File.Exists(file), file);
        Assert.Equal("%PDF-1.4 SP gowning: entry/exit", await File.ReadAllTextAsync(file));
        var meta = Sidecar(file);
        Assert.Equal(doc.Number, meta["DocumentNumber"].GetString());
        Assert.Equal("A", meta["Revision"].GetString());
        Assert.Equal("Procedure", meta["DocumentType"].GetString());
        Assert.Equal("Effective", meta["DocumentStatus"].GetString());
        Assert.Equal("Operations", meta["Department"].GetString());
        Assert.Equal($"{doc.EffectiveDate:yyyy-MM-dd}T00:00:00Z", meta["EffectiveDate"].GetString()); // INT-303 date and time

        var pub = Assert.Single(await PubsAsync(id));
        Assert.Equal(SpPublicationStatus.Published, pub.Status);
        Assert.Equal("A", pub.Revision);
        Assert.Equal("Procedure", pub.FolderPath);
        using (var c = fx.As("pshah"))
            Assert.True(await c.Db().Audit.AnyAsync(a => a.RecordId == id && a.Action == "Published to SharePoint"));
        var panel = (await PanelAsync(id))!;
        Assert.Equal("published", panel.State);
        Assert.Single(panel.Current);

        // Saved within the phase (administrative override): the metadata is republished to the same file.
        using (var m = fx.As("sortiz"))
        {
            var snap = await m.LoadAsync(id);
            var proposed = new RecordData();
            proposed.Main.Set("department", FieldCodec.FromObject(snap.Field("department")!, "quality", snap.Config));
            var res = await m.ServiceProvider.GetRequiredService<RecordService>().SaveAsync(snap, proposed, new SaveOptions { AdminOverride = true, Reason = "Department correction" });
            Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
        }
        var resave = Assert.Single(await RunAsync(id));
        Assert.Equal(SpJobStatus.Succeeded, resave.Status);
        Assert.Equal("Saved in Approved", resave.Trigger);
        Assert.Equal("Quality", Sidecar(file)["Department"].GetString());
        var after = Assert.Single(await PubsAsync(id));
        Assert.Equal(pub.Id, after.Id);
        Assert.NotNull(after.UpdatedAt);
    }

    // ------------------------------------------------------------------ DOC-084, INT-305, INV-163

    [Fact] // DOC-084, INT-305, INV-163
    public async Task A_pending_revision_never_overwrites_the_completed_revisions_published_file()
    {
        await using var cfg = await ConfigureAsync(r => { r.FolderTemplate = "Docs"; r.FileNameTemplate = "{number}"; });
        var a = await ReleasedDocAsync("SP never overwrite");
        Assert.Equal(SpJobStatus.Succeeded, Assert.Single(await RunAsync(a.Id)).Status);
        var file = Path.Combine(cfg.Library, "Docs", a.Number + ".pdf");
        var original = await File.ReadAllBytesAsync(file);

        // A second rule also publishes drafts (pending revisions) to the very same location.
        using (var s = fx.As("admin"))
        {
            var res = await s.ServiceProvider.GetRequiredService<SharePointAdminService>().SaveRuleAsync(null, new RuleInput
            {
                Name = "Drafts " + cfg.Host, FormKey = DocKeys.Document, PhaseKey = DocKeys.Draft, OnEnter = true, OnSave = true,
                ProfileId = cfg.ProfileId, LibraryId = cfg.LibraryId, FieldKeys = ["primary_file"], FolderTemplate = "Docs", FileNameTemplate = "{number}",
                EffectiveOnly = false,
            });
            Assert.True(res.Ok, res.Error);
            cfg.ExtraRules.Add(res.Id!.Value);
        }
        Guid revB;
        using (var dc = fx.As("pshah"))
        {
            var svc = dc.ServiceProvider.GetRequiredService<DocumentControlService>();
            revB = (await svc.CreateRevisionAsync(await dc.LoadAsync(a.Id), dc.UserId("pshah"), [dc.UserId("jlee")], null, "Pending change", null, "Pending change")).Id;
        }
        using (var author = fx.As("jlee"))
        {
            var snap = await author.LoadAsync(revB);
            var save = await author.ServiceProvider.GetRequiredService<RecordService>().SaveAsync(snap, new RecordData(), new SaveOptions
            {
                Uploads = [("primary_file", "draft.pdf", "application/pdf", new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 DRAFT B")))],
                RemoveAttachmentIds = snap.AttachmentsOf("primary_file").Select(x => x.Id).ToList(),
            });
            Assert.True(save.Ok, string.Join("; ", save.Errors.Select(e => e.Message)));
        }

        var jobs = await RunAsync(revB);
        Assert.NotEmpty(jobs);
        Assert.All(jobs, j =>
        {
            Assert.Equal(SpJobStatus.Failed, j.Status);
            Assert.Equal(SpErrors.NeverOverwrite, j.ErrorCode);
        });
        Assert.Equal(original, await File.ReadAllBytesAsync(file)); // still revision A's content
        Assert.Empty(await PubsAsync(revB));
        Assert.Equal(SpPublicationStatus.Published, Assert.Single(await PubsAsync(a.Id)).Status);
        Assert.Equal("A", Sidecar(file)["Revision"].GetString());
    }

    // ------------------------------------------------------------------ INT-305, INT-308, SAF-021

    [Fact] // INT-305, INT-308, SAF-021
    public async Task Releasing_the_next_revision_removes_the_superseded_revisions_file()
    {
        await using var cfg = await ConfigureAsync(r => { r.FileNameTemplate = "{number} rev {revision}"; r.WithdrawAction = SpWithdrawAction.Delete; });
        var a = await ReleasedDocAsync("SP supersede delete");
        await RunAsync(a.Id);
        var fileA = Path.Combine(cfg.Library, "Procedure", $"{a.Number} rev A.pdf");
        Assert.True(File.Exists(fileA));

        var revB = await NewRevisionAsync(a.Id);
        // Revision A was superseded in the same transaction; its withdrawal is queued alongside B's publication.
        Assert.Contains(await JobsAsync(a.Id), j => j.Kind == SpJobKind.Withdraw && j.Status == SpJobStatus.Pending);
        var jobs = await RunAsync(revB, a.Id);
        Assert.All(jobs, j => Assert.True(j.Status == SpJobStatus.Succeeded, j.Error));

        Assert.True(File.Exists(Path.Combine(cfg.Library, "Procedure", $"{a.Number} rev B.pdf")));
        Assert.False(File.Exists(fileA));
        Assert.False(File.Exists(fileA + LocalFolderSharePointClient.SidecarSuffix));
        var pubA = Assert.Single(await PubsAsync(a.Id));
        Assert.Equal(SpPublicationStatus.Removed, pubA.Status);
        Assert.NotNull(pubA.WithdrawnAt);
        using (var c = fx.As("pshah"))
            Assert.True(await c.Db().Audit.AnyAsync(x => x.RecordId == a.Id && x.Action == "Withdrawn from SharePoint"));
        Assert.Equal("withdrawn", (await PanelAsync(a.Id))!.State);
    }

    [Fact] // INT-305 (replace or version), INT-307
    public async Task A_new_effective_revision_overwrites_in_place_and_keeps_version_history()
    {
        await using var cfg = await ConfigureAsync(r => r.KeepVersionHistory = true);
        var a = await ReleasedDocAsync("SP versioned");
        await RunAsync(a.Id);
        var file = Path.Combine(cfg.Library, "Procedure", $"{a.Number} - SP versioned.pdf");

        var revB = await NewRevisionAsync(a.Id, Encoding.UTF8.GetBytes("%PDF-1.4 revision B"));
        Assert.All(await RunAsync(revB, a.Id), j => Assert.True(j.Status == SpJobStatus.Succeeded, j.Error));

        Assert.Equal("%PDF-1.4 revision B", await File.ReadAllTextAsync(file));
        Assert.Equal("B", Sidecar(file)["Revision"].GetString());
        var versions = Path.Combine(cfg.Library, LocalFolderSharePointClient.VersionsFolder, "Procedure", Path.GetFileName(file));
        Assert.Contains(Directory.GetFiles(versions), v => File.ReadAllText(v) == "%PDF-1.4 SP versioned");
        var pubA = Assert.Single(await PubsAsync(a.Id));
        Assert.Equal(SpPublicationStatus.Replaced, pubA.Status);
        Assert.Equal(Assert.Single(await PubsAsync(revB)).Id, pubA.ReplacedById);
    }

    [Fact] // INT-308, SAF-021, DOC-103
    public async Task Obsoleting_a_document_marks_its_published_file_obsolete()
    {
        await using var cfg = await ConfigureAsync(r => { r.WithdrawAction = SpWithdrawAction.MarkObsolete; r.ObsoleteFolder = "Obsolete"; });
        var a = await ReleasedDocAsync("SP obsolete");
        await RunAsync(a.Id);
        var file = Path.Combine(cfg.Library, "Procedure", $"{a.Number} - SP obsolete.pdf");
        Assert.True(File.Exists(file));

        using (var s = fx.Services.CreateScope())
        {
            s.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("SharePoint test");
            Ok(await s.ServiceProvider.GetRequiredService<WorkflowEngine>().SystemRouteAsync(a.Id, DocKeys.Obsolete, "Withdrawn from use (test)"));
        }
        var jobs = await RunAsync(a.Id);
        var withdraw = Assert.Single(jobs);
        Assert.Equal(SpJobKind.Withdraw, withdraw.Kind);
        Assert.True(withdraw.Status == SpJobStatus.Succeeded, withdraw.Error);

        Assert.False(File.Exists(file));
        var moved = Path.Combine(cfg.Library, "Obsolete", $"{a.Number} - SP obsolete (rev A obsolete).pdf");
        Assert.True(File.Exists(moved), moved);
        Assert.Equal("Obsolete", Sidecar(moved)["DocumentStatus"].GetString());
        var pub = Assert.Single(await PubsAsync(a.Id));
        Assert.Equal(SpPublicationStatus.MarkedObsolete, pub.Status);
        Assert.Equal("Obsolete", pub.FolderPath);
    }

    [Fact] // SAF-021: the reconciliation sweep is a durable follow-up even if an event was missed
    public async Task Reconciliation_queues_withdrawal_for_files_of_archived_records()
    {
        await using var cfg = await ConfigureAsync();
        var a = await ReleasedDocAsync("SP reconcile");
        await RunAsync(a.Id);
        using (var s = fx.Services.CreateScope())
        {
            // Simulate a supersession that bypassed the behavior (e.g. a data fix).
            await s.Db().Records.Where(r => r.Id == a.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsArchived, true).SetProperty(r => r.ArchiveReason, "superseded").SetProperty(r => r.IsEffective, false));
            s.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("SharePoint test");
            Assert.True(await s.ServiceProvider.GetRequiredService<SharePointPublisher>().ReconcileAsync() >= 1);
        }
        var job = Assert.Single(await RunAsync(a.Id));
        Assert.Equal(SpJobKind.Withdraw, job.Kind);
        Assert.Equal(SpPublicationStatus.Removed, Assert.Single(await PubsAsync(a.Id)).Status);
    }

    // ------------------------------------------------------------------ NFR-025, INT-306

    [Fact] // NFR-025, INT-306
    public async Task SharePoint_outages_retry_with_backoff_then_fail_permanently_and_notify()
    {
        await using var cfg = await ConfigureAsync(outage: true);
        // Routing works while SharePoint is down: publishing is only queued.
        var a = await ReleasedDocAsync("SP outage");

        var before = DateTime.UtcNow;
        var first = Assert.Single(await RunAsync(a.Id));
        Assert.Equal(SpJobStatus.Pending, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(SpErrors.Unavailable, first.ErrorCode);
        Assert.InRange(first.NextAttemptAt, before.AddSeconds(45), DateTime.UtcNow.AddSeconds(75)); // ~1 minute
        Assert.Equal("pending", (await PanelAsync(a.Id))!.State);

        using (var s = fx.Services.CreateScope())
            await s.Db().Set<SharePointJob>().Where(j => j.Id == first.Id).ExecuteUpdateAsync(x => x.SetProperty(j => j.MaxAttempts, 3));
        before = DateTime.UtcNow;
        var second = Assert.Single(await RunAsync(a.Id));
        Assert.Equal(SpJobStatus.Pending, second.Status);
        Assert.InRange(second.NextAttemptAt, before.AddSeconds(100), DateTime.UtcNow.AddSeconds(140)); // ~2 minutes: exponential
        var third = Assert.Single(await RunAsync(a.Id));
        Assert.Equal(SpJobStatus.Failed, third.Status);
        Assert.Equal(3, third.Attempts);

        using (var s = fx.As("pshah"))
        {
            var db = s.Db();
            Assert.Equal(3, await db.Set<SharePointAttempt>().CountAsync(x => x.JobId == first.Id && !x.Success));
            var note = await db.Notifications.AsNoTracking().Where(n => n.UserId == s.UserId("pshah") && n.RecordId == a.Id && n.Title.Contains("SharePoint publishing failed")).ToListAsync();
            Assert.Single(note);
            Assert.Contains("SP-UNAVAILABLE", note[0].Body);
            Assert.True(await db.EmailLog.AnyAsync(e => e.RecordId == a.Id && e.Subject.Contains("SharePoint publishing failed")));
            Assert.True(await db.Audit.AnyAsync(x => x.RecordId == a.Id && x.Action == "SharePoint publishing failed"));
        }
        var failedPanel = (await PanelAsync(a.Id))!;
        Assert.Equal("failed", failedPanel.State);
        Assert.Equal(SpErrors.Unavailable, failedPanel.Failed!.Job.ErrorCode);

        // SharePoint is back: an administrator retries and it goes through.
        using (var s = fx.As("admin"))
        {
            var admin = s.ServiceProvider.GetRequiredService<SharePointAdminService>();
            var p = await s.Db().Set<SharePointProfile>().AsNoTracking().FirstAsync(x => x.Id == cfg.ProfileId);
            Assert.True((await admin.SaveProfileAsync(p.Id, new ProfileInput { Name = p.Name, Host = p.Host, Connection = p.Connection, NotifyUserIds = p.NotifyUserIds, SimulateOutage = false })).Ok);
            Assert.True((await admin.RetryAsync(first.Id)).Ok);
        }
        var retried = Assert.Single(await RunAsync(a.Id));
        Assert.Equal(SpJobStatus.Succeeded, retried.Status);
        Assert.Equal("published", (await PanelAsync(a.Id))!.State);
    }

    // ------------------------------------------------------------------ INT-302

    [Fact] // INT-302
    public async Task Mapped_columns_must_exist_and_system_columns_are_refused()
    {
        using (var s = fx.As("admin"))
        {
            var admin = s.ServiceProvider.GetRequiredService<SharePointAdminService>();
            var sys = await admin.SaveMappingAsync(null, new MappingInput { FormKey = DocKeys.Document, Name = "Bad", Entries = [("$number", "Created")] });
            Assert.False(sys.Ok);
            Assert.Contains(sys.Errors, e => e.Contains("system column"));
            var unknown = await admin.SaveMappingAsync(null, new MappingInput { FormKey = DocKeys.Document, Name = "Bad", Entries = [("no_such_field", "Something")] });
            Assert.Contains(unknown.Errors, e => e.Contains("no_such_field"));
            var dup = await admin.SaveMappingAsync(null, new MappingInput { FormKey = DocKeys.Document, Name = "Bad", Entries = [("$number", "Code"), ("$revision", "Code")] });
            Assert.Contains(dup.Errors, e => e.Contains("more than once"));
        }

        // The library has no Department column, and column names are case-sensitive.
        await using var cfg = await ConfigureAsync(columns: "Title\ndocumentnumber\nRevision\nDocumentType\nDocumentStatus\nEffectiveDate");
        using (var s = fx.As("admin"))
        {
            var check = await s.ServiceProvider.GetRequiredService<SharePointAdminService>().CheckMappingAsync(cfg.MappingId!.Value, cfg.LibraryId);
            Assert.False(check.Ok);
            Assert.Contains(check.Problems, p => p.Contains("\"Department\""));
            Assert.Contains(check.Problems, p => p.Contains("case-sensitive") && p.Contains("documentnumber"));
        }
        var a = await ReleasedDocAsync("SP column check");
        var job = Assert.Single(await RunAsync(a.Id));
        Assert.Equal(SpJobStatus.Failed, job.Status); // a configuration error isn't retried
        Assert.Equal(SpErrors.Column, job.ErrorCode);
        Assert.Empty(await PubsAsync(a.Id));
    }

    [Fact] // INT-302: no mapping profile → names that match columns are filled in automatically
    public async Task Without_a_mapping_profile_matching_columns_are_filled_automatically()
    {
        await using var cfg = await ConfigureAsync(mapping: false, columns: "title\ndepartment\nnumber\nTitle\nUnrelated");
        var a = await ReleasedDocAsync("SP automatic");
        Assert.Equal(SpJobStatus.Succeeded, Assert.Single(await RunAsync(a.Id)).Status);
        var meta = Sidecar(Path.Combine(cfg.Library, "Procedure", $"{a.Number} - SP automatic.pdf"));
        Assert.Equal(["department", "number", "title"], meta.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.Equal("SP automatic", meta["title"].GetString());
        Assert.Equal(a.Number, meta["number"].GetString());
    }

    // ------------------------------------------------------------------ renditions

    [Fact] // DOC-084, SAF-063: the PDF rendition is published when present
    public async Task The_pdf_rendition_is_published_and_publishing_waits_for_it_when_originals_are_not_allowed()
    {
        await using var cfg = await ConfigureAsync(r => { r.PreferRendition = true; r.FallbackToOriginal = false; });
        Guid id;
        using (var s = fx.As("jlee"))
        {
            var doc = await NewDocAsync(s, "SP rendition");
            id = doc.Id;
            var snap = await s.LoadAsync(id);
            var save = await s.ServiceProvider.GetRequiredService<RecordService>().SaveAsync(snap, new RecordData(), new SaveOptions
            {
                Uploads = [("primary_file", "procedure.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", new MemoryStream(Encoding.UTF8.GetBytes("DOCX bytes")))],
                RemoveAttachmentIds = snap.AttachmentsOf("primary_file").Select(x => x.Id).ToList(),
            });
            Assert.True(save.Ok, string.Join("; ", save.Errors.Select(e => e.Message)));
        }
        await ReleaseAsync(fx, id);
        var waiting = Assert.Single(await RunAsync(id));
        Assert.Equal(SpJobStatus.Pending, waiting.Status); // retried later, the rendition may still be generating
        Assert.Equal(SpErrors.RenditionPending, waiting.ErrorCode);

        long originalId, renditionId;
        using (var s = fx.Services.CreateScope())
        {
            s.ServiceProvider.GetRequiredService<CurrentUser>().UseSystem("Rendition test");
            var db = s.Db();
            var original = await db.Attachments.FirstAsync(x => x.RecordId == id && x.FieldKey == "primary_file" && !x.Removed && x.RenditionOfId == null);
            var (sha, size) = await s.ServiceProvider.GetRequiredService<FileStore>().SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7 rendition")));
            var rendition = new Attachment
            {
                RecordId = id, FieldKey = "primary_file", FileName = "procedure.pdf", ContentType = "application/pdf", Sha256 = sha, Size = size,
                UploadedById = s.ServiceProvider.GetRequiredService<UserDirectory>().SystemUserId, UploadedAt = DateTime.UtcNow, RenditionOfId = original.Id,
            };
            db.Attachments.Add(rendition);
            await db.SaveChangesAsync();
            (originalId, renditionId) = (original.Id, rendition.Id);
        }
        var done = Assert.Single(await RunAsync(id));
        Assert.True(done.Status == SpJobStatus.Succeeded, done.Error);
        Record r;
        using (var c = fx.As("pshah")) r = await c.Db().Records.AsNoTracking().FirstAsync(x => x.Id == id);
        var file = Path.Combine(cfg.Library, "Procedure", $"{r.Number} - SP rendition.pdf");
        Assert.Equal("%PDF-1.7 rendition", await File.ReadAllTextAsync(file));
        var pub = Assert.Single(await PubsAsync(id));
        Assert.True(pub.IsRendition);
        Assert.Equal(renditionId, pub.AttachmentId);
        Assert.Equal(originalId, pub.OriginalAttachmentId);
    }

    // ------------------------------------------------------------------ INT-301

    [Fact] // INT-301, DSN-1202
    public async Task Profiles_are_unique_by_host_secrets_are_encrypted_and_changes_are_audited()
    {
        using var s = fx.As("admin");
        var admin = s.ServiceProvider.GetRequiredService<SharePointAdminService>();
        var host = "contoso" + Guid.NewGuid().ToString("N")[..6] + ".sharepoint.com";
        const string secret = "Xy7~verysecret-client-secret-value";
        var res = await admin.SaveProfileAsync(null, new ProfileInput
        {
            Name = "Contoso QMS", Host = "https://" + host.ToUpperInvariant() + "/sites/QMS", Connection = SpConnection.Graph,
            TenantId = "11111111-2222-3333-4444-555555555555", ClientId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", AuthMethod = SpAuthMethod.ClientSecret, Secret = secret,
        });
        Assert.True(res.Ok, res.Error);
        var p = await s.Db().Set<SharePointProfile>().AsNoTracking().FirstAsync(x => x.Id == res.Id);
        Assert.Equal(host, p.Host); // normalized from the pasted URL
        Assert.NotNull(p.SecretProtected);
        Assert.DoesNotContain(secret, p.SecretProtected);
        Assert.Equal(secret, s.ServiceProvider.GetRequiredService<SharePointSecrets>().CredentialFor(p).Secret);

        var audits = await s.Db().ConfigAudit.AsNoTracking().Where(a => a.ObjectType == "SharePointProfile" && a.ObjectKey == host).ToListAsync();
        Assert.Single(audits);
        Assert.DoesNotContain(secret, audits[0].After);
        Assert.DoesNotContain(p.SecretProtected, audits[0].After);

        // Saving again without a secret keeps the stored one (it's never shown back).
        Assert.True((await admin.SaveProfileAsync(p.Id, new ProfileInput
        {
            Name = "Contoso QMS (renamed)", Host = host, Connection = SpConnection.Graph, TenantId = p.TenantId, ClientId = p.ClientId, AuthMethod = SpAuthMethod.ClientSecret,
        })).Ok);
        Assert.Equal(p.SecretProtected, (await s.Db().Set<SharePointProfile>().AsNoTracking().FirstAsync(x => x.Id == p.Id)).SecretProtected);

        var dup = await admin.SaveProfileAsync(null, new ProfileInput { Name = "Duplicate", Host = host, Connection = SpConnection.LocalFolder });
        Assert.False(dup.Ok);
        Assert.Contains(dup.Errors, e => e.Contains("unique by host"));

        // Non-administrators can't change anything.
        using (var user = fx.As("jlee"))
            Assert.False((await user.ServiceProvider.GetRequiredService<SharePointAdminService>().DeleteProfileAsync(p.Id)).Ok);

        // Deleting removes the libraries too (module keys never cascade in the database); mappings likewise.
        Assert.True((await admin.SaveLibraryAsync(p.Id, null, new LibraryInput { Name = "Policies", RelativeUrl = "/sites/QMS/Policies" })).Ok);
        var deleted = await admin.DeleteProfileAsync(p.Id);
        Assert.True(deleted.Ok, deleted.Error);
        Assert.False(await s.Db().Set<SharePointLibrary>().AnyAsync(l => l.ProfileId == p.Id));
        var mapping = await admin.SaveMappingAsync(null, new MappingInput { FormKey = DocKeys.Document, Name = "Disposable", Entries = [("$number", "Code")] });
        Assert.True(mapping.Ok, mapping.Error);
        Assert.True((await admin.DeleteMappingAsync(mapping.Id!.Value)).Ok);
        Assert.Contains(await s.Db().ConfigAudit.AsNoTracking().Where(a => a.ObjectType == "SharePointProfile" && a.ObjectKey == host).Select(a => a.Action).ToListAsync(), a => a == "Deleted");
    }
}
