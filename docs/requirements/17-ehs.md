# 17. Environmental, Health and Safety (EHS)

**Parity sources:** Reliance help › Accidents and Safety Reporting, Behavior Based Safety, Job Safety Analysis, Permit Control, Aspects/Objectives/Targets, Monitoring and Inspection, Emergency Preparedness Plans, Compliance Obligations, Safety Data Sheet; Integrations › RegScan, Compliance Obligations section; Octave product page › Health and Safety, Environmental Management; third-party listings (hazardous waste tracking, case and claims management).

Shared building blocks used here are defined in [§12.1](12-quality-events.md#121-shared-building-blocks): the **action item** with optional verification, **CAPA** spawning, and **risk assessments** from the Risk Register.

## 17.1 Incidents, injuries and OSHA recordkeeping

### Incident records

- **EHS-101** **Incident records** shall capture: event type (drives which conditional sections appear), who completed it, incident information (description, place, date and time, location), initial containment action, witness statements (one row per witness), *investigation required?*, *corrective action required?*, linked risk assessments (showing the **highest** risk result among them), cost line items with a calculated total, and an activities tab.
- **EHS-102** Event types and their conditional sections ([CFG]):

  | Event type | Section shown |
  |---|---|
  | Environmental incident, natural disaster/earthquake, fire/explosion | **Environmental incident report**, including an ISO 14001 emergency-procedure compliance check |
  | Injury/illness, fatality | **Injury/illness reports** (child records) |
  | Production loss | Production loss |
  | Property damage | Property damage |
  | Mobile equipment | Mobile equipment |
  | Security incident | Security incident |
  | Near miss *(Vellum)* | Near-miss details, potential severity |

  The environmental incident report shall be editable in Draft only.
- **EHS-103** Incident workflow: **Draft → Initial Review → (Investigation, only if *investigation required?* = Yes) → Final Review → Completed**. Routes back are Investigation → Initial Review and Final Review → Investigation (or Initial Review when the investigation was skipped). Void is possible from Initial Review or Final Review. The Investigation tab appears from the Investigation phase on.
- **EHS-104** Action items may be created on an incident (Reliance allows this in Draft only; Vellum allows it in any open phase, [CFG]). Corrective actions may be created only when *corrective action required?* = Yes, and they show up in *corrective action links*.
- **EHS-105** Root-cause investigation tools ([§12.1](12-quality-events.md#121-shared-building-blocks)) shall be available on the Investigation tab.

### Injury/illness reports

- **EHS-106** **Injury/illness reports** (activities of an incident) shall capture:
  - **Person:** category *employee* (looked up from the employee directory) / *visitor* / *contractor* (free text, with employee-only fields hidden).
  - **Injury/illness information:** OSHA case classification (death, days away from work, job transfer or restriction, other recordable), body part, nature of injury, cause, object or substance.
  - **Fatality:** date of death, which becomes required when fatality is selected.
  - **OSHA recordable** (Y/N). Yes reveals the OSHA injury/illness type code.
  - **Time-off events:** days away, days of job transfer or restriction (with start and end dates).
  - **Treatment:** physician or provider (including on-site), emergency-room visit, overnight hospitalization.
- **EHS-107** Saving an injury/illness report shall add a link, labeled with the person's name, to the parent incident.
- **EHS-108** Workflow for injury/illness reports: *Draft → Active → Completed*, with Active able to go back to Draft.
- **EHS-109** [SAFETY] Personal and medical fields on injury/illness reports shall be flagged **sensitive** by default ([SEC-520](04-security.md)). For **privacy-concern cases** (29 CFR 1904.29(b)(7)), the employee's name shall be **masked** on the OSHA 300 log ("Privacy Case") while the case is still counted.

### OSHA reports

- **EHS-110** **OSHA 300 log:** for a chosen location (including lower locations) and period, the system shall compile every recordable case. Each row has case number, employee name (or "Privacy Case"), job title, date of injury or illness, where it happened, a description, classification, days away, days restricted or transferred, and injury/illness type. The log is produced as a filled-in official-format file (Excel/PDF). *Unlike Reliance, the number of rows shall not be limited by the template.*
- **EHS-111** **OSHA 300A summary:** totals by classification and by injury/illness type, total days away and total days restricted or transferred, plus establishment information, annual average employees and total hours worked (from *Employee Hours Worked*). It shall be ready for certification by a company executive, captured as an e-signature [P11].
- **EHS-112** **OSHA 301 incident report:** one per recordable case, produced from the injury/illness report.
- **EHS-113** **Employee hours worked** records (location and period) shall feed the 300A and the incident-rate KPIs ([RPT-702](07-reporting.md)).
- **EHS-114** [SAFETY] **Reporting deadlines:** when an incident records a **work-related fatality**, the system shall start an urgent **8-hour** OSHA reporting task. For an **in-patient hospitalization, amputation or loss of an eye**, it shall start a **24-hour** task (29 CFR 1904.39). The task goes to the EHS coordinator, escalates every hour, and its countdown is shown on the record. The rules are configurable per jurisdiction ([SAF-020](20-safety.md)). *(Vellum extension. Reliance's public documentation doesn't describe this.)*
- **EHS-115** *(Provisional)* Electronic submission of 300A/300/301 data to OSHA ITA shall be supported through export in the ITA CSV format, or through the gateway pattern in [INT-903](06-integrations.md).
- **EHS-116** Non-US recordkeeping (for example RIDDOR, WorkSafe) shall be configurable through classification and report templates.

## 17.2 Behavior-based safety observations

- **EHS-201** **Observation templates** shall have a **unique title**, job positions, locations, instructions, reference links, attachments and questions. Each question has a category, text, answer format (*Yes/No*, *Yes/No/N/A*, *Safe/Unsafe*, *Safe/Unsafe/N/A*) and *allow comments and attachments*. Yes means Safe, No means Unsafe, and N/A is not applicable.
- **EHS-202** An **observation event** shall be created from a template, copying its values and questions (all editable). It also has a distribution list (observers) and a response date. Events can't be created until a template exists.
- **EHS-203** Event workflow: **Draft → Observation → Review → Completed**. Void is allowed in Draft only and requires a comment. Entering *Observation* shall **auto-create one observation record per observer**, assigned with the response date. The event shows a live list of the records with assignee and status.
- **EHS-204** Observation records shall have the workflow *Open → Completed*. They inherit the event data, collect answers with optional per-question comments and attachments, calculate an **observation result**, and may create an **incident**.
- **EHS-205** The **observation result** shall be the percentage safe: safe answers ÷ (safe + unsafe answers) × 100. N/A answers are excluded. It is calculated per record, per category and per event. *(Reliance doesn't document its scoring formula. Vellum defines one.)*
- **EHS-206** The event can't move to Review while any observation record is still pending ([INV-077](21-invariants.md)). At Review, the reviewer may create action items per question, and corrective actions and action items at event level. An event summary holds conclusions and attachments.
- **EHS-207** Charts: **score by observation category**, drilling down to questions and then to the view.

## 17.3 Job safety analysis (JSA)

- **EHS-301** A **JSA** shall hold JSA information, owner and reviewer, a **job steps** subform, action items, related JSAs and a compliance obligations section. Each step records the step, the hazard, the **initial risk assessment**, the controls, the **PPE** and the **residual risk assessment**.
- **EHS-302** JSA workflow: **Define (Draft) → Review (reviewer) → Approval (owner) → Awaiting Release → Approved**. Routes back: Review → Define, Approval → Review, Awaiting Release → Define. Void is at Approval only. JSAs shall be revision-controlled ([DOC §10.2](10-document-control.md)).
- **EHS-303** Related JSAs may link only to JSAs that are pending, awaiting release or approved.
- **EHS-304** [SAFETY] The residual risk of each step shall be **≤** its initial risk. Otherwise approval needs a justification ([INV-078](21-invariants.md)). Steps whose residual risk is in a configured *unacceptable* band shall block approval.
- **EHS-305** *(Vellum extension, should)* Approved JSAs can feed Training (a course per JSA for the job positions concerned) and permit-to-work records (EHS §17.4).

## 17.4 Permits

### Regulatory permits

- **EHS-401** **Permit records** shall have:
  - **Permit information**, with permit-type-specific tabs ([CFG]).
  - **Application details.**
  - **Related permits.**
  - **Issuing authority, permit number, effective date, expiration date and renewal lead time.**
  - **Permit conditions/limits subform:** parameter, limit, unit, monitoring frequency and reporting obligation. These link to monitoring (EHS §17.6).
- **EHS-402** Permit workflow: **Initial Application → (Renewal Application, renewals only) → Application Review → Legal Review → Approved for Submission → Active**. Application Review may void or go forward. Legal Review may go back to Application Review or forward. Approved for Submission may void or go forward.
- **EHS-403** **Renewal:** a change request on an Active permit, accepted in *Accept Permit Renewal*, shall create a **new revision** starting in *Renewal Application*. The prior revision stays Active until the new revision is Active, and is then superseded ([DOC §10.10](10-document-control.md) semantics).
- **EHS-404** [SAFETY] The system shall send **renewal reminders** at the renewal lead time before expiration, escalating as expiration nears. At expiration, a permit with no active renewal shall be flagged **Expired**, and the alert goes to the EHS coordinator and facility contacts. *(Vellum. Not described in Reliance's documentation.)*
- **EHS-405** Permits shall be importable in bulk.

### Permit-to-work (Vellum extension, should)

- **EHS-406** Work permits for hot work, confined space, working at height, line break and lockout/tagout should support: a checklist of required precautions, isolation points, gas test readings with acceptable limits, authorization signatures (issuer, receiver, area authority) [P11], validity window, suspension and revalidation, and closeout.

## 17.5 Environmental aspects, objectives and targets

- **EHS-501** **Aspect** records (ISO 14001 §6.1.2) shall capture: activity, product or service; aspect; impact; conditions (normal, abnormal, emergency); significance evaluation (through a risk assessment); and related aspects.
- **EHS-502** Aspect workflow: **Draft → Review → Approval → Awaiting Release → Completed**. Void is at Approval. Aspects are revision-controlled.
- **EHS-503** Risk assessments may be created from an aspect from Draft through Awaiting Release. Corrective actions may be created from Draft through Completed.
- **EHS-504** **Objectives** may be created **only from a completed aspect** ([INV-079](21-invariants.md)). Workflow: **Draft → Review → Approval → Open → Completed**. Objectives hold **targets** (target, metric, baseline, goal, percent complete, completed date, action taken, owner, due date).
- **EHS-505** An objective can complete only when every target is 100% complete or closed with a justification.
- **EHS-506** Views: pending aspects, approved aspects, open objectives (with a targets report showing percent complete), closed objectives.

## 17.6 Environmental monitoring and management

### Monitoring

- **EHS-601** **Monitoring test plans** shall list planned tests: parameter, location or emission point, method, frequency, limits and responsible person. Workflow: **Draft → Review → Approval → Awaiting Release → Approved**. Void is at Approval. Plans are revision-controlled.
- **EHS-602** **Test records** shall be created only from an **approved** plan. They hold a result and status per test, plus additional information and a final status, and they may raise corrective actions. Workflow: *Draft → Completed*.
- **EHS-603** [SAFETY] Results shall be evaluated **automatically** against limits (from the plan and any linked permit conditions). An **exceedance** marks the result as failing, notifies the EHS coordinator, and offers or requires (configurable) a corrective action or incident. It also flags any regulatory reporting duty on the permit condition. *(Vellum extension. Reliance's evaluation is manual.)*
- **EHS-604** *(Vellum, should)* Test plans should generate scheduled test tasks from their frequency, with due-date escalation.

### Environmental management (provisional, open question 6)

- **EHS-610** *(Provisional)* **Waste tracking**, from generation to disposal: waste stream profile (classification codes, for example EPA RCRA/EWC), accumulation containers and start dates with accumulation-time limits, shipments (manifest number, transporter, TSDF), and disposal certificates. Containers nearing accumulation limits trigger alerts [SAFETY].
- **EHS-611** *(Provisional)* **Emissions and resource tracking**: energy, water, air emissions and GHG (Scope 1 and 2, with Scope 3 optional) by location and period, using configurable emission factors. Includes sustainability KPIs and target tracking (linked to objectives, EHS §17.5).

## 17.7 Emergency preparedness

- **EHS-701** **Emergency plans** shall be revision-controlled, with the workflow **Draft → Review → Approval → Awaiting Release → Approved** (void at Approval). They hold the plan, scope or location, roles and contacts (from facility contacts, [ADM-501](05-administration.md)) and a **drill schedule** subform (drill type, planned date, scope).
- **EHS-702** When training is required, training requirements shall be assigned when the plan reaches **Awaiting Release** ([DOC §10.7](10-document-control.md)).
- **EHS-703** **Emergency drills** shall be created **only from an approved plan's** drill schedule row. Workflow: *Draft → Review → Completed*, with Review able to send back. Drills record participants, timings, observations, results and lessons learned, and may raise corrective actions.
- **EHS-704** [SAFETY] Drill schedule rows shall generate **reminders** before the planned date and escalate overdue drills. *(Vellum. Not described in Reliance's documentation.)*

## 17.8 Compliance obligations

- **EHS-801** **Applicability** records shall be created by hand or from the regulatory feed ([INT-901](06-integrations.md)). Each has applicability information, a **references** subform (reference ID, section, heading, description, link) and an **action plan** tab.
- **EHS-802** Applicability workflow: **Draft → Pending Review → Pending Implementation →** one of **Changes Made**, **Changes Not Needed** or **Void (Not Applicable)**. Entering *Pending Implementation* shall **auto-create the action items** defined in the action plan.
- **EHS-803** Saving an applicability shall **auto-link** every requirement whose clause rows share a reference ID with it.
- **EHS-804** An applicability can't be routed to *Changes Made* until **every** reference row has a requirement with a matching reference ID ([INV-162](21-invariants.md)).
- **EHS-805** **Requirement** records (revision-controlled; workflow **Draft → Review → Approval → Awaiting Release → Approved**) shall hold requirement information and **clause rows** (auditable flag, reference ID, section, heading, clause, compliance description, applicability reasoning, applicability category, task links). They also hold: references (including **audit checklist templates**, created or linked, [AUD](14-audits-inspections.md)); a **compliance obligations** section linking **controls** (documents) and **facilities**; risk assessments; activities; and related documents. A requirement created from a reference row inherits that row's fields.
- **EHS-806** When an approved requirement is revised, the system shall **warn** the owners of linked controls and facilities and create review tasks for them ([SAF-021](20-safety.md)).
- **EHS-807** *Show related documents* on an approved requirement shall list every record whose compliance obligations section references it.
- **EHS-808** **Compliance plans** (workflow **Draft → Approval → Active → Completed**) shall group **compliance tasks** (subject, description, priority, due date, assignee) from Task Management, including recurring ones ([TSK](19-projects-tasks.md#194-task-management-including-recurring-tasks)). Only tasks that aren't completed may be linked. A plan completes only when its tasks are completed or justified.
- **EHS-809** **Compliance obligations sections** may be added to any form (for example documents, JSAs, facilities). Each picks an applicability category, links approved requirements, and takes an optional reference date that defaults to the record's creation date. When the record is opened, the system shall **warn** about every linked requirement whose effective date is later than the reference date. Requirements with no effective date never trigger the warning.
- **EHS-810** Views: open applicability records, pending requirements, requirements awaiting release, open plans and active plans.

## 17.9 Safety data sheets and chemical management

- **EHS-901** **SDS records** shall be revision-controlled, with the workflow **Draft → Review → Approval → Awaiting Release → Approved**. They hold SDS information, the SDS file, the SDS type (*Detailed* shows a details tab with GHS hazard classes, pictograms, H/P statements, exposure limits and PPE), related SDSs and risk assessments.
- **EHS-902** A **material/chemical profile** ([SUP §15.2](15-supplier-quality.md)) may be created from an SDS from Draft through Awaiting Release. Risk assessments may be created **only from approved SDSs**.
- **EHS-903** [SAFETY] SDSs shall have a **review date** (default 3 years, configurable). The system shall warn when an SDS is out of date or when the supplier sends a newer revision. Readers shall always get the **current approved** SDS ([INV-100](21-invariants.md)).
- **EHS-904** *(Vellum, should)* **Chemical inventory** by location (container, quantity, storage area), with storage-compatibility warnings and threshold reporting (for example Tier II) supported by exports.
- **EHS-905** SDSs shall be readable on mobile and through an optional public, unauthenticated **SDS lookup** page per facility, using a QR code. It shows approved SDSs only (EHS's right-to-know duty) ([SEC-801](04-security.md)).

## 17.10 Case and claims management (provisional)

- **EHS-1001** *(Provisional)* Workers'-compensation **claims** linked to injury/illness reports shall track: claim number, insurer or TPA, status, reserves, payments, return-to-work and restrictions, and closure. All of it is sensitive data ([SEC-520](04-security.md)).
- **EHS-1002** *(Provisional)* **Occupational health cases**: medical surveillance (for example audiometry, respirator fit), exams due and their results, linked to job positions and exposures.
