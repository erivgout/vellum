using Vellum.Domain;
using Vellum.Modules.Documents;
using Vellum.Packaging;

namespace Vellum.Modules.Training;

public static class TrainingKeys
{
    public const string App = "training";
    public const string Course = "course";
    public const string Assignment = "training_assignment";

    public const string Assigned = "assigned";
    public const string Completed = "completed";
    public const string Expired = "expired";
    public const string Voided = "voided";

    /// <summary>TRN-040: online read-and-understood acknowledgment.</summary>
    public const string SignAcknowledge = "Read and understood";
    public const string SignOnBehalf = "Completed on behalf";
}

/// <summary>
/// Training Management §11 (scoped to course profiles, applicability, assignments, acknowledgment, expiration and retraining):
/// course profiles (TRN-020..028) with applicability rules (TRN-013, INV-106), training assignments with an e-signed
/// read-and-understood completion (TRN-040), at most one pending assignment per employee and course (INV-109),
/// prerequisites (INV-107), immutable completed records (INV-030) and the scheduled tasks of TRN-090.
/// </summary>
public sealed partial class TrainingApp : IPackagedApp
{
    public string Key => TrainingKeys.App;
    public int Order => 25;

    public void Define(AppBuilder app)
    {
        app.Name("Training Management")
            .Description("Course catalog, read-and-understood acknowledgments, training assignments, expiration and retraining.")
            .Icon("bi-mortarboard").Color("#0891b2").Suite("Documents").Order(2).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@" + DocKeys.TrainingCoordinators, AppRole.Manager, designer: true)
            .Grant("@quality", AppRole.Author)
            .Grant("@" + DocKeys.DocControllers, AppRole.Author)
            .Grant("dokafor", AppRole.Manager);

        app.Keywords("trn_course_type", "Course type", k => k
            .Add("online", "Online", "info").Add("instructor_led", "Instructor-led", "violet"));
        app.Keywords("trn_evaluation", "Evaluation method", k => k
            .Add("acknowledgment", "Read and understood").Add("attendance", "Attendance-based").Add("scoring", "Scoring").Add("pass_fail", "Pass/Fail"));

        app.Form(TrainingKeys.Course, "Course Profile", f => f
            .Icon("bi-mortarboard")
            .Description("A course: what it covers, who it applies to, due dates, expiration and retraining (TRN-020).")
            .NonWorkflow()
            .Numbering("CRS-")
            .Title("{course_name}")
            .Section("course", "Course", s =>
            {
                s.Text("course_name", "Course name", 8).Required().MaxLength(200);
                s.YesNo("enabled", "Enabled", 4).Default("yes").Header()
                    .Help("Disabling blocks new assignments and voids pending ones automatically (TRN-027).");
                s.TextArea("overview", "Overview");
                s.Keyword("course_type", "Course type", "trn_course_type", 4).Required().Default("online").Header()
                    .Help("Online courses are completed by the trainee's e-signed acknowledgment. Instructor-led completion is recorded by a coordinator (TRN-040).");
                s.Keyword("evaluation", "Evaluation", "trn_evaluation", 4).Required().Default("acknowledgment");
                s.User("owner", "Course owner", 4).Required().Default("$me").Header();
            }, icon: "bi-mortarboard")
            .Section("source", "Source and materials", s =>
            {
                s.Link("source_document", "Source document", DocKeys.Document)
                    .Help("The controlled document revision this course trains on (TRN-026).");
                s.Text("source_ref", "Source", 6).ReadOnly();
                s.YesNo("automate_next_revision", "Automate with next revision", 6).Default("yes")
                    .Help("Creates a new course with the same settings for each later revision of the source document (DOC-062).");
                s.Attachment("materials", "Course materials")
                    .Help("Copied from the source document's controlled file when assignments are created.");
                s.Url("material_link", "External material", 12);
            }, icon: "bi-paperclip")
            .Section("schedule", "Due dates, expiration and prerequisites", s =>
            {
                s.Number("due_days", "Due-date delay (days)", 4).Required().Min(0).Max(365).Decimals(0).Default("14")
                    .Help("Assignment due date = assignment date + this many days (TRN-023).");
                s.Number("expiration_months", "Expires after (months)", 4).Min(0).Max(120).Decimals(0)
                    .Help("Leave blank for training that never expires (TRN-024).");
                s.Number("retraining_days", "Retrain this many days before expiry", 4).Min(0).Max(365).Decimals(0).VisibleWhen("expiration_months")
                    .Help("Creates the next assignment ahead of expiry so qualification has no gap (TRN-025).");
                s.Link("prerequisites", "Prerequisite courses", TrainingKeys.Course)
                    .Help("Must be completed and not expired before this course can be completed (INV-107).");
                s.Users("named_employees", "Always assign", 12)
                    .Help("People assigned in addition to the applicability rules.");
            }, icon: "bi-calendar-event")
            .Subform("applicability", "Applicability", s =>
            {
                s.Keywords("departments", "Departments", "org_department", 3);
                s.Keywords("sites", "Sites", "org_site", 3);
                s.Keywords("groups", "Training groups", "trn_group", 3);
                s.Text("job_title", "Job title contains", 3);
            }, rowNoun: "rule", icon: "bi-funnel",
               description: "Each rule selects employees who match every filled-in field (any of the values in a field). Employees matching any rule are included (TRN-013, INV-106)."));

        app.Form(TrainingKeys.Assignment, "Training Assignment", f => f
            .Icon("bi-person-check")
            .Description("One employee's assignment of one course, completed by acknowledgment or by a coordinator (TRN-040).")
            .Numbering("TA-")
            .Title("{course_name} – {employee}")
            .Section("assignment", "Assignment", s =>
            {
                s.User("employee", "Employee", 4).Required().Header();
                s.Link("course", "Course", TrainingKeys.Course).Required();
                // Denormalized for views and titles; hidden on the record page, where the course link shows instead.
                s.Text("course_name", "Course", 6).ReadOnly().VisibleWhen("course_name=__views_only__");
                s.Date("due_date", "Due date", 4).Required().Header();
                s.Link("source_doc", "Source document", DocKeys.Document).ReadOnly();
                s.Text("reason", "Assigned because", 8).ReadOnly();
            }, icon: "bi-person-check")
            .Section("acknowledgment", "Acknowledgment", s =>
            {
                s.Prompt("ack_prompt", "Open the course materials and the source document, then choose Acknowledge: read and understood and sign with your password. Your signature confirms you have read, understood and will follow the content (TRN-040).");
                s.TextArea("notes", "Comments");
            }, icon: "bi-pen")
            .Workflow("training_assignment", "Training Assignment", w =>
            {
                w.Start(TrainingKeys.Assigned, "Assigned")
                    .Purpose("Complete the training by the due date. For online courses, read the material and acknowledge it with your e-signature.")
                    .AssignField("employee").DueFrom("due_date").Edit("acknowledgment").ReadOnly("assignment");
                w.Completed(TrainingKeys.Completed, "Completed");
                w.Completed(TrainingKeys.Expired, "Expired");
                w.Voided();
                w.Forward(TrainingKeys.Assigned, TrainingKeys.Completed, sign: TrainingKeys.SignAcknowledge, label: "Acknowledge: read and understood");
            }));

        app.View("my_pending", "My pending training", v => v.Category("1. My training").Forms(TrainingKeys.Assignment).Mine()
            .Columns("number", "course_name", "due", "reason").Sort("due", false).Default());
        app.View("my_records", "My training records", v => v.Category("1. My training").Forms(TrainingKeys.Assignment).All().Filter("employee", "eq", "$me")
            .Columns("number", "course_name", "phase", "due_date", "modified").Sort("modified"));

        app.View("pending_by_course", "Pending by course", v => v.Category("2. Assignments").Forms(TrainingKeys.Assignment).Open()
            .Columns("course_name", "employee", "number", "due", "reason").Sort("course_name", false));
        app.View("pending_by_employee", "Pending by employee", v => v.Category("2. Assignments").Forms(TrainingKeys.Assignment).Open()
            .Columns("employee", "course_name", "number", "due").Sort("employee", false));
        app.View("overdue", "Overdue training", v => v.Category("2. Assignments").Forms(TrainingKeys.Assignment).Overdue()
            .Columns("number", "course_name", "employee", "due").Sort("due", false));
        app.View("completed", "Completed training records", v => v.Category("2. Assignments").Forms(TrainingKeys.Assignment).Completed().Phases(TrainingKeys.Completed)
            .Columns("number", "course_name", "employee", "modified").Sort("modified"));
        app.View("expired", "Expired training", v => v.Category("2. Assignments").Forms(TrainingKeys.Assignment).Completed().Phases(TrainingKeys.Expired)
            .Columns("number", "course_name", "employee", "modified").Sort("modified"));
        app.View("voided", "Voided assignments", v => v.Category("2. Assignments").Forms(TrainingKeys.Assignment).Voided()
            .Columns("number", "course_name", "employee", "modified").Sort("modified"));

        app.View("courses_active", "Active courses", v => v.Category("3. Course catalog").Forms(TrainingKeys.Course).All().Filter("enabled", "eq", "yes")
            .Columns("number", "title", "course_type", "source_ref", "owner", "expiration_months").Sort("title", false));
        app.View("courses_disabled", "Disabled courses", v => v.Category("3. Course catalog").Forms(TrainingKeys.Course).All().Filter("enabled", "eq", "no")
            .Columns("number", "title", "source_ref", "owner", "modified").Sort("modified"));
    }
}
