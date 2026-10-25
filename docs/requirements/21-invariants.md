# 21. System Invariants

An **invariant** is a property that shall hold in **every committed state** of the system, for every tenant, whatever the path that led there: UI, API, import, mobile sync, script, scheduled task, escalation, promotion, restore or upgrade.

Functional requirements say what the system does. Invariants say what must **never** be false. Each invariant:

- shall be enforced at the **lowest layer that can guarantee it**: a database constraint, then the domain service or aggregate, then the workflow guard. UI checks are convenience only and never count as enforcement;
- shall have an **automated test**. Property-based or model-based tests are preferred for workflow invariants;
- where it can be checked after the fact, shall be checked by the integrity checker ([SAF-066](20-safety.md)). Violations are raised as incidents.

Notation: *R* is a record, *D* a controlled document (a revision-controlled record family), *P* a phase, *u* a user.

*Enforced by* values: **DB** = database constraint or transaction; **Domain** = domain service or aggregate; **Workflow** = routing guard; **Validator** = configuration publish validation.

## 21.1 Identity and uniqueness

- **INV-001** Every record *R* is an instance of exactly one form and, if that form has workflows, exactly one workflow at a time. Its current phase belongs to that workflow. *Enforced by:* DB, Domain. *Refs:* [PLT-201](02-platform.md).
- **INV-020** A record number is unique within its application and number sequence, and is never reassigned. That holds even after void, obsolescence, deletion or purge, because tombstones keep it. *Enforced by:* DB unique index on the tenant, application and number, plus tombstones. *Refs:* [DOC-012](10-document-control.md).
- **INV-021** Fields marked *unique* and composite unique keys are unique across all records of the form, including archived records. Unique keys can't be bypassed by import, API or script. *Enforced by:* DB. *Refs:* [PLT-410](02-platform.md).
- **INV-022** Asset IDs are unique within the tenant. *Enforced by:* DB. *Refs:* [AST-002](18-assets.md).
- **INV-023** Generated regulatory report numbers (MFR, importer, user facility) are unique per reporter code and year, and form an unbroken, increasing sequence of numbers that have been issued. *Enforced by:* DB sequence per mapping and year, inside the submit transaction. *Refs:* [AER-005](12-quality-events.md).
- **INV-024** A risk item's hazard name is unique within its department and location. *Enforced by:* DB. *Refs:* [RSK-007](16-risk.md).

## 21.2 Immutability, deletion and retention

- **INV-010** Regulated records are never hard-deleted. This covers records that have left their starting phase, been signed, are under retention or are on legal hold. Every allowed deletion leaves a deletion history entry and a tombstone. *Enforced by:* Domain delete guard. *Refs:* [PLT-206](02-platform.md), [DOC-026](10-document-control.md), [TRN-028](11-training.md), [NFR-044](22-non-functional.md).
- **INV-012** A record on legal hold is never purged, whatever its retention. A purge happens only through an approved disposition record listing it. *Enforced by:* Domain. *Refs:* [DOC-113](10-document-control.md).
- **INV-030** Submitted test attempts, completed training records and completed calibration readings are immutable. Correction is by a void-and-reissue action that keeps the original. *Enforced by:* Domain. *Refs:* [TRN-033](11-training.md), [TRN-061](11-training.md).
- **INV-031** Records in Voided, Completed, Archived or Obsolete phases, and records locked by a terminal decision (for example PPAP *approved with exceptions*), are read-only. Content can't change except through an audited administrative override where the rules allow one. Content of archived revisions can never be overridden. *Enforced by:* Domain. *Refs:* [DOC-034](10-document-control.md), [DOC-107](10-document-control.md), [PPAP-005](15-supplier-quality.md).
- **INV-032** Only Normal phases have assignees and due dates. Records in terminal phases have no open assignments. *Enforced by:* Domain. *Refs:* [PLT-601](02-platform.md).

## 21.3 Durable automation and gating

