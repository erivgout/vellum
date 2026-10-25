# 20. Safety Requirements

Vellum isn't a medical device and doesn't control machinery. It **is** the system of record that people rely on to decide:

- which procedure to follow;
- whether a product can ship;
- whether equipment is fit to use;
- whether a hazard is controlled;
- whether a regulator has to be told about an injury or a device malfunction.

A software failure that shows the wrong revision, loses an action, skips a deadline, or quietly drops a safety alert can contribute to harm to **people** (workers, patients, consumers), **product**, the **environment**, or the organization's **regulatory standing**.

This section lists the requirements that prevent or reduce those outcomes. Each one is tagged **[SAFETY]** wherever it appears in the module files. Every [SAFETY] requirement shall have automated regression tests ([NFR-085](22-non-functional.md)), and each is traced in the hazard log (§20.9).

## 20.1 Approach

- **SAF-001** Vellum shall keep a **software hazard log** for the product itself, using an ISO 14971-style process adapted for software: hazard → hazardous situation → harm → cause → risk control → verification. The log is reviewed at every release. The starting entries are in §20.9.
- **SAF-002** Each release's risk assessment ([NFR-082](22-non-functional.md)) shall cover the [SAFETY] and [P11] requirements touched by that release, and record the residual risk.
- **SAF-003** When a requirement elsewhere in this specification conflicts with a SAF requirement, the SAF requirement wins. Any deviation needs a documented risk acceptance from the product safety owner.

## 20.2 Regulated mode and compliance-sensitive configuration

- **SAF-010** Each tenant shall have a **regulated mode** flag. It is on by default for tenants that select life sciences, food, or EHS packages. In regulated mode, the system shall **reject** configuration that weakens compliance controls:
  - turning off electronic signatures where a packaged workflow requires them;
  - the *username only, no re-authentication* signing option ([SEC-610](04-security.md));
  - lowering password or session controls below the minimums;
  - letting configuration skip the system audit trail (this can't be done anywhere, but it's restated here);
  - letting escalations skip validation without a recorded justification;
  - turning off malware scanning.

  Outside regulated mode, changing any of these settings shall need an administrator e-signature and a reason, and the change is recorded in the configuration audit trail ([ADM-101](05-administration.md)).
- **SAF-011** Every compliance-sensitive setting shall be **identified in the UI** with a marker and an explanation of its compliance effect, and listed in a *compliance posture* report that administrators and auditors can view.
- **SAF-012** **Non-production environments** shall always show a banner that can't be dismissed ([ADM-103](05-administration.md)). Notifications sent from them shall carry a subject prefix (for example `[TEST]`) and may be routed to a sink address, so test emails can't be mistaken for real assignments or safety alerts.
- **SAF-013** Copying production data into a non-production environment shall need administrator approval. Personal and sensitive fields shall be **masked or pseudonymized** by default.
- **SAF-014** The platform time source shall be a trusted, monitored time service (NTP with at least 3 sources). Clock skew beyond 1 second shall raise an operational alert. Audit timestamps and regulatory deadlines depend on this ([INV-055](21-invariants.md)).

## 20.3 Safety-critical alerts and regulatory deadlines

- **SAF-020** **Safety-critical alerts** shall be their own notification class. It covers:
  - fatality and serious-injury incidents (8-hour and 24-hour OSHA clocks, [EHS-114](17-ehs.md));
  - reportable complaints and MDR/vigilance deadlines ([CMP-009](12-quality-events.md));
  - eMDR submission failures ([AER-010](12-quality-events.md));
  - HACCP critical-limit deviations ([HACCP-009](16-risk.md));
  - environmental permit exceedances ([EHS-603](17-ehs.md));
  - critical quality events ([QE-103](12-quality-events.md));
  - failed alert actions ([RPT-606](07-reporting.md));
  - quarantined measurement equipment that is still in use.

  Safety-critical alerts:
  - shall **never** be digested, batched, muted or turned off by user preference ([PLT-1205](02-platform.md));
  - shall go out by email **and** in-app, plus push or SMS where configured;
  - shall need **acknowledgment**, with the acknowledger, time and channel recorded;
  - shall **escalate** up the configured chain at configured intervals until acknowledged;
  - shall appear on an *Open safety-critical alerts* dashboard for EHS and quality leads.
