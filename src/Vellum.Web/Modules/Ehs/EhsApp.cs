using Vellum.Domain;
using Vellum.Modules.Kit;
using Vellum.Packaging;

namespace Vellum.Modules.Ehs;

/// <summary>
/// Environment, Health &amp; Safety (EHS §17): incidents with OSHA recordkeeping and severe-injury reporting clocks (EHS-101..114),
/// hours worked for incidence rates (EHS-113, RPT-702), job safety analyses (EHS §17.3) and work permits (EHS §17.4).
/// </summary>
public sealed class EhsApp : IPackagedApp
{
    public string Key => "ehs";
    public int Order => 80;

    public void Define(AppBuilder app)
    {
        app.Name("Environment, Health & Safety")
            .Description("Report incidents and near misses, keep the OSHA log, analyze job hazards and control work permits.")
            .Icon("bi-cone-striped").Color("#ea580c").Suite("EHS").Order(10).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@ehs", AppRole.Manager)
            .Grant("mchen", AppRole.Manager, designer: true)
            // Anyone can report an incident or near miss (EHS-101); personal and medical details stay restricted (SEC-520).
            .Grant("@all-employees", AppRole.Author, formKey: "incident")
            .Grant("@operations", AppRole.Author, formKey: "jsa")
            .Grant("@operations", AppRole.Author, formKey: "permit")
            .Grant("@engineering", AppRole.Author, formKey: "jsa");

        Sites.Define(app);
        DefineKeywords(app);
        DefineIncident(app);
        DefineHoursWorked(app);
        DefineJsa(app);
        DefinePermit(app);
        DefineViews(app);
    }

    public Task SeedDemoAsync(DemoContext demo) => EhsDemo.SeedAsync(demo);

