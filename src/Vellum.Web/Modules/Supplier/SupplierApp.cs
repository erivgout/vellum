using Vellum.Domain;
using Vellum.Packaging;

namespace Vellum.Modules.Supplier;

/// <summary>
/// Supplier Quality (§15): supplier profiles with signed approval status and certificates (SUP-001, SUP-003),
/// material and chemical profiles (SUP-010..012, INV-089), receiving inspection with skip-lot (RCV-002..006, INV-170/171)
/// and supplier scorecards with weighted criteria (SUP-030 simplified, INV-094/095).
/// </summary>
public sealed class SupplierApp : IPackagedApp
{
    public string Key => "suppliers";
    public int Order => 70;

    public void Define(AppBuilder app)
    {
        var gradeIssues = Scoring.ValidateGrades(Scoring.Grades);
        if (gradeIssues.Count > 0) throw new InvalidOperationException("Supplier grade bands are invalid: " + string.Join(" ", gradeIssues));

        app.Name("Supplier Quality")
            .Description("Approved suppliers, materials and chemicals, receiving inspection with skip-lot, and supplier scorecards.")
            .Icon("bi-truck").Color("#0369a1").Suite("Supplier").Order(10).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@supplier-quality", AppRole.Author)
            .Grant("@qc-lab", AppRole.Author, formKey: "receiving")
            .Grant("@operations", AppRole.Author, formKey: "receiving")
            .Grant("cwu", AppRole.Manager, designer: true);

        app.Keywords("sup_status", "Supplier approval status", k => k
            .Add("prospective", "Prospective", "secondary").Add("approved", "Approved", "success").Add("conditional", "Conditional", "warning")
            .Add("disqualified", "Disqualified", "danger").Add("inactive", "Inactive", "dark"));
        app.Keywords("sup_type", "Supplier type", k => k
            .Add("raw_material", "Raw material").Add("component", "Component manufacturer").Add("contract_mfg", "Contract manufacturer")
            .Add("packaging", "Packaging").Add("service", "Service provider").Add("distributor", "Distributor"));
        app.Keywords("sup_risk_class", "Supplier risk class", k => k
            .Add("low", "Low", "success").Add("medium", "Medium", "info").Add("high", "High", "warning").Add("critical", "Critical", "danger"));
        app.Keywords("sup_qual_method", "Qualification method", k => k
            .Add("onsite_audit", "On-site audit").Add("remote_audit", "Remote audit").Add("questionnaire", "Questionnaire")
            .Add("cert_review", "Certificate review").Add("first_article", "First article inspection").Add("history", "Historical performance"));
        app.Keywords("sup_cert_std", "Certification standard", k => k
            .Add("iso_9001", "ISO 9001").Add("iso_13485", "ISO 13485").Add("iso_14001", "ISO 14001").Add("iso_45001", "ISO 45001")
            .Add("iatf_16949", "IATF 16949").Add("iso_11135", "ISO 11135 (EO sterilization)").Add("iso_11137", "ISO 11137 (radiation sterilization)")
            .Add("iso_17025", "ISO/IEC 17025").Add("mdsap", "MDSAP").Add("other", "Other"));
        app.Keywords("sup_cert_state", "Certificate status", k => k
            .Add("valid", "Valid", "success").Add("expiring", "Expires within 60 days", "warning").Add("expired", "Certificate expired", "danger")
            .Add("superseded", "Superseded (renewed)", "secondary").Add("none", "No certificates", "secondary"));
        app.Keywords("sup_commodity", "Commodity", k => k
            .Add("resin", "Resin / polymer").Add("molded", "Molded component").Add("machined", "Machined component")
            .Add("electronic", "Electronic assembly").Add("packaging", "Sterile packaging").Add("labeling", "Labels & IFU")
            .Add("chemical", "Chemical").Add("service", "Service"));
        app.Keywords("sup_sampling", "Sampling plan", k => k
            .Add("z14_l2_065", "ANSI/ASQ Z1.4 Level II, AQL 0.65").Add("z14_l2_10", "ANSI/ASQ Z1.4 Level II, AQL 1.0")
            .Add("z14_s3_25", "ANSI/ASQ Z1.4 S-3, AQL 2.5").Add("c0_065", "c=0 plan, AQL 0.65").Add("full", "100% inspection")
            .Add("coa_review", "CoA review and identity check"));
        app.Keywords("sup_ghs", "GHS hazard pictogram", k => k
            .Add("flammable", "Flammable").Add("oxidizer", "Oxidizer").Add("corrosive", "Corrosive").Add("toxic", "Acute toxicity")
            .Add("health", "Health hazard").Add("irritant", "Irritant").Add("environment", "Environmental hazard").Add("gas", "Gas under pressure"));
        app.Keywords("rcv_status", "Inspection status", k => k
            .Add("required", "Inspection required", "warning").Add("skip", "Skip (skip-lot)", "success"));
        app.Keywords("rcv_result", "Inspection result", k => k
            .Add("accept", "Accept", "success").Add("reject", "Reject", "danger"));
        app.Keywords("sup_grade", "Supplier grade", k =>
        {
            foreach (var g in Scoring.Grades)
                k.Add(g.Key, g.Label, g.Key switch { "a" => "success", "b" => "info", "c" => "warning", _ => "danger" });
        });

        DefineSupplier(app);
        DefineMaterial(app);
        DefineReceiving(app);
        DefineScorecard(app);
        DefineViews(app);
    }

