# 12. Quality Events, CAPA and Product Safety

**Parity sources:** Reliance help › Quality Events, Nonconformance Reporting, Corrective Action (CAPA/SCAR), Planned Deviation, Lab Investigations, Customer Feedback, Complaints Handling (base and 1.0.2), Adverse Event Reporting 1.0.0, Return Material Authorization; Administrator › eMDR Profile; AI Features › Complaints and Feedback Advisor.

## 12.1 Shared building blocks

These building blocks are used across every quality and EHS application.

### Action items

- **QE-001** A standard **Action Item** activity shall be available to every application. It has:
  - Subject and general information.
  - Assignee and due date.
  - *Verification required?*. When set, *assign verification to* and *verification to be performed* become required.
  - Action taken, with attachments.
  - A verification section.

  Workflow: **Initiate/Draft → Open → (Verification, if required) → Completed/Closed**. Open can go back to Initiate with comments. Verification can go back to Open with comments. Closing needs an e-signature where the parent application is regulated [P11].
- **QE-002** **Planned actions** (rows in an investigation plan or action plan) shall become real Action Items **automatically** when the parent enters its configured phase. Each row's assignee, due date (or *days to complete*) and verification settings carry over, and the link is written back to the row ([INV-040](21-invariants.md)).
- **QE-003** Parents shall be able to require that **all child Action Items are completed** (with voided counting as completed) before they move past a configured phase ([PLT-640](02-platform.md)).

### Due-date extension and reassignment

- **QE-004** A **due-date extension / reassignment request** activity shall be available in any open phase of configured parents. It has its own approval workflow (*Approval → Approved / Rejected*). On approval, the parent phase's due date and/or assignee is updated and recorded in history. If the parent moves on or closes before a decision, the request is automatically routed to *Not Approved*.

### Gating decisions

- **QE-005** **"Is a ⟨CAPA / NCMR / investigation⟩ required?" pattern:**
  - **Yes** shows an *Initiate* action. The parent can't move past the gate phase until the linked record exists.
  - **No** requires a **justification**, which is kept in the audit trail ([INV-041](21-invariants.md)).

### Root cause, containment and effectiveness

- **QE-006** **Root-cause analysis tools** *(Vellum extension, should; Reliance offers free-text root cause only)*: structured **5 Whys**, **Ishikawa/fishbone** (6M categories, configurable), **Is / Is-Not**, **fault tree** and **8D** discipline sections. Root cause categories come from a keyword list, which allows trending ([RPT-701](07-reporting.md)).
- **QE-007** **Containment** sections shall record the immediate actions taken, the quantity and location of suspect product, and the containment owner and date.
- **QE-008** **Effectiveness checks** shall be schedulable for a future date (for example 90 days after implementation). They record the criteria, the result (*effective / not effective*), evidence and the verifier's e-signature. *Not effective* shall reopen or re-raise the CAPA ([INV-042](21-invariants.md)).

### Optional sections and lookups

- **QE-009** **Optional sections**, switched on per record, shall look up and copy data from master data (more than one row per section is allowed):
  - Audit, customer, document, equipment/asset, incident, product/item, supplier and material, and process information.
  - When the source application isn't licensed, the section falls back to manual entry.

### Linked records and mail

- **QE-010** **Risk assessments** ([RSK §16.1](16-risk.md)) may be created from any quality event. The parent shows linked assessments with a risk-status color, and the highest result is rolled up.
- **QE-011** **Mail activities** ([ADM-612](05-administration.md)) shall be available on records that deal with external parties: customers, suppliers and auditees.
- **QE-012** **Cost tracking:** cost lines with category (administrative, equipment, labor, materials, transportation, third-party services, scrap, rework), date and amount. Subtotals and a cost log are calculated by category.

## 12.2 Quality events (deviations from procedure)

- **QE-101** A **quality event** shall capture:
  - Event information and immediate actions.
  - **Criticality** (for example *Critical / Major / Minor*, [CFG]).
  - Initial impact assessment.
  - **Track & Trend** (Y/N). Yes requires a justification.
  - Investigation plan, investigation summary, root cause, trend analysis.
  - *Is a CAPA required?* (QE-005).
  - Linked risk assessments, activities, optional sections (QE-009).
  - A distribution list.
- **QE-102** Workflow: **Initiate → Initial Review → Investigation Planning → Plan Approval → Active Investigation → Final Approval → Completed**.
  - **Track & Trend shortcut:** Initial Review → Final Approval, skipping the investigation.
  - Void is possible from Initiate and Initial Review.
  - Each phase can go back to the previous one.