    private static void DefineKeywords(AppBuilder app)
    {
        app.Keywords("incident_type", "Incident type", k => k
            .Add("injury", "Injury", "danger").Add("illness", "Illness", "warning").Add("near_miss", "Near miss", "info")
            .Add("environmental", "Environmental", "success").Add("property_damage", "Property damage", "secondary"));
        app.Keywords("incident_outcome", "Actual outcome", k => k
            .Add("none", "No injury or damage", "secondary").Add("first_aid", "First aid only", "info")
            .Add("medical", "Medical treatment", "warning").Add("restricted", "Restricted work or transfer", "warning")
            .Add("lost_time", "Lost time", "danger").Add("serious", "Serious injury", "danger").Add("fatality", "Fatality", "dark")
            .Add("environmental_release", "Environmental release", "success").Add("damage", "Property damage only", "secondary"));
        app.Keywords("potential_severity", "Potential severity", k => k
            .Add("low", "Low: first aid at most", "secondary").Add("moderate", "Moderate: medical treatment", "info")
            .Add("high", "High: lost time or serious injury", "warning").Add("catastrophic", "Catastrophic: fatality or permanent disability", "danger"));
        app.Keywords("osha_severe_event", "Severe outcome reportable to OSHA", k => k
            .Add("fatality", "Work-related fatality (8 hours)", "dark").Add("inpatient", "In-patient hospitalization (24 hours)", "danger")
            .Add("amputation", "Amputation (24 hours)", "danger").Add("eye_loss", "Loss of an eye (24 hours)", "danger"));
        app.Keywords("osha_classification", "OSHA case classification", k => k
            .Add("death", "Death", "dark").Add("days_away", "Days away from work", "danger")
            .Add("restricted", "Job transfer or restriction", "warning").Add("other_recordable", "Other recordable case", "info"));
        app.Keywords("osha_case_type", "Injury or illness type", k => k
            .Add("injury", "Injury").Add("skin_disorder", "Skin disorder").Add("respiratory", "Respiratory condition")
            .Add("poisoning", "Poisoning").Add("hearing_loss", "Hearing loss").Add("other_illness", "All other illnesses"));
        app.Keywords("person_category", "Person category", k => k
            .Add("employee", "Employee").Add("temporary", "Temporary worker").Add("contractor", "Contractor").Add("visitor", "Visitor"));
        app.Keywords("body_part", "Body part", k => k
            .Add("head", "Head").Add("eye", "Eye").Add("face", "Face").Add("neck", "Neck").Add("back", "Back").Add("shoulder", "Shoulder")
            .Add("arm", "Arm / elbow").Add("wrist", "Wrist").Add("hand", "Hand / finger").Add("trunk", "Chest / abdomen")
            .Add("leg", "Leg / knee").Add("foot", "Foot / toe").Add("multiple", "Multiple"));
        app.Keywords("injury_nature", "Nature of injury or illness", k => k
            .Add("laceration", "Cut / laceration").Add("strain", "Strain / sprain").Add("msd", "Musculoskeletal disorder").Add("fracture", "Fracture")
            .Add("burn", "Burn").Add("contusion", "Bruise / contusion").Add("crush", "Crush").Add("amputation", "Amputation")
            .Add("foreign_body", "Foreign body").Add("chemical", "Chemical exposure").Add("hearing", "Hearing loss").Add("other", "Other"));
        app.Keywords("medical_treatment", "Medical treatment", k => k
            .Add("none", "None").Add("first_aid", "First aid on site").Add("clinic", "Physician / clinic (beyond first aid)")
            .Add("er", "Emergency room").Add("hospitalized", "Hospitalized overnight"));
        app.Keywords("root_cause_category", "Root cause category (6M)", k => k
            .Add("man", "People").Add("machine", "Machine / equipment").Add("method", "Method / process").Add("material", "Material")
            .Add("measurement", "Measurement").Add("environment", "Environment").Add("design", "Design").Add("supplier", "Supplier"));

        app.Keywords("jsa_severity", "Severity (JSA)", k => k
            .Add("sev1", "1 Negligible").Add("sev2", "2 Minor").Add("sev3", "3 Moderate").Add("sev4", "4 Major").Add("sev5", "5 Catastrophic"));
        app.Keywords("jsa_likelihood", "Likelihood (JSA)", k => k
            .Add("lik1", "1 Rare").Add("lik2", "2 Unlikely").Add("lik3", "3 Possible").Add("lik4", "4 Likely").Add("lik5", "5 Almost certain"));
        app.Keywords("jsa_risk_band", "Risk band (JSA)", k => k
            .Add("low", "Low", "success").Add("medium", "Medium", "info").Add("high", "High", "warning").Add("extreme", "Extreme", "danger"));

        app.Keywords("permit_type", "Work permit type", k => k
            .Add("ptw", "General permit to work", "secondary").Add("hot_work", "Hot work", "danger").Add("confined_space", "Confined space entry", "warning")
            .Add("height", "Working at height", "info").Add("loto", "Lockout / tagout", "primary").Add("line_break", "Line break", "violet"));
        app.Keywords("permit_validity", "Permit validity", k => k
            .Add("pending", "Not yet valid", "secondary").Add("valid", "Valid", "success").Add("expiring", "Expiring soon", "warning")
            .Add("expired", "Expired", "danger").Add("closed", "Closed out", "secondary"));
        app.Keywords("gas_result", "Gas test result", k => k.Add("pass", "Within limits", "success").Add("fail", "Outside limits", "danger"));
    }

