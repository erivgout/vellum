using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>Small helpers shared by the quality applications' behaviors.</summary>
internal static class QualityExtensions
{
    /// <summary>Set a computed value on a subform row (behaviors only).</summary>
    public static void SetRow(this SavingContext ctx, RowBag row, string fieldKey, object? value)
    {
        var f = ctx.Form.Fields.First(x => x.Key == fieldKey);
        row.Values.Set(fieldKey, FieldCodec.FromObject(f, value, ctx.Config));
    }

    public static bool IsYes(this ValueBag bag, string key) => bag.Bool(key) == true;
    public static bool IsNo(this ValueBag bag, string key) => bag.Bool(key) == false;

    /// <summary>The acting user may run record actions: a current assignee (or delegate) or an application manager.</summary>
    public static async Task<bool> CanActAsync(this RecordContext ctx) =>
        ctx.Access.RoleFor(ctx.Record.AppId) >= AppRole.Manager || await ctx.Access.ActingAssignmentAsync(ctx.Record.Id) != null;

    public static bool IsOpen(this RecordContext ctx) => ctx.Record.PhaseType == PhaseType.Normal && !ctx.Record.IsArchived;
}

/// <summary>Linked-record helpers: "Initiate X" actions and "is X required?" gates (QE-005, INV-041).</summary>
public static class QualityLinks
{
    /// <summary>
    /// Create a record from <paramref name="ctx"/>'s record, link it (Source link, both directions queryable), store its ID in
    /// <paramref name="linkFieldOnSource"/> and audit both sides, all in one transaction (INV-093).
    /// </summary>
    public static async Task<(Record? Record, string? Error)> CreateFromAsync(RecordContext ctx, string formKey,
        Dictionary<string, object?> values, string? linkFieldOnSource = null, Dictionary<string, List<Dictionary<string, object?>>>? rows = null,
        IReadOnlyList<int>? assignees = null)
    {
        var form = ctx.Config.FormsByKey.GetValueOrDefault(formKey);
        if (form == null) return (null, $"The {formKey} application isn't installed.");
        try
        {
            return await Tx.RunAsync(ctx.Db, async () =>
            {
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = formKey, Values = values, RowValues = rows, SkipAccessCheck = true, Assignees = assignees,
                    CreateComment = $"Created from {ctx.Config.FormsById[ctx.Record.FormId].Name} {ctx.Record.Number}",
                });
                if (!res.Ok) throw new WorkflowException($"Couldn't create the {form.Name}: " + string.Join("; ", res.Errors.Select(e => e.Message)));
                await ctx.Records.LinkAsync(ctx.Record.Id, res.Record!.Id, LinkKind.Source);
                if (linkFieldOnSource != null)
                {
                    var current = await ctx.Records.LoadRequiredAsync(ctx.Record.Id);
                    var links = current.Links(linkFieldOnSource).Append(res.Record.Id).Distinct().ToList();
                    var upd = await ctx.Records.UpdateAsync(ctx.Record.Id, new() { [linkFieldOnSource] = links }, $"Linked {form.Name} {res.Record.Number}");
                    if (!upd.Ok) throw new WorkflowException(string.Join("; ", upd.Errors.Select(e => e.Message)));
                }
                await ctx.Db.SaveChangesAsync();
                return ((Record?)res.Record, (string?)null);
            });
        }
        catch (WorkflowException ex)
        {
            ctx.Db.ChangeTracker.Clear();
            return (null, ex.Message);
        }
    }

    /// <summary>Non-voided records of <paramref name="formKey"/> referenced by a link field.</summary>
    public static async Task<List<Record>> LinkedAsync(RecordContext ctx, string linkField, string formKey)
    {
        var ids = ctx.Snapshot.Links(linkField);
        if (ids.Count == 0 || !ctx.Config.FormsByKey.TryGetValue(formKey, out var form)) return [];
        return await ctx.Db.Records.AsNoTracking().Where(r => ids.Contains(r.Id) && r.FormId == form.Id && r.PhaseType != PhaseType.Voided).ToListAsync();
    }

    /// <summary>
    /// INV-041: "Is a ⟨X⟩ required?" Yes → a linked, non-voided X must exist; No → a justification must be stored.
    /// </summary>
    public static async Task CheckRequiredGateAsync(RoutingContext ctx, string questionField, string justificationField, string linkField,
        string formKey, string noun)
    {
        var answer = ctx.Snapshot.Bool(questionField);
        if (answer == null)
            ctx.Error($"Answer \"Is a {noun} required?\" before leaving {ctx.From.Name}.");
        else if (answer == true && (await LinkedAsync(ctx, linkField, formKey)).Count == 0)
            ctx.Error($"A {noun} is required. Use \"Initiate {noun}\" (or link an existing {noun}) before leaving {ctx.From.Name} (INV-041).");
        else if (answer == false && string.IsNullOrWhiteSpace(ctx.Snapshot.Text(justificationField)))
            ctx.Error($"Explain why no {noun} is required. The justification is kept in the audit trail (INV-041).");
    }

    /// <summary>Open (Normal-phase) action items under the record (QE-003).</summary>
    public static async Task<List<string>> OpenActionItemsAsync(RecordContext ctx)
    {
        if (!ctx.Config.FormsByKey.TryGetValue("action_item", out var ai)) return [];
        return await ctx.Db.Records.AsNoTracking().Where(r => r.ParentId == ctx.Record.Id && r.FormId == ai.Id && r.PhaseType == PhaseType.Normal)
            .Select(r => r.Number).ToListAsync();
    }
}

