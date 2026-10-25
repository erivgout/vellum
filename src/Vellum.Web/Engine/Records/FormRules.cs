using System.Globalization;
using Vellum.Domain;

namespace Vellum.Engine.Records;

public enum SectionState { Hidden = 0, ReadOnly = 1, Editable = 2 }

/// <summary>The visibility matrix (DSN-305), simple conditions and title templates.</summary>
public static class FormRules
{
    /// <summary>
    /// DSN-305. Editable in phase → editable; read-only in phase → read-only; not listed → hidden.
    /// A phase that lists nothing uses the default: everything editable (Normal) or read-only (terminal).
    /// A record in a terminal phase is read-only at most.
    /// </summary>
    public static SectionState StateOf(SectionDefinition s, PhaseDefinition? phase, Record record)
    {
        var terminal = record.IsArchived || (record.WorkflowId != null && record.PhaseType != PhaseType.Normal);
        SectionState state;
        if (phase == null)
            state = SectionState.Editable;
        else if (phase.HiddenSections.Contains(s.Key))
            state = SectionState.Hidden;
        else if (phase.EditableSections.Count == 0 && phase.ReadOnlySections.Count == 0)
            state = phase.Type == PhaseType.Normal ? SectionState.Editable : SectionState.ReadOnly;
        else if (phase.ReadOnlySections.Contains(s.Key))
            state = SectionState.ReadOnly; // INV-135 forbids listing a section in both; read-only wins defensively
        else if (phase.EditableSections.Contains(s.Key) || phase.EditableSections.Contains("*"))
            state = SectionState.Editable;
        else if (phase.ReadOnlySections.Contains("*"))
            state = SectionState.ReadOnly;
        else
            state = SectionState.Hidden;
        return terminal && state == SectionState.Editable ? SectionState.ReadOnly : state;
    }

    /// <summary>
    /// Evaluate a simple condition against record data. Grammar: clauses joined by <c>&amp;&amp;</c> (binds tighter) and <c>||</c>.
    /// Clause: <c>field=value</c>, <c>field!=value</c>, <c>field</c> (has a value) or <c>!field</c> (empty).
    /// Values match keyword keys, yes/no, numbers or text (case-insensitive).
    /// </summary>
    public static bool Matches(string? condition, ValueBag data, FormDefinition form, ConfigModel cfg)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        return condition.Split("||").Any(any => any.Split("&&").All(c => Clause(c.Trim(), data, form, cfg)));
    }

    private static bool Clause(string c, ValueBag data, FormDefinition form, ConfigModel cfg)
    {
        if (c.Length == 0) return true;
        var neg = c.Contains("!=");
        var parts = c.Split(neg ? "!=" : "=", 2);
        var key = parts[0].Trim();
        if (parts.Length == 1)
            return key.StartsWith('!') ? !data.Has(key[1..]) : data.Has(key);
        var expected = parts[1].Trim();
        var field = form.Fields.FirstOrDefault(f => f.Key == key);
        var values = data[key];
        bool hit = field?.Type switch
        {
            FieldType.Keyword or FieldType.KeywordMulti => values.Any(v => string.Equals(cfg.Keyword(v.RefId)?.Key, expected, StringComparison.OrdinalIgnoreCase)),
            FieldType.YesNo => values.Any(v => (v.RefId == 1 ? "yes" : "no") == expected.ToLowerInvariant()),
            FieldType.Number or FieldType.Currency => decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && values.Any(v => v.Number == n),
            _ => values.Any(v => string.Equals(v.Text, expected, StringComparison.OrdinalIgnoreCase)),
        };
        return neg ? !hit : hit;
    }

    /// <summary>Fields the form engine treats as currently shown (PLT-506: field rules only apply while displayed).</summary>
    public static bool IsFieldVisible(FieldDefinition f, ValueBag data, FormDefinition form, ConfigModel cfg) =>
        Matches(f.VisibleWhen, data, form, cfg);

    /// <summary>Template with <c>{field}</c>, <c>{number}</c>, <c>{form}</c> placeholders.</summary>
    public static string ApplyTemplate(string template, Func<string, string> resolve)
    {
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0) { sb.Append(template, i, template.Length - i); break; }
            var close = template.IndexOf('}', open);
            if (close < 0) { sb.Append(template, i, template.Length - i); break; }
            sb.Append(template, i, open - i);
            sb.Append(resolve(template[(open + 1)..close]));
            i = close + 1;
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+[–\-|]\s*$|^\s*[–\-|]\s+", "").Trim();
    }
}
