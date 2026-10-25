using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Change;

/// <summary>Demo-data helpers on top of <see cref="DemoContext"/>: run custom record actions and close action items through the engine.</summary>
public static class DemoKit
{
    /// <summary>Run a custom toolbar action as <paramref name="asUser"/>, exactly as the record page does.</summary>
    public static async Task<ActionOutcome> ActAsync(DemoContext d, Guid recordId, string actionKey, string asUser,
        Dictionary<string, string>? values = null, string? comment = null)
    {
        d.As(asUser);
        d.Db.ChangeTracker.Clear();
        var records = d.Services.GetRequiredService<RecordService>();
        var snap = await records.LoadRequiredAsync(recordId);
        var ctx = new RecordContext(snap, d.Services);
        foreach (var b in d.Services.GetRequiredService<BehaviorRegistry>().For(snap.Form))
        {
            if (!(await b.ActionsAsync(ctx)).Any(a => a.Key == actionKey && a.DisabledReason == null)) continue;
            var res = await b.ExecuteAsync(ctx, actionKey, new ActionInput
            {
                Values = values?.ToDictionary(kv => kv.Key, kv => new[] { kv.Value }) ?? [],
                Comment = comment, Password = DemoContext.Password, Certify = true,
            });
            if (!res.Success) throw new InvalidOperationException($"Demo action {actionKey} on {snap.Record.Number} failed: {string.Join("; ", res.Errors ?? [])}");
            return res;
        }
        throw new InvalidOperationException($"Demo action {actionKey} isn't available on {snap.Record.Number}.");
    }

    /// <summary>Change subform rows (and optionally append new ones) as a system save attributed to <paramref name="asUser"/>.</summary>
    public static async Task UpdateRowsAsync(DemoContext d, Guid recordId, string section, string asUser, Action<RecordSnapshot, RowBag>? mutate,
        List<Dictionary<string, object?>>? add = null, Dictionary<string, object?>? values = null)
    {
        d.As(asUser);
        d.Db.ChangeTracker.Clear();
        var records = d.Services.GetRequiredService<RecordService>();
        var snap = await records.LoadRequiredAsync(recordId);
        var proposed = new RecordData();
        var rows = proposed.RowsOf(section);
        foreach (var r in snap.Rows(section))
        {
            var c = r.Clone();
            mutate?.Invoke(snap, c);
            rows.Add(c);
        }
        foreach (var v in add ?? []) rows.Add(ModuleKit.NewRow(snap.Form, snap.Config, rows.Count, v));
        foreach (var (k, v) in values ?? [])
            proposed.Main.Set(k, FieldCodec.FromObject(snap.Form.Fields.First(f => f.Key == k), v, snap.Config));
        var res = await records.SaveAsync(snap, proposed, new SaveOptions { System = true });
        if (!res.Ok) throw new InvalidOperationException($"Demo row update of {snap.Record.Number} failed: {string.Join("; ", res.Errors.Select(e => e.Message))}");
    }

    /// <summary>Complete the open action items of a record (with verification where required), leaving the last <paramref name="leaveOpen"/> open.</summary>
    public static async Task CloseActionItemsAsync(DemoContext d, Guid parentId, int leaveOpen = 0, string? note = null)
    {
        var records = d.Services.GetRequiredService<RecordService>();
        d.Db.ChangeTracker.Clear();
        var items = (await records.ChildrenAsync(parentId, "action_item")).Where(i => i.PhaseType == Domain.PhaseType.Normal).ToList();
        foreach (var ai in items.Take(Math.Max(0, items.Count - leaveOpen)))
        {
            d.Db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(ai.Id);
            var doer = d.Directory.Get(snap.UserId("assignee"))!.Username;
            await d.UpdateAsync(ai.Id, new()
            {
                ["action_taken"] = note ?? "Completed as planned. Records filed with the change package.",
                ["completed_on"] = DemoContext.Days(-1),
            }, doer);
            if (snap.Bool("verification_required") == true)
            {
                await d.RouteAsync(ai.Id, "verification", asUser: doer);
                var verifier = d.Directory.Get(snap.UserId("verifier"))!.Username;
                await d.UpdateAsync(ai.Id, new() { ["verification_result"] = "verified", ["verification_comments"] = "Checked objective evidence; effective." }, verifier);
                await d.RouteAsync(ai.Id, "completed", asUser: verifier);
            }
            else
            {
                await d.RouteAsync(ai.Id, "completed", asUser: doer);
            }
        }
    }
}
