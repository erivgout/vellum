using Vellum.Domain;
using Vellum.Modules.Kit;
using Vellum.Packaging;

namespace Vellum.Modules.Projects;

/// <summary>
/// Projects, Tasks and Meetings (PRJ/TSK/MTG §19): stage-gate (NPI-style) projects whose gates are approved with e-signatures by
/// every gate approver (PRJ-020), tasks as project activities (PRJ-007), recurring task schedules with catch-up (TSK-002..005),
/// and meetings with minutes and action items (MTG-006, MTG-010).
/// </summary>
public sealed class ProjectsApp : IPackagedApp
{
    public string Key => "projects";
    public int Order => 90;

    public static readonly string[] Stages = ["concept", "feasibility", "development", "validation", "launch"];

    public void Define(AppBuilder app)
    {
        app.Name("Projects & Tasks")
            .Description("Run stage-gate product and transfer projects, recurring compliance tasks and meetings with minutes and actions.")
            .Icon("bi-kanban").Color("#4f46e5").Suite("Operations").Order(20).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@engineering", AppRole.Author)
            .Grant("@operations", AppRole.Author)
            .Grant("@quality", AppRole.Author)
            .Grant("@ehs", AppRole.Author)
            .Grant("lgarcia", AppRole.Manager, designer: true);

        Sites.Define(app);
        app.Keywords("npi_stage", "Project stage", k => k
            .Add("concept", "Concept").Add("feasibility", "Feasibility").Add("development", "Development")
            .Add("validation", "Validation").Add("launch", "Launch"));
        app.Keywords("project_type", "Project type", k => k
            .Add("npi", "New product introduction", "primary").Add("transfer", "Manufacturing transfer", "violet")
            .Add("improvement", "Process improvement", "info").Add("capital", "Capital project", "secondary"));
        app.Keywords("gate_decision", "Gate decision", k => k
            .Add("go", "Go", "success").Add("go_conditions", "Go with conditions", "info").Add("hold", "Hold / recycle", "warning").Add("stop", "Stop", "danger"));
        app.Keywords("project_risk", "Schedule risk", k => k
            .Add("none", "No risk", "success").Add("low", "Low", "warning").Add("high", "High", "danger"));
        app.Keywords("priority", "Priority", k => k
            .Add("low", "Low", "secondary").Add("medium", "Medium", "info").Add("high", "High", "warning").Add("critical", "Critical", "danger"));
        app.Keywords("recurrence_frequency", "Recurrence", k => k
            .Add("daily", "Daily").Add("weekly", "Weekly").Add("monthly", "Monthly").Add("yearly", "Yearly"));
        app.Keywords("recurrence_end", "Recurrence ends", k => k
            .Add("never", "No end date").Add("after", "After a number of occurrences").Add("on_date", "On a date"));
        app.Keywords("recurrence_status", "Schedule status", k => k
            .Add("active", "Active", "success").Add("paused", "Paused", "warning").Add("ended", "Ended", "secondary"));
        app.Keywords("meeting_type", "Meeting type", k => k
            .Add("project_review", "Project review").Add("design_review", "Design review").Add("gate_review", "Gate review")
            .Add("ehs_committee", "EHS committee").Add("management_review", "Management review").Add("staff", "Staff meeting"));

        DefineProject(app);
        DefineTask(app);
        DefineRecurring(app);
        DefineMeeting(app);
        DefineViews(app);
    }

    public Task SeedDemoAsync(DemoContext demo) => ProjectsDemo.SeedAsync(demo);