- **INV-040** If a committed transaction triggers automation (auto-created activities, notifications, archival, publishing, outbound messages), that automation is recorded durably in the same commit, and it is eventually either done or visibly failed. It is never silently lost. *Enforced by:* transactional outbox. *Refs:* [QE-002](12-quality-events.md), [NFR-002](22-non-functional.md), [SAF-031](20-safety.md).
- **INV-041** For every "is X required?" gate: if the answer is *Yes*, the record doesn't pass the gate phase until a linked X exists. If the answer is *No*, a non-empty justification is stored. *Enforced by:* Workflow. *Refs:* [QE-005](12-quality-events.md).
- **INV-042** A CAPA whose effectiveness check result is *not effective* never sits in *Closed*. It is reopened or a successor CAPA is linked. *Enforced by:* Workflow. *Refs:* [QE-008](12-quality-events.md).
- **INV-043** A CAPA is in Effectiveness Check or later only if all of its action items are closed or voided. *Enforced by:* Workflow. *Refs:* [CAPA-004](12-quality-events.md).
- **INV-044** A lab investigation with *lab error identified = No* can reach Closed only through Full Investigation. Original results are marked invalid only when *lab error identified = Yes*. *Enforced by:* Workflow. *Refs:* [LAB-002](12-quality-events.md), [LAB-003](12-quality-events.md).
- **INV-045** A re-test testing activity's analyst differs from the original test's analyst. A re-sample's analyst is the same as the original's. *Enforced by:* Domain. *Refs:* [LAB-005](12-quality-events.md).
- **INV-046** A complaint goes past Investigation only if every MDR and vigilance decision-tree activity on it is completed. *Enforced by:* Workflow. *Refs:* [CMP-004](12-quality-events.md).
- **INV-047** For a given MDR decision tree with MFR-type numbering, at most one non-voided initial (5-day) MedWatch exists. *Enforced by:* DB partial unique index. *Refs:* [CMP-005](12-quality-events.md).
- **INV-048** A MedWatch is Completed only if all three FDA acknowledgements are recorded and *confirm final acknowledgement = Yes*. *Enforced by:* Workflow. *Refs:* [AER-009](12-quality-events.md).

## 21.4 Audit trail and electronic signatures

- **INV-050** The system audit trail is append-only and complete. Every state change of every record has a matching audit entry in the **same** transaction. No user, administrator, support engineer, script or migration can update or delete audit entries. *Enforced by:* DB (append-only table, no update or delete grants) and Domain. *Refs:* [PLT-303](02-platform.md), [PLT-1001](02-platform.md), [PLT-1006](02-platform.md), [PLT-1901](02-platform.md).
- **INV-051** Every audit entry names its actor: a human user; *delegate X on behalf of Y*; or the system identity together with the rule, task, escalation or integration that triggered it. The system identity never produces electronic signatures. *Enforced by:* Domain. *Refs:* [PLT-650](02-platform.md), [PLT-1305](02-platform.md), [SEC-609](04-security.md), [SEC-806](04-security.md).
- **INV-052** Signature records are immutable. They can't be edited through overrides, Office files, imports or the API. Routing back marks signatures *superseded*; it never deletes them. *Enforced by:* DB and Domain. *Refs:* [DOC-042](10-document-control.md), [PLT-1903](02-platform.md), [INT-405](06-integrations.md).
- **INV-053** Every stored file version has a SHA-256 checksum recorded at upload. A served file always matches its checksum, and a mismatch is never served silently. *Enforced by:* the storage service. *Refs:* [PLT-803](02-platform.md).
- **INV-054** A record never enters a phase whose configuration requires PDF renditions of certain fields unless current renditions of those fields exist and are linked. *Enforced by:* Workflow. *Refs:* [PLT-807](02-platform.md).
- **INV-055** For each record, audit entries form a hash chain: each entry includes the hash of the previous one. Entry timestamps never decrease. *Enforced by:* Domain and the integrity checker. *Refs:* [PLT-1006](02-platform.md), [SAF-014](20-safety.md).
- **INV-112** An electronic signature is always executed by the signer's own credentials. Delegation never transfers signing authority. *Enforced by:* the signing service. *Refs:* [PLT-1306](02-platform.md).
- **INV-113** Every electronic signature is preceded by a fresh, two-component authentication ceremony (password re-entry, forced IdP re-authentication or passkey user verification) by the human signer, within the signing request. Service principals can't create signatures or signature tokens. *Enforced by:* the signing service. *Refs:* [SEC-602](04-security.md), [INT-102](06-integrations.md).
- **INV-114** Each signature stores the hash of the record version it signed. "Valid for current content" is true if and only if the current record version's hash equals that stored hash. *Enforced by:* Domain. *Refs:* [SEC-604](04-security.md).

