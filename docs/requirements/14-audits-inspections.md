# 14. Audits and Inspections

**Parity sources:** Reliance help › Audits Management, Inspection Management (including mobile differences), Compliance Obligations (audit checklist templates).

## 14.1 Audit setup

### Checklist templates

- **AUD-001** **Checklist templates** shall be reusable, auto-numbered question banks. They can be disabled, and disabled templates can't be used to create checklists. When locations are in use, template pickers shall be filtered by the audit's location.
- **AUD-002** Each **question** shall have:
  - Text and a hint.
  - Links to Document Control documents, **pinned to the linked revision** even after newer revisions are released ([INV-164](21-invariants.md)).
  - Attachments and an *is required* flag.
  - A value or "out of" score (scoring audits only).
  - A **classification** (for example *Conforming, Observation, Minor Nonconformance, Major Nonconformance, Opportunity for Improvement*; configurable).
  - Comments and a score.
  - Links to corrective actions and recommendations.
  - *(Vellum)* A **clause reference** (for example ISO 9001:2015 §7.5.3), so findings can be reported by clause.
- **AUD-003** Checklists shall be **importable** from spreadsheets. Import creates checklists that don't exist yet and updates the ones that do.

### Audit types

- **AUD-004** **Audit types** shall define:
  - Evaluation type: **Normal** or **Scoring**. Scoring adds per-question values, and the audit type's total score is the sum over its checklists, recalculated on save.
  - Default checklists.
  - **Optional sections:**
    - Introduction, scoring guidelines, glossary and instructions, each with default text.
    - Opening and closing statements, with text and attachments.
    - Customer info, supplier info and documents reviewed.
  - **Report options:** export corrective actions and/or recommendations to Excel (one file with two sheets when both are chosen), only show checklists containing findings, show all checklists, hide corrective actions.
  - A **frequency**.
- **AUD-005** Audit types shall be filtered by the audit plan's location.

## 14.2 Audit programs and plans

- **AUD-010** **Audit plans** shall have one workflow per program type: **Environmental, Quality, Safety, Supplier**, and more configurable. Each has:
  - Plan information, schedule, audit location, lead auditor, audit type.
  - Effective date and review date.
  - Online distribution list.
  - Links to previous plans and the current audit.
  - A **planned audits** subform (audit, audit type, auditee or area, planned dates, lead auditor).
  - **Additional (unplanned) audits:** links to any saved pending audit. These can be added in any plan phase except Voided.
- **AUD-011** Plan workflow: **Draft → Approval → Active → Completed**.
  - Approval can go forward, back to Draft (comments required) or void.
  - Active can go back to Approval or forward to Completed.
- **AUD-012** **Schedule audit** shall be available only on Active or Completed plans. It creates the audit in *Scheduled*, assigned to the lead auditor, with its checklists in Draft, and links the plan and the audit both ways. After that, the action is replaced by a link to the scheduled audit.
- **AUD-013** A planned audit row can be deleted only while the plan is in Draft or Approval. If an audit was already scheduled from it, the user shall be warned that the audit, its checklists and its recommendations will be **voided** ([INV-165](21-invariants.md)).
- **AUD-014** **Copy plan** shall copy everything except effective date, review date and distribution list.
- **AUD-015** *(Vellum extension, should)* A **risk-based audit program** view shall show every auditable entity: process, supplier, site, clause or requirement (with the *auditable* flag from Compliance Obligations, [EHS-805](17-ehs.md)). For each it shows the last audit date and result, the frequency recommendation and the next due date. Overdue audits are flagged.

## 14.3 Conducting audits

- **AUD-020** An **audit** shall capture:
  - Audit information, audit type, start and end date-times (past values allowed), lead auditor.
  - Links to plan, previous audit and next audit.
  - Customer and supplier lookups.
  - The inherited statement and guideline sections.
  - **Participants:** internal (a user) or external (a typed name), with a role.
  - **Report prepared by:** name, title, company, date and a signature (an e-signature for internal users [P11]; an image for external ones).
  - **Report distribution:** internal users, or external name, title, company and email.
  - **Documents reviewed:** a link that fills in number, revision, title and effective date, or a manual entry for external documents. The lookup lists documents in any phase.
- **AUD-021** Audit workflow: **Draft → Scheduled (lead auditor) → Report Approval → (optional) Summary Corrective Action Approval → Completed**, with void possible from Draft and Scheduled.
  - The *Scheduled* due date is the audit's end date-time. If that is empty or in the past, it's calculated from the phase settings.
  - The full set of terminal phases shall follow the platform model ([PLT-601](02-platform.md)).
