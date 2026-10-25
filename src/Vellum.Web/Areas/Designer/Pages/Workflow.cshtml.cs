using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

public sealed class WorkflowSaveRequest
{
    public WorkflowDoc? Workflow { get; set; }
    public Renames? Renames { get; set; }
}

/// <summary>
/// Visual workflow designer (DSN §3.6): phases, routes, the DSN-305 visibility matrix, a server-rendered diagram and the
/// DSN-612 routing table with unreachable/dead-end flags. Edits go to the draft only.
/// </summary>
public class WorkflowModel(DesignerStore store) : DesignerPageModel(store)
{
    [BindProperty(SupportsGet = true)] public string Form { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Wf { get; set; } = "";
    public FormDoc F { get; private set; } = null!;
    public WorkflowDoc W { get; private set; } = null!;
    public LockResult Lock { get; private set; } = new(true);
    public bool ReadOnly => !Lock.Ok;
    public string Component => $"form:{Form}";
    public string IslandJson { get; private set; } = "{}";
    public string Svg { get; private set; } = "";
    public string Base => $"{AppHref}/forms/{Form}/workflows/{Wf}";

    public async Task<IActionResult> OnGetAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        F = T.Doc.Forms.FirstOrDefault(x => x.Key == Form)!;
        W = F?.Workflows.FirstOrDefault(x => x.Key == Wf)!;
        if (F == null || W == null) return NotFound();
        Lock = await Store.AcquireAsync(T.AppKey, Component, $"form {F.Name}");
        var (_, ctx, issues) = await Store.ValidateAsync(T);
        var (records, history) = Counts(W, ctx);
        Svg = WorkflowDiagram.Render(W, records);
        var cfg = Store.Cfg;
        var activityForms = T.Doc.Forms.Select(x => new { key = x.Key, name = x.Name, app = T.Doc.Name, isActivity = x.IsActivity })
            .Concat(cfg.FormsByKey.Values.Where(x => x.AppId != T.Doc.Id && x.IsActivity).Select(x => new { key = x.Key, name = x.Name, app = x.App.Name, isActivity = x.IsActivity }))
            .Where(x => x.isActivity).ToList();
        IslandJson = Island(new
        {
            app = T.AppKey,
            form = new { key = F.Key, name = F.Name },
            workflow = W,
            readOnly = ReadOnly,
            urls = new { save = $"{Base}?handler=Save", analyze = $"{Base}?handler=Analyze", heartbeat = $"{Base}?handler=Heartbeat", form = $"{AppHref}/forms/{F.Key}" },
            sections = F.Sections.Select(s => new { key = s.Key, name = s.Name, tab = s.Tab, isSubform = s.IsSubform }),
            fields = F.MainFields.Where(x => x.Type != FieldType.Prompt).Select(x => new { key = x.Key, label = x.Label, type = x.Type.ToString(), keywordList = x.KeywordList }),
            keywordLists = cfg.KeywordListsByKey.Values.ToDictionary(l => l.Key, l => l.Keywords.Where(k => !k.Disabled).Select(k => new { key = k.Key, label = k.Label })),
            groups = Store.Directory.Groups.Where(g => !g.Inactive).Select(g => new { key = g.Key, name = g.Name, members = Store.Directory.ActiveMembers(g.Key).Count() }),
            users = Store.Directory.Assignable.Select(u => new { username = u.Username, name = u.DisplayName }),
            activityForms,
            records, history,
            table = WorkflowDiagram.Table(W, records),
            issues = WfIssues(issues, F.Key, W.Key),
        });
        return Page();
    }

