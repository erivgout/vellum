using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>How a SharePoint profile connects (INT-301).</summary>
public enum SpConnection
{
    /// <summary>SharePoint Online through a Microsoft Graph app registration (client credentials, Sites.Selected).</summary>
    Graph = 0,
    /// <summary>Development and demo: writes files and a metadata sidecar under App_Data/sharepoint.</summary>
    LocalFolder = 1,
}

public enum SpAuthMethod { ClientSecret = 0, Certificate = 1 }

/// <summary>INT-308: what happens to published files when their revision is superseded or the document is obsoleted.</summary>
public enum SpWithdrawAction
{
    /// <summary>Delete the file (and its version history) from the library.</summary>
    Delete = 0,
    /// <summary>Move it to the obsolete folder, rename it with its revision and status, and update the status columns.</summary>
    MarkObsolete = 1,
}

public enum SpJobKind { Publish = 0, Withdraw = 1 }

public enum SpJobStatus
{
    /// <summary>Waiting for its first attempt or for a retry (NextAttemptAt).</summary>
    Pending = 0,
    Succeeded = 1,
    /// <summary>Permanent failure: retries exhausted or a non-retryable error. The profile's notify list was told.</summary>
    Failed = 2,
    /// <summary>Nothing to do any more (rule disabled, record left the phase, not effective…).</summary>
    Skipped = 3,
    Cancelled = 4,
}

public enum SpPublicationStatus
{
    /// <summary>The file is in the library and is current.</summary>
    Published = 0,
    /// <summary>A later publication (usually the next effective revision) overwrote the same file.</summary>
    Replaced = 1,
    /// <summary>Deleted from the library (INT-308).</summary>
    Removed = 2,
    /// <summary>Moved to the obsolete folder and marked obsolete (INT-308).</summary>
    MarkedObsolete = 3,
}