- **AUD-022** Checklists start in Draft, assigned to the lead auditor. **Assign** and **assign all** move them to *Assigned* with an assignee and due date, and the assignees get emails. **Required questions** of required checklists must be answered before the audit can leave *Scheduled*.
- **AUD-023** A score can't be more than the question's "out of" value ([INV-067](21-invariants.md)). Ad-hoc questions added during an audit **can't be removed**. They can only be neutralized (uncheck *required* and clear "out of"), which excludes them from scoring.
- **AUD-024** Removing a checklist from an audit shall void that checklist and its recommendations. Linked corrective actions are **not** voided automatically. The user shall be shown them and offered a bulk void with a reason.
- **AUD-025** Users shall be able to raise a **corrective action** or **recommendation** straight from a question. It's pre-filled from the question and linked both ways. Recommendations: *Draft → Open → (Verification) → Completed*, with void possible from Open or Verification. An incomplete recommendation doesn't escalate to a CAPA on its own.
- **AUD-026** **Send invitations** shall email every internal participant a link to the audit, plus calendar invitations ([INT-604](06-integrations.md)).
- **AUD-027** **Copy audit / schedule from previous:** copy from the current or previous audit, or from the audit type template. Comments, open CAs and open recommendations may optionally be carried into the matching checklist rows. Earlier additional CAs and recommendations appear as *previous corrective actions/recommendations*. Previous and next audit links are kept both ways.

### Audit summary and report

- **AUD-028** **Audit summary** (editable in Draft, Scheduled and Report Approval):
  - Checklist count, findings summary, checklists containing findings, all checklists.
  - **Total score** and total results, updated automatically.
  - Conclusions.
  - **Audit result:** *Acceptable / Marginal / Unacceptable*.
  - **Frequency recommendation:** *Increase / Decrease / Keep*, with comments.
  - Additional CAs and recommendations.
- **AUD-029** **Summary report:**
  - *Generate* produces a sequentially numbered PDF, plus the Excel file of CAs and recommendations if the audit type's report options call for it. After that the action reads *Update*, and it reverts once the generated files are deleted.
  - *Send mail* creates a mail activity addressed to the distribution list, with the PDF and findings attached. It can be sent now or saved for later, and it's linked to the audit.

### Offline and external auditing

- **AUD-030** **Offline and external auditing** through spreadsheets:
  - *Synchronize attachments* exports the checklists and findings to protected Excel sheets.
  - Only the **classification** and **comments** columns can be edited, and only those sync back.
  - A returned **findings sheet** fills in each CA's *root cause* and *corrective action* descriptions and each recommendation's *action taken*. It replaces any earlier findings sheet.
- **AUD-031** *(Vellum extension)* **Native offline auditing** on the mobile app ([PLT-1703](02-platform.md)): checklists are downloaded, answered offline with photos, and synced. A **supplier/auditee portal** lets externals answer findings directly ([SUP §15.1](15-supplier-quality.md)).
- **AUD-032** Packaged views:
  - Checklist templates and audit types.
  - Audits by number; open audits by assignee; open checklists.
  - *(Vellum)* Findings by clause, by classification and by auditee.
  - *(Vellum)* Overdue findings.

## 14.4 Inspection management

### Setup hierarchy: criteria → points → plans

- **INS-001** **Criteria profiles** (auto-numbered, can be disabled) define one inspection criterion with a data type:
  - **Numeric**, with numeric type **RANGE / TARGET / MIN / MAX**. These control which limits apply: lower spec limit, lower control limit, target, upper control limit, upper spec limit.
  - **Keyword list**: rows of keyword, result (*Pass / Warn / Fail*) and a disable flag per keyword.

  Each profile shows a composite description.
