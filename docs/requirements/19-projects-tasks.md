# 19. Projects, Tasks, Meetings and Release Management

**Parity sources:** Reliance help › Project Control, Meetings, Task Management, Release Management for ETQ; Octave product page › New Product Introduction.

## 19.1 Project Control

- **PRJ-001** The system shall provide **Project Templates** with: project type (*Native* or *Imported from Microsoft Project*), a project role list, optional section toggles (product, customer, supplier, process), non-working days (*skip weekends* plus a list of days to skip), a disabled flag, and an optional Microsoft Project (.mpp) file.
- **PRJ-002** A template shall contain one or more **Outline Templates**. Each holds **Task templates** with name, description, assigned role (limited to the template's role list), predecessors, duration (days), start-date delay (days) and the task workflow to use.
- **PRJ-003** Creating a **Project** from an active template (disabled templates are hidden) shall copy its defaults and **generate the Outline and Task records automatically**.
- **PRJ-004** A Project shall capture: start date; project end date (calculated as the latest task finish); actual completion date; a role-to-person mapping; product information (looked up from Suppliers, Materials and Chemicals, or entered by hand); customer information (looked up from Customer Profiles); supplier information (looked up from Supplier Profiles); and process information.
- **PRJ-005** Task dates shall be calculated as *start = project start + start delay* and *finish = start + duration*, skipping non-working days as configured.
- **PRJ-006** **Assign All Tasks** shall route each task whose roles are all filled to *Open* and assign it to the mapped people. Tasks with unfilled roles shall be skipped and reported.
- **PRJ-007** Task workflow: *Open → Completed* or *Not Completed*. Completing a task shall set percent complete to 100.
- **PRJ-008** The project shall show a **calculated risk status**: *High* if any task is red (overdue or at risk), else *Low* if any task is yellow, else *No Risk*. The thresholds for each task color shall be configurable. [CFG]
- **PRJ-009** **Microsoft Project sync:**
  - Uploading or saving a project with a .mpp file shall generate outlines and tasks, create a project role per MPP resource, replace resources with the assigned names, shift dates by the business-day offset between the MPP start date and the project start date, and fill in predecessor links with their relationship types (FS, SS, FF, SF).
  - Mapped fields shall sync both ways: on re-upload, new tasks are added in order and tasks deleted in MPP are routed to *Rejected*.
  - Constraints, validated on upload with clear errors: fixed-units tasks only; resources must match project roles; durations in days; every task under a summary task; MPP holidays must match Vellum's calendar; a normal task can't later become a summary task; the MPP workflow column is used only when the task is first created.
- **PRJ-010** Tasks and projects shall be able to schedule **Meetings** (§19.3) and spawn activities (action items, CAPAs, change requests).
- **PRJ-011** The system shall render a **Gantt chart** for any project. It shall support day, week, month and year scales; dependency lines colored by relationship type; tooltips (name, start, end, duration, assignee); sorting; export to PDF; and print. Non-working days shall be shaded and excluded from durations. Unlike Reliance, Vellum **shall** also exclude configured holidays.

## 19.2 New Product Introduction (provisional, open question 2)

- **PRJ-020** *(Provisional)* The system shall provide an NPI project type built on Project Control, with **stage gates**: configurable phases (for example Concept → Feasibility → Design → Validation → Launch). Each gate needs an e-signed **design review** record with attendees, checklist, decisions and action items before the project can move on.
- **PRJ-021** *(Provisional)* An NPI project shall link to design inputs and outputs (Requirements, §19.5), risk analyses (FMEA, [RSK §16.3](16-risk.md)), change requests ([CHG](13-change-management.md)) and PPAP submissions ([PPAP](15-supplier-quality.md)). It shall produce a **traceability matrix** linking design inputs → outputs → verification → validation.

## 19.3 Meetings

- **MTG-001** The system shall provide **Meeting Profiles**: templates for recurring meetings with chair, invitees, minute-taker, default agenda items and inheritance options. A meeting can inherit its agenda from the profile, or from the previous meeting, optionally together with minutes, open action items and open corrective actions.
- **MTG-002** Creating a **Meeting** from an enabled profile (disabled profiles can't be used) shall copy start and end times, chair, invitees, minute-taker and agenda. All of these shall stay editable until invitations are sent.
- **MTG-003** **Notify Invitees** shall send calendar invitations through the calendar integration (Microsoft 365/Outlook, Google Calendar; [INT §6.6](06-integrations.md)). If calendar integration is off, the system shall send an *invitation activity* the invitee answers with *Will attend* or *Will not attend*. Changes after the invitations go out shall reach invitees only when invitations are re-sent. The UI shall show a warning when that's needed.
- **MTG-004** **Cancel invitations** shall send cancellations to all invitees.
- **MTG-005** **Load Attendees** shall put invitees who declined in *Absentees*, and everyone who accepted or didn't answer in *Attendees*. The list shall stay editable afterward.
- **MTG-006** The meeting shall record minutes per agenda item. Action items and corrective actions shall be created from agenda items and linked to them. A **Meeting Summary** shall list the agenda items with their linked actions.
- **MTG-007** **Action Items** workflow: *Open → (optional) Verification → Completed*. A verifier may send the item back to the assignee.
- **MTG-008** **Schedule Next** on a completed meeting shall create the next meeting, applying the profile's inheritance options.
- **MTG-009** Meetings shall be able to book **rooms** ([ADM §5.5](05-administration.md)). The system should flag double-booked rooms.
- **MTG-010** Meeting workflow: *Draft → Completed* or *Voided*. Packaged views: Meeting Profiles, Upcoming, Completed, Voided.

## 19.4 Task Management (including recurring tasks)

- **TSK-001** The system shall provide standalone **Tasks** with workflow *Draft → Open (assigned) → Completed*. A task may link to a source record, such as a compliance plan or requirement, and the source shall get a back-link automatically.
- **TSK-002** A task may be **recurring**, in which case it generates ordinary tasks on a schedule. Supported patterns:
  - **Daily:** every *X* days.
  - **Weekly:** every *X* weeks, or on chosen weekdays.
  - **Monthly:** day *N* of every *X* months, or the *[ordinal] [weekday]* of every *X* months.
  - **Yearly:** every *X* years on the start date's day and month; on day *D* of month *M*; or the *[ordinal] [weekday]* of *[month]*.
- **TSK-003** Recurrence evaluation: the first task is generated when the start date is reached and no generated task exists yet. After that, a task is generated whenever the pattern matches. A generated task's due date is *creation + one period*, configurable.
- **TSK-004** Recurrence end options: *no end date*, *after N occurrences*, or *end on date*. When the end condition is met, the generator shall stop and route itself to *Completed*.
- **TSK-005** [SAFETY] If the scheduler can't run (outage), it shall **catch up** on resuming. It generates every task that was missed, keeps its original due dates and marks it *late-generated*, so no compliance task is skipped silently ([SAF-030](20-safety.md)). *(This is stronger than Reliance's behavior.)*
- **TSK-006** Due-date notification options shall be configurable per task: reminders before the due date, on it, and when overdue.

## 19.5 Release Management (validation of Vellum configuration)

This application lets regulated customers validate **their own Vellum configuration**: requirements → test scripts → executed test records, plus change control for configuration changes.

- **REL-001** The Application Profile shall keep a registry of **environments**, each with a unique name and URL (for example Development, Test/Validation, Production).
- **REL-002** **Requirements** shall be revision-controlled ([DOC §10.2](10-document-control.md) mechanics: change requests, obsolete requests, archive). They follow the workflow *Draft → Approval → Completed* (or back to Draft, or Void), and they support **parent/child hierarchies**. A child shall be **approved before** it is linked to a parent ([INV-111](21-invariants.md)).
- **REL-003** **Test Scripts** shall be revision-controlled, owned and approved, with the workflow *Draft → Owner Approval → Completed*. A test script lists the **completed** requirements it covers and the applicable configuration items (for example which applications it exercises).
- **REL-004** A **Validation Project** shall group requirements. *Create Test Plans* generates one Test Plan per included requirement. *Update Test Plans* creates plans for added requirements and voids plans for removed ones. A requirement with no children may have a Test Plan created directly from it.
- **REL-005** A **Test Plan** shall name the test environment and the scripts to run. When newer revisions of its test scripts exist, it shall show a *Synchronize Test Scripts* action. *Generate Test Records* creates one Test Record per selected script for the chosen environment.
- **REL-006** A **Test Record** shall hold the executed script evidence (attachments, screenshots), the result (*Pass/Fail*), the tester's e-signature and comments. Failed records shall be able to raise a deviation or defect ([QE](12-quality-events.md)).
- **REL-007** **Configuration Change Requests** shall follow *Draft → Review → On Hold → Accepted → Assigned to Release*. They record target date and target release, and they link to the promotion package that implements them ([DSN §3.8](03-designer.md)).
- **REL-008** The system shall produce a **traceability matrix** report: requirement ↔ test script ↔ test record (with result) per validation project and environment. It shall flag requirements with no coverage and failed tests that have no linked resolution.