- **QE-103** [SAFETY] When criticality is **Critical**, an **escalation notification list** shall be required. Its members are notified on **every** phase transition.
- **QE-104** Moving from Plan Approval to Active Investigation shall turn each investigation plan row into an Action Item (QE-002).
- **QE-105** Routing to Final Approval shall fail unless all Action Items are complete.
- **QE-106** On Completed, the online distribution list shall be notified.
- **QE-107** Packaged views: all, open and completed quality events; all and open action items; extension and reassignment requests.

## 12.3 Nonconformance (NCR / NCMR)

- **NC-001** A **nonconformance** record shall capture:
  - Origination information and **nonconformance type**. The type controls which sections show. For example, supplier information is hidden for *Finished Product*, and audit information shows only for *Stock Rejection* [CFG].
  - Supplier information (lookup), customer information (lookup), process information, audit information (lookup).
  - Product information: part number, looked up from materials and filtered by the chosen supplier.
  - Lot or batch, quantity affected, defect codes.
  - Containment.
  - **Disposition**.
  - Verification and effectiveness.
  - Costs.
- **NC-002** **Disposition types:** *Use As Is*, *Return to Supplier* (requires return information), *Scrap* (with instructions), *Rework*, *Re-grade*, plus *Repair* and *Sort/Screen* as Vellum extensions [CFG]. Each disposition line records quantity, so one NCR can have several dispositions. The disposition quantities shall add up to the affected quantity ([INV-065](21-invariants.md)).
- **NC-003** Workflow: **Create → MRB Review → Disposition → Final Approval → Verification → Completed**. Vellum adds back-routes and void, configurable as in other applications. MRB review shall support multiple approvers (parallel or voting) with e-signatures [P11].
- **NC-004** [SAFETY] **Use As Is** shall require an **active planned deviation** (DEV) to be created and approved before the NCR can complete. Only **one active deviation** may exist at a time for the same *part number + defect code* ([INV-118](21-invariants.md)).
- **NC-005** *Supplier fault? = Yes* shall reveal **Create SCAR** (a CAPA of type SCAR, CAPA §12.4). The NCR may also create an 8D CAPA, a risk assessment, an inspection record, a deviation and action items.
- **NC-006** NCRs may be created from complaints, lab investigations, failed receiving or in-process inspections, CAPA, and inspection samples ([INS](14-audits-inspections.md)).
- **NC-007** [SAFETY] *(Vellum extension)* An NCR may place the affected material on **hold**: a status flag sent to ERP or MES through integration ([INT §6.2](06-integrations.md)). The hold is released only when the disposition is approved.

## 12.4 Corrective and preventive action (CAPA), 8D and SCAR

### CAPA

- **CAPA-001** **CAPA** records shall capture:
  - Problem identification and source (audit, customer, complaint, NCR, quality event, lab investigation, incident, risk assessment, management review), linked to the source record.
  - Containment.
  - **Root cause** (with the tools in QE-006).
  - **Action plan**: *develop solution* text plus planned action rows.
  - An **effectiveness check**.
- **CAPA-002** CAPA workflow: **Initiate → Initial Review → Root Cause & Corrective Action Plan → Plan Approval → Implementation → Effectiveness Check → Closed**.
  - Initial Review may send back, **void** (invalid) or **close without action**, which requires a justification.
  - Plan Approval may send back.
  - Effectiveness Check may go back to Implementation.
- **CAPA-003** Entering Implementation shall create and assign the planned Action Items. Extra ad-hoc Action Items may be added during Implementation. Action Items may also be assigned during Initiate.
- **CAPA-004** A CAPA can't move to Effectiveness Check until **all** its Action Items are closed ([INV-043](21-invariants.md)).
- **CAPA-005** From Initiate through Effectiveness Check, users may create linked CAPAs, deviations and NCRs from the CAPA.
- **CAPA-006** CAPA types shall be configurable: Corrective, Preventive, **8D**, **SCAR**, and others. Each type may have its own workflow. Packaged 8D sections: D0 Prepare, D1 Team, D2 Problem, D3 Containment, D4 Root Cause (occurrence and escape), D5 Permanent Corrective Action, D6 Implement and Validate, D7 Prevent Recurrence, D8 Recognize.
- **CAPA-007** [SAFETY] *(Vellum extension)* Each CAPA shall have a **risk-based priority** (from a linked risk assessment, or set by hand with a justification) that sets default phase due dates. Overdue CAPAs shall escalate up the management chain ([PLT-1207](02-platform.md)).

### SCAR

