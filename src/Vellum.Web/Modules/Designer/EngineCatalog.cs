using Vellum.Domain;
using Vellum.Engine;

namespace Vellum.Modules.Designer;

public sealed record CatalogItem(string Type, string Icon, string Name, string Key, string? AppKey, string? AppName, string? Parent, string? Detail, string? Href, string AuditKey);

public sealed record UsageRef(string Where, string What, string? Href);

/// <summary>DSN-1205 / ADM-1202: the Platform Engine view of every configuration object, orphans and "where used".</summary>
public static class EngineCatalog
{
    public static readonly string[] Types = ["Application", "Form", "Section", "Subform", "Field", "Workflow", "Phase", "Route", "View", "Keyword list"];

    public static string IconFor(string type) => type switch
    {
        "Application" => "bi-grid-1x2", "Form" => "bi-ui-checks-grid", "Section" => "bi-card-heading", "Subform" => "bi-table",
        "Field" => "bi-input-cursor", "Workflow" => "bi-diagram-3", "Phase" => "bi-signpost", "Route" => "bi-arrow-right-circle",
        "View" => "bi-layout-text-window", "Keyword list" => "bi-tags", _ => "bi-square",
    };

    public static IEnumerable<CatalogItem> Items(ConfigModel cfg, Func<AppDefinition, bool> visible)
    {
        foreach (var app in cfg.Apps.Where(visible))
        {
            var ah = $"/designer/apps/{app.Key}";
            yield return new CatalogItem("Application", IconFor("Application"), app.Name, app.Key, app.Key, app.Name, app.Suite,
                $"{app.Forms.Count} forms · {app.Views.Count} views", app.IsSystem ? null : ah, app.Key);
            foreach (var f in app.Forms)
            {
                var fh = $"{ah}/forms/{f.Key}";
                var kind = f.IsActivity ? "activity form" : f.IsNonWorkflow ? "profile form" : "main form";
                yield return new CatalogItem("Form", IconFor("Form"), f.Name, f.Key, app.Key, app.Name, null, $"{kind} · {f.Fields.Count} fields", fh, $"{app.Key}/{f.Key}");
                foreach (var s in f.Sections)
                    yield return new CatalogItem(s.IsSubform ? "Subform" : "Section", IconFor(s.IsSubform ? "Subform" : "Section"), s.Name, s.Key, app.Key, app.Name, f.Name,
                        $"tab {s.Tab} · {f.Fields.Count(x => x.SectionKey == s.Key)} fields", fh, $"{app.Key}/{f.Key}#{s.Key}");
                foreach (var x in f.Fields)
                    yield return new CatalogItem("Field", IconFor("Field"), x.Type == FieldType.Prompt ? "(prompt)" : x.Label, x.Key, app.Key, app.Name, f.Name,
                        DesignDocs.TypeLabel(x.Type) + (x.KeywordList != null ? $" · {x.KeywordList}" : "") + (x.Required ? " · required" : ""), fh, $"{app.Key}/{f.Key}.{x.Key}");
                foreach (var w in f.Workflows)
                {
                    var wh = $"{fh}/workflows/{w.Key}";
                    yield return new CatalogItem("Workflow", IconFor("Workflow"), w.Name, w.Key, app.Key, app.Name, f.Name, $"{w.Phases.Count} phases · {w.Routes.Count} routes", wh, $"{app.Key}/{f.Key}/{w.Key}");
                    foreach (var p in w.Phases)
                        yield return new CatalogItem("Phase", IconFor("Phase"), p.Name, p.Key, app.Key, app.Name, $"{f.Name} › {w.Name}",
                            p.Type + (p.IsStart ? " · start" : "") + (p.DueDays is { } d ? $" · {d}d" : ""), wh, $"{app.Key}/{f.Key}/{w.Key}/{p.Key}");
                    foreach (var r in w.Routes)
                        yield return new CatalogItem("Route", IconFor("Route"), r.Label ?? $"{r.FromPhase.Name} → {r.ToPhase.Name}", $"{r.FromPhase.Key}→{r.ToPhase.Key}", app.Key, app.Name,
                            $"{f.Name} › {w.Name}", r.Kind + (r.SignatureMeaning != null ? $" · signs \"{r.SignatureMeaning}\"" : ""), wh, $"{app.Key}/{f.Key}/{w.Key}/{r.FromPhase.Key}->{r.ToPhase.Key}:{r.Kind}");
                }
            }
            foreach (var v in app.Views)
                yield return new CatalogItem("View", IconFor("View"), v.Name, v.Key, app.Key, app.Name, v.Category,
                    $"{v.Status} · {(v.Forms.Count == 0 ? "all main forms" : string.Join(", ", v.Forms))}", $"{ah}/views/{v.Key}", $"{app.Key}/view:{v.Key}");
        }
        foreach (var l in cfg.KeywordListsByKey.Values.OrderBy(l => l.Name))
            yield return new CatalogItem("Keyword list", IconFor("Keyword list"), l.Name, l.Key, null, null, null,
                $"{l.Keywords.Count} keywords" + (l.IsSystem ? " · system" : ""), $"/designer/engine/usage?kind=list&key={Uri.EscapeDataString(l.Key)}", l.Key);
    }

