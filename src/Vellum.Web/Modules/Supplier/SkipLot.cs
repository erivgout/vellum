namespace Vellum.Modules.Supplier;

/// <summary>RCV-002 skip-lot profile of a material.</summary>
/// <param name="Allowed">Skip-lot may be used for this material at all.</param>
/// <param name="InitialPasses">Consecutive accepted lots needed before skipping starts.</param>
/// <param name="LotsToSkip">Lots released without inspection between two inspected lots once qualified.</param>
/// <param name="RequalifyPasses">Consecutive accepted lots needed after a rejection before skipping resumes.</param>
public sealed record SkipLotProfile(bool Allowed, int InitialPasses, int LotsToSkip, int RequalifyPasses)
{
    public static readonly SkipLotProfile Default = new(true, 3, 2, 5);
}

/// <summary>Outcome of an earlier lot of the same supplier and material, in receipt order.</summary>
public enum LotOutcome { Passed, Failed, Skipped }

public sealed record SkipLotDecision(bool Skip, string Basis, int ConsecutivePasses, int PassesNeeded, bool Qualified, int SkippedSinceInspection);

/// <summary>
/// INV-170: the inspection status of a receiving line is a deterministic function of the material profile (skip-lot
/// parameters, CoA requirement), the supplier's eligibility and the ordered history of earlier lots for the same
/// supplier and material. No clock, randomness or ambient state is involved, so replaying the same inputs always
/// gives the same answer.
/// </summary>
public static class SkipLot
{
    public static SkipLotDecision Evaluate(SkipLotProfile profile, IReadOnlyList<LotOutcome> history,
        string? supplierIneligibleReason = null, bool coaMissing = false)
    {
        var needed = Math.Max(1, profile.InitialPasses);
        var consecutive = 0;
        var qualified = false;
        var skipped = 0;
        var requalifying = false;
        foreach (var o in history)
        {
            switch (o)
            {
                case LotOutcome.Passed:
                    consecutive++;
                    if (!qualified && consecutive >= needed) qualified = true;
                    skipped = 0; // an inspected, accepted lot starts a new skip cycle
                    break;
                case LotOutcome.Failed:
                    consecutive = 0;
                    qualified = false;
                    skipped = 0;
                    needed = Math.Max(1, profile.RequalifyPasses);
                    requalifying = true;
                    break;
                case LotOutcome.Skipped:
                    skipped++;
                    break;
            }
        }

        SkipLotDecision Required(string why) => new(false, why, consecutive, needed, qualified, skipped);

        if (!profile.Allowed) return Required("Skip-lot isn't allowed for this material: every lot is inspected.");
        if (supplierIneligibleReason != null) return Required(supplierIneligibleReason);
        if (coaMissing) return Required("The material needs a certificate of analysis and none was received with this lot.");
        if (profile.LotsToSkip <= 0) return Required("The material's skip-lot profile skips no lots: every lot is inspected.");

        if (!qualified)
            return Required(requalifying
                ? $"Requalifying after a rejected lot: {consecutive} of {needed} consecutive accepted lots so far."
                : $"{consecutive} of {needed} consecutive accepted lots needed before skipping starts.");
        if (skipped >= profile.LotsToSkip)
            return Required($"{skipped} lot{(skipped == 1 ? "" : "s")} skipped since the last inspection: this lot is inspected to keep the skip-lot qualification.");
        return new SkipLotDecision(true,
            $"Skip-lot qualified ({consecutive} consecutive accepted lot{(consecutive == 1 ? "" : "s")}, {needed} needed). This is skipped lot {skipped + 1} of {profile.LotsToSkip} before the next inspection.",
            consecutive, needed, true, skipped);
    }
}
