# 11. Training Management

Training Management keeps a qualified workforce. It covers employee profiles and applicability, the course catalog, assignment generation, online and instructor-led delivery, testing, certification, expiration and retraining, and the tie to controlled-document revisions.

**Parity sources:** Reliance help › Training Management, Training Management 2.1.0, Document Control 2.0.0 (training tab), Homepage (Trainings widget).

## 11.1 Application profile and permissions

- **TRN-001** Training Management shall have **exactly one** Application Profile, a singleton ([INV-104](21-invariants.md)). It holds application-wide permissions and the certificate template.
- **TRN-002** The Application Profile shall define these **employee-information permissions**. Each defaults as shown, and each can be extended to the employee's *Reports To* manager and/or to extra users or groups:

  | Permission | Default holders |
  |---|---|
  | Create employee profiles | Application managers |
  | View employee profiles | Application managers + the employee |
  | View employee assignments and view tests | Application managers + the employee |
  | Edit employee profiles and manage employee assignments | Application managers |

- **TRN-003** The Application Profile shall define cumulative **course permissions**, for example for training coordinators: (1) create and edit course profiles; (2) + create assignments from a course profile; (3) + view test templates; (4) + edit test templates. Level 1 defaults to application managers. Levels 2–4 default to application managers and course owners.
- **TRN-004** Users shall need at least **author** access to the application before they can be given any create or edit training permission.

## 11.2 Employee profiles and applicability

- **TRN-010** Every trainee shall have an **Employee Profile** linked **one-to-one** to a User Profile. **Training-only users**, who have no login rights to the rest of the system, shall be supported ([INV-105](21-invariants.md)).
- **TRN-011** The Employee Profile shall copy first, middle and last name, status, reports-to and location(s) from the user profile, and keep them in sync. It shall hold employee type (*Full Time*, *Temporary*, or configurable), department(s), job position(s), training group(s) and a **job position history** (position, start, end).
- **TRN-012** User Profiles shall have to exist before Employee Profiles can be created. The system shall support creating Employee Profiles automatically when users are provisioned (for example via SCIM), if configured.
- **TRN-013** **Applicability rules** on a course shall select employees by department, job position, training group, location, employee type and any other configured attribute. Semantics: **values within one field are OR; fields within one rule row are AND; separate rows are OR**. Empty fields are ignored, and each row shall have at least one non-empty field ([INV-106](21-invariants.md)).
- **TRN-014** When an employee's applicability attributes change (for example a new job position), the system shall, on save: (a) **create assignments** for courses that now apply, if auto-assignment is enabled for those courses, and (b) **void pending assignments** for courses that no longer apply. Both actions are recorded on the employee's training history.
- **TRN-015** From an Employee Profile, authorized users shall be able to: change due dates of pending assignments; void pending assignments; assign courses not yet assigned (with due date); and **complete assignments on the employee's behalf**. On-behalf completion **requires a justification** and is recorded as such.
- **TRN-016** The Employee Profile shall offer an **Employee Training Records** view: all of the employee's pending, completed, expired and voided assignments. It shows employee and organizational attributes, course details, assignment status and dates (due, completed, expiration), and the source document's number, title, revision and effective date.

## 11.3 Course profiles

- **TRN-020** A **Course Profile** shall define: name, overview, topic category, owner(s) and location(s) (defaulting to the creator), course type (**Online** or **Instructor-led**), and evaluation method (**Attendance-based**, **Scoring** or **Pass/Fail**). Pass/Fail turns on testing.
- **TRN-021** Course **materials** shall include attachments, external links and Vellum record links.
- **TRN-022** Course **prerequisites** shall link to other Course Profiles. An employee shall not be able to complete a course until every prerequisite course is completed and not expired ([INV-107](21-invariants.md)).
- **TRN-023** A **due-date delay** (number + unit) shall calculate assignment due dates from the assignment date.
- **TRN-024** **Expiration:** a course may set completed assignments to expire after a period (number + unit). Expired assignments shall get status *Expired*.
- **TRN-025** **Retraining before expiration:** a course may create a new pending assignment a set period before the current completion expires, so there is no gap in qualification. Turning expiration or retraining on shall also apply to existing completed assignments, after the user confirms that impact.
- **TRN-026** **Source material:** a course created from a controlled document shall record the source number, source link and source attachments. If *automate with next revision* is set, the next revision gets a new course automatically ([DOC-062](10-document-control.md)).
- **TRN-027** A Course Profile may be **disabled** at any time. Disabling makes it read-only, blocks new assignments and schedules, **voids its pending assignments automatically**, and flags its open course schedules for manual voiding. Disabled courses may be re-enabled.
- **TRN-028** Course Profiles shall never be deleted once any assignment exists. They are disabled instead ([INV-010](21-invariants.md)).

## 11.4 Testing