- **CAPA-010** **SCAR** (supplier corrective action request) shall capture problem identification, supplier and product information, containment, the **supplier response** and verification.
  - Workflow: **Draft → Initial Approval → Assign to Supplier → Verification & Effectiveness → Completed**, with back-routes.
- **CAPA-011** **Send to Supplier** shall either create a mail activity carrying the SCAR response template (an Excel/Word file), or invite the supplier to the **supplier portal** ([SUP §15.1](15-supplier-quality.md)). A returned template file, once attached and saved, shall **fill in the supplier response section automatically**. Portal responses fill it in directly.
- **CAPA-012** Audit findings that come back from external auditees (offline sheets) shall fill in the CAPA's root cause and corrective action descriptions ([AUD-030](14-audits-inspections.md)).

### Views

- **CAPA-020** Packaged views:
  - CAPA: open (by phase, owner, source, priority), overdue, voided, closed without action, closed.
  - Action items: open by assignee, closed by date.
  - SCARs: open and closed.
  - Effectiveness checks: due.

## 12.5 Planned deviations

- **DEV-001** A **planned deviation** shall capture:
  - Deviation information: scope, affected item, part and lot, the requirement being deviated from (linked to the controlled document), justification, and risk assessment.
  - **Validity limits:** start and end date, and/or maximum quantity or number of lots.
  - Related documents, activities and verification.
- **DEV-002** Workflow: **Draft → Review → Initial Approval → Final Approval → Active → Closed/Expired**.
  - Each phase can go back one step. Void is possible at Final Approval.
  - Verification is recorded while Active.
  - Vellum adds explicit closure: deviations close **automatically on expiry** (end date reached or quantity used up), or by hand.
- **DEV-003** [SAFETY] While a deviation is Active, every linked material, part or item profile shall show a warning: **"This item has an active deviation"**, with a link. When it expires, the warning shall be removed and the deviation's owner notified ([INV-119](21-invariants.md)).
- **DEV-004** Deviations may be created from NCRs (*Use As Is*), CAPAs, PPAP dispositions and inspection follow-ups.

## 12.6 Laboratory investigations (OOS/OOT)

- **LAB-001** A **lab investigation** shall capture:
  - Type of result (**OOS** or **OOT**).
  - Batch information.
  - Test details: analyst (defaults to the current user), linked **approved** reference documents (SOP, test method).
  - Equipment information, with the last calibration date looked up from Assets ([AST](18-assets.md)).
  - Lab investigation checklist.
  - *Is additional testing required?*
  - **"Lab error identified and original results invalidated?"**
  - Type of lab error, lab investigation summary.
  - Root cause and final conclusion, related lab investigations.
  - *Is an NCMR required?*, *Is a CAPA required?*
  - The **full (manufacturing) investigation**: reason, checklist, results.
- **LAB-002** Workflow: **Initiate → Lab Investigation →**
  - if a lab error was identified: **Final Approval → Closed**;
  - if not: **Lab Investigation Approval → Full Investigation → Final Approval → Closed**.

  Answering *No* to the lab-error question shall switch the phase tracker to the full-investigation path and set *full investigation required = Yes* ([INV-044](21-invariants.md)). Back-routes are as described in the source, with comments.
- **LAB-003** [SAFETY] [P11] **Original results may be invalidated only when an assignable lab error is identified and recorded.** Without one, the full investigation is mandatory. This follows FDA guidance on investigating OOS results.
- **LAB-004** **Testing activities** (re-testing and re-sampling), created from the *additional testing* subform during Lab Investigation or Full Investigation, shall have:
  - The workflow **Initiate → QC Approval → Testing in Progress → Final Approval → Closed**.
  - Inherited batch and general information.
  - A number of replicates, with one row per replicate holding the actual result and whether it *meets specification*.
- **LAB-005** [SAFETY] **Segregation of duties:** a **re-test** must be assigned to a **different** analyst from the one who ran the original test. A **re-sample** goes to the **same** analyst, filled in automatically ([INV-045](21-invariants.md)).
- **LAB-006** Gating:
  - Testing activities must be complete before the Full Investigation phase can be left.
  - All Action Items must be complete before any phase advance.
  - NCMR or CAPA *required = Yes* needs the linked record to exist (QE-005).
- **LAB-007** E-signatures shall be required on: void (with a reason), Lab Investigation Approval → Full Investigation, Final Approval → Closed, testing activity QC approval and close, and Action Item close [P11].
- **LAB-008** *(Vellum, should)* Lab results may be received from a LIMS ([INT-202](06-integrations.md)), which creates OOS investigations automatically when a result is outside specification.

## 12.7 Customers, feedback and complaints