- **SAF-021** **Currency of controlled information:** when a controlled item is superseded or obsoleted (document, SDS, JSA, emergency plan, HACCP plan, requirement), the system shall, in the same transaction or as a durable follow-up task, withdraw the old content from every distribution point it controls:
  - views of effective documents;
  - SharePoint publications ([INT-308](06-integrations.md));
  - mobile offline caches (invalidated at next sync, and shown as *superseded* if opened offline);
  - public SDS lookup pages;
  - lists of controlled prints for reconciliation.

  Owners of downstream items that reference the changed item (for example controls linked to a revised requirement, [EHS-806](17-ehs.md)) shall get review tasks.
- **SAF-022** **Regulatory gating:** the system shall stop a record from reaching an effective or completed state while a required regulatory precondition is unmet. Examples:
  - PAS approval not recorded ([CHG-051](13-change-management.md));
  - CBE-30 waiting period not elapsed;
  - eMDR acknowledgments not received ([AER-009](12-quality-events.md));
  - required training not complete for training-gated releases ([TRN-071](11-training.md)).

  Overriding a gate shall need a role with override authority, a justification and an e-signature, and the override is reported on the compliance posture report.
- **SAF-023** **Deadline clocks** (OSHA, MDR, vigilance, permit expiry, CAPA due dates, calibration due dates) shall be calculated from the **source event date** (awareness date, incident date, expiry date), not from when the record was entered. Any backdated entry shall be shown, and late entry beyond a threshold shall be flagged in the audit trail.
- **SAF-024** Deadline calculations shall handle time zones, DST changes and business calendars correctly. Calendar-day deadlines shall ignore business calendars. Business-day deadlines shall use the location's calendar ([ADM-302](05-administration.md)). Each deadline shall show the rule that produced it.

## 20.4 Automation and scheduler reliability

- **SAF-030** **No silent skips:** scheduled work shall detect missed runs after downtime and catch up according to policy, with an administrator alert. Scheduled work covers escalations ([PLT-1211](02-platform.md)), recurring tasks ([TSK-005](19-projects-tasks.md)), training expiration, calibration and maintenance due notices, permit and SDS reviews, alert templates and monitoring schedules ([ADM-805](05-administration.md)).
- **SAF-031** **Durable side effects:** automation triggered by a committed transaction shall be recorded in a transactional outbox in the same commit. It is then executed at least once, idempotently, with retries. If it fails permanently, the failure shall be visible on the source record and in the operational health dashboard ([ADM-1008](05-administration.md)) ([INV-040](21-invariants.md)). Such automation includes auto-created action items, child activities, notifications, archival and publishing.
- **SAF-032** A failed automation step shall never leave a workflow stuck invisibly. Every record with a failed or pending automation shall show a banner with the failing step and a *retry* action for authorized users.
- **SAF-033** Bulk operations (imports, bulk route, change field value, promotion apply) shall validate before committing and report a result for every item. They shall never leave a single record partially updated ([INV-093](21-invariants.md)).

## 20.5 Product, equipment and process safety controls

- **SAF-040** **Quarantined measurement equipment:** assets that are *out of tolerance / quarantined* or *overdue* shall be refused as measurement tools and as calibration standards ([AST-025](18-assets.md), [INS-013](14-audits-inspections.md)). An as-found failure shall start an impact assessment of past measurements ([AST-024](18-assets.md)). Overrides need a justification, an e-signature and an automatic quality event.
- **SAF-041** **Material holds** from NCRs ([NC-007](12-quality-events.md)), failed receiving inspections ([RCV-006](15-supplier-quality.md)) and HACCP deviations shall be shown on material and lot records. Through integration, they shall be sent to ERP and MES as holds that can be released only by an approved disposition.
- **SAF-042** **Active deviations** shall be clearly flagged on the affected material and item records, and removed automatically when they expire ([DEV-003](12-quality-events.md)).
- **SAF-043** **Effective-only use:** wherever a controlled document is shown for operational use (reader views, mobile, kiosk, QR links, SharePoint), the **effective** revision shall be shown, with its revision and effective date visible. Draft or superseded content shown to authorized users for other purposes shall carry a clear *DRAFT* or *SUPERSEDED* watermark or banner ([DOC-080](10-document-control.md), [INV-100](21-invariants.md)).
- **SAF-044** **Segregation of duties** rules shall be configurable and enforced for safety-relevant approvals. At minimum: author ≠ approver for controlled documents (configurable), re-test analyst ≠ original analyst ([LAB-005](12-quality-events.md)), and CAPA implementer ≠ effectiveness verifier (configurable). The system shall block assigning the same person to conflicting roles on the same record, or warn when the rule is set to warn.
- **SAF-045** **Risk acceptance:** risks in unacceptable bands can't be closed without mitigation or an e-signed acceptance by an authorized role ([RSK-014](16-risk.md), [FMEA-006](16-risk.md), [FMEA-011](16-risk.md), [EHS-304](17-ehs.md)).

