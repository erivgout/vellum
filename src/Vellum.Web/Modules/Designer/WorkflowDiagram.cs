using System.Globalization;
using System.Net;
using System.Text;
using Vellum.Domain;

namespace Vellum.Modules.Designer;

/// <summary>One row of the read-only routing table (DSN-612).</summary>
public sealed record RoutingRow(string Key, string Name, string Type, bool Start, List<string> Forward, List<string> Back, List<string> Special,
    List<string> Void, bool Unreachable, bool DeadEnd, bool NoCompletion, int Records);

/// <summary>
/// Server-rendered workflow diagram (DSN-601/612): normal phases left → right in order, completed phases stacked at the end,
/// void/archive phases underneath. Forward edges are solid, back edges dashed arcs above, void edges red, and signed routes
/// carry a pen badge. Styling comes from designer.css (light and dark).
/// </summary>
public static class WorkflowDiagram
{
    private const double NW = 156, NH = 58, GX = 64, Pad = 28, Top = 104, RowGap = 84;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Box(PhaseDoc Phase, double X, double Y, int Col, int Row)
    {
        public double Cx => X + NW / 2;
        public double Cy => Y + NH / 2;
    }

    public static List<RoutingRow> Table(WorkflowDoc w, IReadOnlyDictionary<string, int>? records = null)
    {
        var a = WorkflowAnalysis.Analyze(w);
        string N(string key) => w.Phases.FirstOrDefault(p => p.Key == key)?.Name ?? key;
        List<string> T(PhaseDoc p, RouteKind k) => w.Routes.Where(r => r.From == p.Key && r.Kind == k).Select(r => N(r.To) + (r.RequireSignature ? " ✎" : "") + (r.Condition != null ? " ⓘ" : "")).ToList();
        return w.Phases.Select(p => new RoutingRow(p.Key, p.Name, p.Type.ToString(), p.IsStart, T(p, RouteKind.Forward), T(p, RouteKind.Back), T(p, RouteKind.SpecialForward),
            T(p, RouteKind.Void), a.Unreachable.Contains(p.Key), a.DeadEnds.Contains(p.Key), a.NoCompletion.Contains(p.Key), records?.GetValueOrDefault(p.Key) ?? 0)).ToList();
    }

    public static string Render(WorkflowDoc w, IReadOnlyDictionary<string, int>? records = null, string? title = null)
    {
        var a = WorkflowAnalysis.Analyze(w);
        var normals = w.Phases.Where(p => p.Type == PhaseType.Normal).ToList();
        var completed = w.Phases.Where(p => p.Type == PhaseType.Completed).ToList();
        var others = w.Phases.Where(p => p.Type is not (PhaseType.Normal or PhaseType.Completed)).ToList();
        var boxes = new Dictionary<string, Box>(StringComparer.OrdinalIgnoreCase);
        double X(int col) => Pad + col * (NW + GX);

        for (var i = 0; i < normals.Count; i++) boxes.TryAdd(normals[i].Key, new Box(normals[i], X(i), Top, i, 0));
        var endCol = normals.Count;
        for (var k = 0; k < completed.Count; k++) boxes.TryAdd(completed[k].Key, new Box(completed[k], X(endCol), Top + k * RowGap, endCol, k));
        var mainRows = Math.Max(1, completed.Count);
        var bottomY = Top + mainRows * RowGap + 64;
        if (others.Count > 0)
        {
            var spanEnd = normals.Count > 0 ? X(normals.Count - 1) + NW : X(0) + NW;
            var total = others.Count * (NW + GX) - GX;
            var startX = Math.Max(Pad, (X(0) + spanEnd) / 2 - total / 2);
            for (var k = 0; k < others.Count; k++)
                boxes.TryAdd(others[k].Key, new Box(others[k], startX + k * (NW + GX), bottomY, -1, 99));
        }

        var width = Math.Max(boxes.Values.Select(b => b.X + NW).DefaultIfEmpty(X(0) + NW).Max() + Pad + 16, 360);
        var height = (others.Count > 0 ? bottomY + NH : Top + (mainRows - 1) * RowGap + NH + 70) + 28;

        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg class=\"dz-wf-svg\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {F(width)} {F(height)}\" width=\"{F(width)}\" height=\"{F(height)}\" role=\"img\" aria-label=\"{E(title ?? w.Name)} workflow diagram\">");
        sb.Append("<defs>");
        foreach (var kind in new[] { "fwd", "back", "void", "special" })
            sb.Append($"<marker id=\"dz-arr-{kind}\" viewBox=\"0 0 10 10\" refX=\"8.5\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path class=\"dz-arrow dz-arrow-{kind}\" d=\"M0 0 L10 5 L0 10 z\"/></marker>");
        sb.Append("</defs>");

        if (w.Phases.Count == 0)
        {
            sb.Append($"<text x=\"{F(Pad)}\" y=\"{F(Top)}\" class=\"dz-wf-empty\">Add a starting phase to begin.</text></svg>");
            return sb.ToString();
        }

