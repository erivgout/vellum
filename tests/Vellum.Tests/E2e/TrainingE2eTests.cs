namespace Vellum.Tests.E2e;

/// <summary>
/// Training end to end through the pages: setting up training on a document, assignments at Awaiting Release, the trainee's
/// e-signed acknowledgment, the training-gated release and its override, and the training application's own records.
/// </summary>
[Collection("vellum")]
public class TrainingE2eTests(VellumFixture fx)
{
    /// <summary>jlee writes an Operations SOP that needs training, gated at the given completion percentage.</summary>
    private async Task<Page> TrainedSopAsync(Cast c, string title, int gatePercent)
    {
        var page = await (await c.As("jlee")).CreateAsync("document", f => f
            .Pick("f.doc_type", "procedure").Set("f.title", title)
            .Set("f.owner", fx.Uid("sortiz").ToString()).Set("f.reviewers", fx.Uids("dokafor", "tbrooks"))
            .Pick("f.department", "operations").Set("f.revision_reason", "New procedure for the line.")
            .Pick("f.training_required", "yes").Pick("f.training_gated", "yes").Set("f.training_gate_percent", gatePercent)
            .Attach("file.primary_file", $"{title}.pdf", E2e.Pdf(title), "application/pdf"));
        Assert.Equal("Draft", page.Phase());
        return page;
    }

    /// <summary>The author sets up the course from the document's Training menu. Returns the course page.</summary>
    private static async Task<Page> SetUpTrainingAsync(Page doc, int dueDays = 14)
    {
        Assert.Contains("setup_training", doc.Actions());
        var course = await doc.ActionAsync("setup_training", f => f.Set("a.due_days", dueDays));
        Assert.Equal("Course created. Check its applicability rules.", course.Toast);
        Assert.StartsWith("CRS-", course.Number());
        return course;
    }

    /// <summary>The assignment of a course for one trainee, found in their work queue.</summary>
    private static async Task<Page> AssignmentInWorkAsync(Browser trainee, string courseName)
    {
        var work = await trainee.GoAsync("/work");
        var link = work.All("a[href^='/records/']").FirstOrDefault(a => Page.Squash(a.Closest("tr, li, .vl-list-item, .vl-work-item")?.TextContent ?? a.TextContent).Contains(courseName))
            ?? throw new Xunit.Sdk.XunitException($"No assignment of '{courseName}' in /work.\n{work.Excerpt()}");
        return await trainee.GoAsync(link.GetAttribute("href")!);
    }

