using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Modules.Documents;
using Vellum.Modules.Training;
using Record = Vellum.Domain.Record;

namespace Vellum.Tests;

/// <summary>Training Management §11: assignments, acknowledgment, applicability and the document linkage.</summary>
[Collection("vellum")]
public class TrainingTests(VellumFixture fx)
{
    private static async Task<Record> NewCourseAsync(IServiceScope s, string name, string type = "online", List<Guid>? prerequisites = null)
    {
        var res = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = TrainingKeys.Course,
            Values = new()
            {
                ["course_name"] = name, ["course_type"] = type, ["evaluation"] = type == "online" ? "acknowledgment" : "attendance",
                ["owner"] = s.UserId("kpatel"), ["due_days"] = 14, ["enabled"] = true, ["prerequisites"] = prerequisites,
            },
        });
        Assert.True(res.Ok, string.Join("; ", res.Errors.Select(e => e.Message)));
        return res.Record!;
    }

    private static Task<CreateResult> AssignAsync(IServiceScope s, Guid course, string employee) =>
        s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = TrainingKeys.Assignment,
            Values = new() { ["employee"] = s.UserId(employee), ["course"] = course, ["due_date"] = BusinessCalendar.Today.AddDays(10) },
        });

    [Fact] // INV-109, TRN-042
    public async Task Duplicate_pending_assignment_is_rejected()
    {
        using var s = fx.As("kpatel");
        var course = await NewCourseAsync(s, "Duplicate check course");
        var first = await AssignAsync(s, course.Id, "nflores");
        Assert.True(first.Ok, string.Join("; ", first.Errors.Select(e => e.Message)));
        var second = await AssignAsync(s, course.Id, "nflores");
        Assert.False(second.Ok);
        Assert.Contains(second.Errors, e => e.Message.Contains("INV-109"));
        // The bulk path skips the duplicate instead of creating it.
        var (created, skipped) = await s.ServiceProvider.GetRequiredService<TrainingService>().AssignAsync(course.Id, [s.UserId("nflores"), s.UserId("akim")], null, "bulk");
        Assert.Single(created);
        Assert.Single(skipped);
    }

    [Fact] // TRN-003
    public async Task Only_coordinators_and_course_owners_create_assignments()
    {
        Guid course;
        using (var s = fx.As("kpatel")) course = (await NewCourseAsync(s, "Permission course")).Id;
        using var other = fx.As("jlee");
        var res = await AssignAsync(other, course, "nflores");
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("TRN-003"));
    }

    [Fact] // TRN-040, INV-030
    public async Task Trainee_acknowledges_with_a_signature_and_the_record_is_then_immutable()
    {
        Guid id;
        using (var s = fx.As("kpatel"))
        {
            var course = await NewCourseAsync(s, "Acknowledgment course");
            id = (await AssignAsync(s, course.Id, "nflores")).Record!.Id;
            Assert.False((await s.RouteAsync(id, TrainingKeys.Completed)).Ok); // only the trainee is assigned
        }
        using (var trainee = fx.As("nflores"))
        {
            Assert.False((await trainee.RouteAsync(id, TrainingKeys.Completed, password: "wrong")).Ok);
            DocumentControlTests.Ok(await trainee.RouteAsync(id, TrainingKeys.Completed));
            var sig = await trainee.Db().Signatures.AsNoTracking().SingleAsync(x => x.RecordId == id);
            Assert.Equal(TrainingKeys.SignAcknowledge, sig.Meaning);
            Assert.Equal(trainee.UserId("nflores"), sig.SignerId);
        }
        using var coordinator = fx.As("kpatel");
        var edit = await coordinator.ServiceProvider.GetRequiredService<RecordService>().UpdateAsync(id, new() { ["notes"] = "changed later" });
        Assert.False(edit.Ok);
        Assert.Contains(edit.Errors, e => e.Message.Contains("INV-030"));
    }

    [Fact] // INV-107
    public async Task Prerequisites_must_be_completed_first()
    {
        Guid basic, advanced;
        using (var s = fx.As("kpatel"))
        {
            var pre = await NewCourseAsync(s, "Prerequisite: cleanroom basics");
            var course = await NewCourseAsync(s, "Advanced aseptic filling", prerequisites: [pre.Id]);
            basic = (await AssignAsync(s, pre.Id, "akim")).Record!.Id;
            advanced = (await AssignAsync(s, course.Id, "akim")).Record!.Id;
        }
        using var t = fx.As("akim");
        var blocked = await t.RouteAsync(advanced, TrainingKeys.Completed);
        Assert.False(blocked.Ok);
        Assert.Contains(blocked.Errors, e => e.Message.Contains("INV-107"));
        DocumentControlTests.Ok(await t.RouteAsync(basic, TrainingKeys.Completed));
        DocumentControlTests.Ok(await t.RouteAsync(advanced, TrainingKeys.Completed));
    }

    [Fact] // TRN-040: instructor-led training isn't completed by acknowledgment
    public async Task Instructor_led_training_is_recorded_by_the_coordinator()
    {
        Guid id;
        using (var s = fx.As("kpatel")) id = (await AssignAsync(s, (await NewCourseAsync(s, "Forklift practical", "instructor_led")).Id, "ebaker")).Record!.Id;
        using var t = fx.As("ebaker");
        var res = await t.RouteAsync(id, TrainingKeys.Completed);
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("Instructor-led"));
    }

    [Theory] // INV-106: OR of rows, AND of fields, OR of values, empty rows never match
    [InlineData("Operations", "KZO", "operations", true)]
    [InlineData("Operations", "GAL", "operations", false)]
    [InlineData("Quality", "KZO", "quality", true)]
    [InlineData("Facilities", "KZO", "maintenance", false)]
    public void Applicability_rules_combine_as_specified(string dept, string site, string group, bool expected)
    {
        var rules = new List<ApplicabilityRule>
        {
            new(["Operations"], ["KZO", "BOS"], [], null),   // Operations at Kalamazoo or Boston
            new(["Quality", "QC Laboratory"], [], ["quality"], null), // Quality people in the quality group
            new([], [], [], null),                           // empty: ignored
        };
        var e = new EmployeeAttributes(1, dept, site, "Technician", [group]);
        Assert.Equal(expected, ApplicabilityRule.Applies(rules, e));
        Assert.True(new ApplicabilityRule([], [], [], "techn").Matches(e));
        Assert.False(new ApplicabilityRule([], [], [], null).Matches(e));
    }

    [Fact] // INV-106: every rule row has at least one field
    public async Task Empty_applicability_rows_are_rejected()
    {
        using var s = fx.As("kpatel");
        var res = await s.ServiceProvider.GetRequiredService<RecordService>().CreateAsync(new CreateRequest
        {
            FormKey = TrainingKeys.Course,
            Values = new() { ["course_name"] = "Empty rule course", ["course_type"] = "online", ["evaluation"] = "acknowledgment", ["owner"] = s.UserId("kpatel"), ["due_days"] = 7 },
            RowValues = new() { ["applicability"] = [new() { ["job_title"] = "" }] },
        });
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.Message.Contains("INV-106"));
    }

    [Fact] // DOC-061, DOC-063, TRN-071
    public async Task Awaiting_release_creates_assignments_and_gated_release_waits_for_training()
    {
        Guid doc;
        using (var s = fx.As("jlee"))
            doc = (await DocumentControlTests.NewDocAsync(s, "Gated SOP", training: true, extra: new() { ["training_gated"] = true, ["training_gate_percent"] = 100 })).Id;
        using (var owner = fx.As("sortiz"))
        {
            var snap = await owner.LoadAsync(doc);
            await owner.ServiceProvider.GetRequiredService<TrainingService>().CreateCourseForDocumentAsync(snap, "Gated SOP training", "online", 10, 24, []);
        }
        await DocumentControlTests.ToAwaitingReleaseAsync(fx, doc);

        List<AssignmentRow> rows;
        using (var c = fx.As("kpatel"))
        {
            var t = c.ServiceProvider.GetRequiredService<TrainingService>();
            var course = (await t.CourseIdsForDocumentAsync(doc)).Single();
            rows = await t.RowsAsync(t.AssignmentsOf(course));
            // Applicability came from the document's department (Operations): Alex Kim, Luis Garcia, Noel Flores.
            Assert.Equal(new[] { "akim", "lgarcia", "nflores" }.Select(c.UserId).Order(), rows.Select(r => r.EmployeeId!.Value).Order());
            var materials = await c.Db().Attachments.CountAsync(a => a.RecordId == course && a.FieldKey == "materials" && !a.Removed);
            Assert.Equal(1, materials);
        }

        using (var dc = fx.As("pshah"))
        {
            var blocked = await dc.RouteAsync(doc, DocKeys.Approved);
            Assert.False(blocked.Ok);
            Assert.Contains(blocked.Errors, e => e.Message.Contains("TRN-071"));
        }
        foreach (var r in rows)
        {
            using var trainee = fx.As(fx.Services.GetRequiredService<Vellum.Engine.Security.UserDirectory>().Get(r.EmployeeId!.Value)!.Username);
            DocumentControlTests.Ok(await trainee.RouteAsync(r.Id, TrainingKeys.Completed));
        }
        using (var dc = fx.As("pshah")) DocumentControlTests.Ok(await dc.RouteAsync(doc, DocKeys.Approved));
    }
}