    private static void DefineIncident(AppBuilder app)
    {
        app.Form("incident", "Incident", f => f
            .Icon("bi-bandaid")
            .Description("Injury, illness, near miss, environmental or property-damage event, with OSHA recordkeeping.")
            .Numbering("INC-")
            .Title("{subject}")
            .Section("event", "What happened", s =>
            {
                s.Text("subject", "Short description", 12).Required().MaxLength(200)
                    .Placeholder("For example: Operator's glove caught on deburring tool at cell 4");
                s.Keyword("incident_type", "Event type", "incident_type").Required().Header();
                s.DateTime("occurred_at", "Date and time it happened").Required().Header()
                    .Help("In your own time zone. OSHA reporting clocks run from this moment, not from when the report is entered (SAF-023).");
                s.Keyword("site", "Site", "ops_site").Required().Header();
                s.Text("area", "Exact location", 8).Placeholder("Building, line, room or equipment, e.g. Molding hall, Press 7");
                s.Keyword("outcome", "Actual outcome", "incident_outcome").Required().Header();
                s.TextArea("description", "What happened").Required()
                    .Help("Facts only: the task, what went wrong and the sequence of events. Don't put the injured person's name or medical details here.");
                s.Keyword("potential_severity", "Potential severity", "potential_severity").VisibleWhen("incident_type=near_miss")
                    .Help("How bad could it have been? Near misses rated High or Catastrophic get a full investigation.");
                s.Keyword("osha_severe_event", "Severe outcome reportable to OSHA", "osha_severe_event", 8)
                    .Help("Fatality: OSHA must be told within 8 hours. In-patient hospitalization, amputation or loss of an eye: within 24 hours (29 CFR 1904.39). " +
                          "Choosing one starts the reporting clock and alerts EHS and management immediately.");
                s.User("reported_by", "Reported by").Default("$me");
                s.User("supervisor", "Area supervisor").Help("The supervisor is alerted together with EHS for severe outcomes.");
                s.Users("people_involved", "People involved", 4).Help("Colleagues involved or present. The injured person's details go on the Injury tab (restricted).");
            }, icon: "bi-exclamation-diamond")
            .Section("immediate", "Immediate actions", s =>
            {
                s.TextArea("immediate_actions", "Immediate actions taken").Placeholder("First aid given, machine isolated, spill contained, area barricaded…");
                s.YesNo("area_made_safe", "Area made safe?");
                s.TextArea("suggestion", "Suggested improvement", 8).Help("Optional: how could this be prevented?");
            }, icon: "bi-shield-plus")
            .Subform("witnesses", "Witness statements", s =>
            {
                s.Text("witness", "Witness", 4).Required();
                s.TextArea("statement", "Statement", 8);
            }, rowNoun: "witness", icon: "bi-chat-quote")
            .Section("injured", "Injured or ill person", s =>
            {
                s.Prompt("injured_prompt", "Personal and medical details are visible only to the EHS team (SEC-520). For privacy-concern cases the name is replaced by \"Privacy case\" on the OSHA 300 log (29 CFR 1904.29(b)(7)).");
                s.Keyword("injured_category", "Person category", "person_category");
                s.Text("injured_name", "Name", 4).Sensitive();
                s.Text("injured_job_title", "Job title", 4);
                s.Keywords("body_part", "Body part(s)", "body_part", 4).Sensitive();
                s.Keyword("injury_nature", "Nature of injury or illness", "injury_nature").Sensitive();
                s.Keyword("medical_treatment", "Medical treatment", "medical_treatment").Sensitive();
                s.Text("treatment_provider", "Physician or clinic", 6).Sensitive();
                s.YesNo("privacy_case", "Privacy case?").Default("no")
                    .Help("Injuries to intimate body parts, sexual assault, mental illness, HIV/hepatitis/TB, needlestick with contaminated sharps, or at the employee's request.");
            }, tab: "Injury", icon: "bi-person-badge", description: "For injuries and illnesses. Completed by EHS during the investigation.")
            .Section("osha", "OSHA recordkeeping", s =>
            {
                s.YesNo("recordable", "OSHA recordable?").RequiredIn("review")
                    .Help("Recordable: work-related and results in death, days away, restricted work or transfer, medical treatment beyond first aid, loss of consciousness, or a significant diagnosed injury or illness (29 CFR 1904.7).");
                s.Keyword("osha_classification", "Case classification", "osha_classification").VisibleWhen("recordable=yes");
                s.Keyword("osha_case_type", "Injury or illness type", "osha_case_type").VisibleWhen("recordable=yes");
                s.Number("days_away", "Days away from work", 3).Min(0).Max(180).Decimals(0).Default("0").VisibleWhen("recordable=yes")
                    .Help("Calendar days, capped at 180 (29 CFR 1904.7(b)(3)).");
                s.Number("days_restricted", "Days of job transfer or restriction", 3).Min(0).Max(180).Decimals(0).Default("0").VisibleWhen("recordable=yes");
                s.YesNo("dart", "DART case", 3).ReadOnly().Help("Calculated: a recordable case with days away, restricted work or job transfer.");
                s.TextArea("recordability_notes", "Recordability decision").Help("Why the case is or isn't recordable. Kept for the OSHA log certification.");
            }, tab: "OSHA", icon: "bi-journal-medical")
            .Section("osha_report", "OSHA severe-injury report (29 CFR 1904.39)", s =>
            {
                s.DateTime("osha_deadline", "Report to OSHA by").ReadOnly().Header();
                s.Text("osha_rule", "Deadline rule", 8).ReadOnly();
                s.User("osha_ack_by", "Clock acknowledged by").ReadOnly();
                s.DateTime("osha_ack_at", "Acknowledged at").ReadOnly();
                s.DateTime("osha_reported_at", "Reported to OSHA at")
                    .Help("When the report was made (phone 1-800-321-6742, online or in person). Recording it stops the clock.");
                s.Text("osha_report_ref", "OSHA reference number", 4);
                s.User("osha_reported_by", "Reported by");
            }, tab: "OSHA", icon: "bi-alarm",
               description: "Only for fatalities, in-patient hospitalizations, amputations and losses of an eye. Use the actions on the clock panel to acknowledge and record the report.")
            .Section("investigation", "Investigation", s =>
            {
                s.Keyword("root_cause_category", "Root cause category", "root_cause_category").RequiredIn("investigation");
                s.TextArea("root_cause", "Root cause").RequiredIn("investigation");
                s.TextArea("contributing_factors", "Contributing factors");
                s.YesNo("corrective_action_required", "Corrective action required?").RequiredIn("investigation");
                s.User("reviewer", "Management reviewer").RequiredIn("investigation")
                    .Help("Usually the area or site manager. Reviews the investigation before the incident closes.");
                s.Currency("total_cost", "Total cost", "USD", 4).ReadOnly().Help("Sum of the cost lines below.");
            }, tab: "Investigation", icon: "bi-search")
            .Subform("whys", "5 Whys", s =>
            {
                s.Text("why", "Why?", 6);
                s.Text("because", "Because…", 6);
            }, tab: "Investigation", rowNoun: "why", maxRows: 7, icon: "bi-question-circle")
            .Subform("corrective_actions", "Corrective actions", s =>
            {
                s.Text("action", "Action", 6).Required();
                s.User("assignee", "Assign to", 3).Required();
                s.Number("days", "Days to complete", 2).Required().Min(1).Max(365).Decimals(0);
                s.YesNo("verify", "Verify?", 1);
            }, tab: "Investigation", rowNoun: "action", icon: "bi-list-check",
               description: "Each row becomes an Action Item when the incident enters Corrective Actions (EHS-104).")
            .Subform("costs", "Costs", s =>
            {
                s.Text("item", "Cost item", 8).Required();
                s.Currency("amount", "Amount", "USD", 4).Required().Min(0);
            }, tab: "Investigation", rowNoun: "cost line", icon: "bi-cash-coin")
            .Section("review", "Management review", s =>
            {
                s.TextArea("review_comments", "Review comments").RequiredIn("review");
                s.TextArea("lessons_learned", "Lessons learned to share").Help("Shared in the monthly EHS committee and safety stand-downs.");
            }, tab: "Review", icon: "bi-clipboard-check")
            .Section("sys", "System", s =>
            {
                s.YesNo("in_current_year", "In the current calendar year").ReadOnly();
                s.YesNo("anonymous", "Reported anonymously").ReadOnly();
                s.User("anon_accepted_by", "Anonymous report accepted by").ReadOnly();
                s.DateTime("anon_accepted_at", "Anonymous report accepted at").ReadOnly();
            }, tab: "System", collapsed: true)
            .Workflow("incident", "Incident", w =>
            {
                w.Start("report", "Report").Purpose("Say what happened, where and when, and what you did straight away. EHS takes it from there.")
                    .DueDays(1).Edit("event", "immediate", "witnesses")
                    .Hide("injured", "osha", "osha_report", "investigation", "whys", "corrective_actions", "costs", "review", "sys");
                w.Phase("investigation", "Investigation").Purpose("EHS confirms the facts, records the injured person and the OSHA recordability decision, and finds the root cause.")
                    .AssignGroup("ehs").DueDays(10)
                    .Edit("event", "immediate", "witnesses", "injured", "osha", "osha_report", "investigation", "whys", "corrective_actions", "costs")
                    .Hide("review", "sys");
                w.Phase("corrective_actions", "Corrective Actions").Purpose("Carry out the corrective actions. Every action item must be closed before the management review.")
                    .AssignGroup("ehs").DueDays(30).Edit("injured", "osha", "osha_report", "costs").ReadOnlyRest().Hide("review", "sys")
                    .MustComplete("action_item").Activities("action_item");
                w.Phase("review", "Review").Purpose("Management reviews the investigation, the actions taken and the recordability decision, then closes the incident.")
                    .AssignField("reviewer").DueDays(5).Edit("review", "osha", "osha_report").ReadOnlyRest().Hide("sys");
                w.Completed("closed", "Closed").Hide("sys");
                w.Voided().Hide("sys");

                w.Forward("report", "investigation", label: "Submit to EHS");
                w.Void("report");
                w.Forward("investigation", "corrective_actions", label: "Start corrective actions", when: "corrective_action_required=yes");
                w.Forward("investigation", "review", label: "No actions needed: send to review", when: "corrective_action_required=no");
                w.Back("investigation", "report", label: "Return to reporter for details");
                w.Void("investigation");
                w.Forward("corrective_actions", "review", label: "Send to management review");
                w.Back("corrective_actions", "investigation", label: "Back to investigation");
                w.Forward("review", "closed", label: "Close incident", sign: "Reviewed and closed");
                w.Back("review", "investigation", label: "Reopen investigation");
            }));
    }