- **TRN-030** A course with testing enabled shall have a **question bank**. Each question has at least two choices and **exactly one correct** choice. Question validation shall enforce this ([INV-108](21-invariants.md)). *(Vellum extension, should: multi-select, true/false and ordered-answer question types, with a scoring rule per type.)*
- **TRN-031** Test settings: minimum passing score (%); pass message and fail message; **fixed** or **random** test format (random needs a number of questions per test); optional delay before a retake (number + unit); optional **maximum attempts** (unlimited if unset); per-question binding to a specific attempt number; and an option to show incorrectly answered questions after submission.
- **TRN-032** A **Validate Question Bank** action shall check that there are enough questions for every allowed attempt, including attempt-specific questions, and that the bank matches the test format.
- **TRN-033** Test results shall be recorded with every answer, score, pass/fail, attempt number and timestamp. Attempt records shall be immutable once submitted ([INV-030](21-invariants.md)).
- **TRN-034** When an employee uses up all attempts without passing, their manager shall be notified. An authorized user may then (a) **add more attempts**, with the number and a required reason, or (b) record **one-on-one remedial training**, with comments, attachments and submission, which completes the assignment as *remediated*.
- **TRN-035** Test templates linked to a document revision shall be copied to new revisions and obsoleted with old ones ([DOC-064](10-document-control.md)).

## 11.5 Assignment generation

- **TRN-040** Completion modes by course type:
  - **Online, no test:** the employee opens the materials and **acknowledges** that they have read and understood them. This is the read-and-understood mechanism for controlled documents. It shall need an e-signature if configured [P11].
  - **Online with test:** *Take Test*, with results shown immediately.
  - **Instructor-led, no test:** the employee can't complete it. The instructor records the outcome through a Course Schedule.
  - **Instructor-led with test:** the test can be taken only after the instructor opens testing.
- **TRN-041** From a Course Profile, authorized users shall be able to **load employees** from the applicability rules, refine the list, choose which to include (*not taken before*, *completed or expired*), **force retraining** for selected employees who have already completed the course, set the due date (or let the due-date delay calculate it), and **create assignments** in bulk. Each employee gets a notification.
- **TRN-042** The system shall prevent **duplicate pending assignments** of the same course for the same employee ([INV-109](21-invariants.md)).
- **TRN-043** The system shall automatically void assignments that become invalid (employee deactivated, course disabled, applicability removed). Employees shall not be able to open invalid assignments while they wait to be voided.
- **TRN-044** Assignments shall appear in the employee's homepage **Trainings** widget, the calendar and *My Assignments* ([PLT §2.14](02-platform.md)).

## 11.6 Instructor-led delivery (course schedules)

- **TRN-050** A **Course Schedule** may be created from New Document or from a Course Profile. It shall reference exactly one course. Enrollees shall be chosen from that course's pending assignments ([INV-110](21-invariants.md)).
- **TRN-051** Course Schedules shall capture date and time, location or room ([ADM §5.5](05-administration.md)), instructor and enrollees. They shall also have a **Training Evidence** section: a description and attachments such as sign-in sheets.
- **TRN-052** Completion by evaluation method:
  - **Attendance-based:** mark *Attended?* per enrollee (with *mark all*). On completion, attendees' assignments are completed and non-attendees' assignments are voided.
  - **Scoring:** record a score and pass/fail per enrollee. Passed assignments are completed, and failed ones are voided.
  - **Pass/Fail with test:** set the testing due date and assignee, then route to *Assign for Testing*. Enrollees are notified that the test is open, and the schedule completes later.
- **TRN-053** Instructors shall be able to e-sign attendance records when configured [P11].

## 11.7 Certificates and records

- **TRN-060** On completion, the system shall create a **training record**. Where configured, it shall also create a **certificate** from the Application Profile's document template, filled in with employee, course, date, score and expiration ([INT §6.4](06-integrations.md)). The certificate can be downloaded from the training record.
- **TRN-061** Training records shall be immutable once completed ([INV-030](21-invariants.md)). Corrections shall go through a voiding-and-reissue action with a reason.
- **TRN-062** Completed training records shall stay visible in reports even after the employee is deactivated or the course is disabled.

## 11.8 Qualification and gating

- **TRN-070** *(Vellum, should; extends parity)* The system should be able to treat a person as **qualified** for a job position or task when all applicable courses are completed and not expired. It should expose that status to other applications (for example to block assigning an unqualified person as an inspector or approver).
- **TRN-071** [SAFETY] A document revision configured with a *training-gated release* shall not become Effective until the configured percentage of applicable employees (default 100%, configurable) has completed training, or until an authorized user overrides with a recorded justification.

## 11.9 Views

- **TRN-080** Packaged views: Employee › Pending Assignments, Active Training Records; Employee Management › Employees, Assignments › Pending (by assignee, by course, by department), Overdue; Course Catalogs › Active, Disabled; Course Schedules › Open, Completed; Administration › Application Profile. Views shall honor training-visibility permissions (TRN-002).

## 11.10 Scheduled automation

- **TRN-090** Vellum shall provide these scheduled tasks, each configurable and individually turn-off-able:
  - *Disable course on source document archive/obsolete/void.*
  - *Void non-applicable training assignments.*
  - *Create assignments from expiration (retraining).*
  - *Expire completed assignments.*
  - *Overdue assignment reminders and escalation to the manager.*
- **TRN-091** Each scheduled task run shall log start, end, the records affected and any errors ([ADM §5.10](05-administration.md)).

## 11.11 Import

- **TRN-100** Employee profiles, course profiles, question banks and historical training records shall be importable in bulk ([INT §6.8](06-integrations.md)). Parent records are imported first, and child rows refer to them by number. The import may skip field filter and correction rules where needed.
