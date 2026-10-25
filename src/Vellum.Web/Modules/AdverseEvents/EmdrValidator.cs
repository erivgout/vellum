using System.Text.RegularExpressions;
using Vellum.Engine.Records;

namespace Vellum.Modules.AdverseEvents;

/// <summary>
/// AER-006: the FDA form and eMDR schema versions Vellum validates and generates against. When the FDA publishes a new 3500A
/// revision or eMDR schema, the rule set is updated and its version changes; every submission records the versions it used.
/// </summary>
public static class EmdrSchema
{
    public const string FormVersion = "FDA 3500A (MedWatch, mandatory reporting)";
    public const string SchemaVersion = "HL7 ICSR R2 · PORR_IN049016UV (FDA eMDR)";
    public const string RuleSetVersion = "Vellum eMDR rules 2026.1";
    public const string RuleSetDate = "2026-09-30";
}

public sealed record EmdrRule(string Id, string Section, string Description);

/// <param name="Blocking">False for advisories that don't stop routing.</param>
public sealed record EmdrIssue(string Rule, string Section, string? FieldKey, string Message, bool Blocking = true, Guid? RowId = null);

/// <param name="Stage">approval (Draft → Approval) or submit (Approval → Submitted to FDA).</param>
/// <param name="DuplicateNumberOf">Another MedWatch that already holds the hand-entered report number.</param>
public sealed record EmdrValidationContext(AerProfileInfo? Profile, AerMappingInfo? Mapping, IReadOnlyCollection<string> KnownProductCodes,
    DateOnly Today, string Stage, string? DuplicateNumberOf = null);

/// <summary>
/// Validates MedWatch content against the eMDR business rules before routing (AER-006). Missing or invalid data produces
/// field-level issues; blocking issues stop Draft → Approval and Approval → Submitted to FDA.
/// </summary>
public static partial class EmdrValidator
{
    public static readonly IReadOnlyList<EmdrRule> Rules =
    [
        new("AER-R01", "Setup", "The application profile exists (AER-002) and, with mapping on, the report's location has an MFR/importer mapping with a code."),
        new("AER-R02", "Numbers", "Report numbers: code of 7–10 digits, a 4-digit year no later than this year, and a 5-digit sequence (user facility: 4-digit). With mapping off, every part is entered; with manual sequences, the sequence is entered."),
        new("AER-R03", "Numbers", "A hand-entered report number isn't already used by another initial report (INV-023)."),
        new("AER-R04", "A", "Adverse event reports give the patient's sex (or Unknown). Age and weight have units; the identifier is at most 10 characters."),
        new("AER-R05", "B", "B1 type of report, B4 report date and B5 description are present; dates aren't in the future and the event isn't after the report."),
        new("AER-R06", "B", "A death outcome has a date of death, and agrees with H1 (death)."),
        new("AER-R07", "D", "D1 brand name, D2 common name, D2b product code (three letters, in the product code table), D3 manufacturer and address, D5 operator and D9 availability are present."),
        new("AER-R08", "D", "At least one device identifier (model, catalog, serial, lot or UDI). A UDI is in GS1, HIBCC or ICCBBA format."),
        new("AER-R09", "E", "Initial reporter: name or organization, country, and whether they're a health professional (with occupation)."),
        new("AER-R10", "G", "G1 contact office and address, G2 phone, G3 report source and G4 date received (not after the report date)."),
        new("AER-R11", "H", "H1 event type, H3 evaluation with a summary, and H8 usage are present. 5-day reports state the remedial action (H7, 21 CFR 803.53)."),
        new("AER-R12", "H", "IMDRF codes match their annex (A01, A0101… for Annex A). At least one Annex A device problem code."),
        new("AER-R13", "H", "Advisory: patient events carry an Annex E or F health effect code; evaluated devices carry Annex B–D evaluation codes."),
        new("AER-R14", "Report", "Supplemental reports name the original report, the follow-up type (H2) and the sections changed."),
        new("AER-R15", "Approvals", "At least one MedWatch approver, none of them the report owner (segregation of duties)."),
    ];