## 21.5 Field and calculation semantics

- **INV-060** Default rules run exactly at the triggers set by the field's display format. A *computed when composed* value never changes after the first save. *Enforced by:* the form engine. *Refs:* [PLT-402](02-platform.md).
- **INV-061** Keyword values are stored as stable keyword IDs. Changing or translating a label never changes stored data. Disabled keywords are never offered for new values and are always shown on existing records. *Enforced by:* the data model. *Refs:* [PLT-404](02-platform.md), [ADM-604](05-administration.md).
- **INV-062** Number and currency values use exact decimal storage and arithmetic. Calculated results are the same for the same inputs and configuration version. *Enforced by:* the type system. *Refs:* [PLT-407](02-platform.md), [SAF-064](20-safety.md).
- **INV-063** A *prevent null* field is never empty in any committed record version, whatever the write path. *Enforced by:* DB NOT NULL where possible, otherwise Domain on every write path. *Refs:* [PLT-503](02-platform.md).
- **INV-064** Calibration pass/fail and deviation follow the definition in [AST-022](18-assets.md) exactly, for both the before and after readings. *Enforced by:* Domain, with reference-table unit tests.
- **INV-065** For each NCR, the sum of the disposition line quantities equals the affected quantity before the NCR leaves the Disposition phase. *Enforced by:* Workflow. *Refs:* [NC-002](12-quality-events.md).
- **INV-066** For each RMA line, quantity received ≤ quantity authorized, unless an override with a justification exists. *Enforced by:* Domain. *Refs:* [RMA-004](12-quality-events.md).
- **INV-067** For each audit question, 0 ≤ score ≤ out-of value. *Enforced by:* Domain. *Refs:* [AUD-023](14-audits-inspections.md).
- **INV-068** For each numeric criteria profile, the limits that apply satisfy LSL ≤ LCL ≤ Target ≤ UCL ≤ USL. *Enforced by:* Domain. *Refs:* [INS-002](14-audits-inspections.md).
- **INV-069** The criteria result (Pass/Warn/Fail) depends only on the measured value and the profile's limits, as defined in [INS-003](14-audits-inspections.md). *Enforced by:* Domain.
- **INV-178** FMEA RPN = S × O × D, revised RPN = S′ × O′ × D′, and reduction % = (RPN − RPN′)/RPN. These fields aren't user-editable. *Enforced by:* Domain. *Refs:* [FMEA-004](16-risk.md).

## 21.6 Workflow and routing

- **INV-070** A route (any phase change other than system or administrative routes) is done only by a current assignee of the record, or by that assignee's active delegate. *Enforced by:* Workflow. *Refs:* [PLT-604](02-platform.md).
- **INV-071** A record already in a phase keeps the assignment, routing and signature configuration it entered with until it leaves that phase. *Enforced by:* Workflow (configuration version pinned per phase entry). *Refs:* [PLT-614](02-platform.md), [SAF-072](20-safety.md).
- **INV-072** Every record in a Normal phase has at least one active assignee. *Enforced by:* Workflow, plus reassignment tasks on user deactivation. *Refs:* [PLT-622](02-platform.md).
- **INV-073** No new assignment or notification is ever made to an inactive user or group. *Enforced by:* Domain. *Refs:* [PLT-623](02-platform.md), [SEC-704](04-security.md).
- **INV-074** Every record in a Normal phase has a valid due date. Publishing is blocked for any configuration where an auto-route target couldn't get an assignee and a due date. *Enforced by:* Workflow and Validator. *Refs:* [PLT-631](02-platform.md), [DSN-611](03-designer.md).
- **INV-075** A record leaves a phase only when that phase's activity gates are met: every *must create* type exists, and every *must complete* activity is completed or voided. *Enforced by:* Workflow. *Refs:* [PLT-640](02-platform.md).
- **INV-076** A record with any unpublished co-authoring session can't be routed forward. *Enforced by:* Workflow. *Refs:* [INT-505](06-integrations.md).
- **INV-077** A BBS observation event can't be in Review while any of its observation records is pending. *Enforced by:* Workflow. *Refs:* [EHS-206](17-ehs.md).
- **INV-078** Each JSA step's residual risk ≤ its initial risk, unless a justification is stored. *Enforced by:* Domain. *Refs:* [EHS-304](17-ehs.md).
- **INV-079** Every objective is linked to a completed aspect. *Enforced by:* Domain. *Refs:* [EHS-504](17-ehs.md).
- **INV-080** Assignees and notification recipients offered under a location filter are only those who can access the record. *Enforced by:* Domain. *Refs:* [PLT-621](02-platform.md).
- **INV-162** An applicability is in *Changes Made* only if every reference row has a requirement with a matching reference ID. *Enforced by:* Workflow. *Refs:* [EHS-804](17-ehs.md).
- **INV-165** When a planned audit row with a scheduled audit is deleted, that audit, its checklists and its recommendations are voided in the same transaction. *Enforced by:* Domain. *Refs:* [AUD-013](14-audits-inspections.md).
- **INV-166** New inspection records reference only Active inspection plans. *Enforced by:* Domain. *Refs:* [INS-010](14-audits-inspections.md).
- **INV-167** A sample result is Fail if and only if some criterion whose class is among the point's sample-failure classes is Fail. *Enforced by:* Domain. *Refs:* [INS-014](14-audits-inspections.md).
- **INV-168** An inspection record result is defined only when remaining samples = 0 for every point. It is Fail if and only if a non-voided sample is Fail. *Enforced by:* Domain. *Refs:* [INS-015](14-audits-inspections.md).
- **INV-169** A sample whose outcome meets its point's *review required* condition never goes from Inspect to Close without passing through Review. *Enforced by:* Workflow. *Refs:* [INS-016](14-audits-inspections.md).

