using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Domain;

namespace Vellum.Modules.Designer;

/// <param name="Type">Application, Form, Section, Field, Workflow, Phase, Route, View.</param>
/// <param name="Key">Configuration audit object key, e.g. <c>capa/capa.subject</c>.</param>
/// <param name="Action">Created, Updated or Deleted.</param>
public sealed record ObjectChange(string Type, string Key, string Label, string Action, string? Before, string? After)
{
    public IReadOnlyList<PropertyChange> Properties => DesignDiff.Properties(Before, After);
}

public sealed record PropertyChange(string Name, string? Old, string? New);

/// <summary>
/// Structured diff between two application documents (DSN-1202): one entry per changed object (INV-145) with before/after
/// JSON of the object's own settings. Parents record a re-ordering of children that exist on both sides; adding or removing
/// a child is that child's change only.
/// </summary>
public static class DesignDiff
{
    public static List<ObjectChange> Compare(AppDoc? before, AppDoc after)
    {
        var list = new List<ObjectChange>();
        var app = after.Key;
        Add(list, "Application", app, after.Name,
            before == null ? null : Own(before, ["forms", "views"], ("formOrder", Order(before.Forms.Select(f => f.Key), after.Forms.Select(f => f.Key))), ("viewOrder", Order(before.Views.Select(v => v.Key), after.Views.Select(v => v.Key)))),
            Own(after, ["forms", "views"], ("formOrder", Order(after.Forms.Select(f => f.Key), before?.Forms.Select(f => f.Key))), ("viewOrder", Order(after.Views.Select(v => v.Key), before?.Views.Select(v => v.Key)))));

        foreach (var (b, a) in Pair(before?.Forms, after.Forms, f => f.Id, f => f.Key))
            Form(list, app, b, a);
        foreach (var (b, a) in Pair(before?.Views, after.Views, v => v.Id, v => v.Key))
        {
            var v = (a ?? b)!;
            Add(list, "View", $"{app}/view:{v.Key}", v.Name, b == null ? null : Own(b, []), a == null ? null : Own(a, []));
        }
        return list;
    }

    private static void Form(List<ObjectChange> list, string app, FormDoc? b, FormDoc? a)
    {
        var f = (a ?? b)!;
        var fk = $"{app}/{f.Key}";
        Add(list, "Form", fk, f.Name,
            b == null ? null : Own(b, ["sections", "detached", "workflows"], ("sectionOrder", Order(b.Sections.Select(s => s.Key), a?.Sections.Select(s => s.Key)))),
            a == null ? null : Own(a, ["sections", "detached", "workflows"], ("sectionOrder", Order(a.Sections.Select(s => s.Key), b?.Sections.Select(s => s.Key)))));

        foreach (var (bs, s) in Pair(b?.Sections, a?.Sections ?? [], x => x.Id, x => x.Key))
        {
            var sec = (s ?? bs)!;
            Add(list, "Section", $"{fk}#{sec.Key}", $"{f.Name} › {sec.Name}",
                bs == null ? null : Own(bs, ["fields"], ("fieldOrder", Order(bs.Fields.Select(x => x.Key), s?.Fields.Select(x => x.Key)))),
                s == null ? null : Own(s, ["fields"], ("fieldOrder", Order(s.Fields.Select(x => x.Key), bs?.Fields.Select(x => x.Key)))));
        }

        var bFields = Placed(b);
        var aFields = Placed(a);
        foreach (var (bf, af) in Pair(bFields.Select(x => x.Field).ToList(), aFields.Select(x => x.Field).ToList(), x => x.Id, x => x.Key))
        {
            var x = (af ?? bf)!;
            var bSec = bf == null ? null : bFields.First(p => p.Field == bf).Section;
            var aSec = af == null ? null : aFields.First(p => p.Field == af).Section;
            Add(list, "Field", $"{fk}.{x.Key}", $"{f.Name} › {(x.Type == FieldType.Prompt ? x.Key : x.Label)}",
                bf == null ? null : Own(bf, [], ("section", bSec ?? "(detached)")),
                af == null ? null : Own(af, [], ("section", aSec ?? "(detached)")));
        }

        foreach (var (bw, w) in Pair(b?.Workflows, a?.Workflows ?? [], x => x.Id, x => x.Key))
        {
            var wf = (w ?? bw)!;
            var wk = $"{fk}/{wf.Key}";
            Add(list, "Workflow", wk, $"{f.Name} › {wf.Name}",
                bw == null ? null : Own(bw, ["phases", "routes"], ("phaseOrder", Order(bw.Phases.Select(p => p.Key), w?.Phases.Select(p => p.Key)))),
                w == null ? null : Own(w, ["phases", "routes"], ("phaseOrder", Order(w.Phases.Select(p => p.Key), bw?.Phases.Select(p => p.Key)))));
            foreach (var (bp, p) in Pair(bw?.Phases, w?.Phases ?? [], x => x.Id, x => x.Key))
            {
                var ph = (p ?? bp)!;
                Add(list, "Phase", $"{wk}/{ph.Key}", $"{wf.Name} › {ph.Name}", bp == null ? null : Own(bp, []), p == null ? null : Own(p, []));
            }
            var bRoutes = bw?.Routes ?? [];
            var aRoutes = w?.Routes ?? [];
            var keys = new Dictionary<string, int>();
            string RouteKey(RouteDoc r)
            {
                var k = $"{wk}/{r.From}->{r.To}:{r.Kind}";
                var n = keys[k] = keys.GetValueOrDefault(k) + 1;
                return n > 1 ? $"{k}#{n}" : k;
            }
            foreach (var r in aRoutes)
            {
                var old = r.Id is { } id ? bRoutes.FirstOrDefault(x => x.Id == id) : null;
                Add(list, "Route", RouteKey(r), $"{wf.Name} › {Name(w, r.From)} → {Name(w, r.To)}", old == null ? null : Own(old, []), Own(r, []));
            }
            foreach (var r in bRoutes.Where(r => aRoutes.All(x => x.Id == null || x.Id != r.Id)))
                Add(list, "Route", RouteKey(r), $"{wf.Name} › {Name(bw, r.From)} → {Name(bw, r.To)}", Own(r, []), null);
        }
    }