- **INS-002** Limits shall be validated in conventional order: **LSL ≤ LCL ≤ Target ≤ UCL ≤ USL**, using whichever apply to the numeric type ([INV-068](21-invariants.md)). *(Reliance's documentation shows an order that is probably a typo. Vellum uses the standard order.)*
- **INS-003** Numeric evaluation of a measured value *x* ([INV-069](21-invariants.md)):
  - **Fail:** *x* is outside the spec limits (*x < LSL* or *x > USL*). For MIN, *x < LSL*; for MAX, *x > USL*.
  - **Warn:** within spec but outside the control limits (*x < LCL* or *x > UCL*).
  - **Pass:** otherwise. For TARGET, a configured tolerance around the target counts as within spec.

  *(Reliance doesn't document this evaluation. Vellum defines it.)*
- **INS-004** **Inspection points** are created directly in *Active*. Each holds:
  - Inspection criteria rows: criteria profile, reference, class (*Critical / Major / Minor*) and **CID** (default `⟨point number⟩-⟨row⟩`, editable).
  - **Sampling:** *100%*, *time-based* (quantity per N minutes), *quantity-based* (quantity per lot quantity) or *none*. Vellum extension: *AQL tables (ANSI/ASQ Z1.4 / ISO 2859-1)* by inspection level and AQL.
  - **Sample failure classes:** a multi-select of critical, major and minor.
  - **Review required for:** *never / criteria warnings / criteria failures / sample failures / always*.
  - Optional sections.

  Points can be *Inactive* or *Voided*. Disabled criteria profiles can't be picked.
- **INS-005** **Inspection plans**:
  - Workflow: **Define → Approve → Active → Close**. Define can be voided. Approve can go back to Define with comments.
  - Each plan holds an ordered list of inspection points (step, default = row order), optional sections, references (parent, subordinate and external plan links) and links to its inspection records.
  - Inactive or voided points can't be added. If a point linked to a plan later becomes inactive or void, the system shall show a warning.
- **INS-006** **Copy**:
  - Points can be copied from Active or Inactive. The copy starts in Active.
  - Plans can be copied from any phase except Voided. The copy starts in Define.
  - Copies leave out the number, locations and additional security.
  - Sorting by CID or step shall be **natural (numeric-aware)**, not plain ASCII.

### Inspection records and samples

- **INS-010** **Inspection records** can be created only **for an Active plan**, by users with author access. Closed plans can't create new records, but pending records can still be finished ([INV-166](21-invariants.md)). A record holds:
  - Plan link and record name.
  - **Lot number, lot total quantity, lot start and end date-time.**
  - For each point: total samples and remaining samples (calculated from the sampling rule and the lot quantity or time window), plus sample links.
  - Additional (ad-hoc) samples.
  - Result and color.
  - Remarks, initials, date and follow-up actions.
- **INS-011** Saving a record shall load the plan's points, calculate the sample counts, show *create sample* per point, set the result to *Pending*, and add a back-link on the plan.
- **INS-012** **Inspection samples**:
  - Each sample holds a serial number, point and record links, *taken by* and *taken on*.
  - Saving with a serial number loads the point's criteria. Disabled keywords are left out.
  - For each criterion, the inspector enters a **measured value** and **measurement tool** (numeric) or a **keyword** (keyword list). The system calculates the **criteria result** and color: Pass = green, Warn = orange, Fail = red, Pending = orange.
  - For Warn or Fail results, the inspector records **defect codes, cause codes, remarks and corrections, attachments and follow-up links**.
- **INS-013** **Measurement tools** shall be picked from assets with a valid calibration. **Quarantined or overdue assets are refused** ([AST-051](18-assets.md)).
- **INS-014** **Sample result** ([INV-167](21-invariants.md)):
  - **Fail** if any criterion whose class is among the point's *sample failure classes* fails.
  - Otherwise **Pass**.
  - Criteria of other classes don't affect the sample result.

  *Save and calculate sample results* also gathers the Warn and Fail remarks into a summary.
- **INS-015** **Inspection record result** shall be calculated once *remaining samples = 0* for every point. It's **Fail** if any non-voided sample failed, otherwise **Pass**. Voided samples are excluded ([INV-168](21-invariants.md)).
- **INS-016** **Forced review:** when a sample's outcome meets the point's *review required for* condition, the inspector can't send it straight to Close. It must go to *Review* ([INV-169](21-invariants.md)).
- **INS-017** Workflows:
  - **Record:** *Inspect → Review / Close / Void*; Review can go to Close, back to Inspect with comments, or Void.
  - **Sample:** *Inspect → Review / Close (only if review isn't required) / Void*; Review can go to Close or back to Inspect.
- **INS-018** **Create follow-up action** from a Warn or Fail criterion, or from a sample, shall create an **Action Item, NCR, quality event, deviation, CAPA or SCAR**, linked back to that criterion or sample. From a record, it creates an action item.
- **INS-019** Ad-hoc samples shall be linked to the matching point row, or to *additional samples* if the record has no such point.
- **INS-020** Due-date extension and reassignment requests ([QE-004](12-quality-events.md)) are available on points (Active), plans (Define, Approve, Active), records (Inspect), samples (Inspect) and action items (Initiate).
- **INS-021** **Optional sections** on points, plans, records and samples shall look up customers, suppliers and materials, items, assets and documents, or take manual entries and process information.

### Mobile and SPC

- **INS-022** **Mobile inspection** ([PLT §2.17](02-platform.md)):
  - Samples can be done offline: ad-hoc sample creation, *save and start inspection*, and criteria entry.
  - Some fields are hidden on mobile (CID, colors, follow-up actions, optional sections, summaries) unless the mobile layout includes them.
  - On mobile, **only the sample's assignees may edit it**. *(Reliance parity; Vellum applies the same rule when the layout is in mobile mode.)*
- **INS-023** *(Vellum extension, should)* **SPC** on numeric criteria: X̄-R and individuals charts over samples by point and criterion, Cp/Cpk/Pp/Ppk, and run-rule alerts that raise quality events.
- **INS-024** Packaged views:
  - Setup: criteria profiles, inspection points, inspection plans.
  - Inspect: inspection records, inspection samples.
  - Activity: action items.

  All are sorted by number, descending.
