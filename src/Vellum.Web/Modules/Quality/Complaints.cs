using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Packaging;

namespace Vellum.Modules.Quality;

/// <summary>
/// Complaints §12.7: Draft → Investigation → Review → Awaiting Closure → Closed, plus Voided (CMP-002). Awaiting Closure is read-only
/// except the diligence log and action items (CMP-006). MDR and EU vigilance decision trees must be completed before the complaint leaves
/// Investigation (CMP-004, INV-046). Regulatory reporting clocks run from the awareness date (CMP-009, SAF-023, SAF-024).
/// </summary>
public sealed class ComplaintsApp : IPackagedApp
{
    public string Key => "complaints";
    public int Order => 40;

    private const string Patient = "patient_involved=yes";

    public void Define(AppBuilder app)
    {
        app.Name("Complaint Handling")
            .Description("Capture, investigate and close product complaints with MDR and vigilance decisions and regulatory reporting clocks.")
            .Icon("bi-chat-left-dots").Color("#be123c").Suite("Quality").Order(50).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@quality", AppRole.Author)
            .Grant("@regulatory", AppRole.Author)
            .Grant("rnovak", AppRole.Manager)
            .Grant("sortiz", AppRole.Manager, designer: true);

        QualitySites.Keywords(app);
        RiskPriority.Keywords(app);
        app.Keywords("complaint_category", "Complaint category", k => k
            .Add("product_quality", "Product quality").Add("adverse_event", "Adverse event / injury", "danger").Add("labeling", "Labeling / IFU")
            .Add("packaging", "Packaging / sterility").Add("delivery", "Delivery / logistics").Add("service", "Service / support"));
        app.Keywords("complaint_failure_mode", "Failure mode", k => k
            .Add("occlusion_alarm", "Occlusion alarm failure").Add("leak", "Leak").Add("display", "Display / UI failure").Add("battery", "Battery / power")
            .Add("mechanical", "Mechanical break / crack").Add("label", "Label / IFU error").Add("contamination", "Contamination / particulate")
            .Add("no_failure", "No failure found").Add("other", "Other"));
        app.Keywords("complaint_reporter_type", "Reporter type", k => k
            .Add("patient", "Patient / caregiver").Add("hcp", "Healthcare professional").Add("biomed", "Biomedical engineer")
            .Add("distributor", "Distributor").Add("sales", "Acme sales representative").Add("other", "Other"));
        app.Keywords("complaint_channel", "Received via", k => k
            .Add("phone", "Phone").Add("email", "Email").Add("web", "Web form").Add("sales_rep", "Sales representative").Add("field_service", "Field service"));
        app.Keywords("patient_sex", "Sex", k => k.Add("female", "Female").Add("male", "Male").Add("unknown", "Unknown / not provided"));
        app.Keywords("complaint_conclusion", "Investigation conclusion", k => k
            .Add("confirmed", "Complaint confirmed", "danger").Add("not_confirmed", "Not confirmed", "success")
            .Add("no_failure", "No failure found", "success").Add("not_returned", "Device not returned", "secondary"));
        app.Keywords("mdr_decision", "MDR decision", k => k
            .Add("pending", "Decision pending", "secondary").Add("reportable_30", "Reportable: 30-day", "danger")
            .Add("reportable_5", "Reportable: 5-day", "danger").Add("not_reportable", "Not reportable", "success"));
        app.Keywords("vigilance_decision", "EU vigilance decision", k => k
            .Add("not_applicable", "Not applicable (not on EU market)", "secondary").Add("pending", "Decision pending", "secondary")
            .Add("reportable_2", "Reportable: 2 days", "danger").Add("reportable_10", "Reportable: 10 days", "danger")
            .Add("reportable_15", "Reportable: 15 days", "danger").Add("not_reportable", "Not reportable", "success"));
        app.Keywords("reporting_clock_status", "Reporting clock", k => k
            .Add("none", "No report due", "secondary").Add("running", "Clock running", "info").Add("due_soon", "Due soon", "warning")
            .Add("missed", "Deadline missed", "danger").Add("submitted", "Submitted", "success"));
        app.Keywords("diligence_method", "Contact method", k => k.Add("phone", "Phone").Add("email", "Email").Add("letter", "Letter").Add("visit", "Visit"));
        app.Keywords("mdr_report_type", "MDR report type", k => k
            .Add("thirty_day", "30-day report", "danger").Add("five_day", "5-day report", "danger").Add("not_reportable", "Not reportable", "success"));
        app.Keywords("vigilance_severity", "Serious incident type", k => k
            .Add("public_health_threat", "Serious public health threat (2 days)", "danger")
            .Add("death_serious", "Death or unanticipated serious deterioration (10 days)", "danger")
            .Add("other_serious", "Other serious incident (15 days)", "warning"));

        app.Form("complaint", "Complaint", f => f
            .Icon("bi-chat-left-dots")
            .Description("A product complaint: the event, the device, the reporter, patient involvement, investigation, MDR decision and response.")
            .Numbering("CMP-")
            .Title("{subject}")
            .Section("complaint", "Complaint", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200).Placeholder("For example: VX-200 did not alarm on downstream occlusion");
                s.Keyword("category", "Category", "complaint_category").Required().Header();
                s.User("owner", "Complaint handler").Required().Header().Default("$me");
                s.Date("received_on", "Entered on").Default("$today").Help("When the complaint was logged here.");
                s.Date("awareness_date", "Awareness date").Required().Header().Default("$today")
                    .Help("The date anyone at Acme first became aware of the event. All regulatory reporting clocks run from this date, not from entry (CMP-009, SAF-023).");
                s.Keyword("site_reported", "Site where reported", "quality_site").Required();
                s.Keyword("channel", "Received via", "complaint_channel");
                s.Link("customer", "Customer", "customer");
                s.TextArea("description", "Complaint description").Required();
            }, icon: "bi-chat-left-dots")
            .Section("product", "Product", s =>
            {
                s.Text("product", "Product", 4).Required();
                s.Text("model", "Model / catalog number", 4);
                s.Number("quantity", "Quantity", 2).Min(0).Default("1");
                s.Text("lot_number", "Lot / batch", 3);
                s.Text("serial_number", "Serial number", 3);
                s.Text("udi", "UDI", 6).Help("Device identifier and production identifier from the label, e.g. (01)00812345000123(17)270630(10)24-127.");
                s.Keyword("failure_mode", "Failure mode", "complaint_failure_mode");
                s.YesNo("device_returned", "Device returned?");
                s.Link("rma_record", "Customer return (RMA)", "rma");
            }, icon: "bi-upc-scan")
            .Section("reporter", "Reporter", s =>
            {
                s.Text("reporter_name", "Name", 4);
                s.Keyword("reporter_type", "Reporter type", "complaint_reporter_type");
                s.Text("reporter_organization", "Organization", 4);
                s.Text("reporter_email", "Email", 4);
                s.Text("reporter_phone", "Phone", 4);
                s.Text("reporter_country", "Country", 4);
            }, icon: "bi-person")
            .Section("patient", "Patient involvement", s =>
            {
                s.YesNo("patient_involved", "Was a patient involved?").Required();
                s.Text("patient_identifier", "Patient identifier (initials / ID)", 4).VisibleWhen(Patient).Sensitive();
                s.Number("patient_age", "Age", 2).VisibleWhen(Patient).Min(0).Max(130).Decimals(0);
                s.Keyword("patient_sex", "Sex", "patient_sex", 3).VisibleWhen(Patient);
                s.YesNo("injury", "Injury reported?", 3).VisibleWhen(Patient);
                s.YesNo("death", "Death reported?", 3).VisibleWhen(Patient);
                s.YesNo("medical_intervention", "Medical intervention needed?", 3).VisibleWhen(Patient);
                s.TextArea("patient_outcome", "Patient outcome").VisibleWhen(Patient);
            }, tab: "Patient & safety", icon: "bi-heart-pulse")
            .Section("regulatory", "Regulatory reporting", s =>
            {
                s.Prompt("regulatory_prompt", "Decisions come from the completed MDR and vigilance decision trees. Report-due dates are calculated from the awareness date; submissions are recorded with \"Record regulatory submission\" (e-signed).");
                s.YesNo("eu_market", "Device marketed in the EU?").Default("no").Help("Yes adds the EU MDR vigilance decision tree and clock (Article 87).");
                s.Keyword("mdr_decision", "FDA MDR decision", "mdr_decision").ReadOnly();
                s.Keyword("vigilance_decision", "EU vigilance decision", "vigilance_decision").ReadOnly();
                s.Date("report_due", "Next report due").ReadOnly().Header();
                s.Text("report_rule", "Rule", 8).ReadOnly();
                s.Keyword("clock_status", "Reporting clock", "reporting_clock_status").ReadOnly().Header();
                s.Date("fda_submitted_on", "FDA report submitted").ReadOnly();
                s.Date("eu_submitted_on", "EU report submitted").ReadOnly();
                s.Text("report_reference", "Report numbers", 6).ReadOnly();
            }, tab: "Regulatory", icon: "bi-alarm")
            .Section("investigation", "Investigation", s =>
            {
                s.TextArea("investigation_summary", "Investigation summary").RequiredIn("investigation");
                s.Keyword("conclusion", "Conclusion", "complaint_conclusion").RequiredIn("investigation");
                s.Keyword("root_cause_category", "Root cause category", "root_cause_category");
                s.TextArea("root_cause", "Root cause");
            }, tab: "Investigation", icon: "bi-search")
            .Section("risk", "Risk assessment", s =>
            {
                s.Keyword("harm_severity", "Severity of harm", "quality_severity");
                s.TextArea("risk_assessment", "Risk evaluation (against the risk management file)");
                s.Link("risk_record", "Linked risk assessment");
            }, tab: "Investigation", icon: "bi-exclamation-diamond")
            .Section("additional", "Additional actions", s =>
            {
                s.YesNo("capa_required", "Is a CAPA required?").Help("Yes: use \"Initiate CAPA\". No: a justification is required (QE-005).");
                s.TextArea("capa_justification", "Justification (no CAPA)").VisibleWhen("capa_required=no");
                s.Link("capa_record", "Linked CAPA", "capa");
                s.YesNo("ncr_required", "Is an NCR required?");
                s.TextArea("ncr_justification", "Justification (no NCR)").VisibleWhen("ncr_required=no");
                s.Link("ncr_record", "Linked NCR", "ncr");
                s.Link("related_complaints", "Related complaints", "complaint");
            }, tab: "Investigation", icon: "bi-diagram-3")
            .Section("response", "Response to customer", s =>
            {
                s.YesNo("response_required", "Response required?").Default("yes");
                s.TextArea("response_text", "Response").VisibleWhen("response_required=yes").RequiredIn("review");
                s.Date("response_sent_on", "Sent on").VisibleWhen("response_required=yes").RequiredIn("review");
            }, tab: "Response", icon: "bi-reply")
            .Subform("diligence_log", "Diligence log", s =>
            {
                s.Date("dl_date", "Date", 2).Required();
                s.Keyword("dl_method", "Method", "diligence_method", 2).Required();
                s.Text("dl_contact", "Contacted", 3).Required();
                s.Text("dl_outcome", "Outcome / information obtained", 4).Required();
                s.YesNo("dl_info_obtained", "Info?", 1);
            }, tab: "Diligence log", rowNoun: "attempt", icon: "bi-telephone",
               description: "Every attempt to get information about the event (CMP-007). Still editable in Awaiting Closure (CMP-006).")
            .Workflow("complaint", "Complaint", w =>
            {
                w.Start("draft", "Draft").Purpose("Log the complaint: the event, the device, the reporter and patient involvement. Check the awareness date.")
                    .DueDays(2).Edit("complaint", "product", "reporter", "patient", "regulatory", "diligence_log")
                    .Hide("investigation", "risk", "additional", "response");
                w.Phase("investigation", "Investigation").Purpose("Investigate the complaint and complete the MDR (and EU vigilance) decision trees. They must be completed before the complaint moves on (INV-046).")
                    .AssignField("owner").DueDays(20)
                    .Edit("complaint", "product", "reporter", "patient", "regulatory", "investigation", "risk", "additional", "response", "diligence_log")
                    .MustCreate("mdr_decision_tree").MustComplete("mdr_decision_tree", "vigilance_decision_tree")
                    .Activities("mdr_decision_tree", "vigilance_decision_tree", "action_item");
                w.Phase("review", "Review").Purpose("Quality reviews the investigation, the reportability decisions and the response. Reportable events must be submitted first.")
                    .AssignGroup("qa-approvers").DueDays(5).Edit("response", "additional", "diligence_log").ReadOnlyRest()
                    .MustComplete("mdr_decision_tree", "vigilance_decision_tree").Activities("mdr_decision_tree", "vigilance_decision_tree", "action_item");
                w.Phase("awaiting_closure", "Awaiting Closure").Purpose("Everything is read-only. Only diligence log entries and action items can still be added (CMP-006).")
                    .AssignField("owner").DueDays(10).Edit("diligence_log").ReadOnlyRest().MustComplete("action_item").Activities("action_item");
                w.Completed("closed", "Closed");
                w.Voided();

                w.Forward("draft", "investigation", label: "Start investigation");
                w.Forward("draft", "review", label: "Skip investigation: send to review", comment: true);
                w.Forward("investigation", "review", label: "Send to review");
                w.Back("investigation", "draft");
                w.Forward("review", "awaiting_closure", label: "Approve: awaiting closure", sign: "Approved");
                w.Back("review", "investigation");
                w.Back("review", "draft");
                w.Void("review", sign: "Voided");
                w.Forward("awaiting_closure", "closed", label: "Close complaint", sign: "Closed");
                w.Back("awaiting_closure", "review");
            }));

        // CMP-003: FDA MDR decision tree (21 CFR 803).
        app.Form("mdr_decision_tree", "MDR Decision Tree", f => f
            .Icon("bi-diagram-2")
            .Description("FDA Medical Device Reporting decision (21 CFR 803): is the event reportable, and is it a 5-day or 30-day report?")
            .Numbering("MDR-")
            .Title("{subject}")
            .Activity("complaint")
            .Section("mdr_questions", "Reportability questions", s =>
            {
                s.Text("subject", "Subject", 12).Default("MDR reportability decision").ReadOnly();
                s.Prompt("mdr_prompt", "Answer each question in turn. The next question appears when it's needed; the conclusion is worked out for you.");
                s.YesNo("q_death", "1. Did the device cause or contribute to a death?", 12).RequiredIn("open");
                s.YesNo("q_serious_injury", "2. Did it cause or contribute to a serious injury (life-threatening, permanent impairment, or needing intervention to prevent it)?", 12)
                    .VisibleWhen("q_death=no").RequiredIn("open");
                s.YesNo("q_malfunction", "3. Did the device malfunction?", 12).VisibleWhen("q_death=no&&q_serious_injury=no").RequiredIn("open");
                s.YesNo("q_malfunction_harm", "4. Would the malfunction be likely to cause or contribute to a death or serious injury if it recurred?", 12)
                    .VisibleWhen("q_death=no&&q_serious_injury=no&&q_malfunction=yes").RequiredIn("open");
                s.YesNo("q_remedial", "5. Does the event need remedial action to prevent an unreasonable risk of substantial harm to public health? (5-day report, 21 CFR 803.53)", 12)
                    .VisibleWhen("q_death=yes||q_serious_injury=yes||q_malfunction_harm=yes").RequiredIn("open");
            }, icon: "bi-signpost-split")
            .Section("mdr_conclusion", "Conclusion", s =>
            {
                s.YesNo("mdr_reportable", "Reportable to FDA?").ReadOnly().Header();
                s.Keyword("mdr_report_type", "Report type", "mdr_report_type").ReadOnly().Header();
                s.TextArea("mdr_rationale", "Rationale").RequiredIn("open");
            }, icon: "bi-check2-square")
            .Workflow("mdr_tree", "MDR decision tree", w =>
            {
                w.Start("open", "Open").Purpose("Answer the reportability questions and explain the decision.").DueDays(3).Edit("mdr_questions", "mdr_conclusion");
                w.Phase("approval", "Regulatory Approval").Purpose("Regulatory affairs approves the reportability decision.")
                    .AssignGroup("regulatory").DueDays(2).ReadOnlyRest();
                w.Completed("completed", "Completed");
                w.Voided();
                w.Forward("open", "approval", label: "Send for regulatory approval");
                w.Void("open");
                w.Forward("approval", "completed", label: "Approve decision", sign: "MDR decision approved");
                w.Back("approval", "open");
            }));

        // CMP-003: EU MDR vigilance (Article 87) decision tree.
        app.Form("vigilance_decision_tree", "Vigilance Decision Tree", f => f
            .Icon("bi-globe-europe-africa")
            .Description("EU MDR Article 87 decision: is this a serious incident, and within how many days must it be reported?")
            .Numbering("VIG-")
            .Title("{subject}")
            .Activity("complaint")
            .Section("vig_questions", "Serious incident questions", s =>
            {
                s.Text("subject", "Subject", 12).Default("EU vigilance decision").ReadOnly();
                s.YesNo("q_incident", "1. Is this an incident (malfunction or deterioration in performance, inadequate information supplied, or a use error caused by ergonomic features)?", 12).RequiredIn("open");
                s.YesNo("q_causal", "2. Is a causal relationship between the device and the incident established or reasonably possible?", 12).VisibleWhen("q_incident=yes").RequiredIn("open");
                s.YesNo("q_serious", "3. Did it lead, or might it have led, to death, a serious deterioration in health, or a serious public health threat?", 12)
                    .VisibleWhen("q_incident=yes&&q_causal=yes").RequiredIn("open");
                s.Keyword("vig_severity", "4. Which applies?", "vigilance_severity", 12).VisibleWhen("q_incident=yes&&q_causal=yes&&q_serious=yes").RequiredIn("open");
            }, icon: "bi-signpost-split")
            .Section("vig_conclusion", "Conclusion", s =>
            {
                s.YesNo("vig_reportable", "Reportable serious incident?").ReadOnly().Header();
                s.Number("vig_days", "Report within (days)").ReadOnly().Header();
                s.TextArea("vig_rationale", "Rationale").RequiredIn("open");
            }, icon: "bi-check2-square")
            .Workflow("vig_tree", "Vigilance decision tree", w =>
            {
                w.Start("open", "Open").Purpose("Answer the serious-incident questions and explain the decision.").DueDays(2).Edit("vig_questions", "vig_conclusion");
                w.Phase("approval", "Regulatory Approval").Purpose("Regulatory affairs approves the vigilance decision.")
                    .AssignGroup("regulatory").DueDays(1).ReadOnlyRest();
                w.Completed("completed", "Completed");
                w.Voided();
                w.Forward("open", "approval", label: "Send for regulatory approval");
                w.Void("open");
                w.Forward("approval", "completed", label: "Approve decision", sign: "Vigilance decision approved");
                w.Back("approval", "open");
            }));

        app.View("mine", "My complaints", v => v.Category("1. My work").Forms("complaint").Mine()
            .Columns("number", "title", "phase", "due", "category", "report_due").Sort("due", false).Default());
        app.View("open_by_assignee", "Open by assignee", v => v.Category("2. Complaints").Forms("complaint").Open()
            .Columns("number", "assignees", "title", "phase", "due", "category"));
        app.View("approaching", "Approaching reporting deadline", v => v.Category("2. Complaints").Forms("complaint").Open()
            .Filter("clock_status", "ne", "none").Filter("clock_status", "ne", "submitted").Filter("clock_status", "ne", "running")
            .Columns("number", "title", "report_due", "clock_status", "report_rule", "mdr_decision", "owner").Sort("report_due", false));
        app.View("clocks", "All running clocks", v => v.Category("2. Complaints").Forms("complaint").Open().Filter("clock_status", "ne", "none").Filter("clock_status", "ne", "submitted")
            .Columns("number", "title", "awareness_date", "report_due", "clock_status", "mdr_decision", "vigilance_decision").Sort("report_due", false));
        app.View("all", "All complaints", v => v.Category("2. Complaints").Forms("complaint").All()
            .Columns("number", "title", "phase", "category", "product", "failure_mode", "site_reported", "created").Sort("created"));
        app.View("investigations_by_site", "Investigations by site", v => v.Category("2. Complaints").Forms("complaint").Open().Phases("investigation")
            .Columns("site_reported", "number", "title", "due", "owner", "failure_mode").Sort("site_reported", false));
        app.View("patient", "Patient involvement", v => v.Category("2. Complaints").Forms("complaint").All().Filter("patient_involved", "eq", "yes")
            .Columns("number", "title", "injury", "death", "mdr_decision", "phase", "created").Sort("created"));
        app.View("awaiting_closure", "Awaiting closure", v => v.Category("2. Complaints").Forms("complaint").Open().Phases("awaiting_closure")
            .Columns("number", "title", "due", "owner", "conclusion"));
        app.View("open_mdr", "Open MDR decision trees", v => v.Category("3. Decision trees").Forms("mdr_decision_tree").Open()
            .Columns("number", "title", "phase", "due", "assignees", "mdr_reportable"));
        app.View("open_vig", "Open vigilance decision trees", v => v.Category("3. Decision trees").Forms("vigilance_decision_tree").Open()
            .Columns("number", "title", "phase", "due", "assignees", "vig_reportable"));
        app.View("reportable", "Reportable events", v => v.Category("3. Decision trees").Forms("mdr_decision_tree").Completed().Filter("mdr_reportable", "eq", "yes")
            .Columns("number", "title", "mdr_report_type", "modified").Sort("modified"));
        app.View("closed", "Closed complaints", v => v.Category("4. Closed").Forms("complaint").Completed()
            .Columns("number", "title", "category", "conclusion", "mdr_decision", "modified").Sort("modified"));
        app.View("voided", "Voided complaints", v => v.Category("4. Closed").Forms("complaint").Voided().Columns("number", "title", "owner", "modified"));
        app.View("actions", "Complaint action items", v => v.Category("5. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee"));
    }

    public async Task SeedDemoAsync(DemoContext d)
    {
        var db = d.Db;
        var customerForm = d.Config.FormsByKey.GetValueOrDefault("customer");
        async Task<Guid?> Customer(string name) => customerForm == null ? null : await db.RecordValues
            .Where(v => v.FieldKey == "customer_name" && v.Text == name && db.Records.Any(r => r.Id == v.RecordId && r.FormId == customerForm.Id))
            .Select(v => (Guid?)v.RecordId).FirstOrDefaultAsync();
        var records = d.Services.GetRequiredService<RecordService>();

        // 1. Reportable (30-day) complaint whose MDR deadline is 3 days away; investigation underway.
        var c1 = await d.CreateAsync("complaint", new()
        {
            ["subject"] = "VX-200 did not alarm on downstream occlusion; patient received delayed therapy",
            ["category"] = "adverse_event", ["owner"] = d.User("jlee"), ["received_on"] = DemoContext.Days(-26), ["awareness_date"] = DemoContext.Days(-27),
            ["site_reported"] = "kalamazoo", ["channel"] = "phone", ["customer"] = await Customer("St. Mary's Regional Medical Center"),
            ["description"] = "ICU nurse found the IV line kinked under the patient with no occlusion alarm. Heparin infusion was interrupted for about 40 minutes; the patient needed an extra aPTT check and a dose adjustment.",
            ["product"] = "VX-200 infusion pump", ["model"] = "VX-200-US", ["lot_number"] = "VX2-24180", ["serial_number"] = "VX2-24180-0412",
            ["udi"] = "(01)00812345000123(21)VX2-24180-0412", ["failure_mode"] = "occlusion_alarm", ["device_returned"] = true,
            ["reporter_name"] = "Tom Becker", ["reporter_type"] = "hcp", ["reporter_organization"] = "St. Mary's Regional Medical Center", ["reporter_country"] = "USA",
            ["patient_involved"] = true, ["patient_identifier"] = "J.D. / MRN withheld", ["patient_age"] = 67m, ["patient_sex"] = "male",
            ["injury"] = true, ["death"] = false, ["medical_intervention"] = true, ["patient_outcome"] = "Recovered; additional lab draw and dose adjustment.",
        }, "jlee", daysAgo: 26, rows: new()
        {
            ["diligence_log"] =
            [
                new() { ["dl_date"] = DemoContext.Days(-24), ["dl_method"] = "phone", ["dl_contact"] = "Tom Becker (ICU)", ["dl_outcome"] = "Confirmed pump settings and line set lot AS-2409", ["dl_info_obtained"] = true },
                new() { ["dl_date"] = DemoContext.Days(-20), ["dl_method"] = "email", ["dl_contact"] = "Dr. Angela Ruiz (biomed)", ["dl_outcome"] = "Requested pump event log export", ["dl_info_obtained"] = false },
            ],
        });
        await d.RouteAsync(c1.Id, "investigation");
        var tree1 = (await records.ChildrenAsync(c1.Id, "mdr_decision_tree")).First();
        await d.UpdateAsync(tree1.Id, new()
        {
            ["q_death"] = false, ["q_serious_injury"] = true, ["q_remedial"] = false,
            ["mdr_rationale"] = "Interrupted anticoagulant therapy needed medical intervention to prevent impairment: serious injury under 21 CFR 803.3. No public-health remedial action needed beyond the CAPA.",
        }, "jlee");
        await d.RouteAsync(tree1.Id, "approval");
        await d.RouteAsync(tree1.Id, "completed", asUser: "rnovak");
        await d.UpdateAsync(c1.Id, new() { ["investigation_summary"] = "Returned pump reproduced the failure: occlusion alarm latency 95 s. Force sensor lot FS-0824 implicated (see NCR).", ["conclusion"] = "confirmed", ["harm_severity"] = "serious" }, "jlee");

        // 2. Reportable 5-day event: deadline missed (critical alerts raised by the clock job).
        var c2 = await d.CreateAsync("complaint", new()
        {
            ["subject"] = "Particulate found in ST-40 tubing set fluid path",
            ["category"] = "packaging", ["owner"] = d.User("jlee"), ["received_on"] = DemoContext.Days(-9), ["awareness_date"] = DemoContext.Days(-12),
            ["site_reported"] = "galway", ["channel"] = "email", ["customer"] = await Customer("MedSupply Europe B.V."), ["eu_market"] = true,
            ["description"] = "Distributor reports visible black particles inside sealed pouches of ST-40 lot 24-127 (the EO lot under QE investigation). Three sets affected.",
            ["product"] = "ST-40 sterile tubing set", ["lot_number"] = "24-127", ["quantity"] = 3m, ["failure_mode"] = "contamination", ["device_returned"] = true,
            ["reporter_name"] = "Joost de Vries", ["reporter_type"] = "distributor", ["reporter_organization"] = "MedSupply Europe B.V.", ["reporter_country"] = "Netherlands",
            ["patient_involved"] = false,
        }, "jlee", daysAgo: 9);
        await d.RouteAsync(c2.Id, "investigation");
        var tree2 = (await records.ChildrenAsync(c2.Id, "mdr_decision_tree")).First();
        await d.UpdateAsync(tree2.Id, new()
        {
            ["q_death"] = false, ["q_serious_injury"] = false, ["q_malfunction"] = true, ["q_malfunction_harm"] = true, ["q_remedial"] = true,
            ["mdr_rationale"] = "Particulate in a sterile fluid path could cause embolism if it recurred; lot-wide removal (remedial action) is under evaluation: 5-day report.",
        }, "jlee");
        await d.RouteAsync(tree2.Id, "approval");
        await d.RouteAsync(tree2.Id, "completed", asUser: "rnovak");
        var vig2 = (await records.ChildrenAsync(c2.Id, "vigilance_decision_tree")).FirstOrDefault();
        if (vig2 != null)
        {
            await d.UpdateAsync(vig2.Id, new() { ["q_incident"] = true, ["q_causal"] = true, ["q_serious"] = true, ["vig_severity"] = "other_serious",
                ["vig_rationale"] = "Particulate could have led to serious deterioration in health; no deaths or public health threat." }, "jlee");
            await d.RouteAsync(vig2.Id, "approval");
        }

        // 3. Closed, not reportable, with signatures.
        var c3 = await d.CreateAsync("complaint", new()
        {
            ["subject"] = "VX-200 display backlight flickers at low brightness",
            ["category"] = "product_quality", ["owner"] = d.User("sortiz"), ["received_on"] = DemoContext.Days(-60), ["awareness_date"] = DemoContext.Days(-60),
            ["site_reported"] = "boston", ["channel"] = "field_service", ["customer"] = await Customer("Boston General Hospital"),
            ["description"] = "Biomed reports backlight flicker on one pump at the lowest brightness setting. No effect on infusion.",
            ["product"] = "VX-200 infusion pump", ["serial_number"] = "VX2-23977-0088", ["failure_mode"] = "display", ["device_returned"] = true,
            ["reporter_name"] = "Priya Raman", ["reporter_type"] = "biomed", ["patient_involved"] = false,
        }, "sortiz", daysAgo: 60);
        await d.RouteAsync(c3.Id, "investigation");
        var tree3 = (await records.ChildrenAsync(c3.Id, "mdr_decision_tree")).First();
        await d.UpdateAsync(tree3.Id, new()
        {
            ["q_death"] = false, ["q_serious_injury"] = false, ["q_malfunction"] = true, ["q_malfunction_harm"] = false,
            ["mdr_rationale"] = "Cosmetic display malfunction; screen stays readable and infusion is unaffected. Recurrence wouldn't cause death or serious injury.",
        }, "sortiz");
        await d.RouteAsync(tree3.Id, "approval");
        await d.RouteAsync(tree3.Id, "completed", asUser: "rnovak");
        await d.UpdateAsync(c3.Id, new()
        {
            ["investigation_summary"] = "Reproduced: PWM dimming at 5% duty cycle causes visible flicker on display lot D-2207. Firmware 4.3 raises the minimum duty cycle.",
            ["conclusion"] = "confirmed", ["root_cause_category"] = "design", ["root_cause"] = "Minimum PWM duty cycle too low for display lot D-2207.",
            ["capa_required"] = false, ["capa_justification"] = "Already addressed by firmware 4.3 (change CC-0398); no further action.",
            ["ncr_required"] = false, ["ncr_justification"] = "No nonconforming product: within display specification.",
            ["response_text"] = "Thank you for the report. Firmware 4.3, available now, fixes the flicker; your field service engineer will update the pumps.",
            ["response_sent_on"] = DemoContext.Days(-40),
        }, "sortiz");
        await d.RouteAsync(c3.Id, "review", assignees: ["dokafor"]);
        await d.RouteAsync(c3.Id, "awaiting_closure");
        await d.RouteAsync(c3.Id, "closed");

        // 4. Draft, just received.
        await d.CreateAsync("complaint", new()
        {
            ["subject"] = "Luer connector cracked on VX-200 administration set",
            ["category"] = "product_quality", ["owner"] = d.User("jlee"), ["awareness_date"] = DemoContext.Days(-1),
            ["site_reported"] = "boston", ["channel"] = "sales_rep", ["customer"] = await Customer("Boston General Hospital"),
            ["description"] = "Sales rep reports four administration sets with cracked female luers at the pump inlet, found during set-up. No patient involvement.",
            ["product"] = "VX-200 administration set", ["lot_number"] = "AS-2409", ["quantity"] = 4m, ["failure_mode"] = "mechanical",
            ["reporter_name"] = "Chris Dalton", ["reporter_type"] = "sales", ["patient_involved"] = false,
        }, "jlee", daysAgo: 1);

        // 5. Older complaint in Review with a running clock already submitted.
        var c5 = await d.CreateAsync("complaint", new()
        {
            ["subject"] = "Occlusion alarm delayed on VX-200 during home infusion",
            ["category"] = "adverse_event", ["owner"] = d.User("jlee"), ["received_on"] = DemoContext.Days(-35), ["awareness_date"] = DemoContext.Days(-36),
            ["site_reported"] = "kalamazoo", ["channel"] = "phone",
            ["description"] = "Home-care nurse reports the pump alarmed about 2 minutes after the line was clamped. Patient had a short delay in antibiotic therapy; no injury.",
            ["product"] = "VX-200 infusion pump", ["lot_number"] = "VX2-24180", ["serial_number"] = "VX2-24180-0377", ["failure_mode"] = "occlusion_alarm",
            ["reporter_type"] = "hcp", ["patient_involved"] = true, ["patient_age"] = 54m, ["patient_sex"] = "female", ["injury"] = false, ["death"] = false,
        }, "jlee", daysAgo: 35);
        await d.RouteAsync(c5.Id, "investigation");
        var tree5 = (await records.ChildrenAsync(c5.Id, "mdr_decision_tree")).First();
        await d.UpdateAsync(tree5.Id, new()
        {
            ["q_death"] = false, ["q_serious_injury"] = false, ["q_malfunction"] = true, ["q_malfunction_harm"] = true, ["q_remedial"] = false,
            ["mdr_rationale"] = "Alarm latency malfunction could contribute to serious injury if it recurred with a critical drug: 30-day malfunction report.",
        }, "jlee");
        await d.RouteAsync(tree5.Id, "approval");
        await d.RouteAsync(tree5.Id, "completed", asUser: "rnovak");
        await d.UpdateAsync(c5.Id, new()
        {
            ["fda_submitted_on"] = DemoContext.Days(-8), ["report_reference"] = "MFR 1834567-2026-00041",
            ["investigation_summary"] = "Same failure signature as CMP-00001 (force sensor lot FS-0824).", ["conclusion"] = "confirmed",
            ["capa_required"] = true, ["ncr_required"] = false, ["ncr_justification"] = "Covered by the force sensor NCR.",
            ["response_text"] = "We confirmed the issue and are replacing the pump; a corrective action is underway.", ["response_sent_on"] = DemoContext.Days(-6),
        }, "jlee");
        var capa = await d.Db.Records.Where(r => r.Title.StartsWith("8D: Occlusion alarm")).Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
        if (capa is { } capaId) await QualityEventsApp.LinkAsync(d, c5.Id, capaId, "capa_record");
        await d.RouteAsync(c5.Id, "review", assignees: ["sortiz"]);

        // Run the reporting-clock job once so statuses and safety-critical alerts reflect today.
        d.Services.GetRequiredService<Engine.Security.CurrentUser>().UseSystem("Regulatory reporting clocks");
        await d.Services.GetServices<Infrastructure.Jobs.IScheduledJob>().OfType<RegulatoryClockJob>().First()
            .RunAsync(new Infrastructure.Jobs.JobContext(d.Services, true), CancellationToken.None);

        // Insights: the "repeat complaints → CAPA" alert (RPT-603) watches these complaints.
        await Insights.InsightsDemo.SeedAsync(d);
    }
}