/// <summary>QE-002: planned-action rows become real Action Items when the parent enters its configured phase.</summary>
public static class PlannedActions
{
    public static async Task<int> CreateAsync(RoutingContext ctx, string sectionKey, int? verifierId, string actionField = "action",
        string assigneeField = "assignee", string daysField = "days", string verifyField = "verify")
    {
        var calendar = ctx.Get<BusinessCalendar>();
        var snap = ctx.Snapshot;
        var proposed = new RecordData();
        var updated = proposed.RowsOf(sectionKey);
        var created = 0;
        foreach (var row in snap.Rows(sectionKey))
        {
            var clone = row.Clone();
            if (row.CreatedRecordId == null && row.Values.Text(actionField) is { } action && row.Values.Ref(assigneeField) is { } assignee)
            {
                var due = await calendar.AddAsync(BusinessCalendar.Today, (int)(row.Values.Number(daysField) ?? 10), true);
                var verify = row.Values.Bool(verifyField) == true && verifierId != null && verifierId != assignee;
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
                        ["description"] = $"Planned action from {snap.Record.Number}: {snap.Record.Title}",
                        ["assignee"] = assignee,
                        ["due_date"] = due,
                        ["verification_required"] = verify,
                        ["verifier"] = verify ? verifierId : null,
                    },
                    CreateComment = $"Created automatically from the planned actions of {snap.Record.Number} (QE-002)",
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
            await ctx.Records.SaveAsync(fresh, proposed, new SaveOptions { System = true });
        }
        return created;
    }
}

/// <summary>QE-012: cost tracking subform with subtotals by category.</summary>
public static class QualityCosts
{
    public const string ListKey = "quality_cost_category";

    public static void Keywords(AppBuilder app) => app.Keywords(ListKey, "Quality cost category", k => k
        .Add("administrative", "Administrative").Add("equipment", "Equipment").Add("labor", "Labor").Add("materials", "Materials")
        .Add("transportation", "Transportation").Add("third_party", "Third-party services").Add("scrap", "Scrap").Add("rework", "Rework"));

