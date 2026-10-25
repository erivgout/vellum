# 13. Change Management

Change Management controls changes to products, processes, specifications, equipment, suppliers and documents, from proposal through impact assessment, approval, implementation and verification. It also classifies regulatory filings.

Vellum ships the template-driven model (parity with Reliance Change Management 2.0.0). The legacy type-based model (Engineering, Process, Specification and Supplier changes) is reproduced as packaged **change templates**, so customers moving from the legacy app keep their behavior.

**Parity sources:** Reliance help › Change Management (legacy), Change Management V2.0.0, Document Control (multiple change requests).

## 13.1 Change templates

- **CHG-001** **Change templates** shall be reusable blueprints that appear as the *change type* when a change record is created. A template pre-fills: subject pattern, change information, required approval types and approvers, impacted areas, action plan rows, optional sections, verification requirement and regulatory reporting defaults.
- **CHG-002** Templates are **enabled** by default. A disabled template is removed from the change type list for new records. Disabling or editing a template shall **not** change existing records created from it ([INV-147](21-invariants.md)).
- **CHG-003** Vellum shall ship templates matching the legacy types:
  - **Engineering change:** optional feasibility analysis; ad-hoc action items during implementation.
  - **Process change:** extra review and verification phases, through template-driven phase skipping.
  - **Specification change.**
  - **Supplier change.**

  Each keeps its legacy phase path: phases a template doesn't need are skipped by configuration.

## 13.2 Change record

- **CHG-010** A **change record** shall capture:
  - Subject, **change owner**, planned implementation date.
  - Change information, **current state**, **proposed change** and reason or justification.
  - **Change impact level** (*Low/Medium/High*).
  - **Approvals:** rows of approval type (for example Quality, Regulatory, Engineering, Manufacturing, EHS) with the users or groups for each.
  - **Impact assessments**, optional information sections, optional implementation sections, verification and costs (QE-012).
- **CHG-011** **Optional information** sections shall look up and copy data from master profiles:
  - **Asset**, **customer** (account, contacts, addresses, feedback), **product** (material or chemical data) and **supplier**.
  - **Engineering:** CN/ECN/EWO, change type, design responsibility, planned and actual dates, customer change number, design leader, drawings and specifications.
- **CHG-012** A *validation review* section shall be available when the change affects validated processes, equipment, limits or cleaning. It is driven by template questions or a decision tree (legacy parity).

## 13.3 Workflow

| Phase | Assignee | Forward | Back | Can void |
|---|---|---|---|---|
| Draft | Author | Review | (none) | Yes (reason required) |
| Review | Change owner | Approval | Draft | Yes (reason required) |
| Approval | Approvers, by approval type | Implementation | Review | No |
| Implementation | Change owner | Verification (if required) or Completed | Draft, Review | No |
| Verification | Verification user | Completed | Implementation | No |
| Completed | (none; change is effective) | (none) | (none) | (none) |

- **CHG-020** Vellum shall ship the workflow above. Phases are configurable, and templates may add phases. For example, the legacy Process type adds Review → Final Approval.
- **CHG-021** [P11] In Approval, the record shall be assigned to **every** approver at once. It can't leave Approval until **at least one approver from each approval type** has signed (approved) ([INV-148](21-invariants.md)). Templates may require *all* approvers of a type instead. A rejection by any approver shall send the change back to Review with a comment, if configured.
- **CHG-022** Void shall be allowed only in Draft and Review, with a mandatory reason.
- **CHG-023** The Verification phase shall exist only when *verification of implemented change required* is set (with the verification user and instructions). Otherwise Implementation goes straight to Completed.
- **CHG-024** Leaving Implementation shall require that **all action plan items are completed**, and any post-implementation action items too, before Verification ([INV-149](21-invariants.md)). Leaving Verification shall require **objective evidence** to be recorded.

## 13.4 Impact assessment

