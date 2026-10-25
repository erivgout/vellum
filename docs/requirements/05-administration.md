# 5. Administration, Master Data and Profiles

Administration in Vellum happens in the **Administration Center**, a system application whose records are **profiles** (users, groups, locations, keyword lists and so on), plus a single **System Preferences** record. Every administrative object is a versioned, audited record. Changes go to the configuration audit trail ([DSN-1202](03-designer.md)), and promotable profiles move between environments through promotion ([DSN §3.8](03-designer.md)).

**Parity sources:** Reliance help › Administrator › Administration, Configurability, Profiles, Import, Special Access; Users and Groups; Localization; Task Profile; Reliance Engine.

## 5.1 System Preferences and environment personalization

- **ADM-101** System Preferences shall be one tenant-wide settings record, editable only by administrators. Each change is audited and needs a reason. Settings flagged as compliance-sensitive also need an e-signature ([SAF-010](20-safety.md)).
- **ADM-102** **Environment personalization:**
  - **Header logo:** up to 50 KB.
  - **Environment name.**
  - **Banner bar:** show or hide; show the environment name and/or free text; background and text colors.
  - **Login page:** cover photo (up to 300 KB), main logo, center logo (each up to 50 KB), an optional footer with logo, slogan and colors.
- **ADM-103** Non-production environments shall show a banner that **can't be turned off** ("TEST", "VALIDATION" and so on), so people can't confuse them with production ([SAF-012](20-safety.md)). *(Vellum extension.)*
- **ADM-104** **Display preferences:** the symbol shown for empty values in read mode (default "–"); how the location picker appears (*hierarchical drill-down* or *searchable dialog*; dialog is recommended for large hierarchies); the system identity's display name (default "System"); whether view column search runs *as you type* or *on Enter*.
- **ADM-105** **Routing action labels:** tenant-wide labels for forward (default "Send"), back ("Back"), void ("Void"), resend ("Resend") and assign-to-next ("Send"). Each label is used on both the action and its dialog title. *Vellum extension:* labels can be translated.
- **ADM-106** **Bulk-change fields:** the set of fields allowed in *global change field value* (PLT-1104). Protected fields can never be added ([PLT-408](02-platform.md)).
- **ADM-107** **Other preferences:**
  - Hide database errors in the UI (on by default and recommended).
  - Image types that show inline.
  - PDF watermark style: font, size, opacity, color, angle and position.
  - File types that get watermarked.
  - Max upload size.
  - Allowed attachment types (allow-list or deny-list).

## 5.2 Users and groups

The full profile model and lifecycle are in [SEC §4.7](04-security.md).

- **ADM-201** Users and groups shall be manageable in the UI, by import, by SCIM and through the API. Views of **users**: active (by name, reports-to, group, last modified, location, additional security), disabled and inactive. Views of **groups**: by group, by member, by location, inactive, by inactivated member, and **by access** (which applications and configuration items each group can reach, and at what level).
- **ADM-202** Importing reports-to relationships shall work in any order. The system resolves references after loading every row. *(Reliance makes you import top managers first.)*
- **ADM-203** A row with an invalid or duplicate email shall be skipped with a logged error, and the remaining rows shall still import.
- **ADM-204** Disabling a user shall need administration-center manager rights, or editor rights plus being listed in that user profile's record security.

## 5.3 Calendars, time zones and regional settings

- **ADM-301** The **business calendar** shall define: the first day of the week (also used by report date filters), weekend days, public holidays chosen from per-country holiday sets, extra **non-working dates**, and extra **working dates** that override weekends or holidays.
- **ADM-302** *(Vellum extension, should)* Calendars should be assignable **per location**. A record's due dates then use the calendar of its primary location.
- **ADM-303** The **system time zone** shall drive scheduled tasks and escalation evaluation. Users see times in their own time zone. Locations have a time zone that users inherit by default.
- **ADM-304** **Regional preferences:**
  - Dates and times shown in each user's locale (default), or in fixed full, long, medium and short patterns set by the administrator.
  - Optionally one locale enforced for everyone. If no locale is given, the server locale is used.
  - Optionally a picker required for date and time entry.
