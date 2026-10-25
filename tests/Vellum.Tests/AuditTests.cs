using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Modules.Audits;
using Vellum.Modules.Change;

namespace Vellum.Tests;

/// <summary>Audits: INV-067 (score ≤ out of), AUD-022 (required questions), INV-165 (removing a scheduled planned audit voids it), AUD-025.</summary>
[Collection("vellum")]
public class AuditTests(VellumFixture fx)
{
    private static Dictionary<string, object?> Q(string question, decimal? outOf, decimal? score = null, bool required = true, string? cls = null) =>
        new() { ["question"] = question, ["clause"] = "ISO 13485:2016 §7.5.1", ["out_of"] = outOf, ["score"] = score, ["required"] = required, ["classification"] = cls };

    private static Dictionary<string, object?> AuditValues(IServiceScope s) => new()
    {
        ["subject"] = "Final assembly audit", ["audit_kind"] = "internal", ["auditee"] = "Final assembly", ["lead_auditor"] = s.UserId("tbrooks"),
        ["start_at"] = DateTime.UtcNow.Date.AddHours(13), ["evaluation"] = "scoring",
    };

    private static async Task<ActionOutcome> ActAsync(IServiceScope s, Guid id, string action, Dictionary<string, string> values, string? comment = null)
    {
        s.Db().ChangeTracker.Clear();
        var snap = await s.ServiceProvider.GetRequiredService<RecordService>().LoadRequiredAsync(id);
        var ctx = new RecordContext(snap, s.ServiceProvider);
        var behavior = s.ServiceProvider.GetRequiredService<BehaviorRegistry>().For(snap.Form).First();
        return await behavior.ExecuteAsync(ctx, action, new ActionInput { Values = values.ToDictionary(kv => kv.Key, kv => new[] { kv.Value }), Comment = comment });
    }

    [Theory] // INV-067 (pure rule)
    [InlineData(5.0, 5.0, true)]
    [InlineData(5.0, 0.0, true)]
    [InlineData(5.0, 5.5, false)]
    [InlineData(5.0, -1.0, false)]
    [InlineData(null, 3.0, false)]
    [InlineData(null, null, true)]
    public void Score_is_between_zero_and_out_of(double? outOf, double? score, bool ok) =>
        Assert.Equal(ok, AuditRules.ScoreError((decimal?)outOf, (decimal?)score) == null);

