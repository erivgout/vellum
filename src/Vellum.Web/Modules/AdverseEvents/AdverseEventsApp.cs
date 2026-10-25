using Vellum.Domain;
using Vellum.Modules.Quality;
using Vellum.Packaging;

namespace Vellum.Modules.AdverseEvents;

public static class AerKeys
{
    public const string App = "adverse_events";
    public const string MedWatch = "medwatch";
    public const string Approver = "medwatch_approver";
    public const string Ack = "emdr_ack";
    public const string Profile = "aer_profile";
    public const string Mapping = "aer_mapping";
    public const string ProductCode = "aer_product_code";

    public const string Manufacturer = "manufacturer";
    public const string Importer = "importer";

    public const string Initial = "initial";
    public const string FiveDay = "five_day";
    public const string Supplemental = "supplemental";
}

/// <summary>
/// Adverse Event Reporting §12.8 (FDA eMDR / MedWatch 3500A). Setup records: the singleton application profile (AER-001/002),
/// MFR and importer mappings by location, and the FDA product-code table. MedWatch workflow Draft → Approval → Submitted to FDA →
/// Completed, or Voided (AER-004), with the MedWatch Approver activity, eMDR validation before routing (AER-006), report-number
/// generation on submission (AER-005, INV-023), the HL7 ICSR file and gateway submission with ACK1/ACK2/ACK3 tracking
/// (AER-008..010, INV-048), and supplemental reports (AER-011).
/// </summary>
public sealed class AdverseEventsApp : IPackagedApp
{
    public string Key => AerKeys.App;
    public int Order => 45;

    private const string Supplemental = "report_type=supplemental";
    private const string Importer = "reporter_type=importer";