- **ADM-305** A default time zone and locale shall be configurable for anonymous users and submitters.

## 5.4 Locations

- **ADM-401** **Location profiles** shall define the organization hierarchy. Each has a hierarchical path (for example *Global : North America : USA : Kalamazoo*), a short display name, a time zone, location administrators (users and groups), optional calendar, address and regulatory identifiers, and a disabled flag. Views list locations by hierarchy, by display name and disabled.
- **ADM-402** Every location used on a record shall have a location profile ([INV-085](21-invariants.md)).
- **ADM-403** Records shall carry one or more locations. New records default to the creator's primary location.
- **ADM-404** A disabled location can't be picked on new records or profiles. Existing records keep it until someone removes it by hand.
- **ADM-405** Configuration objects and profiles (groups, keyword lists, names lists, homepage, mail and template profiles, workflows, views, forms, escalations, connection profiles) may have **location security**. Only administrators of those locations, plus global administrators, may then edit them ([SEC-518](04-security.md)).
- **ADM-406** Location hierarchy semantics shall be explicit:
  - "lower" means every descendant and "higher" means every ancestor.
  - Location administrators of a parent location **should** be able to administer descendant locations if the tenant enables *inherit location administration*. *(Reliance's documentation doesn't say either way.)*
- **ADM-407** Location profiles shall be importable in bulk.

## 5.5 Facilities and rooms

- **ADM-501** **Facility profiles** shall hold general, financial and regulatory information about a site (for example registration numbers such as FDA FEI, EPA ID and permits).
  - They also hold **contacts**: user or external person, title, address, type (*Emergency, Tier 1, Tier 2, Coordinator*, configurable) and up to three contact methods (email, mobile, phone).
  - They also hold a **compliance obligations** section ([EHS §17.8](17-ehs.md)).
- **ADM-502** **Room profiles** shall hold name, display name, description, phone, capacity and features. Rooms can be picked in names-type fields and booked for meetings and training sessions ([MTG-009](19-projects-tasks.md)).

## 5.6 Shared profiles

### Homepage profiles

- **ADM-601** Homepage profiles shall have:
  - Name and location security.
  - Widgets (at least one): delegations; assignments (applications to include, forms to hide); trainings; favorite charts (chart, display mode *chart / report / both*, one per row); favorite views.
  - External links, shown under Favorites.
  - *Applies to* users and groups.
  - *Apply to users without a homepage*.
- **ADM-602** When several profiles apply to a user, the user shall pick a default, which persists across sessions.

### Keyword lists

- **ADM-603** **Master keyword lists** shall have: display name, parent applications (filled in automatically from the fields that use the list), location security, keywords (text, stable ID, disabled flag, optional color, optional sort order, optional parent keyword for drill-down hierarchies), *allow location administrators to add keywords*, and linked **location keyword lists**.
- **ADM-604** Option resolution for keyword fields:
  - If the record's location has a location keyword list, the field offers **only** that list's subset.
  - Otherwise it offers the full master list.
  - Disabled keywords are never offered for new values, but they still display on existing records ([INV-061](21-invariants.md)).
- **ADM-605** Keywords that location administrators add shall be stored in the master list, tagged with their location, used only by that location, and protected from other locations' administrators.
- **ADM-606** Keyword lists shall support import (appending), export to Excel, and views by name, by field, by application, by location and **orphaned** (not used by any field).
- **ADM-607** Renaming a keyword's text shall be allowed only with a warning. Stored values are IDs, so historical records show the new label. Search shall reindex the affected records. The recommended alternative (disable the keyword and add a new one) shall be offered in the warning.

### Names lists

- **ADM-608** **Master names lists** shall have: name, location security, a shortlist of users and groups, a *location lists only* mode, and linked **location names lists**.
- **ADM-609** Option resolution for names fields:
  - **Normal mode:** the master shortlist **plus** the location list for the record's location.
  - **Location-only mode:** only the location list, or no options if the location has no list.
- **ADM-610** A master names list with location names lists can't be deleted until those lists are deleted.

