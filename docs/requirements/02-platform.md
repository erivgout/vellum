# 2. Core Platform (Runtime Behavior)

This section covers what the platform does at run time for every application: records, forms, workflow execution, assignment, linking, attachments, audit, views, notifications, the homepage, search and mobile. How each of these is *configured* is in [03-designer.md](03-designer.md).

**Parity sources:** Reliance help › Learn the Basics (Homepage, All Applications, Application Page, Document Page, Search), Keyboard Shortcuts, Unlock Documents, Delegation, Designer › Settings (Application, Form, Section, Subform, Field, Workflow, Phase, Action, View, Column, Data Source, Mobile), Escalations.

## 2.1 Applications and navigation

- **PLT-101** The system shall show each user an **All Applications** page with a card for every application they can access, and only those. Each card shall show the name, a description on hover, a favorite toggle, a link to the paired archive application (if one exists) and, for users with designer rights, a *Design* action.
- **PLT-102** A user shall not be able to open an application if every view in it is hidden from them. The system shall show a clear message rather than an empty application.
- **PLT-103** A persistent **main header** shall offer: home, the current application (closing it closes all its open records), global search (if enabled), the user's **default application** shortcut (if set), a **Favorites** menu (applications, views, records; records grouped by application with counts), All Applications, and the user menu (profile, preferences, sign out, help, privacy policy).
- **PLT-104** Administrators shall also get a **System Actions** menu: About, System Preferences, System Log, User Session Status, and *Log out all users*.
- **PLT-105** Users shall be able to open several records and applications at once in tabs inside the app, and switch between them without losing unsaved work.
- **PLT-106** Administrators shall be able to set an environment **banner** (for example "VALIDATION ENVIRONMENT – NOT FOR PRODUCTION USE") and personalize the login page ([ADM §5.1](05-administration.md)).

## 2.2 Records

- **PLT-201** Every record shall be an instance of exactly **one form** and, for workflow forms, exactly **one workflow** (its *document type*) at a time ([INV-001](21-invariants.md)).
- **PLT-202** Every record shall have an immutable internal ID and a human-readable **number**. Numbering is *sequential* (system-assigned, read-only) or *manual* (user-entered and checked for uniqueness), with optional prefix and suffix (static, expression or script), zero-padding (default 5 digits), and a starting number (default 1). Numbering configured on the workflow overrides the form's, and any value the workflow leaves unset falls back to the form's. A form may keep a **single sequence** across all its workflows.
- **PLT-203** Forms may be **non-workflow** forms (profiles, master data). These have no phases. They are enabled or disabled instead of being routed, and their edit/read state is governed by section rules.
- **PLT-204** Records shall carry system fields: created by/on, modified by/on, current phase, phase type, due date, assignees, locations, additional-security filter, owners, reviewers, electronic distribution, hard-copy distribution, review date and effective date (where enabled). [CFG]
- **PLT-205** **Activity** forms shall be created from a parent record. They shall be linked to their parent both ways, and they shall travel with the parent on archive.
- **PLT-206** Hard deletion of records shall be possible only for users in the application's *Who can delete records* list. It shall never be possible for records that are locked by retention policy, have been signed, have left their starting phase, or are on legal hold ([INV-010](21-invariants.md)). Every deletion shall be logged in **Deletion History**, with record identity, a metadata snapshot, user, time and reason ([ADM §5.10](05-administration.md)).
- **PLT-207** Disabled forms and workflows shall be hidden from *New* menus. If disabling leaves an application with no creatable forms, the *New* action shall be hidden.

## 2.3 Record page