    public static readonly IReadOnlyDictionary<string, string> CodePatterns = new Dictionary<string, string>
    {
        ["device_problem"] = "A", ["evaluation_method"] = "B", ["evaluation_result"] = "C", ["evaluation_conclusion"] = "D",
        ["health_effect"] = "E", ["health_impact"] = "F", ["component"] = "G",
    };

    [GeneratedRegex(@"^\d{7,10}$")] private static partial Regex CodeRx();
    [GeneratedRegex(@"^[A-Z]{3}$")] private static partial Regex ProcodeRx();
    [GeneratedRegex(@"^\(01\)(\d{14})")] private static partial Regex Gs1Rx();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailRx();

    public static bool IsValidCode(string? code) => code != null && CodeRx().IsMatch(code);
    public static bool IsValidProcode(string? code) => code != null && ProcodeRx().IsMatch(code);

    /// <summary>An IMDRF code of the annex letter followed by 2, 4 or 6 digits (level 1, 2 or 3 terms).</summary>
    public static bool IsValidImdrf(string annexLetter, string? code) =>
        code != null && code.Length is 3 or 5 or 7 && code[0] == annexLetter[0] && code[1..].All(char.IsAsciiDigit);

    public static string? UdiProblem(string? udi)
    {
        if (string.IsNullOrWhiteSpace(udi)) return null;
        udi = udi.Trim();
        if (udi.StartsWith("(01)", StringComparison.Ordinal)) return Gs1Rx().IsMatch(udi) ? null : "A GS1 UDI starts with (01) and a 14-digit device identifier.";
        if (udi[0] is '+' or '=') return null; // HIBCC / ICCBBA
        return "Enter the UDI in GS1 (01)…, HIBCC (+…) or ICCBBA (=…) format, exactly as on the label.";
    }

    public static string? EmailProblem(string? email) => string.IsNullOrWhiteSpace(email) || EmailRx().IsMatch(email.Trim()) ? null : "Enter a valid email address.";

