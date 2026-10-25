using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Modules.Designer;

namespace Vellum.Tests;

/// <summary>Designer: draft → validate → publish (DSN-003, DSN-1201..1203, SAF-070/071, INV-135, INV-145).</summary>
[Collection("vellum")]
public class DesignerTests(VellumFixture fx)
{
    private static DesignerStore Store(IServiceScope s) => s.ServiceProvider.GetRequiredService<DesignerStore>();
    private static DesignerPublisher Publisher(IServiceScope s) => s.ServiceProvider.GetRequiredService<DesignerPublisher>();
    private static ConfigModel Cfg(IServiceScope s) => s.ServiceProvider.GetRequiredService<ConfigCache>().Current;

    /// <summary>A throw-away application (its own form and workflow) so tests never change the packaged apps others rely on.</summary>
    private async Task<string> NewPublishedAppAsync(IServiceScope s)
    {
        var key = "dz" + Guid.NewGuid().ToString("N")[..8];
        var store = Store(s);
        var doc = DesignerOps.NewApp("Designer Test", key, "Created by DesignerTests", "bi-vector-pen", "#0f766e", "Operations",
            Cfg(s).FormsByKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        await store.CreateAppDraftAsync(doc);
        var res = await Publisher(s).PublishAsync(key, "Initial version", acceptWarnings: true);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        return key;
    }

    [Fact] // DSN-003, DSN-1202, DSN-1203, INV-145
    public async Task Draft_changes_stay_out_of_live_configuration_until_published()
    {
        using var s = fx.As("admin");
        var key = await NewPublishedAppAsync(s);
        var field = Cfg(s).Form(key).Fields.Single(f => f.Key == "title");
        var fieldId = field.Id;
        var versionBefore = Cfg(s).Version;

        await Store(s).MutateAsync(key, d => d.Forms[0].Sections[0].Fields.First(f => f.Key == "title").Label = "Headline");
        await Store(s).MutateAsync(key, d => d.Forms[0].Workflows[0].Phases.First(p => p.Key == "review").DueDays = 9);

        // Live configuration is untouched by the draft.
        Assert.Equal("Title", Cfg(s).Form(key).Fields.Single(f => f.Key == "title").Label);
        Assert.Equal(5, Cfg(s).Form(key).Workflows[0].Phases.Single(p => p.Key == "review").DueDays);
        Assert.Equal(versionBefore, Cfg(s).Version);

        var res = await Publisher(s).PublishAsync(key, "Rename title, longer review", acceptWarnings: true);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.Equal(versionBefore + 1, res.Version);

        var live = Cfg(s).Form(key);
        var renamed = live.Fields.Single(f => f.Key == "title");
        Assert.Equal("Headline", renamed.Label);
        Assert.Equal(fieldId, renamed.Id); // updated in place: stable IDs
        Assert.Equal(9, live.Workflows[0].Phases.Single(p => p.Key == "review").DueDays);
        Assert.Equal(res.Version, Cfg(s).Version);

        // One configuration audit entry per changed object, stamped with the new version, with before/after JSON.
        var entries = await s.Db().ConfigAudit.AsNoTracking().Where(e => e.ConfigVersion == res.Version).ToListAsync();
        var fieldEntry = Assert.Single(entries, e => e.ObjectType == "Field");
        Assert.Equal($"{key}/{key}.title", fieldEntry.ObjectKey);
        Assert.Contains("\"Title\"", fieldEntry.Before);
        Assert.Contains("\"Headline\"", fieldEntry.After);
        Assert.Single(entries, e => e.ObjectType == "Phase");
        Assert.True(entries.Count(e => e.ObjectType != "Publication") == 2, string.Join(" | ", entries.Select(e => $"{e.ObjectType} {e.ObjectKey}: {e.Before} => {e.After}")));
        Assert.True(await s.Db().ConfigVersions.AnyAsync(v => v.Number == res.Version && v.PublishedById == s.UserId("admin")));
        Assert.True(await Store(s).Publications.AnyAsync(p => p.AppKey == key && p.VersionNumber == res.Version && p.Changes == 2));
        Assert.False(await Store(s).Drafts.AnyAsync(d => d.AppKey == key)); // the draft is consumed
    }

    [Fact] // DSN-1201, INV-135, DSN-612
    public async Task Publish_validation_blocks_overlapping_sections_and_unreachable_phases()
    {
        using var s = fx.As("admin");
        var key = await NewPublishedAppAsync(s);
        var version = Cfg(s).Version;

        await Store(s).MutateAsync(key, d =>
        {
            var review = d.Forms[0].Workflows[0].Phases.First(p => p.Key == "review");
            review.EditableSections = ["details"];
            review.ReadOnlySections = ["details"];
        });
        var res = await Publisher(s).PublishAsync(key, "Overlap", acceptWarnings: true);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Contains("both editable and read-only"));

        await Store(s).MutateAsync(key, d =>
        {
            var wf = d.Forms[0].Workflows[0];
            wf.Phases.First(p => p.Key == "review").ReadOnlySections = ["*"];
            wf.Phases.First(p => p.Key == "review").EditableSections = [];
            wf.Phases.Insert(2, new PhaseDoc { Key = "orphan", Name = "Orphan", Type = PhaseType.Normal, DueDays = 3 });
        });
        res = await Publisher(s).PublishAsync(key, "Unreachable", acceptWarnings: true);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Contains("can't be reached"));
        Assert.Contains(res.Errors, e => e.Contains("dead end"));
        Assert.Equal(version, Cfg(s).Version); // nothing was published
        Assert.DoesNotContain(Cfg(s).Form(key).Workflows[0].Phases, p => p.Key == "orphan");

