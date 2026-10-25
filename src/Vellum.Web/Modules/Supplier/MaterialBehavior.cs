using System.Text.RegularExpressions;
using Vellum.Engine.Behaviors;

namespace Vellum.Modules.Supplier;

/// <summary>Material and chemical profiles (SUP-010..012): CAS number check digit, skip-lot profile sanity, supplier panel.</summary>
public sealed partial class MaterialBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["material"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var main = ctx.Proposed.Main;
        if (main.Text("cas_number") is { } cas && !IsValidCas(cas))
            ctx.Error("cas_number", $"\"{cas}\" isn't a valid CAS registry number (format 1234-56-7 with a correct check digit).");
        if (main.Bool("skip_lot_allowed") != false && main.Number("skip_count") is { } skip && skip > 0 && main.Number("skip_initial") is null)
            ctx.Error("skip_initial", "Say how many consecutive accepted lots are needed before skipping starts.");
        return Task.CompletedTask;
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.To.Key == "approved" && s.KeywordKey("commodity") == "chemical")
        {
            if (s.Bool("cas_registered") == true && string.IsNullOrWhiteSpace(s.Text("cas_number")))
                ctx.Error("Enter the CAS number for this CAS-registered chemical before approving it (SUP-011).");
            if (string.IsNullOrWhiteSpace(s.Text("hazard_summary")))
                ctx.Error("Summarize the chemical's hazards from the SDS before approving it (SUP-011).");
        }
        return Task.CompletedTask;
    }

    /// <summary>CAS registry number: 2–7 digits, 2 digits, 1 check digit; check = Σ(digit × position from the right) mod 10.</summary>
    public static bool IsValidCas(string cas)
    {
        var m = CasPattern().Match(cas.Trim());
        if (!m.Success) return false;
        var digits = m.Groups[1].Value + m.Groups[2].Value;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++) sum += (digits[digits.Length - 1 - i] - '0') * (i + 1);
        return sum % 10 == m.Groups[3].Value[0] - '0';
    }

    [GeneratedRegex(@"^(\d{2,7})-(\d{2})-(\d)$")]
    private static partial Regex CasPattern();

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var vm = await SupplierPanels.MaterialSuppliersAsync(ctx);
        var lots = await SupplierPanels.ReceivingAsync(ctx, "material");
        return
        [
            new RecordPanel
            {
                Key = "mat-suppliers", Title = "Suppliers", Icon = "bi-building", Placement = "tab", Order = 1, Count = vm.Suppliers.Count,
                Partial = "~/Modules/Supplier/Views/_MaterialSuppliers.cshtml", Model = vm,
            },
            new RecordPanel
            {
                Key = "mat-receiving", Title = "Receiving", Icon = "bi-box-arrow-in-down", Placement = "tab", Order = 2, Count = lots.Lots.Count,
                Partial = "~/Modules/Supplier/Views/_ReceivingHistory.cshtml", Model = lots,
            },
        ];
    }
}
