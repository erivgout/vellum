# 6. Integrations, Import/Export, Collaboration and AI

**Parity sources:** Reliance help › Integrations (options, Web Services, External, Connection Profiles, SharePoint, RegScan, Compliance Obligations, Office Integration), Collaboration (Google, Microsoft 365), Import From File (administrator and author), AI Features (AI-Powered Search, Form Field Advisor, Complaints and Feedback Advisor, AI Credit Usage); Octave product page (ERP, PLM, MES, LIMS and HR connectors).

## 6.1 APIs, web services and data connections

### Public REST API

- **INT-101** Vellum shall expose a versioned, documented **REST API** (OpenAPI 3.1) covering:
  - **Records:** create, read, update, query (with Vellum Query filters), route, void, reassign; subform rows; links.
  - **Attachments:** upload and download (resumable for large files), with checksums.
  - **Search.**
  - **Audit:** audit trail and field history export.
  - **Configuration:** read-only metadata (applications, forms, fields, workflows, phases, views, keyword lists).
  - **Administration:** users, groups, locations and profiles.
  - **Jobs:** imports and exports.
  - **Reports:** report and chart data.

  *(Reliance's REST API reference isn't public, so Vellum defines its own complete API.)*
- **INT-102** Every API write shall go through **the same validation, workflow rules, authorization and audit trail** as the UI. There is no back door ([INV-082](21-invariants.md)). API routes that need an e-signature shall accept a signature token. The token comes from an interactive signing ceremony by the human signer and is never minted by a service account ([INV-113](21-invariants.md)).
- **INT-103** The API shall support idempotency keys on writes, optimistic concurrency (ETag / If-Match), cursor pagination, per-client rate limits with `429` and `Retry-After`, and bulk endpoints with per-item results.

### Webhooks

- **INT-104** **Outbound webhooks** shall be configurable per application and event: record created, phase entered or exited, field changed, record completed, voided or archived, signature applied, attachment added, and user lifecycle events.
  - Payloads are signed (HMAC-SHA256 with a timestamp).
  - Delivery is **at-least-once**, with exponential backoff for at least 24 h, then a dead-letter queue the administrator can see and replay.
  - Each event carries an event ID so receivers can de-duplicate.

### Outbound REST client

- **INT-105** **Web-service profiles** shall let Vellum call external REST and SOAP services from scripts, tasks and actions. Each profile holds base URL, authentication (none, basic, API key, OAuth 2.0 client credentials, mTLS), headers, timeout and retry policy. Credentials are stored encrypted and are environment-bound ([DSN-808](03-designer.md)). Outbound targets shall be checked against an allow-list to prevent SSRF ([NFR-037](22-non-functional.md)).

### External database lookups

- **INT-106** **External data connections** (read-only database or REST sources) shall back keyword fields of type drop-down, list box, dialog list, dialog drill-down and dialog view. The field setting gives the source table or view, the key column and the display column. Lookups shall be cached with a configurable time-to-live. If the source is unreachable, the field shall show the stored value and a warning, and the record shall stay usable.

### Connection profiles (file and message integration)

- **INT-107** **Connection profiles** shall run structured data integration in two directions:
  - **Inbound (to Vellum):** import **XML** (parity), **JSON** and **CSV** (Vellum extension) messages or files.
    - Field mapping uses XPath / JSONPath / column.
    - Subform mapping takes a base path per subform.
    - Optional schema validation (XSD/DTD/JSON Schema).
    - Target application, form and creation phase are set statically or by rule. Voided and archive phases are never targets.
    - Assignees are set statically or by rule.
    - Re-import policy: *update the matching record by key field* (optionally including completed records), *always create new*, or *error on duplicate*.
    - Processed source files and attachments are deleted or archived.
    - An **acknowledgement** is produced per message (file name from a template; default `⟨number⟩_⟨timestamp⟩` or `⟨timestamp⟩` when no record was created).
  - **Outbound (from Vellum):** export records to XML, JSON or CSV.
    - Field mapping uses fields or rules. Keyword values can be exported as text or ID. Empty tags can be omitted.
    - Multi-value fields become repeated elements.
    - Subforms are mapped per subform.
    - An output template is used, with optional schema validation that stops the export on error.