### Customer profiles and feedback

- **CFB-001** **Customer profiles** shall hold account information, addresses, **contacts** and a **feedback history**. The history automatically lists every closed and voided feedback record for the customer. Customer profiles are the lookup source for change, audits, NCR, quality events, RMA and projects.
- **CFB-002** **Feedback** shall be created from a customer profile (*New Feedback*), with contacts limited to that profile's contacts. Workflow: **Investigation → Approval → Completed**, with void possible from Approval. Feedback captures feedback information, product information, the investigation and the action taken. It may create a CAPA, an RMA or a risk assessment. *Notify* sends a mail activity to the customer.

### Complaints

- **CMP-001** A **complaint** shall capture:
  - Complaint description and product information: product, model, **lot/serial/UDI**, quantity.
  - Reporter information.
  - **Event/awareness date** (the date the company first became aware) and site where reported.
  - Patient involvement (for medical products).
  - Response to the customer.
  - Risk assessment section.
  - Additional information (create NCMR, CAPA or another activity).
  - **Diligence log**, activities and related complaints.
- **CMP-002** Workflow: **Draft → Investigation → Review → Awaiting Closure → Closed**, plus Voided.
  - Draft may skip straight to Review.
  - Investigation can go back to Draft. Review can go back to Draft or Investigation. Awaiting Closure can go back to Review. All back-routes need comments.
  - Void is possible in Review.
- **CMP-003** Activity options per complaint:
  - **Initial assessment.**
  - **Complaint investigation** (Draft → Open → Approval → Completed).
  - **MDR decision tree:** a configurable yes/no question tree ending in **"Reportable to FDA?"**, plus a data-mapping tab that controls which complaint data goes into the MedWatch report.
  - **ADI/vigilance decision tree:** for adverse device incidents, determines **vigilance (MDV) reportability**, for example EU MDR Article 87.
- **CMP-004** **All MDR and vigilance decision-tree activities must be completed before the complaint can move to the next phase** ([INV-046](21-invariants.md)).
- **CMP-005** When an MDR tree concludes *reportable*, **Create MedWatch** shall become available in the tree's Open phase. It creates a MedWatch record in Adverse Event Reporting (§12.8) with the mapped data. Only **one initial (or 5-day) MedWatch** may exist per MDR tree for MFR-type report numbers, unless the existing one is voided ([INV-047](21-invariants.md)).
- **CMP-006** In **Awaiting Closure**, every complaint field shall be read-only. The only things that can still be created are Action Items and diligence log entries.
- **CMP-007** **Diligence log entries** record attempts to get information: date, method, contact, outcome. Each has a mini-workflow (*Open → Completed*), and only completed entries appear in the log.
- **CMP-008** Related records (risk assessments, NCMRs, CAPAs) shall be linked back to the complaint automatically when saved, and their progress shall be shown on the complaint.
- **CMP-009** [SAFETY] *(Vellum extension. Reliance's public documentation doesn't describe this.)* **Regulatory reporting clocks** shall run from the **awareness date**:
  - **FDA MDR:** 30 calendar days, or 5 working days for events that need remedial action to prevent unreasonable risk of substantial harm (21 CFR 803).
  - **EU MDR vigilance:** 2, 10 or 15 days depending on severity.
  - **Other jurisdictions:** configurable.

  The deadline shall be shown on the complaint and the MedWatch record, and escalations shall fire at configurable points before it. Missing a deadline shall raise a critical alert to the regulatory affairs group ([SAF-020](20-safety.md)).
- **CMP-010** *(Vellum extension, should)* Complaint **trending**: by product, failure mode, lot and site, with alert templates ([RPT §7.6](07-reporting.md)) for signal detection.
- **CMP-011** The **Complaint & Feedback Advisor** AI assistant ([INT-1003](06-integrations.md)) shall be available on complaint and feedback forms when enabled. [AI]
- **CMP-012** Packaged views:
  - Open complaints by assignee; investigations by site.
  - Open MDR trees; open ADI trees.
  - Complaints awaiting closure; voided complaints; closed complaints.
  - Action items.
  - Complaints approaching their reporting deadline.

## 12.8 Adverse event reporting (eMDR & vigilance)

