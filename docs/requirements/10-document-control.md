# 10. Document Control and Records

Document Control is Vellum's core application. It governs controlled documents (SOPs, policies, work instructions, test methods, specifications, forms) from first draft through approval, release, periodic review, revision, obsolescence and archive.

The generic revision-control behavior in this section (§10.2, §10.8, §10.10, §10.11) is a **platform capability**. Any application can turn it on for its forms, which is how Requirements and Test Scripts work in [Release Management](19-projects-tasks.md#195-release-management-validation-of-vellum-configuration).

**Parity sources:** Reliance help › Document Control, Document Control 2.0.0, Revision Control Documents, Quality Records Disposition, Collaboration in Google/M365, Office Integration Templates.

## 10.1 Document types and type profiles

- **DOC-001** The system shall provide **Document Type Profiles** that define, for each document type (for example Procedure, Policy, Work Instruction, Test Method, Specification, Form), its numbering scheme, revision format, periodic review frequency and enabled/disabled status. [CFG]
- **DOC-002** A document type shall be linked to **at most one enabled** Document Type Profile at a time ([INV-101](21-invariants.md)).
- **DOC-003** Authors shall only be able to create documents of types that have an enabled Document Type Profile.
- **DOC-004** Disabling a Document Type Profile shall block creation of new documents of that type. It shall not change existing documents. Pending documents of a disabled type shall stop auto-calculating their periodic review date, but the date shall stay manually editable until release.
- **DOC-005** Only application managers shall be able to see and maintain Document Type Profiles. Views shall show Active and Disabled profiles separately.
- **DOC-006** Each document type shall declare whether **training is mandatory to consider** for new documents and revisions. By default this is on for Procedure, Policy, Work Instruction and Test Method. [CFG]

## 10.2 Numbering and revisions

- **DOC-010** The system shall assign a **document number** on first save. It is built from the type profile's prefix, an optional current-year element, a zero-padded sequence (5 digits by default, for example `00001`) and a suffix. [CFG]
- **DOC-011** Prefix and suffix may be static or computed by a rule or script from record data (for example department or priority) ([DSN-120](03-designer.md)).
- **DOC-012** Document numbers shall be **unique within the application and never reused**, even after a document is voided or obsoleted ([INV-020](21-invariants.md)).
- **DOC-013** Every revision of a document shall share the document number and differ only by **revision identifier** ([INV-102](21-invariants.md)).
- **DOC-014** The system shall support these revision formats per type profile: **Numeric from 0**, **Numeric from 1**, **Alphabetic** (A…Z, AA, AB…), **Manual** (a required, user-entered revision field) and **No revision** (the revision field is hidden).
- **DOC-015** For numeric and alphabetic formats, a new revision shall get the next value automatically. For the manual format, the user shall enter a value, and the system shall reject one that duplicates an existing revision of the same document.
- **DOC-016** The system shall keep a read-only **Revision History** table for each document. Each row shows the revision, the reason for the revision, the approver(s) and the effective date. It can't be edited or deleted by any user, including administrators. It is recalculated automatically at each phase transition and at archive. It is paginated (10 rows per page by default) and searchable.
- **DOC-017** The Revision History section shall also show: the *Reason for New/Revised Document* (entered for a new document; inherited, with a link to the originating Change Request, for a revision), links to any Obsolete Request, and links to Periodic Review records.

## 10.3 Authoring and content

- **DOC-020** A controlled document shall have identification fields (type, title (required), revision, owner(s), reviewer(s)) and body fields: a **primary controlled file** attachment, a **supporting files** attachment, links to external documents (URLs or Vellum records) and **related documents** (Vellum records picked from a view).
- **DOC-021** The primary controlled file shall be converted to a **PDF rendition** when a configured phase is reached (default: on approval). The original stays available to authorized editors. Which files are converted, and in which phases, shall be configurable per attachment field and phase. [CFG]
- **DOC-022** Rendition PDFs may carry a configured **watermark**. The watermark can be static text (for example "CONFIDENTIAL", "UNCONTROLLED WHEN PRINTED") or computed from record data (for example document number, revision, phase, effective date). It is configured per phase. [CFG]
- **DOC-023** In read mode, a phase may be configured so that **only the PDF renditions** are visible in attachment fields. This keeps readers from getting editable source files.
- **DOC-024** Authors shall be able to create attachments by uploading a file, by **generating from an Office template** with field-merge ([INT §6.4](06-integrations.md)), or by **starting a co-authoring file** in Microsoft 365 or Google Workspace ([INT §6.5](06-integrations.md)).
- **DOC-025** The system shall keep the full **field history** for document metadata. This is a per-field log of old value, new value, user, time and phase ([PLT §2.10](02-platform.md)).
- **DOC-026** Hard deletion of controlled documents shall not be supported. Documents are **voided** (if never released) or **obsoleted** (if released) ([INV-010](21-invariants.md)).

## 10.4 Approval workflow

The packaged Document Control workflow is listed below. Every phase, assignee, route and rule is configurable in the Designer. The packaged default shall ship exactly as described so customers get a validated baseline.

| Phase | Assignee | Purpose | Forward routes | Back routes | Can void |
|---|---|---|---|---|---|
| Draft | Author | Fill in identification, body, related documents and the training decision | Document Control Review; **Awaiting Release** (minor change shortcut) | (none) | Yes, by author |
| Document Control Review | Document Controllers | Check template, format and SOP adherence | Initial Approval | Draft | No |
| Initial Approval | Owner | Initial approval of the content | Review | Document Control Review | Yes |
| Review | Reviewers | Content review | Final Approval | Initial Approval | No |
| Final Approval | Owner | Approve for distribution | Awaiting Release | Review | Yes |
| Awaiting Release | Document Controllers | Hold until training is done and/or the effective date is reached | Approved | Final Approval | No |
| Approved (Effective) | (none) | In force. Periodic review, change and obsolescence start from here | (requests only) | (none) | No |
| Voided | (none) | Terminal state for documents abandoned before release | (none) | (none) | (none) |

- **DOC-030** Vellum shall ship the packaged workflow above.
- **DOC-031** The **minor-change shortcut** (Draft → Awaiting Release) shall be allowed only when a configured condition holds (default: the author marks the change as *minor/editorial*, and that flag is recorded in the audit trail). It shall be blockable by configuration. [CFG]
- **DOC-032** The *Document Controller* group shall get author (edit) rights on documents while those documents are assigned to them, even if the controllers aren't otherwise authors of the document.
- **DOC-033** The training decision (DOC-060) shall be required before a document can leave Draft.
- **DOC-034** Voiding shall require a **reason comment**. Voided documents shall go to the *Voided Documents* view and become read-only ([INV-031](21-invariants.md)).
- **DOC-035** Review and approval phases shall support **sequential, parallel, and voting/quorum** routing for multiple approvers ([PLT §2.6](02-platform.md)).

## 10.5 Electronic signatures and approval manifest

- **DOC-040** [P11] Routing out of approval phases (Initial Approval, Review, Final Approval and Awaiting Release by default) shall require an **electronic signature**: re-authentication plus the meaning of the signature ([SEC §4.6](04-security.md)). [CFG]
- **DOC-041** [P11] Every document shall have an **Electronic Signature** section that records, for each signed phase, the signer's full name, user ID, date and time (with time zone), the meaning (for example *Approved*, *Reviewed*, *Released*) and the phase.
- **DOC-042** [P11] When a document is routed **backward** to an earlier phase, the signature-section entries for that phase and every later phase shall be marked **superseded**, and new signatures shall be captured when the document moves forward again. Superseded signatures shall stay in the audit trail and shall never be deleted ([INV-052](21-invariants.md)).
- **DOC-043** [P11] The system shall produce an **approval manifest** (a PDF signature page) for any approved revision. It lists every signature, generated from a configurable template, and it can be appended to or bound with the PDF rendition.
- **DOC-044** [P11] Printed and PDF output of signed records shall include the signature manifestation: name, date and time, and meaning (21 CFR 11.50).

## 10.6 Release and effectiveness

- **DOC-050** A document in Awaiting Release shall become **Approved (Effective)** on the later of (a) its **effective date** and (b) completion of any release preconditions (for example the required training completion percentage). Release may also be manual, by a Document Controller, if configured. [CFG]
- **DOC-051** The workflow shall support an **effective date delay** in days. It may be static or computed (for example 3 days for safety-related categories, 10 days otherwise) ([DSN-120](03-designer.md)).
- **DOC-052** [SAFETY] At any moment, exactly **one** revision of a given document number shall be Effective, or none if the document is new or obsolete ([INV-100](21-invariants.md)).
- **DOC-053** [SAFETY] When a new revision becomes Effective, the previous Effective revision shall be **superseded and moved to the archive in the same transaction**, together with its activities (change requests, periodic reviews, action items). It shall never be possible to see two Effective revisions at once ([INV-100](21-invariants.md)).
- **DOC-054** Links to any superseded revision shall resolve to the **current Effective revision** by default. The user shall still be able to open the specific historical revision through the archive, if authorized.
- **DOC-055** On release, the system shall notify the configured **distribution list** (users, groups, roles, locations) with a link to the newly effective revision. [CFG]

## 10.7 Training linkage

- **DOC-060** For types where training is considered (DOC-006), every new document and revision shall record *"Is training required for this revision?"* (Yes/No). If **No**, a **justification** shall be required and kept in the audit history.
- **DOC-061** If **Yes**, the author (or controller) shall be able to **Set Up Training**. This creates a Course Profile pre-filled with the document's name, owner, source number, source link and source attachments ([TRN §11.3](11-training.md)).
- **DOC-062** A course may be flagged **"automate with next revision"**. The system then automatically creates a new Course Profile for each later revision, with the latest content and the same configuration.
- **DOC-063** Training assignments for a revision shall be created when the revision enters **Awaiting Release**, so training can be finished before the effective date.
- **DOC-064** When a revision reaches Awaiting Release, the **test templates** of the previous revision shall be marked obsolete, and the new revision's test templates (copied from the previous revision when the change request was accepted) shall become active.
- **DOC-065** When a new revision is released, the previous revision's Course Profile shall stay as it is until pending trainees finish. Then it shall be retired automatically.
- **DOC-066** When a document is archived, obsoleted or voided, its Course Profile shall be **disabled**, and its pending training assignments that no longer apply shall be **voided**, by scheduled tasks ([TRN-090](11-training.md)).
- **DOC-067** From a document (Awaiting Release or Approved), authorized users shall be able to open **Document Training Records**. This is a view of every training assignment for the document's courses, with status (Completed, Expired, Not Assigned, Pending, Voided). It is filtered by the viewer's training-visibility permissions. It excludes non-completed assignments of inactive employees and disabled courses, and it always includes completed assignments.

## 10.8 Periodic review

- **DOC-070** Each document type profile shall define a **periodic review frequency** (number + unit: day/week/month/year). The frequency may be static or computed by rule (for example 12 or 36 months, depending on category).
- **DOC-071** On approval, the system shall calculate the **next review date** from the frequency. If there is no frequency, the date shall be set manually or stay blank.
- **DOC-072** The system shall send **review reminder** notifications to the owner (and configured reviewers) ahead of and on the review date, and it shall escalate overdue reviews ([PLT §2.12](02-platform.md)). [CFG]
- **DOC-073** Authorized users (administrators, application managers, users listed in the workflow's *Periodic Review* permission, and document owners) shall be able to start a **Periodic Review** record on an **Approved** document at any time (ad hoc) or from the reminder.
- **DOC-074** A Periodic Review shall link to the reviewed revision. It shall capture review comments and an optional next review date. If the next review date is left blank, it shall be calculated at completion as *completion date + frequency*.
- **DOC-075** Completing a Periodic Review shall: move it from *Open* to *Closed* views, add a back-link on the parent document, set the parent's review date to the next review date, and add a **distinguishable system comment** (reviewer, time, comment) to the parent's comment log.
- **DOC-076** The outcome of a periodic review shall be one of: *No change needed*, *Change required* (which starts a Change Request, §10.10) or *Obsolete* (which starts an Obsolete Request, §10.11).

## 10.9 Distribution, viewing, printing and publishing

- **DOC-080** Readers shall see the **Effective** revision of a controlled document by default. Draft and pending revisions shall be visible only to users with the right access (author, assignee, manager, reviewer).
- **DOC-081** The document viewer shall render PDF renditions in the browser without a download. Downloading and printing shall each be permission-controlled per application and phase. [CFG]
- **DOC-082** *(Provisional, open question 5)* **Controlled printing:** the system should support printing a controlled copy that carries a per-print watermark (printed by, date and time, copy number, "controlled copy valid until"). Every controlled print should be logged with user, time, revision and copy number. The system should support recalling controlled copies (a printout reconciliation list) when a revision is superseded.
- **DOC-083** The system shall support **printing a record together with its related documents and attachments** in one combined PDF, where configured ([ADM §5.9](05-administration.md)).
- **DOC-084** The system shall support **publishing** renditions to an external library (SharePoint) when a configured phase is reached. The folder may be configured or computed. A missing folder is created on first publish. A completed revision's published file shall never be overwritten by files from a pending higher revision ([INT §6.3](06-integrations.md)).
- **DOC-085** Read-and-understood **acknowledgment** of a document shall be supported through Training (an online course with acknowledgment and no test, [TRN-040](11-training.md)).

## 10.10 Change requests

- **DOC-090** Authorized users (administrators, application managers, users in the workflow's *Process Change Request* permission, and owners) shall be able to raise a **Change Request** on an **Approved** document.
- **DOC-091** A Change Request shall capture: **reason for change (required)**, supporting attachments, *assign new revision to* (users/groups; defaults to the Change Request's approvers), *notify of new revision* users, due date, and optional extra record-level security. It shall show a read-only summary of the target document (title, type, number, revision, status).
- **DOC-092** A Change Request shall follow its own workflow (default: *Requested → Accepted*, voidable once saved).
- **DOC-093** When a Change Request is accepted, the system shall, as one transaction:
  1. Create a **new Draft revision** that copies all document data (still editable), with the revision identifier incremented per the format. Its *created by* and *created on* shall be the Change Request's requester and the acceptance time.
  2. Assign the new revision to the chosen assignees and email them a link.
  3. Send a separate notification about each new revision to every *notify of new revision* user.
  4. Add cross-links between the Change Request, the Effective revision and the new Draft revision.
  5. If training is required, copy and link the test templates to the new revision.
  6. If the workflow is configured to do so, duplicate configured activity types onto the new revision.
- **DOC-094** **Multiple-document change requests:** in the Requested phase, a Change Request may include extra documents, picked from the target's related documents or from any Approved document that has no change in progress. Each included document shall be marked *Related* or *Additional*. Assignment options: all to one user/group (default), each to its owner, each to its author, or per document. Due-date options: one shared date (default) or per document.
- **DOC-095** A Change Request shared by several documents shall be archived together with whichever of its documents is archived first. It shall stay linked to the others.
- **DOC-096** **Change Request Log** (optional, off by default): when it is enabled, *Process Change Request* opens a per-document log. Any user with at least author access can propose changes there (description, benefit, attachments). The owner is notified (unless the owner submitted the item) and sets each item to *Approved*, *Rejected* or *Pending*. *Process Request* creates a Change Request that carries the approved items into its reason. Pending items carry forward to the new revision's log, and rejected items are closed.
- **DOC-097** Mutual-exclusion rules for revision-control requests ([INV-103](21-invariants.md)):
  - Change Requests, Obsolete Requests and Periodic Reviews shall **not** be allowed on documents that are not Approved.
  - A Change Request shall not be allowed while another Change Request or Obsolete Request for the same document is pending or unsaved, or while a new revision created by an earlier Change Request is still pending.
  - An Obsolete Request shall not be allowed while any Change Request, Periodic Review or Obsolete Request is pending or unsaved. The *Obsolete* action shall be hidden in that state, and the user shall be told which request is blocking.
  - A Periodic Review shall not be allowed while a Change Request or Obsolete Request is pending or unsaved.
  - Voiding or deleting the blocking item shall release the block immediately.

## 10.11 Obsolescence, archive and restore

- **DOC-100** Each revision-controlled application shall have a paired **Archive application**. It mirrors the live application's forms and views, holds superseded and obsolete revisions, and is reachable from the live application.
- **DOC-101** Only users listed in the Approved phase's *Who Can Obsolete* setting (plus administrators) shall be able to raise an **Obsolete Request**. If that list is empty, only administrators can.
- **DOC-102** An Obsolete Request shall link to the parent document, require a **reason**, follow its own workflow (default: *New → Approval → Accepted*, voidable), and mark the parent with status *Obsoletion in progress* while it is open.
- **DOC-103** When an Obsolete Request is accepted, the system shall, as one transaction: move the document and its activities (Obsolete Request, Change Requests, Periodic Reviews, action items) to the archive; put the document in the read-only **Obsolete** phase; add a comment log and phase-tracking entry; mark linked training requirements and test templates *Obsolete*; and notify the distribution list.
- **DOC-104** Automatic archiving (supersession by a new revision) and manual archiving (obsolescence) shall be **distinguishable** in the data and in the UI.
- **DOC-105** **Restore** shall be offered only on **manually obsoleted** documents, never on superseded revisions. Only users in the archive phase's *Who Can Restore* setting (plus administrators) can restore. If that list is empty, only administrators can.
- **DOC-106** Restore shall require an **active** assignee (user or group), a due date and a **mandatory comment**. It shall create a **new Draft revision** in the live application, with a new revision identifier and the inherited data. If training applies, it shall copy test templates and collaboration files. It shall log the restorer, time, phase and comment.
- **DOC-107** Archived records shall be immutable, apart from the restore action and the retention and disposition actions in §10.12 ([INV-031](21-invariants.md)).

## 10.12 Records retention and disposition

Quality Records Disposition, plus platform retention.

- **DOC-110** The system shall provide a **Quality Record** register that defines each class of quality record (paper or electronic), its control start date, and **short-term** and optional **long-term** retention requirements (retention period, disposition frequency, next disposition date).
- **DOC-111** Retention may be calculated from a **keyword-based label** (for example *shelf life*, *model year*) plus a period, or from the *current date − retention period*. The system shall generate the disposition instruction text (for example "Dispose of all records older than 11/2025").
- **DOC-112** *New Record Disposition* shall be available only on approved Quality Records. Completing a disposition activity shall recalculate the next disposition date as *disposition date + frequency*.
- **DOC-113** Every application shall support a configurable **retention policy** (retention period from a date field or phase, plus a disposition action: archive, export-then-purge, or review). Purging shall only happen through an approved **Disposition** record listing the affected records. Records under **legal hold** shall never be purged ([INV-012](21-invariants.md)). [SAFETY]
- **DOC-114** Purging a record shall leave a **tombstone** that keeps its identifier, type, number, dates, disposition record reference and audit trail of the disposition. The audit trail of the record's lifecycle shall be kept for at least as long as the longest applicable regulatory retention period, or longer if configured.

## 10.13 Views

- **DOC-120** The packaged application shall provide views for: Pending Documents (including Awaiting Release), Approved/Effective Documents (by type, owner, location, and next review date), Voided Documents, Periodic Reviews (Open and Closed), Change Requests (Open and by phase), Obsolete Requests (Open; and in the archive), Document Type Profiles (Active and Disabled; managers only), Documents due for review (next 30/60/90 days and overdue), and Documents without training decision.

## 10.14 Compliance obligations linkage

- **DOC-130** A controlled document may link to a **compliance obligation applicability category**. When the document is itself a new monitored requirement (for example an internal policy that implements a regulation), the linked obligations shall show the document as evidence ([EHS §17.8](17-ehs.md)).