    private static void DefineSupplier(AppBuilder app) => app.Form("supplier", "Supplier", f => f
        .Icon("bi-building")
        .Description("Supplier profile: approval status, contacts, certificates, supplied materials and scorecard weights.")
        .Numbering("SUP-", 4)
        .Title("{name}")
        .NonWorkflow()
        .Section("profile", "Supplier information", s =>
        {
            s.Text("name", "Supplier name", 8).Required().Unique().MaxLength(200).Placeholder("Legal name, e.g. Polymera Resins Inc.");
            s.Text("code", "Supplier code", 4).Required().Unique().MaxLength(30).Help("The vendor code used in the ERP and on purchase orders.");
            s.Keyword("approval_status", "Approval status", "sup_status").ReadOnly().Protected().Header().Default("prospective")
                .Help("Changed only with the signed Approve, Disqualify and Deactivate actions, so every change is e-signed and in the audit trail (SUP-001).");
            s.Keyword("risk_class", "Risk class", "sup_risk_class").Required().Header()
                .Help("Impact of this supplier's product or service on device safety and performance. High and critical suppliers need a quality agreement and a valid certificate before approval.");
            s.Keyword("supplier_type", "Supplier type", "sup_type").Required();
            s.User("owner", "Supplier quality engineer").Required().Default("$me");
            s.Date("approved_on", "Approved on").ReadOnly().Protected();
            s.Date("requalify_on", "Requalification due").Help("Set at approval from the risk class: critical 1 year, high 2 years, others 3 years (SUP-005).");
            s.TextArea("address", "Address", 6);
            s.Text("country", "Country", 3);
            s.Url("website", "Website", 3);
            s.TextArea("scope", "Approved scope", 12).Help("What this supplier is approved to supply. Shown on the approved supplier list.");
            s.TextArea("conditions", "Conditions of approval", 12).VisibleWhen("approval_status=conditional")
                .Help("Restrictions that apply while the supplier is conditionally approved, for example 100% inspection or an open SCAR.");
        }, icon: "bi-building")
        .Subform("contacts", "Contacts", s =>
        {
            s.Text("contact_name", "Name", 3).Required();
            s.Text("contact_role", "Role", 3);
            s.Text("email", "Email", 3);
            s.Text("phone", "Phone", 2);
            s.YesNo("notify_ratings", "Rating reports", 1);
        }, rowNoun: "contact", icon: "bi-person-lines-fill",
           description: "People at the supplier. Contacts flagged for rating reports receive published scorecards (SUP-036).")
        .Section("qualification", "Qualification", s =>
        {
            s.Keywords("qual_methods", "Qualification methods", "sup_qual_method", 8);
            s.Date("qual_date", "Qualified on", 4);
            s.Number("qual_audit_score", "Qualification audit score (%)", 4).Min(0).Max(100).Decimals(1);
            s.YesNo("quality_agreement", "Quality agreement in place?", 4).Default("no");
            s.Date("quality_agreement_on", "Quality agreement signed", 4).VisibleWhen("quality_agreement=yes");
            s.Link("qual_audit", "Qualification audit record").Help("The supplier audit that qualified this supplier, if there was one.");
            s.TextArea("qual_summary", "Qualification summary").Help("The evidence reviewed and the conclusion. Required before approval.");
        }, tab: "Qualification", icon: "bi-clipboard-check")
        .Subform("certifications", "Certificates", s =>
        {
            s.Keyword("standard", "Standard", "sup_cert_std", 3).Required();
            s.Text("cert_number", "Certificate no.", 2);
            s.Text("registrar", "Certification body", 3);
            s.Date("expires_on", "Expires on", 2).Required();
            s.Keyword("cert_state", "Status", "sup_cert_state", 2).ReadOnly();
        }, tab: "Certificates", rowNoun: "certificate", icon: "bi-patch-check",
           description: "Checked every day. Supplier quality is notified 60 days before a certificate expires, and the supplier is flagged when one expires (SUP-003). Keep renewed certificates as a new row: the older one shows as superseded.")
        .Section("cert_summary", "Certificate status", s =>
        {
            s.Keyword("cert_status", "Certificate status", "sup_cert_state").ReadOnly().Header();
            s.Date("next_cert_expiry", "Next expiry").ReadOnly();
            s.Keyword("cert_notified_state", "Last reminder for", "sup_cert_state").ReadOnly();
            s.Date("cert_notified_on", "Last reminder sent").ReadOnly();
            s.Attachment("cert_files", "Certificate copies");
        }, tab: "Certificates", icon: "bi-shield-check")
        .Subform("supplied_materials", "Supplied materials", s =>
        {
            s.Link("material", "Material", "material").Width(5).Required();
            s.Text("supplier_part", "Supplier part no.", 3);
            s.Number("lead_time", "Lead time (days)", 2).Min(0).Decimals(0);
            s.Text("notes", "Notes", 2);
        }, tab: "Materials", rowNoun: "material", icon: "bi-box-seam",
           description: "This supplier's approved supplier list entries. Only approved material profiles can be listed (INV-089), and receiving is blocked for materials not listed here (SUP-003).")
        .Section("weights", "Scorecard weights", s =>
        {
            s.Prompt("weights_prompt", "New scorecards for this supplier start from these weights. They must total 100% (INV-094).");
            s.Number("w_quality", "Quality (ppm) %", 3).Default("40").Min(0).Max(100).Decimals(2);
            s.Number("w_delivery", "On-time delivery %", 3).Default("30").Min(0).Max(100).Decimals(2);
            s.Number("w_response", "Responsiveness %", 3).Default("15").Min(0).Max(100).Decimals(2);
            s.Number("w_audit", "Audit %", 3).Default("15").Min(0).Max(100).Decimals(2);
        }, tab: "Performance", icon: "bi-sliders")
        .Section("rating", "Current rating", s =>
        {
            s.Number("current_score", "Latest score", 3).ReadOnly().Decimals(2);
            s.Keyword("current_grade", "Grade", "sup_grade", 3).ReadOnly().Header();
            s.Text("current_period", "Period", 3).ReadOnly();
            s.Link("last_scorecard", "Latest scorecard").ReadOnly();
        }, tab: "Performance", icon: "bi-speedometer2"));