    private static void DefineHoursWorked(AppBuilder app)
    {
        app.Form("hours_worked", "Hours Worked", f => f
            .Icon("bi-clock-history")
            .Description("Employee hours worked per site and month. Feeds the OSHA 300A and the TRIR / DART rates (EHS-113, RPT-702).")
            .Numbering("HRS-")
            .NonWorkflow()
            .Title("{site} · {period_label}")
            .Section("hours", "Hours worked", s =>
            {
                s.Keyword("site", "Site", "ops_site").Required().Header();
                s.Date("period", "Month").Required().Header().Help("Any day in the month; it's stored as the first day. One record per site and month.");
                s.Text("period_label", "Period", 3).ReadOnly();
                s.Number("employees", "Average employees", 3).Min(0).Decimals(0);
                s.Number("hours", "Total hours worked", 3).Required().Min(0).Decimals(2).Header()
                    .Help("All hours actually worked by employees and supervised temporary workers, including overtime; excluding vacation, sick leave and holidays.");
                s.TextArea("notes", "Source and notes").Placeholder("For example: payroll export PR-2026-09, includes agency staff");
            }, icon: "bi-clock-history"));
    }

    private static void DefineJsa(AppBuilder app)
    {
        app.Form("jsa", "Job Safety Analysis", f => f
            .Icon("bi-person-gear")
            .Description("Break a job into steps, identify the hazards of each and rate the risk before and after controls (EHS §17.3).")
            .Numbering("JSA-")
            .Title("{job_title}")
            .Section("info", "Job information", s =>
            {
                s.Text("job_title", "Job or task", 8).Required().MaxLength(200).Placeholder("For example: Changing the injection mold on Press 7");
                s.Keyword("site", "Site", "ops_site").Required().Header();
                s.Text("area", "Area / equipment", 6);
                s.Text("department", "Department", 6);
                s.User("owner", "JSA owner").Required().Header().Default("$me");
                s.User("reviewer", "Reviewer").Required().Header().Help("Checks and approves the JSA with an e-signature. Must be someone other than the owner (SAF-044).");
                s.Text("required_ppe", "Required PPE for the whole job", 12).Placeholder("Safety glasses, cut-resistant gloves, safety shoes…");
                s.TextArea("scope", "Scope and when this JSA applies");
            }, icon: "bi-info-circle")
            .Subform("steps", "Job steps", s =>
            {
                s.Text("step", "Step", 2).Required();
                s.Text("hazard", "Hazard", 2).Required();
                s.Keyword("sev_i", "Severity", "jsa_severity", 1).Required();
                s.Keyword("lik_i", "Likelihood", "jsa_likelihood", 1).Required();
                s.Number("risk_i", "Initial risk", 1).ReadOnly();
                s.Text("controls", "Controls", 2).Required();
                s.Keyword("sev_r", "Residual severity", "jsa_severity", 1).Required();
                s.Keyword("lik_r", "Residual likelihood", "jsa_likelihood", 1).Required();
                s.Number("risk_r", "Residual risk", 1).ReadOnly();
                s.Keyword("band_r", "Residual band", "jsa_risk_band", 1).ReadOnly();
                s.Text("justification", "Justification", 2).Help("Required when the residual risk is higher than the initial risk (INV-078).");
            }, rowNoun: "step", required: true, icon: "bi-list-ol",
               description: "Risk = severity × likelihood (1–25): Low 1–4, Medium 5–9, High 10–14, Extreme 15–25. Residual risk may not exceed the initial risk without a justification, and no step may be left Extreme.")
            .Workflow("jsa", "JSA", w =>
            {
                w.Start("draft", "Draft").Purpose("Break the job into steps, list each step's hazards and rate the risk before and after the controls.")
                    .DueDays(10).Edit("info", "steps");
                w.Phase("review", "Review").Purpose("Check that every hazard is identified and the controls really reduce the risk, then approve with your e-signature.")
                    .AssignField("reviewer").DueDays(5).ReadOnlyRest();
                w.Completed("approved", "Approved");
                w.Voided();

                w.Forward("draft", "review", label: "Submit for review");
                w.Void("draft");
                w.Forward("review", "approved", label: "Approve JSA", sign: "Approved");
                w.Back("review", "draft", label: "Return for changes");
            }));
    }

