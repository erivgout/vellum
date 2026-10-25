using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Modules.Documents;
using Vellum.Modules.Training;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>Document Control §10 against the real engine: numbering, workflow rules, signatures, release, supersession and requests.</summary>
[Collection("vellum")]
public class DocumentControlTests(VellumFixture fx)
{
    internal static async Task<Record> NewDocAsync(IServiceScope s, string title, string type = "procedure", bool? training = false,
        Dictionary<string, object?>? extra = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["doc_type"] = type, ["title"] = title, ["owner"] = s.UserId("sortiz"),
            ["reviewers"] = new List<int> { s.UserId("dokafor"), s.UserId("tbrooks") },
            ["department"] = "operations", ["revision_reason"] = "New document for the test.", ["training_required"] = training,
            ["training_justification"] = training == false ? "Administrative content only; no new tasks." : null,
        };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        var res = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = DocKeys.Document, Values = values,
            Files = [("primary_file", $"{title}.pdf", "application/pdf", new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 " + title)))],
        });
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => $"{e.FieldKey}: {e.Message}")));
        return res.Record!;
    }

    internal static void Ok(RouteResult r) => Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));

    /// <summary>Draft → … → Awaiting Release through every signed approval (DOC-030).</summary>
    internal static async Task ToAwaitingReleaseAsync(VellumFixture fx, Guid id, string author = "jlee")
    {
        using (var a = fx.As(author)) Ok(await a.RouteAsync(id, DocKeys.DcReview));
        using (var dc = fx.As("pshah")) Ok(await dc.RouteAsync(id, DocKeys.InitialApproval));
        using (var o = fx.As("sortiz")) Ok(await o.RouteAsync(id, DocKeys.Review));
        using (var r1 = fx.As("dokafor")) Ok(await r1.RouteAsync(id, DocKeys.FinalApproval));
        using (var r2 = fx.As("tbrooks")) Ok(await r2.RouteAsync(id, DocKeys.FinalApproval));
        using (var o = fx.As("sortiz")) Ok(await o.RouteAsync(id, DocKeys.AwaitingRelease));
    }

    internal static async Task ReleaseAsync(VellumFixture fx, Guid id, string author = "jlee")
    {
        await ToAwaitingReleaseAsync(fx, id, author);
        using var dc = fx.As("pshah");
        Ok(await dc.RouteAsync(id, DocKeys.Approved));
    }

    private static async Task<ActionOutcome> ActAsync(IServiceScope s, Guid id, string action, Dictionary<string, string[]> values, string? comment = null, string? password = null)
    {
        var snap = await s.LoadAsync(id);
        var behavior = s.ServiceProvider.GetServices<RecordBehavior>().OfType<DocumentBehavior>().Single();
        return await behavior.ExecuteAsync(new RecordContext(snap, s.ServiceProvider), action,
            new ActionInput { Values = values, Comment = comment, Password = password, Certify = true });
    }

    [Fact] // DOC-001, DOC-010, DOC-014, INV-020
    public async Task New_documents_are_numbered_from_their_type_profile()
    {
        using var s = fx.As("jlee");
        var sop = await NewDocAsync(s, "Numbering SOP");
        var wi = await NewDocAsync(s, "Numbering WI", "work_instruction");
        var spec = await NewDocAsync(s, "Numbering spec", "specification");
        Assert.StartsWith("SOP-", sop.Number);
        Assert.StartsWith("WI-", wi.Number);
        Assert.StartsWith("SPEC-", spec.Number);
        Assert.Equal("A", sop.Revision);
        Assert.Equal("1", spec.Revision); // the Specification profile uses Numeric from 1
        Assert.Equal(sop.Id, sop.FamilyId);
        var another = await NewDocAsync(s, "Numbering SOP 2");
        Assert.NotEqual(sop.Number, another.Number);
    }

    [Fact] // DOC-005, INV-101
    public async Task Type_profiles_are_manager_only_and_one_enabled_per_type()
    {
        using (var s = fx.As("jlee")) await NewDocAsync(s, "Profile bootstrap"); // installs the default profiles
        var values = new Dictionary<string, object?>
        {
            ["doc_type"] = "procedure", ["profile_name"] = "Second procedure profile", ["number_prefix"] = "PRC-", ["revision_format"] = "alphabetic", ["enabled"] = true,
        };
        using (var author = fx.As("jlee"))
        {
            var res = await author.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest { FormKey = DocKeys.Profile, Values = values });
            Assert.False(res.Ok);
            Assert.Contains(res.Errors, e => e.Message.Contains("DOC-005"));
        }
        using var mgr = fx.As("sortiz");
        var dup = await mgr.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest { FormKey = DocKeys.Profile, Values = values });
        Assert.False(dup.Ok);
        Assert.Contains(dup.Errors, e => e.Message.Contains("INV-101"));
        values["enabled"] = false;
        Assert.True((await mgr.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest { FormKey = DocKeys.Profile, Values = values })).Ok);
    }

    [Fact] // DOC-060, DOC-033
    public async Task Leaving_draft_needs_the_training_decision_and_a_justification_for_no()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Training decision", training: null)).Id;
        using var a = fx.As("jlee");
        var res = await a.RouteAsync(id, DocKeys.DcReview);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("DOC-060"));

        var records = a.ServiceProvider.GetRequiredService<RecordService>();
        Assert.True((await records.UpdateAsync(id, new() { ["training_required"] = false })).Ok);
        res = await a.RouteAsync(id, DocKeys.DcReview);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("justification"));

        Assert.True((await records.UpdateAsync(id, new() { ["training_justification"] = "Clarifies wording only." })).Ok);
        Ok(await a.RouteAsync(id, DocKeys.DcReview));
    }

    [Fact] // SAF-044
    public async Task Reviewers_must_be_independent_of_the_author()
    {
        Guid id;
        using (var s = fx.As("jlee"))
            id = (await NewDocAsync(s, "SoD check", extra: new() { ["reviewers"] = new List<int> { s.UserId("jlee"), s.UserId("tbrooks") } })).Id;
        using var a = fx.As("jlee");
        var res = await a.RouteAsync(id, DocKeys.DcReview);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("SAF-044"));
    }

    [Fact] // DOC-031
    public async Task Minor_change_shortcut_needs_the_flag_and_a_revision()
    {
        Guid plain, flagged;
        using (var s = fx.As("jlee"))
        {
            plain = (await NewDocAsync(s, "Minor shortcut: not flagged")).Id;
            flagged = (await NewDocAsync(s, "Minor shortcut: new document", extra: new() { ["minor_change"] = true })).Id;
        }
        using var a = fx.As("jlee");
        var notFlagged = await a.RouteAsync(plain, DocKeys.AwaitingRelease, comment: "Typo fix");
        Assert.False(notFlagged.Ok); // route condition minor_change=yes doesn't hold
        var newDoc = await a.RouteAsync(flagged, DocKeys.AwaitingRelease, comment: "Typo fix");
        Assert.False(newDoc.Ok);
        Assert.Contains(newDoc.Errors, e => e.Message.Contains("only for revisions"));
    }

    [Fact] // DOC-040, INV-113: release needs the Document Controller's e-signature
    public async Task Release_requires_an_electronic_signature()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Signed release")).Id;
        await ToAwaitingReleaseAsync(fx, id);
        using (var dc = fx.As("pshah"))
        {
            var bad = await dc.RouteAsync(id, DocKeys.Approved, password: "not-the-password");
            Assert.False(bad.Ok);
            Assert.False((await dc.LoadAsync(id)).Record.IsEffective);
            Ok(await dc.RouteAsync(id, DocKeys.Approved));
        }
        using var check = fx.As("pshah");
        var doc = await check.LoadAsync(id);
        Assert.True(doc.Record.IsEffective);
        Assert.Equal(BusinessCalendar.Today, doc.Record.EffectiveDate);
        var meanings = await check.Db().Signatures.AsNoTracking().Where(x => x.RecordId == id && !x.Superseded).OrderBy(x => x.Id).Select(x => x.Meaning).ToListAsync();
        Assert.Equal(["Approved", "Reviewed", "Reviewed", "Approved", "Released"], meanings);
        // DOC-080: the readers list is removed once effective, so any reader can open it.
        using var reader = fx.As("nflores");
        Assert.True(await reader.ServiceProvider.GetRequiredService<AccessService>().CanReadAsync(id));
    }

    [Fact] // DOC-080
    public async Task Drafts_are_hidden_from_uninvolved_readers()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Hidden draft")).Id;
        using (var reader = fx.As("nflores")) Assert.False(await reader.ServiceProvider.GetRequiredService<AccessService>().CanReadAsync(id));
        using (var owner = fx.As("sortiz")) Assert.True(await owner.ServiceProvider.GetRequiredService<AccessService>().CanReadAsync(id));
        using (var reviewer = fx.As("tbrooks")) Assert.True(await reviewer.ServiceProvider.GetRequiredService<AccessService>().CanReadAsync(id));
    }

    [Fact] // DOC-093, INV-100, INV-102, INV-103, DOC-053
    public async Task Change_request_creates_revision_B_and_release_supersedes_A_in_one_step()
    {
        Guid a;
        using (var s = fx.As("jlee")) a = (await NewDocAsync(s, "Supersession SOP")).Id;
        await ReleaseAsync(fx, a);

        Guid crId;
        using (var dc = fx.As("pshah"))
        {
            var outcome = await ActAsync(dc, a, "request_change", new()
            {
                ["summary"] = ["Add a second-person check"], ["reason"] = ["Audit finding IA-2026-07."],
                ["approver"] = [dc.UserId("sortiz").ToString()], ["assign_to"] = [dc.UserId("jlee").ToString()],
            });
            Assert.True(outcome.Success, string.Join("; ", outcome.Errors ?? []));
            crId = outcome.OpenRecordId!.Value;

            // INV-103: no second change request while one is pending.
            var second = await ActAsync(dc, a, "request_change", new() { ["summary"] = ["Another"], ["reason"] = ["Another"] });
            Assert.False(second.Success);
            Assert.Contains(second.Errors!, e => e.Contains("INV-103") && e.Contains("CR-"));
            var svc = dc.ServiceProvider.GetRequiredService<DocumentControlService>();
            Assert.NotNull(await svc.BlockerAsync((await dc.LoadAsync(a)).Record, DocKeys.ObsoleteRequest));
            // …and the rule holds on the engine path too, not only in the action.
            var direct = await dc.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
            {
                FormKey = DocKeys.ChangeRequest, ParentId = a, Values = new() { ["target_document"] = a, ["summary"] = "x", ["reason"] = "y" },
            });
            Assert.False(direct.Ok);
        }

        using (var owner = fx.As("sortiz")) Ok(await owner.RouteAsync(crId, "accepted"));

        Record revA, revB;
        using (var s = fx.As("pshah"))
        {
            var family = await s.Db().Records.AsNoTracking().Where(r => r.FamilyId == a).OrderBy(r => r.RevisionOrdinal).ToListAsync();
            Assert.Equal(2, family.Count);
            (revA, revB) = (family[0], family[1]);
            Assert.Equal(revA.Number, revB.Number); // INV-102
            Assert.Equal("B", revB.Revision);
            Assert.Equal(1, revB.RevisionOrdinal);
            Assert.Equal(s.UserId("pshah"), revB.CreatedById); // created by the requester (DOC-093)
            Assert.Equal(DocKeys.Draft, (await s.LoadAsync(revB.Id)).PhaseKey);
            var shaA = await s.Db().Attachments.Where(x => x.RecordId == revA.Id).Select(x => x.Sha256).ToListAsync();
            var shaB = await s.Db().Attachments.Where(x => x.RecordId == revB.Id).Select(x => x.Sha256).ToListAsync();
            Assert.Equal(shaA, shaB);
            Assert.True(await s.Db().Links.AnyAsync(l => l.FromId == crId && l.ToId == revB.Id && l.Kind == LinkKind.Revision));
            Assert.True((await s.LoadAsync(revB.Id)).Data.Main.Link("change_request") == crId);
            Assert.True(revA.IsEffective); // A stays effective while B is drafted

            // INV-103: a pending new revision blocks another change request.
            var blocked = await ActAsync(s, a, "request_change", new() { ["summary"] = ["Again"], ["reason"] = ["Again"] });
            Assert.False(blocked.Success);
            Assert.Contains(blocked.Errors!, e => e.Contains("revision B"));
        }

        using (var author = fx.As("jlee"))
        {
            Assert.True((await author.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(revB.Id, new()
            {
                ["minor_change"] = true, ["training_required"] = false, ["training_justification"] = "Editorial change.",
            })).Ok);
            Ok(await author.RouteAsync(revB.Id, DocKeys.AwaitingRelease, comment: "Editorial: added cross-reference")); // DOC-031 shortcut
        }
        using (var dc = fx.As("pshah")) Ok(await dc.RouteAsync(revB.Id, DocKeys.Approved));

        using var c = fx.As("pshah");
        var a2 = await c.Db().Records.AsNoTracking().FirstAsync(r => r.Id == revA.Id);
        var b2 = await c.Db().Records.AsNoTracking().FirstAsync(r => r.Id == revB.Id);
        Assert.False(a2.IsEffective);
        Assert.True(a2.IsArchived);
        Assert.Equal(DocKeys.Superseded, a2.ArchiveReason);
        Assert.NotNull(a2.ArchivedAt);
        Assert.True(b2.IsEffective);
        Assert.Equal(1, await c.Db().Records.CountAsync(r => r.FamilyId == a && r.IsEffective)); // INV-100
        Assert.True(await c.Db().Audit.AnyAsync(x => x.RecordId == revA.Id && x.Action == "Superseded"));
        Assert.True(await c.Db().Audit.AnyAsync(x => x.RecordId == revB.Id && x.Action == "Effective"));
        // The change request travelled to the archive with revision A (DOC-053).
        Assert.True(await c.Db().Records.AnyAsync(r => r.Id == crId && r.IsArchived));
        // INV-100 also holds in the database: a second effective revision is rejected by the filtered unique index.
        await Assert.ThrowsAnyAsync<SqlException>(() => c.Db().Database.ExecuteSqlRawAsync("UPDATE Records SET IsEffective = 1 WHERE Id = {0}", revA.Id));
    }

    [Fact] // DOC-073..076: completing a periodic review resets the schedule and logs a review comment
    public async Task Periodic_review_resets_the_next_review_date()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Reviewed SOP")).Id;
        await ReleaseAsync(fx, id);
        Guid pr;
        using (var owner = fx.As("sortiz"))
        {
            var info = await owner.ServiceProvider.GetRequiredService<DocumentControlService>().ReviewInfoAsync((await owner.LoadAsync(id)).Record);
            Assert.Equal(BusinessCalendar.Today.AddMonths(24), info!.NextReview); // DOC-071: Procedure profile reviews every 24 months
            var started = await ActAsync(owner, id, "start_review", new() { ["reviewer"] = [owner.UserId("sortiz").ToString()] });
            Assert.True(started.Success, string.Join("; ", started.Errors ?? []));
            pr = started.OpenRecordId!.Value;
            var next = BusinessCalendar.Today.AddMonths(12);
            Assert.True((await owner.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(pr, new()
            {
                ["outcome"] = "no_change", ["review_comments"] = "Still accurate.", ["next_review_date"] = next,
            })).Ok);
            Ok(await owner.RouteAsync(pr, "completed"));
            var after = await owner.ServiceProvider.GetRequiredService<DocumentControlService>().ReviewInfoAsync((await owner.LoadAsync(id)).Record);
            Assert.Equal(next, after!.NextReview);
            Assert.True(await owner.Db().Comments.AnyAsync(c => c.RecordId == id && c.Kind == "review" && c.IsSystem));
        }
    }

    [Fact] // DOC-101..103, DOC-066
    public async Task Accepted_obsolete_request_archives_the_document_and_disables_training()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewDocAsync(s, "Obsoleted SOP", training: true)).Id;
        using (var owner = fx.As("sortiz"))
        {
            var doc = await owner.LoadAsync(id);
            await owner.ServiceProvider.GetRequiredService<TrainingService>().CreateCourseForDocumentAsync(doc, "Obsoleted SOP training", "online", 14, null, []);
        }
        await ReleaseAsync(fx, id);
        Guid or;
        using (var dc = fx.As("pshah"))
        {
            var created = await ActAsync(dc, id, "request_obsolete", new() { ["reason"] = ["Process moved to the Galway site."] });
            Assert.True(created.Success, string.Join("; ", created.Errors ?? []));
            or = created.OpenRecordId!.Value;
            Ok(await dc.RouteAsync(or, "approval", assignees: [dc.UserId("sortiz")]));
        }
        using (var qa = fx.As("sortiz")) Ok(await qa.RouteAsync(or, "accepted"));
        using var c = fx.As("pshah");
        var d = await c.LoadAsync(id);
        Assert.Equal(DocKeys.Obsolete, d.PhaseKey);
        Assert.False(d.Record.IsEffective);
        Assert.True(d.Record.IsArchived);
        Assert.Equal(DocKeys.ObsoleteReason, d.Record.ArchiveReason);
        var courses = await c.ServiceProvider.GetRequiredService<TrainingService>().CoursesForDocumentAsync(id);
        Assert.All(courses, x => Assert.False(x.Enabled));
        Assert.True(await c.Db().Records.AnyAsync(r => r.Id == or && r.IsArchived));
    }
}