    private static void DefineMaterial(AppBuilder app) => app.Form("material", "Material", f => f
        .Icon("bi-box-seam")
        .Description("Material or chemical profile with its commodity, inspection criteria and skip-lot profile.")
        .Numbering("MAT-", 4)
        .Title("{part_number} · {description}")
        .Section("material", "Material information", s =>
        {
            s.Text("part_number", "Part number", 3).Required().Unique().MaxLength(40).Header().Help("Internal part number. Must be unique.");
            s.Text("description", "Description", 9).Required().MaxLength(200);
            s.Keyword("commodity", "Commodity", "sup_commodity").Required().Header();
            s.User("owner", "Material owner").Required().Default("$me").Help("Approves the profile (SUP-012).");
            s.Text("spec_ref", "Specification / drawing", 4).Placeholder("e.g. DWG-200-114 rev C");
            s.Text("uom", "Unit of measure", 2).Default("EA");
            s.YesNo("do_not_rate", "Do not rate", 3).Default("no").Help("Leave this material out of supplier scorecards (SUP-010).");
            s.YesNo("inactive", "Inactive", 3).Default("no").Help("Inactive materials can't be added to supplier lists or received.");
        }, icon: "bi-box-seam")
        .Section("chemical", "Chemical information", s =>
        {
            s.Prompt("chem_prompt", "Shown for chemicals (SUP-011). Hazard data comes from the supplier's safety data sheet.").VisibleWhen("commodity=chemical");
            s.YesNo("cas_registered", "CAS registered?", 3).VisibleWhen("commodity=chemical");
            s.Text("cas_number", "CAS number", 3).VisibleWhen("commodity=chemical&&cas_registered=yes").Placeholder("e.g. 67-63-0")
                .Help("Checked with the CAS check digit.");
            s.Text("ca_index_name", "CA index name", 6).VisibleWhen("commodity=chemical&&cas_registered=yes");
            s.Text("synonyms", "Synonyms", 6).VisibleWhen("commodity=chemical");
            s.Keywords("ghs", "GHS pictograms", "sup_ghs", 6).VisibleWhen("commodity=chemical");
            s.TextArea("hazard_summary", "Summary hazard of the product", 6).VisibleWhen("commodity=chemical");
            s.TextArea("osha_hazcom", "OSHA hazard communication", 6).VisibleWhen("commodity=chemical");
            s.Url("sds_link", "Safety data sheet", 6).VisibleWhen("commodity=chemical").Help("Link to the latest approved SDS revision.");
        }, icon: "bi-droplet-half")
        .Section("inspection", "Incoming inspection", s =>
        {
            s.Keyword("sampling_plan", "Sampling plan", "sup_sampling").RequiredIn("draft");
            s.Number("accept_number", "Accept number (c)", 2).Default("0").Min(0).Max(50).Decimals(0)
                .Help("A lot is rejected when the sample has more defects than this.");
            s.User("inspector", "Default inspector", 3).Help("Receiving inspections of this material are assigned here.");
            s.YesNo("coa_required", "CoA required?", 3).Default("yes").Help("Lots received without a certificate of analysis are always inspected.");
            s.TextArea("inspection_criteria", "Inspection criteria").Placeholder("Dimensional, visual and functional checks with their limits");
            s.YesNo("skip_lot_allowed", "Skip-lot allowed?", 3).Default("yes");
            s.Number("skip_initial", "Passes before skipping", 3).Default("3").Min(1).Max(20).Decimals(0).VisibleWhen("skip_lot_allowed=yes")
                .Help("Consecutive accepted lots needed before skipping starts (RCV-002).");
            s.Number("skip_count", "Lots to skip", 3).Default("2").Min(0).Max(20).Decimals(0).VisibleWhen("skip_lot_allowed=yes")
                .Help("Lots released without inspection between inspected lots.");
            s.Number("skip_requalify", "Passes after a failure", 3).Default("5").Min(1).Max(30).Decimals(0).VisibleWhen("skip_lot_allowed=yes")
                .Help("Consecutive accepted lots needed after a rejection before skipping resumes.");
        }, tab: "Inspection", icon: "bi-search")
        .Workflow("material", "Material", w =>
        {
            w.Start("draft", "Draft").Purpose("Describe the material, its commodity and how incoming lots are inspected.")
                .DueDays(10).Edit("material", "chemical", "inspection");
            w.Phase("approval", "Approval").Purpose("The material owner checks the profile and approves it. Only approved materials can be listed as supplied materials or received (INV-089).")
                .AssignField("owner").DueDays(5).ReadOnlyRest();
            w.Completed("approved", "Approved");
            w.Voided();

            w.Forward("draft", "approval", label: "Submit for approval");
            w.Void("draft");
            w.Forward("approval", "approved", label: "Approve material", sign: "Approved");
            w.Back("approval", "draft", label: "Send back for changes");
            w.Void("approval");
        }));