    private static void DefinePermit(AppBuilder app)
    {
        app.Form("permit", "Work Permit", f => f
            .Icon("bi-file-earmark-lock")
            .Description("Permit to work, hot work or confined space entry with precautions, gas tests, authorization and close-out (EHS-406).")
            .Numbering("PTW-")
            .Title("{work_title}")
            .Section("request", "Work request", s =>
            {
                s.Text("work_title", "Work to be done", 8).Required().MaxLength(200).Placeholder("For example: Weld bracket to mezzanine handrail, bay 3");
                s.Keyword("permit_type", "Permit type", "permit_type").Required().Header();
                s.Keyword("site", "Site", "ops_site").Required().Header();
                s.Text("location", "Exact location / equipment", 8).Required();
                s.Text("contractor", "Company / contractor", 4);
                s.User("receiver", "Permit receiver").Required().Header().Help("The person in charge of the work crew. Must differ from the issuer (SAF-044).");
                s.User("issuer", "Permit issuer").Required().Help("The authorized person who checks the precautions on site and signs the permit.");
                s.Keyword("validity_status", "Validity", "permit_validity").ReadOnly().Header();
                s.DateTime("valid_from", "Valid from").Required();
                s.DateTime("valid_to", "Valid until").Required().Header()
                    .Help("Hot work and confined space permits are valid for one shift at most (12 hours); other permits for up to 7 days.");
                s.TextArea("hazards", "Hazards identified");
                s.Link("jsa", "Job safety analysis", "jsa").Help("Link the approved JSA for this job, if there is one.");
            }, icon: "bi-clipboard2-pulse")
            .Subform("conditions", "Conditions and precautions", s =>
            {
                s.Text("condition", "Precaution / condition", 9).Required();
                s.YesNo("confirmed", "In place?", 3);
            }, rowNoun: "precaution", icon: "bi-check2-square",
               description: "Every precaution must be confirmed in place before the issuer can authorize the permit.")
            .Section("gas_test", "Atmospheric test", s =>
            {
                s.Prompt("gas_prompt", "Required for confined space entry. Acceptable: oxygen 19.5–23.5 %, flammables below 10 % LEL, H₂S below 10 ppm, CO below 35 ppm.");
                s.Number("o2_pct", "Oxygen (%)", 3).Min(0).Max(100).Decimals(1);
                s.Number("lel_pct", "Flammables (% LEL)", 3).Min(0).Max(100).Decimals(1);
                s.Number("h2s_ppm", "H₂S (ppm)", 3).Min(0).Decimals(1);
                s.Number("co_ppm", "CO (ppm)", 3).Min(0).Decimals(0);
                s.User("tested_by", "Tested by");
                s.DateTime("tested_at", "Tested at");
                s.Keyword("gas_result", "Result", "gas_result").ReadOnly();
            }, icon: "bi-wind")
            .Section("closeout", "Close-out", s =>
            {
                s.YesNo("work_completed", "Work completed?").RequiredIn("active");
                s.YesNo("area_left_safe", "Area left safe (guards back, isolations removed, fire watch done)?").RequiredIn("active");
                s.TextArea("closeout_notes", "Close-out notes");
            }, icon: "bi-door-closed")
            .Workflow("permit", "Work permit", w =>
            {
                w.Start("request", "Request").Purpose("Describe the work, the location and the validity window, list the precautions and send it to the issuer.")
                    .DueDays(2).Edit("request", "conditions", "gas_test").Hide("closeout");
                w.Phase("authorization", "Authorization").Purpose("Check the precautions on site, record the gas test where needed and authorize the permit with your e-signature.")
                    .AssignField("issuer").DueDays(1).Edit("conditions", "gas_test").ReadOnly("request").Hide("closeout");
                w.Phase("active", "Active").Purpose("Work may proceed only inside the validity window with every precaution in place. Close out when done; the permit expires automatically at its end time.")
                    .AssignField("receiver").DueFrom("valid_to").Edit("closeout").ReadOnlyRest();
                w.Completed("closed", "Closed");
                w.Completed("expired", "Expired");
                w.Voided("voided", "Cancelled");

                w.Forward("request", "authorization", label: "Submit for authorization");
                w.Void("request", label: "Cancel request");
                w.Forward("authorization", "active", label: "Authorize permit", sign: "Authorized");
                w.Back("authorization", "request", label: "Return to requester");
                w.Void("authorization", label: "Refuse permit");
                w.Forward("active", "closed", label: "Close out permit", sign: "Closed out");
                w.Back("active", "authorization", label: "Suspend: needs revalidation");
                w.Void("active", label: "Cancel permit");
            }));
    }

