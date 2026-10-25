# 16. Risk Management, Design Risk and Food Safety

**Parity sources:** Reliance help › Risk Register, Enterprise Risk Management, Failure Mode and Effects Analysis (Design Project, SRA, Control Plan, Labeling), Hazard Analysis and Critical Control Points (HACCP); Octave product page › Risk Register; food and beverage product pages (FSMA, SQF, HACCP).

## 16.1 Risk Register

### Risk matrix templates

- **RSK-001** **Risk assessment templates** (risk matrix profiles) shall define one or more **risk rows**. Each row has a title and **up to 4 dimensions** (for example severity, likelihood, detectability, exposure). Empty dimensions aren't shown on assessments ([INV-174](21-invariants.md)). *(Vellum should allow up to 6.)*
- **RSK-002** Each dimension shall have a **risk values** list: keywords with numeric values (for example *Catastrophic = 5*). The list can be edited only after the template's first save.
- **RSK-003** Each risk row shall define how its dimensions are **combined** into a risk value: **product**, **sum**, **maximum**, or a **lookup table** (a 2D or n-D matrix of cell values). *(Reliance doesn't document its formula. Vellum makes it explicit and configurable.)* [CFG]
- **RSK-004** **Risk results** ranges map value ranges to a description, **color**, **guidance** (for example "Unacceptable: stop work and escalate") and an optional matrix image. The ranges shall cover the whole possible value domain without overlaps. Any gap shall be reported when the template is saved ([INV-175](21-invariants.md)). A value outside every range shows **"No matching results"**.

### Library objects

- **RSK-005** **Controls** shall be library records (control identification, type, owner). Workflow: *Draft → Approve (assigned to the control owner) → Completed*, which can send back with comments or void.
- **RSK-006** **Escalation factors** shall be library records. Workflow: *Draft → Approve (assigned to the approver) → Completed*. Each gets a unique number.
- **RSK-007** **Risk items** (hazards) shall hold:
  - A **hazard name**, which must be **unique within the same department and location** ([INV-024](21-invariants.md)).
  - Department, location and description.
  - **Risk history:** every assessment of the item, each with its risk color, as a time series.
  - **Threats**, with their **preventive controls**.
  - **Consequences**, with their **recovery controls**.
  - An optional **bowtie** section: a diagram attachment and *import bowtie file*. Threat and consequence profiles get unique numbers.
- **RSK-008** *Update risk assessment* on a risk item shall create a new assessment linked to it.

### Risk assessments

- **RSK-009** A **risk assessment** shall present the template's matrix:
  - The user picks a keyword for each dimension of each risk row.
  - *Calculate results* shows a colored box per row with the numeric value, description and guidance.
  - *Show analysis fields* reveals: problem description or failure mode, potential risk description and effect, assignment (assessors and due date), and analysis (assessment explanation and recommended actions).
  - Links to related assessments made from the same source.
- **RSK-010** **Event risk classification** shall be a simplified assessment with a single *safety risk* dimension. Its analysis fields include assignment only.
- **RSK-011** Workflow for risk assessments and event risk classifications: **Draft → Assigned (assessor) → Approval → Completed**. Assigned can go back to Draft. Approval can go back to Assigned or void.
- **RSK-012** Risk assessments shall be creatable from: incidents, aspects, requirements, SDSs, JSA steps (initial and residual), ERM surveys, complaints, NCRs, customer feedback, quality events, lab investigations, material profiles, change records and supplier profiles. The source record shows the linked assessments with colors and the **highest** result rolled up ([QE-010](12-quality-events.md)).
- **RSK-013** A **Corrective Action** button on a risk assessment shall create a CAPA that inherits the assessment's data.
- **RSK-014** [SAFETY] *(Vellum extension)* Assessments whose result falls in a band flagged **unacceptable** shall need a mitigation (a CAPA, control or action) or a documented risk acceptance by a role with acceptance authority (e-signed). The assessment can't be completed without one of these ([INV-176](21-invariants.md)).
- **RSK-015** *(Vellum, should)* A **risk heat map** chart ([RPT-103](07-reporting.md)) and a **risk register report** (items, current risk, trend, controls, owner).
- **RSK-016** The risk-item lookup shall be hidden in applications when the Risk Register isn't licensed (licensing-aware UI).

## 16.2 Enterprise risk management (ERM)

- **ERM-001** **Survey templates** shall hold a department and **risk items** rows. Each row's risk lookup copies the item's risk category and link from the Risk Register. A template must be saved before plans can use it.
- **ERM-002** A **risk plan** shall hold:
  - Plan information.
  - A **risk survey** subform: survey template (which fills in the department) and assessor.
  - A **cutoff value**, applied with *update results*.
  - A **control plan**, which appears in *Implement Control Plan*.
- **ERM-003** Risk plan workflow: **Planning → Survey Distribution → Implement Control Plan → Awaiting Closure → Completed**. Survey Distribution can go back to Planning. Awaiting Closure can go back to Implement Control Plan.
- **ERM-004** Entering *Survey Distribution* shall **create one risk survey per survey row** from its template, assigned to the assessor.
- **ERM-005** Risk surveys (*Draft → Assigned → Awaiting Closure → Completed*) shall hold three lists:
  - **Risks** (from the template).
  - **Other risks** (ad-hoc risk items picked from a view).
  - **New risks** (proposed).

  For each risk, the assessor runs an *assess risk* (risk assessment) and records the **cost of avoidance** and **cost of implementation**. Ad-hoc rows can be removed only if they have no assessment. At *Awaiting Closure*, *create risk item* turns a new-risk row into a pre-filled Risk Register item.
- **ERM-006** The **control plan** shall list each risk whose assessed value is **≥ the cutoff**. When the cutoff changes, *update results* removes rows that are now below it, **except rows that already have actions** ([INV-177](21-invariants.md)). Each row's **action** link fills in automatically, so progress can be tracked.

## 16.3 Design and process risk: FMEA, SRA, control plans and labeling

### Design projects and hazard profiles

- **FMEA-001** **Design projects** shall contain the FMEAs, system risk analyses, control plans and labeling records for a product or design. Workflow: **Draft → Review → Approval → Approved**. Review can send back. Approval can send back or void.
- **FMEA-002** **Hazard profiles** (a hazard library) shall hold a hazard code, hazard, adverse effect, effect on, severity, type and a disable flag. Disabled profiles can't be picked.

### FMEA

- **FMEA-003** **FMEA** records (design, process, or **supplier FMEA**, which suppliers complete through the portal or a template) shall hold the FMEA worksheet and an action plan. Workflow: **Pending → Assigned → Completed**. Assigned can send back or void.
- **FMEA-004** The FMEA worksheet shall be **native and server-calculated** (Vellum), with Excel import and export for parity. Columns:
  - Unique ID (automatic), function ID, function or process step.
  - Failure mode ID, failure mode.
  - Effects, which must state any safety or regulatory noncompliance.
  - **Severity (S)**, potential causes, **occurrence (O)**.
  - **Class:** potentially critical, potentially significant, critical, significant.
  - Prevention controls, detection controls, **detection (D)**.
  - **RPN = S × O × D**.
  - Recommended actions, implemented actions.
  - Revised S/O/D, **revised RPN**, **reduction %** = (RPN − revised RPN) / RPN.

  RPN, revised RPN and reduction % are calculated and can't be edited ([INV-178](21-invariants.md)).
- **FMEA-005** *(Vellum, should)* The **AIAG-VDA FMEA (2019)** method as an option: structure, function and failure analysis; **action priority (AP: High / Medium / Low)** tables in place of RPN; and an optional MSR (monitoring and system response) section.
- **FMEA-006** [SAFETY] Rows with severity ≥ a configured threshold (default 9), or with RPN or AP above a threshold, shall need a recommended action or a documented justification before the FMEA can be completed ([INV-179](21-invariants.md)).
- **FMEA-007** *Assign action item* shall turn action plan rows into Action Items (*Pending → Verification (if required) → Completed*).
- **FMEA-008** FMEAs shall support **related FMEAs** (system → design → process cascades). **Control plans** can be created straight from a process FMEA.

### Control plans

- **FMEA-009** **Control plans** shall have the workflow *Pending → Assigned → Completed* (void possible at Assigned). Each row's ID and process step are **synced from the parent process FMEA**. The user adds process characteristics (product/process, special characteristic class), specification and tolerance, evaluation and measurement technique, sample size and frequency, control method and **reaction plan**.

### System risk analysis (ISO 14971)

- **FMEA-010** A **system risk analysis** (SRA) shall have hazard rows looked up from hazard profiles, which fill in the hazard data. The user enters:
  - Occurrence and risk.
  - **Risk control measures**, coded **E** (eliminate by design), **M** (mediate with protective measures or alarms) or **W** (warnings: labeling, instructions for use, training), in that priority order.
  - Risk control description.
  - **Implementation verification** and **effectiveness verification**.
  - Post-control occurrence and **residual risk**.

  It also holds action items and a **labeling** tab. Workflow: *Pending → Assigned → Completed* (void possible).
- **FMEA-011** [SAFETY] Every hazard row with risk above *acceptable* shall have at least one risk control, with **both** verifications recorded before the SRA can be completed. The overall **residual risk acceptability** (benefit-risk) shall be recorded with an e-signature [P11] ([INV-180](21-invariants.md)).
- **FMEA-012** **Labeling** records (*Pending → Assigned → Completed*, void possible) shall hold a labeling reference (a linked controlled document or artwork) as a *W* risk control, linked both ways to the SRA rows that rely on it.
- **FMEA-013** Packaged views: hazard profiles; design projects (pending, approved, voided); FMEAs (pending, completed); SRAs, control plans and labeling records (each pending, completed, voided); action items (pending, closed).

## 16.4 Food safety: HACCP and prerequisite programs

### Prerequisite programs

- **HACCP-001** **Prerequisite programs (PRP)** and **operational prerequisite programs (oPRP)** shall be revision-controlled program documents. Workflow: **Draft → Approval → Awaiting Release → Completed**. Approval can send back or void. Awaiting Release can go back to Approval.

### HACCP plan

- **HACCP-002** A **HACCP plan** shall hold:
  - Plan and company information.
  - **HACCP team:** member, role, department, *is external?* (which adds address and contact details).
  - **Product information:** product details, ingredients and raw materials by category, intended use and consumers, and specification items linked from product specifications ([PSM](15-supplier-quality.md#154-product-specifications-and-bills-of-materials)).
  - **Process flow:** steps from primary production to consumption, plus a diagram attachment.
  - **Hazard analysis.**
  - **CCP and critical limits.**
  - **Monitoring and corrective actions.**
  - **Verification.**
  - **Validation.**
- **HACCP-003** HACCP plan workflow, following the Codex 12 steps and 7 principles: **Define → Describe the Product/Process → Define the Process Flow → Conduct Hazard Analysis → Determine CCPs → Establish Critical Limits → Monitoring and Corrective Actions → Establish Verification Procedure → Validate the Plan → Awaiting Release → Completed**.
  - Every phase from *Describe* to *Establish Verification* can go back to *Define*.
  - Validate can go back, void or go forward.
  - Each tab is editable only in its own phase.
  - Plans are revision-controlled.
- **HACCP-004** In **hazard analysis**, the steps come from the process flow. Each step records the hazard type (biological, chemical including **allergens**, physical, radiological; Vellum adds fraud and intentional adulteration as an option), *significant?* (required), and control measures when significant. A **CCP decision tree** gives the result **CCP**, **oPRP** or **PRP**. The monitoring and supporting-documentation sections are shown only for steps with one of those results.
- **HACCP-005** Each CCP shall define: **critical limits** (parameter, min and/or max, unit), monitoring (what, how, frequency, who), corrective action procedure, verification activities and records.
- **HACCP-006** *Validate the plan* shall require a **validation record** to be attached, and the approver's decision that the plan is valid (e-signed [P11]).
- **HACCP-007** The verification section shall fill in automatically with each step's hazard description, CCP and critical limits.

### Monitoring and verification records

- **HACCP-008** **Monitoring records** and **verification records** may be created **only from a Completed (effective) plan** ([INV-181](21-invariants.md)). Verification records may also be created while the plan is in *Establish Verification Procedure*. Both inherit plan data, and their link to the plan shows the HACCP number, product, **revision**, phase and due date. Monitoring records are numbered automatically.
- **HACCP-009** [SAFETY] *(Vellum extension. Reliance's evaluation is manual.)* Monitoring results shall be **automatically evaluated against the critical limits** of the plan revision in force:
  - A **deviation** (a value outside the limits) shall mark the record *out of limits*.
  - It shall require a **corrective action** (the launch-corrective-action button becomes mandatory) **and** a **product disposition**: hold, rework, destroy, or release after evaluation, linked to the affected lots.
  - It shall notify the HACCP team at once ([SAF-020](20-safety.md)).
- **HACCP-010** [SAFETY] *(Vellum extension, should)* **Monitoring schedules** from each CCP's frequency shall create due monitoring tasks. Missed monitoring shall escalate.
- **HACCP-011** Plans shall show **HACCP status by process or product** (dashboard parity).

### FSMA and food-safety program extensions (Vellum extensions, should; relevant to food and ingredient manufacturers)

- **HACCP-020** **FSMA preventive controls (21 CFR 117 Subpart C)** food safety plans: hazard analysis, process, allergen, sanitation and supply-chain preventive controls, recall plan, PCQI sign-off.
- **HACCP-021** **Supply-chain program (21 CFR 117 Subpart G)**: approved suppliers per hazard, verification activities (audits, sampling, record review) and their frequencies, linked to supplier profiles ([SUP](15-supplier-quality.md)).
- **HACCP-022** **Food defense plans (21 CFR 121)** and **food fraud vulnerability assessments**.
- **HACCP-023** **Recall management and mock recalls:**
  - Lot genealogy tracing, one step forward and one step back, fed by ERP lot data ([INT §6.2](06-integrations.md)) or imported.
  - Recall classification.
  - Customer notifications.
  - Effectiveness checks.
  - **Timed mock recall drills** that record the trace time and the percentage of product accounted for.
  - **FSMA 204 key data elements** for foods on the food traceability list.
- **HACCP-024** **Environmental monitoring program (EMP)** for pathogens: zones 1–4, sampling sites, schedules, results, and positive-result escalation with vector swabbing and corrective actions.