    private static void DefineReceiving(AppBuilder app) => app.Form("receiving", "Receiving Inspection", f => f
        .Icon("bi-box-arrow-in-down")
        .Description("A received lot: skip-lot decision, incoming inspection results and disposition.")
        .Numbering("RCV-", 5)
        .Title("{material_name} · lot {lot_number}")
        .Section("receipt", "Receipt", s =>
        {
            s.Text("po_number", "Purchase order", 3).Required().MaxLength(40);
            s.Date("received_on", "Received on", 3).Required().Default("$today").Header();
            s.Text("lot_number", "Supplier lot / batch", 3).Required().MaxLength(60).Header();
            s.Number("quantity", "Quantity received", 3).Required().Min(0.0001m);
            s.Link("supplier", "Supplier", "supplier").Required().Help("Only approved or conditionally approved suppliers can be received from (SUP-003).");
            s.Link("material", "Material", "material").Required().Help("Must be an approved material on this supplier's list (INV-089, SUP-003).");
            s.YesNo("coa_received", "Certificate of analysis received?", 4).Default("no");
            s.Text("supplier_name", "Supplier name", 4).ReadOnly();
            s.Text("material_name", "Material", 4).ReadOnly();
            s.Attachment("coa_file", "CoA and packing documents");
        }, icon: "bi-truck")
        .Section("status", "Inspection status", s =>
        {
            s.Keyword("inspection_status", "Inspection status", "rcv_status").ReadOnly().Header()
                .Help("Worked out from the material's skip-lot profile and this supplier's history for the material (INV-170).");
            s.Text("sampling_plan_text", "Sampling plan", 4).ReadOnly();
            s.User("inspector", "Inspector").VisibleWhen("inspection_status=required").RequiredIn("receipt");
            s.TextArea("status_basis", "How the status was decided").ReadOnly();
        }, icon: "bi-signpost-split")
        .Section("inspection", "Inspection results", s =>
        {
            s.Number("sample_size", "Sample size", 3).RequiredIn("inspection").Min(1).Decimals(0);
            s.Number("defects_found", "Defects found", 3).RequiredIn("inspection").Min(0).Decimals(0);
            s.YesNo("doc_nonconforming", "Documentation nonconforming?", 3).Default("no").Help("For example a missing or out-of-specification CoA.");
            s.Keyword("inspection_result", "Result", "rcv_result", 3).ReadOnly().Header()
                .Help("Reject when defects exceed the material's accept number, or the documentation is nonconforming.");
            s.TextArea("inspection_notes", "Observations");
            s.TextArea("defect_description", "Nonconformance description").VisibleWhen("inspection_result=reject");
            s.Number("qty_rejected", "Quantity rejected", 3).Min(0).VisibleWhen("inspection_result=reject").Help("Leave blank to count the whole lot.");
            s.Link("ncr_link", "NCMR").VisibleWhen("inspection_result=reject")
                .Help("A rejected lot can only be closed with a linked NCMR (INV-171). Use Create NCMR, or link an existing nonconformance.");
        }, tab: "Inspection", icon: "bi-clipboard2-pulse")
        .Workflow("receiving", "Receiving inspection", w =>
        {
            w.Start("receipt", "Receipt").Purpose("Record the delivery. Vellum decides whether the lot is inspected from the supplier's skip-lot history (INV-170).")
                .DueDays(1).Edit("receipt", "status").Hide("inspection");
            w.Phase("inspection", "Inspection").Purpose("Inspect the sample against the material's criteria and record the defects found. A rejected lot needs an NCMR before it can close (INV-171).")
                .AssignField("inspector").DueDays(3).Edit("inspection").ReadOnly("receipt", "status");
            w.Completed("released", "Released (skip-lot)");
            w.Completed("accepted", "Accepted");
            w.Completed("rejected", "Rejected");
            w.Voided();

            w.Forward("receipt", "inspection", label: "Send to inspection", when: "inspection_status=required");
            w.Forward("receipt", "released", label: "Release without inspection (skip-lot)", sign: "Released under skip-lot", when: "inspection_status=skip");
            w.Void("receipt");
            w.Forward("inspection", "accepted", label: "Accept lot", sign: "Inspected and accepted", when: "inspection_result=accept");
            w.Forward("inspection", "rejected", label: "Reject lot", sign: "Inspected and rejected", when: "inspection_result=reject");
            w.Back("inspection", "receipt", label: "Back to receipt");
            w.Void("inspection", label: "Void inspection (invalid)");
        }));