        // Edges first so nodes paint over their ends.
        var badges = new StringBuilder();
        for (var i = 0; i < w.Routes.Count; i++)
        {
            var r = w.Routes[i];
            if (!boxes.TryGetValue(r.From, out var from) || !boxes.TryGetValue(r.To, out var to)) continue;
            var (path, mid) = Path(from, to, r.Kind);
            var kind = r.Kind switch { RouteKind.Back => "back", RouteKind.Void => "void", RouteKind.SpecialForward => "special", _ => "fwd" };
            var tip = $"{Label(r.Kind)}: {from.Phase.Name} → {to.Phase.Name}" + (r.Label != null ? $" ({r.Label})" : "") +
                      (r.RequireSignature ? $" · e-signature: {r.SignatureMeaning}" : "") + (r.Condition != null ? $" · when {r.Condition}" : "");
            sb.Append($"<g class=\"dz-edge dz-edge-{kind}\" data-route-index=\"{i}\"><title>{E(tip)}</title>");
            sb.Append($"<path class=\"dz-edge-hit\" d=\"{path}\"/>");
            sb.Append($"<path class=\"dz-edge-line\" d=\"{path}\" marker-end=\"url(#dz-arr-{kind})\"/></g>");
            if (r.RequireSignature)
                badges.Append(Inv, $"<g class=\"dz-sig-badge\" transform=\"translate({F(mid.X)} {F(mid.Y)})\"><title>{E("E-signature: " + r.SignatureMeaning)}</title><circle r=\"10\"/><text class=\"dz-glyph\" text-anchor=\"middle\" dominant-baseline=\"central\">&#xF4C7;</text></g>");
            else if (r.Condition != null)
                badges.Append(Inv, $"<g class=\"dz-cond-badge\" transform=\"translate({F(mid.X)} {F(mid.Y)})\"><title>{E("Condition: " + r.Condition)}</title><circle r=\"7\"/><text text-anchor=\"middle\" dominant-baseline=\"central\">?</text></g>");
        }
        sb.Append(badges);