    /// <summary>Keyword lists no field uses (DSN-802 warns about orphaned lists).</summary>
    public static List<KeywordList> UnusedLists(ConfigModel cfg)
    {
        var used = cfg.FormsById.Values.SelectMany(f => f.Fields).Where(f => f.KeywordList != null).Select(f => f.KeywordList!).ToHashSet();
        return cfg.KeywordListsByKey.Values.Where(l => !used.Contains(l.Key) && !l.IsSystem).OrderBy(l => l.Name).ToList();
    }

    /// <summary>Broken references across the live configuration (views on missing forms, fields on missing lists, …).</summary>
    public static List<UsageRef> BrokenReferences(ConfigModel cfg, Func<AppDefinition, bool> visible)
    {
        var list = new List<UsageRef>();
        foreach (var app in cfg.Apps.Where(visible))
        {
            foreach (var v in app.Views)
                foreach (var k in v.Forms.Where(k => !cfg.FormsByKey.ContainsKey(k)))
                    list.Add(new UsageRef($"{app.Name} › view {v.Name}", $"points at missing form '{k}'", $"/designer/apps/{app.Key}/views/{v.Key}"));
            foreach (var f in app.Forms)
            {
                foreach (var x in f.Fields)
                {
                    if (x.KeywordList != null && !cfg.KeywordListsByKey.ContainsKey(x.KeywordList))
                        list.Add(new UsageRef($"{f.Name} › {x.Label}", $"uses missing keyword list '{x.KeywordList}'", $"/designer/apps/{app.Key}/forms/{f.Key}"));
                    foreach (var lf in x.LinkForms.Where(lf => !cfg.FormsByKey.ContainsKey(lf)))
                        list.Add(new UsageRef($"{f.Name} › {x.Label}", $"links to missing form '{lf}'", $"/designer/apps/{app.Key}/forms/{f.Key}"));
                    if (f.Sections.All(s => s.Key != x.SectionKey))
                        list.Add(new UsageRef($"{f.Name} › {x.Label}", "is detached from the layout (not shown)", $"/designer/apps/{app.Key}/forms/{f.Key}"));
                }
                foreach (var w in f.Workflows)
                {
                    var a = WorkflowAnalysis.Analyze(DesignDocs.FromLive(w));
                    foreach (var p in w.Phases.Where(p => a.Unreachable.Contains(p.Key) && p.Type == PhaseType.Normal))
                        list.Add(new UsageRef($"{f.Name} › {w.Name} › {p.Name}", "can't be reached from a starting phase", $"/designer/apps/{app.Key}/forms/{f.Key}/workflows/{w.Key}"));
                    foreach (var p in w.Phases.Where(p => a.DeadEnds.Contains(p.Key)))
                        list.Add(new UsageRef($"{f.Name} › {w.Name} › {p.Name}", "is a dead end (no route out)", $"/designer/apps/{app.Key}/forms/{f.Key}/workflows/{w.Key}"));
                }
            }
        }
        return list;
    }

