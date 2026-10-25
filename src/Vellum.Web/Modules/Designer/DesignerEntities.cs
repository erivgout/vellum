using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vellum.Modules.Designer;

/// <summary>
/// DSN-003: an application's unpublished configuration. Edits change only this document; the live configuration tables
/// change when the draft is published. One draft per application.
/// </summary>
public class ConfigDraft
{
    public int Id { get; set; }
    public string AppKey { get; set; } = "";
    /// <summary>The application doesn't exist yet (created by "New application").</summary>
    public bool IsNewApp { get; set; }
    /// <summary>The <see cref="AppDoc"/> as JSON.</summary>
    public string Json { get; set; } = "";
    /// <summary>Hash of the live application document the draft started from (detects concurrent publishes).</summary>
    public string BaseHash { get; set; } = "";
    public int BaseVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public int UpdatedById { get; set; }
    public string UpdatedByName { get; set; } = "";
    /// <summary>Optimistic concurrency: every save increments it.</summary>
    public int Revision { get; set; }
}

/// <summary>DSN-004: a component (settings, form + its workflows, view) locked to one designer while edited.</summary>
public class DesignerLock
{
    /// <summary>"{appKey}|{component}".</summary>
    public string Key { get; set; } = "";
    public string AppKey { get; set; } = "";
    public string Component { get; set; } = "";
    public string Label { get; set; } = "";
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public DateTime AcquiredAt { get; set; }
    public DateTime LastActivityAt { get; set; }
}

/// <summary>DSN-1203: what one publish changed. Keeps a snapshot of the published application so versions can be restored.</summary>
public class ConfigPublication
{
    public int Id { get; set; }
    public int VersionNumber { get; set; }
    public string AppKey { get; set; } = "";
    public string AppName { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public int? PublishedById { get; set; }
    public string PublishedByName { get; set; } = "";
    public string? Note { get; set; }
    public int Changes { get; set; }
    /// <summary>JSON array of the warnings the publisher accepted (DSN-1201).</summary>
    public string? AcceptedWarnings { get; set; }
    /// <summary>JSON array of the impact analysis shown before publishing (SAF-071).</summary>
    public string? Impact { get; set; }
    /// <summary>The published <see cref="AppDoc"/>.</summary>
    public string Snapshot { get; set; } = "";
    /// <summary>Publication restored from an earlier version (rollback is itself a new version).</summary>
    public int? RestoredFromVersion { get; set; }
}

public sealed class ConfigDraftConfiguration : IEntityTypeConfiguration<ConfigDraft>
{
    public void Configure(EntityTypeBuilder<ConfigDraft> e)
    {
        e.ToTable("DesignerDrafts");
        e.HasIndex(x => x.AppKey).IsUnique();
        e.Property(x => x.AppKey).HasMaxLength(64);
        e.Property(x => x.Json).HasMaxLength(-1);
        e.Property(x => x.BaseHash).HasMaxLength(64);
        e.Property(x => x.Revision).IsConcurrencyToken();
    }
}

public sealed class DesignerLockConfiguration : IEntityTypeConfiguration<DesignerLock>
{
    public void Configure(EntityTypeBuilder<DesignerLock> e)
    {
        e.ToTable("DesignerLocks");
        e.HasKey(x => x.Key);
        e.Property(x => x.Key).HasMaxLength(200);
        e.Property(x => x.AppKey).HasMaxLength(64);
        e.HasIndex(x => x.AppKey);
    }
}

public sealed class ConfigPublicationConfiguration : IEntityTypeConfiguration<ConfigPublication>
{
    public void Configure(EntityTypeBuilder<ConfigPublication> e)
    {
        e.ToTable("DesignerPublications");
        e.HasIndex(x => x.VersionNumber);
        e.HasIndex(x => x.AppKey);
        e.Property(x => x.AppKey).HasMaxLength(64);
        e.Property(x => x.Note).HasMaxLength(4000);
        e.Property(x => x.AcceptedWarnings).HasMaxLength(-1);
        e.Property(x => x.Impact).HasMaxLength(-1);
        e.Property(x => x.Snapshot).HasMaxLength(-1);
    }
}