## 21.7 Security

- **INV-081** No search result, view row, chart point, report row, export line, notification, AI response or API response shows any data (including title, number or snippet) from a record the requesting identity can't open. Sensitive fields follow the same rule. *Enforced by:* the security-trimmed query layer. *Refs:* [PLT-1504](02-platform.md), [RPT-102](07-reporting.md), [SAF-053](20-safety.md).
- **INV-082** Every operation is authorized on the server for the calling identity, whatever the client. Visibility rules and *Hide When* rules never grant or deny access. *Enforced by:* the authorization middleware and domain checks. *Refs:* [DSN-104](03-designer.md), [SEC-519](04-security.md), [INT-102](06-integrations.md).
- **INV-083** Without a matching grant, access is denied. *Enforced by:* the authorization service. *Refs:* [SEC-501](04-security.md).
- **INV-084** Every application has at least one active Manager. Removing the last one is rejected. *Enforced by:* Domain. *Refs:* [SEC-502](04-security.md).
- **INV-085** Every location referenced by a record has a location profile. *Enforced by:* DB foreign key. *Refs:* [ADM-402](05-administration.md).
- **INV-086** Access to a co-authoring file on the external platform never exceeds the user's access to the Vellum record. Revoking record access revokes file access. *Enforced by:* the sync service and permission reconciliation. *Refs:* [INT-506](06-integrations.md).
- **INV-087** Only a calibration or maintenance schedule's assignee (or an application manager) can create a record from that schedule. *Enforced by:* Domain. *Refs:* [AST-020](18-assets.md).
- **INV-088** External supplier users can access only records linked to their own supplier organization. *Enforced by:* the authorization service. *Refs:* [SUP-004](15-supplier-quality.md).
- **INV-143** Scripts and queries can never read password or secret values, credentials, or another tenant's data. *Enforced by:* the sandbox API surface. *Refs:* [DSN-1006](03-designer.md).
- **INV-144** Every query runs parameterized. No query text is ever built by concatenating user or record data. *Enforced by:* the query engine API. *Refs:* [DSN-1102](03-designer.md).
- **INV-150** The login and password-reset flows behave the same for existing and non-existing usernames (no account enumeration). *Enforced by:* the authentication service. *Refs:* [SEC-101](04-security.md).
- **INV-151** Plaintext passwords are never stored, logged or transmitted after login processing. *Enforced by:* the authentication service. *Refs:* [SEC-202](04-security.md).

## 21.8 Links, concurrency and atomicity

