using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellum.Tests.E2e;

/// <summary>
/// Application views, search and favorites, the Insights dashboards, chart builder and alerts, and the home page, end to end
/// through the web pages. Records are created through the UI with a unique tag so other tests' data never matches.
/// </summary>
[Collection("vellum")]
public class InsightsViewsE2eTests(VellumFixture fx)
{
    private static Task<Page> NcrAsync(Browser b, string subject, string severity, string part) =>
        b.CreateAsync("ncr", f => f.Set("f.subject", subject).Pick("f.nc_type", "in_process").Pick("f.severity", severity).Set("f.part_number", part));

    private static Task<Page> ComplaintAsync(Browser b, string subject, string product, string org, string channel = "phone") =>
        b.CreateAsync("complaint", f => f.Set("f.subject", subject).Pick("f.category", "product_quality").Pick("f.site_reported", "boston")
            .Pick("f.channel", channel).Set("f.product", product).Set("f.reporter_organization", org).Set("f.description", "Found during the end-to-end test."));

    // ------------------------------------------------------------------ application views (PLT §2.11)

    [Fact] // PLT-1101..1103, INV-081
    public async Task The_record_list_searches_filters_sorts_and_exports_the_same_records()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var a = (await NcrAsync(jlee, $"{tag} burr on housing", "minor", $"PN-{tag}-3")).Number();
        var b = (await NcrAsync(jlee, $"{tag} cracked boss", "major", $"PN-{tag}-1")).Number();
        var c = (await NcrAsync(jlee, $"{tag} missing thread", "critical", $"PN-{tag}-2")).Number();

        // The default view is "My NCRs"; the navigator switches to the open list.
        var mine = await jlee.GoAsync("/apps/nonconformance");
        Assert.Equal("My NCRs", Page.Squash(mine.One(".vl-viewnav a.active .text-truncate").TextContent));
        Assert.Contains(a, mine.Numbers());
        var open = await mine.ClickAsync("Open NCRs");
        Assert.Equal("Open NCRs", Page.Squash(open.One(".vl-viewnav a.active .text-truncate").TextContent));

        // Search box (number or title).
        var searched = await open.Form("form.vl-toolbar").Set("q", tag).SubmitAsync();
        Assert.Equal(new[] { a, b, c }.Order(), searched.Numbers().Order());
        Assert.Equal("1–3 of 3", Page.Squash(searched.One(".vl-table-foot .tabular").TextContent));
        Assert.Equal([b], (await open.Form("form.vl-toolbar").Set("q", b).SubmitAsync()).Numbers());

        // Quick filter (f.severity) on top of the search.
        var major = await searched.Form("form.vl-toolbar").Pick("f.severity", "major").SubmitAsync();
        Assert.Equal([b], major.Numbers());
        Assert.Contains("f.severity=major", major.Path);
        major.ExpectText("Major").ExpectNoText($"{tag} burr on housing");

        // Export matches what the list shows (PLT-1103).
        var csv = DesignerSteps.Csv(await jlee.DownloadAsync(major.Link("Export").GetAttribute("href")!));
        Assert.Contains("Severity", csv[0]);
        Assert.Equal([b], csv.Skip(1).Select(r => r[csv[0].IndexOf("Number")]).ToList());
        Assert.Equal("Major", csv[1][csv[0].IndexOf("Severity")]);
        var all = DesignerSteps.Csv(await jlee.DownloadAsync(searched.Link("Export").GetAttribute("href")!));
        Assert.Equal(new[] { a, b, c }.Order(), all.Skip(1).Select(r => r[0]).Order());

        // Sorting by a column header keeps the search, and a second click reverses it.
        var byPart = await jlee.GoAsync(searched.One("th a[href*='sort=part_number']").GetAttribute("href")!);
        Assert.Equal([b, c, a], byPart.Numbers());
        Assert.Equal("ascending", byPart.One("th[aria-sort]").GetAttribute("aria-sort"));
        var reversed = await jlee.GoAsync(byPart.One("th a[href*='sort=part_number']").GetAttribute("href")!);
        Assert.Equal([a, c, b], reversed.Numbers());