        foreach (var b in boxes.Values)
        {
            var p = b.Phase;
            var type = p.Type.ToString().ToLowerInvariant();
            var cls = $"dz-node dz-node-{type}" + (p.IsStart ? " is-start" : "") + (a.Unreachable.Contains(p.Key) && p.Type == PhaseType.Normal ? " has-error" : "") +
                      (a.DeadEnds.Contains(p.Key) ? " has-error" : "");
            var glyph = p.IsStart ? "&#xF4F4;" : p.Type switch
            {
                PhaseType.Completed => "&#xF26A;", PhaseType.Voided => "&#xF567;", PhaseType.Archived or PhaseType.Obsolete => "&#xF10D;", _ => "&#xF518;",
            };
            var sub = p.Type == PhaseType.Normal
                ? $"{(p.DueDays is { } d ? d + "d" : p.DueDateField != null ? "by date" : "5d")} · {Assign(p)}"
                : p.Type.ToString();
            var n = records?.GetValueOrDefault(p.Key) ?? 0;
            var tip = $"{p.Name} ({p.Type}{(p.IsStart ? ", start" : "")})" + (p.Type == PhaseType.Normal ? $" · assigned to {Assign(p)} · due {sub.Split(' ')[0]}" : "") +
                      (n > 0 ? $" · {n} record{(n == 1 ? "" : "s")} here" : "");
            sb.Append(Inv, $"<g class=\"{cls}\" data-phase-key=\"{E(p.Key)}\" tabindex=\"0\" role=\"button\" aria-label=\"{E(tip)}\"><title>{E(tip)}</title>");
            sb.Append(Inv, $"<rect class=\"dz-node-box\" x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(NW)}\" height=\"{F(NH)}\" rx=\"11\"/>");
            sb.Append(Inv, $"<circle class=\"dz-node-dot\" cx=\"{F(b.X + 20)}\" cy=\"{F(b.Cy)}\" r=\"11\"/>");
            sb.Append(Inv, $"<text class=\"dz-glyph dz-node-glyph\" x=\"{F(b.X + 20)}\" y=\"{F(b.Cy)}\" text-anchor=\"middle\" dominant-baseline=\"central\">{glyph}</text>");
            sb.Append(Inv, $"<text class=\"dz-node-name\" x=\"{F(b.X + 38)}\" y=\"{F(b.Y + 25)}\">{E(Trim(p.Name, 17))}</text>");
            sb.Append(Inv, $"<text class=\"dz-node-sub\" x=\"{F(b.X + 38)}\" y=\"{F(b.Y + 42)}\">{E(Trim(sub, 22))}</text>");
            if (n > 0)
                sb.Append(Inv, $"<g class=\"dz-node-count\"><rect x=\"{F(b.X + NW - 30)}\" y=\"{F(b.Y - 9)}\" width=\"30\" height=\"18\" rx=\"9\"/><text x=\"{F(b.X + NW - 15)}\" y=\"{F(b.Y)}\" text-anchor=\"middle\" dominant-baseline=\"central\">{(n > 999 ? "999+" : n.ToString(Inv))}</text></g>");
            if (cls.Contains("has-error"))
                sb.Append(Inv, $"<g class=\"dz-node-flag\"><circle cx=\"{F(b.X + 6)}\" cy=\"{F(b.Y + 6)}\" r=\"9\"/><text x=\"{F(b.X + 6)}\" y=\"{F(b.Y + 6)}\" text-anchor=\"middle\" dominant-baseline=\"central\">!</text></g>");
            sb.Append("</g>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Assign(PhaseDoc p) => p.Assignment switch
    {
        AssignmentMode.Creator => "creator",
        AssignmentMode.Field => p.AssigneeField ?? "field",
        AssignmentMode.Group => "@" + (p.AssigneeGroup ?? "group"),
        _ => p.AssigneeField != null ? $"any ({p.AssigneeField})" : "any user",
    };

    private static string Label(RouteKind k) => k switch
    {
        RouteKind.Back => "Back", RouteKind.Void => "Void", RouteKind.SpecialForward => "Special forward", _ => "Forward",
    };

    private readonly record struct Pt(double X, double Y);

    /// <summary>SVG path for an edge plus the point where its badge goes.</summary>
    private static (string Path, Pt Mid) Path(Box a, Box b, RouteKind kind)
    {
        Pt p0, p1, p2, p3;
        if (a.Phase == b.Phase)
        {
            p0 = new(a.X + NW - 34, a.Y); p1 = new(a.X + NW - 34, a.Y - 40); p2 = new(a.X + NW + 22, a.Y - 22); p3 = new(a.X + NW, a.Y + 14);
        }
        else if (b.Row == 99 && a.Row != 99)
        {
            var off = -18.0;
            p0 = new(a.Cx + off, a.Y + NH); p1 = new(a.Cx + off, a.Y + NH + 48); p2 = new(b.Cx, b.Y - 54); p3 = new(b.Cx, b.Y);
        }
        else if (Math.Abs(a.Y - b.Y) < 1 && b.X > a.X && kind != RouteKind.Back)
        {
            var cols = (int)Math.Round((b.X - a.X) / (NW + GX));
            if (cols <= 1)
            {
                p0 = new(a.X + NW, a.Cy); p3 = new(b.X, b.Cy);
                p1 = new(p0.X + 20, p0.Y); p2 = new(p3.X - 20, p3.Y);
            }
            else
            {
                var d = Math.Min(22 + 10 * cols, 62);
                p0 = new(a.Cx + 18, a.Y + NH); p1 = new(p0.X, p0.Y + d); p3 = new(b.Cx - 18, b.Y + NH); p2 = new(p3.X, p3.Y + d);
            }
        }
        else if (Math.Abs(a.Y - b.Y) < 1)
        {
            // Leftward (or a back edge): an arc over the row.
            var cols = Math.Max(1, (int)Math.Round(Math.Abs(b.X - a.X) / (NW + GX)));
            var h = Math.Min(26 + 15 * cols, 92);
            p0 = new(a.Cx - 18, a.Y); p1 = new(p0.X, p0.Y - h); p3 = new(b.Cx + 18, b.Y); p2 = new(p3.X, p3.Y - h);
        }
        else if (b.X > a.X + 1)
        {
            var cols = (int)Math.Round((b.X - a.X) / (NW + GX));
            if (cols <= 1)
            {
                p0 = new(a.X + NW, a.Cy); p3 = new(b.X, b.Cy); p1 = new(p0.X + GX * 0.6, p0.Y); p2 = new(p3.X - GX * 0.6, p3.Y);
            }
            else
            {
                var d = Math.Min(22 + 10 * cols, 62);
                p0 = new(a.Cx + 18, a.Y + NH); p1 = new(p0.X, p0.Y + d); p3 = new(b.X, b.Cy); p2 = new(b.X - 48, b.Cy);
            }
        }
        else
        {
            // Anything else (e.g. back from a terminal phase): a gentle curve between centres of the facing sides.
            p0 = new(a.Cx, a.Y); p3 = new(b.Cx, b.Y + NH);
            p1 = new(p0.X, p0.Y - 40); p2 = new(p3.X, p3.Y + 40);
        }
        var mid = new Pt((p0.X + 3 * p1.X + 3 * p2.X + p3.X) / 8, (p0.Y + 3 * p1.Y + 3 * p2.Y + p3.Y) / 8);
        return ($"M{F(p0.X)} {F(p0.Y)} C{F(p1.X)} {F(p1.Y)} {F(p2.X)} {F(p2.Y)} {F(p3.X)} {F(p3.Y)}", mid);
    }

    private static string F(double v) => Math.Round(v, 1).ToString(Inv);
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