- **AER-001** **Setup** ([INT-902](06-integrations.md)):
  1. An eMDR profile (one per environment).
  2. A singleton **application profile** with:
     - **MFR/importer mapping** on or off.
     - **Submission receipt delay** (minutes to wait for acknowledgements before flagging a timeout).
     - **E-signature meanings** for *Submit to FDA*, *Complete* and *Void*.
  3. One **MFR and importer mapping** per location:
     - Reporter type: *manufacturer* or *importer*.
     - Manufacturer code: *basic* (static) or *advanced* (calculated by rule).
     - Sequence format: *manual* or *sequential*, with an initial number.
     - Importer code and its sequence format.
  4. A **product code** table (device name ↔ FDA product code), which can be imported.

  A location shall have **one** manufacturer code. Several locations may share a code.
- **AER-002** The **application profile must exist** before any MedWatch can be created.
- **AER-003** The **MedWatch** record (FDA Form 3500A content) shall include:
  - Report number(s): MFR, user facility and/or importer. Each has three parts: code, year and sequence.
  - **Location**, taken from the complaint's site where reported when mapping is on.
  - Patient information.
  - Adverse event/problem codes, using **FDA IMDRF codes**.
  - Suspect products/devices, initial reporter, device manufacturers.
  - Report type: initial, 5-day, supplemental/follow-up.
  - An attachments-and-outputs tab with submission acknowledgements.
  - An activities-and-approvals tab with the MedWatch approver.
  - *Confirm final acknowledgement*.
- **AER-004** Workflow: **Draft → Approval → Submitted to FDA → Completed**, or **Voided**.
  - Approval can go back to Draft, go forward (e-signed submission) or void (e-signed).
  - Submitted to FDA → Completed is e-signed [P11].
- **AER-005** Report number generation:
  - With mapping **off**, all three parts are entered by hand in Draft.
  - With mapping **on**, the code part is filled in automatically on entry to *Submitted to FDA*.
    - **Sequential** format: the sequence number is generated then too, as the next number after the last *initial* MedWatch from the same mapping in the **same year**. The sequence resets each year.
    - **Manual** format: the user enters the sequence in Draft.
  - Generated numbers shall be unique ([INV-023](21-invariants.md)).
- **AER-006** Data shall be validated against the **current FDA eMDR schema and business rules** before submission. Missing or invalid data blocks routing with field-level errors. The platform shall track FDA form and schema versions and update the validation rules when the FDA publishes changes.
- **AER-007** Action Items may be created in Draft, Approval and Submitted to FDA. Every Action Item created in Draft or Approval must be complete before the record moves to *Submitted to FDA*.
- **AER-008** On entry to *Submitted to FDA*, the system shall:
  1. Generate the FDA-format XML (HL7 ICSR).
  2. Send it through the gateway.
  3. Receive and process the **three acknowledgements**, creating an acknowledgement record for each and linking it.
  4. Show any rejection or error on the record and alert the submitter.
- **AER-009** **Completion** shall require all three acknowledgements to be received and *confirm final acknowledgement = Yes* ([INV-048](21-invariants.md)).
- **AER-010** [SAFETY] If acknowledgements don't arrive within the configured receipt delay, or a rejection arrives (ACK2/ACK3 errors), the system shall raise a high-priority task to regulatory affairs with the error detail. A submission shall never stay silently pending past its reporting deadline (CMP-009).
- **AER-011** **Supplemental/follow-up reports** shall be creatable from a completed MedWatch, carrying the original report number and changed sections.
- **AER-012** *(Vellum, should)* EU MDR **Manufacturer Incident Report (MIR)**, **FSCA/FSN** and **PSR/PMS** records follow the same pattern ([INT-903](06-integrations.md)).
- **AER-013** Packaged views: open MedWatch reports (by assignee), submitted (by number), completed (by number), acknowledgements (MFR, importer), action items, the application profile, product codes, and mappings by location.

## 12.9 Customer returns (RMA)

- **RMA-001** A **customer return** shall capture:
  - Return information, and customer information (looked up from customer profiles when licensed).
  - Problem description.
  - A **part list** (part, quantity, unit value, **total = quantity × value**).
  - **Receiving:** part, ship date, quantity received, inspected and voided.
  - Inspection, disposition, **accounting disposition** (credit, replacement, repair charge), and verification and effectiveness.
- **RMA-002** Workflow: **Draft → Initial Approval → Receiving Inspection → Disposition → Verification → Completed**.
  - Initial Approval can go back or void.
  - Every later phase can go back one step.
  - Tabs appear phase by phase. Return information becomes read-only from Receiving Inspection on.
- **RMA-003** **Load parts list** shall copy the part list into the receiving and inspection tabs. Manual entry is also allowed.
- **RMA-004** Received quantities shall not exceed authorized quantities without an override and a justification ([INV-066](21-invariants.md)).
- **RMA-005** RMAs may be created from customer feedback and complaints, and may create NCRs, CAPAs and action items.
