namespace Vellum.Modules.Documents;

/// <summary>Stable keys of the Document Control application (forms, phases, groups, signature meanings).</summary>
public static class DocKeys
{
    public const string App = "documents";
    public const string Document = "document";
    public const string Profile = "doc_type_profile";
    public const string ChangeRequest = "change_request";
    public const string PeriodicReview = "periodic_review";
    public const string ObsoleteRequest = "obsolete_request";

    public static readonly string[] RequestForms = [ChangeRequest, PeriodicReview, ObsoleteRequest];

    // Document workflow phases (DOC-030)
    public const string Draft = "draft";
    public const string DcReview = "dc_review";
    public const string InitialApproval = "initial_approval";
    public const string Review = "review";
    public const string FinalApproval = "final_approval";
    public const string AwaitingRelease = "awaiting_release";
    public const string Approved = "approved";
    public const string Voided = "voided";
    public const string Obsolete = "obsolete";

    // Groups (CoreSeed)
    public const string DocControllers = "document-control";
    public const string QaApprovers = "qa-approvers";
    public const string TrainingCoordinators = "training-coordinators";

    // Signature meanings (DOC-040/041)
    public const string SignReleased = "Released";
    public const string SignGateOverride = "Training gate override";

    // Archive reasons (DOC-104)
    public const string Superseded = "superseded";
    public const string ObsoleteReason = "obsolete";
}
