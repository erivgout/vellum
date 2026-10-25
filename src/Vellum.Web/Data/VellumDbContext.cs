using Microsoft.EntityFrameworkCore;
using Vellum.Domain;

namespace Vellum.Data;

public class VellumDbContext(DbContextOptions<VellumDbContext> options) : DbContext(options)
{
    // Configuration metamodel
    public DbSet<AppDefinition> Apps => Set<AppDefinition>();
    public DbSet<FormDefinition> Forms => Set<FormDefinition>();
    public DbSet<SectionDefinition> Sections => Set<SectionDefinition>();
    public DbSet<FieldDefinition> Fields => Set<FieldDefinition>();
    public DbSet<WorkflowDefinition> Workflows => Set<WorkflowDefinition>();
    public DbSet<PhaseDefinition> Phases => Set<PhaseDefinition>();
    public DbSet<RouteDefinition> Routes => Set<RouteDefinition>();
    public DbSet<ViewDefinition> Views => Set<ViewDefinition>();
    public DbSet<KeywordList> KeywordLists => Set<KeywordList>();
    public DbSet<Keyword> Keywords => Set<Keyword>();
    public DbSet<ConfigVersion> ConfigVersions => Set<ConfigVersion>();
    public DbSet<ConfigAuditEntry> ConfigAudit => Set<ConfigAuditEntry>();

    // Identity and access
    public DbSet<User> Users => Set<User>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<AppRoleGrant> Grants => Set<AppRoleGrant>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<PasswordHistory> PasswordHistory => Set<PasswordHistory>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<Delegation> Delegations => Set<Delegation>();

    // Records
    public DbSet<Record> Records => Set<Record>();
    public DbSet<RecordValue> RecordValues => Set<RecordValue>();
    public DbSet<SubformRow> SubformRows => Set<SubformRow>();
    public DbSet<PhaseEntry> PhaseEntries => Set<PhaseEntry>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<FileBlob> FileBlobs => Set<FileBlob>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<RecordLink> Links => Set<RecordLink>();
    public DbSet<RecordLock> Locks => Set<RecordLock>();
    public DbSet<RecordAccess> RecordAccess => Set<RecordAccess>();
    public DbSet<Signature> Signatures => Set<Signature>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<EmailLogEntry> EmailLog => Set<EmailLogEntry>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<DeletionHistory> DeletionHistory => Set<DeletionHistory>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();

    // System
    public DbSet<SystemSettings> Settings => Set<SystemSettings>();
    public DbSet<CalendarDay> CalendarDays => Set<CalendarDay>();
    public DbSet<TaskProfile> Tasks => Set<TaskProfile>();
    public DbSet<TaskRun> TaskRuns => Set<TaskRun>();

    /// <summary>
    /// INV-050/055: audit entries added in this unit of work are chained (Seq, PrevHash, Hash) per record inside the
    /// same transaction as the change they describe. The last link is read under UPDLOCK/HOLDLOCK so concurrent
    /// appends serialize; the unique (RecordId, Seq) index turns any remaining race into a failed commit, never a fork.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        var pending = ChangeTracker.Entries<AuditEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (pending.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