### Decision trees

- **ADM-611** **Decision tree profiles** shall be created when a decision-tree section is added to a form. Each holds questions. Each question has answers: Reliance allows 4; Vellum allows at least 4 and should allow up to 10. Each answer leads to **another question** in the same profile or **shows a section** of the parent form. The designer shall reject loops and unreachable questions ([INV-139](21-invariants.md)).

### Mail profiles

- **ADM-612** **Mail profiles** shall define email templates for a form:
  - To, CC and BCC recipients, from fields, users, groups or rules.
  - Subject and rich-text body with inserted field values.
  - Static attachments, the parent record as PDF, and selected attachment fields.
- **ADM-613** On records with a *mail activities* section, users shall be able to create a **mail activity** from a profile, edit it, send it, and *send again* (which makes an editable copy). Sent mail activities are read-only.
- **ADM-614** BCC recipients shall be visible only to the sender and to administrators. They are **not** exposed in field history to other readers. *(Fixes a Reliance privacy gap.)*

### Other profiles

- **ADM-615** **Inheritance settings** ([DSN-450](03-designer.md)), **document templates** ([INT §6.4](06-integrations.md)), **connection profiles** ([INT §6.1](06-integrations.md)), **SSO profiles** ([SEC §4.1](04-security.md)) and **IP address profiles** ([SEC §4.9](04-security.md)) shall all be managed as profiles in the Administration Center.

## 5.7 Localization

- **ADM-701** Localization shall be switched on per tenant. With it off, the UI is in the tenant's base language.
- **ADM-702** **Localization profiles**, one per locale, shall cover a chosen set of applications (empty means all, with administration and engine items included optionally). They cover:
  - Platform UI.
  - Application and record pages.
  - Designer pages.
  - Login page.
  - System Preferences and user menu pages.
  - Configured labels: forms, fields, sections, tabs, phases, workflows, views, columns, actions.
  - Keyword values.
  - Notification templates.
  - Mobile layouts.
- **ADM-703** Translators shall be able to **export** a translation package (spreadsheets or XLIFF), translate it offline, and **import** it back.
  - *Sync with base* shall add or change entries after configuration changes and highlight new or changed entries.
  - Imports shall be validated, and protected tokens such as placeholders and bracketed codes must be kept intact.
  - Vellum should also allow translating inline in the UI.
- **ADM-704** Fallback: any string without a translation falls back to the tenant's base language. The login page language follows the browser locale. Users change language in their profile, and Vellum applies the change without forcing a new login.
- **ADM-705** At least the **44** Reliance languages shall be supported: Arabic, Bulgarian, Chinese (Simplified), Chinese (Traditional), Croatian, Czech, Danish, Dutch, English (US), English (International), Estonian, Finnish, French, German, Greek, Hebrew, Hindi, Hungarian, Indonesian, Italian, Japanese, Korean, Latvian, Lithuanian, Malay, Norwegian, Persian, Polish, Portuguese, Punjabi, Romanian, Russian, Serbian, Slovak, Slovenian, Spanish, Swedish, Thai, Turkish, Ukrainian and Vietnamese, plus others as added. Right-to-left languages shall render RTL ([NFR-061](22-non-functional.md)).
- **ADM-706** *(Vellum extension)* Items Reliance can't localize shall be localizable in Vellum: routing labels, system comments, the submitter page, placeholders and environment banner text. User-entered record data is never translated automatically, except by optional AI translation that is clearly labeled ([INT §6.10](06-integrations.md)).

## 5.8 Scheduled tasks (task profiles)

- **ADM-801** **Task profiles** shall have: display name, description, scope (one application or system-wide), what they run (a packaged job or a Vellum Script), enabled flag, and a schedule.
  - Schedule options: *daily at* ⟨time⟩; *every N days*; *several times a day* (an interval, with a daily window); *weekly on* ⟨days⟩; each with optional start and end dates.
  - *Run on weekends and holidays* is optional. Otherwise the business calendar is respected.
  - Schedules run in the system time zone.