    [Fact] // DOC-061, DOC-063, TRN-040, TRN-044, TRN-071
    public async Task Training_set_up_on_a_document_is_assigned_at_awaiting_release_and_gates_the_release()
    {
        using var c = new Cast(fx);
        var title = $"Line start-up {E2e.Tag()}";
        var doc = await TrainedSopAsync(c, title, gatePercent: 50);
        var url = doc.Path;
        Assert.Contains(doc.All(".vl-doc-banner, .card").Select(e => e.TextContent), t => t.Contains("Training"));

        var course = await SetUpTrainingAsync(doc);
        course.ExpectText(title).ExpectText("Operations");
        var courseUrl = course.Path;
        Assert.DoesNotContain("setup_training", (await c.Open("jlee", url)).Actions());

        // Assignments are created when the revision reaches Awaiting Release (Operations: Alex Kim, Luis Garcia, Noel Flores).
        var awaiting = await c.ToAwaitingReleaseAsync(url);
        Assert.Contains(awaiting.Comments(), t => t.Contains("3 training assignments created"));
        var waiting = (await c.Open("pshah", url)).Banner("Waiting for training");
        Assert.Contains("0 of 3", waiting.Text);
        foreach (var who in new[] { "akim", "lgarcia", "nflores" }) Assert.Contains(c.Name(who), awaiting.Text);

        // Release is blocked until half of the trainees have completed (TRN-071).
        var blocked = await (await c.Open("pshah", url)).RouteAsync("Release: make effective");
        Assert.Contains(blocked.RouteErrors(), e => e.Contains("TRN-071") && e.Contains("0 of 3"));
        Assert.False((await c.Open("pshah", url)).IsEffective());

        // The trainee sees the assignment on the home page and in their work queue, and acknowledges it with a signature.
        var nflores = await c.As("nflores");
        var home = await nflores.GoAsync("/");
        Assert.Contains(home.All(".vl-list-item .ttl").Select(e => Page.Squash(e.TextContent)), t => t == title);
        var assignment = await AssignmentInWorkAsync(nflores, title);
        Assert.Equal("Assigned", assignment.Phase());
        Assert.StartsWith("TA-", assignment.Number());
        var assignmentUrl = assignment.Path;
        var wrong = await assignment.RouteAsync("Acknowledge: read and understood", password: "not-my-password");
        Assert.NotEmpty(wrong.RouteErrors());
        Assert.Equal("Assigned", (await nflores.GoAsync(assignmentUrl)).Phase());
        var done = await (await nflores.GoAsync(assignmentUrl)).RouteOkAsync("Acknowledge: read and understood");
        Assert.Equal("Completed", done.Phase());
        Assert.Contains("signed", done.One(".vl-sig-badge").TextContent);
        Assert.Null(done.Q("[data-save-button]"));
        Assert.Single((await nflores.GoAsync($"/apps/training?view=my_records&q={Uri.EscapeDataString(title)}")).RowsWith(title));
        Assert.Empty((await nflores.GoAsync($"/apps/training?view=my_pending&q={Uri.EscapeDataString(title)}")).RowsWith(title));

        // Someone else can't acknowledge for a trainee: the coordinator records completion instead (TRN-015).
        var lgarcia = await AssignmentInWorkAsync(await c.As("lgarcia"), title);
        var kpatel = await c.Open("kpatel", lgarcia.Path);
        Assert.Empty(kpatel.RouteOptions());
        Assert.Contains("complete_on_behalf", kpatel.Actions());

        // 1 of 3 (33%) is still short of 50%.
        Assert.Contains("1 of 3", (await c.Open("pshah", url)).Banner("Waiting for training").Text);
        var stillBlocked = await (await c.Open("pshah", url)).RouteAsync("Release: make effective");
        Assert.Contains(stillBlocked.RouteErrors(), e => e.Contains("TRN-071"));

        await (await AssignmentInWorkAsync(await c.As("akim"), title)).RouteOkAsync("Acknowledge: read and understood");
        Assert.Contains("Training gate met", (await c.Open("pshah", url)).Banners());
        var effective = await c.ReleaseAsync(url);
        effective.ExpectText(c.Name("nflores"));

        // The coordinator sees the course's progress.
        var coursePage = await c.Open("kpatel", courseUrl);
        coursePage.ExpectText(c.Name("akim")).ExpectText(c.Name("lgarcia")).ExpectText(c.Name("nflores"));
        Assert.Single((await (await c.As("kpatel")).GoAsync($"/apps/training?view=pending_by_course&q={Uri.EscapeDataString(title)}")).RowsWith(c.Name("lgarcia")));
    }

    [Fact] // TRN-071: Document Control can override the training gate with a signed justification
    public async Task Document_Control_overrides_the_training_gate_with_a_signed_justification()
    {
        using var c = new Cast(fx);
        var title = $"Override SOP {E2e.Tag()}";
        var doc = await TrainedSopAsync(c, title, gatePercent: 100);
        var url = doc.Path;
        await SetUpTrainingAsync(doc);
        await c.ToAwaitingReleaseAsync(url);

        Assert.DoesNotContain("override_gate", (await c.Open("jlee", url)).Actions());
        var pshah = await c.Open("pshah", url);
        Assert.Contains("override_gate", pshah.Actions());
        pshah.ExpectText("hazard H2");

        var noReason = await pshah.ActionAsync("override_gate", password: TestHelpers.Password);
        Assert.Contains(noReason.ActionErrors("override_gate"), e => e.Contains("justification"));
        var wrong = await (await c.Open("pshah", url)).ActionAsync("override_gate", password: "not-my-password", comment: "Line restart can't wait.");
        Assert.NotEmpty(wrong.ActionErrors("override_gate"));
        Assert.Contains("Waiting for training", (await c.Open("pshah", url)).Banners());

        const string why = "Trainees are briefed at shift start today; records follow within 2 days (deviation DEV-0042).";
        var overridden = await (await c.Open("pshah", url)).ActionAsync("override_gate", password: TestHelpers.Password, comment: why);
        Assert.Equal("Training gate overridden. The revision can now be released.", overridden.Toast);
        var banner = overridden.Banner("Training gate overridden");
        Assert.Contains(c.Name("pshah"), banner.Text);
        Assert.Contains(why, banner.Text);
        Assert.DoesNotContain("override_gate", overridden.Actions());

        var effective = await c.ReleaseAsync(url);
        Assert.Contains("Training gate override", effective.RevisionHistory()[0].Approvals);
        Assert.Contains(effective.All(".vl-sig-meaning, [data-sig-meaning], .vl-manifest li, li").Select(e => e.TextContent), t => t.Contains("Training gate override"));
    }

