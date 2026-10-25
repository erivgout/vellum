using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Data;
using Vellum.Domain;
using Record = Vellum.Domain.Record;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Tests;

/// <summary>Core platform invariants (INV Â§21) against the real SQL Server schema.</summary>
[Collection("vellum")]
public class CoreInvariantTests(VellumFixture fx)
{
    private async Task<Record> NewCapaAsync(IServiceScope s, string subject = "Test CAPA") =>
        await s.CreateAsync("capa", new()
        {
            ["subject"] = subject, ["capa_type"] = "corrective", ["source"] = "internal_audit", ["priority"] = "high",
            ["owner"] = s.UserId("jlee"), ["description"] = "Something went wrong.",
            ["effectiveness_criteria"] = "It doesn't happen again.", ["effectiveness_verifier"] = s.UserId("tbrooks"),
        }, rows: new()
        {
            ["planned_actions"] = [new() { ["action"] = "Fix it", ["assignee"] = s.UserId("akim"), ["days"] = 5m, ["verify"] = false }],
        });

    [Fact] // INV-050
    public async Task Audit_trail_rejects_update_and_delete()
    {
        using var s = fx.As("jlee");
        var r = await NewCapaAsync(s);
        await Assert.ThrowsAnyAsync<SqlException>(() => s.Db().Database.ExecuteSqlRawAsync("UPDATE AuditEntries SET Action = 'tampered' WHERE RecordId = {0}", r.Id));
        await Assert.ThrowsAnyAsync<SqlException>(() => s.Db().Database.ExecuteSqlRawAsync("DELETE FROM AuditEntries WHERE RecordId = {0}", r.Id));
    }

    [Fact] // INV-055
    public async Task Audit_trail_is_an_intact_hash_chain()
    {
        using var s = fx.As("jlee");
        var r = await NewCapaAsync(s);
        Assert.True((await s.RouteAsync(r.Id, "initial_review")).Ok);
        var entries = await s.Db().Audit.AsNoTracking().Where(a => a.RecordId == r.Id).OrderBy(a => a.Seq).ToListAsync();
        Assert.True(entries.Count > 3);
        Assert.Null(AuditHashing.FirstBreak(entries));
        entries[1].NewValue = "forged";
        Assert.NotNull(AuditHashing.FirstBreak(entries));
    }

    [Fact] // INV-070
    public async Task Only_the_current_assignee_can_route()
    {
        Guid id;
        using (var s = fx.As("jlee")) id = (await NewCapaAsync(s)).Id;
        using var other = fx.As("pshah");
        var res = await other.RouteAsync(id, "initial_review");
        Assert.False(res.Ok);
    }

    [Fact] // INV-043, INV-052, INV-113, INV-114, QE-002
    public async Task Capa_lifecycle_enforces_gates_and_signatures()
    {
        using var s = fx.As("jlee");
        var r = await NewCapaAsync(s, "Lifecycle CAPA");
        Assert.True((await s.RouteAsync(r.Id, "initial_review")).Ok);
        Assert.True((await s.RouteAsync(r.Id, "root_cause_plan")).Ok);

        // Required-in-phase fields block the route until filled.
        Assert.False((await s.RouteAsync(r.Id, "plan_approval")).Ok);
        var upd = await s.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(r.Id, new()
        {
            ["root_cause_category"] = "method", ["root_cause"] = "No procedure.", ["solution"] = "Write one.",
        });
        Assert.True(upd.Ok);
        Assert.True((await s.RouteAsync(r.Id, "plan_approval", assignees: [s.UserId("sortiz")])).Ok);

        using var approver = fx.As("sortiz");
        Assert.False((await approver.RouteAsync(r.Id, "implementation", password: "wrong-password")).Ok); // signature needs the real password
        Assert.True((await approver.RouteAsync(r.Id, "implementation")).Ok);

        var sig = await s.Db().Signatures.AsNoTracking().SingleAsync(x => x.RecordId == r.Id);
        Assert.Equal("Approved", sig.Meaning);
        Assert.Equal(s.UserId("sortiz"), sig.SignerId);
        var hash = s.ServiceProvider.GetRequiredService<RecordService>().ComputeHash(await s.LoadAsync(r.Id));
        Assert.Equal(hash, sig.RecordHash);

        // Signatures can't be deleted or altered at the database level.
        await Assert.ThrowsAnyAsync<SqlException>(() => s.Db().Database.ExecuteSqlRawAsync("DELETE FROM Signatures WHERE Id = {0}", sig.Id));
        await Assert.ThrowsAnyAsync<SqlException>(() => s.Db().Database.ExecuteSqlRawAsync("UPDATE Signatures SET Meaning = 'Forged' WHERE Id = {0}", sig.Id));

        // Planned actions became action items; the CAPA can't reach Effectiveness Check while they're open.
        var items = await s.Db().Records.AsNoTracking().Where(x => x.ParentId == r.Id).ToListAsync();
        Assert.Single(items);
        Assert.False((await s.RouteAsync(r.Id, "effectiveness_check")).Ok);

        using var doer = fx.As("akim");
        await doer.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(items[0].Id, new() { ["action_taken"] = "Done", ["completed_on"] = BusinessCalendar.Today });
        Assert.True((await doer.RouteAsync(items[0].Id, "completed")).Ok);
        Assert.True((await s.RouteAsync(r.Id, "effectiveness_check")).Ok);
    }