- **ADM-802** Packaged jobs shall include: data integration (connection profiles), escalations, license-expiry reminder, **attachment integrity checker** (checks every stored file against its checksum and reports missing or corrupt files), training expiration and retraining, assignment voiding, document review reminders, recurring task generation, delegation expiry, dormant-user check, retention disposition candidates, regulatory feed import, search index maintenance, and AI index maintenance.
- **ADM-803** A task shall never run twice at the same time ([INV-121](21-invariants.md)). *Run now* (with confirmation) shall be available for enabled tasks. A manual run while a scheduled run is pending skips that scheduled run.
- **ADM-804** Task failures shall notify administrators on each failure, whatever the task type, until the task succeeds or is disabled. Every run (start, end, outcome, records affected, errors) shall be kept in run history.
- **ADM-805** [SAFETY] Missed runs (outage or downtime) shall be detected and **caught up** on recovery, per task policy (*run once* or *run every missed occurrence*), and administrators shall be alerted ([SAF-030](20-safety.md)).

## 5.9 Printing and PDF preferences

- **ADM-901** Tenants shall be able to switch on **print with related records and attachments**. It produces one combined PDF of a record, its linked records (to a configurable depth) and their PDF or converted attachments, with a table of contents.
- **ADM-902** The PDF conversion service shall support a documented set of server fonts, including CJK families. When a font isn't available, the conversion shall **warn** (font substitution) instead of silently changing the output. Users shall be told how to embed fonts.

## 5.10 Logs and monitoring

- **ADM-1001** **System log:** daily UTC log files with case-insensitive search (matches highlighted) and download of a whole day. Administrators only. At least **12 months** viewable. *(Reliance keeps 6 months.)*
- **ADM-1002** **Email log:** per-record history of every email sent (date, from, to, CC, subject, body summary, delivery status), exportable to PDF. In Vellum the email log shall be available to **every** tenant. *(Reliance needs a 21 CFR license.)*
- **ADM-1003** **Deletion history:** every record deletion (not only those on e-signature forms), with date, application, form, number, revision, metadata snapshot, comment or reason, user and signature (if one was required).
- **ADM-1004** **Import history:** each import job with job ID, file, user, mode, counts, error report download and an emailed summary. Error reports over 20 MB are downloadable only, not attached to the email.
- **ADM-1005** **Integration logs:** every inbound and outbound integration call or message (endpoint, status, timing, payload reference, retries, error).
- **ADM-1006** **Task run history** (ADM-804) and **escalation run logs** ([PLT-1210](02-platform.md)).
- **ADM-1007** **AI usage report:** credits allocated, consumed and remaining (absolute and %), per feature and period, refreshed at least every 4 hours ([INT §6.10](06-integrations.md)).
- **ADM-1008** **Operational health dashboard** *(Vellum extension)*: queue depths, failed jobs, failed notifications, conversion failures, integration failures, index lag and certificate expiry dates.

## 5.11 Licensing and usage

- **ADM-1101** The system shall track licenses by type (full, reader, external, submitter) and by mode (concurrent, permanent/named). *About* shall show the logged-in and maximum seats for each type.
- **ADM-1102** Login usage records (SEC-1003) shall be kept at least **12 months**. *(Reliance keeps 90 days.)*
- **ADM-1103** License-expiry warnings shall be emailed to administrators using a configurable lead time and template.

## 5.12 Administration Center and Platform Engine catalog

- **ADM-1201** The Administration Center shall group its views into numbered categories: users, groups, locations, facilities, rooms, keyword lists, names lists, homepage, mail, template, decision tree, localization, task, connection, SSO, IP address and regulatory-gateway profiles, inheritance settings, import history, deletion history, usage reports and AI usage.
- **ADM-1202** The **Platform Engine** catalog ([DSN-1205](03-designer.md)) shall also provide:
  - A **data dictionary**: every form and subform with its fields, field types, multi-value, unique and not-null flags, and the keyword list used.
  - **Orphan detection**: actions, columns, data sources, fields, forms, phases, sections, subforms, text prompts, views and workflows that aren't attached to anything.
  - **Impact analysis**: what uses a given object.