## 20.6 AI safety

[AI] requirements are governed by this subsection. "AI" means any machine-learned or large-language-model feature: semantic search, summarization, suggestion, rating, classification, forecasting.

- **SAF-050** **Advisory only:** AI shall never route a record, sign, approve, reject, void, delete, change security, send external communications, or make a regulatory determination such as MDR reportability or OSHA recordability. Every AI output is a **suggestion** that a human must review and act on.
- **SAF-051** **Human acceptance is recorded:**
  - When a user accepts AI-suggested field values, the audit trail shall record that the value came from AI, along with the feature, the model version and the accepting user.
  - AI-suggested values shall stay visibly marked until the user edits or confirms them ([INT-1004](06-integrations.md)).
  - AI ratings (urgency, product risk) shall never fill in a regulated field on their own.
- **SAF-052** **Provenance and confidence:** each AI output shall show what it was based on (links to records and attachments), a confidence indicator, when it was generated, and a reminder that it must be reviewed. Generated summaries shall never be stored as the record's official text unless a user copies them in and the copy is attributed.
- **SAF-053** **Access-scoped context:** AI context shall be limited to content the requesting user can access, including record, location, additional and sensitive-field security. It shall exclude content hidden by visibility rules by default ([INT-1007](06-integrations.md)). AI features shall never reveal the existence or content of records the user can't open ([INV-081](21-invariants.md)).
- **SAF-054** **Prompt-injection resistance:** record and attachment content given to AI models shall be treated as **untrusted data**. Instructions in that content shall not be able to change system behavior, reach other records, call tools or raise privileges. AI features shall run without write tools.
- **SAF-055** **Data protection:**
  - Customer data shall not be used to train or fine-tune shared models without explicit written opt-in.
  - AI processing shall stay in the tenant's data residency region ([NFR-040](22-non-functional.md)).
  - Prompts and outputs shall be kept only as long as configured, and can be purged.
- **SAF-056** **Kill switch and degradation:**
  - Each AI feature can be turned off per tenant and per environment immediately.
  - AI outages, or running out of credits, shall never affect non-AI functions ([INT-1006](06-integrations.md)).
- **SAF-057** **Evaluation:** each AI feature shall have a documented evaluation set and acceptance thresholds, re-run whenever the model or prompts change. Results shall be published in the release notes for regulated customers.
- **SAF-058** **Bias and harm review:** AI urgency and risk ratings shall be evaluated for systematic under-rating of safety-relevant complaints. A rating of *Undetermined* is required when confidence is low, never a guess.

## 20.7 Data integrity (ALCOA+)