    private static string Name(WorkflowDoc? w, string key) => w?.Phases.FirstOrDefault(p => p.Key == key)?.Name ?? key;

    private static List<(FieldDoc Field, string? Section)> Placed(FormDoc? f) =>
        f == null ? [] : f.Sections.SelectMany(s => s.Fields.Select(x => (x, (string?)s.Key))).Concat(f.Detached.Select(x => (x, (string?)null))).ToList();

    /// <summary>Pairs (before, after) matched by ID, then by design name for objects that don't have an ID yet.</summary>
    private static IEnumerable<(T? Before, T? After)> Pair<T>(IReadOnlyList<T>? before, IReadOnlyList<T> after, Func<T, int?> id, Func<T, string> key) where T : class
    {
        var b = before?.ToList() ?? [];
        var used = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var a in after)
        {
            var match = id(a) is { } i ? b.FirstOrDefault(x => id(x) == i) : null;
            match ??= id(a) == null ? b.FirstOrDefault(x => id(x) == null && !used.Contains(x) && string.Equals(key(x), key(a), StringComparison.OrdinalIgnoreCase)) : null;
            if (match != null) used.Add(match);
            yield return (match, a);
        }
        foreach (var x in b.Where(x => !used.Contains(x))) yield return (x, null);
    }

    /// <summary>Order of children that exist on both sides (adds and removes don't count as re-ordering).</summary>
    private static string[] Order(IEnumerable<string> mine, IEnumerable<string>? other)
    {
        if (other == null) return [.. mine];
        var common = other.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return mine.Where(common.Contains).ToArray();
    }

    private static JsonObject Own<T>(T obj, string[] drop, params (string Name, object Value)[] extra)
    {
        var node = JsonSerializer.SerializeToNode(obj, DesignDocs.Json)!.AsObject();
        node.Remove("id");
        foreach (var d in drop) node.Remove(d);
        foreach (var (n, v) in extra) node[n] = JsonSerializer.SerializeToNode(v, DesignDocs.Json);
        return node;
    }

    private static void Add(List<ObjectChange> list, string type, string key, string label, JsonObject? before, JsonObject? after)
    {
        var b = before?.ToJsonString(DesignDocs.Json);
        var a = after?.ToJsonString(DesignDocs.Json);
        if (b == a) return;
        // A field's re-ordering inside its section is recorded by the section, not by every field that moved.
        list.Add(new ObjectChange(type, key, label, before == null ? "Created" : after == null ? "Deleted" : "Updated", b, a));
    }

    // ------------------------------------------------------------------ property-level view

    public static IReadOnlyList<PropertyChange> Properties(string? before, string? after)
    {
        var b = Flatten(before);
        var a = Flatten(after);
        var names = b.Keys.Concat(a.Keys).Distinct().ToList();
        var result = new List<PropertyChange>();
        foreach (var n in names)
        {
            b.TryGetValue(n, out var ov);
            a.TryGetValue(n, out var nv);
            if (ov == nv) continue;
            if (before != null && after != null && string.IsNullOrEmpty(ov) && string.IsNullOrEmpty(nv)) continue;
            result.Add(new PropertyChange(Humanize(n), ov, nv));
        }
        return result;
    }

    private static Dictionary<string, string?> Flatten(string? json)
    {
        var d = new Dictionary<string, string?>();
        if (string.IsNullOrEmpty(json)) return d;
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch (JsonException) { d["value"] = json; return d; }
        if (node is not JsonObject o) { d["value"] = node?.ToJsonString(); return d; }
        foreach (var (k, v) in o) d[k] = Text(v);
        return d;
    }

    private static string? Text(JsonNode? v) => v switch
    {
        null => null,
        JsonArray arr => arr.Count == 0 ? null : string.Join(", ", arr.Select(x => x is JsonObject ? x.ToJsonString() : x?.ToString())),
        JsonObject obj => obj.ToJsonString(),
        JsonValue val when val.TryGetValue<bool>(out var bo) => bo ? "yes" : "no",
        _ => v.ToString(),
    };

    private static string Humanize(string camel)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(camel, "([a-z])([A-Z])", "$1 $2");
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    /// <summary>Collapse configuration audit entries between two versions into one change per object (first before, last after).</summary>
    public static List<ObjectChange> FromAudit(IEnumerable<ConfigAuditEntry> entries) =>
        entries.OrderBy(e => e.Id).GroupBy(e => (e.ObjectType, e.ObjectKey)).Select(g =>
        {
            var first = g.First();
            var last = g.Last();
            var before = first.Before;
            var after = last.After;
            var action = before == null && after == null ? last.Action : before == null ? "Created" : after == null ? "Deleted" : "Updated";
            return new ObjectChange(g.Key.ObjectType, g.Key.ObjectKey, Label(after ?? before, g.Key.ObjectKey), action, before, after);
        }).Where(c => c.Before != c.After || c.Before == null).ToList();

    private static string Label(string? json, string fallback)
    {
        if (json == null) return fallback;
        try
        {
            if (JsonNode.Parse(json) is JsonObject o)
                return (o["name"] ?? o["label"])?.ToString() is { Length: > 0 } n ? n : fallback;
        }
        catch (JsonException) { }
        return fallback;
    }
}
