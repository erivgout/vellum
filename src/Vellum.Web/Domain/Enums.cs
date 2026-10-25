namespace Vellum.Domain;

/// <summary>PLT-601: only Normal phases carry assignees and due dates; every other type is terminal and read-only.</summary>
public enum PhaseType { Normal = 0, Completed = 1, Voided = 2, Archived = 3, Obsolete = 4 }

public enum RouteKind { Forward = 0, Back = 1, SpecialForward = 2, Void = 3 }

/// <summary>PLT-610..612.</summary>
public enum RoutingType { Parallel = 0, Sequential = 1, Voting = 2 }

/// <summary>PLT-620 (simplified): who a phase is assigned to on entry.</summary>
public enum AssignmentMode
{
    /// <summary>Any active user, picked by the router. Defaults come from <c>AssigneeField</c> when set.</summary>
    Any = 0,
    /// <summary>Forced from a names field on the record.</summary>
    Field = 1,
    /// <summary>Members of a group (all of them, or a pick among them).</summary>
    Group = 2,
    /// <summary>The record's creator.</summary>
    Creator = 3,
}

public enum FieldType
{
    Text = 0,
    TextArea = 1,
    Number = 2,
    Currency = 3,
    Date = 4,
    DateTime = 5,
    Keyword = 6,
    KeywordMulti = 7,
    YesNo = 8,
    User = 9,
    UserMulti = 10,
    Link = 11,
    Url = 12,
    Attachment = 13,
    /// <summary>Static text shown in the layout. Stores nothing.</summary>
    Prompt = 14,
}

/// <summary>SEC-503. Ordered so that the highest grant wins (SEC-504).</summary>
public enum AppRole { None = 0, Reader = 1, Author = 2, Manager = 3 }

public enum PrincipalKind { User = 0, Group = 1 }

/// <summary>SEC-704.</summary>
public enum UserStatus { Active = 0, Disabled = 1, Inactive = 2 }

public enum AssignmentStatus { Active = 0, Waiting = 1, Completed = 2, Cancelled = 3 }

public enum VoteDecision { None = 0, Approve = 1, Reject = 2 }

/// <summary>INV-051: every audit entry names a human, a delegate acting for a human, or the system identity.</summary>
public enum ActorKind { User = 0, Delegate = 1, System = 2 }

public enum NotificationKind { Assignment = 0, Info = 1, Reminder = 2, Escalation = 3, SafetyCritical = 4 }

public enum EnvironmentType { Production = 0, Validation = 1, Test = 2, Development = 3 }

/// <summary>DOC-014.</summary>
public enum RevisionFormat { None = 0, NumericFrom0 = 1, NumericFrom1 = 2, Alphabetic = 3, Manual = 4 }

public enum LicenseType { Full = 0, Reader = 1, Submitter = 2, External = 3 }

public enum LinkKind
{
    /// <summary>A value of a link field.</summary>
    Field = 0,
    /// <summary>Parent → activity (PLT-205).</summary>
    Activity = 1,
    Related = 2,
    /// <summary>Revision family cross-link (DOC-093).</summary>
    Revision = 3,
    /// <summary>Record created from another record (e.g. CAPA from NCR).</summary>
    Source = 4,
}

/// <summary>SEC-507.</summary>
public enum RecordAccessLevel { Reader = 0, Editor = 1, Manager = 2 }

public enum DelegationStatus { Pending = 0, Active = 1, Completed = 2, Rejected = 3 }