    /// <summary>Adds the <c>costs</c> subform and the <c>cost_summary</c> section (computed total).</summary>
    public static FormBuilder Define(FormBuilder f, string tab = "Costs") => f
        .Subform("costs", "Cost log", s =>
        {
            s.Keyword("cost_category", "Category", ListKey, 3).Required();
            s.Date("cost_date", "Date", 2).Required();
            s.Currency("cost_amount", "Amount", "USD", 2).Required().Min(0);
            s.Text("cost_note", "Description", 5);
        }, tab: tab, rowNoun: "cost", icon: "bi-cash-coin", description: "Record each cost as it's incurred. Subtotals by category are calculated automatically.")
        .Section("cost_summary", "Cost of poor quality", s =>
        {
            s.Currency("cost_total", "Total cost", "USD", 4).ReadOnly().Help("Sum of every line in the cost log (calculated).");
        }, tab: tab, icon: "bi-calculator");

    public static void Compute(SavingContext ctx)
    {
        if (ctx.Form.Fields.All(f => f.Key != "cost_total")) return;
        var total = ctx.Proposed.RowsOf("costs").Sum(r => r.Values.Number("cost_amount") ?? 0m);
        ctx.Set("cost_total", total == 0m && ctx.Proposed.RowsOf("costs").Count == 0 ? null : total);
    }

    public static RecordPanel? Panel(RecordContext ctx)
    {
        var rows = ctx.Snapshot.Rows("costs");
        if (rows.Count == 0) return null;
        var lines = rows.GroupBy(r => ctx.Snapshot.KeywordKey(r, "cost_category") ?? "")
            .Select(g => new CostLine(ctx.Config.Keyword(ListKey, g.Key)?.Label ?? "Uncategorized", g.Sum(r => r.Values.Number("cost_amount") ?? 0m), g.Count()))
            .OrderByDescending(l => l.Amount).ToList();
        return new RecordPanel
        {
            Key = "quality-costs", Title = "Cost summary", Icon = "bi-cash-coin", Placement = "side", Order = 50,
            Partial = "~/Modules/Quality/Views/_CostSummary.cshtml", Model = new CostSummary(lines, lines.Sum(l => l.Amount)),
        };
    }
}

public sealed record CostLine(string Category, decimal Amount, int Count);
public sealed record CostSummary(List<CostLine> Lines, decimal Total);

/// <summary>CAPA-007: risk-based priority from severity × occurrence.</summary>
public static class RiskPriority
{
    public static void Keywords(AppBuilder app)
    {
        app.Keywords("quality_severity", "Severity of harm", k => k
            .Add("negligible", "1 · Negligible", "secondary").Add("minor", "2 · Minor", "info").Add("serious", "3 · Serious", "warning")
            .Add("critical", "4 · Critical", "danger").Add("catastrophic", "5 · Catastrophic", "dark"));
        app.Keywords("quality_occurrence", "Probability of occurrence", k => k
            .Add("remote", "1 · Remote").Add("unlikely", "2 · Unlikely").Add("occasional", "3 · Occasional")
            .Add("probable", "4 · Probable").Add("frequent", "5 · Frequent"));
    }

    public static int Score(string? key) => key switch
    {
        "negligible" or "remote" => 1, "minor" or "unlikely" => 2, "serious" or "occasional" => 3,
        "critical" or "probable" => 4, "catastrophic" or "frequent" => 5, _ => 0,
    };

    /// <summary>Severity × occurrence (1–25): ≥15 critical, ≥8 high, ≥4 medium, else low. Null when either input is missing.</summary>
    public static string? Priority(string? severity, string? occurrence)
    {
        var s = Score(severity);
        var o = Score(occurrence);
        if (s == 0 || o == 0) return null;
        var rpn = s * o;
        return rpn >= 15 || s == 5 ? "critical" : rpn >= 8 ? "high" : rpn >= 4 ? "medium" : "low";
    }

    /// <summary>Phase-duration factor per priority: critical work gets half the standard time, low gets 50% more.</summary>
    public static decimal Factor(string? priority) => priority switch { "critical" => 0.5m, "high" => 0.75m, "low" => 1.5m, _ => 1m };
}

/// <summary>Sites used across the quality applications.</summary>
public static class QualitySites
{
    public static void Keywords(AppBuilder app) => app.Keywords("quality_site", "Site", k => k
        .Add("kalamazoo", "Kalamazoo, MI").Add("boston", "Boston, MA").Add("galway", "Galway, Ireland"));
}
