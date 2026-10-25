using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Modules.Training;

namespace Vellum.Modules.Documents;

/// <summary>One status banner line on a document (SAF-043 draft/superseded context, DOC-102 obsoletion in progress, DOC-050 release state).</summary>
public sealed record DocBanner(string Tone, string Icon, string Title, string Text, string? Href = null, string? LinkText = null);

public sealed record DocumentStatusModel(List<DocBanner> Banners);

public sealed record RevisionHistoryModel(Guid CurrentId, List<RevisionRow> Rows, UserDirectory Dir, TimeZoneInfo Tz);

public sealed record DocumentTrainingModel(Guid DocumentId, bool? TrainingRequired, string? Justification, GateStatus Gate, List<CourseInfo> Courses,
    CourseStats Stats, List<AssignmentRow> Assignments, bool ShowDetail, bool InAwaitingRelease, UserDirectory Dir);

public sealed record ApprovalManifestModel(Record Record, List<Signature> Signatures, TimeZoneInfo Tz);

public sealed record ReviewCardModel(ReviewInfo Info, UserDirectory Dir);

public sealed record RequestTargetModel(Record? Document, PhaseDefinition? Phase, string? TypeLabel, string? TypeColor, int? OwnerId, UserDirectory Dir, string Note);

public sealed record ReviewScheduleRow(ReviewInfo Info, string? TypeLabel, string? TypeColor);