    public static List<EmdrIssue> Validate(RecordSnapshot s, EmdrValidationContext c)
    {
        var issues = new List<EmdrIssue>();
        void Add(string rule, string section, string? field, string message, bool blocking = true, Guid? row = null) =>
            issues.Add(new EmdrIssue(rule, section, field, message, blocking, row));
        bool Has(string key) => s.Data.Main.Has(key);
        string? Kw(string key) => s.KeywordKey(key);
        var multi = (string key) => s.KeywordKeys(key).ToHashSet();

        var reportType = Kw("report_type") ?? AerKeys.Initial;
        var supplemental = reportType == AerKeys.Supplemental;
        var reporterType = Kw("reporter_type") ?? AerKeys.Manufacturer;

        // ---- setup and numbers (AER-002, AER-005, INV-023)
        if (c.Profile == null)
            Add("AER-R01", "Setup", null, "Set up the Adverse Event Reporting application profile before MedWatch reports can be processed (AER-002).");
        var mappingOn = c.Profile?.MappingEnabled == true;
        var (codeKey, yearKey, seqKey) = reporterType == AerKeys.Importer ? ("importer_code", "importer_year", "importer_sequence") : ("mfr_code", "mfr_year", "mfr_sequence");
        var label = reporterType == AerKeys.Importer ? "Importer" : "MFR";
        if (mappingOn && !supplemental)
        {
            if (c.Mapping == null)
                Add("AER-R01", "Numbers", "location", $"There's no MFR/importer mapping for {s.Keyword("location")?.Label ?? "this location"}. Add one on the Adverse Event Reporting setup page, or turn mapping off.");
            else
            {
                if (c.Mapping.ReporterType != reporterType)
                    Add("AER-R01", "Numbers", "reporter_type", $"The mapping for {s.Keyword("location")?.Label} is for {(c.Mapping.ReporterType == AerKeys.Importer ? "importer" : "manufacturer")} reports.");
                var code = c.Mapping.CodeFor(reporterType, s.Text("product_code")?.Trim().ToUpperInvariant());
                if (!IsValidCode(code))
                    Add("AER-R01", "Numbers", "location", $"The mapping for {s.Keyword("location")?.Label} has no valid {label.ToLowerInvariant()} code (7 to 10 digits).");
                if (!c.Mapping.Sequential(reporterType))
                {
                    if (s.Number(seqKey) is not { } seq) Add("AER-R02", "Numbers", seqKey, $"{label} sequence: this mapping uses manual sequences, so enter the sequence number.");
                    else if (seq is < 1 or > 99999) Add("AER-R02", "Numbers", seqKey, $"{label} sequence must be between 1 and 99999.");
                }
            }
        }
        else if (!supplemental)
        {
            var code = s.Text(codeKey)?.Trim();
            if (string.IsNullOrEmpty(code)) Add("AER-R02", "Numbers", codeKey, $"{label} code is required: mapping is off, so enter every part of the report number.");
            else if (!IsValidCode(code)) Add("AER-R02", "Numbers", codeKey, $"{label} code must be the 7 to 10 digit registration or FEI number.");
            if (s.Number(yearKey) is not { } y) Add("AER-R02", "Numbers", yearKey, $"{label} year is required.");
            else if (y > c.Today.Year || y < 1990) Add("AER-R02", "Numbers", yearKey, $"{label} year can't be after {c.Today.Year}.");
            if (s.Number(seqKey) is not { } q) Add("AER-R02", "Numbers", seqKey, $"{label} sequence is required.");
            else if (q is < 1 or > 99999) Add("AER-R02", "Numbers", seqKey, $"{label} sequence must be between 1 and 99999.");
        }
        if (c.DuplicateNumberOf != null)
            Add("AER-R03", "Numbers", seqKey, $"Report number {s.Text("report_number")} is already used by {c.DuplicateNumberOf}. Report numbers are never reused (INV-023).");
        var ufParts = new[] { Has("uf_code"), Has("uf_year"), Has("uf_sequence") };
        if (ufParts.Any(x => x) && !ufParts.All(x => x))
            Add("AER-R02", "Numbers", "uf_code", "Enter all three parts of the user facility report number, or none.");
        if (Has("uf_code") && !IsValidCode(s.Text("uf_code"))) Add("AER-R02", "Numbers", "uf_code", "The user facility code must be 7 to 10 digits.");

        // ---- A · patient
        var kinds = multi("event_kind");
        var outcomes = multi("outcomes");
        var adverseEvent = kinds.Contains("adverse_event") || outcomes.Count > 0;
        if (adverseEvent && !Has("patient_sex")) Add("AER-R04", "A", "patient_sex", "A3 Sex is required for an adverse event report. Choose Unknown if it wasn't provided.");
        if (Has("patient_age") && !Has("patient_age_unit")) Add("AER-R04", "A", "patient_age_unit", "A2 Age needs a unit.");
        if (Has("patient_weight") && !Has("patient_weight_unit")) Add("AER-R04", "A", "patient_weight_unit", "A4 Weight needs a unit.");

        // ---- B · event
        if (kinds.Count == 0) Add("AER-R05", "B", "event_kind", "B1 Choose adverse event, product problem or both.");
        if (string.IsNullOrWhiteSpace(s.Text("event_description"))) Add("AER-R05", "B", "event_description", "B5 Describe the event or problem.");
        var reportDate = s.Date("report_date");
        if (reportDate == null) Add("AER-R05", "B", "report_date", "B4 Date of this report is required.");
        else if (reportDate > c.Today) Add("AER-R05", "B", "report_date", "B4 Date of this report can't be in the future.");
        if (s.Date("event_date") is { } ev)
        {
            if (ev > c.Today) Add("AER-R05", "B", "event_date", "B3 Date of event can't be in the future.");
            else if (reportDate is { } rd && ev > rd) Add("AER-R05", "B", "event_date", "B3 Date of event can't be after the date of this report.");
        }
        var eventType = Kw("event_type");
        if (outcomes.Contains("death"))
        {
            if (s.Date("death_date") is not { } dd) Add("AER-R06", "B", "death_date", "B2 Death: enter the date of death.");
            else if (dd > c.Today) Add("AER-R06", "B", "death_date", "The date of death can't be in the future.");
            if (eventType != null && eventType != "death") Add("AER-R06", "H", "event_type", "H1 must be Death when the outcome includes death.");
        }
        else if (eventType == "death") Add("AER-R06", "B", "outcomes", "B2 must include Death when H1 is Death.");

        // ---- D · device
        if (!Has("brand_name")) Add("AER-R07", "D", "brand_name", "D1 Brand name is required.");
        if (!Has("common_name")) Add("AER-R07", "D", "common_name", "D2 Common device name is required.");
        var procode = s.Text("product_code")?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(procode)) Add("AER-R07", "D", "product_code", "D2b Product code is required.");
        else if (!IsValidProcode(procode)) Add("AER-R07", "D", "product_code", "D2b Product code is the three-letter FDA code, e.g. FRN.");
        else if (c.KnownProductCodes.Count > 0 && !c.KnownProductCodes.Contains(procode))
            Add("AER-R07", "D", "product_code", $"D2b Product code {procode} isn't in the product code table. Add or import it on the setup page.");
        if (!Has("mfr_name")) Add("AER-R07", "D", "mfr_name", "D3 Manufacturer name is required.");
        if (!Has("mfr_address")) Add("AER-R07", "D", "mfr_address", "D3 Manufacturer address is required.");
        if (!Has("device_operator")) Add("AER-R07", "D", "device_operator", "D5 Operator of the device is required.");
        if (!Has("device_available")) Add("AER-R07", "D", "device_available", "D9 Say whether the device is available for evaluation.");
        if (!new[] { "model_number", "catalog_number", "serial_number", "lot_number", "udi" }.Any(Has))
            Add("AER-R08", "D", "model_number", "D4 Give at least one device identifier: model, catalog, serial or lot number, or the UDI.");
        if (UdiProblem(s.Text("udi")) is { } udiErr) Add("AER-R08", "D", "udi", udiErr);
        if (s.Date("implant_date") is { } imp && s.Date("explant_date") is { } exp && exp < imp) Add("AER-R08", "D", "explant_date", "D6 Explant date can't be before the implant date.");
        if (s.Date("returned_on") is { } ret && ret > c.Today) Add("AER-R08", "D", "returned_on", "The return date can't be in the future.");

