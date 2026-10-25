namespace Vellum.Domain;

/// <summary>PLT §2.2. An instance of exactly one form and (for workflow forms) one workflow (INV-001).</summary>
public class Record
{
    public Guid Id { get; set; }
    public int AppId { get; set; }
    public AppDefinition App { get; set; } = null!;
    public int FormId { get; set; }
    public FormDefinition Form { get; set; } = null!;
    public int? WorkflowId { get; set; }
    public WorkflowDefinition? Workflow { get; set; }
    public int? PhaseId { get; set; }
    public PhaseDefinition? Phase { get; set; }
    /// <summary>Denormalized from the phase for fast view filtering.</summary>
    public PhaseType PhaseType { get; set; }

    public string Number { get; set; } = "";
    public long Sequence { get; set; }
    public string Title { get; set; } = "";
    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTime ModifiedAt { get; set; }
    public int ModifiedById { get; set; }
    public User ModifiedBy { get; set; } = null!;
    /// <summary>Due date of the current Normal phase (INV-074). Null in terminal phases (INV-032).</summary>
    public DateOnly? DueDate { get; set; }
    /// <summary>Activity parent (PLT-205).</summary>
    public Guid? ParentId { get; set; }
    public Record? Parent { get; set; }

    // Revision control (DOC §10.2, INV-100..102)
    public Guid? FamilyId { get; set; }
    public string? Revision { get; set; }
    public int RevisionOrdinal { get; set; }
    public bool IsEffective { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public bool IsArchived { get; set; }
    /// <summary>DOC-104: "superseded" (automatic) or "obsolete" (manual).</summary>
    public string? ArchiveReason { get; set; }
    public DateTime? ArchivedAt { get; set; }

    /// <summary>INV-146: configuration version current at the last phase transition.</summary>
    public int ConfigVersion { get; set; }
    public bool LegalHold { get; set; }
    /// <summary>Optimistic concurrency (PLT-905, INV-092).</summary>
    public int Version { get; set; }

    public List<RecordValue> Values { get; set; } = [];
    public List<Assignment> Assignments { get; set; } = [];
}

/// <summary>
/// One stored field value (typed columns, one row per value of a multi-value field).
/// Numbers use exact decimal storage (INV-062); keywords are stored by stable ID (INV-061).
/// </summary>
public class RecordValue
{
    public long Id { get; set; }
    public Guid RecordId { get; set; }
    /// <summary>Subform row, when the field belongs to a subform.</summary>
    public Guid? RowId { get; set; }
    public string FieldKey { get; set; } = "";
    public int Index { get; set; }
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public DateTime? Date { get; set; }
    /// <summary>Keyword ID, user ID or boolean (1/0).</summary>
    public int? RefId { get; set; }
    /// <summary>Linked record ID.</summary>
    public Guid? RefGuid { get; set; }
}

public class SubformRow
{
    public Guid Id { get; set; }
    public Guid RecordId { get; set; }
    public string SectionKey { get; set; } = "";
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Activity created from this row (QE-002), if any.</summary>
    public Guid? CreatedRecordId { get; set; }
}

/// <summary>PLT-1004: phase tracking.</summary>
public class PhaseEntry
{
    public long Id { get; set; }
    public Guid RecordId { get; set; }
    public int PhaseId { get; set; }
    public PhaseDefinition Phase { get; set; } = null!;
    public string PhaseName { get; set; } = "";
    public DateTime EnteredAt { get; set; }
    public int? EnteredById { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTime? ExitedAt { get; set; }
    public int? ExitedById { get; set; }
    /// <summary>forward | back | void | special | resend | reassign | escalated | system | auto</summary>
    public string? ExitAction { get; set; }
    public string? Comment { get; set; }
    public int ConfigVersion { get; set; }
}

public class Assignment
{
    public long Id { get; set; }
    public Guid RecordId { get; set; }
    public Record Record { get; set; } = null!;
    public long PhaseEntryId { get; set; }
    public PhaseEntry PhaseEntry { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int Order { get; set; }
    public AssignmentStatus Status { get; set; }
    public DateTime AssignedAt { get; set; }
    public int? AssignedById { get; set; }
    public DateTime? CompletedAt { get; set; }
    public VoteDecision Vote { get; set; }
    public string? VoteComment { get; set; }
    public DateTime? LastReminderAt { get; set; }
}

/// <summary>PLT-303.</summary>
public class Comment
{
    public long Id { get; set; }
    public Guid RecordId { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public string Text { get; set; } = "";
    public bool IsSystem { get; set; }
    /// <summary>route | signature | review | restore | system | user</summary>
    public string Kind { get; set; } = "user";
    public DateTime At { get; set; }
}

/// <summary>Content-addressed file content (PLT-803, INV-053).</summary>
public class FileBlob
{
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime StoredAt { get; set; }
}

public class Attachment
{
    public long Id { get; set; }
    public Guid RecordId { get; set; }
    public Guid? RowId { get; set; }
    public string FieldKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public int UploadedById { get; set; }
    public User UploadedBy { get; set; } = null!;
    public DateTime UploadedAt { get; set; }
    public bool Removed { get; set; }
    public DateTime? RemovedAt { get; set; }
    public int? RemovedById { get; set; }
    /// <summary>Derived copy (e.g. PDF rendition) of another attachment (SAF-063).</summary>
    public long? RenditionOfId { get; set; }
}

public class RecordLink
{
    public long Id { get; set; }
    public Guid FromId { get; set; }
    public Record From { get; set; } = null!;
    public Guid ToId { get; set; }
    public Record To { get; set; } = null!;
    public string? FieldKey { get; set; }
    public LinkKind Kind { get; set; }
    public DateTime At { get; set; }
    public int ById { get; set; }
}

/// <summary>PLT-901 / INV-091: at most one edit lock per record (primary key).</summary>
public class RecordLock
{
    public Guid RecordId { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime AcquiredAt { get; set; }
    public DateTime LastActivityAt { get; set; }
}

/// <summary>SEC-507.</summary>
public class RecordAccess
{
    public long Id { get; set; }
    public Guid RecordId { get; set; }
    public PrincipalKind PrincipalKind { get; set; }
    public int PrincipalId { get; set; }
    public RecordAccessLevel Level { get; set; }
}

/// <summary>SEC-603/604, INV-052/114. Immutable apart from the superseded flag (DB trigger).</summary>
public class Signature
{
    public long Id { get; set; }
    public Guid RecordId { get; set; }
    public int SignerId { get; set; }
    public string SignerName { get; set; } = "";
    public string SignerUsername { get; set; } = "";
    public int? OnBehalfOfId { get; set; }
    public string? OnBehalfOfName { get; set; }
    public string Meaning { get; set; } = "";
    public string PhaseKey { get; set; } = "";
    public string PhaseName { get; set; } = "";
    public int PhaseOrder { get; set; }
    /// <summary>What was signed, e.g. "Send to Final Approval".</summary>
    public string Action { get; set; } = "";
    public DateTime SignedAt { get; set; }
    /// <summary>SHA-256 of the record content at signing (INV-114).</summary>
    public string RecordHash { get; set; } = "";
    public int RecordVersion { get; set; }
    public string? Comment { get; set; }
    public bool Superseded { get; set; }
    public DateTime? SupersededAt { get; set; }
}

/// <summary>PLT-1001, INV-050/055: append-only, hash-chained per record. DB triggers block UPDATE and DELETE.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    /// <summary>Null for system-wide (non-record) events, which form their own chain.</summary>
    public Guid? RecordId { get; set; }
    public long Seq { get; set; }
    public DateTime At { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public int? OnBehalfOfId { get; set; }
    public string? OnBehalfOfName { get; set; }
    public ActorKind ActorKind { get; set; }
    /// <summary>UI | API | Import | System | Script</summary>
    public string Source { get; set; } = "UI";
    public string Action { get; set; } = "";
    public string? FieldKey { get; set; }
    public string? FieldLabel { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Detail { get; set; }
    public string? Reason { get; set; }
    public string? Ip { get; set; }
    public string? PhaseName { get; set; }
    public int? RecordVersion { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";

    private static long _clientOrder;
    /// <summary>Creation order within a unit of work, so the chain follows the order entries were appended. Not stored.</summary>
    public long ClientOrder { get; } = Interlocked.Increment(ref _clientOrder);
}

public class Notification
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public Guid? RecordId { get; set; }
    public NotificationKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    /// <summary>SAF-020: safety-critical alerts need acknowledgment.</summary>
    public DateTime? AcknowledgedAt { get; set; }
}

/// <summary>PLT-1005 / ADM-1002.</summary>
public class EmailLogEntry
{
    public long Id { get; set; }
    public Guid? RecordId { get; set; }
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string? Body { get; set; }
    public DateTime At { get; set; }
    public string Status { get; set; } = "Queued";
}

/// <summary>INV-040 / SAF-031: automation recorded in the same commit, executed at least once.</summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public Guid? RecordId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

/// <summary>ADM-1003.</summary>
public class DeletionHistory
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string AppName { get; set; } = "";
    public string FormName { get; set; } = "";
    public string Number { get; set; } = "";
    public string? Revision { get; set; }
    public string Snapshot { get; set; } = "";
    public string Reason { get; set; } = "";
}

public class Favorite
{
    public long Id { get; set; }
    public int UserId { get; set; }
    /// <summary>app | view | record</summary>
    public string Kind { get; set; } = "";
    public string RefKey { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime At { get; set; }
}

public class NumberSequence
{
    public string Scope { get; set; } = "";
    public long Next { get; set; }
}