- **INV-090** For dual-link fields, *A links to B in field f* holds if and only if *B links to A in f's paired field*. *Enforced by:* Domain, in the same transaction. *Refs:* [PLT-702](02-platform.md).
- **INV-091** At most one user holds the edit lock on a record at a time. *Enforced by:* DB lock row. *Refs:* [PLT-901](02-platform.md).
- **INV-092** A write commits only if it was based on the record's current version (optimistic concurrency). Lost updates are impossible. *Enforced by:* DB version check. *Refs:* [PLT-905](02-platform.md), [PLT-1703](02-platform.md).
- **INV-093** Every record-level change is atomic. A bulk, import or integration operation either fully applies to a given record (with its audit, signatures and automation) or doesn't apply to it at all. *Enforced by:* a DB transaction per record (or per job, for all-or-nothing modes). *Refs:* [PLT-1104](02-platform.md), [INT-111](06-integrations.md), [SAF-033](20-safety.md).

## 21.9 Revision control

- **INV-100** For every controlled document family *D*, at most one revision is Effective at any time. A revision becoming Effective and its predecessor being superseded happen in one transaction. *Enforced by:* DB partial unique index on (family, effective = true), plus Domain. *Refs:* [DOC-052](10-document-control.md), [DOC-053](10-document-control.md), [EHS-903](17-ehs.md).
- **INV-101** A document type has at most one enabled document type profile. *Enforced by:* DB partial unique index. *Refs:* [DOC-002](10-document-control.md).
- **INV-102** All revisions of *D* share one document number. Revision identifiers within *D* are unique and, for automatic formats, strictly increasing. *Enforced by:* DB and Domain. *Refs:* [DOC-013](10-document-control.md).
- **INV-103** For each *D*, at most one pending item exists among {change request, obsolete request, periodic review, pending new revision}, as the exclusion rules in [DOC-097](10-document-control.md) allow. No request exists on a non-Approved revision. *Enforced by:* Domain.
- **INV-160** A change record is Completed only if every linked affected document and specification action has reached its target state (effective or obsolete), unless the template explicitly allows otherwise. *Enforced by:* Workflow. *Refs:* [CHG-042](13-change-management.md).
- **INV-163** A file published from an effective revision is never overwritten by a file from a pending revision. *Enforced by:* the publishing service. *Refs:* [INT-305](06-integrations.md).
- **INV-164** A link pinned to a specific revision (for example from an audit question) keeps resolving to that revision after newer revisions are released. *Enforced by:* the link model. *Refs:* [AUD-002](14-audits-inspections.md).

## 21.10 Training

- **INV-104** Exactly one Training Management application profile exists per tenant and environment. *Enforced by:* DB singleton. *Refs:* [TRN-001](11-training.md).
- **INV-105** Every employee profile is linked to exactly one user profile, and every user profile to at most one employee profile. *Enforced by:* DB unique foreign key. *Refs:* [TRN-010](11-training.md).
- **INV-106** Applicability is OR of rows, AND of fields within a row, OR of values within a field. Empty fields are ignored. Every row has at least one non-empty field. *Enforced by:* Domain, with a test matrix. *Refs:* [TRN-013](11-training.md).
- **INV-107** A course assignment is completed only if every prerequisite course is completed and not expired for that employee at the time of completion. *Enforced by:* Domain. *Refs:* [TRN-022](11-training.md).
- **INV-108** Every question in a question bank has at least two choices and exactly one correct choice. *Enforced by:* Domain. *Refs:* [TRN-030](11-training.md).
- **INV-109** At most one pending assignment exists per employee and course. *Enforced by:* DB partial unique index. *Refs:* [TRN-042](11-training.md).
- **INV-110** Every course schedule references exactly one course. Every enrollee comes from a pending assignment of that course. *Enforced by:* Domain. *Refs:* [TRN-050](11-training.md).

## 21.11 Release management

- **INV-111** A requirement can be linked as a child only if it is completed (approved). *Enforced by:* Domain. *Refs:* [REL-002](19-projects-tasks.md).

## 21.12 Assets, materials and deviations

- **INV-115** A calibration standard used on a calibration record was in calibration (not overdue, not quarantined) on the calibration date. *Enforced by:* Domain. *Refs:* [AST-012](18-assets.md).
- **INV-116** A calibration record whose as-found result is Fail is completed only if a linked corrective action exists or a bypass with a non-empty reason is recorded. *Enforced by:* Workflow. *Refs:* [AST-024](18-assets.md).
- **INV-117** A part's quantity decreases only when a maintenance record that consumed it is completed, and the decrease equals the quantity consumed. *Enforced by:* Domain. *Refs:* [AST-042](18-assets.md).
- **INV-118** At most one active deviation exists per (part number, defect code). An NCR with *Use As Is* is completed only with a linked active deviation. *Enforced by:* DB and Workflow. *Refs:* [NC-004](12-quality-events.md).
- **INV-119** A material or item shows the active-deviation warning if and only if an active deviation is linked to it. *Enforced by:* a derived view. *Refs:* [DEV-003](12-quality-events.md).
- **INV-161** A change classified as PAS is Completed only if a regulatory approval date and approval evidence are recorded. A CBE-30 change is Completed only if 30 days have passed since submission or an agency response is recorded. *Enforced by:* Workflow. *Refs:* [CHG-051](13-change-management.md).