- **PLT-301** The record page shall have: a title (built from the form's title expression, with defaults *"⟨Workflow⟩ (New)"* for new records and *"⟨Workflow⟩ #⟨Number⟩"* otherwise); primary actions (Save, Save & Close, Send/route, with several routes grouped in a menu); a **navigator** to jump between tabs and sections; a **phase tracker**; a **comments pane**; and a toolbar with configured and system actions.
- **PLT-302** The **phase tracker** shall show completed phases, the current phase, the next phases available and the remaining phases. The current phase shall be colored by due-date status (overdue, due today, future). It can be collapsed. Per workflow, it may show the tracker, the routing actions, or both, and it may start expanded.
- **PLT-303** The **comments pane** shall show the latest comment when the record opens. Users can add a comment, and the full comment history opens in a dialog. System events (reviews, obsolescence, collaboration events, restores) shall appear as visually distinct system comments. Comment history is read-only unless the form allows editing; edits are themselves audited ([INV-050](21-invariants.md)).
- **PLT-304** Sections may be configured as **optional**. The user then shows or hides them through a selector on the record. On workflow forms, sections are **editable, read-only or hidden per phase** according to the visibility matrix in [DSN-305](03-designer.md).
- **PLT-305** The system shall enforce **read mode** for records in Completed, Voided, Archived and Obsolete phases, and in any phase configured as read mode (where an *Edit* action may be offered to authorized users).
- **PLT-306** Users shall be able to add the current record to **Favorites**, and create a new related record from the current one (*New* from record).
- **PLT-307** Date and date-time fields shall accept both typed entry (in any supported locale, not only English) and a calendar picker. Administrators may turn typed entry off.

## 2.4 Field behavior at run time

- **PLT-401** The platform shall support these field types: text (optionally a color picker), text area, rich text, static text prompt, number, currency, date, time, date-time, keyword fields (drop-down, checkbox, radio, list box, dialog list, dialog drill-down hierarchy, dialog view lookup), names (users/groups), attachment, internal link, external link (URL), and system fields. It shall also support Vellum's **risk matrix** and **signature** field types ([DSN §3.4](03-designer.md)).
- **PLT-402** Each field shall have a display format: **Editable** (stored), **Editable for display** (not stored), **Computed** (read-only, recalculated and stored on create, open and save), **Computed for display** (read-only, not stored), or **Computed when composed** (set once on create, then frozen). Default-value rules run on *create* for Editable, Editable-for-display and Computed-when-composed fields, and on *create, open and save* for Computed and Computed-for-display fields ([INV-060](21-invariants.md)).
- **PLT-403** *Correction* rules shall run on refresh and save, and may normalize values (for example take an absolute value or replace a substring).
- **PLT-404** Keyword values shall be stored by **stable keyword ID**, never by label. That way, renaming or translating a label never changes stored data ([INV-061](21-invariants.md)).
- **PLT-405** Names and keyword fields shall autocomplete after the third character typed, show the best match highlighted, and load more results 30 at a time.
- **PLT-406** Date-time fields shall support *viewer's local time zone* (optionally with the zone shown), *fixed time zone* (with a default zone) and *show UTC equivalent*, plus 12/24-hour display. Stored values are always UTC ([NFR-062](22-non-functional.md)).
- **PLT-407** Number and currency fields shall support up to 30 significant digits and up to 30 decimal places. Currency fields shall set decimals from the currency, and both types may trim trailing zeros. Values shall use exact decimal arithmetic, never binary floating point ([INV-062](21-invariants.md)).
- **PLT-408** A field may be marked **protected from administrative updates**. Then no one, administrators included, can change it through bulk *Change Field Value* or *Edit All Fields*.
- **PLT-409** Attachment fields may show image files inline (configurable extensions). This shall work for any Unicode file name.
- **PLT-410** A field may be **unique** across the form. A set of fields may form a **unique composite key**. The system shall reject a save that would break either ([INV-021](21-invariants.md)).

## 2.5 Validation and required fields

- **PLT-501** Required-field checks and validation rules shall run on the triggers enabled for the form and workflow: **on save**, **on send forward** (default), **on send back** and **on void**. "On save" covers save, save & close, and switching to read mode. [CFG]
- **PLT-502** A field may be required **only in specific phases**, declared by rule ([DSN-410](03-designer.md)).
- **PLT-503** *Prevent null values* shall be enforced on **every** save path, including bulk routing, imports, API writes, escalations and scheduled tasks. Plain *required* checks follow the configured triggers ([INV-063](21-invariants.md)).
- **PLT-504** A subform may be required. It then needs at least one complete record, unless it is hidden or not editable.
- **PLT-505** Validation failures shall block the action and show (a) a summary at the top of the record and (b) a message next to each failing field. Form-level (cross-field) rule failures shall show at the top.
- **PLT-506** Field-level rules shall apply only while the field is displayed. Form-level rules shall always apply, even when fields are hidden.

## 2.6 Workflow execution

### Phases

- **PLT-601** Phase types: **Normal**, **Completed**, **Voided**, **Archived** and **Obsolete**. Only Normal phases have assignees and due dates. Terminal phases are always read-only ([INV-032](21-invariants.md)).
- **PLT-602** A new record shall start in a phase marked as a **starting phase**. A workflow may have several starting phases, and the creator picks one when there is more than one.

### Routes

- **PLT-603** From a phase, the assignee may route to **forward phases**, **backward phases**, **special forward phases** (only users in *who can use special forward*), and the **void phase** (only users in *who can void*). The allowed targets shall be worked out at run time from static lists, rules on record data, and scripts. When more than one is configured, the result is their **union** ([DSN-610](03-designer.md)).
- **PLT-604** Only the record's **current assignee(s)** may route it. Delegates acting for them and administrators using *Global Route* may also route it. Document managers who aren't assignees shall not route ([INV-070](21-invariants.md)).
- **PLT-605** Routing shall open a **phase dialog** that shows the target phase, the assignee picker (governed by the target phase's assignment rules), the due date (calculated, and editable if allowed), the users to notify, and a comment. Voiding shall require a comment and skips assignee selection.
- **PLT-606** A phase may list itself as a forward target, so a record can be handed to another user in the same phase. **Resend** (if enabled) shall re-send the record to another assignee within the phase, optionally with a new due date.
- **PLT-607** **Reassign** shall be available to: the current assignee (if the application lets authors reassign their own assignments), application authors allowed to reassign any assignment, document managers and application managers. Reassignment may be required to follow the phase's assignment rules.
- **PLT-608** If the user routing a record is also an assignee of the target phase, the record shall stay open and no self-email shall be sent, when configured.

### Routing types

- **PLT-610** **Parallel:** all assignees get the record and an email at once. Only **one** assignee may edit at a time (lock). Any assignee may complete the phase, or pass it on with *Assign to next person*. The record appears under each assignee in views.
- **PLT-611** **Sequential:** assignees work in their configured order. Only the current assignee is notified and may edit. Each hand-off adds a phase-tracking row. The **last** assignee routes onward.
- **PLT-612** **Voting/quorum:** like Parallel, but the record moves forward only after the required **number** or **percentage** (0–100) of assignees approve. Each vote shall be recorded with signer, time, decision and comment, and shall need an e-signature where configured [P11]. A rejection by any voter shall send the record back if configured. Otherwise rejections only count toward the quorum.
- **PLT-613** **Role-based assignment:** assigned groups act as roles. Across roles the routing is **Parallel** or **Sequential**. Within each role it is **Parallel** or **Voting**, and the quorum applies per role or across all roles.
- **PLT-614** Changes to a phase's routing configuration shall apply only to **future** routes. Records already in a phase keep the configuration they entered with ([INV-071](21-invariants.md)).

### Assignment

- **PLT-620** Phase assignment modes: **any user/group** (default); **short list** (users/groups or a names list) used as a *default, editable* choice, a *forced* choice or a *limited* choice; **from field** (a names field on the record) used as *force all*, *select among* or *default plus any*; or **by rule or script**. Groups may be expanded into their **active** members.
- **PLT-621** The candidate assignees and notification recipients may be **filtered by the record's locations** (and optionally its additional security), so that only people who can access the record are offered ([INV-080](21-invariants.md)).
- **PLT-622** If the configured assignment list resolves to nobody, the system shall block the route and say why, unless the phase allows falling back to the full directory. It shall never assign a record to nobody ([INV-072](21-invariants.md)).
- **PLT-623** Inactive users shall never be assignable ([INV-073](21-invariants.md)).

### Due dates

- **PLT-630** On entry to a Normal phase, the system shall calculate a **due date** from: a fixed number of days (≥ 0, where 0 means today), a date field on the record (optionally falling back to the fixed number), or a rule or script. **Weekends and holidays from the applicable calendar shall be skipped** ([ADM §5.3](05-administration.md)).
- **PLT-631** Every route into a Normal phase shall end up with a valid due date. If none can be calculated and the assigner can't enter one, the route shall be blocked with a configuration error ([INV-074](21-invariants.md)).
- **PLT-632** Phases may let the assigner change the due date. They may also let the assignee request a **due-date extension or reassignment** through a built-in section. Each request gets approved or rejected and is kept in history.

### Activity gates and auto-routing

- **PLT-640** A phase may list activity types that **may** be created, that **must be created** (at least one of each) before routing out, and that **must be completed** (every one created, with voided counting as complete) before routing out. A route that fails a gate shall be blocked, and the dialog shall say which gate failed ([INV-075](21-invariants.md)).
- **PLT-641** A phase may **auto-route** to a set target phase once its must-complete activities are done. The target must have a resolvable assignment and due date.
- **PLT-642** A phase may let users **create activities or dual-linked records even if they can't edit** the parent record.
- **PLT-643** **Activity creator** subforms shall automatically create one activity per subform row when the parent enters the configured phase. Mapped fields are copied, and each activity is assigned, given a due date and linked back to its row.

### System-initiated routes

- **PLT-650** Escalations, scheduled tasks and integrations shall route records under a dedicated, non-interactive **system identity**. That identity can't log in, and its actions are clearly attributed in the audit trail as system actions, together with the rule that triggered them ([INV-051](21-invariants.md)).
- **PLT-651** Escalation routes (*send to phase*) shall skip activity gates only if the escalation is configured to. Unlike Reliance, Vellum shall **not** skip required-field or *prevent null* validation on escalation routes by default. Skipping validation must be configured explicitly per escalation, and each skip is recorded in the audit trail. [SAFETY]

## 2.7 Links, activities and inheritance

- **PLT-701** **Link fields** shall link records within and across applications. Links may be single- or multi-valued, and may be added by paste (from allowed forms) or by picking from a lookup view.
- **PLT-702** Link modes: **one-way** (default); **always dual** (the target automatically gets a back-link in a configured field, and removing one side removes the other); **optional dual** (the user confirms each time) ([INV-090](21-invariants.md)).
- **PLT-703** A link may be configured to always open the **latest effective revision** of a revision-controlled target.
- **PLT-704** **Hierarchical links** shall show linked records as an expandable tree of their own links, limited to the same parent form.
- **PLT-705** Named users may be allowed to change link fields while the record is in read mode. Each such change is audited.
- **PLT-706** **Link descriptions** (the text shown for a linked record) shall come from a per-form template, with variants for *open*, *awaiting release*, *completed*, *voided* and *other* states. They are shown in the viewer's locale and time zone. Default: *"⟨Title⟩, ⟨Workflow⟩ ⟨Number⟩ [⟨Revision⟩] (⟨Phase⟩, due ⟨Due date⟩)"*.
- **PLT-707** **Inheritance** shall copy mapped field values from a source record (parent or linked record) into a new record when it is created ([DSN-450](03-designer.md)).
- **PLT-708** A link field may be filled automatically by a *create record* action. The created record then shows up in the link field.
- **PLT-709** **Lookup subforms** shall fill subform rows from a looked-up record or a query, triggered by a key field. They refresh automatically, manually or conditionally. In hierarchical mode, they show one editable row per linked record with read-only nested rows.
- **PLT-710** **Read-only subforms** shall show rows from a query keyed to the current record (for example *related CAPAs*). New records shall show their data right after the first save, without a manual refresh. *(This is better than Reliance, which needs a manual refresh.)*

## 2.8 Attachments, renditions and PDF output

- **PLT-801** Attachment fields shall support multi-file upload (drag and drop), rename, copy link, download, multi-select **download all** (several files as .zip) and delete (with confirmation). Each file shall show name, size, upload user and last-updated time.
- **PLT-802** PDFs and common image and Office formats shall preview in the browser.
- **PLT-803** Files shall be scanned for malware before they are available ([NFR-038](22-non-functional.md)). Files shall be stored content-addressed with a SHA-256 checksum, and integrity shall be verified on read ([INV-053](21-invariants.md)).
- **PLT-804** **PDF conversion** may be configured per attachment field and phase: convert on save in the phase or on routing out of it. Naming: `Report.docx` becomes `Report_docx.pdf`, a file with no extension gets `.pdf` appended, and an existing `.pdf` is unchanged. On a new revision, *edit all fields* or reassign, only attachments that changed shall be converted again.
- **PLT-805** PDF conversion options: Word tracked-changes markup (*All*, *Simple*, *None*; default *All*); PDF security (prevent copying, printing, editing, or all); **appended page** (from a template, with body and footer text calculated from the record, for example a signature page); **watermark** (text from a rule or script, with font, size, opacity, color, angle and position set in System Preferences). Existing PDF uploads may also be watermarked, which creates a separate `_WM` copy and keeps the original.
- **PLT-806** A phase may show **only PDF renditions** of selected fields in read mode.
- **PLT-807** If a conversion fails (corrupt file, unsupported format, timeout), the failure shall be logged, the user shall be notified, and routing shall be **blocked** if a rendition is required for the target phase. The system shall never route a controlled document onward without its required rendition ([INV-054](21-invariants.md)). [SAFETY]
- **PLT-808** **Capture as PDF:** a form may capture the whole record (all tabs) as a PDF into an attachment field, either on routing into configured phases or on demand. The capture may include linked activities one level deep (only those the user may see) and existing PDF attachments. The file name comes from a rule. The system shall stop a capture from including its own target field.
- **PLT-809** Users shall be able to **print** a record to PDF. Fields marked *ignore in print* are left out. Printing a record together with its related records and attachments shall be supported where enabled ([DOC-083](10-document-control.md)).

## 2.9 Concurrency and locking

- **PLT-901** Opening a record for edit shall take an **exclusive edit lock**. Other users shall see it read-only, with the lock holder's name and when the lock was taken ([INV-091](21-invariants.md)).
- **PLT-902** Locks shall be released on save & close, route, cancel, session end and lock timeout (configurable, default 30 minutes idle). The editor shall be warned before their lock times out.
- **PLT-903** Users with edit rights may **unlock** a record locked by someone else, after confirming that the holder's unsaved changes will be lost. Unlock shall be refused while the record is mid-save, mid-route or mid-delete. Only administration managers may unlock profile (non-workflow) records.
- **PLT-904** A user whose lock was broken shall be told when they next try to save or route, shall be moved to read mode, and shall not be able to save over the newer state. Their unsaved input should be offered as a downloadable draft so nothing is lost silently.
- **PLT-905** All writes shall use **optimistic version checks** in addition to locks, so that API, import and background writes can never silently overwrite a newer version ([INV-092](21-invariants.md)).

## 2.10 Audit trail, comments, field history and phase tracking

- **PLT-1001** [P11] The platform shall keep a **system audit trail** for every record in every application. It shall always be on and can't be turned off by configuration. It records create, read of a controlled record (where configured), every field change (old value, new value), every route (from, to, assignees, due date), every signature, attachment add/replace/remove (with checksums), link changes, security changes, lock and unlock, print and export of controlled content, void, delete, archive and restore. Each entry carries user, on-behalf-of (delegation), UTC timestamp, client IP or source (UI/API/import/system rule), and reason or comment ([INV-050](21-invariants.md)).
- **PLT-1002** For each form, the **audit display format** shall be configurable: *Comment history* (comments and routes), *Audit log* (adds a field-history view) or *Force authentication* (adds e-signature re-authentication on save and/or route, [SEC §4.6](04-security.md)). A *None* setting only hides the user-facing history sections. The system audit trail (PLT-1001) is still captured. *(Reliance's "None" stops capture entirely. Vellum deliberately doesn't allow that.)*
- **PLT-1003** **Field history** shall show, for each field, every value change with user, time, phase and whether it was signed. Fields may be included in or excluded from the *display*, but the underlying audit capture is never affected.
- **PLT-1004** **Phase tracking** shall list every phase entry and exit: phase, assignee(s), assigned by, assigned on, due date, completed by, completed on, action (forward, back, void, resend, reassign, escalated, delegated) and comment.
- **PLT-1005** Each record shall have an **email log** of every notification sent about it (recipients, subject, time, delivery status). Authorized users can open it from the record.
- **PLT-1006** Audit trail entries shall be **append-only** and tamper-evident (hash-chained per record), and they shall be kept at least as long as the record ([INV-050](21-invariants.md), [INV-055](21-invariants.md)).
- **PLT-1007** Authorized reviewers shall be able to search, filter and **export** a record's audit trail (and audit trails across records) in human-readable form (PDF) and machine-readable form (CSV/JSON), for inspections (21 CFR 11.10(b), (e)).

## 2.11 Views

- **PLT-1101** Each application shall have a **view navigator** that lists views grouped by category (sorted alphabetically, so numeric prefixes can set the order), plus the system **My Assignments** view (type, phase, number, due date, title). Applications may hide My Assignments.
- **PLT-1102** Users shall be able to copy any view into **My Views**, with an optional rename. Copies also appear under Favorites › Views.
- **PLT-1103** Views shall support: reload without losing sort or filter; multi-column sort (with modifier-click); per-column value filters; additional preset filters; expand/collapse row details; column resizing; pagination (default 25 per page) with *show all pages* for exact totals; opening attachments straight from a row; and export to **PDF** and **Excel** (subject to permissions). Link and color columns can't be sorted or filtered.
- **PLT-1104** View **bulk actions** on selected records (each permission-checked and audited): *change field value*, *copy links*, *export*, *new record*, *reassign*, *re-route* (administrators) and *route*. Bulk actions shall run each record's normal validation and report per-record success or failure. They shall never partly apply a change to a single record ([INV-093](21-invariants.md)).
- **PLT-1105** Views shall be row-filtered by the user's access, and optionally by location scope (8 modes over the location hierarchy: primary/all locations, with or without lower and/or higher levels) and by additional security ([SEC §4.5](04-security.md)).
- **PLT-1106** **Calendar views** shall show records on a calendar by a date field. **Embedded views** shall act as lookups for dialog, link and lookup fields, hidden from the navigator.
- **PLT-1107** A view may join a **secondary form** through a link field to show columns from linked records.
- **PLT-1108** View queries shall time out at a configurable limit (default 5 minutes) with a clear message. They shall never degrade other users' service ([NFR-013](22-non-functional.md)).

## 2.12 Notifications and escalations

- **PLT-1201** Routing into a phase shall send an **assignment email** to each new assignee, using the **target phase's** template (subject and body built from expressions or scripts, HTML allowed). The email shall link straight to the record. Phases may turn assignment emails off.
- **PLT-1202** Phases may notify configured users, names fields or rule-calculated recipients **on entering** and **on exiting** (not for terminal phases), using the notification template. The assigner may add recipients in the phase dialog, unless the list is locked.
- **PLT-1203** **Send Notification** shall be available from records, view rows, homepage cards and search results. It has recipients (filtered by the record's location/security if configured), subject (defaulting to the record description) and body, and it writes to the record's email log.
- **PLT-1204** Resend actions shall have their own assignment and notification templates.
- **PLT-1205** Users shall be able to choose **digest** delivery (immediate, hourly or daily summary) for notifications other than assignments. Assignment emails and safety-critical alerts ([SAF-020](20-safety.md)) shall always be sent immediately. *(Vellum extension.)*
- **PLT-1206** Mobile **push notifications** for assignments and notifications shall be configurable per phase (title and body). They go only to users who opted in.
- **PLT-1207** **Escalations** shall watch a date field on a form or subform for records selected by a view, query or data source, with an optional extra condition rule. Each escalation has up to **three levels**. Each level has an enabled flag, an offset in days from the date (negative is before, 0 is on the day, positive is after) and an action: **notify** (To and CC recipients from fields and/or fixed users and groups, with their own subject and body; optional *notify reports-to* up 1–10 management levels; frequency *once*, *every day*, or *every X days for Y days*) or **send to phase** (route to the current phase's escalation phase as the system identity).
- **PLT-1208** Escalation evaluation, per run and per record: levels are checked from **highest to lowest**. The first enabled level whose *date + offset ≤ today* fires, and **at most one level acts per record per run**. Dates are evaluated in the system time zone. Records that no longer match the selection stop escalating. Inactive people in the reports-to chain are skipped.
- **PLT-1209** Escalation settings shall be validated on save: offsets must move monotonically toward the date as the level rises, frequencies must be non-negative integers with *every ≤ for*, and a query may not write ([INV-120](21-invariants.md)).
- **PLT-1210** Escalations shall run on a configurable schedule (task profile) and can also be run on demand (*Run now*). Every run shall log its start, end, the records acted on and any errors.
- **PLT-1211** Unlike Reliance, Vellum offsets and frequencies may be expressed in **business days** or calendar days (configurable). Missed runs (for example over weekends or during outages) shall **catch up** so no escalation level is skipped ([SAF-030](20-safety.md)).

## 2.13 Delegation

- **PLT-1301** Users shall be able to request a **delegation** of all their assignments to another user for a date range. Workflow: *New → Approval (by the delegate, who can accept, send back or void) → Active → Completed*.
- **PLT-1302** While a delegation is active, the delegate shall be able to edit, save, route, reassign and void the requester's assignments, **but only where the delegate's own access allows**. The requester keeps their own access.
- **PLT-1303** Requester and delegate shall not be able to edit the same record at the same time (normal locking).
- **PLT-1304** Delegation shall end at the first of: the end date (automatically), manual completion, or loss of the delegate's access to a given record.
- **PLT-1305** Every action a delegate takes shall be recorded **both** in the record's audit trail (as *delegate on behalf of requester*) and in the delegation's own log ([INV-051](21-invariants.md)). [P11]
- **PLT-1306** [P11] Electronic signatures shall never be delegated. A delegate signs with their **own** credentials, and the signature manifestation shows the delegate's name with "on behalf of ⟨requester⟩" ([INV-112](21-invariants.md)).
- **PLT-1307** The homepage shall show *Delegated to me* and *Requested by me* delegations.

## 2.14 Homepage and personal workspace

- **PLT-1401** The homepage shall be put together from **homepage profiles** assigned by administrators ([ADM §5.6](05-administration.md)). A user with several profiles can switch between homepages.
- **PLT-1402** Homepage widgets:
  - **Calendar:** monthly view that marks days with overdue or upcoming items. Hovering a day lists its items with a *view all* link.
  - **Date range:** presets Today, Next 7 days (default), Next 14 days and Next 30 days, or a custom range. *Include overdue* is selected automatically when overdue items exist. Shows an overdue count.
  - **Assignments:** cards for all of the user's assignments across applications, overdue first, filterable by overdue or open. Each card shows type and number, title, application, due date and phase. Card actions: edit, reassign (if allowed) and send notification. Paginated, default 10.
  - **Trainings:** training assignments with course, due date and status.
  - **Delegations:** *delegated to me* and *requested by me*.
  - **My Charts:** favorite charts from the user's homepage profile ([RPT §7.2](07-reporting.md)).
  - **My Views / favorite views.**
- **PLT-1403** **View all** shall open a dialog that groups items by application in accordion sections. Each section has overdue and open quick filters, a bulk *All/Open/Overdue* filter and expand-all.
- **PLT-1404** Users shall be able to set preferences: language, time zone, date format, default application, homepage, notification delivery, and keyboard shortcuts ([NFR-053](22-non-functional.md)).

## 2.15 Global search

- **PLT-1501** Global search shall be switchable per tenant. When on, it searches across applications: text, text area, rich text, names and keyword fields; attachment content (PDF, Office, RTF, TXT, MSG, EML); comment history; e-signature sections (excluding dates); phase tracking; additional document security; revision history; and decision-tree answers.
- **PLT-1502** By default, search shall cover **completed/effective records and profiles**. Users with access to drafts may also search pending records if the tenant enables it. *(Vellum extension.)*
- **PLT-1503** Matching shall be word-prefix by default, and support quoted phrases and field filters. Results shall be grouped by application (up to 100 per application, 10 per page) and filterable by date (presets 30/60/90 days, 6/12 months, or custom) and application.
- **PLT-1504** [SAFETY] Search results shall be **fully security-trimmed**: standard access, location security, additional security and record-level security all apply. A user shall never see a title, number or snippet of a record they can't open ([INV-081](21-invariants.md)). *(Fixes a Reliance behavior, where records restricted only by additional or location security still appear in results.)*
- **PLT-1505** The search page may be minimized while results load in the background.
- **PLT-1506** Search shall exclude archive applications by default (with an *include archive* filter for authorized users), administration applications, and password-protected files. It shall say when attachment text was too large to index fully.
- **PLT-1507** **AI-powered semantic search** may be enabled as well ([INT §6.10](06-integrations.md)). [AI]

## 2.16 Calendar integration (from records)

- **PLT-1601** A form may map chair, location, required and optional attendees, start and end, subject and body to calendar invitation fields. It then offers a *Send invitation* action that sends an invitation through Microsoft 365/Outlook or Google Calendar. The invitation includes a link back to the record. Responses go to the organizer, and later edits update the event.

## 2.17 Mobile

- **PLT-1701** The system shall provide mobile apps (iOS and Android) and a mobile web experience for forms that have a **mobile layout**. A mobile layout is a tabs → grid → fields arrangement generated from, and limited to, the web form. The form's settings and security are shared.
- **PLT-1702** Mobile users shall be able to view assignments, open, edit and route records, create records (where the form allows it), capture photos, video and audio as attachments, scan barcodes and QR codes into fields, record GPS location, and sign electronically [P11].
- **PLT-1703** **Offline** use shall be supported for configured forms: a *local view* is cached on the device, plus at least **4 lookup fields with up to 3,000 options each** (parity minimum; Vellum should allow more), plus extra fields needed by rules. Records captured offline shall sync when the device reconnects, with server-side validation. Conflicts are resolved by the normal locking and version rules, and the user is told about any conflict ([INV-092](21-invariants.md)).
- **PLT-1704** Offline data on the device shall be encrypted and erasable remotely. Offline sessions shall still re-authenticate for e-signatures when the device is back online, or use a device-bound credential that meets [SEC §4.6](04-security.md).
- **PLT-1705** Mobile push notifications shall follow PLT-1206.

## 2.18 Keyboard and accessibility behavior

- **PLT-1801** Standard keys: Tab moves between fields (and opens drop-downs); Enter in a date field fills in today; Enter opens multi-value dialogs (arrow keys move, Space selects); Space toggles radio buttons and checkboxes; arrow keys move between views in the navigator and between rows in views.
- **PLT-1802** Configured shortcuts (modifier + key) on actions, sections, subforms and phases shall work as described in [DSN-130](03-designer.md).

## 2.19 Administrative overrides

- **PLT-1901** Authorized administrators and application managers shall be able to **Edit All Fields** on a record in any phase, including read-only ones but excluding protected fields, and to run bulk **Change Field Value**. Both need a **reason**, are e-signed when the form forces authentication, and are recorded as administrative overrides in the audit trail ([INV-050](21-invariants.md)).
- **PLT-1902** Administrators shall be able to **Global Route** and **Global Re-route** records (one or many) to any phase of their workflow, with assignees and due dates that follow the target phase's rules. A reason is required, and every action is recorded as an override.
- **PLT-1903** Overrides shall never be able to change an executed electronic signature, an audit trail entry, or an archived revision's content ([INV-052](21-invariants.md)).