    /// <summary>"Where used" for a keyword list, a field (<c>form.field</c>), a form or a group.</summary>
    public static List<UsageRef> WhereUsed(ConfigModel cfg, string kind, string key)
    {
        var refs = new List<UsageRef>();
        string FormHref(FormDefinition f) => $"/designer/apps/{f.App.Key}/forms/{f.Key}";
        string WfHref(WorkflowDefinition w) => $"/designer/apps/{w.Form.App.Key}/forms/{w.Form.Key}/workflows/{w.Key}";
        string ViewHref(ViewDefinition v) => $"/designer/apps/{cfg.AppsById[v.AppId].Key}/views/{v.Key}";
        IEnumerable<FormDefinition> ViewForms(ViewDefinition v) =>
            v.Forms.Count > 0 ? v.Forms.Select(k => cfg.FormsByKey.GetValueOrDefault(k)).OfType<FormDefinition>() : cfg.AppsById[v.AppId].Forms.Where(f => !f.IsActivity);

        switch (kind)
        {
            case "list":
                foreach (var f in cfg.FormsById.Values)
                    foreach (var x in f.Fields.Where(x => x.KeywordList == key))
                    {
                        refs.Add(new UsageRef($"{f.App.Name} › {f.Name}", $"field {x.Label} ({x.Key})", FormHref(f)));
                        refs.AddRange(WhereUsed(cfg, "field", $"{f.Key}.{x.Key}").Where(r => r.What.StartsWith("view", StringComparison.Ordinal)));
                    }
                break;
            case "field":
                var parts = key.Split('.', 2);
                if (parts.Length != 2 || !cfg.FormsByKey.TryGetValue(parts[0], out var form)) break;
                var fk = parts[1];
                if (DesignDocs.TemplateFields(form.TitleTemplate).Contains(fk)) refs.Add(new UsageRef($"{form.Name}", "title template", FormHref(form)));
                foreach (var x in form.Fields.Where(x => DesignDocs.ConditionFields(x.VisibleWhen).Contains(fk)))
                    refs.Add(new UsageRef($"{form.Name} › {x.Label}", "visibility rule", FormHref(form)));
                foreach (var w in form.Workflows)
                {
                    foreach (var p in w.Phases.Where(p => p.AssigneeField == fk)) refs.Add(new UsageRef($"{w.Name} › {p.Name}", "assignment field", WfHref(w)));
                    foreach (var p in w.Phases.Where(p => p.DueDateField == fk)) refs.Add(new UsageRef($"{w.Name} › {p.Name}", "due-date field", WfHref(w)));
                    foreach (var r in w.Routes.Where(r => DesignDocs.ConditionFields(r.Condition).Contains(fk)))
                        refs.Add(new UsageRef($"{w.Name} › {r.FromPhase.Name} → {r.ToPhase.Name}", "route condition", WfHref(w)));
                }
                foreach (var v in cfg.Apps.SelectMany(a => a.Views).Where(v => ViewForms(v).Contains(form)))
                {
                    var where = $"{cfg.AppsById[v.AppId].Name} › {v.Name}";
                    if (v.Columns.Contains(fk)) refs.Add(new UsageRef(where, "view column", ViewHref(v)));
                    if (v.SortBy == fk) refs.Add(new UsageRef(where, "view sort", ViewHref(v)));
                    if (v.FilterJson?.Contains($"\"{fk}\"") == true) refs.Add(new UsageRef(where, "view filter", ViewHref(v)));
                    if (v.CalendarField == fk) refs.Add(new UsageRef(where, "view calendar column", ViewHref(v)));
                }
                break;
            case "form":
                foreach (var f in cfg.FormsById.Values)
                {
                    foreach (var x in f.Fields.Where(x => x.LinkForms.Contains(key))) refs.Add(new UsageRef($"{f.App.Name} › {f.Name} › {x.Label}", "link target", FormHref(f)));
                    if (f.ParentForms.Contains(key)) refs.Add(new UsageRef($"{f.App.Name} › {f.Name}", "activity parent form", FormHref(f)));
                    foreach (var w in f.Workflows)
                        foreach (var p in w.Phases.Where(p => p.MustCompleteForms.Contains(key) || p.MustCreateForms.Contains(key) || p.ActivityForms.Contains(key)))
                            refs.Add(new UsageRef($"{f.Name} › {w.Name} › {p.Name}", "activity gate", WfHref(w)));
                }
                foreach (var v in cfg.Apps.SelectMany(a => a.Views).Where(v => v.Forms.Contains(key)))
                    refs.Add(new UsageRef($"{cfg.AppsById[v.AppId].Name} › {v.Name}", "view data source", ViewHref(v)));
                break;
            case "group":
                foreach (var w in cfg.WorkflowsById.Values)
                    foreach (var p in w.Phases.Where(p => p.AssigneeGroup == key)) refs.Add(new UsageRef($"{w.Form.Name} › {w.Name} › {p.Name}", "phase assignment group", WfHref(w)));
                foreach (var w in cfg.WorkflowsById.Values)
                    foreach (var r in w.Routes.Where(r => r.AllowedPrincipals.Contains("@" + key))) refs.Add(new UsageRef($"{w.Form.Name} › {w.Name} › {r.FromPhase.Name} → {r.ToPhase.Name}", "special-forward users", WfHref(w)));
                break;
        }
        return refs.DistinctBy(r => (r.Where, r.What)).ToList();
    }
}