## 21.13 Scheduling, alerts and integrations

- **INV-120** Escalation configurations have offsets that move strictly toward the date as the level rises, non-negative integer frequencies with *every ≤ for*, and read-only selection queries. *Enforced by:* Validator. *Refs:* [PLT-1209](02-platform.md).
- **INV-121** At most one run of a given scheduled task executes at a time. *Enforced by:* a distributed lease. *Refs:* [ADM-803](05-administration.md).
- **INV-122** Within a connection profile, each target field and each subform is mapped at most once. *Enforced by:* Validator. *Refs:* [INT-109](06-integrations.md).
- **INV-123** A regulatory feed item is acknowledged as read at the source only after the records created from it are committed. A re-delivered item never creates duplicates. *Enforced by:* the integration service (idempotency key). *Refs:* [INT-901](06-integrations.md).
- **INV-124** Every saved chart template satisfies the rule set in [RPT-105](07-reporting.md). *Enforced by:* Validator.
- **INV-125** A chart document's relative date ranges are evaluated against the generation or refresh time, never the template's creation time. *Enforced by:* the reporting engine. *Refs:* [RPT-202](07-reporting.md).
- **INV-126** A chart template with scheduling enabled never uses *match the user's primary location*. *Enforced by:* Validator. *Refs:* [RPT-501](07-reporting.md).
- **INV-127** An alert create-record action always sets every required field of the target form, or the attempt is recorded as a visible failure. *Enforced by:* the alert engine. *Refs:* [RPT-603](07-reporting.md), [RPT-606](07-reporting.md).

## 21.14 Configuration

- **INV-130** Each configuration object has an immutable ID and a design name that is unique within its type and application and is never made only of digits. *Enforced by:* DB and Validator. *Refs:* [DSN-007](03-designer.md).
- **INV-131** At most one *Hide When* rule exists per (scope, system action). *Enforced by:* Validator. *Refs:* [DSN-104](03-designer.md).
- **INV-132** A form linked to a workflow is never a non-workflow form. An activity form is never non-workflow, read-only or hidden from *New*. *Enforced by:* Validator. *Refs:* [DSN-112](03-designer.md).
- **INV-133** A disabled workflow isn't linked to any form for new records. *Enforced by:* Validator. *Refs:* [DSN-116](03-designer.md).
- **INV-134** Keyboard shortcuts are unique across a form's actions, sections and subforms and every phase of its workflows. *Enforced by:* Validator. *Refs:* [DSN-130](03-designer.md).
- **INV-135** No section is in both the editable and read-only lists of the same phase. *Enforced by:* Validator. *Refs:* [DSN-305](03-designer.md).
- **INV-136** Every activity-creator subform targets an activity workflow with at least two normal phases and one completed phase, and an assigned second phase that has a due-date duration. *Enforced by:* Validator. *Refs:* [DSN-307](03-designer.md).
- **INV-137** Inheritance and lookup mappings connect only type-compatible fields. *Enforced by:* Validator. *Refs:* [DSN-450](03-designer.md).
- **INV-138** Every view has exactly one data source. *Enforced by:* Validator and DB. *Refs:* [DSN-705](03-designer.md).
- **INV-139** Decision trees are acyclic, and every question can be reached from the root. *Enforced by:* Validator. *Refs:* [ADM-611](05-administration.md).
- **INV-140** A promotion package is applied only if its signature and content hash verify. Application is all-or-nothing. *Enforced by:* the promotion service. *Refs:* [DSN-803](03-designer.md), [DSN-804](03-designer.md).
- **INV-141** A script that goes over its resource limits leaves no partial effects. Its transaction is rolled back. *Enforced by:* the sandbox runtime. *Refs:* [DSN-1004](03-designer.md).
- **INV-142** Predicate and template hooks have no side effects. All script writes go through domain validation and are audited as *user via script*. *Enforced by:* the sandbox API. *Refs:* [DSN-1005](03-designer.md).
- **INV-145** Every configuration change produces exactly one configuration audit entry holding a before-and-after diff. *Enforced by:* the configuration repository. *Refs:* [DSN-1202](03-designer.md).
- **INV-146** Every phase transition records the configuration version it happened under. Historical records remain renderable under that version. *Enforced by:* Domain. *Refs:* [DSN-1204](03-designer.md).
- **INV-147** Editing or disabling a change template never changes existing change records. *Enforced by:* Domain (copy-on-create). *Refs:* [CHG-002](13-change-management.md).
- **INV-148** A change record leaves Approval forward only if every approval type has at least the required number of approving signatures. *Enforced by:* Workflow. *Refs:* [CHG-021](13-change-management.md).
- **INV-149** A change record leaves Implementation only if all action-plan items (and post-implementation items, before Verification) are complete. It leaves Verification only if objective evidence is recorded. *Enforced by:* Workflow. *Refs:* [CHG-024](13-change-management.md).

