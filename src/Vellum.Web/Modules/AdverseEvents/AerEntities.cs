using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vellum.Modules.AdverseEvents;

/// <summary>
/// Regulatory index of every MedWatch record (one row per record). The report content lives in the record; this row holds the
/// keys the database itself must police: one non-voided initial/5-day MedWatch per MDR tree for MFR-type numbering (INV-047),
/// and unique generated report numbers per reporter type, code and year (INV-023). Written in the same transaction as the
/// record's create and routes.
/// </summary>
public class MedWatchReport
{
    public Guid MedWatchId { get; set; }
    public Guid? ComplaintId { get; set; }
    public Guid? MdrTreeId { get; set; }
    /// <summary>Initial or 5-day report (as opposed to a supplemental / follow-up).</summary>
    public bool IsInitial { get; set; }
    /// <summary>manufacturer | importer.</summary>
    public string ReporterType { get; set; } = AerKeys.Manufacturer;
    public bool IsVoided { get; set; }
    public Guid? OriginalMedWatchId { get; set; }
    public int? FollowUpNumber { get; set; }
    /// <summary>Report number parts of the record's reporter type, set on entry to Submitted to FDA (AER-005).</summary>
    public string? ReportCode { get; set; }
    public int? ReportYear { get; set; }
    public int? ReportSequence { get; set; }
    /// <summary>True when the sequence came from the yearly sequence (sequential format), false when entered by hand.</summary>
    public bool SequenceGenerated { get; set; }
    public DateTime? NumberAssignedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One transmission of a MedWatch to the FDA through an <see cref="IEmdrGateway"/> (AER-008).</summary>
public class EmdrSubmission
{
    public long Id { get; set; }
    public Guid MedWatchId { get; set; }
    public int Attempt { get; set; }
    /// <summary>manual | simulated | as2.</summary>
    public string Gateway { get; set; } = "";
    /// <summary>Unique message ID carried in the XML (the gateway correlates acknowledgements with it).</summary>
    public string MessageId { get; set; } = "";
    public string ReportNumber { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FileSha256 { get; set; } = "";
    public long? AttachmentId { get; set; }
    public string FormVersion { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string RuleSetVersion { get; set; } = "";
    /// <summary>test | production (the eMDR profile's account type at the time).</summary>
    public string AccountType { get; set; } = "test";
    public DateTime CreatedAt { get; set; }
    public int CreatedById { get; set; }
    /// <summary>When the file reached the FDA gateway (manual: when the user recorded the ESG WebTrader upload).</summary>
    public DateTime? SentAt { get; set; }
    /// <summary>AwaitingUpload | Sent | Accepted | Rejected | Failed.</summary>
    public string Status { get; set; } = EmdrStatus.AwaitingUpload;
    public DateTime? Ack1At { get; set; }
    public DateTime? Ack2At { get; set; }
    public DateTime? Ack3At { get; set; }
    public string? LastError { get; set; }
    /// <summary>Simulated gateway only: accept | reject_ack3 | withhold_ack3.</summary>
    public string? SimulatedOutcome { get; set; }
    /// <summary>A later attempt replaced this one (resubmission after a rejection).</summary>
    public bool Superseded { get; set; }
    public DateTime? OverdueAlertedAt { get; set; }
    public DateTime? FailureAlertedAt { get; set; }
    public DateTime? DeadlineAlertedAt { get; set; }
}

public static class EmdrStatus
{
    public const string AwaitingUpload = "AwaitingUpload";
    public const string Sent = "Sent";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";

    public static bool IsPending(string s) => s is AwaitingUpload or Sent;
}

/// <summary>
/// INT-902: the eMDR gateway profile. One per environment (unique environment key), bound to the environment it was created
/// in and never part of configuration promotion. Secrets and the private key are encrypted at rest with ASP.NET Core Data
/// Protection and never shown again.
/// </summary>
public class EmdrProfile
{
    public int Id { get; set; }
    /// <summary>"{EnvironmentType}:{EnvironmentName}" of the environment the profile belongs to.</summary>
    public string EnvironmentKey { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    /// <summary>test | production.</summary>
    public string AccountType { get; set; } = "test";
    public string SenderOrganization { get; set; } = "";
    public string SenderRoutingId { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string? AcknowledgementUrl { get; set; }
    public string? FdaRoutingId { get; set; }
    public string? EsgUrl { get; set; }
    public string? SubmissionCenter { get; set; }
    /// <summary>manual | simulated | as2.</summary>
    public string Gateway { get; set; } = "manual";
    /// <summary>Simulated gateway: accept | reject_ack3 | withhold_ack3.</summary>
    public string SimulatedOutcome { get; set; } = "accept";

    // FDA ESG server (SSL) certificate, as supplied by the FDA.
    public string? FdaCertificatePem { get; set; }

    // Our encryption / signing certificate (generated here, public part sent to the FDA).
    public string? CertCommonName { get; set; }
    public string? CertOrganization { get; set; }
    public string? CertOrganizationalUnit { get; set; }
    public string? CertLocality { get; set; }
    public string? CertState { get; set; }
    public string? CertCountry { get; set; }
    public string? CertEmail { get; set; }
    public string? CertPem { get; set; }
    public string? CertThumbprint { get; set; }
    public DateTime? CertNotBefore { get; set; }
    public DateTime? CertNotAfter { get; set; }
    public DateTime? CertGeneratedAt { get; set; }
    /// <summary>When an administrator recorded that the FDA has installed the current certificate.</summary>
    public DateTime? CertFdaConfirmedAt { get; set; }
    /// <summary>Data Protection payload of the PKCS#8 private key.</summary>
    public string? PrivateKeyProtected { get; set; }

    /// <summary>Data Protection payload of the ESG account / AS2 password.</summary>
    public string? EsgPasswordProtected { get; set; }
    public DateTime? EsgPasswordSetAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int UpdatedById { get; set; }
}

public sealed class MedWatchReportConfig : IEntityTypeConfiguration<MedWatchReport>
{
    public void Configure(EntityTypeBuilder<MedWatchReport> e)
    {
        e.ToTable("AerMedWatchReports");
        e.HasKey(x => x.MedWatchId);
        e.Property(x => x.ReporterType).HasMaxLength(20);
        e.Property(x => x.ReportCode).HasMaxLength(20);
        // INV-047: at most one non-voided initial (or 5-day) MedWatch per MDR decision tree for MFR-type numbering.
        e.HasIndex(x => x.MdrTreeId).IsUnique().HasDatabaseName("UX_AerMedWatch_InitialPerTree")
            .HasFilter("[MdrTreeId] IS NOT NULL AND [IsInitial] = 1 AND [IsVoided] = 0 AND [ReporterType] = 'manufacturer'");
        // INV-023: an issued report number is never issued twice (voided reports keep theirs).
        e.HasIndex(x => new { x.ReporterType, x.ReportCode, x.ReportYear, x.ReportSequence }).IsUnique().HasDatabaseName("UX_AerMedWatch_Number")
            .HasFilter("[IsInitial] = 1 AND [ReportSequence] IS NOT NULL AND [ReportCode] IS NOT NULL AND [ReportYear] IS NOT NULL");
        // AER-011: one live follow-up per number.
        e.HasIndex(x => new { x.OriginalMedWatchId, x.FollowUpNumber }).IsUnique().HasDatabaseName("UX_AerMedWatch_FollowUp")
            .HasFilter("[OriginalMedWatchId] IS NOT NULL AND [FollowUpNumber] IS NOT NULL AND [IsVoided] = 0");
        e.HasIndex(x => x.ComplaintId);
    }
}

public sealed class EmdrSubmissionConfig : IEntityTypeConfiguration<EmdrSubmission>
{
    public void Configure(EntityTypeBuilder<EmdrSubmission> e)
    {
        e.ToTable("AerEmdrSubmissions");
        e.HasIndex(x => x.MessageId).IsUnique();
        e.HasIndex(x => new { x.MedWatchId, x.Attempt }).IsUnique();
        e.HasIndex(x => x.Status);
        e.Property(x => x.Gateway).HasMaxLength(20);
        e.Property(x => x.MessageId).HasMaxLength(80);
        e.Property(x => x.ReportNumber).HasMaxLength(40);
        e.Property(x => x.FileName).HasMaxLength(200);
        e.Property(x => x.FileSha256).HasMaxLength(64);
        e.Property(x => x.Status).HasMaxLength(20);
        e.Property(x => x.AccountType).HasMaxLength(20);
        e.Property(x => x.FormVersion).HasMaxLength(60);
        e.Property(x => x.SchemaVersion).HasMaxLength(60);
        e.Property(x => x.RuleSetVersion).HasMaxLength(60);
        e.Property(x => x.SimulatedOutcome).HasMaxLength(20);
        e.Property(x => x.LastError).HasMaxLength(4000);
    }
}

public sealed class EmdrProfileConfig : IEntityTypeConfiguration<EmdrProfile>
{
    public void Configure(EntityTypeBuilder<EmdrProfile> e)
    {
        e.ToTable("AerEmdrProfiles");
        // INT-902: one profile per environment.
        e.HasIndex(x => x.EnvironmentKey).IsUnique();
        e.Property(x => x.EnvironmentKey).HasMaxLength(120);
        e.Property(x => x.EnvironmentName).HasMaxLength(80);
        e.Property(x => x.AccountType).HasMaxLength(20);
        e.Property(x => x.SenderOrganization).HasMaxLength(120);
        e.Property(x => x.SenderRoutingId).HasMaxLength(60);
        e.Property(x => x.SenderEmail).HasMaxLength(200);
        e.Property(x => x.AcknowledgementUrl).HasMaxLength(400);
        e.Property(x => x.FdaRoutingId).HasMaxLength(60);
        e.Property(x => x.EsgUrl).HasMaxLength(400);
        e.Property(x => x.SubmissionCenter).HasMaxLength(20);
        e.Property(x => x.Gateway).HasMaxLength(20);
        e.Property(x => x.SimulatedOutcome).HasMaxLength(20);
        foreach (var p in new[] { nameof(EmdrProfile.CertCommonName), nameof(EmdrProfile.CertOrganization), nameof(EmdrProfile.CertOrganizationalUnit),
                     nameof(EmdrProfile.CertLocality), nameof(EmdrProfile.CertState), nameof(EmdrProfile.CertEmail) })
            e.Property(p).HasMaxLength(128);
        e.Property(x => x.CertCountry).HasMaxLength(2);
        e.Property(x => x.CertThumbprint).HasMaxLength(64);
        e.Property(x => x.CertPem).HasMaxLength(-1);
        e.Property(x => x.FdaCertificatePem).HasMaxLength(-1);
        e.Property(x => x.PrivateKeyProtected).HasMaxLength(-1);
        e.Property(x => x.EsgPasswordProtected).HasMaxLength(-1);
    }
}