    [Fact] // TRN-003, TRN-013, TRN-041, INV-106, INV-109, TRN-015
    public async Task A_coordinator_runs_a_course_from_the_training_application()
    {
        using var c = new Cast(fx);
        var tag = E2e.Tag();
        var name = $"Forklift refresher {tag}";

        // Only coordinators and course owners maintain courses.
        var jlee = await c.As("jlee");
        var notAllowed = await (await jlee.NewAsync("course")).RecordForm().Set("f.course_name", name).Pick("f.course_type", "online").SubmitAsync();
        Assert.Contains(notAllowed.Errors, e => e.Contains("TRN-003"));

        // A blank applicability rule is dropped, never saved as "everyone" (INV-106): the course applies to nobody yet.
        var kpatel = await c.As("kpatel");
        var form = await kpatel.NewAsync("course");
        var blank = form.AddRow("applicability");
        var unscoped = await form.RecordForm().Set("f.course_name", $"Unscoped course {tag}").Pick("f.course_type", "online")
            .Set($"r.applicability.{blank}.job_title", "  ").SubmitAsync();
        Assert.StartsWith("CRS-", unscoped.Number());
        var nobody = await unscoped.ActionAsync("assign_employees");
        Assert.Contains(nobody.ActionErrors("assign_employees"), e => e.Contains("Nobody to assign"));

        form = await kpatel.NewAsync("course");
        var row = form.AddRow("applicability");
        var course = await form.RecordForm().Set("f.course_name", name).Pick("f.course_type", "online").Pick("f.evaluation", "acknowledgment")
            .Set("f.due_days", "10").Pick($"r.applicability.{row}.departments", "facilities").Set("f.named_employees", fx.Uid("nflores").ToString())
            .SubmitAsync();
        Assert.StartsWith("CRS-", course.Number());
        var courseUrl = course.Path;

        var assigned = await course.ActionAsync("assign_employees");
        Assert.Equal("2 assignments created.", assigned.Toast);
        var again = await (await kpatel.GoAsync(courseUrl)).ActionAsync("assign_employees");
        Assert.StartsWith("0 assignments created. 2 skipped", again.Toast);
        Assert.Contains($"{c.Name("nflores")}: already has a pending assignment", again.Toast); // INV-109

        var pending = await kpatel.GoAsync($"/apps/training?view=pending_by_course&q={Uri.EscapeDataString(name)}");
        Assert.Equal(2, pending.RowsWith(name).Count);
        Assert.Single(pending.RowsWith(c.Name("ebaker")));

        // ebaker completes; the coordinator voids nflores's assignment with a reason.
        var ebaker = await c.As("ebaker");
        await (await AssignmentInWorkAsync(ebaker, name)).RouteOkAsync("Acknowledge: read and understood");
        Assert.Single((await kpatel.GoAsync($"/apps/training?view=completed&q={Uri.EscapeDataString(name)}")).RowsWith(c.Name("ebaker")));

        var nflores = await AssignmentInWorkAsync(await c.As("nflores"), name);
        var asCoordinator = await kpatel.GoAsync(nflores.Path);
        var noReason = await asCoordinator.ActionAsync("void_assignment");
        Assert.Contains(noReason.ActionErrors("void_assignment"), e => e.Contains("justification"));
        var voided = await (await kpatel.GoAsync(nflores.Path)).ActionAsync("void_assignment", comment: "Not a forklift operator.");
        Assert.Equal("Assignment voided.", voided.Toast);
        Assert.Equal("Voided", voided.Phase());
        Assert.Single((await kpatel.GoAsync($"/apps/training?view=voided&q={Uri.EscapeDataString(name)}")).RowsWith(c.Name("nflores")));

        var courses = await kpatel.GoAsync($"/apps/training?view=courses_active&q={Uri.EscapeDataString(name)}");
        Assert.Single(courses.RowsWith(name));
    }
}