    private static void DefineProject(AppBuilder app)
    {
        app.Form("project", "Project", f => f
            .Icon("bi-kanban")
            .Description("A stage-gate project: Concept → Feasibility → Development → Validation → Launch, each gate signed by the gate approvers.")
            .Numbering("PRJ-")
            .Title("{project_name}")
            .Section("overview", "Project", s =>
            {
                s.Text("project_name", "Project name", 8).Required().MaxLength(200).Placeholder("For example: VX-300 infusion pump");
                s.Text("project_code", "Code", 4).MaxLength(30);
                s.Keyword("project_type", "Type", "project_type").Required().Header();
                s.Text("product", "Product / process", 8);
                s.Keyword("site", "Lead site", "ops_site").Header();
                s.User("project_manager", "Project manager").Required().Header().Default("$me");
                s.User("sponsor", "Executive sponsor").Required();
                s.Users("gate_approvers", "Gate approvers", 8).Required()
                    .Help("Everyone listed approves each gate with an e-signature; the project moves on when all have signed. Include the project manager.");
                s.Users("team", "Core team", 12);
                s.Date("start_date", "Start date").Required().Default("$today");
                s.Date("target_launch", "Target launch / completion").Required().Header();
                s.TextArea("objectives", "Objectives and scope").Required();
                s.TextArea("business_case", "Business case");
            }, icon: "bi-kanban")
            .Subform("gate_reviews", "Gate reviews", s =>
            {
                s.Keyword("gate", "Stage", "npi_stage", 2).Required();
                s.Date("review_date", "Review date", 2).Required();
                s.Keyword("decision", "Decision", "gate_decision", 2).Required();
                s.Text("summary", "Deliverables reviewed / summary", 4).Required();
                s.Text("conditions", "Conditions", 2);
            }, tab: "Gates", rowNoun: "gate review", icon: "bi-signpost-2",
               description: "Record the gate review of the current stage before the gate approvers sign. A gate passes only with a Go or Go-with-conditions decision for that stage.")
            .Section("status", "Status", s =>
            {
                s.Keyword("risk_status", "Schedule risk", "project_risk").ReadOnly().Header()
                    .Help("Calculated (PRJ-008): High when a task is overdue, Low when a task is due within 3 days, otherwise No risk.");
                s.Number("percent_complete", "Tasks complete (%)", 3).ReadOnly().Decimals(0);
                s.Number("open_tasks", "Open tasks", 3).ReadOnly().Decimals(0);
                s.Number("overdue_tasks", "Overdue tasks", 3).ReadOnly().Decimals(0);
                s.TextArea("status_summary", "Status summary").Placeholder("Progress this period, risks and help needed");
            }, icon: "bi-activity")
            .Section("closure", "Closure", s =>
            {
                s.Date("actual_completion", "Actual completion").RequiredIn("launch");
                s.TextArea("lessons_learned", "Lessons learned").RequiredIn("launch");
            }, icon: "bi-flag")
            .Workflow("project", "Stage-gate project", w =>
            {
                w.Start("concept", "Concept").Purpose("Define the opportunity, objectives, business case and team. The Concept gate approves feasibility work.")
                    .AssignField("gate_approvers").Voting(100).DueFrom("target_launch").Edit("overview", "gate_reviews", "status").Hide("closure");
                w.Phase("feasibility", "Feasibility").Purpose("Show the product and process can be made: design inputs, preliminary hazard analysis, supply chain. The gate approves development.")
                    .AssignField("gate_approvers").Voting(100).DueFrom("target_launch").Edit("overview", "gate_reviews", "status").Hide("closure");
                w.Phase("development", "Development").Purpose("Design and develop the product and process: design outputs, FMEA, supplier qualification, pilot builds. The gate approves validation.")
                    .AssignField("gate_approvers").Voting(100).DueFrom("target_launch").Edit("overview", "gate_reviews", "status").Hide("closure");
                w.Phase("validation", "Validation").Purpose("Verify and validate the design and process: design verification, IQ/OQ/PQ, usability. The gate approves launch.")
                    .AssignField("gate_approvers").Voting(100).DueFrom("target_launch").Edit("overview", "gate_reviews", "status").Hide("closure");
                w.Phase("launch", "Launch").Purpose("Transfer to production and launch. Close the project when the launch criteria are met and the lessons are captured.")
                    .AssignField("project_manager").DueFrom("target_launch").Edit("overview", "gate_reviews", "status", "closure");
                w.Completed("closed", "Closed");
                w.Voided("cancelled", "Stopped");

                w.Forward("concept", "feasibility", label: "Approve Concept gate", sign: "Gate approved: Concept");
                w.Forward("feasibility", "development", label: "Approve Feasibility gate", sign: "Gate approved: Feasibility");
                w.Forward("development", "validation", label: "Approve Development gate", sign: "Gate approved: Development");
                w.Forward("validation", "launch", label: "Approve Validation gate", sign: "Gate approved: Validation");
                w.Forward("launch", "closed", label: "Close project", sign: "Project closed");
                w.Back("feasibility", "concept", label: "Recycle to Concept");
                w.Back("development", "feasibility", label: "Recycle to Feasibility");
                w.Back("validation", "development", label: "Recycle to Development");
                w.Back("launch", "validation", label: "Back to Validation");
                foreach (var stage in Stages) w.Void(stage, "cancelled", label: "Stop project");
            }));
    }

