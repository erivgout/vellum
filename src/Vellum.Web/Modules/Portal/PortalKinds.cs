namespace Vellum.Modules.Portal;

/// <summary>A group of read-only fields the supplier sees (a card on the request page).</summary>
public sealed record PortalGroup(string Title, string Icon, params string[] Fields);

/// <summary>Subform rows shown to (and partly answered by) the supplier.</summary>
public sealed record PortalRowSpec(string Section, string Noun, string[] Visible, string[] Editable);

/// <summary>
/// What a supplier sees and may change for one kind of portal request. This is the whitelist behind "only the parts of the
/// record meant for them": the portal pages render only these fields and attachments, and the portal service only ever
/// writes the editable ones. Anything not listed (internal notes, verification, reviews, comments, audit trail, links,
/// people other than the internal contact) never leaves the server.
/// </summary>
public sealed class PortalKind
{
    public required string Key { get; init; }
    public required string FormKey { get; init; }
    /// <summary>Supplier-facing name, e.g. "Corrective action request".</summary>
    public required string Noun { get; init; }
    public required string Icon { get; init; }
    /// <summary>Phases in which the supplier can respond while the request is open.</summary>
    public string[] ResponsePhases { get; init; } = [];
    public PortalGroup[] Groups { get; init; } = [];
    /// <summary>Main-form fields the supplier fills in.</summary>
    public string[] Editable { get; init; } = [];
    /// <summary>Editable fields required before the supplier can submit.</summary>
    public string[] Required { get; init; } = [];
    /// <summary>Attachment fields the supplier can download from.</summary>
    public string[] VisibleFiles { get; init; } = [];
    /// <summary>Attachment fields the supplier can upload to (always also visible).</summary>
    public string[] UploadFiles { get; init; } = [];
    /// <summary>At least one file must be uploaded to this field before submitting.</summary>
    public string? RequiredFile { get; init; }
    public PortalRowSpec? Rows { get; init; }
    /// <summary>Set to the submitting contact's name and today's date on submit.</summary>
    public string? RespondedByField { get; init; }
    public string? RespondedOnField { get; init; }
    /// <summary>The internal owner (the supplier's contact person and the one notified).</summary>
    public string OwnerField { get; init; } = "owner";
    public string? DueField { get; init; }
    /// <summary>Read-only kinds (scorecards) are shared, never answered.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>For read-only kinds: the phases in which the record may be shown (released scorecards only, SUP-036).</summary>
    public string[] VisiblePhases { get; init; } = [];
    /// <summary>When true the supplier's submit routes the record to this phase (the internal review).</summary>
    public string? SubmitRoutesTo { get; init; }

    public IEnumerable<string> VisibleFields => Groups.SelectMany(g => g.Fields).Concat(Editable).Distinct();
    public IEnumerable<string> AllFiles => VisibleFiles.Concat(UploadFiles).Distinct();
}

public static class PortalKinds
{
    public const string Scar = "scar";
    public const string Document = "document";
    public const string Questionnaire = "questionnaire";
    public const string AuditResponse = "audit_response";
    public const string Scorecard = "scorecard";

    public const string DocForm = "supplier_doc_request";
    public const string QuestionnaireForm = "supplier_questionnaire";
    public const string AuditResponseForm = "supplier_audit_response";

    /// <summary>CAPA-011: the SCAR up to and including the supplier response section; never verification.</summary>
    public static readonly PortalKind ScarKind = new()
    {
        Key = Scar, FormKey = "scar", Noun = "Corrective action request", Icon = "bi-shield-exclamation",
        ResponsePhases = ["assign_to_supplier"], DueField = "response_due",
        Groups =
        [
            new("The problem", "bi-search", "subject", "description", "requirement"),
            new("Part and lot", "bi-box-seam", "part_number", "part_description", "lot_number", "po_number", "quantity_affected"),
            new("Containment we need from you", "bi-bandaid", "containment_required", "stock_quarantined", "containment_actions"),
            new("Our request", "bi-envelope-paper", "response_due", "request_message"),
        ],
        Editable = ["sr_containment", "sr_root_cause", "sr_corrective_action", "sr_preventive_action", "sr_implementation_date", "sr_responder"],
        Required = ["sr_root_cause", "sr_corrective_action", "sr_implementation_date"],
        UploadFiles = ["sr_file"],
        RespondedOnField = "sr_received_on",
    };