        await Store(s).DiscardAsync(key);
        Assert.False(await Store(s).Drafts.AnyAsync(d => d.AppKey == key));
    }

    [Fact] // DSN-601, INV-071, SAF-071
    public async Task Deleting_a_phase_that_holds_records_is_blocked()
    {
        using var s = fx.As("admin");
        var key = await NewPublishedAppAsync(s);
        var record = await s.CreateAsync(key, new() { ["title"] = "In the draft phase" });
        Assert.Equal("draft", Cfg(s).Phase(record.PhaseId)!.Key);

        await Store(s).MutateAsync(key, d =>
        {
            var wf = d.Forms[0].Workflows[0];
            wf.Phases.RemoveAll(p => p.Key == "draft");
            wf.Phases.First(p => p.Key == "review").IsStart = true;
            wf.Routes.RemoveAll(r => r.From == "draft" || r.To == "draft");
        });
        var t = (await Store(s).OpenAsync(key))!;
        var plan = await Publisher(s).PlanAsync(t);
        Assert.Contains(plan.Impact, i => i.Tone == "bad" && i.Count == 1);
        Assert.False(plan.CanPublish);

        var res = await Publisher(s).PublishAsync(key, "Drop draft phase", acceptWarnings: true);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Contains("can't be deleted") && e.Contains("Move them"));
        Assert.Contains(Cfg(s).Form(key).Workflows[0].Phases, p => p.Key == "draft");

        // A change that keeps the phase publishes, and the record stays in the same phase row (stable IDs).
        await Store(s).DiscardAsync(key);
        await Store(s).MutateAsync(key, d => d.Forms[0].Workflows[0].Phases.First(p => p.Key == "draft").Name = "Drafting");
        res = await Publisher(s).PublishAsync(key, "Rename phase", acceptWarnings: true);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        var snap = await s.LoadAsync(record.Id);
        Assert.Equal("Drafting", snap.Phase!.Name);
        Assert.Equal(record.PhaseId, snap.Record.PhaseId);
    }

    [Fact] // DSN-1201: the shipped reference applications pass the designer's own publish checks.
    public async Task Packaged_reference_apps_validate_without_errors()
    {
        using var s = fx.As("admin");
        foreach (var key in new[] { "capa", "actions" })
        {
            var t = await Store(s).OpenAsync(key);
            Assert.NotNull(t);
            var (_, _, issues) = await Store(s).ValidateAsync(t!);
            Assert.DoesNotContain(issues, i => i.IsError);
            Assert.Empty(DesignDiff.Compare(t!.LiveDoc, DesignDocs.Clone(t.LiveDoc!)));
            var svg = WorkflowDiagram.Render(t.Doc.Forms.First(f => f.Workflows.Count > 0).Workflows[0]);
            Assert.StartsWith("<svg", svg);
        }
    }

    [Fact] // DSN-001: designer rights are per application.
    public async Task Designer_rights_are_per_application()
    {
        using var s = fx.As("sortiz");
        Assert.NotNull(await Store(s).OpenAsync("capa"));
        Assert.Null(await Store(s).OpenAsync("actions"));
        var res = await Publisher(s).PublishAsync("actions", "Not allowed", acceptWarnings: true);
        Assert.False(res.Ok);
    }
}