    private static void DefineTask(AppBuilder app)
    {
        app.Form("task", "Task", f => f
            .Icon("bi-check2-circle")
            .Description("A piece of project work or an occurrence of a recurring task (PRJ-007, TSK-001).")
            .Numbering("TSK-")
            .Activity("project", "recurring_task")
            .Title("{subject}")
            .Section("task", "Task", s =>
            {
                s.Text("subject", "Task", 12).Required().MaxLength(200);
                s.TextArea("description", "Description");
                s.User("assignee", "Assigned to").Required().Header();
                s.Keyword("stage", "Project stage", "npi_stage").Header().Help("For project tasks: the gate this task must be closed before.");
                s.Keyword("priority", "Priority", "priority").Default("medium");
                s.Date("start_date", "Start").Default("$today");
                s.Date("due_date", "Due").Required().Header().Default("$today+14");
                s.Number("percent_complete", "% complete", 3).Min(0).Max(100).Decimals(0).Default("0");
                s.Text("deliverable", "Deliverable / evidence", 9).Placeholder("Document number, report or link");
                s.Date("occurrence_date", "Scheduled occurrence").ReadOnly();
                s.YesNo("late_generated", "Late-generated", 3).ReadOnly().Help("Created after its scheduled date because the scheduler caught up a missed run (TSK-005).");
            }, icon: "bi-check2-circle")
            .Section("result", "Result", s =>
            {
                s.TextArea("outcome", "What was done").RequiredIn("open");
            }, icon: "bi-clipboard-check")
            .Workflow("task", "Task", w =>
            {
                w.Start("open", "Open").Purpose("Do the work and record the result. Completing sets % complete to 100 (PRJ-007).")
                    .AssignField("assignee").DueFrom("due_date").Edit("task", "result");
                w.Completed("completed", "Completed");
                w.Completed("not_completed", "Not Completed");
                w.Voided();
                w.Forward("open", "completed", label: "Complete task");
                w.Forward("open", "not_completed", label: "Close as not completed", comment: true);
                w.Void("open");
            }));
    }

    private static void DefineRecurring(AppBuilder app)
    {
        app.Form("recurring_task", "Recurring Task", f => f
            .Icon("bi-arrow-repeat")
            .Description("A schedule that creates a task every day, week, month or year, catching up any occurrence missed during downtime (TSK-002..005).")
            .Numbering("RTK-")
            .NonWorkflow()
            .Title("{subject}")
            .Section("definition", "Schedule", s =>
            {
                s.Text("subject", "Task", 8).Required().MaxLength(160).Placeholder("For example: Weekly eyewash station and safety shower check, Kalamazoo");
                s.Keyword("recurrence_status", "Status", "recurrence_status").Required().Default("active").Header();
                s.TextArea("description", "Instructions");
                s.User("assignee", "Assign each task to").Required().Header();
                s.User("owner", "Schedule owner").Default("$me");
                s.Keyword("priority", "Priority", "priority").Default("medium");
                s.Keyword("frequency", "Repeats", "recurrence_frequency").Required().Header();
                s.Number("interval", "Every", 2).Required().Min(1).Max(366).Decimals(0).Default("1").Help("For example 2 with Weekly = every other week.");
                s.Date("start_date", "First occurrence").Required().Default("$today");
                s.Keyword("end_rule", "Ends", "recurrence_end").Required().Default("never");
                s.Number("end_after", "Number of occurrences", 3).Min(1).Max(10000).Decimals(0).VisibleWhen("end_rule=after");
                s.Date("end_on", "Last date").VisibleWhen("end_rule=on_date");
                s.Number("due_days", "Due (days after each occurrence)", 3).Required().Min(0).Max(365).Decimals(0).Default("7");
                s.Link("related_record", "Source record").Help("Optional: the compliance plan, requirement or record this schedule serves.");
            }, icon: "bi-arrow-repeat")
            .Section("schedule", "Generated", s =>
            {
                s.Date("next_occurrence", "Next occurrence").ReadOnly().Header();
                s.Number("generated_count", "Tasks generated", 3).ReadOnly().Decimals(0);
                s.Date("last_occurrence", "Last generated occurrence").ReadOnly();
                s.Number("late_count", "Late-generated (catch-up)", 3).ReadOnly().Decimals(0);
            }, icon: "bi-calendar-check",
               description: "Kept by the recurring task generator. Generated tasks are listed on the Activities tab."));
    }