- **CHG-030** **Impact assessment** rows (impacted area, assignee, due date) shall be added by the author in Draft or the owner in Review. *Assign impact assessment* creates a linked **impact assessment record** and routes it straight to *Assessment*. The record captures impact (*High / Medium / Low / N/A*) and a description. Its workflow is *Draft → Assessment → Completed*, voidable with a reason.
- **CHG-031** Impact assessments should be finished during Review or Approval. Completing them isn't required to complete the change, unless the template says so. [CFG]
- **CHG-032** *(Vellum extension, should)* The change record shall show an **impact summary**: the highest impact level, open assessments, and the affected documents, specifications, assets, products and suppliers.

## 13.5 Implementation planning

- **CHG-040** **Action plan** rows: action subject, assign to (*change owner* or *other* user/group), days to complete, *require verification* (adds verifier and days to verify). Entering Implementation turns them into Action Items in Open ([QE-002](12-quality-events.md)), with due dates calculated from *days to complete*.
- **CHG-041** **Affected documents** rows: action **Change**, **Obsolete** or **New** (new needs a document type and title), the current revision (picked from **effective** documents only, excluding documents that already have a pending change), the reviser, and the draft due date.
  - On entering Implementation, the system shall create the matching Document Control change requests, obsolete requests or new drafts ([DOC §10.10](10-document-control.md)), linked back to the change.
  - Legacy parity: affected documents can be bundled into one **multiple-document change request**.
- **CHG-042** [SAFETY] A change can't be **Completed** while any linked affected document or specification is still pending. The new revisions must be **effective**, or the obsoletion completed, unless the template allows completion with documents in Awaiting Release ([INV-160](21-invariants.md)).
- **CHG-043** **Affected product specifications** rows work the same way: change, obsolete or new item specification ([SUP §15.4](15-supplier-quality.md)).
- **CHG-044** **Post-implementation action items:** subject, assignees, days to complete, optional verification. They must be complete before Verification.
- **CHG-045** **Related projects:** a change may create or link Project Control projects ([PRJ](19-projects-tasks.md)).
- **CHG-046** **Feasibility analysis** activity (legacy engineering parity): identification, feasibility information rows and more. Workflow: *Draft → Approval → Completed*. It can be created while the change is in Approval.

## 13.6 Regulatory reporting

- **CHG-050** A **regulatory reporting** section shall classify the change by its impact on the product's identity, strength, quality, purity or potency (or on safety and effectiveness for devices):

  | Impact | Filing |
  |---|---|
  | Substantial | **Prior Approval Supplement (PAS)**, approval needed before distribution |
  | Moderate | **CBE-30** (changes being effected in 30 days) or **CBE-0** (changes being effected immediately) |
  | Minor | No submission; document in the **Annual Report** |

  Each filing type shall show its own information section. For example, PAS shows submission details and the approval date.
- **CHG-051** [SAFETY] When the filing is **PAS**, the change can't be **Completed** (made effective) until the regulatory approval date is recorded with the agency's approval evidence ([INV-161](21-invariants.md)). When it's **CBE-30**, completion shall be blocked until 30 days after the submission date unless the agency responded earlier ([SAF-022](20-safety.md)).
- **CHG-052** *(Vellum, should)* Device-specific and non-US classifications shall be configurable: 510(k) letter-to-file vs new 510(k), PMA supplements, EU MDR significant change and notified body notification.

## 13.7 Reports and views

- **CHG-060** Packaged views: open, completed and voided changes; open, completed and voided impact assessments; open action items; enabled and disabled change templates; feasibility analyses (open by assignee, closed by date).
- **CHG-061** **Timeliness reports** (legacy parity), each by change type, department, reason code and month, quarter or year:
  - **Aging by month:** age in days of open changes.
  - **Created vs completed.**
  - **Time to complete.**
  - **Timeliness by phase:** *days = due date − date completed* per phase. Shown as *Early* (green, > 0), *On time* (yellow, = 0) or *Late* (red, < 0).
