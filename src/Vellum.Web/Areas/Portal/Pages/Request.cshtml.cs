using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Modules.Portal;

namespace Vellum.Areas.Portal.Pages;

/// <summary>An input control on the response form.</summary>
public sealed record PortalInput(PortalFieldVm Field, string Name, string Value, string Id);

/// <summary>A file upload control with the supplier's files already uploaded to that field.</summary>
public sealed record PortalUploadVm(int ShareId, string Field, string Label, bool Required, List<PortalFileVm> Files);

/// <summary>A list of downloadable files (optionally with "remove" checkboxes for the supplier's own uploads).</summary>
public sealed record PortalFileListVm(int ShareId, List<PortalFileVm> Files, bool AllowRemove);

/// <summary>
/// One shared request: the parts of the record meant for the supplier (read-only), and the response form while it's open.
/// Everything shown comes from <see cref="PortalView"/>; everything posted goes through <see cref="PortalService.RespondAsync"/>,
/// which takes only the request kind's editable fields.
/// </summary>
[RequestFormLimits(MultipartBodyLengthLimit = 110L * 1024 * 1024)]
[RequestSizeLimit(110L * 1024 * 1024)]
public class RequestModel(PortalService portal) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    public PortalView View { get; private set; } = null!;
    public List<string> Errors { get; private set; } = [];
    private IFormCollection? _posted;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await portal.ViewAsync(Id) is not { } v) return NotFound();
        View = v;
        return Page();
    }

    public async Task<IActionResult> OnPostRespondAsync(int version, string? submit)
    {
        var res = await portal.RespondAsync(Id, version, Request.Form, submit == "1");
        if (!res.Ok)
        {
            if (await portal.ViewAsync(Id) is not { } v) return NotFound();
            View = v;
            Errors = res.Errors;
            _posted = Request.Form;
            return Page();
        }
        TempData["Toast"] = res.Message;
        return Redirect(PortalService.Url(Id));
    }

    public async Task<IActionResult> OnGetFileAsync(long fileId, bool inline = false)
    {
        if (await portal.FileAsync(Id, fileId) is not { } file) return NotFound();
        var (att, bytes) = file;
        var safe = att.ContentType is "application/pdf" or "image/png" or "image/jpeg" or "image/gif" or "text/plain";
        return inline && safe ? File(bytes, att.ContentType) : File(bytes, att.ContentType, att.FileName);
    }

    /// <summary>The value to show in an input: what the supplier just posted (after a failed submit), else the stored value.</summary>
    public PortalInput Input(PortalFieldVm f, string name) =>
        new(f, name, _posted != null && _posted.ContainsKey(name) ? _posted[name].ToString() : f.Input, "in-" + name.Replace('.', '-'));

    public PortalInput MainInput(string key) => Input(View.Editable[key], $"f.{key}");

    public PortalInput RowInput(PortalRowVm row, string key) => Input(row.Fields[key], $"r.{row.Id:N}.{key}");

    public static string FileHref(int shareId, PortalFileVm f, bool inline = false) =>
        $"/portal/requests/{shareId}?handler=File&fileId={f.Id}" + (inline ? "&inline=true" : "");
}
