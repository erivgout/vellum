using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Modules.Documents;
using Vellum.Packaging;

namespace Vellum.Modules.Training;

/// <summary>
/// Demo data beyond the document-linked courses (which Document Control's demo creates through Set up training and DOC-063):
/// a GDP course with completions and an overdue trainee, an instructor-led course completed on behalf by the coordinator,
/// and a course with a prerequisite.
/// </summary>
public sealed partial class TrainingApp
{
    public async Task SeedDemoAsync(DemoContext d)
    {
        var t = d.Services.GetRequiredService<TrainingService>();

        var gdp = await CourseAsync(d, t, "Good documentation practices (GDP)",
            "ALCOA+ principles for paper and electronic records: attributable, legible, contemporaneous, original and accurate. Correcting entries, signing and dating.",
            "online", 24, 30, [new() { ["groups"] = new List<string> { "quality", "operations", "qc-lab" } }]);
        await AssignAsync(d, t, gdp, DemoContext.Days(10), "Annual GMP curriculum 2026");
        await CompleteAsync(d, t, gdp, "jlee", "sortiz", "pshah", "hmoreau", "akim", "tbrooks", "cwu", "dokafor");
        await OverdueAsync(d, t, gdp, "ebaker", -4);

        var forklift = await CourseAsync(d, t, "Forklift operation and safety (practical)",
            "Classroom and practical assessment for powered industrial trucks (OSHA 1910.178). Recorded by the coordinator after the session.",
            "instructor_led", 36, 60, [new() { ["groups"] = new List<string> { "maintenance" } }, new() { ["job_title"] = "Operator" }]);
        await AssignAsync(d, t, forklift, DemoContext.Days(21), "Warehouse certification renewal");
        d.Db.ChangeTracker.Clear();
        var ebakerRow = (await t.RowsAsync(t.AssignmentsOf(forklift))).First(r => r.EmployeeId == d.User("ebaker"));
        await DemoActions.RunAsync<TrainingAssignmentBehavior>(d, ebakerRow.Id, "complete_on_behalf", "kpatel",
            comment: "Attended the 14 Oct session with instructor R. Daniels (Kalamazoo warehouse); practical assessment passed. Sign-in sheet on file.");

        var gowning = await LatestCourseAsync(d, "Cleanroom gowning and entry");
        var aseptic = await CourseAsync(d, t, "Aseptic connection technique",
            "Hands-on aseptic connection of ST-40 tubing sets inside the ISO 7 cleanroom. Requires cleanroom gowning training first.",
            "online", null, null, [new() { ["departments"] = new List<string> { "operations" }, ["sites"] = new List<string> { "kzo" }, ["job_title"] = "Operator" }],
            prerequisites: gowning == null ? null : [gowning.Value]);
        await AssignAsync(d, t, aseptic, DemoContext.Days(18), "New ST-40 line qualification");
    }

    private static async Task<Guid> CourseAsync(DemoContext d, TrainingService t, string name, string overview, string type, int? expMonths, int? retrainDays,
        List<Dictionary<string, object?>> rows, List<Guid>? prerequisites = null)
    {
        d.As("kpatel");
        d.Db.ChangeTracker.Clear();
        var res = await t.ElevatedAsync(() => d.Services.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = TrainingKeys.Course, SkipAccessCheck = true, CreatedAt = DateTime.UtcNow.AddDays(-40),
            Values = new()
            {
                ["course_name"] = name, ["overview"] = overview, ["course_type"] = type, ["evaluation"] = type == "online" ? "acknowledgment" : "attendance",
                ["owner"] = d.User("kpatel"), ["due_days"] = 21, ["expiration_months"] = expMonths, ["retraining_days"] = retrainDays, ["enabled"] = true,
                ["automate_next_revision"] = false, ["prerequisites"] = prerequisites,
            },
            RowValues = new() { ["applicability"] = rows },
        }));
        if (!res.Ok) throw new InvalidOperationException($"Demo course {name} failed: {string.Join("; ", res.Errors.Select(e => e.Message))}");
        return res.Record!.Id;
    }

    private static async Task AssignAsync(DemoContext d, TrainingService t, Guid course, DateOnly due, string reason)
    {
        d.As("kpatel");
        d.Db.ChangeTracker.Clear();
        var c = await t.CourseAsync(course) ?? throw new InvalidOperationException("Demo course missing.");
        var people = await t.ApplicableAsync(c);
        await t.AssignAsync(course, people.Select(p => p.Id), due, $"{reason} (assigned by Kiran Patel)");
    }

    private static async Task CompleteAsync(DemoContext d, TrainingService t, Guid course, params string[] users)
    {
        d.Db.ChangeTracker.Clear();
        var rows = await t.RowsAsync(t.AssignmentsOf(course));
        foreach (var u in users)
            if (rows.FirstOrDefault(r => r.EmployeeId == d.User(u) && r.PhaseType == PhaseType.Normal) is { } row)
                await d.RouteAsync(row.Id, TrainingKeys.Completed, asUser: u);
    }

    private static async Task OverdueAsync(DemoContext d, TrainingService t, Guid course, string user, int days)
    {
        d.Db.ChangeTracker.Clear();
        var row = (await t.RowsAsync(t.AssignmentsOf(course))).FirstOrDefault(r => r.EmployeeId == d.User(user) && r.PhaseType == PhaseType.Normal);
        if (row == null) return;
        var due = DemoContext.Days(days);
        await d.SetDueAsync(row.Id, due);
        await d.Db.RecordValues.Where(v => v.RecordId == row.Id && v.FieldKey == "due_date" && v.RowId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Date, due.ToDateTime(TimeOnly.MinValue)));
    }

    private static async Task<Guid?> LatestCourseAsync(DemoContext d, string name)
    {
        var formId = d.Config.Form(TrainingKeys.Course).Id;
        return await d.Db.Records.AsNoTracking().Where(r => r.FormId == formId && r.Title == name)
            .OrderByDescending(r => r.Sequence).Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
    }
}
