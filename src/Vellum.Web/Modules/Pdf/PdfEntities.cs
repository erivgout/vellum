using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vellum.Domain;

namespace Vellum.Modules.Pdf;

/// <summary>
/// PDF rendition settings of one form (PLT-804..807, DOC-021..023, DOC-081): watermark style and automatic
/// DRAFT/SUPERSEDED marking (SAF-043), plus per-phase (<see cref="PdfPhaseSetting"/>) and per-field-and-phase
/// (<see cref="PdfFieldSetting"/>) rules.
/// </summary>
public class PdfProfile
{
    public int Id { get; set; }
    public string FormKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>SAF-043: mark non-effective revisions DRAFT and archived ones SUPERSEDED/OBSOLETE whenever a rendition is shown.</summary>
    public bool AutoMark { get; set; } = true;
    /// <summary>Watermark colour (#rrggbb) and opacity (5..100 %) (PLT-805).</summary>
    public string Color { get; set; } = "#b91c1c";
    public int Opacity { get; set; } = 18;
    /// <summary>DOC-043: offer a controlled copy (rendition + approval manifest) and the manifest PDF for approved revisions.</summary>
    public bool Manifest { get; set; } = true;
    /// <summary>Text printed above the signature table of the approval manifest (template, INT-409).</summary>
    public string? ManifestNote { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int? UpdatedById { get; set; }
    /// <summary>"default" when installed from a packaged default, "custom" once edited.</summary>
    public string Origin { get; set; } = "custom";
    public List<PdfPhaseSetting> Phases { get; set; } = [];
    public List<PdfFieldSetting> Fields { get; set; } = [];
}

public enum WatermarkPosition { Diagonal = 0, Header = 1, Footer = 2 }

/// <summary>Per form and phase: the rendition watermark (DOC-022) and viewer download / print permission (DOC-081).</summary>
public class PdfPhaseSetting
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string PhaseKey { get; set; } = "";
    /// <summary>Static text or tokens such as {number}, {revision}, {phase}, {effective_date} (DOC-022).</summary>
    public string? Watermark { get; set; }
    public WatermarkPosition Position { get; set; }
    /// <summary>Minimum application role that may download the rendition in this phase (None = nobody but managers).</summary>
    public AppRole DownloadRole { get; set; } = AppRole.Reader;
    public AppRole PrintRole { get; set; } = AppRole.Reader;
}

/// <summary>Per attachment field and phase: convert, require (INV-054) and PDF-only read mode (DOC-023, PLT-806).</summary>
public class PdfFieldSetting
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string FieldKey { get; set; } = "";
    public string PhaseKey { get; set; } = "";
    /// <summary>Convert current files on entering the phase and on save while in it (PLT-804, DOC-021).</summary>
    public bool Convert { get; set; }
    /// <summary>The record can't enter the phase without current renditions of this field (PLT-807, INV-054).</summary>
    public bool Require { get; set; }
    /// <summary>Readers see only the PDF renditions of this field in read mode (DOC-023, PLT-806).</summary>
    public bool PdfOnly { get; set; }
}

public enum PdfJobStatus { Queued = 0, Converting = 1, Done = 2, Failed = 3, Cancelled = 4 }

/// <summary>
/// One queued conversion of an original attachment into a rendition (NFR-005: asynchronous, progress visible).
/// Failures are retried with exponential backoff and end in <see cref="PdfJobStatus.Failed"/> (NFR-025, PLT-807).
/// </summary>
public class PdfJob
{
    public long Id { get; set; }
    public long AttachmentId { get; set; }
    public Guid RecordId { get; set; }
    public string FieldKey { get; set; } = "";
    public Guid? RowId { get; set; }
    public string FileName { get; set; } = "";
    public string SourceSha { get; set; } = "";
    /// <summary>Phase the rendition is produced for (its watermark).</summary>
    public string? PhaseKey { get; set; }
    /// <summary>The resolved watermark text stamped into the rendition.</summary>
    public string? Watermark { get; set; }
    /// <summary>phase | save | manual | retry | demo</summary>
    public string Trigger { get; set; } = "phase";
    public PdfJobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? Converter { get; set; }
    public string? LastError { get; set; }
    /// <summary>True when the failure can't be fixed by retrying (unsupported type, corrupt or protected file, too large).</summary>
    public bool Permanent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public int? RequestedById { get; set; }
    public long? RenditionId { get; set; }
}