## 21.15 Supplier quality

- **INV-089** Only approved material profiles are linked as supplied materials. *Enforced by:* Domain. *Refs:* [SUP-012](15-supplier-quality.md).
- **INV-094** In an active scoring profile, major-criteria weights sum to 100%, and within each major criterion, main-criteria weights sum to 100%. *Enforced by:* Validator. *Refs:* [SUP-030](15-supplier-quality.md).
- **INV-095** The effective weights of the applicable sub-criteria within a main criterion sum to the main criterion's weight, with not-applicable weight redistributed proportionally. *Enforced by:* the scoring engine. *Refs:* [SUP-031](15-supplier-quality.md).
- **INV-096** At most one rating profile exists per commodity. *Enforced by:* DB unique index. *Refs:* [SUP-033](15-supplier-quality.md).
- **INV-097** The editable and required PPAP elements in the Requirements phase are exactly those defined for the chosen submission level. *Enforced by:* Domain. *Refs:* [PPAP-002](15-supplier-quality.md).
- **INV-170** A receiving line's inspection status is a deterministic function of (material profile, sampling plan, supplier-material skip-lot state history). *Enforced by:* Domain. *Refs:* [RCV-003](15-supplier-quality.md).
- **INV-171** A failed incoming inspection is closed only with a linked NCMR, or as voided with a reason. *Enforced by:* Workflow. *Refs:* [RCV-006](15-supplier-quality.md).
- **INV-172** In a BOM, an item links only its direct children. *Enforced by:* Domain. *Refs:* [PSM-003](15-supplier-quality.md).
- **INV-173** The BOM graph is acyclic. An item is approved only if all its descendants are approved. *Enforced by:* Domain. *Refs:* [PSM-003](15-supplier-quality.md).

## 21.16 Risk and food safety

- **INV-174** Each risk row has 1 to *N*_max dimensions (*N*_max = 4 for parity, configurable up to 6). *Enforced by:* Validator. *Refs:* [RSK-001](16-risk.md).
- **INV-175** Risk result ranges in a template don't overlap. Any part of the possible value domain left uncovered is flagged when the template is saved. *Enforced by:* Validator. *Refs:* [RSK-004](16-risk.md).
- **INV-176** A risk assessment in an unacceptable band is Completed only with a linked mitigation or an e-signed acceptance. *Enforced by:* Workflow. *Refs:* [RSK-014](16-risk.md).
- **INV-177** Updating ERM control-plan results never removes rows that have linked actions. *Enforced by:* Domain. *Refs:* [ERM-006](16-risk.md).
- **INV-179** An FMEA is Completed only if every row over the severity or RPN/AP threshold has a recommended action or a justification. *Enforced by:* Workflow. *Refs:* [FMEA-006](16-risk.md).
- **INV-180** An SRA is Completed only if every above-acceptable hazard has a control with implementation and effectiveness verification, and the residual-risk acceptability is signed. *Enforced by:* Workflow. *Refs:* [FMEA-011](16-risk.md).
- **INV-181** HACCP monitoring records are created only against an effective plan revision. They are evaluated against that revision's critical limits. *Enforced by:* Domain. *Refs:* [HACCP-008](16-risk.md), [HACCP-009](16-risk.md).
