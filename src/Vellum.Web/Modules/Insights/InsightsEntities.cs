using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vellum.Data;

namespace Vellum.Modules.Insights;

/// <summary>
/// RPT-101: a chart template (shared, reusable definition) or a chart document (an instance a user owns).
/// The definition itself is <see cref="ChartDefinition"/>, stored as JSON.
/// </summary>
public class InsightsChart
{
    public int Id { get; set; }
    /// <summary>Unique number assigned on first save (CHT-00001 for documents, CTT-00001 for templates).</summary>
    public string Number { get; set; } = "";
    public bool IsTemplate { get; set; }
    /// <summary>The template a document was created from.</summary>
    public int? TemplateId { get; set; }
    public int OwnerId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string AppKey { get; set; } = "";
    public string ViewKey { get; set; } = "";
    public string Type { get; set; } = "column";
    public string DefinitionJson { get; set; } = "{}";
    /// <summary>RPT-101: location security, defaulting to the creator's location. Null = every location.</summary>
    public int? LocationId { get; set; }
    /// <summary>RPT-101: templates can be disabled.</summary>
    public bool Disabled { get; set; }
    /// <summary>RPT-405: pinned to the owner's homepage.</summary>
    public bool Pinned { get; set; }
    /// <summary>chart | table | both</summary>
    public string PinMode { get; set; } = "chart";
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}

/// <summary>RPT-601: a threshold alert watching a view.</summary>
public class InsightsAlertTemplate
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int OwnerId { get; set; }
    public string AppKey { get; set; } = "";
    public string ViewKey { get; set; } = "";
    /// <summary>JSON array of column keys forming the grouping key (e.g. ["source"]).</summary>
    public string MatchColumnsJson { get; set; } = "[]";
    /// <summary>JSON array of extra report columns (in order).</summary>
    public string ReportColumnsJson { get; set; } = "[]";
    /// <summary>Applied per distinct combination of match-column values.</summary>
    public int MinMatches { get; set; } = 2;
    /// <summary>Only records created within the last N days are counted (blank = every record in the view).</summary>
    public int? LookbackDays { get; set; }
    /// <summary>The template only runs while today is inside [WindowStart, WindowEnd]. Blank ends are open.</summary>
    public DateOnly? WindowStart { get; set; }
    public DateOnly? WindowEnd { get; set; }
    /// <summary>Slide the window forward by the frequency on each run.</summary>
    public bool IncrementDates { get; set; }
    public int FrequencyDays { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool OnlyWhenChanged { get; set; }
    /// <summary>notify (create-record is not offered in this release).</summary>
    public string Action { get; set; } = "notify";
    /// <summary>JSON: { "users": [ids], "groups": [keys] }.</summary>
    public string RecipientsJson { get; set; } = "{}";
    /// <summary>JSON: { "users": [ids] }.</summary>
    public string CcJson { get; set; } = "{}";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime? LastRunAt { get; set; }
    public DateTime? LastReportAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}

/// <summary>RPT-604: the outcome of one alert run, with its origin and the matched records.</summary>
public class InsightsAlertReport
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public int TemplateId { get; set; }
    public DateTime GeneratedAt { get; set; }
    /// <summary>RPT-605: produced by "Test report" (no notifications are sent).</summary>
    public bool IsTest { get; set; }
    public bool Manual { get; set; }
    /// <summary>JSON snapshot of the template at run time: application, forms, view, match columns, minimum, frequency, window.</summary>
    public string OriginJson { get; set; } = "{}";
    /// <summary>Machine-readable result (groups → matched records), also offered as a JSON download.</summary>
    public string ResultJson { get; set; } = "{}";
    public int GroupCount { get; set; }
    public int RecordCount { get; set; }
    /// <summary>Notified | Failed | NoMatches | Unchanged | Test</summary>
    public string ActionStatus { get; set; } = "";
    public string? ActionDetail { get; set; }
    public string ResultHash { get; set; } = "";
    /// <summary>Users notified (JSON array of ids): they may open the report.</summary>
    public string NotifiedJson { get; set; } = "[]";
}

public sealed class InsightsChartConfig : IEntityTypeConfiguration<InsightsChart>
{
    public void Configure(EntityTypeBuilder<InsightsChart> e)
    {
        e.ToTable("InsightsCharts");
        e.HasIndex(x => x.Number).IsUnique();
        e.HasIndex(x => new { x.OwnerId, x.IsTemplate });
        e.Property(x => x.Description).HasMaxLength(2000);
        e.Property(x => x.DefinitionJson).HasMaxLength(-1);
    }
}

public sealed class InsightsAlertTemplateConfig : IEntityTypeConfiguration<InsightsAlertTemplate>
{
    public void Configure(EntityTypeBuilder<InsightsAlertTemplate> e)
    {
        e.ToTable("InsightsAlertTemplates");
        e.HasIndex(x => x.Number).IsUnique();
        e.Property(x => x.Description).HasMaxLength(2000);
        e.Property(x => x.Body).HasMaxLength(4000);
        e.Property(x => x.MatchColumnsJson).HasMaxLength(2000);
        e.Property(x => x.ReportColumnsJson).HasMaxLength(2000);
        e.Property(x => x.RecipientsJson).HasMaxLength(4000);
        e.Property(x => x.CcJson).HasMaxLength(4000);
    }
}

public sealed class InsightsAlertReportConfig : IEntityTypeConfiguration<InsightsAlertReport>
{
    public void Configure(EntityTypeBuilder<InsightsAlertReport> e)
    {
        e.ToTable("InsightsAlertReports");
        e.HasIndex(x => x.Number).IsUnique();
        e.HasIndex(x => new { x.TemplateId, x.GeneratedAt });
        e.Property(x => x.OriginJson).HasMaxLength(-1);
        e.Property(x => x.ResultJson).HasMaxLength(-1);
        e.Property(x => x.ActionDetail).HasMaxLength(4000);
        e.Property(x => x.NotifiedJson).HasMaxLength(4000);
    }
}

/// <summary>
/// The platform creates the schema with EnsureCreated, which adds nothing to a database that already exists.
/// So that Insights also works on a database created before this module was installed, its own tables are
/// created on first use from the EF model's own create script (only the Insights statements).
/// </summary>
public static class InsightsSchema
{
    private static volatile bool _ready;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] Tables = ["InsightsCharts", "InsightsAlertTemplates", "InsightsAlertReports"];

    public static async Task EnsureAsync(VellumDbContext db)
    {
        if (_ready) return;
        await Gate.WaitAsync();
        try
        {
            if (_ready) return;
            var missing = new List<string>();
            foreach (var t in Tables)
            {
                var exists = await db.Database.SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID({0}, 'U') IS NULL THEN 0 ELSE 1 END AS [Value]", t).ToListAsync();
                if (exists.Single() == 0) missing.Add(t);
            }
            if (missing.Count > 0)
            {
                var script = db.Database.GenerateCreateScript();
                var batches = script.Split(["\nGO", "\r\nGO"], StringSplitOptions.RemoveEmptyEntries)
                    .Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
                // Tables first, then their indexes.
                foreach (var t in missing)
                    foreach (var b in batches.Where(b => b.StartsWith($"CREATE TABLE [{t}]", StringComparison.Ordinal)))
                        await db.Database.ExecuteSqlRawAsync(b);
                foreach (var t in missing)
                    foreach (var b in batches.Where(b => b.StartsWith("CREATE ", StringComparison.Ordinal) && b.Contains($" ON [{t}]", StringComparison.Ordinal)))
                        await db.Database.ExecuteSqlRawAsync(b);
            }
            _ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