    public void Define(AppBuilder app)
    {
        app.Name("Adverse Event Reporting")
            .Description("FDA eMDR MedWatch 3500A reports from reportable complaints: validation, approval, gateway submission and acknowledgements.")
            .Icon("bi-file-earmark-medical").Color("#9f1239").Suite("Quality").Order(55).Version("1.0.0")
            .DefaultAccess(AppRole.None)
            .Grant("@regulatory", AppRole.Author)
            .Grant("@qa-approvers", AppRole.Author)
            .Grant("@quality", AppRole.Reader)
            .Grant("rnovak", AppRole.Manager, designer: true)
            .Grant("sortiz", AppRole.Manager);

        QualitySites.Keywords(app);
        app.Keywords("patient_sex", "Sex", k => k.Add("female", "Female").Add("male", "Male").Add("unknown", "Unknown / not provided"));
        app.Keywords("aer_report_type", "MedWatch report type", k => k
            .Add(AerKeys.Initial, "Initial (30-day)", "danger").Add(AerKeys.FiveDay, "5-day", "danger").Add(AerKeys.Supplemental, "Supplemental / follow-up", "info"));
        app.Keywords("aer_reporter_type", "Reporter type", k => k.Add(AerKeys.Manufacturer, "Manufacturer").Add(AerKeys.Importer, "Importer"));
        app.Keywords("aer_code_mode", "Manufacturer code", k => k.Add("basic", "Basic (static code)").Add("advanced", "Advanced (calculated by rule)"));
        app.Keywords("aer_sequence_format", "Sequence format", k => k.Add("sequential", "Sequential (resets each year)").Add("manual", "Manual (entered in Draft)"));
        app.Keywords("aer_age_unit", "Age unit", k => k.Add("years", "Years").Add("months", "Months").Add("weeks", "Weeks").Add("days", "Days"));
        app.Keywords("aer_weight_unit", "Weight unit", k => k.Add("kg", "kg").Add("lb", "lb"));
        app.Keywords("aer_event_kind", "Type of report (B1)", k => k.Add("adverse_event", "Adverse event").Add("product_problem", "Product problem"));
        app.Keywords("aer_outcome", "Outcome attributed to the event (B2)", k => k
            .Add("death", "Death", "danger").Add("life_threatening", "Life-threatening", "danger").Add("hospitalization", "Hospitalization (initial or prolonged)", "warning")
            .Add("disability", "Disability or permanent damage", "warning").Add("congenital", "Congenital anomaly / birth defect", "warning")
            .Add("intervention", "Required intervention to prevent permanent impairment", "warning").Add("other_serious", "Other serious (important medical event)", "warning"));
        app.Keywords("aer_device_operator", "Operator of device (D5)", k => k.Add("hcp", "Health professional").Add("patient", "Patient / lay user").Add("other", "Other"));
        app.Keywords("aer_yes_no_unknown", "Yes / no / unknown", k => k.Add("yes", "Yes").Add("no", "No").Add("unknown", "Unknown"));
        app.Keywords("aer_report_source", "Report source (G3)", k => k
            .Add("foreign", "Foreign").Add("study", "Study").Add("literature", "Literature").Add("consumer", "Consumer")
            .Add("health_professional", "Health professional").Add("user_facility", "User facility").Add("company_rep", "Company representative")
            .Add("distributor", "Distributor").Add("other", "Other"));
        app.Keywords("aer_event_type", "Type of reportable event (H1)", k => k
            .Add("death", "Death", "danger").Add("serious_injury", "Serious injury", "danger").Add("malfunction", "Malfunction", "warning").Add("other", "Other"));
        app.Keywords("aer_followup_type", "Follow-up type (H2)", k => k
            .Add("correction", "Correction").Add("additional_info", "Additional information").Add("fda_request", "Response to FDA request").Add("device_evaluation", "Device evaluation"));
        app.Keywords("aer_section", "3500A section", k => k
            .Add("a", "A · Patient").Add("b", "B · Adverse event / product problem").Add("d", "D · Suspect device").Add("e", "E · Initial reporter")
            .Add("g", "G · All manufacturers").Add("h", "H · Device manufacturers"));
        app.Keywords("aer_evaluated", "Device evaluated by manufacturer? (H3)", k => k
            .Add("yes", "Yes").Add("not_returned", "No: device not returned").Add("no_other", "No: other reason"));
        app.Keywords("aer_remedial", "Remedial action (H7)", k => k
            .Add("recall", "Recall").Add("repair", "Repair").Add("replace", "Replace").Add("relabeling", "Relabeling").Add("notification", "Notification")
            .Add("inspection", "Inspection").Add("monitoring", "Patient monitoring").Add("modification", "Modification / adjustment").Add("other", "Other"));
        app.Keywords("aer_usage", "Usage of device (H8)", k => k.Add("initial", "Initial use").Add("reuse", "Reuse").Add("unknown", "Unknown"));
        app.Keywords("aer_code_type", "IMDRF code annex", k => k
            .Add("device_problem", "Annex A · Medical device problem").Add("evaluation_method", "Annex B · Evaluation type")
            .Add("evaluation_result", "Annex C · Evaluation result").Add("evaluation_conclusion", "Annex D · Evaluation conclusion")
            .Add("health_effect", "Annex E · Health effect (clinical)").Add("health_impact", "Annex F · Health effect (impact)")
            .Add("component", "Annex G · Component"));
        app.Keywords("aer_submission_status", "eMDR submission", k => k
            .Add("not_submitted", "Not submitted", "secondary").Add("awaiting_upload", "Awaiting ESG upload", "warning").Add("awaiting_ack", "Awaiting acknowledgements", "info")
            .Add("acknowledged", "Accepted by FDA", "success").Add("rejected", "Rejected by FDA", "danger").Add("overdue", "Acknowledgement overdue", "danger"));
        app.Keywords("aer_gateway", "eMDR gateway", k => k
            .Add("manual", "Manual (ESG WebTrader)").Add("simulated", "Simulated (non-production)").Add("as2", "AS2 (not configured)"));
        app.Keywords("aer_ack_type", "Acknowledgement", k => k
            .Add("ack1", "ACK1 · ESG receipt (MDN)").Add("ack2", "ACK2 · CDRH receipt").Add("ack3", "ACK3 · Load and validation"));
        app.Keywords("aer_ack_status", "Acknowledgement result", k => k.Add("accepted", "Accepted", "success").Add("rejected", "Rejected / error", "danger"));
        app.Keywords("aer_device_class", "Device class", k => k.Add("i", "Class I").Add("ii", "Class II").Add("iii", "Class III").Add("unclassified", "Unclassified"));
        app.Keywords("aer_mapping_group", "Complaint data mapped to MedWatch", k => k
            .Add("event", "Event and dates").Add("patient", "Patient").Add("device", "Device").Add("reporter", "Initial reporter").Add("evaluation", "Investigation and codes"));

        DefineMedWatch(app);
        DefineActivities(app);
        DefineSetup(app);

        // AER-013 packaged views.
        app.View("mine", "My open MedWatch reports", v => v.Category("1. My work").Forms(AerKeys.MedWatch).Mine()
            .Columns("number", "title", "phase", "due", "report_type", "report_due", "submission_status").Sort("due", false).Default());
        app.View("my_approvals", "MedWatch approvals assigned to me", v => v.Category("1. My work").Forms(AerKeys.Approver).Mine()
            .Columns("number", "title", "phase", "due", "approver"));
        app.View("open_by_assignee", "Open MedWatch reports by assignee", v => v.Category("2. MedWatch").Forms(AerKeys.MedWatch).Open()
            .Columns("assignees", "number", "title", "phase", "due", "report_type", "report_due", "owner").Sort("assignees", false));
        app.View("deadlines", "Open reports by reporting deadline", v => v.Category("2. MedWatch").Forms(AerKeys.MedWatch).Open()
            .Columns("number", "title", "report_due", "report_rule", "phase", "report_type", "submission_status").Sort("report_due", false));
        app.View("submitted", "Submitted to FDA (by report number)", v => v.Category("2. MedWatch").Forms(AerKeys.MedWatch).Open().Phases("submitted")
            .Columns("report_number", "number", "title", "submission_status", "submitted_at", "ack1_at", "ack2_at", "ack3_at").Sort("report_number", false));
        app.View("completed", "Completed (by report number)", v => v.Category("2. MedWatch").Forms(AerKeys.MedWatch).Completed()
            .Columns("report_number", "number", "title", "report_type", "followup_number", "ack3_at", "modified").Sort("report_number", false));
        app.View("supplementals", "Supplemental / follow-up reports", v => v.Category("2. MedWatch").Forms(AerKeys.MedWatch).All().Filter("report_type", "eq", AerKeys.Supplemental)
            .Columns("number", "report_number", "followup_number", "title", "phase", "original_medwatch").Sort("created"));
        app.View("all", "All MedWatch reports", v => v.Category("2. MedWatch").Forms(AerKeys.MedWatch).All()
            .Columns("number", "report_number", "title", "phase", "report_type", "location", "created").Sort("created"));
        app.View("voided", "Voided MedWatch reports", v => v.Category("2. MedWatch").Forms(AerKeys.MedWatch).Voided()
            .Columns("number", "title", "report_type", "owner", "modified").Sort("modified"));
        app.View("acks_mfr", "Acknowledgements: manufacturer reports", v => v.Category("3. Acknowledgements").Forms(AerKeys.Ack).All().Filter("ack_reporter_type", "eq", AerKeys.Manufacturer)
            .Columns("ack_report_number", "number", "ack_type", "ack_status", "ack_received_at", "ack_message_id").Sort("ack_received_at"));
        app.View("acks_importer", "Acknowledgements: importer reports", v => v.Category("3. Acknowledgements").Forms(AerKeys.Ack).All().Filter("ack_reporter_type", "eq", AerKeys.Importer)
            .Columns("ack_report_number", "number", "ack_type", "ack_status", "ack_received_at", "ack_message_id").Sort("ack_received_at"));
        app.View("acks_rejected", "Rejections and errors", v => v.Category("3. Acknowledgements").Forms(AerKeys.Ack).All().Filter("ack_status", "eq", "rejected")
            .Columns("ack_report_number", "number", "ack_type", "ack_received_at", "ack_detail").Sort("ack_received_at"));
        app.View("approvals", "MedWatch approver activities", v => v.Category("4. Activities").Forms(AerKeys.Approver).All()
            .Columns("number", "title", "phase", "approver", "modified").Sort("modified"));
        app.View("actions", "MedWatch action items", v => v.Category("4. Activities").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee").Sort("due", false));
        app.View("profile", "Application profile", v => v.Category("5. Setup").Forms(AerKeys.Profile).All()
            .Columns("number", "title", "mapping_enabled", "receipt_delay_minutes", "modified"));
        app.View("product_codes", "Product codes", v => v.Category("5. Setup").Forms(AerKeys.ProductCode).All()
            .Columns("pc_code", "pc_device_name", "pc_class", "pc_regulation", "pc_panel").Sort("pc_code", false).PageSize(100));
        app.View("mappings", "Mappings by location", v => v.Category("5. Setup").Forms(AerKeys.Mapping).All()
            .Columns("map_location", "map_reporter_type", "map_mfr_code", "map_mfr_code_mode", "map_mfr_seq_format", "map_importer_code").Sort("map_location", false));
    }

    private static void DefineMedWatch(AppBuilder app)
    {
        string[] content = ["report", "numbers", "patient", "event", "device", "reporter", "manufacturer", "device_mfr", "problem_codes", "approvals"];
        app.Form(AerKeys.MedWatch, "MedWatch", f =>
        {
            f.Icon("bi-file-earmark-medical")
                .Description("FDA Form 3500A content for an eMDR submission: patient, event, suspect device, reporter, manufacturer and IMDRF codes.")
                .Numbering("MW-")
                .Title("{subject}")
                .Section("report", "Report", s =>
                {
                    s.Text("subject", "Subject", 12).Required().MaxLength(200);
                    s.User("owner", "Report owner", 4).Required().Header().Default("$me").Help("Regulatory affairs owner. Submits the report once it's approved.");
                    s.Keyword("report_type", "Report type", "aer_report_type", 4).Required().Header().Default(AerKeys.Initial)
                        .Help("Initial (30-day) or 5-day reports come from a reportable MDR decision; supplemental reports are created from a completed MedWatch (AER-011).");
                    s.Keyword("location", "Location", "quality_site", 4).Required()
                        .Help("Taken from the complaint's site where reported. With MFR/importer mapping on, it selects the report-number mapping (AER-001).");
                    s.Date("awareness_date", "Date received by manufacturer (G4)", 4)
                        .Help("The awareness date. The reporting deadline runs from it (CMP-009).");
                    s.Date("report_due", "Report due", 4).ReadOnly().Header();
                    s.Text("report_rule", "Deadline rule", 4).ReadOnly();
                    s.Link("complaint", "Complaint", "complaint").ReadOnly();
                    s.Link("mdr_tree", "MDR decision tree", "mdr_decision_tree").ReadOnly();
                    s.Link("original_medwatch", "Original MedWatch", AerKeys.MedWatch).ReadOnly().VisibleWhen(Supplemental);
                    s.Number("followup_number", "Follow-up #", 2).ReadOnly().VisibleWhen(Supplemental);
                    s.Keywords("followup_types", "Follow-up type (H2)", "aer_followup_type", 5).VisibleWhen(Supplemental);
                    s.Keywords("changed_sections", "Sections changed", "aer_section", 5).VisibleWhen(Supplemental)
                        .Help("Which parts of the report this follow-up corrects or adds to.");
                }, icon: "bi-file-earmark-medical")
                .Section("numbers", "Report numbers", s =>
                {
                    s.Prompt("numbers_prompt", "Each number has three parts: code, year and sequence. With MFR/importer mapping on, the code (and a sequential number) is assigned when the report enters Submitted to FDA; with mapping off, enter every part here (AER-005).");
                    s.Keyword("reporter_type", "Reporter type", "aer_reporter_type", 4).Required().Default(AerKeys.Manufacturer);
                    s.Text("report_number", "Report number", 4).ReadOnly().Header();
                    s.Text("mfr_code", "MFR code (registration / FEI number)", 4).MaxLength(10).Placeholder("1834567");
                    s.Number("mfr_year", "MFR year", 2).Decimals(0).Min(1990).Max(2100);
                    s.Number("mfr_sequence", "MFR sequence", 2).Decimals(0).Min(1).Max(99999);
                    s.Text("importer_code", "Importer code", 4).MaxLength(10).VisibleWhen(Importer);
                    s.Number("importer_year", "Importer year", 2).Decimals(0).Min(1990).Max(2100).VisibleWhen(Importer);
                    s.Number("importer_sequence", "Importer sequence", 2).Decimals(0).Min(1).Max(99999).VisibleWhen(Importer);
                    s.Text("uf_code", "User facility code (F2)", 4).MaxLength(10).Help("Only when a user facility reported the event to you with its own report number.");
                    s.Number("uf_year", "UF year", 2).Decimals(0).Min(1990).Max(2100);
                    s.Number("uf_sequence", "UF sequence", 2).Decimals(0).Min(1).Max(9999);
                }, icon: "bi-123")
                .Section("patient", "A · Patient information", s =>
                {
                    s.Text("patient_identifier", "Patient identifier (A1)", 4).MaxLength(10).Sensitive().Help("Initials or an internal code, at most 10 characters. Never the patient's name.");
                    s.Number("patient_age", "Age (A2)", 2).Min(0).Max(150).Decimals(0);
                    s.Keyword("patient_age_unit", "Age unit", "aer_age_unit", 2).Default("years");
                    s.Keyword("patient_sex", "Sex (A3)", "patient_sex", 2);
                    s.Number("patient_weight", "Weight (A4)", 2).Min(0).Max(700).Decimals(1);
                    s.Keyword("patient_weight_unit", "Weight unit", "aer_weight_unit", 2).Default("kg");
                }, tab: "Patient & event", icon: "bi-person-vcard")
                .Section("event", "B · Adverse event or product problem", s =>
                {
                    s.Keywords("event_kind", "Type of report (B1)", "aer_event_kind", 6);
                    s.Keywords("outcomes", "Outcomes attributed to the event (B2)", "aer_outcome", 6);
                    s.Date("death_date", "Date of death", 3);
                    s.Date("event_date", "Date of event (B3)", 3);
                    s.Date("report_date", "Date of this report (B4)", 3).Default("$today");
                    s.TextArea("event_description", "Describe the event or problem (B5)").MaxLength(20000);
                    s.TextArea("relevant_tests", "Relevant tests and laboratory data (B6)").MaxLength(4000);
                    s.TextArea("other_history", "Other relevant history (B7)").MaxLength(4000);
                }, tab: "Patient & event", icon: "bi-activity")
                .Section("device", "D · Suspect medical device", s =>
                {
                    s.Text("brand_name", "Brand name (D1)", 6).MaxLength(200);
                    s.Text("common_name", "Common device name (D2)", 4).MaxLength(200);
                    s.Text("product_code", "Product code (D2b)", 2).MaxLength(3).Placeholder("FRN").Help("The FDA three-letter product code. It must be in the product code table.");
                    s.Text("mfr_name", "Manufacturer name (D3)", 6).MaxLength(200);
                    s.Text("mfr_address", "Manufacturer address", 6).MaxLength(400);
                    s.Text("model_number", "Model # (D4)", 3).MaxLength(100);
                    s.Text("catalog_number", "Catalog #", 3).MaxLength(100);
                    s.Text("serial_number", "Serial #", 3).MaxLength(100);
                    s.Text("lot_number", "Lot #", 3).MaxLength(100);
                    s.Date("expiration_date", "Expiration date", 3);
                    s.Text("udi", "Unique device identifier (UDI)", 9).MaxLength(200).Help("GS1 format, e.g. (01)00812345000123(21)VX2-24180-0412.");
                    s.Keyword("device_operator", "Operator of device (D5)", "aer_device_operator", 4);
                    s.Date("implant_date", "Implant date (D6)", 2);
                    s.Date("explant_date", "Explant date", 2);
                    s.YesNo("reprocessed", "Single-use device reprocessed? (D7)", 4);
                    s.Keyword("device_available", "Available for evaluation? (D9)", "aer_yes_no_unknown", 4);
                    s.Date("returned_on", "Returned to manufacturer on", 3);
                    s.TextArea("concomitant_products", "Concomitant medical products (D10)").MaxLength(4000);
                }, tab: "Device", icon: "bi-upc-scan")
                .Section("reporter", "E · Initial reporter", s =>
                {
                    s.Text("reporter_name", "Name (E1)", 4).MaxLength(120);
                    s.Text("reporter_address", "Address / organization", 8).MaxLength(400);
                    s.Text("reporter_phone", "Phone", 4).MaxLength(40);
                    s.Text("reporter_email", "Email", 4).MaxLength(200);
                    s.Text("reporter_country", "Country", 4).MaxLength(60);
                    s.YesNo("reporter_is_hcp", "Health professional? (E2)", 4);
                    s.Text("reporter_occupation", "Occupation (E3)", 4).MaxLength(120);
                    s.Keyword("reporter_sent_to_fda", "Initial reporter also sent a report to FDA? (E4)", "aer_yes_no_unknown", 4).Default("unknown");
                }, tab: "Reporter", icon: "bi-person-lines-fill")
                .Section("manufacturer", "G · All manufacturers", s =>
                {
                    s.Text("contact_name", "Contact office: name (G1)", 4).MaxLength(120);
                    s.Text("contact_address", "Contact office: address", 8).MaxLength(400);
                    s.Text("contact_phone", "Phone (G2)", 4).MaxLength(40);
                    s.Keywords("report_sources", "Report source (G3)", "aer_report_source", 8);
                    s.Text("premarket_number", "PMA / 510(k) number (G5)", 4).MaxLength(20);
                    s.YesNo("combination_product", "Combination product?", 4);
                }, tab: "Manufacturer", icon: "bi-building")
                .Section("device_mfr", "H · Device manufacturers only", s =>
                {
                    s.Keyword("event_type", "Type of reportable event (H1)", "aer_event_type", 4);
                    s.Keyword("evaluated", "Device evaluated by manufacturer? (H3)", "aer_evaluated", 4);
                    s.Date("manufacture_date", "Device manufacture date (H4)", 4);
                    s.TextArea("evaluation_summary", "Evaluation summary").MaxLength(8000);
                    s.YesNo("single_use_labeled", "Labeled for single use? (H5)", 4);
                    s.Keywords("remedial_actions", "Remedial action initiated (H7)", "aer_remedial", 8);
                    s.Keyword("usage", "Usage of device (H8)", "aer_usage", 4);
                    s.Text("correction_number", "Correction / removal report number (H9)", 8).MaxLength(60);
                    s.TextArea("mfr_narrative", "Additional manufacturer narrative (H10)").MaxLength(20000);
                }, tab: "Manufacturer", icon: "bi-gear-wide-connected")
                .Subform("problem_codes", "Event problem and evaluation codes (IMDRF)", s =>
                {
                    s.Keyword("code_type", "Annex", "aer_code_type", 4).Required();
                    s.Text("code", "Code", 2).Required().MaxLength(10);
                    s.Text("term", "Term", 6).MaxLength(200);
                }, tab: "Manufacturer", rowNoun: "code", icon: "bi-upc",
                   description: "IMDRF adverse event terminology. At least one Annex A device problem code is needed; patient events also need an Annex E or F code.")
                .Section("approvals", "MedWatch approvers", s =>
                {
                    s.Prompt("approvals_prompt", "Each approver gets a MedWatch Approver activity when the report enters Approval. Every approver must approve before the report can be submitted to the FDA.");
                    s.Users("approvers", "Approvers", 8).Help("Someone other than the report owner (segregation of duties).");
                }, tab: "Activities & approvals", icon: "bi-person-check")
                .Section("submission", "Submission and acknowledgements", s =>
                {
                    s.Keyword("submission_status", "eMDR submission", "aer_submission_status", 4).ReadOnly().Header().Default("not_submitted");
                    s.Keyword("gateway", "Gateway", "aer_gateway", 4).ReadOnly();
                    s.DateTime("submitted_at", "Sent to FDA", 4).ReadOnly();
                    s.DateTime("ack1_at", "ACK1 · ESG receipt", 4).ReadOnly();
                    s.DateTime("ack2_at", "ACK2 · CDRH receipt", 4).ReadOnly();
                    s.DateTime("ack3_at", "ACK3 · load and validation", 4).ReadOnly();
                }, tab: "Attachments & outputs", icon: "bi-send-check")
                .Section("completion", "Final acknowledgement", s =>
                {
                    s.Prompt("completion_prompt", "Complete the report only when ACK1, ACK2 and ACK3 are all recorded without errors, then confirm the final acknowledgement (INV-048).");
                    s.YesNo("confirm_final_ack", "Confirm final acknowledgement", 6);
                    s.TextArea("completion_notes", "Notes");
                }, tab: "Attachments & outputs", icon: "bi-patch-check")
                .Section("outputs", "Attachments and outputs", s =>
                {
                    s.Attachment("submission_file", "eMDR submission file (HL7 ICSR XML)").ReadOnly();
                    s.Attachment("supporting_files", "Supporting documents");
                }, tab: "Attachments & outputs", icon: "bi-paperclip");

            f.Workflow("medwatch", "MedWatch", w =>
            {
                w.Start("draft", "Draft").Purpose("Complete the 3500A content. eMDR validation must pass before the report goes for approval (AER-006).")
                    .DueDays(3).Edit([.. content, "outputs"]).ReadOnly("submission").Hide("completion").Activities("action_item");
                w.Phase("approval", "Approval").Purpose("MedWatch approvers review the report. Once every approver has approved, the owner submits it to the FDA (e-signed).")
                    .AssignField("owner").DueDays(2).ReadOnlyRest().Hide("completion")
                    .MustComplete(AerKeys.Approver, "action_item").Activities(AerKeys.Approver, "action_item");
                w.Phase("submitted", "Submitted to FDA").Purpose("The report has been sent through the eMDR gateway. Record or wait for ACK1, ACK2 and ACK3, then confirm the final acknowledgement.")
                    .AssignField("owner").DueDays(3).Edit("completion", "outputs").ReadOnlyRest().Activities("action_item");
                w.Completed("completed", "Completed");
                w.Voided();

                w.Forward("draft", "approval", label: "Send for approval");
                w.Void("draft", sign: "Voided");
                w.Forward("approval", "submitted", label: "Submit to FDA", sign: "Submitted to FDA");
                w.Back("approval", "draft");
                w.Void("approval", sign: "Voided");
                w.Forward("submitted", "completed", label: "Complete: final acknowledgement confirmed", sign: "Final acknowledgement confirmed");
                w.Back("submitted", "draft", label: "Correct and resubmit");
            });
        });
    }

    private static void DefineActivities(AppBuilder app)
    {
        // The MedWatch approver activity (AER-003).
        app.Form(AerKeys.Approver, "MedWatch Approver", f => f
            .Icon("bi-person-check")
            .Description("One approver's review of a MedWatch report before it's submitted to the FDA.")
            .Numbering("MWA-")
            .Title("{subject}")
            .Activity(AerKeys.MedWatch)
            .HiddenFromNew()
            .Section("approval", "Approval", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200);
                s.User("approver", "Approver", 4).Required().Header();
                s.TextArea("approval_comments", "Comments");
            }, icon: "bi-person-check")
            .Workflow("medwatch_approver", "MedWatch approval", w =>
            {
                w.Start("open", "Open").Purpose("Review the MedWatch report and approve it, or request changes.").AssignField("approver").DueDays(2).Edit("approval");
                w.Completed("approved", "Approved");
                w.Completed("changes_requested", "Changes Requested");
                w.Voided();
                w.Forward("open", "approved", label: "Approve report", sign: "Approved");
                w.Forward("open", "changes_requested", label: "Request changes", comment: true);
                w.Void("open");
            }));

