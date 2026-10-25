using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Vellum.Tests.E2e;

/// <summary>
/// The designer and Insights pages as a person uses them. The form, workflow and view designers are script-driven: they
/// read a JSON island, edit it in the browser and post it back with the page's CSRF token (designer.js), so these steps do
/// the same. Everything else is plain forms.
/// </summary>
public static class DesignerSteps
{
    /// <summary>The JSON data island a designer script reads (script#id).</summary>
    public static JsonObject Island(this Page page, string id) =>
        JsonNode.Parse(page.One($"script#{id}").TextContent)!.AsObject();

    /// <summary>DSN-001 "New application": a draft-only app with a starter form, workflow and views. Returns the app overview.</summary>
    public static async Task<Page> NewAppAsync(this Browser b, string name, string key)
    {
        var home = await b.GoAsync("/designer");
        var page = await home.Form("form[action='/designer?handler=NewApp']").Set("NewName", name).Set("NewKey", key).SubmitAsync();
        Assert.Equal($"/designer/apps/{key}", page.Url.AbsolutePath);
        return page;
    }

    /// <summary>Validate → publish (DSN-003). Fails unless it lands on the version it created; returns that page.</summary>
    public static async Task<Page> PublishAsync(this Browser b, string app, string note)
    {
        var result = await b.TryPublishAsync(app, note);
        if (!result.Url.AbsolutePath.StartsWith("/designer/versions"))
            throw new Xunit.Sdk.XunitException($"Publishing {app} failed: {string.Join(" | ", result.Errors)} {result.Alert}\n{result.Excerpt()}");
        return result;
    }

    /// <summary>Press Publish with a reason, accepting warnings when the page asks for it.</summary>
    public static async Task<Page> TryPublishAsync(this Browser b, string app, string note)
    {
        var page = await b.GoAsync($"/designer/apps/{app}/publish");
        var form = page.Form($"form[action='/designer/apps/{app}/publish']").Set("Note", note);
        if (page.Q("#dz-p-accept") != null) form.Set("AcceptWarnings", "true");
        return await form.SubmitAsync();
    }

    /// <summary>Edit a form in the form designer and press "Save draft" (posts the edited document like designer.js).</summary>
    public static async Task<JsonObject> SaveFormAsync(this Browser b, string app, string form, Action<JsonObject> change)
    {
        var page = await b.GoAsync($"/designer/apps/{app}/forms/{form}");
        var data = page.Island("dz-form-data");
        var doc = data["form"]!.AsObject();
        change(doc);
        var reply = await b.PostJsonAsync(data["urls"]!["save"]!.GetValue<string>(), new { form = doc, renames = (object?)null });
        if (reply.Status != HttpStatusCode.OK || !reply.Json.GetProperty("ok").GetBoolean())
            throw new Xunit.Sdk.XunitException($"Saving form {form} failed ({(int)reply.Status}): {reply.Text}");
        return JsonNode.Parse(reply.Text)!.AsObject();
    }

    /// <summary>Edit a workflow in the workflow designer and press "Save draft".</summary>
    public static async Task<JsonObject> SaveWorkflowAsync(this Browser b, string app, string form, string wf, Action<JsonObject> change)
    {
        var page = await b.GoAsync($"/designer/apps/{app}/forms/{form}/workflows/{wf}");
        var data = page.Island("dz-wf-data");
        var doc = data["workflow"]!.AsObject();
        change(doc);
        var reply = await b.PostJsonAsync(data["urls"]!["save"]!.GetValue<string>(), new { workflow = doc, renames = (object?)null });
        if (reply.Status != HttpStatusCode.OK || !reply.Json.GetProperty("ok").GetBoolean())
            throw new Xunit.Sdk.XunitException($"Saving workflow {wf} failed ({(int)reply.Status}): {reply.Text}");
        return JsonNode.Parse(reply.Text)!.AsObject();
    }

    /// <summary>Edit a view in the view designer and press "Save draft".</summary>
    public static async Task<JsonObject> SaveViewAsync(this Browser b, string app, string view, Action<JsonObject> change)
    {
        var page = await b.GoAsync($"/designer/apps/{app}/views/{view}");
        var data = page.Island("dz-view-data");
        var doc = data["view"]!.AsObject();
        change(doc);
        var reply = await b.PostJsonAsync(data["urls"]!["save"]!.GetValue<string>(), new { view = doc });
        if (reply.Status != HttpStatusCode.OK || !reply.Json.GetProperty("ok").GetBoolean())
            throw new Xunit.Sdk.XunitException($"Saving view {view} failed ({(int)reply.Status}): {reply.Text}");
        return JsonNode.Parse(reply.Text)!.AsObject();
    }

    /// <summary>Application settings (DSN-101): change the default access level and save to the draft.</summary>
    public static async Task<Page> SetDefaultAccessAsync(this Browser b, string app, string role)
    {
        var page = await b.GoAsync($"/designer/apps/{app}/settings");
        var saved = await page.Form($"form[action='/designer/apps/{app}/settings']").Pick("DefaultAccess", role).SubmitAsync();
        Assert.Equal("Settings saved to the draft.", saved.Toast);
        return saved;
    }