- **SAF-060** **Attributable:** every create, change, signature, route and deletion shall be attributed to a specific identity (a user, a delegate acting for a user, or the system identity with its triggering rule). Shared accounts shall be prohibited for interactive use ([INV-051](21-invariants.md)).
- **SAF-061** **Legible and enduring:** records, audit trails and signatures shall stay readable for their full retention period, including after configuration changes ([INV-146](21-invariants.md)), field deletions (a deleted field's historical values are kept and shown read-only) and platform upgrades.
- **SAF-062** **Contemporaneous:** server UTC timestamps shall be recorded at the moment of each action. Client-supplied timestamps (offline mobile capture) shall be stored *as well as* the server receipt time, never instead of it.
- **SAF-063** **Original:** uploaded files shall be kept byte-for-byte, with checksums ([INV-053](21-invariants.md)). Renditions shall be derived copies linked to their originals.
- **SAF-064** **Accurate:** calculations (RPN, scores, calibration deviations, OSHA totals, sample results, risk values) shall be done on the server with exact arithmetic, versioned with the record, and covered by unit tests against the reference examples in this specification ([INV-062](21-invariants.md)).
- **SAF-065** **Complete, consistent, available:** no silent data loss on lock break ([PLT-904](02-platform.md)), session timeout (auto-saved drafts), offline sync conflicts ([PLT-1703](02-platform.md)) or failed conversions ([PLT-807](02-platform.md)).
- **SAF-066** **Data integrity monitoring:** a scheduled integrity checker shall verify attachment checksums, audit-trail hash chains ([INV-055](21-invariants.md)) and signature links. Any discrepancy shall be reported to administrators as a security event.

## 20.8 Configuration change safety

- **SAF-070** Configuration shall take effect only through **publish** with validation ([DSN-003](03-designer.md), [DSN-1201](03-designer.md)) or through an atomic, signed **promotion** ([DSN-804](03-designer.md)). In regulated mode, publishing to production needs a linked, approved configuration change request ([REL-007](19-projects-tasks.md)).
- **SAF-071** Before publishing, the designer shall show **impact analysis**: records currently in affected phases, scripts and queries that reference changed objects, and views and reports that would break.
- **SAF-072** Records in flight shall keep the configuration version they started each phase under, where changing it would affect assignments, routing or signatures ([PLT-614](02-platform.md), [INV-071](21-invariants.md)).
- **SAF-073** Script and query sandboxes shall stop a misconfiguration from corrupting data at scale. Limits apply, writes go through validation, and bulk-writing tasks shall support a **dry-run** mode in non-production ([DSN-1004](03-designer.md), [DSN-1007](03-designer.md)).

## 20.9 Software hazard log (initial)

| # | Hazard / failure | Potential harm | Key risk controls (requirements) |
|---|---|---|---|
| H1 | Operator follows a superseded or draft procedure | Injury, product defect, regulatory finding | DOC-052/053, DOC-054, SAF-021, SAF-043, INT-305/308, INV-100 |
| H2 | Required training not completed before a procedure takes effect | Untrained worker does a hazardous task | DOC-063, TRN-071, SAF-022, TRN-025 |
| H3 | Fatality or serious injury not reported to OSHA in time | Regulatory penalty; missed corrective action | EHS-114, SAF-020, SAF-023 |
| H4 | Reportable device complaint not submitted to the FDA in time | Patient harm from an unaddressed device issue; enforcement action | CMP-004, CMP-009, AER-008..010, SAF-020 |
| H5 | Out-of-tolerance gauge stays in use | Defective product shipped; safety-critical dimension unverified | AST-024/025, AST-051, INS-013, SAF-040 |
| H6 | HACCP critical-limit breach not acted on | Foodborne illness, allergen exposure | HACCP-009/010, SAF-020, SAF-041 |
| H7 | Nonconforming material released without disposition | Defective or unsafe product reaches the customer | NC-004, NC-007, RCV-006, SAF-041, DEV-003 |
| H8 | Escalation or recurring compliance task silently skipped | Overdue CAPA, missed inspection, expired permit | PLT-1211, TSK-005, ADM-805, SAF-030, EHS-404 |
| H9 | Auto-created action items lost after a failure | Planned corrective work never happens | QE-002, SAF-031/032, INV-040 |
| H10 | Electronic signature forged, reused or detached | Invalid approval of unsafe change; Part 11 violation | SEC-602..604, PLT-1306, INV-052, INV-113, INV-114 |
| H11 | Audit trail altered or incomplete | Undetectable falsification; inspection failure | PLT-1001/1006, SAF-066, INV-050, INV-055 |
| H12 | User sees records they shouldn't (search or AI leak) | Privacy breach (injury or medical data); trade secret loss | PLT-1504, SEC-519/520, INT-1007, SAF-053, INV-081 |
| H13 | Untested configuration change breaks a workflow in production | Approvals bypassed; records stuck | DSN-003, DSN-1201, DSN-804, SAF-070..073 |
| H14 | Script misbehaves in bulk (mass mis-route or overwrite) | Large-scale data corruption | DSN-1004/1005, SAF-073, INV-141, INV-142 |
| H15 | AI suggestion accepted blindly (for example under-rated urgency) | Delayed handling of a safety complaint | SAF-050..058 |
| H16 | Change effective before regulatory approval (PAS) | Distribution of an unapproved product change | CHG-051, SAF-022, INV-161 |
| H17 | Unacceptable risk closed without mitigation | Uncontrolled hazard | RSK-014, FMEA-006/011, EHS-304, SAF-045 |
| H18 | Wrong time or deadline from time-zone or DST error | Missed regulatory deadline | SAF-014, SAF-023/024, NFR-062 |
| H19 | Test environment email mistaken for a production alert | Wrong action or alert fatigue | SAF-012 |
| H20 | Obsolete SDS shown to a worker | Chemical exposure from outdated hazard data | EHS-903, EHS-905, SAF-021 |
