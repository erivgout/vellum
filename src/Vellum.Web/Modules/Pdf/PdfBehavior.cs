using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Workflow;

namespace Vellum.Modules.Pdf;

/// <summary>
/// Applies PDF rendition rules to every form that has them (PLT-804..807, DOC-021, INV-054):
/// <list type="bullet">
/// <item>blocks a route into a phase that requires renditions unless current renditions exist and are linked, with a
/// clear route error (before any signature is collected), and re-checks after entry so system routes can't bypass it;</item>
/// <item>queues renditions on entering a converting phase, producing PDF/image/text ones inline;</item>
/// <item>adds "Convert to PDF now" / "Retry conversion" actions and the PDF renditions card.</item>
/// </list>
/// Runs after the form's own behaviors (Order 50) so values they set on entry (e.g. the effective date) reach the watermark.
/// </summary>
public sealed class PdfBehavior(ConfigCache cfg) : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => cfg.Current.FormsByKey.Keys.ToList();
    public override int Order => 50;

    private static async Task<PdfFormRules?> RulesAsync(RecordContext ctx)
    {
        var r = await ctx.Get<PdfRuleStore>().ForFormAsync(ctx.Snapshot.Form.Key);
        return r is { Enabled: true } ? r : null;
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (await RulesAsync(ctx) == null) return;
        foreach (var e in await ctx.Get<RenditionService>().MissingRequiredAsync(ctx.Record, ctx.Snapshot.Form, ctx.To, producedOnEntry: true))
            ctx.Error(e);
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var rules = await RulesAsync(ctx);
        if (rules == null) return;
        var queue = ctx.Get<PdfQueue>();
        var fields = rules.ConvertFields(ctx.To.Key).ToList();
        if (fields.Count > 0 && !ctx.Record.IsArchived)
        {
            var jobs = await queue.EnqueueAsync(ctx.Snapshot, fields, "phase");
            await queue.ProcessInlineAsync(jobs);
        }
        // INV-054 safety net for every path into the phase (system routes, escalations, auto-routes).
        var missing = await ctx.Get<RenditionService>().MissingRequiredAsync(ctx.Record, ctx.Snapshot.Form, ctx.To, producedOnEntry: false);
        if (missing.Count > 0) throw new WorkflowException(string.Join(" ", missing));
    }

    // ------------------------------------------------------------------ actions

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var rules = await RulesAsync(ctx);
        var r = ctx.Record;
        if (rules == null || r.IsArchived || ctx.Snapshot.Phase is not { } phase) return [];
        var configured = rules.FieldKeys.Where(rules.ConvertsAnywhere).ToList();
        var originals = configured.SelectMany(f => RenditionService.OriginalsOf(ctx.Snapshot.Attachments, f)).ToList();
        if (originals.Count == 0 || !await CanOperateAsync(ctx)) return [];
        var jobs = await ctx.Get<RenditionService>().LatestJobsAsync(r.Id);
        var failed = originals.Where(o => jobs.GetValueOrDefault(o.Id) is { Status: PdfJobStatus.Failed }).ToList();
        var list = new List<RecordAction>
        {
            new()
            {
                Key = "pdf_convert", Label = "Convert to PDF now", Icon = "bi-file-earmark-pdf", Group = "PDF",
                Description = $"Make new PDF renditions of {Ui(originals.Count, "file")} for {phase.Name}, with this phase's watermark. The originals don't change (SAF-063).",
                DisabledReason = phase.Type != PhaseType.Normal && phase.Type != PhaseType.Completed ? "Renditions can't be made in this phase." : null,
            },
        };
        if (failed.Count > 0)
            list.Add(new RecordAction
            {
                Key = "pdf_retry", Label = "Retry failed conversion", Icon = "bi-arrow-clockwise", Group = "PDF",
                Description = $"Try again to convert {string.Join(", ", failed.Select(f => f.FileName))}. Fix or replace the file first if it's corrupt or protected.",
            });
        return list;
    }

    private static string Ui(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static async Task<bool> CanOperateAsync(RecordContext ctx) =>
        ctx.Access.RoleFor(ctx.Record.AppId) >= AppRole.Manager || (await ctx.Access.CanEditAsync(ctx.Record)).Allowed ||
        await ctx.Access.ActingAssignmentAsync(ctx.Record.Id) != null;

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey is not ("pdf_convert" or "pdf_retry")) return ActionOutcome.Fail("This action isn't available.");
        var rules = await RulesAsync(ctx);
        if (rules == null || ctx.Record.IsArchived) return ActionOutcome.Fail("PDF renditions aren't configured for this record.");
        if (!await CanOperateAsync(ctx)) return ActionOutcome.Fail("Only the current assignee, an editor or an application manager can convert files to PDF.");
        var queue = ctx.Get<PdfQueue>();
        if (actionKey == "pdf_retry")
        {
            var jobs = await ctx.Get<RenditionService>().LatestJobsAsync(ctx.Record.Id);
            var current = rules.FieldKeys.SelectMany(f => RenditionService.OriginalsOf(ctx.Snapshot.Attachments, f)).Select(o => o.Id).ToHashSet();
            var failed = jobs.Values.Where(j => j.Status == PdfJobStatus.Failed && current.Contains(j.AttachmentId)).ToList();
            if (failed.Count == 0) return ActionOutcome.Fail("There's no failed conversion to retry.");
            var queued = await Tx.RunAsync(ctx.Db, async () =>
            {
                foreach (var j in failed) await queue.RetryAsync(j.Id);
                var tracked = await ctx.Db.Set<PdfJob>().Where(j => failed.Select(f => f.Id).Contains(j.Id)).ToListAsync();
                await queue.ProcessInlineAsync(tracked);
                await ctx.Db.SaveChangesAsync();
                return tracked;
            });
            return Outcome(queued);
        }
        var fields = rules.FieldKeys.Where(rules.ConvertsAnywhere).ToList();
        var created = await Tx.RunAsync(ctx.Db, async () =>
        {
            var jobs = await queue.EnqueueAsync(ctx.Snapshot, fields, "manual", force: true);
            await queue.ProcessInlineAsync(jobs);
            await ctx.Db.SaveChangesAsync();
            return jobs;
        });
        return created.Count == 0 ? ActionOutcome.Fail("There are no files to convert.") : Outcome(created);
    }

    private static ActionOutcome Outcome(List<PdfJob> jobs)
    {
        var done = jobs.Count(j => j.Status == PdfJobStatus.Done);
        var failed = jobs.Where(j => j.Status == PdfJobStatus.Failed).ToList();
        if (failed.Count > 0 && done == 0) return ActionOutcome.Fail(failed.Select(f => $"{f.FileName}: {f.LastError}").ToArray());
        if (done == jobs.Count) return ActionOutcome.Ok(done == 1 ? "PDF rendition created." : $"{done} PDF renditions created.");
        return ActionOutcome.Ok($"Conversion started for {Ui(jobs.Count - done, "file")}. The PDF appears on the record when it's ready.");
    }

    // ------------------------------------------------------------------ panel

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var rules = await RulesAsync(ctx);
        if (rules == null) return [];
        var svc = ctx.Get<RenditionService>();
        var pairs = (await svc.ForRecordAsync(ctx.Record.Id)).Where(p => rules.ConvertsAnywhere(p.Original.FieldKey) || p.Rendition != null).ToList();
        var signatures = await ctx.Db.Signatures.AsNoTracking().CountAsync(s => s.RecordId == ctx.Record.Id && !s.Superseded);
        if (pairs.Count == 0 && !(rules.Profile.Manifest && signatures > 0)) return [];
        var access = await svc.AccessAsync(ctx.Record, null, rules);
        var phase = ctx.Snapshot.Phase;
        var model = new PdfPanelModel(ctx.Record, ctx.Snapshot.Form, pairs, access, rules.Profile.Manifest && signatures > 0, signatures,
            RenditionService.MarkingFor(ctx.Record, ctx.Snapshot.Form, rules), rules.RequiredFields(phase?.Key).Any(),
            phase == null ? [] : ctx.Snapshot.Workflow!.Phases.Where(p => rules.RequiredFields(p.Key).Any()).Select(p => p.Name).ToList(),
            ctx.Records.TimeZone);
        return
        [
            new RecordPanel
            {
                Key = "pdf-renditions", Title = "PDF renditions", Icon = "bi-file-earmark-pdf", Placement = "side", Order = 0,
                Partial = "~/Modules/Pdf/Views/_PdfPanel.cshtml", Model = model, Count = pairs.Count(p => p.Rendition != null),
            },
        ];
    }
}

public sealed record PdfPanelModel(Record Record, FormDefinition Form, List<RenditionPair> Pairs, PdfAccess Access, bool Manifest, int Signatures,
    string? Marking, bool PhaseRequires, List<string> RequiringPhases, TimeZoneInfo Tz)
{
    public string FieldLabel(string key) => Form.Fields.FirstOrDefault(f => f.Key == key)?.Label ?? key;
    public bool AnyPending => Pairs.Any(p => p.Pending);
}