    /// <summary>A field of a form document (in any section) by design name.</summary>
    public static JsonObject Field(this JsonObject form, string key) =>
        form["sections"]!.AsArray().SelectMany(s => s!["fields"]!.AsArray()).Select(f => f!.AsObject()).FirstOrDefault(f => (string?)f["key"] == key)
        ?? throw new Xunit.Sdk.XunitException($"The form has no field '{key}'.");

    public static JsonObject Section(this JsonObject form, string key) =>
        form["sections"]!.AsArray().Select(s => s!.AsObject()).FirstOrDefault(s => (string?)s["key"] == key)
        ?? throw new Xunit.Sdk.XunitException($"The form has no section '{key}'.");

    public static JsonObject Phase(this JsonObject wf, string key) =>
        wf["phases"]!.AsArray().Select(p => p!.AsObject()).FirstOrDefault(p => (string?)p["key"] == key)
        ?? throw new Xunit.Sdk.XunitException($"The workflow has no phase '{key}'.");

    public static JsonObject Route(this JsonObject wf, string from, string to) =>
        wf["routes"]!.AsArray().Select(r => r!.AsObject()).FirstOrDefault(r => (string?)r["from"] == from && (string?)r["to"] == to)
        ?? throw new Xunit.Sdk.XunitException($"The workflow has no route {from} → {to}.");

    /// <summary>A new field as the designer's "Add" toolbox creates it (never published: no id).</summary>
    public static JsonObject NewField(string key, string label, string type = "Text", int width = 6) => new()
    {
        ["id"] = null, ["key"] = key, ["label"] = label, ["type"] = type, ["width"] = width, ["required"] = false,
        ["requiredInPhases"] = new JsonArray(), ["linkForms"] = new JsonArray(),
    };

    public static JsonArray Strings(params string[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    /// <summary>Strings of a JSON array property.</summary>
    public static List<string> List(this JsonObject o, string name) => o[name]!.AsArray().Select(x => x!.GetValue<string>()).ToList();

    /// <summary>The first CSV row's cells and the rest, parsed (quoted fields, doubled quotes, BOM).</summary>
    public static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        text = text.TrimStart('﻿');
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n') { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add(row); row = []; }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>Download a file the way a link click would and return its text (fails unless 200).</summary>
    public static async Task<string> DownloadAsync(this Browser b, string url)
    {
        var res = await b.RawGetAsync(url);
        if (res.StatusCode != HttpStatusCode.OK) throw new Xunit.Sdk.XunitException($"GET {url} answered {(int)res.StatusCode}.");
        return await res.Content.ReadAsStringAsync();
    }

    /// <summary>Record numbers listed in a records table or list on the page.</summary>
    public static IReadOnlyList<string> Numbers(this Page page) =>
        page.All("td.col-number a, .vl-list-item .num").Select(e => Page.Squash(e.TextContent)).Where(t => t.Length > 0).ToList();

    // ------------------------------------------------------------------ Insights

    /// <summary>A KPI tile by its label: the number shown and where it links.</summary>
    public static (string Value, string Href) Kpi(this Page page, string label)
    {
        var tile = page.All("a.vl-stat").FirstOrDefault(a => Page.Squash(a.QuerySelector(".vl-stat-label")?.TextContent ?? "") == label)
            ?? throw new Xunit.Sdk.XunitException($"No KPI tile '{label}' on {page.Path}. Tiles: {string.Join(", ", page.All(".vl-stat-label").Select(e => e.TextContent))}");
        return (Page.Squash(tile.QuerySelector(".vl-stat-value")!.TextContent), tile.GetAttribute("href")!);
    }

    /// <summary>A row of a chart's report table (the accessible table under every chart): its cells and their links.</summary>
    public static List<(string Text, string? Href)> ReportRow(this Page page, string chartTitle, string category)
    {
        var card = page.All("section.card").FirstOrDefault(c => Page.Squash(c.QuerySelector("h2")?.TextContent ?? "").Contains(chartTitle))
            ?? throw new Xunit.Sdk.XunitException($"No chart '{chartTitle}' on {page.Path}.");
        var row = card.QuerySelectorAll("table.ins-report tbody tr").FirstOrDefault(r => Page.Squash(r.QuerySelector("th")!.TextContent) == category)
            ?? throw new Xunit.Sdk.XunitException($"Chart '{chartTitle}' has no row '{category}'. Rows: {string.Join(", ", card.QuerySelectorAll("table.ins-report tbody th").Select(t => Page.Squash(t.TextContent)))}");
        return row.QuerySelectorAll("td").Select(td => (Page.Squash(td.TextContent), td.QuerySelector("a")?.GetAttribute("href"))).ToList();
    }

    /// <summary>
    /// Evaluate an alert template the way the scheduled "Insights threshold alerts" job does (as the system identity; the
    /// scheduler is off in tests). Returns the alert report's id.
    /// </summary>
    public static async Task<long> RunAlertAsScheduledAsync(this VellumFixture fx, int templateId)
    {
        using var scope = fx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Vellum.Engine.Security.CurrentUser>().UseSystem("Insights threshold alerts");
        var report = await scope.ServiceProvider.GetRequiredService<Vellum.Modules.Insights.AlertEngine>().RunAsync(templateId, manual: false, test: false);
        return report.Id;
    }
}
