using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;

namespace Vellum.Modules.Supplier;

/// <summary>
/// Lets a behavior change fields that users can't edit directly (approval status, risk acceptance). The behavior opens
/// the gate only around its own, authorized, signed change; any other write that tries to change a gated field is rejected.
/// </summary>
[ScopedService]
public sealed class ProtectedFieldGate
{
    public string? OpenFor { get; private set; }
    public bool IsOpen(string scope) => OpenFor == scope;
    internal void Open(string scope) => OpenFor = scope;
    internal void Close() => OpenFor = null;
}

/// <summary>
/// Applies a field change made by a custom action, optionally with an electronic signature, in one transaction (INV-093):
/// the values are saved as a validated, audited system write, the signature is collected over the resulting content
/// (INV-113/114), and a comment is added. A wrong password rolls everything back.
/// </summary>
public static class SignedChange
{
    public static async Task<ActionOutcome> ApplyAsync(RecordContext ctx, Dictionary<string, object?> values, string? meaning,
        string actionLabel, ActionInput input, string comment, string message, string? gateScope = null,
        Func<Task<string?>>? after = null)
    {
        var db = ctx.Db;
        var records = ctx.Records;
        var gate = ctx.Get<ProtectedFieldGate>();
        var id = ctx.Record.Id;
        try
        {
            return await Tx.RunAsync(db, async () =>
            {
                var proposed = new RecordData();
                foreach (var (k, v) in values)
                    proposed.Main.Set(k, FieldCodec.FromObject(ctx.Snapshot.Form.Fields.First(f => f.Key == k), v, ctx.Config));
                if (gateScope != null) gate.Open(gateScope);
                SaveResult save;
                try { save = await records.SaveAsync(ctx.Snapshot, proposed, new SaveOptions { System = true, Reason = input.Comment }); }
                finally { gate.Close(); }
                if (!save.Ok) return ActionOutcome.Fail(save.Errors.Select(e => e.Message).ToArray());

                if (meaning != null)
                {
                    var fresh = await records.LoadRequiredAsync(id);
                    var sig = await ctx.Get<SignatureService>().SignAsync(fresh, meaning, actionLabel, input.Password, input.Certify, null,
                        records.ComputeHash, input.Comment);
                    if (!sig.Ok) return ActionOutcome.Fail(sig.Error!);
                }
                db.Comments.Add(new Comment
                {
                    RecordId = id, UserId = ctx.Me.IsSystem ? null : ctx.Me.Id, IsSystem = true, Kind = "system", At = DateTime.UtcNow,
                    Text = comment + (string.IsNullOrWhiteSpace(input.Comment) ? "" : $": {input.Comment}"),
                });
                if (after != null && await after() is { } err) return ActionOutcome.Fail(err);
                await db.SaveChangesAsync();
                return ActionOutcome.Ok(message);
            }, o => o.Success);
        }
        catch (WorkflowException ex)
        {
            return ActionOutcome.Fail(ex.Message);
        }
    }
}