    private static void DefineScorecard(AppBuilder app) => app.Form("scorecard", "Supplier Scorecard", f => f
        .Icon("bi-speedometer2")
        .Description("Periodic supplier rating from weighted quality, delivery, responsiveness and audit criteria.")
        .Numbering("SCR-", 5)
        .Title("{supplier_name} · {period}")
        .Section("period", "Scorecard", s =>
        {
            s.Link("supplier", "Supplier", "supplier").Required();
            s.Text("supplier_name", "Supplier name", 6).ReadOnly();
            s.Date("period_start", "Period start", 3).Required();
            s.Date("period_end", "Period end", 3).Required();
            s.Text("period", "Period", 3).ReadOnly().Header();
            s.Number("lots_received", "Lots received", 3).ReadOnly().Decimals(0);
            s.Number("lots_rejected", "Lots rejected", 3).ReadOnly().Decimals(0);
        }, icon: "bi-calendar-range")
        .Section("measures", "Performance measures", s =>
        {
            s.Number("ppm", "Quality: defective ppm", 3).RequiredIn("draft").Min(0).Decimals(0)
                .Help("Defective parts per million received. 0 ppm scores 100; every 50 ppm costs one point; 5,000 ppm or more scores 0. Use Calculate from receiving to fill it in.");
            s.Number("otd_pct", "On-time delivery (%)", 3).RequiredIn("draft").Min(0).Max(100).Decimals(1);
            s.Number("responsiveness", "Responsiveness (1–5)", 3).RequiredIn("draft").Min(1).Max(5).Decimals(0)
                .Help("1 = unresponsive, 5 = proactive (SCAR replies, quotes, change notifications). Scores 20 points per step.");
            s.Number("audit_score", "Audit score (%)", 3).Min(0).Max(100).Decimals(1)
                .Help("Leave blank when there was no audit in the period: its weight is shared proportionally among the other criteria (INV-095).");
        }, icon: "bi-rulers")
        .Section("weights", "Criteria weights", s =>
        {
            s.Prompt("weights_prompt", "Weights must total 100% (INV-094). Leave them blank to use the supplier's weights.");
            s.Number("w_quality", "Quality %", 3).Min(0).Max(100).Decimals(2);
            s.Number("w_delivery", "Delivery %", 3).Min(0).Max(100).Decimals(2);
            s.Number("w_response", "Responsiveness %", 3).Min(0).Max(100).Decimals(2);
            s.Number("w_audit", "Audit %", 3).Min(0).Max(100).Decimals(2);
        }, icon: "bi-sliders")
        .Section("result", "Result", s =>
        {
            s.Number("s_quality", "Quality score", 3).ReadOnly().Decimals(2);
            s.Number("s_delivery", "Delivery score", 3).ReadOnly().Decimals(2);
            s.Number("s_response", "Responsiveness score", 3).ReadOnly().Decimals(2);
            s.Number("s_audit", "Audit score", 3).ReadOnly().Decimals(2);
            s.Number("overall_score", "Overall score", 4).ReadOnly().Decimals(2).Header();
            s.Keyword("grade", "Grade", "sup_grade", 4).ReadOnly().Header();
            s.TextArea("comments", "Comments for the supplier");
        }, icon: "bi-award")
        .Workflow("scorecard", "Scorecard", w =>
        {
            w.Start("draft", "Draft").Purpose("Enter the period's measures. Scores and the grade are calculated with exact decimal arithmetic every time you save.")
                .DueDays(10).Edit("period", "measures", "weights", "result");
            w.Completed("published", "Published");
            w.Voided();
            w.Forward("draft", "published", label: "Publish scorecard");
            w.Void("draft");
        }));

