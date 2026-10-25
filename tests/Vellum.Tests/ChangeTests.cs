using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Record = Vellum.Domain.Record;
using Vellum.Engine.Records;
using Vellum.Modules.Change;

namespace Vellum.Tests;

/// <summary>Change Management invariants: INV-147 (copy-on-create), INV-148 (approval by type), INV-149 (gates), INV-161 (regulatory).</summary>
[Collection("vellum")]
public class ChangeTests(VellumFixture fx)
{
    private static async Task<Record> TemplateAsync(IServiceScope s, string name, Dictionary<string, string[]> approvals, bool enabled = true)
    {
        return await s.CreateAsync("change_template", new()
        {
            ["name"] = name, ["enabled"] = enabled, ["verification_required"] = false, ["default_impact"] = "medium",
        }, rows: new()
        {
            ["template_approvals"] = approvals.Select(kv => new Dictionary<string, object?> { ["approval_type"] = kv.Key, ["approvers"] = kv.Value.Select(s.UserId).ToList() }).ToList(),
            ["template_plan"] = [new() { ["action"] = "Update the work instruction", ["days"] = 5m, ["verify"] = false }],
        });
    }

    private static async Task<Record> ChangeAsync(IServiceScope s, Guid template, Dictionary<string, object?>? extra = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["change_template"] = template, ["subject"] = "Change the label stock", ["owner"] = s.UserId("jlee"),
            ["planned_date"] = BusinessCalendar.Today.AddDays(30), ["reason_code"] = "cost_reduction", ["impact_level"] = "low",
            ["current_state"] = "Laser labels.", ["proposed_change"] = "Thermal labels.", ["justification"] = "Cheaper.",
            ["verification_required"] = false,
        };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        return await s.CreateAsync("change", values);
    }

    private static async Task<Assignment[]> ActiveAssignmentsAsync(IServiceScope s, Guid id) =>
        await s.Db().Assignments.AsNoTracking().Where(a => a.RecordId == id && a.Status == AssignmentStatus.Active).ToArrayAsync();

    [Fact] // INV-147, CHG-002
    public async Task Template_is_copied_on_create_and_later_edits_never_change_the_change()
    {
        using var s = fx.As("jlee");
        var tpl = await TemplateAsync(s, "Label change", new() { ["quality"] = ["sortiz"], ["manufacturing"] = ["lgarcia"] });
        var change = await ChangeAsync(s, tpl.Id);

        var snap = await s.LoadAsync(change.Id);
        Assert.Equal("Label change", snap.Text("change_type"));
        Assert.Equal(2, snap.Rows("approvals").Count);
        Assert.Single(snap.Rows("plan"));

        // Edit the template: new name, different approvals, then disable it.
        var records = s.ServiceProvider.GetRequiredService<RecordService>();
        var tplSnap = await s.LoadAsync(tpl.Id);
        var proposed = new RecordData();
        proposed.Main.Set("name", [Val.Of("Label change v2")]);
        proposed.Main.Set("enabled", [Val.Bool(false)]);
        var rows = proposed.RowsOf("template_approvals");
        rows.Add(ModuleKit.NewRow(tplSnap.Form, tplSnap.Config, 0, new() { ["approval_type"] = "regulatory", ["approvers"] = new List<int> { s.UserId("rnovak") } }));
        Assert.True((await records.SaveAsync(tplSnap, proposed)).Ok);

        var after = await s.LoadAsync(change.Id);
        Assert.Equal("Label change", after.Text("change_type"));
        Assert.Equal(["quality", "manufacturing"], after.Rows("approvals").Select(r => after.KeywordKey(r, "approval_type")).ToArray());
        Assert.Equal(records.ComputeHash(snap), records.ComputeHash(after));

        // A disabled template can't be used for new changes.
        var res = await records.CreateAsync(new CreateRequest
        {
            FormKey = "change", Values = new() { ["change_template"] = tpl.Id, ["subject"] = "Another", ["owner"] = s.UserId("jlee") },
        });
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.FieldKey == "change_template");
    }

    [Fact] // INV-148, CHG-021
    public async Task Approval_needs_one_approval_per_type_and_releases_covered_approvers()
    {
        Guid id;
        using (var s = fx.As("jlee"))
        {
            var tpl = await TemplateAsync(s, "Process change (test)", new() { ["quality"] = ["sortiz", "dokafor"], ["regulatory"] = ["rnovak"], ["engineering"] = ["akim"] });
            id = (await ChangeAsync(s, tpl.Id)).Id;
            Assert.True((await s.RouteAsync(id, "review")).Ok);
            Assert.True((await s.RouteAsync(id, "approval")).Ok);
            Assert.Equal(4, (await ActiveAssignmentsAsync(s, id)).Length); // every approver at once
        }

        using (var q = fx.As("sortiz"))
        {
            var res = await q.RouteAsync(id, "implementation");
            Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
            Assert.False(res.Moved);
            var active = await ActiveAssignmentsAsync(q, id);
            Assert.DoesNotContain(active, a => a.UserId == q.UserId("dokafor")); // Quality is covered: Dana is released
            Assert.Equal("approval", (await q.LoadAsync(id)).PhaseKey);
        }
        using (var released = fx.As("dokafor"))
            Assert.False((await released.RouteAsync(id, "implementation")).Ok);
        using (var r = fx.As("rnovak"))
        {
            Assert.True((await r.RouteAsync(id, "implementation")).Ok);
            Assert.Equal("approval", (await r.LoadAsync(id)).PhaseKey); // Engineering still missing
        }
        using (var e = fx.As("akim"))
        {
            var res = await e.RouteAsync(id, "implementation");
            Assert.True(res.Ok, string.Join("; ", res.Errors.Select(x => x.Message)));
            Assert.Equal("implementation", (await e.LoadAsync(id)).PhaseKey);
            var sigs = await e.Db().Signatures.AsNoTracking().Where(x => x.RecordId == id && x.Meaning == "Approved").Select(x => x.SignerUsername).ToListAsync();
            Assert.Equal(["akim", "rnovak", "sortiz"], sigs.Order().ToArray());
        }
    }

    [Fact] // INV-148 (pure rule), template "all approvers" option
    public void Coverage_counts_each_type_and_honors_require_all()
    {
        ApprovalRequirement[] rows = [new("quality", "Quality", [1, 2]), new("regulatory", "Regulatory", [3]), new("quality", "Quality", [4])];
        var one = ChangeRules.Coverage(rows, new HashSet<int> { 2, 3 }, requireAll: false);
        Assert.True(one.Complete);
        Assert.Equal(2, one.Types.Count);
        var all = ChangeRules.Coverage(rows, new HashSet<int> { 2, 3 }, requireAll: true);
        Assert.False(all.Complete);
        Assert.Equal(["quality"], all.Uncovered.Select(t => t.TypeKey).ToArray());
        Assert.True(ChangeRules.IsReleasable(one, 1, requireAll: false));
        Assert.False(ChangeRules.IsReleasable(all, 1, requireAll: true));
    }

    [Fact] // INV-149, CHG-040, CHG-024
    public async Task Implementation_needs_completed_action_items_and_verification_needs_evidence()
    {
        Guid id;
        using (var s = fx.As("jlee"))
        {
            var tpl = await TemplateAsync(s, "Spec change (test)", new() { ["quality"] = ["sortiz"] });
            id = (await ChangeAsync(s, tpl.Id, new() { ["verification_required"] = true, ["verifier"] = s.UserId("tbrooks") })).Id;
            Assert.True((await s.RouteAsync(id, "review")).Ok);
            Assert.True((await s.RouteAsync(id, "approval")).Ok);
        }
        using (var q = fx.As("sortiz")) Assert.True((await q.RouteAsync(id, "implementation")).Ok);

        using (var s = fx.As("jlee"))
        {
            var items = await s.Db().Records.AsNoTracking().Where(r => r.ParentId == id).ToListAsync();
            Assert.Single(items); // the action plan row became an Action Item (CHG-040)
            Assert.False((await s.RouteAsync(id, "verification")).Ok); // INV-149: open action item

            var records = s.ServiceProvider.GetRequiredService<RecordService>();
            await records.UpdateAsync(items[0].Id, new() { ["action_taken"] = "Done", ["completed_on"] = BusinessCalendar.Today });
            Assert.True((await s.RouteAsync(items[0].Id, "completed")).Ok);
            Assert.True((await s.RouteAsync(id, "verification")).Ok);
        }
        using (var v = fx.As("tbrooks"))
        {
            Assert.False((await v.RouteAsync(id, "completed")).Ok); // INV-149: no objective evidence yet
            await v.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, new() { ["verification_evidence"] = "Checked 3 lots: labels correct." });
            var res = await v.RouteAsync(id, "completed");
            Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
        }
    }

    [Fact] // INV-161, CHG-051, SAF-022
    public async Task Pas_change_completes_only_with_approval_date_and_evidence()
    {
        using var s = fx.As("jlee");
        var tpl = await TemplateAsync(s, "Supplier change (test)", new() { ["quality"] = ["sortiz"] });
        var id = (await ChangeAsync(s, tpl.Id, new() { ["reg_impact"] = "substantial", ["submission_date"] = BusinessCalendar.Today.AddDays(-20) })).Id;
        Assert.Equal("pas", (await s.LoadAsync(id)).KeywordKey("filing")); // filing follows the impact (CHG-050)
        Assert.True((await s.RouteAsync(id, "review")).Ok);
        Assert.True((await s.RouteAsync(id, "approval")).Ok);
        using (var q = fx.As("sortiz")) Assert.True((await q.RouteAsync(id, "implementation")).Ok);
        var records = s.ServiceProvider.GetRequiredService<RecordService>();
        foreach (var ai in await s.Db().Records.AsNoTracking().Where(r => r.ParentId == id).ToListAsync())
        {
            await records.UpdateAsync(ai.Id, new() { ["action_taken"] = "Done", ["completed_on"] = BusinessCalendar.Today });
            Assert.True((await s.RouteAsync(ai.Id, "completed")).Ok);
        }

        var blocked = await s.RouteAsync(id, "completed");
        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Errors, e => e.Message.Contains("PAS"));

        await records.UpdateAsync(id, new() { ["approval_date"] = BusinessCalendar.Today.AddDays(-1) });
        Assert.False((await s.RouteAsync(id, "completed")).Ok); // still no approval letter

        s.Db().ChangeTracker.Clear();
        var snap = await records.LoadRequiredAsync(id);
        var save = await records.SaveAsync(snap, new RecordData(), new SaveOptions
        {
            System = true, Uploads = [("approval_evidence", "fda-approval-S024.pdf", "application/pdf", new MemoryStream("%PDF-1.4 approval"u8.ToArray()))],
        });
        Assert.True(save.Ok);
        var done = await s.RouteAsync(id, "completed");
        Assert.True(done.Ok, string.Join("; ", done.Errors.Select(e => e.Message)));
    }

    [Theory] // INV-161 (pure rule): CBE-30 waits 30 days unless the agency responds
    [InlineData("cbe30", -10, null, false)]
    [InlineData("cbe30", -30, null, true)]
    [InlineData("cbe30", -10, -2, true)]
    [InlineData("cbe0", -1, null, true)]
    [InlineData("annual", null, null, true)]
    [InlineData("pas", -40, null, false)]
    public void Regulatory_gate(string filing, int? submittedDaysAgo, int? responseDaysAgo, bool canComplete)
    {
        var today = new DateOnly(2026, 10, 24);
        var blockers = ChangeRules.RegulatoryBlockers(null, filing, submittedDaysAgo is { } s ? today.AddDays(s) : null,
            responseDaysAgo is { } r ? today.AddDays(r) : null, null, false, today);
        Assert.Equal(canComplete, blockers.Count == 0);
    }
}
