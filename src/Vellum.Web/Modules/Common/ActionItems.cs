using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Packaging;

namespace Vellum.Modules.Common;

/// <summary>
/// QE-001: the standard Action Item activity, available to every application (parent forms "*").
/// Workflow: Initiate → Open → (Verification, if required) → Completed, with back-routes and void.
/// </summary>
public sealed class ActionItemsApp : IPackagedApp
{
    public string Key => "actions";
    public int Order => 10;

    public void Define(AppBuilder app)
    {
        app.Name("Action Items")
            .Description("Assigned, tracked and verified actions raised from any quality, EHS or audit record.")
            .Icon("bi-check2-square").Color("#0d9488").Suite("Quality").Order(90).Version("1.0.0")
            .DefaultAccess(AppRole.Author)
            .Grant("@quality", AppRole.Manager);

        app.Keywords("verification_result", "Verification result", k => k
            .Add("verified", "Verified", "success")
            .Add("not_verified", "Not verified", "danger"));

        app.Form("action_item", "Action Item", f => f
            .Icon("bi-check2-square")
            .Description("A single assigned action with optional independent verification.")
            .Numbering("AI-")
            .Title("{subject}")
            .Activity("*")
            .Section("details", "Action", s =>
            {
                s.Text("subject", "Subject", 12).Required().MaxLength(200);
                s.TextArea("description", "What needs to be done");
                s.User("assignee", "Assigned to").Required().Header();
                s.Date("due_date", "Due date").Required().Header().Default("$today+14");
                s.YesNo("verification_required", "Verification required?").Default("no");
                s.User("verifier", "Verification by").VisibleWhen("verification_required=yes").RequiredIn("initiate");
                s.TextArea("verification_instructions", "Verification to be performed", 12).VisibleWhen("verification_required=yes");
            }, icon: "bi-card-checklist")
            .Section("action", "Action taken", s =>
            {
                s.TextArea("action_taken", "Action taken").RequiredIn("open");
                s.Date("completed_on", "Completed on").Default("$today").RequiredIn("open");
                s.Attachment("evidence", "Objective evidence");
            }, icon: "bi-tools")
            .Section("verification", "Verification", s =>
            {
                s.Keyword("verification_result", "Result", "verification_result").RequiredIn("verification");
                s.TextArea("verification_comments", "Verification comments");
            }, icon: "bi-shield-check")
            .Workflow("action_item", "Action Item", w =>
            {
                w.Start("initiate", "Initiate").Purpose("Describe the action, who does it and by when.").DueDays(2).Edit("details").Hide("action", "verification");
                w.Phase("open", "Open").Purpose("Carry out the action and record what was done.")
                    .AssignField("assignee").DueFrom("due_date").Edit("action").ReadOnly("details").Hide("verification");
                w.Phase("verification", "Verification").Purpose("Independently verify the action was effective.")
                    .AssignField("verifier").DueDays(5).Edit("verification").ReadOnly("details", "action");
                w.Completed("completed", "Completed");
                w.Voided();

                w.Forward("initiate", "open", label: "Assign action");
                w.Void("initiate");
                w.Forward("open", "verification", label: "Send for verification", when: "verification_required=yes");
                w.Forward("open", "completed", label: "Complete", sign: "Completed", when: "verification_required!=yes");
                w.Back("open", "initiate");
                w.Void("open");
                w.Forward("verification", "completed", label: "Verify and close", sign: "Verified", when: "verification_result=verified");
                w.Back("verification", "open", label: "Not verified: back to assignee");
            }));

        app.View("mine", "My open action items", v => v.Category("1. My work").Forms("action_item").Mine()
            .Columns("number", "title", "phase", "due", "assignee").Sort("due", false).Default());
        app.View("open", "All open action items", v => v.Category("2. Action items").Forms("action_item").Open()
            .Columns("number", "title", "phase", "due", "assignee", "created").Sort("due", false));
        app.View("overdue", "Overdue action items", v => v.Category("2. Action items").Forms("action_item").Overdue()
            .Columns("number", "title", "phase", "due", "assignee").Sort("due", false));
        app.View("verification", "Awaiting verification", v => v.Category("2. Action items").Forms("action_item").Open().Phases("verification")
            .Columns("number", "title", "due", "verifier", "assignee"));
        app.View("completed", "Completed action items", v => v.Category("3. Closed").Forms("action_item").Closed()
            .Columns("number", "title", "phase", "assignee", "completed_on", "modified").Sort("modified"));
    }
}

/// <summary>Keeps the Open phase due date in step with the action's due date field.</summary>
public sealed class ActionItemBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["action_item"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        if (ctx.Proposed.Main.Bool("verification_required") == true && ctx.Proposed.Main.Ref("verifier") is { } v &&
            v == ctx.Proposed.Main.Ref("assignee"))
            ctx.Error("verifier", "The verifier must be someone other than the person doing the action (SAF-044).");
        if (ctx.Proposed.Main.Date("due_date") is { } d && ctx.IsNew && d < BusinessCalendar.Today)
            ctx.Error("due_date", "The due date can't be in the past.");
        return Task.CompletedTask;
    }
}