        // ---- E · initial reporter
        if (!Has("reporter_name") && !Has("reporter_address")) Add("AER-R09", "E", "reporter_name", "E1 Give the initial reporter's name or organization.");
        if (!Has("reporter_country")) Add("AER-R09", "E", "reporter_country", "E1 The initial reporter's country is required.");
        if (s.Bool("reporter_is_hcp") is null) Add("AER-R09", "E", "reporter_is_hcp", "E2 Say whether the initial reporter is a health professional.");
        else if (s.Bool("reporter_is_hcp") == true && !Has("reporter_occupation")) Add("AER-R09", "E", "reporter_occupation", "E3 Occupation is required for a health professional.");
        if (EmailProblem(s.Text("reporter_email")) is { } emailErr) Add("AER-R09", "E", "reporter_email", emailErr);

        // ---- G · all manufacturers
        if (!Has("contact_name")) Add("AER-R10", "G", "contact_name", "G1 Contact office name is required.");
        if (!Has("contact_address")) Add("AER-R10", "G", "contact_address", "G1 Contact office address is required.");
        if (!Has("contact_phone")) Add("AER-R10", "G", "contact_phone", "G2 Phone number is required.");
        if (multi("report_sources").Count == 0) Add("AER-R10", "G", "report_sources", "G3 Choose at least one report source.");
        if (s.Date("awareness_date") is not { } aw) Add("AER-R10", "G", "awareness_date", "G4 Date received by manufacturer is required.");
        else if (aw > c.Today) Add("AER-R10", "G", "awareness_date", "G4 can't be in the future.");
        else if (reportDate is { } rd2 && aw > rd2) Add("AER-R10", "G", "awareness_date", "G4 Date received can't be after the date of this report.");