    private static void DefineViews(AppBuilder app)
    {
        app.View("asl", "Approved supplier list", v => v.Category("1. Suppliers").Forms("supplier").All()
            .Filter("approval_status", "set").Filter("approval_status", "ne", "prospective").Filter("approval_status", "ne", "disqualified").Filter("approval_status", "ne", "inactive")
            .Columns("number", "title", "code", "approval_status", "risk_class", "supplier_type", "cert_status", "current_grade").Sort("title", false).Default());
        app.View("all_suppliers", "All suppliers", v => v.Category("1. Suppliers").Forms("supplier").All()
            .Columns("number", "title", "code", "approval_status", "risk_class", "owner", "requalify_on").Sort("title", false));
        app.View("prospective", "In qualification", v => v.Category("1. Suppliers").Forms("supplier").All().Filter("approval_status", "eq", "prospective")
            .Columns("number", "title", "supplier_type", "risk_class", "owner", "created"));
        app.View("conditional", "Conditional suppliers", v => v.Category("1. Suppliers").Forms("supplier").All().Filter("approval_status", "eq", "conditional")
            .Columns("number", "title", "risk_class", "current_grade", "requalify_on", "owner"));
        app.View("disqualified", "Disqualified and inactive", v => v.Category("1. Suppliers").Forms("supplier").All()
            .Filter("approval_status", "ne", "prospective").Filter("approval_status", "ne", "approved").Filter("approval_status", "ne", "conditional")
            .Columns("number", "title", "approval_status", "supplier_type", "modified"));
        app.View("expiring", "Expiring certificates", v => v.Category("2. Certificates").Forms("supplier").All()
            .Filter("cert_status", "set").Filter("cert_status", "ne", "valid").Filter("cert_status", "ne", "none")
            .Columns("number", "title", "cert_status", "next_cert_expiry", "approval_status", "owner").Sort("next_cert_expiry", false));

        app.View("materials_status", "Materials by status", v => v.Category("3. Materials").Forms("material").All()
            .Columns("number", "part_number", "description", "commodity", "phase", "owner").Sort("phase", false));
        app.View("materials_pending", "Materials awaiting approval", v => v.Category("3. Materials").Forms("material").Open()
            .Columns("number", "part_number", "description", "phase", "due", "owner"));
        app.View("chemicals", "Chemicals", v => v.Category("3. Materials").Forms("material").All().Filter("commodity", "eq", "chemical")
            .Columns("number", "part_number", "description", "cas_number", "ghs", "phase"));

        app.View("rcv_open", "Open receiving inspections", v => v.Category("4. Receiving").Forms("receiving").Open()
            .Columns("number", "title", "supplier_name", "received_on", "inspection_status", "phase", "due", "inspector").Sort("due", false));
        app.View("rcv_mine", "My inspections", v => v.Category("4. Receiving").Forms("receiving").Mine()
            .Columns("number", "title", "supplier_name", "inspection_status", "phase", "due"));
        app.View("rcv_rejected", "Rejected lots", v => v.Category("4. Receiving").Forms("receiving").All().Filter("inspection_result", "eq", "reject")
            .Columns("number", "title", "supplier_name", "received_on", "phase", "ncr_link"));
        app.View("rcv_all", "All receiving records", v => v.Category("4. Receiving").Forms("receiving").All()
            .Columns("number", "title", "supplier_name", "received_on", "inspection_status", "inspection_result", "phase").Sort("received_on"));

        app.View("scorecards", "Supplier scorecards", v => v.Category("5. Scorecards").Forms("scorecard").All()
            .Columns("number", "supplier_name", "period", "overall_score", "grade", "phase").Sort("period_start"));
        app.View("scorecards_low", "Grade C and D scorecards", v => v.Category("5. Scorecards").Forms("scorecard").All()
            .Filter("grade", "set").Filter("grade", "ne", "a").Filter("grade", "ne", "b")
            .Columns("number", "supplier_name", "period", "overall_score", "grade", "phase").Sort("period_start"));
    }

    public Task SeedDemoAsync(DemoContext demo) => SupplierDemo.SeedAsync(demo);
}