/// <summary>
/// Conversion cache by source checksum: a file that hasn't changed is never converted again (PLT-804), only re-stamped.
/// The base PDF is stored content-addressed like every other file (INV-053).
/// </summary>
public class PdfConversion
{
    public string SourceSha { get; set; } = "";
    public string PdfSha { get; set; } = "";
    public long PdfSize { get; set; }
    public string Converter { get; set; } = "";
    public int Pages { get; set; }
    public int DurationMs { get; set; }
    public DateTime ConvertedAt { get; set; }
}

public sealed class PdfProfileConfig : IEntityTypeConfiguration<PdfProfile>
{
    public void Configure(EntityTypeBuilder<PdfProfile> b)
    {
        b.ToTable("PdfProfiles");
        b.Property(x => x.FormKey).HasMaxLength(100);
        b.HasIndex(x => x.FormKey).IsUnique();
        b.Property(x => x.Color).HasMaxLength(9);
        b.Property(x => x.ManifestNote).HasMaxLength(2000);
        b.Property(x => x.Origin).HasMaxLength(20);
        b.HasMany(x => x.Phases).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Fields).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PdfPhaseSettingConfig : IEntityTypeConfiguration<PdfPhaseSetting>
{
    public void Configure(EntityTypeBuilder<PdfPhaseSetting> b)
    {
        b.ToTable("PdfPhaseSettings");
        b.Property(x => x.PhaseKey).HasMaxLength(100);
        b.Property(x => x.Watermark).HasMaxLength(300);
        b.HasIndex(x => new { x.ProfileId, x.PhaseKey }).IsUnique();
    }
}

public sealed class PdfFieldSettingConfig : IEntityTypeConfiguration<PdfFieldSetting>
{
    public void Configure(EntityTypeBuilder<PdfFieldSetting> b)
    {
        b.ToTable("PdfFieldSettings");
        b.Property(x => x.FieldKey).HasMaxLength(100);
        b.Property(x => x.PhaseKey).HasMaxLength(100);
        b.HasIndex(x => new { x.ProfileId, x.FieldKey, x.PhaseKey }).IsUnique();
    }
}

public sealed class PdfJobConfig : IEntityTypeConfiguration<PdfJob>
{
    public void Configure(EntityTypeBuilder<PdfJob> b)
    {
        b.ToTable("PdfJobs");
        b.Property(x => x.FieldKey).HasMaxLength(100);
        b.Property(x => x.FileName).HasMaxLength(400);
        b.Property(x => x.SourceSha).HasMaxLength(64);
        b.Property(x => x.PhaseKey).HasMaxLength(100);
        b.Property(x => x.Watermark).HasMaxLength(400);
        b.Property(x => x.Trigger).HasMaxLength(20);
        b.Property(x => x.Converter).HasMaxLength(40);
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        b.HasIndex(x => x.AttachmentId);
        b.HasIndex(x => x.RecordId);
    }
}

public sealed class PdfConversionConfig : IEntityTypeConfiguration<PdfConversion>
{
    public void Configure(EntityTypeBuilder<PdfConversion> b)
    {
        b.ToTable("PdfConversions");
        b.HasKey(x => x.SourceSha);
        b.Property(x => x.SourceSha).HasMaxLength(64);
        b.Property(x => x.PdfSha).HasMaxLength(64);
        b.Property(x => x.Converter).HasMaxLength(40);
    }
}