    private (Dictionary<string, int> Records, Dictionary<string, int> History) Counts(WorkflowDoc w, DesignContext ctx)
    {
        var records = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in w.Phases.Where(p => p.Id != null))
        {
            records[p.Key] = ctx.RecordsInPhase.GetValueOrDefault(p.Id!.Value);
            history[p.Key] = ctx.PhaseHistory.GetValueOrDefault(p.Id!.Value);
        }
        return (records, history);
    }

    private static List<DesignIssue> WfIssues(IEnumerable<DesignIssue> issues, string form, string wf) =>
        issues.Where(i => i.Path == $"form:{form}/wf:{wf}" || i.Path.StartsWith($"form:{form}/wf:{wf}/") ||
                          (i.Path.StartsWith($"form:{form}/field:") && i.Rule == "DSN-410")).ToList();

    /// <summary>Live diagram, routing table and problems for the unsaved workflow in the browser.</summary>
    public async Task<IActionResult> OnPostAnalyzeAsync()
    {
        if (await OpenAsync() is not null) return Denied();
        var req = await ReadBodyAsync<WorkflowSaveRequest>();
        var form = T.Doc.Forms.FirstOrDefault(x => x.Key == Form);
        if (req?.Workflow == null || form == null || form.Workflows.All(w => w.Key != Wf)) return Json(new { ok = false }, 400);
        var w = req.Workflow;
        var probe = DesignDocs.Clone(T.Doc);
        DesignerStore.Sanitize(probe);
        var copy = DesignDocs.Clone(w);
        copy.Key = Wf;
        DesignerOps.ReplaceWorkflow(probe, Form, Wf, copy, req.Renames);
        DesignerStore.Sanitize(probe);
        var target = new DesignTarget { AppKey = T.AppKey, Live = T.Live, LiveDoc = T.LiveDoc, Draft = T.Draft, Doc = probe };
        var (_, ctx, issues) = await Store.ValidateAsync(target);
        var sanitized = probe.Forms.First(x => x.Key == Form).Workflows.First(x => x.Key == copy.Key);
        var (records, _) = Counts(sanitized, ctx);
        return Json(new
        {
            ok = true,
            svg = WorkflowDiagram.Render(sanitized, records),
            table = WorkflowDiagram.Table(sanitized, records),
            issues = WfIssues(issues, Form, sanitized.Key),
        });
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (await OpenAsync() is not null) return Denied();
        var req = await ReadBodyAsync<WorkflowSaveRequest>();
        var form = T.Doc.Forms.FirstOrDefault(x => x.Key == Form);
        var current = form?.Workflows.FirstOrDefault(x => x.Key == Wf);
        if (req?.Workflow == null || form == null || current == null) return Json(new { ok = false, error = "The workflow couldn't be read. Reload the page." }, 400);
        var lk = await Store.AcquireAsync(T.AppKey, Component, $"form {form.Name}");
        if (!lk.Ok) return Json(new { ok = false, error = $"{lk.HolderName} is editing this form and its workflows, so your changes weren't saved (DSN-004)." }, 409);

        var incoming = req.Workflow;
        incoming.Key = current.Key; // the workflow's own design name is set when it's created
        var live = T.LiveDoc?.Forms.FirstOrDefault(x => x.Id == form.Id && x.Id != null)?.Workflows.FirstOrDefault(x => x.Id == current.Id && x.Id != null);
        if (live != null)
            foreach (var p in incoming.Phases.Where(p => p.Id != null))
                if (live.Phases.FirstOrDefault(l => l.Id == p.Id) is { } lp) { p.Key = lp.Key; p.Type = lp.Type; }
        foreach (var p in incoming.Phases)
            if (DesignDocs.KeyProblem(p.Key) is { } kp) return Json(new { ok = false, error = $"Phase '{p.Name}': design name {kp}." }, 400);

        await Store.MutateAsync(T.AppKey, d => DesignerOps.ReplaceWorkflow(d, Form, Wf, incoming, req.Renames));
        var t = await Store.OpenAsync(T.AppKey);
        var (_, ctx, issues) = await Store.ValidateAsync(t!);
        var stored = t!.Doc.Forms.First(x => x.Key == Form).Workflows.First(x => x.Key == Wf);
        var (records, _) = Counts(stored, ctx);
        return Json(new
        {
            ok = true, workflow = stored, svg = WorkflowDiagram.Render(stored, records), table = WorkflowDiagram.Table(stored, records),
            issues = WfIssues(issues, Form, Wf),
        });
    }

    public async Task<IActionResult> OnPostHeartbeatAsync()
    {
        if (await OpenAsync() is not null) return Denied();
        var name = T.Doc.Forms.FirstOrDefault(x => x.Key == Form)?.Name ?? Form;
        var l = await Store.AcquireAsync(T.AppKey, Component, $"form {name}");
        return Json(new { ok = l.Ok, holder = l.HolderName });
    }
}