        var ownTx = Database.CurrentTransaction == null ? await Database.BeginTransactionAsync(ct) : null;
        try
        {
            foreach (var chain in pending.GroupBy(a => a.RecordId))
            {
                var last = chain.Key is { } rid
                    ? await Database.SqlQueryRaw<ChainLink>(
                        "SELECT TOP 1 [Seq], [Hash], [At] FROM [AuditEntries] WITH (UPDLOCK, HOLDLOCK) WHERE [RecordId] = {0} ORDER BY [Seq] DESC", rid)
                        .ToListAsync(ct)
                    : await Database.SqlQueryRaw<ChainLink>(
                        "SELECT TOP 1 [Seq], [Hash], [At] FROM [AuditEntries] WITH (UPDLOCK, HOLDLOCK) WHERE [RecordId] IS NULL ORDER BY [Seq] DESC")
                        .ToListAsync(ct);
                var seq = last.Count > 0 ? last[0].Seq : 0;
                var prev = last.Count > 0 ? last[0].Hash : AuditHashing.Genesis;
                var floor = last.Count > 0 ? last[0].At : DateTime.MinValue;
                foreach (var entry in chain.OrderBy(a => a.ClientOrder))
                {
                    if (entry.At < floor) entry.At = floor; // timestamps never decrease
                    floor = entry.At;
                    entry.Seq = ++seq;
                    entry.PrevHash = prev;
                    entry.Hash = AuditHashing.Compute(entry);
                    prev = entry.Hash;
                }
            }
            var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
            if (ownTx != null) await ownTx.CommitAsync(ct);
            return n;
        }
        catch
        {
            if (ownTx != null) await ownTx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    private sealed class ChainLink
    {
        public long Seq { get; set; }
        public string Hash { get; set; } = "";
        public DateTime At { get; set; }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<decimal>().HavePrecision(38, 10);
        b.Properties<string>().HaveMaxLength(400);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---- Configuration ----
        b.Entity<AppDefinition>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasMany(x => x.Forms).WithOne(x => x.App).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Views).WithOne(x => x.App).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Grants).WithOne(x => x.App).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<FormDefinition>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasMany(x => x.Sections).WithOne(x => x.Form).HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Fields).WithOne(x => x.Form).HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Workflows).WithOne(x => x.Form).HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SectionDefinition>(e =>
        {
            e.HasIndex(x => new { x.FormId, x.Key }).IsUnique();
            e.Property(x => x.Description).HasMaxLength(2000);
        });
        b.Entity<FieldDefinition>(e =>
        {
            e.HasIndex(x => new { x.FormId, x.Key }).IsUnique();
            e.Property(x => x.Help).HasMaxLength(2000);
        });
        b.Entity<WorkflowDefinition>(e =>
        {
            e.HasIndex(x => new { x.FormId, x.Key }).IsUnique();
            e.HasMany(x => x.Phases).WithOne(x => x.Workflow).HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Routes).WithOne(x => x.Workflow).HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PhaseDefinition>(e =>
        {
            e.HasIndex(x => new { x.WorkflowId, x.Key }).IsUnique();
            e.Property(x => x.Purpose).HasMaxLength(2000);
        });
        b.Entity<RouteDefinition>(e =>
        {
            e.HasOne(x => x.FromPhase).WithMany().HasForeignKey(x => x.FromPhaseId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.ToPhase).WithMany().HasForeignKey(x => x.ToPhaseId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<ViewDefinition>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.Key }).IsUnique();
            e.Property(x => x.FilterJson).HasMaxLength(4000);
        });
        b.Entity<KeywordList>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.HasMany(x => x.Keywords).WithOne(x => x.List).HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Keyword>(e => e.HasIndex(x => new { x.ListId, x.Key }).IsUnique());
        b.Entity<ConfigVersion>(e => e.HasIndex(x => x.Number).IsUnique());
        b.Entity<ConfigAuditEntry>(e =>
        {
            e.ToTable("ConfigAuditEntries", t => t.HasTrigger("trg_ConfigAudit_AppendOnly"));
            e.Property(x => x.Before).HasMaxLength(-1);
            e.Property(x => x.After).HasMaxLength(-1);
            e.HasIndex(x => x.At);
        });

        // ---- Identity ----
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.ReportsTo).WithMany().HasForeignKey(x => x.ReportsToId);
            e.HasOne(x => x.PrimaryLocation).WithMany().HasForeignKey(x => x.PrimaryLocationId);
            e.Ignore(x => x.Initials);
        });
        b.Entity<Group>(e => e.HasIndex(x => x.Key).IsUnique());
        b.Entity<GroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.UserId });
            e.HasOne(x => x.Group).WithMany(x => x.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(x => x.Groups).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Location>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
        });
        b.Entity<AppRoleGrant>(e => e.HasIndex(x => new { x.AppId, x.PrincipalKind, x.PrincipalId }));
        b.Entity<UserSession>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(64);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<PasswordHistory>(e => e.HasIndex(x => x.UserId));
        b.Entity<SecurityEvent>(e =>
        {
            e.HasIndex(x => x.At);
            e.Property(x => x.Detail).HasMaxLength(2000);
        });
        b.Entity<Delegation>(e =>
        {
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId);
            e.HasOne(x => x.Delegate).WithMany().HasForeignKey(x => x.DelegateId);
        });

        // ---- Records ----
        b.Entity<Record>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Title).HasMaxLength(1000);
            // INV-020: number never reused within an application (revisions share the number, differ by ordinal).
            e.HasIndex(x => new { x.AppId, x.Number, x.RevisionOrdinal }).IsUnique();
            // INV-100: at most one effective revision per document family.
            e.HasIndex(x => x.FamilyId).IsUnique().HasFilter("[IsEffective] = 1 AND [FamilyId] IS NOT NULL").HasDatabaseName("UX_Records_Family_Effective");
            e.HasIndex(x => new { x.FormId, x.PhaseType });
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.DueDate);
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId);
            e.HasOne(x => x.Form).WithMany().HasForeignKey(x => x.FormId);
            e.HasOne(x => x.Workflow).WithMany().HasForeignKey(x => x.WorkflowId);
            e.HasOne(x => x.Phase).WithMany().HasForeignKey(x => x.PhaseId);
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
            e.HasOne(x => x.ModifiedBy).WithMany().HasForeignKey(x => x.ModifiedById);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
            e.HasMany(x => x.Values).WithOne().HasForeignKey(x => x.RecordId);
            e.HasMany(x => x.Assignments).WithOne(x => x.Record).HasForeignKey(x => x.RecordId);
        });
        b.Entity<RecordValue>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(-1);
            e.HasIndex(x => new { x.RecordId, x.FieldKey });
            e.HasIndex(x => new { x.FieldKey, x.RefId });
            e.HasIndex(x => x.RefGuid);
        });
        b.Entity<SubformRow>(e => e.HasIndex(x => x.RecordId));
        b.Entity<PhaseEntry>(e =>
        {
            e.HasIndex(x => x.RecordId);
            e.Property(x => x.Comment).HasMaxLength(4000);
            e.HasOne(x => x.Phase).WithMany().HasForeignKey(x => x.PhaseId);
        });
        b.Entity<Assignment>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasIndex(x => x.RecordId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasOne(x => x.PhaseEntry).WithMany().HasForeignKey(x => x.PhaseEntryId);
        });
        b.Entity<Comment>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(-1);
            e.HasIndex(x => x.RecordId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });
        b.Entity<FileBlob>(e =>
        {
            e.HasKey(x => x.Sha256);
            e.Property(x => x.Sha256).HasMaxLength(64);
        });
        b.Entity<Attachment>(e =>
        {
            e.HasIndex(x => new { x.RecordId, x.FieldKey });
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasOne(x => x.UploadedBy).WithMany().HasForeignKey(x => x.UploadedById);
        });
        b.Entity<RecordLink>(e =>
        {
            e.HasIndex(x => x.FromId);
            e.HasIndex(x => x.ToId);
            e.HasOne(x => x.From).WithMany().HasForeignKey(x => x.FromId);
            e.HasOne(x => x.To).WithMany().HasForeignKey(x => x.ToId);
        });
        b.Entity<RecordLock>(e =>
        {
            e.HasKey(x => x.RecordId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });
        b.Entity<RecordAccess>(e => e.HasIndex(x => x.RecordId));
        b.Entity<Signature>(e =>
        {
            e.ToTable("Signatures", t => t.HasTrigger("trg_Signatures_Immutable"));
            e.HasIndex(x => x.RecordId);
            e.Property(x => x.RecordHash).HasMaxLength(64);
            e.Property(x => x.Comment).HasMaxLength(4000);
        });
        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntries", t => t.HasTrigger("trg_Audit_AppendOnly"));
            e.Ignore(x => x.ClientOrder);
            // INV-055: one chain per record; a race produces a unique violation, never a forked chain.
            e.HasIndex(x => new { x.RecordId, x.Seq }).IsUnique().HasFilter("[RecordId] IS NOT NULL");
            e.HasIndex(x => x.Seq).IsUnique().HasFilter("[RecordId] IS NULL").HasDatabaseName("UX_Audit_GlobalSeq");
            e.HasIndex(x => x.At);
            e.Property(x => x.OldValue).HasMaxLength(-1);
            e.Property(x => x.NewValue).HasMaxLength(-1);
            e.Property(x => x.Detail).HasMaxLength(-1);
            e.Property(x => x.Reason).HasMaxLength(4000);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.PrevHash).HasMaxLength(64);
        });
        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ReadAt });
            e.Property(x => x.Body).HasMaxLength(4000);
        });
        b.Entity<EmailLogEntry>(e =>
        {
            e.HasIndex(x => x.RecordId);
            e.Property(x => x.Body).HasMaxLength(-1);
        });
        b.Entity<OutboxMessage>(e =>
        {
            e.HasIndex(x => x.ProcessedAt);
            e.Property(x => x.Payload).HasMaxLength(-1);
            e.Property(x => x.Error).HasMaxLength(4000);
        });
        b.Entity<DeletionHistory>(e => e.Property(x => x.Snapshot).HasMaxLength(-1));
        b.Entity<Favorite>(e => e.HasIndex(x => new { x.UserId, x.Kind, x.RefKey }).IsUnique());
        b.Entity<NumberSequence>(e => e.HasKey(x => x.Scope));

        // ---- System ----
        b.Entity<SystemSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ESignatureCertification).HasMaxLength(4000);
            e.Property(x => x.LoginMessage).HasMaxLength(4000);
        });
        b.Entity<CalendarDay>(e => e.HasIndex(x => x.Date).IsUnique());
        b.Entity<TaskProfile>(e => e.HasIndex(x => x.Key).IsUnique());
        b.Entity<TaskRun>(e =>
        {
            e.HasIndex(x => x.TaskId);
            e.Property(x => x.Log).HasMaxLength(-1);
        });

        // Modules contribute their own entities through IEntityTypeConfiguration<T> classes.
        b.ApplyConfigurationsFromAssembly(typeof(VellumDbContext).Assembly);

        // Records and their history are never cascaded away (INV-010). Only configuration children cascade.
        var cascadeAllowed = new HashSet<Type>
        {
            typeof(FormDefinition), typeof(SectionDefinition), typeof(FieldDefinition), typeof(WorkflowDefinition),
            typeof(PhaseDefinition), typeof(RouteDefinition), typeof(ViewDefinition), typeof(Keyword),
            typeof(AppRoleGrant), typeof(GroupMember),
        };
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            if (fk.DeleteBehavior == DeleteBehavior.Cascade && !cascadeAllowed.Contains(fk.DeclaringEntityType.ClrType))
                fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}
