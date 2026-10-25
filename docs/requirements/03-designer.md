# 3. Designer, Scripting and Configuration Management

Vellum is a **configurable platform**. Every packaged application in sections 10–19 is built from the same configuration objects that customers can create and change: applications, forms, tabs, sections, fields, subforms, workflows, phases, actions, views, columns, data sources, escalations, templates and profiles.

This section defines that metamodel and the tools around it: the visual designers, the rule builder, **Vellum Script**, **Vellum Query**, design mode, the configuration audit trail and configuration promotion.

**Parity sources:** Reliance help › Designer (all pages), ETQScript and Advanced settings pages, Escalations, Office Integration Templates, Configuration Promotion, Administrator › Reliance Engine, Audit Trail for Configuration Changes.

## 3.1 Designer access and design mode

- **DSN-001** Designer rights shall be granted **per application** (*Application Designers*). Creating a new application shall need the separate *Application Creator* permission. Configuring tenant-wide profiles shall need administrator rights.
- **DSN-002** The designer shall open an application in **design mode** from the All Applications card (and its paired archive application, with a toggle) without affecting live users.
- **DSN-003** Changes made in design mode shall be saved as a **draft configuration version**. They take effect for users only when **published**. Publishing shall run configuration validation (DSN-1201). It may require an approved Configuration Change Request ([REL-007](19-projects-tasks.md)) when the tenant enables change control. *(Reliance doesn't document a publish step. Vellum adds one so that untested changes can't reach production records.)* [SAFETY]
- **DSN-004** Each configuration component (form, workflow, view, and so on) shall be **locked** to one designer while it is being edited. The lock shall show the holder and expire after a configurable idle timeout. Administrators may break it.
- **DSN-005** Core system applications (the platform engine and the Administration Center) shall not be editable in design mode. Partly configurable system applications (for example reporting) shall expose only their allowed properties.
- **DSN-006** The designer home for an application shall list its components by type (main forms, activity forms, workflows, views, data sources, escalations, templates, actions) with counts. It shall support *new component*, *copy view*, *edit application settings*, *generate mobile layout* and adding the designer to favorites.
- **DSN-007** Every configuration object shall have a display name, an auto-generated **design name** (editable, unique within its type and application, never all digits) and an immutable system ID ([INV-130](21-invariants.md)). Scripts and queries shall refer to objects by design name.

## 3.2 Applications, forms and workflows (structure)

### Application settings