    [Fact] // INV-067 enforced on save
    public async Task Audit_rejects_a_score_above_out_of()
    {
        using var s = fx.As("tbrooks");
        var res = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = "audit", Values = AuditValues(s), RowValues = new() { ["checklist"] = [Q("Are work instructions current?", 5, 6, cls: "conforming")] },
        });
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("out of", StringComparison.OrdinalIgnoreCase));

        var ok = await s.CreateAsync("audit", AuditValues(s), rows: new() { ["checklist"] = [Q("Are work instructions current?", 5, 4, cls: "conforming"), Q("Is equipment qualified?", 5, 2, cls: "minor_nc")] });
        var snap = await s.LoadAsync(ok.Id);
        Assert.Equal(6m, snap.Number("total_score"));
        Assert.Equal(10m, snap.Number("max_score"));
        Assert.Equal(60m, snap.Number("score_pct"));
        Assert.Equal(1m, snap.Number("count_minor"));
        Assert.True(snap.Bool("has_nc"));
    }

    [Fact] // AUD-022, AUD-023
    public async Task Required_questions_must_be_answered_before_leaving_scheduled()
    {
        using var s = fx.As("tbrooks");
        var a = await s.CreateAsync("audit", AuditValues(s), rows: new() { ["checklist"] = [Q("Answered?", 5, 5, cls: "conforming"), Q("Not answered yet?", 5)] });
        Assert.True((await s.RouteAsync(a.Id, "scheduled")).Ok);
        var records = s.ServiceProvider.GetRequiredService<RecordService>();
        await records.UpdateAsync(a.Id, new() { ["result"] = "acceptable", ["frequency_rec"] = "keep", ["conclusions"] = "Fine." });
        var blocked = await s.RouteAsync(a.Id, "report_approval");
        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Errors, e => e.Message.Contains("AUD-022"));

        // Questions can't be removed once scheduled (AUD-023).
        var snap = await s.LoadAsync(a.Id);
        var proposed = new RecordData();
        proposed.RowsOf("checklist").Add(snap.Rows("checklist")[0].Clone());
        Assert.False((await records.SaveAsync(snap, proposed, new SaveOptions { System = true })).Ok);

        snap = await s.LoadAsync(a.Id);
        proposed = new RecordData();
        foreach (var r in snap.Rows("checklist"))
        {
            var c = r.Clone();
            c.SetRow(snap.Form, snap.Config, "classification", "observation");
            c.SetRow(snap.Form, snap.Config, "score", 4m);
            proposed.RowsOf("checklist").Add(c);
        }
        Assert.True((await records.SaveAsync(snap, proposed, new SaveOptions { System = true })).Ok);
        Assert.True((await s.RouteAsync(a.Id, "report_approval")).Ok);
    }

    [Fact] // AUD-012, INV-165, AUD-013
    public async Task Removing_a_scheduled_planned_audit_voids_the_audit_in_the_same_transaction()
    {
        Guid planId;
        using (var s = fx.As("tbrooks"))
        {
            var plan = await s.CreateAsync("audit_plan", new()
            {
                ["name"] = "Test program", ["program"] = "quality", ["period_start"] = BusinessCalendar.Today.AddDays(-10),
                ["period_end"] = BusinessCalendar.Today.AddDays(200), ["lead_auditor"] = s.UserId("tbrooks"),
            }, rows: new() { ["planned_audits"] = [new() { ["auditee"] = "Molding", ["planned_start"] = BusinessCalendar.Today.AddDays(5) }] });
            planId = plan.Id;
            Assert.True((await s.RouteAsync(planId, "approval", assignees: [s.UserId("sortiz")])).Ok);
        }
        using (var q = fx.As("sortiz")) Assert.True((await q.RouteAsync(planId, "active")).Ok);

        Guid auditId;
        using (var s = fx.As("tbrooks"))
        {
            var rowId = (await s.LoadAsync(planId)).Rows("planned_audits")[0].Id;
            var outcome = await ActAsync(s, planId, "schedule", new() { ["row"] = rowId.ToString() });
            Assert.True(outcome.Success, string.Join("; ", outcome.Errors ?? []));
            var plan = await s.LoadAsync(planId);
            auditId = plan.Rows("planned_audits")[0].CreatedRecordId!.Value;
            var audit = await s.LoadAsync(auditId);
            Assert.Equal("scheduled", audit.PhaseKey);
            Assert.Equal(planId, audit.Data.Main.Link("audit_plan"));
            Assert.True(await s.Db().Links.AnyAsync(l => l.FromId == planId && l.ToId == auditId)); // linked both ways

            // Removing the row by a plain save is refused while the audit is live (and only Draft/Approval allow it).
            Assert.True((await s.RouteAsync(planId, "approval", assignees: [s.UserId("sortiz")])).Ok);
        }
        using (var q = fx.As("sortiz"))
        {
            var records = q.ServiceProvider.GetRequiredService<RecordService>();
            var plan = await q.LoadAsync(planId);
            var plain = await records.SaveAsync(plan, new RecordData { Rows = { ["planned_audits"] = [] } });
            Assert.False(plain.Ok);
            Assert.Equal(PhaseType.Normal, (await q.LoadAsync(auditId)).Record.PhaseType);

            var rowId = (await q.LoadAsync(planId)).Rows("planned_audits")[0].Id;
            var outcome = await ActAsync(q, planId, "remove", new() { ["row"] = rowId.ToString() }, comment: "Molding moved to next year's program.");
            Assert.True(outcome.Success, string.Join("; ", outcome.Errors ?? []));
            Assert.Equal(PhaseType.Voided, (await q.LoadAsync(auditId)).Record.PhaseType);
            Assert.Empty((await q.LoadAsync(planId)).Rows("planned_audits"));
        }
    }

    [Fact] // AUD-025: a CAPA raised from a nonconformance is pre-filled and linked to the finding
    public async Task Raise_capa_from_a_nonconformance()
    {
        using var s = fx.As("tbrooks");
        var a = await s.CreateAsync("audit", AuditValues(s), rows: new() { ["checklist"] = [Q("Is traceability maintained?", 5, 1, cls: "major_nc")] });
        Assert.True((await s.RouteAsync(a.Id, "scheduled")).Ok);
        var row = (await s.LoadAsync(a.Id)).Rows("checklist")[0].Id;
        var outcome = await ActAsync(s, a.Id, "raise_capa", new() { ["row"] = row.ToString(), ["owner"] = s.UserId("lgarcia").ToString(), ["priority"] = "high" });
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors ?? []));
        var link = await s.Db().Links.AsNoTracking().SingleAsync(l => l.FromId == a.Id && l.FieldKey == FindingsModel.LinkKey(row));
        var capa = await s.LoadAsync(link.ToId);
        Assert.Equal("capa", capa.Form.Key);
        Assert.Equal("high", capa.KeywordKey("priority"));
        Assert.Equal(a.Id, capa.Data.Main.Link("source_record"));
        Assert.False((await ActAsync(s, a.Id, "raise_capa", new() { ["row"] = row.ToString(), ["owner"] = s.UserId("lgarcia").ToString() })).Success); // only once
    }
}