/// <summary>INT-301: one SharePoint site or tenant, unique by host.</summary>
public class SharePointProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lower-case host, e.g. contoso.sharepoint.com (unique).</summary>
    public string Host { get; set; } = "";
    public SpConnection Connection { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public SpAuthMethod AuthMethod { get; set; }
    /// <summary>Client secret, encrypted with ASP.NET Core Data Protection. Never shown back.</summary>
    public string? SecretProtected { get; set; }
    public DateTime? SecretSetAt { get; set; }
    /// <summary>PKCS#12 certificate (base64), encrypted with Data Protection.</summary>
    public string? CertificateProtected { get; set; }
    public string? CertificatePasswordProtected { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? CertificateSubject { get; set; }
    public DateTime? CertificateExpiresAt { get; set; }
    /// <summary>Accounts emailed and notified when publishing fails permanently (INT-306).</summary>
    public List<int> NotifyUserIds { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>Local folder only: every call fails as if SharePoint were unreachable (to exercise NFR-025).</summary>
    public bool SimulateOutage { get; set; }
    public DateTime? LastTestAt { get; set; }
    public bool? LastTestOk { get; set; }
    public string? LastTestSummary { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public List<SharePointLibrary> Libraries { get; set; } = [];
}

/// <summary>INT-301: a document library of a profile (identifier + relative URL).</summary>
public class SharePointLibrary
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public SharePointProfile Profile { get; set; } = null!;
    /// <summary>Identifier used in rules, e.g. "controlled-documents".</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Server-relative URL: "/sites/QMS/Controlled Documents". The last segment is the library; the rest is the site.</summary>
    public string RelativeUrl { get; set; } = "";
    /// <summary>Local folder only: the library's column internal names (one per line or comma-separated), standing in for the list schema. Empty accepts any column.</summary>
    public string? SimulatedColumns { get; set; }
}

/// <summary>INT-302: maps Vellum fields, system values or templates of one form to SharePoint column internal names.</summary>
public class SharePointMapping
{
    public int Id { get; set; }
    public string FormKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public List<SharePointMappingEntry> Entries { get; set; } = [];
}

public class SharePointMappingEntry
{
    public int Id { get; set; }
    public int MappingId { get; set; }
    /// <summary>A field key ("doc_type"), a system value ("$revision") or a template ("{number} rev {revision}").</summary>
    public string Source { get; set; } = "";
    /// <summary>SharePoint column internal name (case-sensitive).</summary>
    public string Column { get; set; } = "";
    public int Order { get; set; }
}

/// <summary>DSN-602 / INT-304: publish attachments of a form when a record enters, or is saved in, a phase.</summary>
public class SharePointRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string FormKey { get; set; } = "";
    /// <summary>Phase key, or "*" for non-workflow forms (publish on create and save).</summary>
    public string PhaseKey { get; set; } = "";
    public bool OnEnter { get; set; } = true;
    public bool OnSave { get; set; }
    public int ProfileId { get; set; }
    public int LibraryId { get; set; }
    /// <summary>Main-form attachment fields to publish.</summary>
    public List<string> FieldKeys { get; set; } = [];
    /// <summary>Folder under the library root. Literal ("Procedures") or computed from tokens ("{doc_type}/{number}"). Empty = root.</summary>
    public string FolderTemplate { get; set; } = "";
    /// <summary>File name without extension, from tokens ("{number} - {title}"). Empty keeps the attachment's name.</summary>
    public string? FileNameTemplate { get; set; }
    /// <summary>Publish the PDF rendition of each attachment when there is one.</summary>
    public bool PreferRendition { get; set; } = true;
    /// <summary>Publish the original when no rendition exists (otherwise wait for it).</summary>
    public bool FallbackToOriginal { get; set; } = true;
    public int? MappingId { get; set; }
    /// <summary>SAF-043: revision-controlled forms only publish their effective revision.</summary>
    public bool EffectiveOnly { get; set; } = true;
    /// <summary>INT-305: overwrite in place so the library keeps version history; off deletes the old file first.</summary>
    public bool KeepVersionHistory { get; set; } = true;
    public SpWithdrawAction WithdrawAction { get; set; }
    /// <summary>For <see cref="SpWithdrawAction.MarkObsolete"/>: folder (from the library root) that obsolete files move to. Empty keeps them in place.</summary>
    public string? ObsoleteFolder { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}

/// <summary>NFR-025 / INT-306: durable outbound queue processed by <see cref="SharePointPublishingJob"/> with exponential backoff.</summary>
public class SharePointJob
{
    public long Id { get; set; }
    public SpJobKind Kind { get; set; }
    public Guid RecordId { get; set; }
    public int? RuleId { get; set; }
    /// <summary>What caused it: "Routed into Approved", "Saved in Approved", "Superseded", "Manual retry"…</summary>
    public string Trigger { get; set; } = "";
    public SpJobStatus Status { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 8;
    public DateTime NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Specific error code (INT-306), e.g. SP-AUTH, SP-COLUMN, SP-UNAVAILABLE.</summary>
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public string? Result { get; set; }
}

/// <summary>INT-307: one published file of a record (history: file, revision, library, URL, time and result).</summary>
public class SharePointPublication
{
    public long Id { get; set; }
    public Guid RecordId { get; set; }
    public Guid? FamilyId { get; set; }
    public string? Revision { get; set; }
    public int RevisionOrdinal { get; set; }
    public int RuleId { get; set; }
    public int ProfileId { get; set; }
    public int LibraryId { get; set; }
    public string FieldKey { get; set; } = "";
    /// <summary>The attachment whose content was published (the rendition when one was used).</summary>
    public long AttachmentId { get; set; }
    /// <summary>The original attachment when a rendition was published.</summary>
    public long? OriginalAttachmentId { get; set; }
    public bool IsRendition { get; set; }
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string FolderPath { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Case-insensitive identity of the location: "{libraryId}:/{folder}/{file}" in lower case.</summary>
    public string PathKey { get; set; } = "";
    public string? WebUrl { get; set; }
    /// <summary>Drive item ID (Graph) or library-relative path (local folder).</summary>
    public string? RemoteId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public SpPublicationStatus Status { get; set; }
    public DateTime PublishedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? WithdrawnAt { get; set; }
    public string? WithdrawnReason { get; set; }
    public long? ReplacedById { get; set; }
    public long? JobId { get; set; }
}

/// <summary>ADM-1005: every attempt of a queued job (status, timing, error).</summary>
public class SharePointAttempt
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public DateTime At { get; set; }
    public int DurationMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}

public sealed class SharePointProfileConfig : IEntityTypeConfiguration<SharePointProfile>
{
    public void Configure(EntityTypeBuilder<SharePointProfile> e)
    {
        e.ToTable("SharePointProfiles");
        e.Property(x => x.Name).HasMaxLength(120);
        e.Property(x => x.Host).HasMaxLength(253);
        e.HasIndex(x => x.Host).IsUnique();
        e.Property(x => x.TenantId).HasMaxLength(100);
        e.Property(x => x.ClientId).HasMaxLength(100);
        e.Property(x => x.CertificateThumbprint).HasMaxLength(64);
        e.Property(x => x.CertificateSubject).HasMaxLength(400);
        e.Property(x => x.LastTestSummary).HasMaxLength(2000);
        e.Property(x => x.SecretProtected).HasMaxLength(-1);
        e.Property(x => x.CertificateProtected).HasMaxLength(-1);
        e.Property(x => x.CertificatePasswordProtected).HasMaxLength(-1);
        e.HasMany(x => x.Libraries).WithOne(l => l.Profile).HasForeignKey(l => l.ProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SharePointLibraryConfig : IEntityTypeConfiguration<SharePointLibrary>
{
    public void Configure(EntityTypeBuilder<SharePointLibrary> e)
    {
        e.ToTable("SharePointLibraries");
        e.Property(x => x.Key).HasMaxLength(64);
        e.Property(x => x.Name).HasMaxLength(120);
        e.Property(x => x.RelativeUrl).HasMaxLength(400);
        e.Property(x => x.SimulatedColumns).HasMaxLength(4000);
        e.HasIndex(x => new { x.ProfileId, x.Key }).IsUnique();
    }
}

public sealed class SharePointMappingConfig : IEntityTypeConfiguration<SharePointMapping>
{
    public void Configure(EntityTypeBuilder<SharePointMapping> e)
    {
        e.ToTable("SharePointMappings");
        e.Property(x => x.FormKey).HasMaxLength(64);
        e.Property(x => x.Name).HasMaxLength(120);
        e.Property(x => x.Description).HasMaxLength(500);
        e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.MappingId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SharePointMappingEntryConfig : IEntityTypeConfiguration<SharePointMappingEntry>
{
    public void Configure(EntityTypeBuilder<SharePointMappingEntry> e)
    {
        e.ToTable("SharePointMappingEntries");
        e.Property(x => x.Source).HasMaxLength(400);
        e.Property(x => x.Column).HasMaxLength(255);
    }
}

public sealed class SharePointRuleConfig : IEntityTypeConfiguration<SharePointRule>
{
    public void Configure(EntityTypeBuilder<SharePointRule> e)
    {
        e.ToTable("SharePointRules");
        e.Property(x => x.Name).HasMaxLength(120);
        e.Property(x => x.FormKey).HasMaxLength(64);
        e.Property(x => x.PhaseKey).HasMaxLength(64);
        e.Property(x => x.FolderTemplate).HasMaxLength(400);
        e.Property(x => x.FileNameTemplate).HasMaxLength(255);
        e.Property(x => x.ObsoleteFolder).HasMaxLength(400);
        e.HasIndex(x => x.FormKey);
    }
}

public sealed class SharePointJobConfig : IEntityTypeConfiguration<SharePointJob>
{
    public void Configure(EntityTypeBuilder<SharePointJob> e)
    {
        e.ToTable("SharePointJobs");
        e.Property(x => x.Trigger).HasMaxLength(200);
        e.Property(x => x.ErrorCode).HasMaxLength(40);
        e.Property(x => x.Error).HasMaxLength(2000);
        e.Property(x => x.Result).HasMaxLength(1000);
        e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        e.HasIndex(x => x.RecordId);
    }
}

public sealed class SharePointPublicationConfig : IEntityTypeConfiguration<SharePointPublication>
{
    public void Configure(EntityTypeBuilder<SharePointPublication> e)
    {
        e.ToTable("SharePointPublications");
        e.Property(x => x.Revision).HasMaxLength(20);
        e.Property(x => x.FieldKey).HasMaxLength(64);
        e.Property(x => x.Sha256).HasMaxLength(64);
        e.Property(x => x.FolderPath).HasMaxLength(400);
        e.Property(x => x.FileName).HasMaxLength(255);
        e.Property(x => x.PathKey).HasMaxLength(700);
        e.Property(x => x.WebUrl).HasMaxLength(2000);
        e.Property(x => x.RemoteId).HasMaxLength(700);
        e.Property(x => x.WithdrawnReason).HasMaxLength(500);
        e.Property(x => x.MetadataJson).HasMaxLength(-1);
        e.HasIndex(x => x.RecordId);
        e.HasIndex(x => x.FamilyId);
        e.HasIndex(x => new { x.LibraryId, x.Status });
    }
}

public sealed class SharePointAttemptConfig : IEntityTypeConfiguration<SharePointAttempt>
{
    public void Configure(EntityTypeBuilder<SharePointAttempt> e)
    {
        e.ToTable("SharePointAttempts");
        e.Property(x => x.ErrorCode).HasMaxLength(40);
        e.Property(x => x.Message).HasMaxLength(2000);
        e.HasIndex(x => x.JobId);
    }
}
