using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;

namespace Vellum.Pages.Records;

public class RecordModel(
    RecordService records, WorkflowEngine workflow, AccessService access, RecordVmBuilder builder, LockService locks,
    BehaviorRegistry behaviors, IServiceProvider services, VellumDbContext db) : PageModel
{
    public RecordVm Vm { get; private set; } = null!;

    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }

    private async Task<RecordSnapshot?> LoadReadableAsync()
    {
        // INV-081: a record the user can't open doesn't exist for them.
        if (!await access.CanReadAsync(Id)) return null;
        return await records.LoadAsync(Id);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var snap = await LoadReadableAsync();
        if (snap == null) return NotFound();
        Vm = await builder.BuildAsync(snap, overrideMode: Mode == "override");
        return Page();
    }

    private (List<(string, string, string, Stream)> Uploads, List<long> Removed) Files()
    {
        var uploads = Request.Form.Files.Where(f => f.Name.StartsWith("file.") && f.Length > 0)
            .Select(f => (f.Name[5..], f.FileName, string.IsNullOrEmpty(f.ContentType) ? "application/octet-stream" : f.ContentType, f.OpenReadStream()))
            .ToList();
        var removed = Request.Form["remove"].Select(v => long.TryParse(v, out var id) ? id : 0).Where(id => id > 0).ToList();
        return (uploads, removed);
    }

    public async Task<IActionResult> OnPostSaveAsync(int version, string? close, string? overrideReason)
    {
        var snap = await LoadReadableAsync();
        if (snap == null) return NotFound();
        var isOverride = Mode == "override";
        var parseErrors = new List<FieldError>();
        var probePhase = isOverride ? null : snap.Phase;
        var proposed = records.ParseInput(snap.Form, probePhase, isOverride ? new Record { PhaseType = PhaseType.Normal } : snap.Record, Request.Form, parseErrors);
        if (parseErrors.Count > 0) return await Redisplay(snap, proposed, parseErrors);

        var (uploads, removed) = Files();
        var res = await records.SaveAsync(snap, proposed, new SaveOptions
        {
            ExpectedVersion = version, Uploads = uploads, RemoveAttachmentIds = removed,
            AdminOverride = isOverride, Reason = isOverride ? overrideReason : null,
        });
        if (!res.Ok)
        {
            snap = await records.LoadRequiredAsync(Id);
            return await Redisplay(snap, proposed, res.Errors.ToList(), isOverride);
        }
        TempData["Toast"] = res.Changed ? "Saved." : "No changes to save.";
        TempData["ToastKind"] = res.Changed ? "ok" : "info";
        if (close == "1")
        {
            await locks.ReleaseAsync(Id);
            return Redirect($"/apps/{snap.App.Key}");
        }
        return Redirect($"/records/{Id}");
    }

    public async Task<IActionResult> OnPostRouteAsync(int version, int routeId, int[]? assignees, DateOnly? due, string? comment, string? password, bool certify)
    {
        var snap = await LoadReadableAsync();
        if (snap == null) return NotFound();
        var parseErrors = new List<FieldError>();
        var can = await access.CanEditAsync(snap.Record);
        RecordData? proposed = null;
        if (can.Allowed)
        {
            proposed = records.ParseInput(snap.Form, snap.Phase, snap.Record, Request.Form, parseErrors);
            if (parseErrors.Count > 0) return await Redisplay(snap, proposed, parseErrors);
        }
        var (uploads, removed) = Files();
        var res = await workflow.RouteAsync(new RouteRequest
        {
            RecordId = Id, RouteId = routeId, ExpectedVersion = version, AssigneeIds = assignees ?? [], DueDate = due,
            Comment = comment, Password = password, Certify = certify, Proposed = proposed,
            SaveOptions = new SaveOptions { ExpectedVersion = version, Uploads = uploads, RemoveAttachmentIds = removed },
        });
        if (!res.Ok)
        {
            db.ChangeTracker.Clear();
            snap = await records.LoadRequiredAsync(Id);
            var vm = await builder.BuildAsync(snap, proposed);
            vm.Errors = res.Errors.Where(e => e.FieldKey != null).ToList();
            vm.RouteErrors = res.Errors.Where(e => e.FieldKey == null).Select(e => e.Message).ToList();
            if (vm.Errors.Count == 0) vm.OpenRouteId = routeId;
            Vm = vm;
            return Page();
        }
        TempData["Toast"] = res.Message ?? "Routed.";
        return Redirect($"/records/{Id}");
    }

    public async Task<IActionResult> OnPostCommentAsync(string text)
    {
        if (await LoadReadableAsync() == null) return NotFound();
        if (!string.IsNullOrWhiteSpace(text))
        {
            await records.AddCommentAsync(Id, text);
            TempData["Toast"] = "Comment added.";
        }
        return Redirect($"/records/{Id}#comments");
    }

    public async Task<IActionResult> OnPostActionAsync(string actionKey, string? comment, string? password, bool certify)
    {
        var snap = await LoadReadableAsync();
        if (snap == null) return NotFound();
        var ctx = new RecordContext(snap, services);
        RecordBehavior? owner = null;
        foreach (var b in behaviors.For(snap.Form))
            if ((await b.ActionsAsync(ctx)).Any(a => a.Key == actionKey && a.DisabledReason == null)) { owner = b; break; }
        if (owner == null) return BadRequest();
        var input = new ActionInput
        {
            Values = Request.Form.Keys.Where(k => k.StartsWith("a.")).ToDictionary(k => k[2..], k => Request.Form[k].Select(v => v ?? "").ToArray()),
            Comment = comment, Password = password, Certify = certify,
        };
        var outcome = await owner.ExecuteAsync(ctx, actionKey, input);
        if (!outcome.Success)
        {
            db.ChangeTracker.Clear();
            snap = await records.LoadRequiredAsync(Id);
            Vm = await builder.BuildAsync(snap);
            Vm.OpenActionKey = actionKey;
            Vm.ActionErrors = outcome.Errors?.ToList() ?? ["The action failed."];
            return Page();
        }
        TempData["Toast"] = outcome.Message ?? "Done.";
        return Redirect($"/records/{outcome.OpenRecordId ?? Id}");
    }

    public async Task<IActionResult> OnPostReassignAsync(int[]? users, string? comment)
    {
        if (await LoadReadableAsync() == null) return NotFound();
        var res = await workflow.ReassignAsync(Id, users ?? [], comment);
        TempData["Toast"] = res.Ok ? res.Message : string.Join(" ", res.Errors.Select(e => e.Message));
        TempData["ToastKind"] = res.Ok ? "ok" : "bad";
        return Redirect($"/records/{Id}");
    }

    public async Task<IActionResult> OnPostBreakLockAsync()
    {
        var snap = await LoadReadableAsync();
        if (snap == null) return NotFound();
        if (!(await access.CanEditAsync(snap.Record)).Allowed && access.RoleFor(snap.Record.AppId) < AppRole.Manager) return Forbid();
        await locks.BreakAsync(Id);
        TempData["Toast"] = "Lock released. The other editor's unsaved changes won't be saved.";
        return Redirect($"/records/{Id}");
    }

    public async Task<IActionResult> OnPostReleaseAsync()
    {
        await locks.ReleaseAsync(Id);
        var snap = await records.LoadAsync(Id);
        return Redirect(snap == null ? "/" : $"/apps/{snap.App.Key}");
    }

    private async Task<IActionResult> Redisplay(RecordSnapshot snap, RecordData proposed, List<FieldError> errors, bool overrideMode = false)
    {
        Vm = await builder.BuildAsync(snap, MergeForDisplay(snap.Data, proposed), overrideMode);
        Vm.Errors = errors;
        return Page();
    }

    /// <summary>Show what the user typed, on top of stored values for anything they couldn't edit.</summary>
    private static RecordData MergeForDisplay(RecordData stored, RecordData proposed)
    {
        var d = stored.Clone();
        foreach (var k in proposed.Main.Keys) d.Main.Set(k, proposed.Main[k]);
        foreach (var (k, rows) in proposed.Rows) d.Rows[k] = rows;
        return d;
    }
}