    private static void DefineViews(AppBuilder app)
    {
        app.View("mine", "My EHS work", v => v.Category("1. My work").Forms("incident", "jsa", "permit").Mine()
            .Columns("number", "form", "title", "phase", "due").Sort("due", false));
        app.View("open", "Open incidents", v => v.Category("2. Incidents").Forms("incident").Open()
            .Columns("number", "title", "phase", "due", "incident_type", "outcome", "occurred_at", "site").Sort("occurred_at").Default());
        app.View("osha_clocks", "OSHA reporting clocks", v => v.Category("2. Incidents").Forms("incident").All()
            .Filter("osha_deadline", "set").Filter("osha_reported_at", "empty")
            .Columns("number", "title", "osha_severe_event", "occurred_at", "osha_deadline", "osha_ack_by", "phase").Sort("osha_deadline", false));
        app.View("near_misses", "Near misses", v => v.Category("2. Incidents").Forms("incident").All().Filter("incident_type", "eq", "near_miss")
            .Columns("number", "title", "phase", "potential_severity", "occurred_at", "site", "reported_by").Sort("occurred_at"));
        app.View("recordable_year", "Recordable incidents this year", v => v.Category("3. OSHA").Forms("incident").All()
            .Filter("recordable", "eq", "yes").Filter("in_current_year", "eq", "yes")
            .Columns("number", "title", "occurred_at", "site", "osha_classification", "days_away", "days_restricted", "dart", "phase").Sort("occurred_at"));
        app.View("osha_log", "OSHA 300-style log", v => v.Category("3. OSHA").Forms("incident").All().Filter("recordable", "eq", "yes")
            .Columns("number", "injured_name", "injured_job_title", "occurred_at", "area", "title", "osha_classification", "days_away", "days_restricted", "osha_case_type")
            .Sort("occurred_at", false).PageSize(100));
        app.View("hours", "Hours worked", v => v.Category("3. OSHA").Forms("hours_worked").All()
            .Columns("number", "site", "period", "employees", "hours").Sort("period"));
        app.View("closed", "Closed incidents", v => v.Category("4. Closed").Forms("incident").Completed()
            .Columns("number", "title", "incident_type", "outcome", "occurred_at", "site", "modified").Sort("modified"));
        app.View("voided", "Voided incidents", v => v.Category("4. Closed").Forms("incident").Voided().Columns("number", "title", "occurred_at", "modified"));
        app.View("jsa_open", "JSAs in progress", v => v.Category("5. Job safety analysis").Forms("jsa").Open()
            .Columns("number", "title", "phase", "due", "site", "owner", "reviewer"));
        app.View("jsa_approved", "Approved JSAs", v => v.Category("5. Job safety analysis").Forms("jsa").Completed()
            .Columns("number", "title", "site", "area", "owner", "modified").Sort("title", false));
        app.View("permits_active", "Active permits", v => v.Category("6. Work permits").Forms("permit").Open().Phases("active")
            .Columns("number", "title", "permit_type", "site", "validity_status", "valid_to", "receiver").Sort("valid_to", false));
        app.View("permits_expiring", "Expiring permits", v => v.Category("6. Work permits").Forms("permit").Open().Filter("validity_status", "eq", "expiring")
            .Columns("number", "title", "permit_type", "site", "valid_to", "receiver", "issuer").Sort("valid_to", false));
        app.View("permits_pending", "Awaiting authorization", v => v.Category("6. Work permits").Forms("permit").Open().Phases("request", "authorization")
            .Columns("number", "title", "phase", "permit_type", "valid_from", "issuer"));
        app.View("permits_all", "All permits", v => v.Category("6. Work permits").Forms("permit").All()
            .Columns("number", "title", "phase", "permit_type", "site", "valid_from", "valid_to", "validity_status").Sort("valid_from"));
    }
}