public sealed class ComplaintBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["complaint"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        if (m.Date("awareness_date") is { } aw && aw > BusinessCalendar.Today)
            ctx.Error("awareness_date", "The awareness date can't be in the future.");

        // Decisions default from the trees; the EU decision only applies to devices on the EU market.
        if (ctx.KeywordKey("mdr_decision") == null) ctx.Set("mdr_decision", "pending");
        var eu = m.IsYes("eu_market");
        var vig = ctx.KeywordKey("vigilance_decision");
        if (!eu) ctx.Set("vigilance_decision", "not_applicable");
        else if (vig is null or "not_applicable") ctx.Set("vigilance_decision", "pending");

        // CMP-009: the reporting clock.
        var clock = await ReportingClock.ComputeAsync(m.Date("awareness_date"), ctx.KeywordKey("mdr_decision"), eu, ctx.KeywordKey("vigilance_decision"),
            m.Date("fda_submitted_on"), m.Date("eu_submitted_on"), ctx.Services.GetRequiredService<BusinessCalendar>());
        var next = clock.Where(c => c.SubmittedOn == null).OrderBy(c => c.Due).FirstOrDefault();
        ctx.Set("report_due", next?.Due);
        ctx.Set("report_rule", next?.Rule);
        ctx.Set("clock_status", ReportingClock.Status(clock, BusinessCalendar.Today));
    }

    /// <summary>SAF-023: late entry is flagged; possible serious injuries or deaths alert regulatory affairs straight away (SAF-020).</summary>
    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var audit = ctx.Get<AuditService>();
        if (s.Date("awareness_date") is { } aw)
        {
            var entered = DateOnly.FromDateTime(s.Record.CreatedAt);
            var late = entered.DayNumber - aw.DayNumber;
            if (late > ReportingClock.LateEntryDays)
            {
                audit.Record(s.Record.Id, "Late entry flagged", $"Entered {late} days after the awareness date ({aw:dd MMM yyyy}). Reporting clocks run from awareness (SAF-023).");
                ctx.Db.Comments.Add(new Comment { RecordId = s.Record.Id, IsSystem = true, Kind = "system", At = DateTime.UtcNow,
                    Text = $"Late entry: logged {late} days after Acme became aware of the event. Reporting deadlines are calculated from the awareness date." });
            }
        }
        if (s.Bool("patient_involved") == true && (s.Bool("death") == true || s.Bool("injury") == true))
        {
            var who = ctx.Directory.ActiveMembers("regulatory").Select(u => u.Id).Append(s.UserId("owner") ?? ctx.Me.Id);
            ctx.Get<NotificationService>().SafetyCritical(who, $"Potentially reportable complaint {s.Record.Number}",
                $"{s.Record.Title}. {(s.Bool("death") == true ? "Death" : "Injury")} reported; awareness date {s.Date("awareness_date"):dd MMM yyyy}. FDA MDR clock is running.", s.Record.Id);
        }
        await ctx.Db.SaveChangesAsync();
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.Kind != RouteKind.Forward) return;
        if (ctx.To.Key == "awaiting_closure")
        {
            // Every complaint gets a completed MDR decision, even when Investigation was skipped.
            if (!ctx.Config.FormsByKey.TryGetValue("mdr_decision_tree", out var mdr) ||
                !await ctx.Db.Records.AnyAsync(r => r.ParentId == s.Record.Id && r.FormId == mdr.Id && r.PhaseType == PhaseType.Completed))
                ctx.Error("Complete an MDR decision tree before approving the complaint. Create one from Create › MDR Decision Tree.");
            await QualityLinks.CheckRequiredGateAsync(ctx, "capa_required", "capa_justification", "capa_record", "capa", "CAPA");
            await QualityLinks.CheckRequiredGateAsync(ctx, "ncr_required", "ncr_justification", "ncr_record", "ncr", "NCR");
            // SAF-022: a reportable event can't move to closure before it's reported.
            var clock = await ReportingClock.ForAsync(ctx);
            foreach (var c in clock.Where(c => !c.Provisional && c.SubmittedOn == null))
                ctx.Error($"{c.Rule} is due {c.Due:dd MMM yyyy} and hasn't been submitted. Record the submission first (SAF-022).");
        }
    }

    /// <summary>CMP-003: entering Investigation starts the MDR (and EU vigilance) decision trees.</summary>
    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "investigation") return;
        var s = ctx.Snapshot;
        var owner = s.UserId("owner") ?? ctx.Me.Id;
        await EnsureTreeAsync(ctx, "mdr_decision_tree", $"MDR decision: {s.Record.Number}", owner);
        if (s.Bool("eu_market") == true) await EnsureTreeAsync(ctx, "vigilance_decision_tree", $"EU vigilance decision: {s.Record.Number}", owner);
    }

    private static async Task EnsureTreeAsync(RecordContext ctx, string formKey, string subject, int owner)
    {
        var form = ctx.Config.Form(formKey);
        if (await ctx.Db.Records.AnyAsync(r => r.ParentId == ctx.Record.Id && r.FormId == form.Id && r.PhaseType != PhaseType.Voided)) return;
        var res = await ctx.Records.CreateAsync(new CreateRequest
        {
            FormKey = formKey, ParentId = ctx.Record.Id, SkipAccessCheck = true, Assignees = [owner], Values = new() { ["subject"] = subject },
            CreateComment = $"Started automatically when {ctx.Record.Number} entered Investigation (CMP-003)",
        });
        if (!res.Ok) throw new WorkflowException($"Couldn't start the {form.Name}: " + string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var list = new List<RecordAction>();
        if (!ctx.IsOpen()) return list;
        var canAct = await ctx.CanActAsync();
        if (canAct && s.PhaseKey is "investigation" or "review")
        {
            if (s.Bool("capa_required") == true && (await QualityLinks.LinkedAsync(ctx, "capa_record", "capa")).Count == 0)
                list.Add(new() { Key = "cmp-capa", Label = "Initiate CAPA", Icon = "bi-shield-plus", Group = "Create", Description = "Create the CAPA this complaint needs (QE-005)." });
            if (s.Bool("ncr_required") == true && (await QualityLinks.LinkedAsync(ctx, "ncr_record", "ncr")).Count == 0)
                list.Add(new() { Key = "cmp-ncr", Label = "Initiate NCR", Icon = "bi-exclamation-octagon", Group = "Create", Description = "Create the NCR for the complained-about product (NC-006)." });
            if (s.Links("rma_record").Count == 0 && ctx.Config.FormsByKey.ContainsKey("rma"))
                list.Add(new() { Key = "cmp-rma", Label = "Create RMA", Icon = "bi-box-arrow-in-left", Group = "Create", Description = "Authorize the return of the complained-about device (RMA-005)." });
        }
        // Regulatory affairs records submissions (e-signed) on any open complaint before closure.
        var regulatory = ctx.Directory.IsMember(ctx.Me.Id, "regulatory") || ctx.Access.RoleFor(ctx.Record.AppId) >= AppRole.Manager;
        if (regulatory && s.PhaseKey is "draft" or "investigation" or "review")
        {
            var pending = (await ReportingClock.ForAsync(ctx)).Where(c => !c.Provisional && c.SubmittedOn == null).ToList();
            if (pending.Count > 0)
                list.Add(new()
                {
                    Key = "cmp-submit", Label = "Record regulatory submission", Icon = "bi-send-check", Group = "Regulatory", SignatureMeaning = "Submitted to regulator",
                    Description = "Record that the MDR or vigilance report was filed. This stops the reporting clock for that jurisdiction.",
                    Inputs =
                    [
                        new ActionField("agency", "Report", "select", true, Options: pending.Select(p => (p.Jurisdiction, p.Rule)).ToList(), Default: pending[0].Jurisdiction),
                        new ActionField("date", "Submitted on", "date", true, Default: BusinessCalendar.Today.ToString("yyyy-MM-dd")),
                        new ActionField("reference", "Report number", Required: true, Help: "For example MFR 1834567-2026-00042 or the MIR reference."),
                    ],
                });
        }
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if ((await ActionsAsync(ctx)).All(a => a.Key != actionKey)) return ActionOutcome.Fail("This action isn't available.");
        var s = ctx.Snapshot;
        if (actionKey == "cmp-submit")
        {
            var agency = input.Get("agency");
            if (agency is not ("FDA" or "EU")) return ActionOutcome.Fail("Choose which report was submitted.");
            if (!DateOnly.TryParse(input.Get("date"), out var date) || date > BusinessCalendar.Today) return ActionOutcome.Fail("The submission date can't be in the future.");
            var reference = input.Get("reference")?.Trim();
            if (string.IsNullOrEmpty(reference)) return ActionOutcome.Fail("Enter the report number.");
            return await Tx.RunAsync(ctx.Db, async () =>
            {
                var sig = await ctx.Get<SignatureService>().SignAsync(s, "Submitted to regulator", $"Record {agency} submission {reference}", input.Password, input.Certify,
                    null, ctx.Records.ComputeHash, input.Comment);
                if (!sig.Ok) return ActionOutcome.Fail(sig.Error!);
                var refs = string.Join("; ", new[] { s.Text("report_reference"), reference }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var upd = await ctx.Records.UpdateAsync(s.Record.Id, new() { [agency == "FDA" ? "fda_submitted_on" : "eu_submitted_on"] = date, ["report_reference"] = refs },
                    $"{agency} report {reference} submitted");
                if (!upd.Ok) return ActionOutcome.Fail(upd.Errors.Select(e => e.Message).ToArray());
                ctx.Get<AuditService>().Record(s.Record.Id, "Regulatory report submitted", $"{agency} report {reference} submitted on {date:dd MMM yyyy}");
                await ctx.Db.SaveChangesAsync();
                return ActionOutcome.Ok($"{agency} submission recorded.");
            }, o => o.Success);
        }
        var (rec, err) = actionKey switch
        {
            "cmp-capa" => await QualityLinks.CreateFromAsync(ctx, "capa", new()
            {
                ["subject"] = s.Text("subject"), ["capa_type"] = "corrective", ["source"] = "customer_complaint",
                ["priority"] = s.Bool("injury") == true || s.Bool("death") == true ? "critical" : "high", ["owner"] = ctx.Me.Id,
                ["source_record"] = s.Record.Id, ["product"] = s.Text("product"), ["description"] = s.Text("description"),
            }, "capa_record"),
            "cmp-ncr" => await QualityLinks.CreateFromAsync(ctx, "ncr", new()
            {
                ["subject"] = s.Text("subject"), ["nc_type"] = "customer_return", ["owner"] = ctx.Me.Id, ["source_record"] = s.Record.Id,
                ["customer"] = s.Data.Main.Link("customer"), ["customer_reference"] = s.Record.Number, ["part_number"] = s.Text("model") ?? s.Text("product"),
                ["part_description"] = s.Text("product"), ["lot_number"] = s.Text("lot_number"), ["quantity_affected"] = s.Number("quantity"), ["description"] = s.Text("description"),
            }, "ncr_record"),
            "cmp-rma" => await QualityLinks.CreateFromAsync(ctx, "rma", new()
            {
                ["subject"] = $"Return for {s.Record.Number}: {s.Text("product")}", ["customer"] = s.Data.Main.Link("customer"), ["owner"] = ctx.Me.Id,
                ["complaint"] = s.Record.Id, ["rma_reason"] = "defective", ["customer_contact"] = s.Text("reporter_name"), ["description"] = s.Text("description"),
            }, "rma_record", rows: new()
            {
                ["rma_parts"] = [new() { ["part_number"] = s.Text("model") ?? s.Text("product"), ["part_description"] = s.Text("product"), ["qty_authorized"] = s.Number("quantity") ?? 1m, ["unit_value"] = 0m }],
            }),
            _ => ((Record?)null, (string?)"This action isn't available."),
        };
        return rec == null ? ActionOutcome.Fail(err!) : ActionOutcome.Ok($"{rec.Number} created and linked to {s.Record.Number}.", rec.Id);
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var clock = await ReportingClock.ForAsync(ctx);
        var late = s.Date("awareness_date") is { } aw ? DateOnly.FromDateTime(s.Record.CreatedAt).DayNumber - aw.DayNumber : 0;
        var trees = new List<(Guid, string, string, string?, PhaseType)>();
        foreach (var key in new[] { "mdr_decision_tree", "vigilance_decision_tree" })
            if (ctx.Config.FormsByKey.TryGetValue(key, out var tf))
                foreach (var t in await ctx.Db.Records.AsNoTracking().Where(r => r.ParentId == s.Record.Id && r.FormId == tf.Id).OrderBy(r => r.CreatedAt).ToListAsync())
                    trees.Add((t.Id, t.Number, tf.Name, ctx.Config.Phase(t.PhaseId)?.Name, t.PhaseType));
        var model = new ReportingClockModel(clock, s.Keyword("mdr_decision"), s.Keyword("vigilance_decision"), s.Keyword("clock_status"), s.Date("awareness_date"),
            late > ReportingClock.LateEntryDays ? late : null, trees, BusinessCalendar.Today, s.IsTerminal);
        var similar = await SimilarComplaints.FindAsync(ctx);
        var panels = new List<RecordPanel>
        {
            new() { Key = "cmp-clock", Title = "Regulatory reporting clock", Placement = "top", Partial = "~/Modules/Quality/Views/_ReportingClock.cshtml", Model = model },
        };
        if (similar.Items.Count > 0)
            panels.Add(new() { Key = "cmp-similar", Title = "Similar complaints (12 months)", Icon = "bi-graph-up-arrow", Placement = "side", Order = 20, Count = similar.Items.Count,
                Partial = "~/Modules/Quality/Views/_SimilarComplaints.cshtml", Model = similar });
        return panels;
    }
}

/// <summary>Works out the MDR / vigilance conclusion as the questions are answered, and copies it to the complaint on completion.</summary>
public sealed class DecisionTreeBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["mdr_decision_tree", "vigilance_decision_tree"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        if (ctx.Form.Key == "mdr_decision_tree")
        {
            bool? reportable = m.Bool("q_death") switch
            {
                true => true,
                false => m.Bool("q_serious_injury") switch
                {
                    true => true,
                    false => m.Bool("q_malfunction") switch { false => false, true => m.Bool("q_malfunction_harm"), _ => null },
                    _ => null,
                },
                _ => null,
            };
            ctx.Set("mdr_reportable", reportable);
            ctx.Set("mdr_report_type", reportable switch
            {
                true => m.Bool("q_remedial") == true ? "five_day" : "thirty_day",
                false => "not_reportable",
                _ => null,
            });
        }
        else
        {
            bool? reportable = m.Bool("q_incident") switch
            {
                false => false,
                true => m.Bool("q_causal") switch { false => false, true => m.Bool("q_serious") switch { false => false, true => true, _ => null }, _ => null },
                _ => null,
            };
            var days = reportable == true ? ctx.KeywordKey("vig_severity") switch { "public_health_threat" => 2, "death_serious" => 10, "other_serious" => 15, _ => (int?)null } : null;
            ctx.Set("vig_reportable", reportable == true && days == null ? null : reportable);
            ctx.Set("vig_days", days);
        }
        return Task.CompletedTask;
    }

    public override async Task OnCreatedAsync(RecordContext ctx)
    {
        if (ctx.Record.ParentId is not { } pid) return;
        var parent = await ctx.Db.Records.AsNoTracking().Where(r => r.Id == pid).Select(r => r.Number).FirstOrDefaultAsync();
        var label = ctx.Snapshot.Form.Key == "mdr_decision_tree" ? "MDR decision" : "EU vigilance decision";
        if (parent != null && ctx.Snapshot.Text("subject") is null or "MDR reportability decision" or "EU vigilance decision")
            await ctx.Records.UpdateAsync(ctx.Record.Id, new() { ["subject"] = $"{label}: {parent}" });
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key == "approval")
        {
            var concluded = ctx.Snapshot.Form.Key == "mdr_decision_tree" ? ctx.Snapshot.Bool("mdr_reportable") : ctx.Snapshot.Bool("vig_reportable");
            if (concluded == null) ctx.Error("Answer the questions until the tree reaches a conclusion.");
        }
        return Task.CompletedTask;
    }

    /// <summary>On completion the decision drives the complaint's reporting clock (CMP-009); reportable events alert regulatory affairs (SAF-020).</summary>
    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "completed" || ctx.Record.ParentId is not { } parentId) return;
        var s = ctx.Snapshot;
        string decision;
        string field;
        if (s.Form.Key == "mdr_decision_tree")
        {
            field = "mdr_decision";
            decision = s.KeywordKey("mdr_report_type") switch { "five_day" => "reportable_5", "thirty_day" => "reportable_30", _ => "not_reportable" };
        }
        else
        {
            field = "vigilance_decision";
            decision = s.Bool("vig_reportable") == true ? $"reportable_{(int)(s.Number("vig_days") ?? 15)}" : "not_reportable";
        }
        var res = await ctx.Records.UpdateAsync(parentId, new() { [field] = decision }, $"{s.Form.Name} {s.Record.Number} completed");
        if (!res.Ok) throw new WorkflowException("Couldn't update the complaint: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        ctx.Get<AuditService>().Record(parentId, "Reportability decided", $"{s.Form.Name} {s.Record.Number}: {ctx.Config.Keyword(field, decision)?.Label}");
        if (decision.StartsWith("reportable"))
        {
            var complaint = await ctx.Records.LoadRequiredAsync(parentId);
            var who = ctx.Directory.ActiveMembers("regulatory").Select(u => u.Id).Append(complaint.UserId("owner") ?? ctx.Me.Id);
            ctx.Get<NotificationService>().SafetyCritical(who, $"Reportable event: {complaint.Record.Number}",
                $"{complaint.Record.Title}. {ctx.Config.Keyword(field, decision)?.Label}. Next report due {complaint.Date("report_due"):dd MMM yyyy} ({complaint.Text("report_rule")}).", parentId);
        }
    }
}