    private static void DefineMeeting(AppBuilder app)
    {
        app.Form("meeting", "Meeting", f => f
            .Icon("bi-people")
            .Description("A meeting with agenda, attendance, minutes per agenda item and action items (MTG §19.3).")
            .Numbering("MTG-")
            .Title("{subject}")
            .Section("details", "Meeting", s =>
            {
                s.Text("subject", "Subject", 8).Required().MaxLength(200);
                s.Keyword("meeting_type", "Type", "meeting_type").Required().Header();
                s.DateTime("starts_at", "Starts").Required().Header();
                s.Number("duration_min", "Duration (minutes)", 2).Min(5).Max(600).Decimals(0).Default("60");
                s.Text("location", "Room / location", 6);
                s.User("chair", "Chair").Required().Header().Default("$me");
                s.User("minute_taker", "Minute taker").Required();
                s.Users("invitees", "Invitees", 12);
                s.Link("project", "Project", "project");
            }, icon: "bi-calendar-event")
            .Subform("agenda", "Agenda and minutes", s =>
            {
                s.Text("topic", "Agenda item", 3).Required();
                s.User("presenter", "Presenter", 2);
                s.TextArea("minutes", "Minutes / discussion", 5);
                s.Text("decision", "Decision", 2);
            }, rowNoun: "agenda item", required: true, icon: "bi-list-ol")
            .Section("attendance", "Attendance", s =>
            {
                s.Users("attendees", "Attendees", 6).Help("Filled from the invitees when the meeting starts (MTG-005); edit as needed.");
                s.Users("absentees", "Absentees", 6);
            }, icon: "bi-person-check")
            .Subform("actions", "Action items", s =>
            {
                s.Text("action", "Action", 5).Required();
                s.User("assignee", "Assign to", 3).Required();
                s.Date("due", "Due", 2).Required();
                s.Text("agenda_ref", "Agenda item", 2);
            }, rowNoun: "action", icon: "bi-check2-square",
               description: "Each row becomes an Action Item when the minutes are published (MTG-006).")
            .Workflow("meeting", "Meeting", w =>
            {
                w.Start("planned", "Planned").Purpose("Set the date, the invitees and the agenda.").DueFrom("starts_at").Edit("details", "agenda").Hide("attendance", "actions");
                w.Phase("minutes", "Minutes").Purpose("Record attendance, the minutes and decisions per agenda item, and the action items. Publishing creates the action items.")
                    .AssignField("minute_taker").DueDays(3).Edit("details", "agenda", "attendance", "actions");
                w.Completed("completed", "Completed");
                w.Voided("voided", "Cancelled");
                w.Forward("planned", "minutes", label: "Start minutes");
                w.Void("planned", label: "Cancel meeting");
                w.Forward("minutes", "completed", label: "Publish minutes");
                w.Back("minutes", "planned", label: "Back to planning");
            }));
    }

    private static void DefineViews(AppBuilder app)
    {
        app.View("my_tasks", "My tasks", v => v.Category("1. My work").Forms("task").Mine()
            .Columns("number", "title", "due", "priority", "stage", "percent_complete").Sort("due", false));
        app.View("by_stage", "Projects by stage", v => v.Category("2. Projects").Forms("project").Open()
            .Columns("number", "title", "phase", "project_type", "project_manager", "target_launch", "percent_complete", "risk_status").Sort("phase", false).Default());
        app.View("at_risk", "Projects at risk", v => v.Category("2. Projects").Forms("project").Open().Filter("risk_status", "eq", "high")
            .Columns("number", "title", "phase", "overdue_tasks", "project_manager", "target_launch"));
        app.View("all_projects", "All projects", v => v.Category("2. Projects").Forms("project").All()
            .Columns("number", "title", "phase", "project_type", "site", "start_date", "target_launch").Sort("start_date"));
        app.View("open_tasks", "Open tasks", v => v.Category("3. Tasks").Forms("task").Open()
            .Columns("number", "title", "due", "assignee", "stage", "priority", "percent_complete").Sort("due", false));
        app.View("overdue_tasks", "Overdue tasks", v => v.Category("3. Tasks").Forms("task").Overdue()
            .Columns("number", "title", "due", "assignee", "stage", "priority").Sort("due", false));
        app.View("late_generated", "Late-generated tasks", v => v.Category("3. Tasks").Forms("task").All().Filter("late_generated", "eq", "yes")
            .Columns("number", "title", "phase", "occurrence_date", "due", "assignee").Sort("occurrence_date"));
        app.View("recurring", "Recurring task schedules", v => v.Category("3. Tasks").Forms("recurring_task").All()
            .Columns("number", "title", "frequency", "interval", "next_occurrence", "generated_count", "assignee", "recurrence_status").Sort("next_occurrence", false));
        app.View("completed_tasks", "Completed tasks", v => v.Category("3. Tasks").Forms("task").Closed()
            .Columns("number", "title", "phase", "assignee", "modified").Sort("modified"));
        app.View("upcoming_meetings", "Upcoming meetings", v => v.Category("4. Meetings").Forms("meeting").Open()
            .Columns("number", "title", "phase", "meeting_type", "starts_at", "chair").Sort("starts_at", false));
        app.View("completed_meetings", "Completed meetings", v => v.Category("4. Meetings").Forms("meeting").Completed()
            .Columns("number", "title", "meeting_type", "starts_at", "chair").Sort("starts_at"));
        app.View("cancelled_meetings", "Cancelled meetings", v => v.Category("4. Meetings").Forms("meeting").Voided()
            .Columns("number", "title", "starts_at", "modified"));
    }
}