        // AER-008: one record per FDA acknowledgement, linked to its MedWatch.
        app.Form(AerKeys.Ack, "eMDR Acknowledgement", f => f
            .Icon("bi-envelope-check")
            .Description("An FDA eMDR acknowledgement (ACK1, ACK2 or ACK3) received for a MedWatch submission.")
            .Numbering("ACK-")
            .Title("{ack_type} · {ack_report_number}")
            .Activity(AerKeys.MedWatch)
            .NonWorkflow()
            .HiddenFromNew()
            .Section("ack", "Acknowledgement", s =>
            {
                s.Keyword("ack_type", "Acknowledgement", "aer_ack_type", 4).Required().ReadOnly().Header();
                s.Keyword("ack_status", "Result", "aer_ack_status", 4).Required().ReadOnly().Header();
                s.DateTime("ack_received_at", "Received", 4).Required().ReadOnly();
                s.Text("ack_report_number", "Report number", 4).ReadOnly();
                s.Keyword("ack_reporter_type", "Reporter type", "aer_reporter_type", 4).ReadOnly();
                s.Text("ack_message_id", "Message ID", 4).ReadOnly();
                s.Text("ack_fda_reference", "FDA reference", 4).ReadOnly();
                s.Text("ack_source", "Recorded by", 8).ReadOnly();
                s.TextArea("ack_detail", "Detail / errors").ReadOnly();
            }, icon: "bi-envelope-check"));
    }

    private static void DefineSetup(AppBuilder app)
    {
        // AER-001 (2): the singleton application profile. Edited on the setup page only, which keeps it single and syncs the
        // e-signature meanings onto the MedWatch routes.
        app.Form(AerKeys.Profile, "Application Profile", f => f
            .Icon("bi-sliders")
            .Description("Adverse Event Reporting settings: MFR/importer mapping, acknowledgement receipt delay, e-signature meanings and default approvers.")
            .Numbering("AERP-")
            .Title("Adverse Event Reporting application profile")
            .NonWorkflow()
            .HiddenFromNew()
            .Section("profile", "Application profile", s =>
            {
                s.Prompt("profile_prompt", "Change these settings on the Adverse Event Reporting setup page. Every change is audited.");
                s.YesNo("mapping_enabled", "MFR / importer mapping", 4).ReadOnly().Header();
                s.Number("receipt_delay_minutes", "Submission receipt delay (minutes)", 4).ReadOnly().Header().Decimals(0).Min(5).Max(20160);
                s.Text("sign_submit", "E-signature meaning: Submit to FDA", 4).ReadOnly();
                s.Text("sign_complete", "E-signature meaning: Complete", 4).ReadOnly();
                s.Text("sign_void", "E-signature meaning: Void", 4).ReadOnly();
                s.Users("default_approvers", "Default MedWatch approvers", 8).ReadOnly();
                s.Keywords("mapped_groups", "Complaint data carried into new MedWatch reports", "aer_mapping_group", 12).ReadOnly();
            }, icon: "bi-sliders"));

        // AER-001 (3): one MFR / importer mapping per location.
        app.Form(AerKeys.Mapping, "MFR & Importer Mapping", f => f
            .Icon("bi-geo-alt")
            .Description("Report-number codes and sequence formats for one location (AER-001, AER-005).")
            .Numbering("AERM-")
            .Title("{map_location} · {map_reporter_type}")
            .NonWorkflow()
            .Section("map", "Location", s =>
            {
                s.Keyword("map_location", "Location", "quality_site", 4).Required().Unique().Header().Help("A location has exactly one mapping. Several locations may share a code.");
                s.Keyword("map_reporter_type", "Reporter type", "aer_reporter_type", 4).Required().Header().Default(AerKeys.Manufacturer);
            }, icon: "bi-geo-alt")
            .Section("map_mfr", "Manufacturer report numbers", s =>
            {
                s.Keyword("map_mfr_code_mode", "Manufacturer code", "aer_code_mode", 4).Required().Default("basic");
                s.Text("map_mfr_code", "Manufacturer code", 4).MaxLength(10).Placeholder("1834567").Help("FDA registration or FEI number (7 to 10 digits). With advanced codes, the default when no rule matches.");
                s.Keyword("map_mfr_seq_format", "Sequence format", "aer_sequence_format", 4).Required().Default("sequential");
                s.Number("map_mfr_seq_initial", "Initial number", 3).Decimals(0).Min(1).Max(99999).Default("1");
                s.Number("map_mfr_seq_initial_year", "Initial number applies to year", 3).Decimals(0).Min(1990).Max(2100)
                    .Help("Later years start again at 00001.");
            }, icon: "bi-upc")
            .Subform("map_code_rules", "Advanced code rules", s =>
            {
                s.Text("rule_product_code", "When the product code is", 4).Required().MaxLength(3);
                s.Text("rule_code", "Use manufacturer code", 4).Required().MaxLength(10);
                s.Text("rule_note", "Why", 4).MaxLength(200);
            }, rowNoun: "rule", icon: "bi-signpost-split", description: "Used when the manufacturer code is Advanced: the first rule whose product code matches the suspect device decides the code.")
            .Section("map_importer", "Importer report numbers", s =>
            {
                s.Text("map_importer_code", "Importer code", 4).MaxLength(10);
                s.Keyword("map_imp_seq_format", "Sequence format", "aer_sequence_format", 4).Default("sequential");
                s.Number("map_imp_seq_initial", "Initial number", 2).Decimals(0).Min(1).Max(99999).Default("1");
                s.Number("map_imp_seq_initial_year", "Applies to year", 2).Decimals(0).Min(1990).Max(2100);
            }, icon: "bi-box-arrow-in-down")
            .Section("map_contact", "Manufacturer details for the report", s =>
            {
                s.Text("map_mfr_name", "Manufacturer name (D3)", 6).MaxLength(200);
                s.Text("map_mfr_address", "Manufacturer address", 6).MaxLength(400);
                s.Text("map_contact_name", "Contact office (G1)", 4).MaxLength(120);
                s.Text("map_contact_address", "Contact office address", 5).MaxLength(400);
                s.Text("map_contact_phone", "Phone (G2)", 3).MaxLength(40);
            }, icon: "bi-building"));

        // AER-001 (4): device name ↔ FDA product code. Can be imported on the setup page.
        app.Form(AerKeys.ProductCode, "Product Code", f => f
            .Icon("bi-upc")
            .Description("An FDA product code (procode) and the device it classifies.")
            .Numbering("AERC-")
            .Title("{pc_code} · {pc_device_name}")
            .NonWorkflow()
            .Section("pc", "Product code", s =>
            {
                s.Text("pc_code", "Product code", 3).Required().Unique().MaxLength(3).Header();
                s.Text("pc_device_name", "Device name", 9).Required().MaxLength(200);
                s.Keyword("pc_class", "Device class", "aer_device_class", 3);
                s.Text("pc_regulation", "Regulation number", 3).MaxLength(20).Placeholder("880.5725");
                s.Text("pc_panel", "Review panel", 6).MaxLength(80);
                s.Text("pc_brand_names", "Our brand names", 12).MaxLength(400).Help("Comma-separated. Used to fill in the product code when a MedWatch is created from a complaint.");
            }, icon: "bi-upc"));
    }

    public Task SeedDemoAsync(DemoContext d) => AerDemo.SeedAsync(d);
}