/// <summary>CMP-010: signal detection on the record (same product and failure mode, or same lot, in the last 12 months).</summary>
public static class SimilarComplaints
{
    public static async Task<SimilarComplaintsModel> FindAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var product = s.Text("product");
        var lot = s.Text("lot_number");
        var mode = s.Data.Main.Ref("failure_mode");
        var since = DateTime.UtcNow.AddMonths(-12);
        var form = s.Form;
        var q = ctx.Access.Readable().AsNoTracking().Where(r => r.FormId == form.Id && r.Id != s.Record.Id && r.CreatedAt >= since && r.PhaseType != PhaseType.Voided);
        var sameLot = lot == null ? [] : await q.Where(r => ctx.Db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "lot_number" && v.Text == lot)).ToListAsync();
        var sameMode = product == null || mode == null ? [] : await q.Where(r =>
            ctx.Db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "product" && v.Text == product) &&
            ctx.Db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "failure_mode" && v.RefId == mode)).ToListAsync();
        var items = sameLot.Select(r => (r, "Same lot")).Concat(sameMode.Select(r => (r, "Same failure mode")))
            .GroupBy(x => x.r.Id).Select(g => new SimilarComplaint(g.First().r.Id, g.First().r.Number, g.First().r.Title, string.Join(" · ", g.Select(x => x.Item2).Distinct()),
                ctx.Config.Phase(g.First().r.PhaseId)?.Name, g.First().r.PhaseType, g.First().r.CreatedAt))
            .OrderByDescending(x => x.Created).ToList();
        return new SimilarComplaintsModel(items, items.Count >= SignalThreshold - 1, s.Keyword("failure_mode")?.Label, lot);
    }

    /// <summary>Three complaints (including this one) with the same lot or failure mode in 12 months is a signal.</summary>
    public const int SignalThreshold = 3;
}

public sealed record SimilarComplaint(Guid Id, string Number, string Title, string Match, string? PhaseName, PhaseType PhaseType, DateTime Created);
public sealed record SimilarComplaintsModel(List<SimilarComplaint> Items, bool Signal, string? FailureMode, string? Lot);