    public static readonly PortalKind DocumentKind = new()
    {
        Key = Document, FormKey = DocForm, Noun = "Document request", Icon = "bi-file-earmark-arrow-up",
        ResponsePhases = ["with_supplier"], DueField = "due_date", SubmitRoutesTo = "review",
        Groups = [new("What we need", "bi-file-earmark-text", "doc_type", "standard", "material", "due_date", "instructions")],
        Editable = ["doc_number", "issuer", "issued_on", "expires_on", "supplier_comments"],
        VisibleFiles = ["request_files"], UploadFiles = ["doc_file"], RequiredFile = "doc_file",
        RespondedByField = "responded_by", RespondedOnField = "responded_on",
    };

    public static readonly PortalKind QuestionnaireKind = new()
    {
        Key = Questionnaire, FormKey = QuestionnaireForm, Noun = "Questionnaire", Icon = "bi-ui-checks",
        ResponsePhases = ["with_supplier"], DueField = "due_date", SubmitRoutesTo = "review",
        Groups = [new("About this questionnaire", "bi-info-circle", "questionnaire_type", "due_date", "instructions")],
        Editable = ["supplier_comments"],
        VisibleFiles = ["request_files"], UploadFiles = ["sq_file"],
        Rows = new("questions", "question", ["q_text", "q_guidance", "q_type", "q_required"], ["answer"]),
        RespondedByField = "responded_by", RespondedOnField = "responded_on",
    };

    /// <summary>AUD-031: the supplier answers the findings of a supplier audit.</summary>
    public static readonly PortalKind AuditResponseKind = new()
    {
        Key = AuditResponse, FormKey = AuditResponseForm, Noun = "Audit findings", Icon = "bi-clipboard2-check",
        ResponsePhases = ["with_supplier"], DueField = "due_date", SubmitRoutesTo = "review",
        Groups = [new("The audit", "bi-clipboard2-check", "audit_title", "audit_date", "auditor_name", "due_date", "instructions")],
        Editable = ["supplier_comments"],
        UploadFiles = ["afr_file"],
        Rows = new("findings", "finding", ["finding_no", "clause", "classification", "finding", "evidence"],
            ["sr_root_cause", "sr_correction", "sr_corrective_action", "sr_target_date"]),
        RespondedByField = "responded_by", RespondedOnField = "responded_on",
    };

    /// <summary>SUP-036: released (published) scorecards, read-only.</summary>
    public static readonly PortalKind ScorecardKind = new()
    {
        Key = Scorecard, FormKey = "scorecard", Noun = "Scorecard", Icon = "bi-speedometer2", ReadOnly = true,
        VisiblePhases = ["published"],
        Groups =
        [
            new("Period", "bi-calendar-range", "period", "period_start", "period_end", "lots_received", "lots_rejected"),
            new("Performance measures", "bi-rulers", "ppm", "otd_pct", "responsiveness", "audit_score"),
            new("Scores", "bi-award", "s_quality", "s_delivery", "s_response", "s_audit", "overall_score", "grade"),
            new("Comments from us", "bi-chat-left-text", "comments"),
        ],
    };

    public static readonly IReadOnlyList<PortalKind> All = [ScarKind, DocumentKind, QuestionnaireKind, AuditResponseKind, ScorecardKind];

    public static PortalKind? Get(string? key) => All.FirstOrDefault(k => k.Key == key);

    public static PortalKind? ForForm(string formKey) => All.FirstOrDefault(k => k.FormKey == formKey);

    /// <summary>Audit finding classifications that need a root cause and corrective action from the supplier.</summary>
    public static readonly string[] NonconformanceClasses = ["minor_nc", "major_nc"];
}
