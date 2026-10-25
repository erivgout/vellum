using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vellum.Modules.Assets;

/// <summary>Deviation and pass/fail of one reading.</summary>
public readonly record struct ReadingResult(decimal Deviation, bool Pass);

/// <summary>
/// AST-022 / INV-064, exactly. With nominal M, minus tolerance T₋ and plus tolerance T₊, a reading R gives:
/// R = M → 0, Pass; M − T₋ ≤ R &lt; M → R − M, Pass; R &lt; M − T₋ → R − (M − T₋), Fail;
/// M &lt; R ≤ M + T₊ → R − M, Pass; R &gt; M + T₊ → R − (M + T₊), Fail. Exact decimal arithmetic, no rounding.
/// </summary>
public static class CalibrationMath
{
    public static ReadingResult Evaluate(decimal reading, decimal nominal, decimal minusTolerance, decimal plusTolerance)
    {
        if (minusTolerance < 0 || plusTolerance < 0) throw new ArgumentOutOfRangeException(nameof(minusTolerance), "Tolerances are magnitudes and can't be negative.");
        if (reading == nominal) return new ReadingResult(0m, true);
        if (reading < nominal)
        {
            var lower = nominal - minusTolerance;
            return reading >= lower ? new ReadingResult(reading - nominal, true) : new ReadingResult(reading - lower, false);
        }
        var upper = nominal + plusTolerance;
        return reading <= upper ? new ReadingResult(reading - nominal, true) : new ReadingResult(reading - upper, false);
    }

    /// <summary>AST-023: overall Fail if any reading fails; null until every reading is entered.</summary>
    public static bool? Overall(IReadOnlyCollection<bool?> results)
    {
        if (results.Any(r => r == false)) return false;
        if (results.Count == 0 || results.Any(r => r == null)) return null;
        return true;
    }
}

/// <summary>Asset status rules (AST-005, AST-027, SAF-040).</summary>
public static class AssetStatus
{
    /// <summary>Statuses only changed by explicit actions or calibration results, never by the due-date calculation.</summary>
    public static readonly string[] Sticky = ["quarantined", "retired", "maintenance"];

    /// <summary>How many days before the due date an asset shows as "Calibration due": a quarter of the interval, 3 to 30 days.</summary>
    public static int DueLeadDays(int intervalDays) => Math.Clamp(intervalDays / 4, 3, 30);

    public static string Compute(string? current, bool calibrationRequired, DateOnly? nextCalibration, int intervalDays, DateOnly today)
    {
        if (current != null && Sticky.Contains(current)) return current;
        if (!calibrationRequired) return "in_service";
        if (nextCalibration is not { } next) return "due";
        if (next < today) return "overdue";
        return next.DayNumber - today.DayNumber <= DueLeadDays(intervalDays) ? "due" : "in_service";
    }

    /// <summary>SAF-040 / AST-025: assets that can't be used to measure (as tools or standards).</summary>
    public static bool Usable(string? status) => status is "in_service" or "due";
}

/// <summary>
/// INV-022 enforced by the database: one row per asset ID, with a primary key on the (case-insensitive) tag.
/// Written in the same transaction that creates the asset; asset IDs never change afterwards.
/// </summary>
public sealed class AssetTagClaim
{
    public string Tag { get; set; } = "";
    public Guid RecordId { get; set; }
    public DateTime ClaimedAt { get; set; }
}

public sealed class AssetTagClaimConfiguration : IEntityTypeConfiguration<AssetTagClaim>
{
    public void Configure(EntityTypeBuilder<AssetTagClaim> b)
    {
        b.ToTable("AssetTagClaims");
        b.HasKey(x => x.Tag);
        b.Property(x => x.Tag).HasMaxLength(64);
        b.HasIndex(x => x.RecordId).IsUnique();
    }
}