        // Clear drops the search and filters.
        var cleared = await major.ClickAsync("Clear");
        Assert.DoesNotContain("q=", cleared.Path);
        Assert.DoesNotContain("f.severity", cleared.Path);
    }

    [Fact] // DSN-702..706, PLT-1101: a view built in the view designer, with its own columns, filter and page size.
    public async Task A_custom_view_from_the_view_designer_pages_through_its_records()
    {
        using var admin = await fx.SignedInAsync("admin");
        var key = await DesignerE2eTests.PublishedAppAsync(admin, "Loaner Pool");
        await admin.SaveFormAsync(key, key, form =>
        {
            var site = DesignerSteps.NewField("site", "Site", "Keyword");
            site["keywordList"] = "quality_site";
            form.Section("details")["fields"]!.AsArray().Add(site);
        });
        var app = await admin.GoAsync($"/designer/apps/{key}");
        var created = await app.Form($"form[action='/designer/apps/{key}?handler=NewView']").Set("Name", "Boston loaners").Set("Category", "2. Records").SubmitAsync();
        Assert.Equal($"/designer/apps/{key}/views/boston_loaners", created.Url.AbsolutePath);
        await admin.SaveViewAsync(key, "boston_loaners", v =>
        {
            v["columns"] = DesignerSteps.Strings("number", "title", "site", "phase");
            v["filters"] = new JsonArray(new JsonObject { ["field"] = "site", ["op"] = "eq", ["value"] = "boston" });
            v["pageSize"] = 5;
            v["sortBy"] = "number";
            v["sortDesc"] = false;
        });
        await admin.PublishAsync(key, "Boston loaner view");

        using var jlee = await fx.SignedInAsync("jlee");
        var numbers = new List<string>();
        for (var i = 1; i <= 7; i++)
            numbers.Add((await jlee.CreateAsync(key, f => f.Set("f.title", $"Loaner {i}").Pick("f.site", i == 4 ? "galway" : "boston"))).Number());
        var boston = numbers.Where((_, i) => i != 3).ToList();

        var list = await jlee.GoAsync($"/apps/{key}");
        var view = await list.ClickAsync("Boston loaners");
        Assert.Equal(["Number", "Title", "Site", "Phase"], view.All("thead th").Select(t => Page.Squash(t.TextContent)).ToList());
        Assert.Equal(boston.Take(5), view.Numbers());
        Assert.Equal("1–5 of 6", Page.Squash(view.One(".vl-table-foot .tabular").TextContent));
        Assert.Equal("6", Page.Squash(view.One(".vl-viewnav a.active .vl-pill-count").TextContent));

        var page2 = await jlee.GoAsync(view.One(".pagination a[aria-label=Next]").GetAttribute("href")!);
        Assert.Equal(boston.Skip(5), page2.Numbers());
        Assert.Equal("6–6 of 6", Page.Squash(page2.One(".vl-table-foot .tabular").TextContent));
        var back = await jlee.GoAsync(page2.One(".pagination a[aria-label=Previous]").GetAttribute("href")!);
        Assert.Equal(boston.Take(5), back.Numbers());

        var csv = DesignerSteps.Csv(await jlee.DownloadAsync(view.Link("Export").GetAttribute("href")!));
        Assert.Equal(boston, csv.Skip(1).Select(r => r[0]).ToList());
        Assert.All(csv.Skip(1), r => Assert.Equal("Boston, MA", r[2]));
    }

    [Fact] // INV-081, PLT-1103, PLT-1504: lists, exports and search never show records the user can't open.
    public async Task Lists_exports_and_search_are_security_trimmed()
    {
        var tag = E2e.Tag();
        // An application with no default access: only its creator (the administrator, as manager) can read it.
        using var admin = await fx.SignedInAsync("admin");
        var key = await DesignerE2eTests.PublishedAppAsync(admin, "Legal Holds", access: null);
        var name = Page.Squash((await admin.GoAsync($"/apps/{key}")).One(".vl-page-title").TextContent);
        var record = await admin.CreateAsync(key, f => f.Set("f.title", $"Litigation hold {tag}"));
        var number = record.Number();
        Assert.Contains(number, (await admin.GoAsync($"/apps/{key}?view=open&q={tag}")).Numbers());
        (await admin.GoAsync($"/search?q={tag}")).ExpectText(number);
        Assert.Contains(number, DesignerSteps.Csv(await admin.DownloadAsync($"/apps/{key}/export.csv?view=open")).Skip(1).Select(r => r[0]));

        using var jlee = await fx.SignedInAsync("jlee");
        await jlee.GoAsync($"/apps/{key}", HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await jlee.RawGetAsync($"/apps/{key}/export.csv?view=open")).StatusCode);
        await jlee.GoAsync(record.Path, HttpStatusCode.NotFound);
        (await jlee.GoAsync($"/search?q={tag}")).ExpectText($"No results for “{tag}”").ExpectNoText(number);
        (await jlee.GoAsync($"/search?q={Uri.EscapeDataString(number)}")).ExpectNoText("Litigation hold");
        var quick = await jlee.QuickSearchAsync(number);
        Assert.DoesNotContain(quick, i => i.Label.Contains(number) || i.Label.Contains(tag));
        Assert.DoesNotContain(await jlee.QuickSearchAsync(name), i => i.Href == $"/apps/{key}");
        var lookup = JsonDocument.Parse(await jlee.DownloadAsync($"/api/lookup/records?q={tag}")).RootElement;
        Assert.Equal(0, lookup.GetArrayLength());
        (await jlee.GoAsync("/apps")).ExpectNoText(name);
        Assert.Empty((await jlee.GoAsync($"/insights/records?app={key}&st=all")).Numbers());
        await jlee.GoAsync($"/insights/apps/{key}", HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ search and favorites (PLT §2.15)

    [Fact] // PLT-1501..1504
    public async Task Search_finds_records_by_number_and_title_and_the_quick_search_offers_them()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var ncr = await NcrAsync(jlee, $"Flash on gate {tag}", "minor", $"PN-{tag}");
        var number = ncr.Number();

        using var sortiz = await fx.SignedInAsync("sortiz");
        var byNumber = await sortiz.GoAsync($"/search?q={Uri.EscapeDataString(number)}");
        var hit = byNumber.All(".vl-list-item").Single(h => h.TextContent.Contains(number));
        Assert.Equal(ncr.Path, hit.GetAttribute("href"));
        Assert.Contains($"Flash on gate {tag}", hit.TextContent);
        Assert.Equal(number, Page.Squash(hit.QuerySelector("mark")!.TextContent));

        var byTitle = await sortiz.GoAsync($"/search?q={Uri.EscapeDataString("gate " + tag)}");
        Assert.Contains(number, byTitle.Numbers());
        Assert.Equal("1", Page.Squash(byTitle.One(".vl-viewnav a.active .vl-pill-count").TextContent));
        var inApp = await sortiz.GoAsync(byTitle.One(".vl-viewnav a[href*='app=nonconformance']").GetAttribute("href")!);
        Assert.Contains("app=nonconformance", inApp.Path);
        Assert.Contains(number, inApp.Numbers());
        (await sortiz.GoAsync($"/search?q={tag}&app=capa")).ExpectNoText(number);

        // The command palette's quick search (Ctrl K).
        var quick = await sortiz.QuickSearchAsync(number);
        var item = Assert.Single(quick, i => i.Kind == "record");
        Assert.Equal($"{number} · Flash on gate {tag}", item.Label);
        Assert.Equal(ncr.Path, item.Href);
        var apps = await sortiz.QuickSearchAsync("Nonconf");
        Assert.Contains(apps, i => i.Kind == "app" && i.Href == "/apps/nonconformance");
        Assert.Empty(await sortiz.QuickSearchAsync("zz" + tag));
    }

    [Fact] // PLT-1105: favorites
    public async Task A_starred_record_and_view_appear_under_favorites()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var ncr = await NcrAsync(jlee, $"Sink mark {tag}", "minor", $"PN-{tag}");
        var star = ncr.One(".vl-star[data-fav-kind=record]");
        Assert.DoesNotContain("on", star.ClassList);

        var on = await jlee.PostJsonAsync("/api/favorites/toggle", new { kind = "record", refKey = star.GetAttribute("data-fav-key"), label = star.GetAttribute("data-fav-label") });
        Assert.True(on.Json.GetProperty("favorite").GetBoolean());
        var page = await jlee.GoAsync(ncr.Path);
        Assert.Contains("on", page.One(".vl-star[data-fav-kind=record]").ClassList);
        var fav = page.All(".vl-side-scroll a.vl-side-link").Single(a => a.TextContent.Contains($"Sink mark {tag}"));
        Assert.Equal(ncr.Path, fav.GetAttribute("href"));
        await jlee.GoAsync(fav.GetAttribute("href")!);

        // A view can be a favorite too.
        var list = await jlee.GoAsync("/apps/nonconformance?view=open");
        var viewStar = list.One(".vl-star[data-fav-kind=view]");
        await jlee.PostJsonAsync("/api/favorites/toggle", new { kind = "view", refKey = viewStar.GetAttribute("data-fav-key"), label = viewStar.GetAttribute("data-fav-label") });
        var home = await jlee.GoAsync("/");
        var favView = home.All(".vl-side-scroll a.vl-side-link").Single(a => a.TextContent.Contains(viewStar.GetAttribute("data-fav-label")!));
        (await jlee.GoAsync(favView.GetAttribute("href")!)).ExpectText("Open NCRs");

        // Other people's favorites are their own; starring again removes it.
        using var sortiz = await fx.SignedInAsync("sortiz");
        Assert.DoesNotContain((await sortiz.GoAsync(ncr.Path)).All(".vl-side-scroll a"), a => a.TextContent.Contains($"Sink mark {tag}"));
        var off = await jlee.PostJsonAsync("/api/favorites/toggle", new { kind = "record", refKey = star.GetAttribute("data-fav-key"), label = star.GetAttribute("data-fav-label") });
        Assert.False(off.Json.GetProperty("favorite").GetBoolean());
        Assert.DoesNotContain((await jlee.GoAsync("/")).All(".vl-side-scroll a"), a => a.TextContent.Contains($"Sink mark {tag}"));
        var viewOff = await jlee.PostJsonAsync("/api/favorites/toggle", new { kind = "view", refKey = viewStar.GetAttribute("data-fav-key"), label = viewStar.GetAttribute("data-fav-label") });
        Assert.False(viewOff.Json.GetProperty("favorite").GetBoolean());
    }

    // ------------------------------------------------------------------ Insights dashboards (RPT-701, RPT-403)

    [Fact] // RPT-701, RPT-704, RPT-403
    public async Task Dashboards_count_new_records_and_drill_through_to_them()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var home = await jlee.GoAsync("/insights?refresh=true");
        var openBefore = int.Parse(home.Kpi("Open items").Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var app = await jlee.GoAsync("/insights/apps/nonconformance?refresh=true");
        var ncrBefore = int.Parse(app.Kpi("Open").Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

        var numbers = new List<string>();
        var startPhase = "";
        foreach (var sev in new[] { "major", "major", "critical" })
        {
            var ncr = await NcrAsync(jlee, $"{tag} short shot", sev, $"PN-{tag}");
            numbers.Add(ncr.Number());
            startPhase = ncr.Phase();
        }

        // Cached numbers (RPT-704) update on refresh.
        home = await jlee.GoAsync("/insights?refresh=true");
        Assert.Equal(openBefore + 3, int.Parse(home.Kpi("Open items").Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
        var byApp = home.ReportRow("Open items by application", "Nonconformance (NCR)");
        Assert.NotNull(byApp[1].Href);

        app = await jlee.GoAsync("/insights/apps/nonconformance?refresh=true");
        var open = app.Kpi("Open");
        Assert.Equal(ncrBefore + 3, int.Parse(open.Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
        app.ExpectText("Open by phase").ExpectText("Pareto by severity");

        // Every number opens the records behind it, re-run for the viewer.
        var drill = await jlee.GoAsync(open.Href);
        Assert.All(numbers, n => Assert.Contains(n, drill.Numbers()));
        drill.ExpectText("Nonconformance").ExpectText("Open");
        var csv = DesignerSteps.Csv(await jlee.DownloadAsync(drill.Link("Export").GetAttribute("href")!));
        Assert.All(numbers, n => Assert.Contains(csv, r => r[0] == n));

        var phaseRow = app.ReportRow("Open by phase", startPhase);
        var inPhase = await jlee.GoAsync(phaseRow.First(c => c.Href != null).Href!);
        Assert.All(numbers, n => Assert.Contains(n, inPhase.Numbers()));
        inPhase.ExpectText($"Phase: {startPhase}");

        // The Pareto bar for Critical lists the critical NCR and not the major ones.
        var critical = app.ReportRow("Pareto by severity", "Critical");
        var critList = await jlee.GoAsync(critical[0].Href!);
        Assert.Contains(numbers[2], critList.Numbers());
        Assert.DoesNotContain(numbers[0], critList.Numbers());

        // A shared drill-through link is re-trimmed for whoever opens it.
        using var nflores = await fx.SignedInAsync("nflores");
        var theirs = await nflores.GoAsync("/insights/records?app=adverse_events&st=all");
        Assert.Empty(theirs.Numbers());
    }

    [Fact] // RPT-403: keyword fields that share a design name with another application's field drill through by their own list.
    public async Task Pareto_bars_drill_through_to_their_records_when_field_names_repeat_across_applications()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var phone = new List<string>();
        for (var i = 0; i < 2; i++) phone.Add((await ComplaintAsync(jlee, $"{tag} cracked housing {i}", $"VX-{tag}", $"Clinic {tag}")).Number());
        var email = (await ComplaintAsync(jlee, $"{tag} label smudged", $"VX-{tag}", $"Clinic {tag}", "email")).Number();

        // Complaints' "Received via" (complaint_channel) shares the design name "channel" with Customer feedback.
        var dash = await jlee.GoAsync("/insights/apps/complaints?refresh=true");
        var bar = dash.ReportRow("Pareto by received via", "Phone");
        var list = await jlee.GoAsync(bar[0].Href!);
        Assert.All(phone, n => Assert.Contains(n, list.Numbers()));
        Assert.DoesNotContain(email, list.Numbers());
        list.ExpectText("Received via: Phone");
        Assert.Equal(int.Parse(bar[0].Text, CultureInfo.InvariantCulture), list.Numbers().Count);
    }

    // ------------------------------------------------------------------ chart builder (RPT §7.1–7.2)

    [Fact] // RPT-101..105, RPT-403
    public async Task A_chart_is_built_viewed_edited_and_deleted()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var major = (await NcrAsync(jlee, $"{tag} weld spatter", "major", $"PN-{tag}")).Number();

        var builder = await jlee.GoAsync("/insights/charts/edit?app=nonconformance&view=open");
        Assert.Contains(builder.All("#ins-cat option"), o => o.GetAttribute("value") == "severity");
        var saved = await builder.Form("#ins-builder").Set("Title", $"Open NCRs by severity {tag}").Pick("Def.Category", "severity").Pick("Def.Type", "column").SubmitAsync();
        Assert.Matches(@"^/insights/charts/\d+$", saved.Url.AbsolutePath);
        Assert.StartsWith("Saved CH", saved.Toast);
        saved.ExpectText($"Open NCRs by severity {tag}");
        var majorRow = saved.ReportRow(null, "Major");
        Assert.True(int.Parse(majorRow[0].Text, CultureInfo.InvariantCulture) >= 1);
        var drill = await jlee.GoAsync(majorRow[0].Href!);
        Assert.Contains(major, drill.Numbers());
        var url = saved.Path;

        (await jlee.GoAsync("/insights/charts")).ExpectText($"Open NCRs by severity {tag}");

        // Edit: new title and chart type.
        var edit = await saved.ClickAsync("Edit");
        Assert.Equal("severity", edit.FormFieldValue("#ins-builder", "Def.Category"));
        var edited = await edit.Form("#ins-builder").Set("Title", $"NCR severity mix {tag}").Pick("Def.Type", "bar").SubmitAsync();
        Assert.Equal("Chart saved", edited.Toast);
        Assert.Equal(url, edited.Path);
        edited.ExpectText($"NCR severity mix {tag}").ExpectNoText($"Open NCRs by severity {tag}");

        // Only the owner (or an administrator) sees a chart document.
        using var nflores = await fx.SignedInAsync("nflores");
        await nflores.GoAsync(url, HttpStatusCode.NotFound);
        await nflores.GoAsync(url.Replace("/charts/", "/charts/edit/"), HttpStatusCode.NotFound);

        var deleted = await edited.Form("form[action$='handler=Delete']").SubmitAsync();
        Assert.Equal("/insights/charts", deleted.Url.AbsolutePath);
        Assert.StartsWith("Deleted CH", deleted.Toast);
        deleted.ExpectNoText($"NCR severity mix {tag}");
        await jlee.GoAsync(url, HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ alerts (RPT §7.6)

    [Fact] // RPT-601, RPT-602, RPT-604, RPT-605, RPT-607
    public async Task A_threshold_alert_reports_its_matches_and_notifies_its_recipients()
    {
        var tag = E2e.Tag();
        using var jlee = await fx.SignedInAsync("jlee");
        var numbers = new List<string>();
        for (var i = 0; i < 2; i++) numbers.Add((await NcrAsync(jlee, $"{tag} porosity {i}", "major", $"PN-{tag}")).Number());

        var editor = await jlee.GoAsync("/insights/alerts/edit?app=nonconformance");
        var created = await editor.Form("form[data-ins-alert]").Disable("[data-show='action=create']")
            .Set("Input.Name", $"Repeat NCRs on one part {tag}").Set("Input.View", "open").Set("Input.MatchColumns", "part_number")
            .Set("Input.ReportColumns", "title", "severity").Set("Input.MinMatches", 2).Set("Input.LookbackDays", 30)
            .Set("Input.Users", fx.Uid("sortiz").ToString()).Pick("Input.Action", "notify").SubmitAsync();
        Assert.Matches(@"^/insights/alerts/\d+$", created.Url.AbsolutePath);
        Assert.StartsWith("Created alert ALT-", created.Toast);
        created.ExpectText($"Repeat NCRs on one part {tag}").ExpectText("Part number").ExpectText("Number, title, Severity");
        var templateUrl = created.Path;
        var templateId = int.Parse(templateUrl.Split('/').Last(), CultureInfo.InvariantCulture);

        // RPT-605: a test report shows what would happen without sending anything.
        var test = await created.Form("form[action$='handler=Test']").SubmitAsync();
        Assert.Matches(@"^/insights/alerts/reports/\d+$", test.Url.AbsolutePath);
        test.ExpectText($"PN-{tag}").ExpectText("would notify");
        Assert.All(numbers, n => test.ExpectText(n));
        using var sortiz = await fx.SignedInAsync("sortiz");
        (await sortiz.GoAsync("/notifications")).ExpectNoText($"Repeat NCRs on one part {tag}");

        // The scheduled run notifies the recipient, who opens the report from the notification.
        var reportId = await fx.RunAlertAsScheduledAsync(templateId);
        var inbox = await sortiz.GoAsync("/notifications");
        var note = inbox.All("a[href]").First(a => a.TextContent.Contains($"Repeat NCRs on one part {tag}"));
        Assert.Equal($"/insights/alerts/reports/{reportId}", note.GetAttribute("href"));
        var report = await sortiz.GoAsync(note.GetAttribute("href")!);
        report.ExpectText($"PN-{tag}").ExpectText("Notified");
        Assert.All(numbers, n => Assert.Contains(report.All("a[href]"), a => Page.Squash(a.TextContent) == n));
        var json = JsonDocument.Parse(await sortiz.DownloadAsync($"/insights/alerts/reports/{reportId}?handler=Json")).RootElement;
        Assert.Contains(json.GetProperty("result").GetProperty("groups").EnumerateArray(), g => g.GetProperty("count").GetInt32() == 2);

        // RPT-607: the owner's alert list and reports tab; other people can't open the template or the report.
        (await jlee.GoAsync("/insights/alerts")).ExpectText($"Repeat NCRs on one part {tag}");
        (await jlee.GoAsync("/insights/alerts?tab=reports")).ExpectText($"Repeat NCRs on one part {tag}");
        using var nflores = await fx.SignedInAsync("nflores");
        await nflores.GoAsync(templateUrl, HttpStatusCode.NotFound);
        await nflores.GoAsync($"/insights/alerts/reports/{reportId}", HttpStatusCode.NotFound);
        await nflores.GoAsync($"/insights/alerts/edit/{templateId}", HttpStatusCode.NotFound);

        // A template with reports can only be disabled, not deleted (no Delete button, and a crafted post is refused).
        var page = await jlee.GoAsync(templateUrl);
        Assert.Null(page.Q("form[action$='handler=Delete']"));
        var refused = await page.Form("form[action$='handler=Toggle']").SubmitToAsync($"{templateUrl}?handler=Delete");
        Assert.Equal("This alert has reports, so it can't be deleted. Disable it instead.", refused.Toast);
        var disabled = await refused.Form("form[action$='handler=Toggle']").SubmitAsync();
        Assert.Equal("Alert disabled", disabled.Toast);
    }

    [Fact] // RPT-603, RPT-604, INV-127
    public async Task An_alert_creates_a_CAPA_linked_to_the_complaints_that_triggered_it()
    {
        var tag = E2e.Tag();
        var product = $"VX-{tag}";
        using var jlee = await fx.SignedInAsync("jlee");
        var complaints = new List<Page>();
        for (var i = 0; i < 3; i++) complaints.Add(await ComplaintAsync(jlee, $"{tag} occlusion alarm {i}", product, $"Clinic {tag}"));

        var editor = await jlee.GoAsync("/insights/alerts/edit?app=complaints");
        var form = editor.Form("form[data-ins-alert]").Disable("[data-show='action=notify']")
            .Set("Input.Name", $"Repeat complaints → CAPA {tag}").Set("Input.View", "open_by_assignee")
            .Set("Input.MatchColumns", "product", "reporter_organization").Set("Input.ReportColumns", "category")
            .Set("Input.MinMatches", 3).Set("Input.LookbackDays", 30).Pick("Input.Action", "create")
            .Set("Input.Create.App", "capa").Set("Input.Create.Form", "capa").Set("Input.Create.Phase", "capa/initiate").Set("Input.Create.DueDays", 5)
            .Set("Input.Create.Users", fx.Uid("jlee").ToString()).Set("Input.Create.NotifyUsers", fx.Uid("sortiz").ToString());
        var fields = new (string Field, string Value)[]
        {
            ("subject", "Recurring complaints: {group.product} ({count})"), ("capa_type", "corrective"), ("source", "Customer complaint"),
            ("priority", "high"), ("owner", "jlee"), ("product", "{group.product}"), ("source_record", "{records}"),
            ("description", "{count} complaints about {group.product} from {group.reporter_organization}: {records}."),
        };
        for (var i = 0; i < fields.Length; i++)
            form.Set($"Input.Create.Fields[{i}].Field", fields[i].Field).Set($"Input.Create.Fields[{i}].Value", fields[i].Value);
        var created = await form.SubmitAsync();
        Assert.True(created.Url.AbsolutePath.StartsWith("/insights/alerts/") && created.Toast?.StartsWith("Created alert") == true,
            $"Saving the alert failed: {string.Join(" | ", created.Errors)} {created.Alert}");
        created.ExpectText("CAPA").ExpectText("Recurring complaints: {group.product} ({count})");
        var templateId = int.Parse(created.Url.AbsolutePath.Split('/').Last(), CultureInfo.InvariantCulture);

        // Dry run first (RPT-605): it says what it would create and creates nothing.
        var test = await created.Form("form[action$='handler=Test']").SubmitAsync();
        test.ExpectText($"Recurring complaints: {product} (3)").ExpectText("Test");
        Assert.DoesNotContain((await jlee.GoAsync($"/search?q={Uri.EscapeDataString(product)}&app=capa")).Numbers(), n => n.StartsWith("CAPA"));

        var reportId = await fx.RunAlertAsScheduledAsync(templateId);
        var report = await jlee.GoAsync($"/insights/alerts/reports/{reportId}");
        report.ExpectText("Created");
        var capaLink = report.All("a[href^='/records/']").First(a => a.TextContent.Contains("CAPA-") || a.TextContent.Contains("Recurring complaints"));
        var capa = await jlee.GoAsync(capaLink.GetAttribute("href")!);
        Assert.Equal($"Recurring complaints: {product} (3)", capa.Title());
        Assert.Equal("Initiate", capa.Phase());
        var reportNumber = System.Text.RegularExpressions.Regex.Match(report.Text, @"ALR-\d+").Value;
        capa.ExpectText($"Clinic {tag}").ExpectText(reportNumber);
        foreach (var c in complaints) Assert.Contains(capa.All("a[href]"), a => a.GetAttribute("href") == c.Path);

        // The complaint links back to the CAPA, and the assignee and notify list see it.
        var complaint = await jlee.GoAsync(complaints[0].Path);
        Assert.Contains(complaint.All("a[href]"), a => a.GetAttribute("href") == capa.Path);
        (await jlee.GoAsync("/work")).ExpectText(capa.Number());
        using var sortiz = await fx.SignedInAsync("sortiz");
        Assert.Contains((await sortiz.GoAsync("/notifications")).All("a[href]"), a => a.GetAttribute("href") == capa.Path);

        // A second run while the CAPA is open creates nothing new (SkipWhileOpen).
        var again = await fx.RunAlertAsScheduledAsync(templateId);
        var second = await jlee.GoAsync($"/insights/alerts/reports/{again}");
        Assert.DoesNotContain(second.All("a[href^='/records/']"), a => a.GetAttribute("href") != capa.Path && a.TextContent.Contains("Recurring complaints"));
    }

    // ------------------------------------------------------------------ home page

    [Theory] // PLT §2.2: the home page and its widgets for different roles, every link going somewhere real.
    [InlineData("admin")]
    [InlineData("sortiz")]
    [InlineData("nflores")]
    [InlineData("kpatel")]
    public async Task The_home_page_renders_for_each_role_and_its_links_work(string user)
    {
        using var b = await fx.SignedInAsync(user);
        var home = await b.GoAsync("/");
        home.ExpectText(fx.Services.GetUserName(user));
        var links = home.All("main a[href]").Select(a => a.GetAttribute("href")!)
            .Where(h => h.StartsWith('/') && !h.StartsWith("//") && !h.Contains("handler=") && !h.Contains(".csv") && !h.StartsWith("/files/") && !h.StartsWith("/pdf/"))
            .Select(h => h.Split('#')[0]).Distinct().Take(40).ToList();
        Assert.NotEmpty(links);
        foreach (var href in links)
        {
            var page = await b.GoAsync(href);
            Assert.False(page.Url.AbsolutePath.StartsWith("/forbidden"), $"{href} on {user}'s home page leads to /forbidden.");
        }
    }
}

internal static class InsightsViewsSteps
{
    public sealed record QuickItem(string Kind, string Label, string Href);

    /// <summary>The command palette's quick search (GET /api/search/quick).</summary>
    public static async Task<List<QuickItem>> QuickSearchAsync(this Browser b, string q)
    {
        var json = JsonDocument.Parse(await b.DownloadAsync($"/api/search/quick?q={Uri.EscapeDataString(q)}")).RootElement;
        return json.GetProperty("items").EnumerateArray()
            .Select(i => new QuickItem(i.GetProperty("kind").GetString()!, i.GetProperty("label").GetString()!, i.GetProperty("href").GetString()!))
            .ToList();
    }

    /// <summary>The selected value of a select (or the value of an input) in a form.</summary>
    public static string? FormFieldValue(this Page page, string formCss, string name) =>
        page.Form(formCss).Values(name).FirstOrDefault();

    public static string GetUserName(this IServiceProvider sp, string username) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Vellum.Engine.Security.UserDirectory>(sp).Get(username)!.DisplayName.Split(' ')[0];
}
