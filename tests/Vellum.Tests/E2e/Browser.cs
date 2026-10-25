using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Vellum.Tests.E2e;

/// <summary>
/// One person's browser session against the in-process application: real HTTP requests through the whole middleware
/// pipeline, cookies, antiforgery tokens and redirects, with pages parsed into a DOM. Forms are filled and submitted the
/// way a browser would (successful controls only, submitter name/value, formaction, multipart uploads), so tests drive the
/// same Razor pages, handlers and model binding that people use. Every page is checked for leaked Razor, escaped markup
/// and server errors as it loads.
/// </summary>
public sealed class Browser : IDisposable
{
    private static readonly HtmlParser Parser = new();
    private readonly HttpClient _http;

    public Browser(VellumFixture fx)
    {
        _http = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    /// <summary>The page currently shown.</summary>
    public Page Page { get; private set; } = null!;

    /// <summary>Open a URL and follow redirects. Fails unless the final response has the expected status (200 by default).</summary>
    public async Task<Page> GoAsync(string url, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var page = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
        page.ExpectStatus(expect);
        return page;
    }

    /// <summary>Raw GET without following redirects or checking the status (for downloads, APIs and access probes).</summary>
    public Task<HttpResponseMessage> RawGetAsync(string url) => _http.GetAsync(url);

    /// <summary>Identifier-first sign-in through /login. Lands on the user's home page (or /portal for supplier contacts).</summary>
    public async Task<Page> SignInAsync(string username, string password = TestHelpers.Password)
    {
        var login = await GoAsync($"/login?u={Uri.EscapeDataString(username)}");
        var page = await login.Form("form[action*='handler=Password']").Set("Username", username).Set("Password", password).SubmitAsync();
        if (page.Path.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
            throw new Xunit.Sdk.XunitException($"Sign-in as {username} failed: {page.Alert ?? page.Text[..Math.Min(300, page.Text.Length)]}");
        return page;
    }

    public async Task<Page> SignOutAsync()
    {
        var page = Page.Path.StartsWith("/login") ? await GoAsync("/") : Page;
        return await page.Form("form[action='/logout']").SubmitAsync();
    }

    internal async Task<Page> SendAsync(HttpRequestMessage request)
    {
        var res = await _http.SendAsync(request);
        for (var hops = 0; IsRedirect(res.StatusCode); hops++)
        {
            if (hops > 10) throw new Xunit.Sdk.XunitException($"Too many redirects from {request.RequestUri}");
            var location = res.Headers.Location!;
            var next = location.IsAbsoluteUri ? location : new Uri(res.RequestMessage!.RequestUri!, location);
            res = await _http.GetAsync(next);
        }
        var html = await res.Content.ReadAsStringAsync();
        var isHtml = res.Content.Headers.ContentType?.MediaType == "text/html";
        Page = new Page(this, res.RequestMessage!.RequestUri!, res.StatusCode, html, isHtml ? Parser.ParseDocument(html) : null);
        if (isHtml) Page.AssertRendersCleanly();
        return Page;
    }

    private static bool IsRedirect(HttpStatusCode s) => s is HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.Moved
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public void Dispose() => _http.Dispose();
}

/// <summary>A loaded page: status, URL, raw HTML and its DOM.</summary>
public sealed partial class Page(Browser browser, Uri url, HttpStatusCode status, string html, IHtmlDocument? doc)
{
    public Browser Browser { get; } = browser;
    public Uri Url { get; } = url;
    public HttpStatusCode Status { get; } = status;
    public string Html { get; } = html;
    public IHtmlDocument Doc => doc ?? throw new Xunit.Sdk.XunitException($"{Url} is not an HTML page.");
    public string Path => Url.PathAndQuery;

    /// <summary>Visible text of the main content, whitespace collapsed.</summary>
    public string Text => Squash((Doc.QuerySelector("main") ?? Doc.Body)?.TextContent ?? "");

    /// <summary>The toast queued by the last post (TempData), if any.</summary>
    public string? Toast => Doc.QuerySelector("[data-toast]")?.GetAttribute("data-toast") is { } t ? t : Doc.QuerySelector(".vl-toast-msg, .toast-body")?.TextContent.Trim();

    /// <summary>Text of the first error banner or validation summary on the page.</summary>
    public string? Alert => Doc.QuerySelectorAll(".vl-banner-bad, .alert-danger, .vl-error-summary, .invalid-feedback, .text-danger[role=alert], [role=alert]")
        .Select(e => Squash(e.TextContent)).FirstOrDefault(t => t.Length > 0);

    /// <summary>All validation and error messages shown on the page.</summary>
    public IReadOnlyList<string> Errors => Doc.QuerySelectorAll(".vl-banner-bad li, .invalid-feedback, .vl-error-summary li, .field-validation-error, .vl-banner-bad .ttl")
        .Select(e => Squash(e.TextContent)).Where(t => t.Length > 0).Distinct().ToList();

    /// <summary>The record id when this is a record page.</summary>
    public Guid RecordId => RecordUrl().Match(Url.AbsolutePath) is { Success: true } m ? Guid.Parse(m.Groups[1].Value)
        : throw new Xunit.Sdk.XunitException($"{Url} is not a record page.");

    public IElement? Q(string css) => Doc.QuerySelector(css);
    public IReadOnlyList<IElement> All(string css) => Doc.QuerySelectorAll(css).ToList();
    public IElement One(string css) => Doc.QuerySelector(css) ?? throw new Xunit.Sdk.XunitException($"No element matches '{css}' on {Path}.\n{Excerpt()}");

    /// <summary>The first link whose text contains the given text (case-insensitive).</summary>
    public IElement Link(string text) => Doc.QuerySelectorAll("a[href]").FirstOrDefault(a => Squash(a.TextContent).Contains(text, StringComparison.OrdinalIgnoreCase))
        ?? throw new Xunit.Sdk.XunitException($"No link '{text}' on {Path}.");

    public Task<Page> ClickAsync(string linkText) => Browser.GoAsync(Link(linkText).GetAttribute("href")!);

    /// <summary>Fill a form on this page (by CSS selector of the form element).</summary>
    public FormFill Form(string css = "form[method=post]")
    {
        var el = Doc.QuerySelector(css) as IHtmlFormElement ?? throw new Xunit.Sdk.XunitException($"No form matches '{css}' on {Path}.\n{Excerpt()}");
        return new FormFill(this, el);
    }

    /// <summary>The form that contains the given element (for example a button).</summary>
    public FormFill FormOf(string elementCss)
    {
        var el = One(elementCss);
        var form = el.GetAttribute("form") is { } id ? Doc.GetElementById(id) as IHtmlFormElement : el.Closest("form") as IHtmlFormElement;
        return new FormFill(this, form ?? throw new Xunit.Sdk.XunitException($"'{elementCss}' is not inside a form on {Path}."));
    }

    /// <summary>
    /// Add a subform row the way the "Add row" button does: the section's row template is copied into the table with a
    /// fresh index. Returns the index, so the row's fields are "r.{section}.{index}.{fieldKey}". Fill forms after adding rows.
    /// </summary>
    public int AddRow(string section)
    {
        var sf = One($"[data-subform='{section}']");
        var tpl = sf.QuerySelector("template[data-row-template]") as IHtmlTemplateElement
            ?? throw new Xunit.Sdk.XunitException($"Subform '{section}' on {Path} can't get new rows here.");
        var index = 900000 + sf.QuerySelectorAll("tbody tr[data-row]").Length;
        var html = string.Concat(tpl.Content.ChildNodes.OfType<IElement>().Select(e => e.OuterHtml)).Replace("__INDEX__", index.ToString());
        sf.QuerySelector("tbody")!.Insert(AdjacentPosition.BeforeEnd, html);
        return index;
    }

    public string AntiforgeryToken => Doc.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value")
        ?? Doc.QuerySelector("meta[name=csrf-token]")?.GetAttribute("content")
        ?? throw new Xunit.Sdk.XunitException($"No antiforgery token on {Path}.");

    public bool Contains(string text) => Text.Contains(text, StringComparison.OrdinalIgnoreCase);

    public Page ExpectText(string text)
    {
        if (!Contains(text)) throw new Xunit.Sdk.XunitException($"Expected '{text}' on {Path}.\n{Excerpt()}");
        return this;
    }

    public Page ExpectNoText(string text)
    {
        if (Contains(text)) throw new Xunit.Sdk.XunitException($"Didn't expect '{text}' on {Path}.");
        return this;
    }

    public Page ExpectStatus(HttpStatusCode expect)
    {
        if (Status != expect) throw new Xunit.Sdk.XunitException($"Expected HTTP {(int)expect} but got {(int)Status} for {Path}.\n{Excerpt()}");
        return this;
    }

    /// <summary>Fail on output that a person must never see: Razor that wasn't compiled as code, markup escaped into text, or an error page.</summary>
    internal void AssertRendersCleanly()
    {
        if (doc == null) return;
        if ((int)Status >= 500 || Html.Contains("An unhandled exception occurred", StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Server error {(int)Status} on {Path}:\n{Squash(doc.Body?.TextContent ?? "")[..Math.Min(1500, Squash(doc.Body?.TextContent ?? "").Length)]}");
        foreach (var leak in Leaks(doc))
            throw new Xunit.Sdk.XunitException($"Raw Razor or escaped markup is visible on {Path}: \"{leak}\"");
    }

    /// <summary>Visible text or attribute values that contain Razor syntax or escaped HTML tags.</summary>
    public static IEnumerable<string> Leaks(IHtmlDocument d)
    {
        // Text inside <pre>, <code>, <textarea> and JSON islands is allowed to show markup.
        static bool Exempt(INode n) => n.Ancestors<IElement>().Any(a => a.LocalName is "pre" or "code" or "textarea" or "script" or "style" or "template");
        foreach (var t in d.Body?.Descendants<IText>() ?? [])
        {
            if (Exempt(t)) continue;
            if (RazorLeak().Match(t.Data) is { Success: true } m) yield return Around(t.Data, m.Index);
            if (EscapedTag().Match(t.Data) is { Success: true } e) yield return Around(t.Data, e.Index);
        }
        foreach (var el in d.All)
        {
            // Razor's <text> wrapper must never reach the page (SVG <text> labels are fine).
            if (el.LocalName is "text" && el.NamespaceUri == AngleSharp.Dom.NamespaceNames.HtmlUri) yield return "<text> element in output: " + Squash(el.OuterHtml)[..Math.Min(120, Squash(el.OuterHtml).Length)];
            foreach (var a in el.Attributes)
                if (a.Name is not ("value" or "data-copy" or "content") && RazorLeak().Match(a.Value) is { Success: true } m)
                    yield return $"{el.LocalName}[{a.Name}]=\"{Around(a.Value, m.Index)}\"";
        }
    }

    private static string Around(string s, int i) => Squash(s.Substring(Math.Max(0, i - 40), Math.Min(s.Length - Math.Max(0, i - 40), 100)));

    internal static string Squash(string s) => Whitespace().Replace(s, " ").Trim();

    public string Excerpt() => Squash((doc?.QuerySelector("main") ?? doc?.Body)?.TextContent ?? Html)[..Math.Min(1200, Squash((doc?.QuerySelector("main") ?? doc?.Body)?.TextContent ?? Html).Length)];

    [GeneratedRegex(@"@(if|foreach|for|switch|while|using|await|Model|Html|Url|ViewData|Ui\.|item\.|string\.)\b|@\(|@\{|@\*|\*@|@:")]
    private static partial Regex RazorLeak();
    [GeneratedRegex(@"</?(span|div|strong|small|em|br|ul|li|td|tr|button|svg|input|label|text|i class|a href)[\s>/]")]
    private static partial Regex EscapedTag();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"^/records/([0-9a-fA-F-]{36})")]
    private static partial Regex RecordUrl();
}

/// <summary>
/// A form being filled in. Starts with the form's successful controls exactly as a browser would submit them (enabled
/// named inputs, checked boxes and radios, selected options, textareas, plus controls outside the form that point at it
/// with form="id"), then lets the test change values before submitting.
/// </summary>
public sealed class FormFill
{
    private readonly Page _page;
    private readonly IHtmlFormElement _form;
    private readonly List<KeyValuePair<string, string>> _fields = [];
    private readonly List<(string Name, string FileName, byte[] Content, string ContentType)> _files = [];
    private readonly HashSet<IElement> _excluded = [];

    internal FormFill(Page page, IHtmlFormElement form)
    {
        _page = page;
        _form = form;
        Collect();
    }

    public IHtmlFormElement Element => _form;

    private IEnumerable<IElement> Controls()
    {
        var own = _form.QuerySelectorAll("input, select, textarea").Where(e => e.GetAttribute("form") is null || e.GetAttribute("form") == _form.Id);
        IEnumerable<IElement> outside = string.IsNullOrEmpty(_form.Id) ? [] : _page.Doc.QuerySelectorAll($"input[form='{_form.Id}'], select[form='{_form.Id}'], textarea[form='{_form.Id}']");
        return own.Concat(outside).Distinct();
    }

    private void Collect()
    {
        _fields.Clear();
        foreach (var el in Controls())
        {
            if (_excluded.Contains(el) || el.HasAttribute("disabled") || el.Closest("fieldset[disabled]") != null) continue;
            if (el.GetAttribute("name") is not { Length: > 0 } name) continue;
            switch (el)
            {
                case IHtmlInputElement input:
                    var type = (input.GetAttribute("type") ?? "text").ToLowerInvariant();
                    if (type is "submit" or "button" or "image" or "reset" or "file") continue;
                    if (type is "checkbox" or "radio" && !input.HasAttribute("checked")) continue;
                    _fields.Add(new(name, input.GetAttribute("value") ?? (type is "checkbox" or "radio" ? "on" : "")));
                    break;
                case IHtmlSelectElement select:
                    var chosen = select.QuerySelectorAll("option").Where(o => o.HasAttribute("selected")).ToList();
                    if (chosen.Count == 0 && !select.HasAttribute("multiple") && select.QuerySelector("option") is { } first) chosen = [first];
                    foreach (var o in chosen) _fields.Add(new(name, o.GetAttribute("value") ?? o.TextContent.Trim()));
                    break;
                case IHtmlTextAreaElement area:
                    _fields.Add(new(name, area.TextContent.StartsWith('\n') ? area.TextContent[1..] : area.TextContent));
                    break;
            }
        }
    }

    /// <summary>
    /// Leave out every control inside the matching elements, as a script disabling them would. Call it before changing
    /// values: the form's controls are collected again.
    /// </summary>
    public FormFill Disable(string css)
    {
        foreach (var scope in _page.Doc.QuerySelectorAll(css))
            foreach (var el in scope.QuerySelectorAll("input, select, textarea")) _excluded.Add(el);
        Collect();
        return this;
    }

    /// <summary>Current values for a field name.</summary>
    public IReadOnlyList<string> Values(string name) => _fields.Where(f => f.Key == name).Select(f => f.Value).ToList();

    /// <summary>Replace every value of a field (adds it when the form has no such control, like a script-built hidden input).</summary>
    public FormFill Set(string name, params string?[] values)
    {
        _fields.RemoveAll(f => f.Key == name);
        foreach (var v in values) if (v != null) _fields.Add(new(name, v));
        return this;
    }

    public FormFill Set(string name, int value) => Set(name, value.ToString());

    public FormFill Add(string name, string value)
    {
        _fields.Add(new(name, value));
        return this;
    }

    public FormFill Remove(string name)
    {
        _fields.RemoveAll(f => f.Key == name);
        return this;
    }

    /// <summary>Choose option(s) of a select, or check radio/checkbox control(s), by data-key, value or visible label.</summary>
    public FormFill Pick(string name, params string[] choices)
    {
        var controls = Controls().Where(e => e.GetAttribute("name") == name).ToList();
        if (controls.Count == 0) throw new Xunit.Sdk.XunitException($"The form has no field '{name}'.");
        var values = new List<string>();
        foreach (var choice in choices)
        {
            string? v = null;
            if (controls[0] is IHtmlSelectElement sel)
            {
                var opt = sel.QuerySelectorAll("option").FirstOrDefault(o => o.GetAttribute("data-key") == choice || o.GetAttribute("value") == choice)
                    ?? sel.QuerySelectorAll("option").FirstOrDefault(o => Page.Squash(o.TextContent).Equals(choice, StringComparison.OrdinalIgnoreCase))
                    ?? sel.QuerySelectorAll("option").FirstOrDefault(o => Page.Squash(o.TextContent).Contains(choice, StringComparison.OrdinalIgnoreCase));
                v = opt?.GetAttribute("value");
                if (opt != null && v == null) v = opt.TextContent.Trim();
            }
            else
            {
                var box = controls.FirstOrDefault(c => c.GetAttribute("data-key") == choice || c.GetAttribute("value") == choice)
                    ?? controls.FirstOrDefault(c => c.Id is { } id && _page.Doc.QuerySelector($"label[for='{id}']") is { } l && Page.Squash(l.TextContent).Contains(choice, StringComparison.OrdinalIgnoreCase));
                v = box?.GetAttribute("value") ?? (box != null ? "on" : null);
            }
            values.Add(v ?? throw new Xunit.Sdk.XunitException($"Field '{name}' has no choice '{choice}'. Options: " +
                string.Join(", ", controls[0] is IHtmlSelectElement s2 ? s2.QuerySelectorAll("option").Select(o => $"{o.GetAttribute("data-key")}/{Page.Squash(o.TextContent)}") : controls.Select(c => c.GetAttribute("data-key") ?? c.GetAttribute("value")))));
        }
        return Set(name, values.ToArray());
    }

    /// <summary>Attach a file to a file input (by input name).</summary>
    public FormFill Attach(string name, string fileName, byte[] content, string contentType = "application/octet-stream")
    {
        _files.Add((name, fileName, content, contentType));
        return this;
    }

    /// <summary>
    /// Submit the form. <paramref name="submitter"/> is a CSS selector for the button pressed (its name/value, formaction and
    /// formmethod apply); without one the form's own action is used.
    /// </summary>
    public Task<Page> SubmitAsync(string? submitter = null)
    {
        IElement? button = null;
        if (submitter != null)
            button = _page.Doc.QuerySelector(submitter) ?? throw new Xunit.Sdk.XunitException($"No submit button '{submitter}' on {_page.Path}.");
        var action = button?.GetAttribute("formaction") ?? _form.GetAttribute("action");
        if (string.IsNullOrEmpty(action)) action = _page.Url.PathAndQuery;
        var method = (button?.GetAttribute("formmethod") ?? _form.GetAttribute("method") ?? "get").ToUpperInvariant();
        var fields = _fields.ToList();
        if (button?.GetAttribute("name") is { Length: > 0 } bn) fields.Add(new(bn, button.GetAttribute("value") ?? ""));
        return SendAsync(method, action, fields);
    }

    /// <summary>Submit to an explicit URL (for example a handler a script would post to).</summary>
    public Task<Page> SubmitToAsync(string action) => SendAsync("POST", action, _fields.ToList());

    private Task<Page> SendAsync(string method, string action, List<KeyValuePair<string, string>> fields)
    {
        var target = new Uri(_page.Url, action);
        if (method == "GET")
        {
            var qs = string.Join("&", fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
            var b = new UriBuilder(target) { Query = qs };
            return _page.Browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, b.Uri));
        }
        if (!fields.Any(f => f.Key == "__RequestVerificationToken")) fields.Add(new("__RequestVerificationToken", _page.AntiforgeryToken));
        HttpContent content;
        var multipart = _files.Count > 0 || string.Equals(_form.GetAttribute("enctype"), "multipart/form-data", StringComparison.OrdinalIgnoreCase);
        if (multipart)
        {
            var mp = new MultipartFormDataContent();
            foreach (var f in fields) mp.Add(new StringContent(f.Value), f.Key);
            foreach (var (name, fileName, bytes, type) in _files)
            {
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                mp.Add(file, name, fileName);
            }
            content = mp;
        }
        else content = new FormUrlEncodedContent(fields);
        return _page.Browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, target) { Content = content });
    }
}