    [Fact] // INV-020
    public async Task Record_numbers_are_unique_and_sequential()
    {
        using var s = fx.As("jlee");
        var a = await NewCapaAsync(s);
        var b = await NewCapaAsync(s);
        Assert.NotEqual(a.Number, b.Number);
        Assert.Equal(a.Sequence + 1, b.Sequence);
    }

    [Fact] // INV-081
    public async Task Readable_records_respect_record_reader_lists()
    {
        Guid id;
        using (var s = fx.As("jlee"))
        {
            id = (await NewCapaAsync(s, "Restricted")).Id;
            s.Db().RecordAccess.Add(new RecordAccess { RecordId = id, PrincipalKind = PrincipalKind.User, PrincipalId = s.UserId("sortiz"), Level = RecordAccessLevel.Reader });
            await s.Db().SaveChangesAsync();
        }
        using var outsider = fx.As("nflores");
        Assert.False(await outsider.ServiceProvider.GetRequiredService<AccessService>().CanReadAsync(id));
        using var listed = fx.As("sortiz");
        Assert.True(await listed.ServiceProvider.GetRequiredService<AccessService>().CanReadAsync(id));
    }
}

public class UnitTests
{
    [Theory] // DOC-014
    [InlineData("A", "B")]
    [InlineData("Z", "AA")]
    [InlineData("AZ", "BA")]
    [InlineData("ZZ", "AAA")]
    public void Alphabetic_revisions_increment(string current, string next) => Assert.Equal(next, NumberingService.NextAlpha(current));

    [Fact] // SEC-202
    public void Password_hashes_are_salted_argon2id()
    {
        var a = PasswordHasher.Hash("Correct-Horse-9");
        var b = PasswordHasher.Hash("Correct-Horse-9");
        Assert.NotEqual(a, b);
        Assert.StartsWith("argon2id$", a);
        Assert.True(PasswordHasher.Verify("Correct-Horse-9", a));
        Assert.False(PasswordHasher.Verify("correct-horse-9", a));
    }

    [Fact] // SEC-201
    public void Password_policy_rejects_weak_passwords()
    {
        var s = new SystemSettings();
        Assert.NotEmpty(PasswordPolicy.Validate("short", s, null, []));
        Assert.NotEmpty(PasswordPolicy.Validate("Password123!", s, null, []));
        Assert.Empty(PasswordPolicy.Validate("Vellum-Strong-Pass-42", s, null, []));
    }

    [Fact] // DSN-305
    public void Visibility_matrix_follows_the_spec()
    {
        var sec = new SectionDefinition { Key = "a" };
        var open = new Record { PhaseType = PhaseType.Normal, WorkflowId = 1 };
        Assert.Equal(SectionState.Editable, FormRules.StateOf(sec, new PhaseDefinition { Type = PhaseType.Normal }, open));
        Assert.Equal(SectionState.Hidden, FormRules.StateOf(sec, new PhaseDefinition { Type = PhaseType.Normal, EditableSections = ["b"] }, open));
        Assert.Equal(SectionState.ReadOnly, FormRules.StateOf(sec, new PhaseDefinition { Type = PhaseType.Normal, EditableSections = ["b"], ReadOnlySections = ["*"] }, open));
        var done = new Record { PhaseType = PhaseType.Completed, WorkflowId = 1 };
        Assert.Equal(SectionState.ReadOnly, FormRules.StateOf(sec, new PhaseDefinition { Type = PhaseType.Completed, EditableSections = ["a"] }, done));
    }
}