- **INT-108** Field formats for connection profiles:

  | Field type | Format |
  |---|---|
  | Date | `YYYYMMDD` and ISO 8601 |
  | Time | `HHMMSS` and ISO 8601 |
  | Date-time | `YYYYMMDDHHMMSS` and ISO 8601 (UTC unless an offset is given) |
  | Number / currency | numeric |
  | Keyword | display text or ID |
  | Multi-value | `;`-separated |
  | Names | display names or user IDs |
  | Attachments | paths under a configured root, or URLs/references |

  A source value whose type doesn't match the target field shall reject that item with a clear error.
- **INT-109** Each target field and each subform shall be mapped **at most once** per profile ([INV-122](21-invariants.md)).
- **INT-110** Connection profiles shall run on a schedule (task profile), when called through the API, or when a watched folder or queue receives a message (SFTP, cloud storage, message queue).

### Integration reliability

- **INT-111** [SAFETY] Integration processing shall be **exactly-once in effect**. Inbound messages are de-duplicated by message ID or key. Outbound messages are de-duplicated by event ID. A failure partway through a batch shall never leave a record half-imported ([INV-093](21-invariants.md)).

## 6.2 Enterprise system connectors

- **INT-201** Vellum shall provide a **connector framework** for ERP, PLM, MES, LIMS, CRM and HRIS systems. It covers scheduled or event-driven synchronization of master data (materials and parts, suppliers, customers, employees, equipment, batches and lots) and transactions (quality notifications, inspection lots, non-conformances, lab results, holds).
- **INT-202** Packaged connectors **should** be provided for at least SAP S/4HANA (materials, vendors, batches, QM notifications, inspection lots), Oracle (EBS/Fusion), Microsoft Dynamics 365, Workday (employees, positions, org units) and a generic LIMS (OOS results through REST or file). *(Reliance markets prebuilt connectors but doesn't document them publicly. See [open question](01-introduction.md#18-open-parity-questions).)*
- **INT-203** Connectors shall map external keys to Vellum records with no duplicates, and they shall report sync status and conflicts in integration logs ([ADM-1005](05-administration.md)).

## 6.3 SharePoint publishing

- **INT-301** **SharePoint profiles** (one per SharePoint site or tenant, unique by host) shall hold the connection: SharePoint Online through Microsoft Graph app registration with Sites.Selected (Vellum), or a legacy on-premises connection. They also hold the accounts to notify when publishing fails, and a list of **libraries** (identifier + relative URL).
- **INT-302** **SharePoint mapping profiles** (per form) shall map Vellum fields or rules to SharePoint column internal names. Matching is case-sensitive, mapped columns must exist, and SharePoint system columns can't be mapped. Without a mapping profile, file properties and template fields whose names match SharePoint columns are filled in automatically.
- **INT-303** Data type mapping:

  | Vellum | SharePoint |
  |---|---|
  | Number | Number |
  | Date, time, date-time | Date and Time |
  | Currency | Currency |
  | Text, keyword, names, attachment, link, external link | Single line of text |
  | Text area | Multiple lines of text |

- **INT-304** When a record is routed into, or saved within, a phase configured for publishing ([DSN-602](03-designer.md)), the system shall upload the configured main-form attachments (or their renditions) to the configured library and folder and set their metadata. Characters SharePoint doesn't allow in file names shall be replaced with underscores.
- **INT-305** [SAFETY] Files published from a **completed/effective** revision shall never be overwritten by files from a pending higher revision ([INV-163](21-invariants.md)). Publishing a new effective revision shall replace (or version, if configured) the previous effective file, so the library always shows the current effective content.
- **INT-306** A publishing failure shall not roll back the record save. It shall be retried with backoff, logged with a specific error code, and emailed to the profile's notify list. The record shall show a *publication pending/failed* indicator until publishing succeeds.
- **INT-307** Vellum shall keep a **publication history** per record: file, revision, library, URL, time and result.
- **INT-308** When a document is obsoleted, its published files shall be removed or marked obsolete in SharePoint (configurable), so uncontrolled copies don't stay available ([SAF-021](20-safety.md)).

## 6.4 Office document templates (field-merge)

- **INT-401** **Master templates** shall hold Word and Excel template files (PowerPoint templates are a Vellum extension). They can be general (offered on any attachment field of the form) or field-specific. **Location templates** linked to a master template shall **replace** its templates for records at matching locations.
- **INT-402** Template resolution: templates chosen on the **form** apply to every workflow. Templates chosen on a **workflow** are **added** for records of that workflow. Location templates replace master templates where the location matches.
- **INT-403** Template fields shall be bound by **design name** through document properties, content controls (Word) and defined names (Excel; `$` isn't allowed in names, so it is mapped). Vellum shall support:
  - **Word:** header and footer fields; multiple subform rows (`Subform.Field#n`, first row unindexed); one-row-per-document subforms (an attachment field inside the subform); inner subforms (`Outer.Inner.Field#i#j`); checkbox fields as a comma-separated list or one control per option; system subforms (phase tracking and signatures) as one read-only block; and the latest comment.
  - **Excel:** each subform on its own worksheet with a repeating row template, a key column (unique text/number), a maximum-rows marker, data validation lists for keywords, and deleting a row (or clearing its key) deletes the subform record.
- **INT-404** Sync direction per attachment field: **record → file** (on save or route), **file → record** (when the file is updated and re-attached; supported types: text, number, date, date-time, text area, dialog list and drill-down, radio, checkbox, list box, drop-down, currency; system fields only if editable), or **both**.
- **INT-405** [P11] **Signature and history** blocks shall sync **only from Vellum to the file**. They can never be edited through the file ([INV-052](21-invariants.md)). Values pushed into a file shall carry the record version they came from.
- **INT-406** [SAFETY] Template generation and sync shall run **on the server** and shall **not** need macros, VBA or ActiveX on users' machines. *(Reliance needs Office macro and ActiveX security relaxed on the client. Vellum removes that risk.)*
- **INT-407** Constraints, validated when a template is uploaded: each field used once per template (only the first occurrence syncs, with a warning on upload), no password-protected templates, synced fields not in protected regions, and *computed for display* fields not supported. Empty values in Word keep the layout (Reliance pads with six spaces; Vellum uses an empty run that keeps the layout).
- **INT-408** Track changes in Word shall be kept during sync.
- **INT-409** Templates shall power: certificates (Training), approval manifests (Document Control), official report forms (for example MedWatch 3500A, OSHA 301), and any customer form.

## 6.5 Document co-authoring (Microsoft 365 and Google Workspace)

- **INT-501** **Collaboration profiles**, one per environment and environment-bound, shall connect Vellum to:
  - **Microsoft 365**: tenant ID, client ID, secret or certificate, site, main folder path and workspace domain, through Microsoft Graph with Sites.Selected least-privilege access.
  - **Google Workspace**: domain, admin email, OAuth client ID, Drive folder ID and service-account key, with domain-wide delegation limited to a read-only directory scope.

  Profile values shall be validated on save.
- **INT-502** Users shall be able to **create** a co-authoring file in an attachment field: Word, Excel or PowerPoint on M365, or Docs on Google (Sheets and Slides on Google are a Vellum extension). A file can be blank or from a template. The default name is "⟨Type⟩ ⟨Number⟩ Collaboration" and can be edited, but it can't equal the record's tab name. Creating the file saves the record. Each collaborator's profile email must belong to the configured domain.
- **INT-503** Who may co-author:
  - **Workflow records:** only in **pending (normal) phases**, and only users with author access to the record, application managers and administrators. Co-authoring is allowed in parallel, voting and (Vellum, both platforms) sequential phases.
  - **Non-workflow records:** users with author access, application managers and administrators.
- **INT-504** **Session model (both platforms in Vellum):**
  - Co-authoring files have the states *Not started*, *In progress* and *Published*.
  - *Join* opens the file in the editor.
  - *View* opens it read-only in Vellum's previewer, which shows record info.
  - *Publish* ends the session for everyone (after confirmation) and snapshots that version into Vellum as an immutable attachment version with a checksum.
  - *Close* leaves the session without publishing.
  - *Start* begins a new session.
- **INT-505** [SAFETY] A record shall **not** be routed forward while any co-authoring file on it has an **unpublished session**. Publishing captures the exact content that was reviewed and approved ([INV-076](21-invariants.md)). *(Reliance enforces this only for Google. Vellum enforces it for both platforms.)*
- **INT-506** Sharing containment:
  - Copy-URL and external sharing shall be **blocked**, and the platform's share controls shall be disabled where the API allows it.
  - Rename happens in the editor.
  - Download is allowed for M365 by permission, and for Google through export of the published version.
  - Access to co-authoring files is governed by Vellum record security and synced to the platform's permissions ([INV-086](21-invariants.md)).
- **INT-507** System comments shall record each create, join and edit-open, publish, delete, route (one per file) and revision copy. Preview and download need not be commented.
- **INT-508** On a new revision (change request) or a restore, every co-authoring file shall be copied into the new revision with state *Not started*, and a comment is added.
- **INT-509** Unlike Reliance, **PDF conversion** shall work on **published** co-authoring versions, both in the configured phase and on routing. Field history shall record publish events and content checksums.
- **INT-510** Switching a tenant between platforms after co-authoring files exist shall be blocked until those files are migrated (Vellum shall provide migration to local attachments).
- **INT-511** Field-merge templates shall sync into online co-authoring files **record → file only**, and only while no session is open. If a sync is skipped, it shall be queued and run when the file closes.

## 6.6 Email, calendar and messaging

- **INT-601** Outbound email shall support a tenant SMTP relay, OAuth-based M365/Gmail sending, or Vellum's managed sender with SPF/DKIM/DMARC alignment.
- **INT-602** **Email masking (anti-spoofing):** when it is on, outbound mail is sent **from** a configured verified address, shows the acting user's name, and puts the user's real address in **Reply-To**. Only administrators may change this.
- **INT-603** Bounces and delivery failures shall be recorded in the record's email log and in the integration log. Repeated hard bounces to a user shall raise an administrator alert.
- **INT-604** Calendar integration (Outlook/M365 and Google Calendar) shall send, update and cancel invitations from meetings, training schedules, audits and any form with calendar mapping ([PLT-1601](02-platform.md)).
- **INT-605** *(Vellum extension, should)* **Inbound email intake**: an application-specific address that creates records from emails (for example complaints), with the email stored as an `.eml` attachment, the sender matched to a customer contact, and a configurable creation phase.
- **INT-606** *(Vellum extension, should)* Notifications should also go to Microsoft Teams and Slack channels for configured events.

## 6.7 Microsoft Project

- **INT-701** Two-way sync with Microsoft Project files shall work as described in [PRJ-009](19-projects-tasks.md). It shall be extended to Microsoft Project for the web / Planner through Graph where available.

## 6.8 Import and export

- **INT-801** **Administrator import** shall be available from the Administration Center:
  - Choose application, form, phase (for workflow forms), file (`.xlsx`, `.xlsm`, `.csv`, tab-delimited), whether to skip correction rules and keyword filters, whether to add subform rows to existing records (choose the subform), and the activity's parent.
  - It is **all-or-nothing**: any error rolls back the whole job, and nothing is committed until every row validates.
  - It supports importing into **any phase**, including completed, to migrate legacy records. Assignees are left empty for completed or archived imports.
- **INT-802** **Author import** shall be enabled per application (toolbar action, optionally hidden by rule):
  - It runs asynchronously.
  - **Bad rows are skipped and reported** in a downloadable error report.
  - Duplicates are handled through an optional key field: *create new*, *update existing* (non-workflow records or pending workflow records only) or *report only*.
  - It produces an import history record and an emailed summary.
  - It's hidden from readers, designers, anonymous users and submitters.
- **INT-803** A pending-phase import with no assignee column shall be **rejected**, with a clear message, unless a default assignee is given. *(Reliance assigns such records to the system user, which leaves orphaned work. Vellum doesn't allow that.)* [SAFETY]
- **INT-804** Import sheet rules:
  - Headers are field design names. The second row is optional help text and data starts at row 3; Vellum also accepts headers on row 1 with data from row 2.
  - One row is one record. The record number must be unique if given.
  - Keyword values may be text or ID and must match existing options.
  - Multi-value fields use `;`.
  - Dates in **ISO 8601** are always accepted, as well as the tenant locale's formats.
  - Subform sheets reference the parent record number. Inner subforms reference the outer row order. Activity sheets reference the parent number.
  - Attachments may be referenced by path in a staged upload area, by URL, or by co-authoring file ID with the prefix `COLLABORATION:`.
  - Group IDs in the assignee column expand to members.
- **INT-805** *(Vellum extension)* Every import shall support a **dry run** that validates everything and reports results without committing.
- **INT-806** Large imports shall keep running on the server however the client connection ends, with progress shown in import history.
- **INT-807** Downloadable **import templates** shall be generated for every form, including subforms and activities, straight from current configuration.
- **INT-808** Bulk import shall also be available for configuration-adjacent data: users, groups, locations, IP profiles, keyword options, names lists and training data.
- **INT-809** Views shall export to **Excel** and **PDF**. Records shall export to PDF (print) and, through the API, to JSON. Full tenant export is in [NFR-042](22-non-functional.md).

## 6.9 Regulatory content and submission gateways

- **INT-901** **Regulatory content feed (RegScan or equivalent):** a scheduled task shall download new regulatory changes. Each regulation document creates an **applicability** record (type = level, requirement = name + description, change date = amendment date), and each requirement adds a reference row (section, heading, description, link, reference ID). The record is assigned for review, and the feed is acknowledged as read **only after** the records are committed ([INV-123](21-invariants.md)).
- **INT-902** **FDA eMDR gateway:** an **eMDR profile** (one per environment, environment-bound and not promotable) shall hold:
  - Account type: test or production.
  - Sender organization, sender routing ID and email.
  - Acknowledgment URL.
  - FDA routing ID, ESG URL and submission center (FDA defaults, which can be overridden).
  - SSL certificate details (downloadable).
  - Encryption certificate generation.

  Certificate fields shall reject `( + < > ; " / \`. Generating a new certificate shall warn that submissions will fail until the FDA has the new certificate. Submissions use AS2 (or the FDA's current gateway API) with acknowledgment tracking (ACK1/ACK2/ACK3) ([AER](12-quality-events.md#128-adverse-event-reporting-emdr--vigilance)).
- **INT-903** *(Provisional)* Other gateways **should** be supported through the same pattern: EU EUDAMED vigilance, Health Canada MDPR, and OSHA Injury Tracking Application (ITA) electronic submission of Forms 300A/300/301 ([EHS §17.1](17-ehs.md)).

## 6.10 AI features

All AI features are governed by [SAF §20.6](20-safety.md#206-ai-safety). [AI]

- **INT-1001** AI features shall be licensed per tenant, **off by default**, and switched on one by one in System Preferences per environment. Features that need the search index shall say so, and they can't be switched on until indexing is running.
- **INT-1002** **AI-powered search:** two modes, *Smart* (semantic and intent matching plus keywords and filters) and *Exact* (keywords only). The user can switch modes, and the choice lasts for the session.
  - Both modes search pending and completed records and attachment content (PDF, DOCX, EML, MSG).
  - Natural-language filter extraction (shown as removable chips) covers: assigned to, workflow status (open/in progress, awaiting release, completed, non-workflow), created/modified date or range, created by, location (hierarchical, parent to children), and application.
  - Results are ranked by relevance (ties go to most recently modified) and show number, filters and a relevance score.
  - Vellum extension: negated and multi-value filters (Reliance supports only one positive value per filter).
- **INT-1003** **Complaint & Feedback Advisor:** an assistant panel on saved records of the configured form types. Actions:
  1. **Summarize**: record plus supported attachments (images, EML/MSG, PDF, DOCX). Covers issue, dates, locations, customer, products and serial numbers, actions already taken, potential safety concerns, potential compliance issues and references.
  2. **Similar records**: the top 3 the user is allowed to see, with a rationale for each.
  3. **Customer urgency rating**.
  4. **Potential product risk rating**.

  Ratings 3 and 4 use the scale *Critical / High / Moderate / Low / Negligible / Undetermined* with a rationale. Every response shows a confidence level (low/medium/high), when it was generated, links to sources, and thumbs up/down, copy and download actions. Responses are in the user's language when localization is on.
- **INT-1004** **Form Field Advisor:** a designer-configured action. It takes one text-area input and fills up to 5 target fields (drop-down, checkbox, radio, list box, drill-down, system keyword) with suggestions learned from existing records.
  - Configuration status: *pending / available / unavailable*, with errors in the log.
  - Suggested values are marked as AI-suggested until the user edits or accepts them, and they always need user review before save.
  - An overall confidence is shown, with a per-field explanation.
- **INT-1005** *(Vellum extensions, should)*:
  - **Root-cause assistant** for CAPA, NC and deviations (suggests causes and 5-Why chains, cited).
  - **Document change summarizer** (a plain-language diff between revisions for reviewers and trainees).
  - **Training quiz generator** from a controlled document (questions still need author approval).
  - **Regulatory change impact assistant** (maps new regulatory requirements to affected documents and obligations).
  - **Translation assistant** for record text (clearly labeled).
- **INT-1006** **AI credits:** usage is metered per feature. Administrators can see allocation, consumption and balance. What happens at zero balance shall be configurable: block the feature or allow overage. It shall never degrade non-AI functions.
- **INT-1007** [SAFETY] AI features shall be **security-trimmed** the same way as search (PLT-1504): record security, location and additional security, **and** field sensitivity (SEC-520). Content hidden only by visibility rules shall be **excluded** from AI context by default. *(Reliance includes it.)*
- **INT-1008** AI features need *Search Indexing*. Vellum's index **can** be turned off and purged by an administrator, unlike Reliance where indexing can't be undone once started. Turning it off stops dependent features.
