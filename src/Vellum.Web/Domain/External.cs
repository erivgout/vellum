using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vellum.Domain;

/// <summary>Where a portal request stands, from the supplier's point of view (SUP-004).</summary>
public enum ExternalShareStatus
{
    /// <summary>Waiting for the supplier.</summary>
    Open = 0,
    /// <summary>The supplier submitted; the internal owner reviews it.</summary>
    Submitted = 1,
    /// <summary>Sent back to the supplier for changes.</summary>
    Returned = 2,
    /// <summary>Closed: accepted, or (scorecards) simply shared. Read-only for the supplier.</summary>
    Accepted = 3,
    /// <summary>No longer shared. The supplier can't see the record any more.</summary>
    Withdrawn = 4,
}

/// <summary>
/// INV-088 / SUP-004: a record shared with an external organization (a supplier profile record) through the supplier portal.
/// This row is the only thing that makes a record readable by external users: <see cref="Engine.Security.AccessService.Readable"/>
/// returns, for an external user, exactly the records with a non-withdrawn share for that user's organization.
/// Only internal users create shares (the "Send to supplier portal" actions).
/// </summary>
public class ExternalShare
{
    public int Id { get; set; }
    public Guid RecordId { get; set; }
    /// <summary>The supplier profile record the record is shared with.</summary>
    public Guid OrgId { get; set; }
    /// <summary>scar | document | questionnaire | audit_response | scorecard (see Modules/Portal/PortalKinds.cs).</summary>
    public string Kind { get; set; } = "";
    public ExternalShareStatus Status { get; set; }
    /// <summary>Message from the internal sender, shown to the supplier.</summary>
    public string? Message { get; set; }
    /// <summary>Feedback when a response is returned for changes, shown to the supplier.</summary>
    public string? Feedback { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTime SharedAt { get; set; }
    public int SharedById { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public int? SubmittedById { get; set; }
    public DateTime? ClosedAt { get; set; }
}

/// <summary>
/// A one-time, expiring account link (portal invitation or password reset, SEC-107). Only the SHA-256 of the token is stored;
/// the plaintext exists only in the email sent to the user.
/// </summary>
public class AccountToken
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    /// <summary>invite | reset</summary>
    public string Purpose { get; set; } = "invite";
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int? CreatedById { get; set; }
}

public sealed class ExternalShareConfiguration : IEntityTypeConfiguration<ExternalShare>
{
    public void Configure(EntityTypeBuilder<ExternalShare> e)
    {
        e.ToTable("ExternalShares");
        e.HasIndex(x => new { x.RecordId, x.OrgId }).IsUnique();
        e.HasIndex(x => new { x.OrgId, x.Status });
        e.Property(x => x.Kind).HasMaxLength(40);
        e.Property(x => x.Message).HasMaxLength(4000);
        e.Property(x => x.Feedback).HasMaxLength(4000);
        e.HasOne<Record>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AccountTokenConfiguration : IEntityTypeConfiguration<AccountToken>
{
    public void Configure(EntityTypeBuilder<AccountToken> e)
    {
        e.ToTable("AccountTokens");
        e.HasIndex(x => x.TokenHash).IsUnique();
        e.Property(x => x.Purpose).HasMaxLength(20);
        e.Property(x => x.TokenHash).HasMaxLength(64);
        e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
