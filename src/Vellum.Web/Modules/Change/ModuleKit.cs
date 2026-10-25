using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;

namespace Vellum.Modules.Change;

/// <summary>Small helpers shared by the Change, Audits and Inspections behaviors.</summary>
public static class ModuleKit
{
    /// <summary>Set a value in a subform row (keyword keys, user IDs, decimals, dates, record IDs…).</summary>
    public static void SetRow(this RowBag row, FormDefinition form, ConfigModel cfg, string key, object? value)
    {
        var f = form.Fields.FirstOrDefault(x => x.Key == key) ?? throw new InvalidOperationException($"Form '{form.Key}' has no field '{key}'.");
        row.Values.Set(key, FieldCodec.FromObject(f, value, cfg));
    }

    /// <summary>A new subform row with the given values.</summary>
    public static RowBag NewRow(FormDefinition form, ConfigModel cfg, int order, Dictionary<string, object?> values)
    {
        var row = new RowBag { IsNew = true, Order = order };
        foreach (var (k, v) in values) row.SetRow(form, cfg, k, v);
        return row;
    }

    /// <summary>The current user is the record's acting assignee (or delegate) or an application manager.</summary>
    public static async Task<bool> CanActAsync(RecordContext ctx) =>
        ctx.Access.RoleFor(ctx.Record.AppId) >= AppRole.Manager || await ctx.Access.ActingAssignmentAsync(ctx.Record.Id) != null;

    public static bool HasAttachment(RecordSnapshot snap, string fieldKey) => snap.AttachmentsOf(fieldKey).Any();

    /// <summary>Run work in one transaction and commit only when it succeeds (INV-093).</summary>
    public static Task<ActionOutcome> InTransactionAsync(RecordContext ctx, Func<Task<ActionOutcome>> work) =>
        RunInTransactionAsync(ctx.Db, work);

    public static async Task<ActionOutcome> RunInTransactionAsync(VellumDbContext db, Func<Task<ActionOutcome>> work)
    {
        try
        {
            return await Tx.RunAsync(db, work, o => o.Success);
        }
        catch (WorkflowException ex)
        {
            db.ChangeTracker.Clear();
            return ActionOutcome.Fail(ex.Message);
        }
    }

    /// <summary>Phase key of a record by ID (null when it doesn't exist).</summary>
    public static async Task<(string? PhaseKey, PhaseType Type)> PhaseOfAsync(RecordContext ctx, Guid id)
    {
        var r = await ctx.Db.Records.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.PhaseId, x.PhaseType }).FirstOrDefaultAsync();
        return r == null ? (null, PhaseType.Normal) : (ctx.Config.Phase(r.PhaseId)?.Key, r.PhaseType);
    }

    /// <summary>
    /// Turn subform rows into Action Items in Open (QE-002), as the CAPA reference does: due dates from "days to complete",
    /// optional verification. Rows that already created a record are skipped. Returns the number created.
    /// </summary>
    /// <summary>
    /// Verifier of a planned action: the row's verifier, else the record owner when someone else does the action, else the
    /// record's verification user. Null when nobody suitable (the verifier never equals the assignee, SAF-044).
    /// </summary>
    public static int? ResolveVerifier(int? rowVerifier, int assignee, int owner, int? fallback)
    {
        var v = rowVerifier ?? (assignee != owner ? owner : fallback);
        return v == assignee ? null : v;
    }

    public static async Task<int> CreateActionItemsAsync(RecordContext ctx, string section, string actionKey, string assigneeKey,
        string daysKey, string verifyKey, string verifierKey, int defaultAssignee, int? fallbackVerifier, string origin)
    {
        var calendar = ctx.Get<BusinessCalendar>();
        var snap = await ctx.Records.LoadRequiredAsync(ctx.Record.Id);
        var proposed = new RecordData();
        var updated = proposed.RowsOf(section);
        var created = 0;
        foreach (var row in snap.Rows(section))
        {
            var clone = row.Clone();
            if (row.CreatedRecordId == null && row.Values.Text(actionKey) is { } action)
            {
                var assignee = row.Values.Ref(assigneeKey) ?? defaultAssignee;
                var verify = row.Values.Bool(verifyKey) == true;
                var verifier = verify ? ResolveVerifier(row.Values.Ref(verifierKey), assignee, defaultAssignee, fallbackVerifier) : null;
                if (verify && verifier == null)
                    throw new WorkflowException($"\"{action}\" needs a verifier who isn't the person doing the action (SAF-044).");
                var due = await calendar.AddAsync(BusinessCalendar.Today, (int)(row.Values.Number(daysKey) ?? 10), true);
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = "action_item",
                    StartPhaseKey = "open",
                    ParentId = snap.Record.Id,
                    SkipAccessCheck = true,
                    Assignees = [assignee],
                    DueDate = due,
                    Values = new()
                    {
                        ["subject"] = action,
                        ["description"] = $"{origin} of {snap.Record.Number}: {snap.Record.Title}",
                        ["assignee"] = assignee,
                        ["due_date"] = due,
                        ["verification_required"] = verify,
                        ["verifier"] = verifier,
                    },
                    CreateComment = $"Created automatically from the {origin.ToLowerInvariant()} of {snap.Record.Number} (QE-002)",
                });
                if (!res.Ok) throw new WorkflowException("Couldn't create an action item: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                clone.CreatedRecordId = res.Record!.Id;
                created++;
            }
            updated.Add(clone);
        }
        if (created > 0)
        {
            var fresh = await ctx.Records.LoadRequiredAsync(snap.Record.Id);
            var save = await ctx.Records.SaveAsync(fresh, proposed, new SaveOptions { System = true });
            if (!save.Ok) throw new WorkflowException(string.Join("; ", save.Errors.Select(e => e.Message)));
        }
        return created;
    }

    /// <summary>Children of a record with their phase, form and selected main-form values (for panels and gates).</summary>
    public static async Task<List<ChildInfo>> ChildrenAsync(RecordContext ctx, Guid parentId, string formKey, params string[] fields)
    {
        if (!ctx.Config.FormsByKey.TryGetValue(formKey, out var form)) return [];
        var kids = await ctx.Db.Records.AsNoTracking().Where(r => r.ParentId == parentId && r.FormId == form.Id).OrderBy(r => r.CreatedAt).ToListAsync();
        var ids = kids.Select(k => k.Id).ToList();
        var values = fields.Length == 0 ? [] : await ctx.Db.RecordValues.AsNoTracking()
            .Where(v => ids.Contains(v.RecordId) && v.RowId == null && fields.Contains(v.FieldKey)).ToListAsync();
        return kids.Select(k => new ChildInfo(k, ctx.Config.Phase(k.PhaseId),
            values.Where(v => v.RecordId == k.Id).GroupBy(v => v.FieldKey).ToDictionary(g => g.Key, g => g.OrderBy(v => v.Index).First()))).ToList();
    }
}

public sealed record ChildInfo(Record Record, PhaseDefinition? Phase, Dictionary<string, RecordValue> Values)
{
    public bool Open => Record.PhaseType == PhaseType.Normal;
    public string? Text(string key) => Values.TryGetValue(key, out var v) ? v.Text : null;
    public int? Ref(string key) => Values.TryGetValue(key, out var v) ? v.RefId : null;
    public decimal? Number(string key) => Values.TryGetValue(key, out var v) ? v.Number : null;
    public DateOnly? Date(string key) => Values.TryGetValue(key, out var v) && v.Date is { } d ? DateOnly.FromDateTime(d) : null;
    public Guid? Link(string key) => Values.TryGetValue(key, out var v) ? v.RefGuid : null;
}