        // ---- H · device manufacturers
        if (reporterType == AerKeys.Manufacturer)
        {
            if (eventType == null) Add("AER-R11", "H", "event_type", "H1 Type of reportable event is required.");
            var evaluated = Kw("evaluated");
            if (evaluated == null) Add("AER-R11", "H", "evaluated", "H3 Say whether the device was evaluated.");
            if (string.IsNullOrWhiteSpace(s.Text("evaluation_summary")))
                Add("AER-R11", "H", "evaluation_summary", evaluated is "not_returned" or "no_other"
                    ? "H3 Explain why the device wasn't evaluated." : "H3 Summarize the evaluation and its findings.");
            if (!Has("usage")) Add("AER-R11", "H", "usage", "H8 Usage of device is required.");
            if (reportType == AerKeys.FiveDay && multi("remedial_actions").Count == 0)
                Add("AER-R11", "H", "remedial_actions", "H7 A 5-day report must state the remedial action taken (21 CFR 803.53).");
        }

        var codes = s.Rows("problem_codes");
        var annexes = new HashSet<string>();
        foreach (var row in codes)
        {
            var type = s.KeywordKey(row, "code_type");
            var code = row.Values.Text("code")?.Trim().ToUpperInvariant();
            if (type == null || code == null) continue;
            annexes.Add(type);
            if (CodePatterns.TryGetValue(type, out var letter) && !IsValidImdrf(letter, code))
                Add("AER-R12", "H", "problem_codes", $"IMDRF code {code} doesn't belong to {s.Config.Keyword("aer_code_type", type)?.Label}: codes look like {letter}01, {letter}0101 or {letter}010101.", row: row.Id);
        }
        if (!annexes.Contains("device_problem")) Add("AER-R12", "H", "problem_codes", "Add at least one IMDRF Annex A medical device problem code.");
        if (adverseEvent && !annexes.Contains("health_effect") && !annexes.Contains("health_impact"))
            Add("AER-R13", "H", "problem_codes", "Patient events should carry an IMDRF Annex E or F health effect code.", blocking: false);
        if (Kw("evaluated") == "yes" && !annexes.Overlaps(["evaluation_method", "evaluation_result", "evaluation_conclusion"]))
            Add("AER-R13", "H", "problem_codes", "Evaluated devices should carry IMDRF Annex B, C and D evaluation codes.", blocking: false);

        // ---- supplemental
        if (supplemental)
        {
            if (s.Links("original_medwatch").Count == 0) Add("AER-R14", "Report", "original_medwatch", "A supplemental report needs its original MedWatch.");
            if (multi("followup_types").Count == 0) Add("AER-R14", "Report", "followup_types", "H2 Choose the type of follow-up.");
            if (multi("changed_sections").Count == 0) Add("AER-R14", "Report", "changed_sections", "Say which sections this follow-up changes.");
        }

        // ---- approvers
        var approvers = s.UserIds("approvers");
        if (approvers.Count == 0) Add("AER-R15", "Approvals", "approvers", "Choose at least one MedWatch approver.");
        else if (s.UserId("owner") is { } owner && approvers.Contains(owner))
            Add("AER-R15", "Approvals", "approvers", "The report owner can't also approve the report (segregation of duties).");

        return issues;
    }
}