- **DSN-101** Application settings: display name, description, show on homepage, icon, default view (must be visible and enabled), hide *My Assignments*, favorite (quick-create) form and workflow, locations (only administrators of those locations may edit the setting), toolbar actions, and **protect from packaged-template upgrade** ([DSN §3.9](#39-packaged-applications-and-upgrades)).
- **DSN-102** Application security: Application Managers, Designers, Authors and Readers (users/groups); default access for everyone else (*No access, Reader, Author, Manager*); *Who can delete records*; optional anonymous read-only access; access to embedded views without application access; authors who can reassign **any** assignment; and authors who can reassign **their own** assignments. On a new application, these default to the creator.
- **DSN-103** Changes to application security shall apply to existing records as soon as they are published.
- **DSN-104** Application-level *Hide When* rules may hide system bulk actions (change field value, copy links, export PDF, export Excel, new record, reassign, re-route, route) based on conditions. There is at most one rule per action ([INV-131](21-invariants.md)). Hiding is a UI convenience only. Every action is still authorized on the server ([INV-082](21-invariants.md)).

### Form settings

- **DSN-110** Form settings: display name, disabled, activity form (with read-only parent forms), non-workflow, read-only (shares a table with an editable form), hidden from *New*, dual-linking forms, document templates, validation triggers (DSN-501), numbering, audit display format and e-signature settings ([SEC §4.6](04-security.md)), capture-as-PDF, document title expression, link description template, toolbar and inline actions, top-bar action *Hide When* rules (at most one per action), phases that open in read mode, the default tab on open, inheritance settings and calendar integration.
- **DSN-111** Form access settings: allowable authors (creation; empty means all application authors; managers can always create; readers never can), default record managers, editors and readers (copied into each new record's security section), submitter (depositor) intake ([SEC §4.8](04-security.md)), and location/additional-security rules for reading when the record's readers list is empty.
- **DSN-112** A form with one or more workflows can't be switched to non-workflow. An activity form can't be non-workflow, read-only or hidden from *New* ([INV-132](21-invariants.md)).
- **DSN-113** Saving a form shall **deploy** its storage schema automatically. Vellum shall manage the physical storage. Designers never name tables or columns, and every rule about storage identifiers is enforced by the platform.

### Workflow settings

- **DSN-115** Workflow settings: display name (required), disabled, document templates, locations, phase tracker display, numbering overrides, picker rules for the system people fields (owners, reviewers, electronic and hard-copy distribution: *any*, *limit to list*, *default and allow*, *force*), revision control (format, change request form (required when revision control is on), who may raise change requests, activity links to copy onto new revisions, obsolete request form), review frequency, review record form and users, effective date delay, workflow users (who may create this type), and location/additional-security read rules.
- **DSN-116** A disabled workflow can't be linked to a form. Disabling a form's last workflow hides the form from *New* ([INV-133](21-invariants.md)).

### Computed workflow properties

- **DSN-120** These workflow and form properties shall accept a **static value, a rule (expression builder) or a script**: number prefix, number suffix, review frequency, effective date delay, phase due-date duration, watermark text, publishing folder, document title, link description, and email subjects and bodies.

### Keyboard shortcuts

- **DSN-130** Designers may give actions, sections, subforms and phases a **keyboard shortcut**: optional Ctrl/Alt/Shift plus one key (0–9, A–Z, F1–F12). F-keys take no modifiers, and any other key needs at least one. A shortcut must be **unique** across the form's actions, sections and subforms and every phase of the form's workflows ([INV-134](21-invariants.md)). Shortcuts aren't allowed on inner or system sections and subforms, on subform actions, or on obsolete and archive phases.

## 3.3 Layout: tabs, sections and subforms

- **DSN-301** The **form designer** shall be visual and drag-and-drop. It shall support tabs (add, reorder), rows, sections (including nested inner sections), subforms (including nested inner subforms) and fields, on a 12-column grid in sections and a 10-column grid in subforms. Element widths shall be limited by the free columns in the row. Attachments, links, rich text, external links and system sections each need a row of their own.
- **DSN-302** Elements may be **detached** from the layout without being deleted, and re-attached later.
- **DSN-303** Section settings: display name, color, optional (shown or hidden by the user), shortcut, *editable when* rule and *read-only when* rule.
- **DSN-304** **Visibility rules** shall be reusable named objects with an *edit when* condition and a *read-only when* condition. Each element can have at most one rule. Editing a shared rule shall show every element that uses it before saving. Packaged (core) rules are read-only. Elements inside system sections can't have rules.
- **DSN-305** Vellum shall use one explicit, documented **visibility matrix** for workflow forms. *(Reliance's equivalent table isn't publicly legible, so Vellum defines its own.)*

  | Phase setting | Section rule result | Effective state |
  |---|---|---|
  | Editable in phase | none / *edit when* true | **Editable** |
  | Editable in phase | *read-only when* true | **Read-only** |
  | Read-only in phase | any | **Read-only** |
  | Not listed in phase | *edit when* true | **Hidden** (phase listing wins) |
  | Not listed in phase | none | **Hidden** |
  | Any | record in a terminal phase | **Read-only** at most |

  A section can't appear in both the editable and read-only lists of the same phase ([INV-135](21-invariants.md)). If both rules on a non-workflow form are true, *read-only* wins.
- **DSN-306** Subform settings: display name, required, collapse rows by default, maximum rows (≥ 1), add-row label, row title, hide add, hide delete, color, subform actions, shortcut, one row at a time **or** pagination (≥ 1 per page, default 10, newest-first option, or no pagination; these options are mutually exclusive), and searchable fields.
- **DSN-307** Subform modes: **plain**, **activity creator** (field mapping to an activity form, with an automatic system link back to each row; the target activity workflow needs at least two normal phases and one completed phase, with an assigned second phase and a due-date duration), **lookup** (by query or by form-field mapping between fields of the same type, refreshed automatically, manually or conditionally, optionally filled from a hierarchical link), **read-only** (filled by a query keyed on the parent record) and **decision tree** (filled from a decision-tree profile, [ADM §5.6](05-administration.md)) ([INV-136](21-invariants.md)).
- **DSN-308** A subform's parent is either a form or an outer subform, never both.

## 3.4 Fields

- **DSN-401** Field settings common to all types: display name, required, *show as required* (visual only; can't be combined with required), protect from administrative updates, placeholder (not for combo, radio or list box), leave out of print, help link, display format (PLT-402), default value, correction rule, validation, unique values, prevent null values and *exclude from search*.
- **DSN-402** Field types and their own properties:

  | Type | Properties |
  |---|---|
  | Text | color-picker mode, max length, pattern/format mask |
  | Text area / rich text | max length (rich text: sanitized HTML, embedded links) |
  | Text prompt | static text, text and background colors |
  | Number | decimal places (0–30), trim trailing zeros, min/max, unique |
  | Currency | currency code (sets decimals) or explicit decimals, trim zeros |
  | Date / time / date-time | style (full, long, medium, short), time-zone mode (local with optional label, fixed with a default zone), show UTC, 24-hour |
  | Keyword (drop-down, checkbox, radio, list box) | keyword list, multi-value (not radio), sort, max options shown (default 50), refresh options on record refresh, refresh record on change, filter rule or query |
  | Keyword dialog | list, hierarchical drill-down, or view lookup (embedded view + column), multi-value, show selected items as links |
  | Names | multi-value; source: users and groups, users only, groups only, names list (optionally expanding groups to members), or a custom view |
  | Attachment | template sync direction (record→file, file→record, both), inline images, allowed file types, max size, PDF rendition rules (per phase) |
  | Link | multi-value, open latest revision, editable in read mode by named users, hierarchical, add by paste (allowed forms) or by lookup view, auto-filled by an action, one-way / always dual / optional dual (with target fields) |
  | External link | multi-value, URL scheme allow-list (default https only) |
  | **Risk matrix** *(Vellum)* | matrix profile ([RSK §16.1](16-risk.md)); stores the severity, likelihood and (optional) detection inputs and the calculated score and level |
  | **Signature** *(Vellum)* | the e-signature meaning to capture; links the signature to the phase and the record version |
  | System field | label, placeholder, keyword or names list, required, prevent null, sort, refresh on change |

- **DSN-403** A field's type is fixed once it has stored data. Changing type needs a migration: a new field, a data copy with conversion rules and a validation report, all recorded in the configuration audit trail.
- **DSN-404** A single-value checkbox shall use the system Yes/No keyword list.
- **DSN-410** Validation shall be authored as **rules** (expression builder) or **scripts**. A rule may also declare the **phases in which the field is required** when sending forward. Expression-builder rules by type:
  - **Text:** equals, does not equal, contains, does not contain, starts with, ends with, length =, length <, length >.
  - **Number:** =, ≠, <, >, each against a constant or another number field.
  - **Date/date-time:** =, ≠, before or after another field, in the past, in the future, today.
  - **Time:** =, ≠.

  Each rule has its own error message.
- **DSN-411** A field-level rule may read only its own field's value and the record context. Cross-field validation belongs in form-level rules (on save).
- **DSN-450** **Inheritance settings** shall map fields from a source form to a target form, for creating activities, linked records, records from a lookup, and records of the next meeting, revision or restore. Mapped fields must have compatible types ([INV-137](21-invariants.md)).

## 3.5 Rules (no-code expression builder)

- **DSN-501** The expression builder shall let non-programmers author conditions (field operator value, combined with AND/OR and nested groups) and **templates** (text with inserted field values). Templates are used for titles, link descriptions, email subjects and bodies, number prefixes and suffixes, and escalation messages. Field types that can't be shown meaningfully (password, attachment, raw link, checkbox in some templates) are excluded from insertion.
- **DSN-502** Where both a rule and a static list are configured (for example routing targets), the effective result is their **union**. The designer UI shall show the effective result for a sample record ([DSN-610](#36-workflows-and-phases)).
- **DSN-503** Switching an authoring mode (for example from rule to script) shall warn before any content is discarded.

## 3.6 Workflows and phases

- **DSN-601** The **workflow designer** shall be visual. The first phase must be a starting phase. Every other phase type becomes available after that, and the *archive* type becomes available once a *completed* phase exists. A workflow may have several start, completed and void phases. Invalid drops shall be refused and shown as invalid. Deleting a phase that has records in it shall be blocked until those records are moved.
- **DSN-602** Phase settings, **basic**: display name (required), type (fixed), editable sections, read-only sections, active actions, active tab, assignment mode (PLT-620), assignee filter, due-date rule, whether the assigner may change the due date, whether the assignee may request an extension, activities that may be created / must be created / must be completed, auto-route target, non-editors may create activities, non-editors may create dual-linked records, PDF conversion and read-mode PDF fields, tracked-changes markup, PDF security, appended page, watermark, *don't copy PDFs on change request* (completed phases), SharePoint publishing (fields, library, folder rule, metadata mapping), keyboard shortcut.
- **DSN-603** Phase settings, **lifecycle** (completed, archive and obsolete phases): archiving phase, obsolete phase, *who can obsolete* (empty means only administrators), *who can restore* (empty means only administrators), archive application.
- **DSN-604** Phase settings, **routing**: starting phase, turn assignment email off, allow resend (with an editable due date), keep open when routing to oneself, routing type (parallel, sequential, voting by count or percent), role-based assignment (across roles parallel or sequential; within roles parallel or voting; quorum per role or overall), forward / special forward / backward / void targets (each a list, a rule and/or a script), *who can use special forward*, *who can void*, escalation phase.
- **DSN-605** Phase settings, **notifications**: assignment, notification, resend-assignment and resend-notification templates (subject and body), notify-on-enter and notify-on-exit lists (users, fields, rule or script), notification recipient filter (by location / additional security), lock the notify list, and mobile push titles and bodies.
- **DSN-606** An **electronic signature requirement** shall be configurable per phase transition (forward, back, void), per action and per save, with the signature meaning for each ([SEC §4.6](04-security.md)). *(Reliance sets this at form level and filters by phase. Vellum supports both levels, and the phase-level setting overrides the form-level one.)*
- **DSN-610** Forward, backward, special-forward and void targets shall each be worked out as the **union** of selected values, matching rule outcomes, and the phases a script returns. Forward targets must be *normal* or *completed* phases. A rule set may define a fallback phase for when no rule matches.
- **DSN-611** Auto-route targets must have a resolvable assignment and due date. The designer shall reject an auto-route target that doesn't ([INV-074](21-invariants.md)).
- **DSN-612** The workflow designer shall show a read-only **routing table** (every phase with its forward, back, special and void targets) and shall flag phases that can't be reached and phases with no way out (dead ends).

## 3.7 Actions, views, columns and data sources

- **DSN-701** Actions: display name, menu category, usable in forms and/or applications, type (**create activity** limited to this application's activity workflows; **create record** in any application's enabled workflows; **open dialog**; **script**), shortcut, show as icon (inline only), icons, *Hide When* rule, and *require e-signature when clicked*.
- **DSN-702** The **view designer** shall be visual. Fields are dragged into columns, columns can be reordered, a view can be copied as a new view, and embedded views can be created.
- **DSN-703** View settings: display name, navigator category, disabled, hidden from navigator, embedded, rows can't be opened, page size (default 25), fields whose attachments open from the view, locations, default sort columns and direction, default search column (embedded views), location filter scope (8 modes), additional-security filter, **view users** (empty means everyone, and managers always see every view), secondary form (application, form, link), *before open* and *after open* scripts, and the data source.
- **DSN-704** Column settings: display name, sortable (default yes; not for link columns), show color, hide rows where empty, width, date part (default date only), date style (default medium), decimals, trim zeros, link column, localize displayed keyword, workflow and phase names, source field or query column, and parent view(s).
- **DSN-705** **Data sources** feed views. They are either a form's own records or a **Vellum Query** (DSN §3.11) that can combine records across applications, including calculated columns (for example year, quarter and month from a date). A view can't exist without a data source ([INV-138](21-invariants.md)).
- **DSN-706** Views may filter on the **current user** (for example *assigned to me*, *authored by me*, *my department*) through built-in parameters.

## 3.8 Configuration promotion

- **DSN-801** The system shall support at least three environments per tenant: **Development, Test/Validation and Production**. Configuration shall move between them only as **promotion packages**.
- **DSN-802** Creating a package shall be a wizard:
  1. Pick applications, with their related profiles (templates, localization, SharePoint profiles and mappings, mail profile, decision trees, names lists, keyword lists, connection profiles, location profiles, task profiles).
  2. Pick shared configuration (group, room, homepage, user, script library, web-service, facility, task and localization profiles; SSO settings).
  3. Optional filters (modified date range; exclude specific items; add specific items).
  4. Review **warnings**: profiles without their dependencies, missing references, groups with more than 1,000 members, orphaned keyword lists and data sources.
  5. Review a **preview**.
  6. Generate the package and a summary report.

  Only items listed in the preview are included.
- **DSN-803** Packages shall be **signed** (content hash plus source environment identity) and versioned. Applying a package that has been tampered with shall be refused ([INV-140](21-invariants.md)).
- **DSN-804** Applying a package shall: validate it against the target **before** changing anything (dependencies present, no conflicting items, schema compatible); show a **diff** against the target; need explicit confirmation; take an automatic **pre-apply snapshot**; apply **atomically** (all or nothing); and produce a PDF report (who, when, source and target environment, package version, items changed, result). *(Reliance has no atomic apply and no automatic rollback point. Vellum requires both.)* [SAFETY]
- **DSN-805** Rolling back to the pre-apply snapshot shall be a single administrator action, recorded in the configuration audit trail.
- **DSN-806** Applying a package should not need logging out every user. Records already in flight keep the configuration version they started with where that matters (PLT-614). A tenant may still choose to apply during a maintenance window with user lock-out.
- **DSN-807** Localization in a package shall merge by default (add missing keys, update changed ones). The other options are *add missing only* and *don't include*.
- **DSN-808** Environment-specific settings (integration credentials, URLs, SSO metadata, collaboration profiles) shall be flagged as **environment-bound**. They are never overwritten by a promotion unless explicitly selected.

## 3.9 Packaged applications and upgrades

- **DSN-901** Vellum shall ship every application in sections 10–19 as a **packaged application template**, versioned on its own (for example *Document Control 2.0.0*).
- **DSN-902** Customers shall choose when to upgrade a packaged application. The upgrade shall show a three-way diff (old template, new template, customer configuration) and apply in a non-production environment first.
- **DSN-903** Applications or individual settings marked **protected from upgrade** shall never be overwritten by an upgrade.

## 3.10 Vellum Script

- **DSN-1001** Vellum shall provide **Vellum Script**, a sandboxed scripting language with Python-compatible syntax (so ETQScript logic is easy to port) for logic that rules can't express.
- **DSN-1002** **Hook points** (parity with the full Reliance hook catalog):
  - **Form:** on open, on refresh, on save (before commit; can block with a validation error), on tab switch, document title, link description, *Hide When* for top-bar actions.
  - **Field:** default, correction, validate, keyword filter parameters.
  - **Section:** editable when, read-only when.
  - **Subform:** conditional refresh, lookup parameters.
  - **Action:** *Hide When*, execute.
  - **Phase:** due-date duration, watermark, publishing folder, forward / backward / special-forward / void targets, *who can void*, *who can special forward*, assignment, notify-on-enter and notify-on-exit lists, all four email templates, activities available / required to create / required to complete.
  - **Workflow:** number prefix and suffix, effective date delay, review frequency.
  - **View:** before open (parameters), after open.
  - **Escalation:** condition, recipients, subject and body.
  - **Scheduled task:** execute.
  - **Application:** *Hide When* for bulk actions.
  - **Integration:** inbound and outbound mapping transforms ([INT §6.1](06-integrations.md)).
- **DSN-1003** Scripts shall get a documented object model: record, field, subform and subform row, phase, workflow, user (with locale and time zone), application, view, link, locale, and helpers for dates (business-day calendars included), strings (localized text) and lists. Keyword values shall be read and set by ID, with lookup helpers for labels.
- **DSN-1004** [SAFETY] **Sandbox:**
  - No access to the file system, network, operating system, process environment, reflection or dynamic code loading.
  - The only I/O is through the platform API.
  - Every API call is **authorization-checked** as the identity the hook runs under: the invoking user for interactive hooks, the system identity (with the script's declared grants) for escalations, tasks and integrations.
  - Strict CPU-time, memory, recursion and call-count limits apply, with defaults of 2 s CPU for interactive hooks and 60 s for tasks.

  A script that breaks a limit is stopped and its transaction rolled back ([INV-141](21-invariants.md)).
- **DSN-1005** **Side-effect classes:** predicate and template hooks (*Hide When*, *editable when*, titles, targets, recipients) shall be **read-only**, and the sandbox shall enforce that. Only on-save, action-execute, task, escalation and integration hooks may write. Every write a script makes shall go through normal validation and the audit trail, attributed to the user *via script ⟨design name⟩* ([INV-142](21-invariants.md)).
- **DSN-1006** Scripts may never read password or secret fields, credentials, or another tenant's data ([INV-143](21-invariants.md)).
- **DSN-1007** The script editor shall provide syntax highlighting, autocomplete over the object model, formatting, **validation** (syntax plus references to fields, phases and keyword IDs that exist), a test runner against a chosen sample record in non-production (dry run, no commit), and a library of **shared scripts** that hooks can import.
- **DSN-1008** Script-execution failures shall show users a safe, localized message (never a stack trace) and shall be logged in full for administrators, with record, hook, script version and error.
- **DSN-1009** Context sharing: hooks on the same record in one request shall share a context in a defined order (form on-open, then field defaults, then the others). This lets a value calculated in on-open be reused. Contexts are never shared between records or users.

## 3.11 Vellum Query

Every place Reliance accepts raw SQL (view data sources, keyword queries, lookup subforms, read-only subforms, link descriptions, escalation selection, scheduled-task selection) is served in Vellum by **Vellum Query**.

- **DSN-1101** Vellum Query shall be a **read-only**, declarative query language over Vellum's **logical model** (applications, forms, fields, subforms, links, phases, users, groups, locations, keyword lists). It shall support filters, joins along link fields, aggregates, grouping, calculated columns (date parts, CASE-style mapping, arithmetic) and sorting. It never exposes physical tables.
- **DSN-1102** [SAFETY] Queries shall be **parameterized only**: current user, the user's locations and groups, the current record, field values and values passed from scripts. String concatenation into queries shall be impossible by design ([INV-144](21-invariants.md)).
- **DSN-1103** [SAFETY] Queries shall run with **row-level security for the calling identity**, so a query can never return records the caller couldn't open. A query running as the system identity (escalations, tasks) shall be limited to the applications its configuration declares.
- **DSN-1104** Queries shall be validated on save (syntax, references, read-only) and capped at run time (default timeout 5 minutes for views and 60 s for interactive lookups, plus row limits). They shall be explainable: the designer shows the query plan and estimated cost.
- **DSN-1105** Queries may target a configured **external data connection** (read-only) for keyword lists and lookups ([INT §6.1](06-integrations.md)).

## 3.12 Configuration audit trail, versioning and validation

- **DSN-1201** Publishing (DSN-003) and applying a promotion shall run **configuration validation**:
  - references resolve;
  - shortcut uniqueness (DSN-130);
  - section-list disjointness (DSN-305);
  - every route into a normal phase can get an assignment and a due date;
  - auto-route targets are valid;
  - activity-creator prerequisites are met;
  - no phase can't be reached unless intended;
  - scripts and queries validate;
  - views have data sources;
  - escalation offsets and frequencies are valid;
  - every required e-signature has a meaning.

  Publishing shall be blocked on any error. Warnings may be accepted, and the acceptance is recorded.
- **DSN-1202** [P11] Every configuration change shall be recorded in an append-only **configuration audit trail**: object, before and after values (a structured diff), user, time, environment, configuration version, and the linked change request or promotion package ([INV-145](21-invariants.md)).
- **DSN-1203** Every published configuration shall be an immutable, numbered **configuration version**. Administrators shall be able to compare any two versions and roll back to an earlier one. A rollback is itself a new version.
- **DSN-1204** Every record shall store the configuration version current at each of its phase transitions, so auditors can reconstruct exactly which rules applied ([INV-146](21-invariants.md)).
- **DSN-1205** A **Platform Engine** administrative application shall list every configuration object by type: applications, forms, fields, sections, subforms, workflows, phases, actions, views, columns, data sources, visibility rules, scripts and escalations. Each is searchable and filterable by parent application, parent form, type and modified date.

## 3.13 Escalation and scheduled-task configuration

- **DSN-1301** Escalation settings: display name, application, form, optional subform, the **date field** watched, an optional condition (rule or script), the tab to open from the email link, the record source (view, data source or query), enabled flag, and three levels (each with enabled, offset, calendar or business days, action, frequency, To and CC recipients and templates, *notify reports-to* with 1–10 levels).
- **DSN-1302** Scheduled tasks (*task profiles*) shall have a schedule (cron-style or interval, in the system time zone), a target (escalation, packaged job or script), the identity they run as, enabled flag, last and next run, and run history ([ADM §5.8](05-administration.md)).

## 3.14 Document templates and mobile layouts

- **DSN-1401** Designers shall be able to assign **document templates** (Word, Excel, PowerPoint) to forms and workflows. Each template defines placeholders or custom properties mapped to record fields, plus a sync direction. Master and location-specific variants are managed in the Administration Center ([INT §6.4](06-integrations.md)).
- **DSN-1402** The **mobile designer** shall build a form's mobile layout from the web form's tabs, sections and fields (drag, drop, remove, re-add). It also configures mobile options: allow create, server view, offline local view, cached lookup fields, extra fields needed by rules, and a mobile script.
- **DSN-1403** Mobile scripts shall run in the same sandbox model as Vellum Script (DSN-1004), with no access to the device beyond the documented API (camera, barcode, location, with user consent).
