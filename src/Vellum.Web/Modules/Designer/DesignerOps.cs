using System.Text.RegularExpressions;
using Vellum.Domain;

namespace Vellum.Modules.Designer;

/// <summary>Design-name renames made in an editor, applied to every reference in the draft.</summary>
public sealed class Renames
{
    public string? Form { get; set; }
    public Dictionary<string, string> Sections { get; set; } = [];
    public Dictionary<string, string> Fields { get; set; } = [];
    public Dictionary<string, string> Phases { get; set; } = [];
}

/// <summary>Edits to an application document. Pure functions: they never touch the database.</summary>
public static class DesignerOps
{
    // ------------------------------------------------------------------ forms

    /// <summary>
    /// Replace a form's layout and settings with the editor's version. The form's workflows are kept from the draft (the workflow
    /// designer owns them); renamed and deleted sections/fields are followed through workflows, views and templates.
    /// </summary>
    public static FormDoc ReplaceForm(AppDoc doc, string originalKey, FormDoc incoming, Renames? renames)
    {
        var current = doc.Forms.FirstOrDefault(f => f.Key == originalKey) ?? throw new InvalidOperationException($"Form '{originalKey}' isn't in this application.");
        renames ??= new Renames();
        incoming.Id = current.Id;
        incoming.Workflows = current.Workflows;
        doc.Forms[doc.Forms.IndexOf(current)] = incoming;

        foreach (var (from, to) in renames.Sections.Where(kv => kv.Key != kv.Value))
            foreach (var p in incoming.Workflows.SelectMany(w => w.Phases))
            {
                Rename(p.EditableSections, from, to);
                Rename(p.ReadOnlySections, from, to);
                Rename(p.HiddenSections, from, to);
            }
        var sections = incoming.Sections.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in incoming.Workflows.SelectMany(w => w.Phases))
        {
            p.EditableSections.RemoveAll(s => s != "*" && !sections.Contains(s));
            p.ReadOnlySections.RemoveAll(s => s != "*" && !sections.Contains(s));
            p.HiddenSections.RemoveAll(s => s != "*" && !sections.Contains(s));
        }

        foreach (var (from, to) in renames.Fields.Where(kv => kv.Key != kv.Value))
        {
            foreach (var p in incoming.Workflows.SelectMany(w => w.Phases))
            {
                if (p.AssigneeField == from) p.AssigneeField = to;
                if (p.DueDateField == from) p.DueDateField = to;
            }
            foreach (var r in incoming.Workflows.SelectMany(w => w.Routes)) r.Condition = RenameInCondition(r.Condition, from, to);
            foreach (var x in incoming.AllFields) x.VisibleWhen = RenameInCondition(x.VisibleWhen, from, to);
            incoming.TitleTemplate = incoming.TitleTemplate.Replace("{" + from + "}", "{" + to + "}");
            foreach (var v in ViewsOf(doc, incoming))
            {
                Rename(v.Columns, from, to);
                if (v.SortBy == from) v.SortBy = to;
                if (v.CalendarField == from) v.CalendarField = to;
                foreach (var f in v.Filters.Where(f => f.Field == from)) f.Field = to;
            }
        }

        if (renames.Form is { } oldFormKey && oldFormKey != incoming.Key)
            RenameFormReferences(doc, oldFormKey, incoming.Key);
        return incoming;
    }

    /// <summary>Views of the application that read a form (explicitly, or as "every main form" when the view lists none).</summary>
    public static IEnumerable<ViewDoc> ViewsOf(AppDoc doc, FormDoc form) =>
        doc.Views.Where(v => v.Forms.Contains(form.Key) || (v.Forms.Count == 0 && !form.IsActivity));

    private static void RenameFormReferences(AppDoc doc, string from, string to)
    {
        foreach (var v in doc.Views) Rename(v.Forms, from, to);
        foreach (var f in doc.Forms)
        {
            Rename(f.ParentForms, from, to);
            foreach (var x in f.AllFields) Rename(x.LinkForms, from, to);
            foreach (var p in f.Workflows.SelectMany(w => w.Phases))
            {
                Rename(p.MustCreateForms, from, to);
                Rename(p.MustCompleteForms, from, to);
                Rename(p.ActivityForms, from, to);
            }
        }
    }

    public static FormDoc NewForm(AppDoc doc, string name, string key, string kind, bool starterWorkflow, ICollection<string> takenFormKeys)
    {
        key = DesignDocs.UniqueKey(string.IsNullOrWhiteSpace(key) ? DesignDocs.Slug(name) : DesignDocs.Slug(key), takenFormKeys);
        var form = new FormDoc
        {
            Key = key, Name = name.Trim(), Icon = kind == "activity" ? "bi-check2-square" : kind == "profile" ? "bi-person-vcard" : "bi-file-earmark-text",
            IsActivity = kind == "activity", IsNonWorkflow = kind == "profile",
            NumberPrefix = Prefix(name), TitleTemplate = "{title}",
            Sections =
            [
                new SectionDoc
                {
                    Key = "details", Name = "Details", Tab = "General", Icon = "bi-card-text",
                    Fields =
                    [
                        new FieldDoc { Key = "title", Label = "Title", Type = FieldType.Text, Width = 12, Required = true, MaxLength = 200 },
                        new FieldDoc { Key = "description", Label = "Description", Type = FieldType.TextArea, Width = 12 },
                    ],
                },
            ],
        };
        if (kind != "profile" && starterWorkflow) form.Workflows.Add(StarterWorkflow(form, key, form.Name));
        doc.Forms.Add(form);
        return form;
    }

    private static string Prefix(string name)
    {
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]))).Where(char.IsLetter).ToArray();
        var p = letters.Length >= 2 ? new string(letters.Take(4).ToArray()) : new string(name.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
        return (p.Length == 0 ? "REC" : p) + "-";
    }

    /// <summary>Draft → Review → Completed, with Voided: a valid starting point that passes publish validation.</summary>
    public static WorkflowDoc StarterWorkflow(FormDoc form, string key, string name)
    {
        var all = form.Sections.Select(s => s.Key).ToList();
        return new WorkflowDoc
        {
            Key = key, Name = name,
            Phases =
            [
                new PhaseDoc { Key = "draft", Name = "Draft", Type = PhaseType.Normal, IsStart = true, Assignment = AssignmentMode.Creator, DueDays = 5, Purpose = "Describe the item.", EditableSections = [.. all] },
                new PhaseDoc { Key = "review", Name = "Review", Type = PhaseType.Normal, Assignment = AssignmentMode.Any, DueDays = 5, Purpose = "Review and approve.", ReadOnlySections = ["*"] },
                new PhaseDoc { Key = "completed", Name = "Completed", Type = PhaseType.Completed },
                new PhaseDoc { Key = "voided", Name = "Voided", Type = PhaseType.Voided },
            ],
            Routes =
            [
                new RouteDoc { From = "draft", To = "review", Kind = RouteKind.Forward, Label = "Submit for review" },
                new RouteDoc { From = "draft", To = "voided", Kind = RouteKind.Void, RequireComment = true },
                new RouteDoc { From = "review", To = "completed", Kind = RouteKind.Forward, Label = "Approve", RequireSignature = true, SignatureMeaning = "Approved" },
                new RouteDoc { From = "review", To = "draft", Kind = RouteKind.Back, Label = "Return for changes", RequireComment = true },
                new RouteDoc { From = "review", To = "voided", Kind = RouteKind.Void, RequireComment = true },
            ],
        };
    }

    public static bool DeleteForm(AppDoc doc, string key)
    {
        var f = doc.Forms.FirstOrDefault(x => x.Key == key);
        if (f == null) return false;
        doc.Forms.Remove(f);
        foreach (var v in doc.Views) v.Forms.Remove(key);
        return true;
    }

    // ------------------------------------------------------------------ workflows

    /// <summary>Replace a workflow with the editor's version; renamed phases are followed through required-in rules and view filters.</summary>
    public static WorkflowDoc ReplaceWorkflow(AppDoc doc, string formKey, string originalKey, WorkflowDoc incoming, Renames? renames)
    {
        var form = doc.Forms.FirstOrDefault(f => f.Key == formKey) ?? throw new InvalidOperationException($"Form '{formKey}' isn't in this application.");
        var current = form.Workflows.FirstOrDefault(w => w.Key == originalKey) ?? throw new InvalidOperationException($"Workflow '{originalKey}' isn't on {form.Name}.");
        incoming.Id = current.Id;
        form.Workflows[form.Workflows.IndexOf(current)] = incoming;
        foreach (var (from, to) in (renames?.Phases ?? []).Where(kv => kv.Key != kv.Value))
        {
            foreach (var x in form.AllFields) Rename(x.RequiredInPhases, from, to);
            foreach (var v in ViewsOf(doc, form)) Rename(v.Phases, from, to);
        }
        var keys = form.Workflows.SelectMany(w => w.Phases).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var x in form.AllFields) x.RequiredInPhases.RemoveAll(p => !keys.Contains(p));
        return incoming;
    }

    public static WorkflowDoc AddWorkflow(AppDoc doc, string formKey, string name)
    {
        var form = doc.Forms.First(f => f.Key == formKey);
        var key = DesignDocs.UniqueKey(DesignDocs.Slug(name), form.Workflows.Select(w => w.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var w = StarterWorkflow(form, key, name.Trim());
        form.Workflows.Add(w);
        form.IsNonWorkflow = false;
        return w;
    }

    public static bool DeleteWorkflow(AppDoc doc, string formKey, string key)
    {
        var form = doc.Forms.FirstOrDefault(f => f.Key == formKey);
        var w = form?.Workflows.FirstOrDefault(x => x.Key == key);
        if (w == null) return false;
        form!.Workflows.Remove(w);
        return true;
    }

    // ------------------------------------------------------------------ views

    public static ViewDoc ReplaceView(AppDoc doc, string originalKey, ViewDoc incoming)
    {
        var current = doc.Views.FirstOrDefault(v => v.Key == originalKey) ?? throw new InvalidOperationException($"View '{originalKey}' isn't in this application.");
        incoming.Id = current.Id;
        doc.Views[doc.Views.IndexOf(current)] = incoming;
        if (incoming.IsDefault) foreach (var v in doc.Views.Where(v => v != incoming)) v.IsDefault = false;
        return incoming;
    }

    /// <summary>New view, or a copy of an existing one (DSN-006, DSN-702).</summary>
    public static ViewDoc NewView(AppDoc doc, string name, string? key, string? category, string? copyFrom)
    {
        var taken = doc.Views.Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var src = copyFrom != null ? doc.Views.FirstOrDefault(v => v.Key == copyFrom) : null;
        var v = src != null ? DesignDocs.Clone(src) : new ViewDoc
        {
            Forms = doc.Forms.Where(f => !f.IsActivity).Take(1).Select(f => f.Key).ToList(),
            Columns = ["number", "title", "phase", "due", "assignees"],
        };
        v.Id = null;
        v.IsDefault = false;
        v.Name = name.Trim();
        v.Key = DesignDocs.UniqueKey(DesignDocs.Slug(string.IsNullOrWhiteSpace(key) ? name : key), taken);
        if (!string.IsNullOrWhiteSpace(category)) v.Category = category.Trim();
        doc.Views.Add(v);
        return v;
    }

    public static bool DeleteView(AppDoc doc, string key) => doc.Views.RemoveAll(v => v.Key == key) > 0;

    // ------------------------------------------------------------------ applications

    public static AppDoc NewApp(string name, string key, string? description, string icon, string color, string suite, ICollection<string> takenFormKeys)
    {
        var doc = new AppDoc { Key = key, Name = name.Trim(), Description = description, Icon = icon, Color = color, Suite = suite, DefaultAccess = AppRole.None };
        var formName = name.Trim().EndsWith('s') && name.Trim().Length > 3 ? name.Trim()[..^1] : name.Trim();
        var form = NewForm(doc, formName, key, "main", true, takenFormKeys);
        doc.Views.Add(new ViewDoc { Key = "mine", Name = "Assigned to me", Category = "1. My work", Forms = [form.Key], AssignedToMe = true, Columns = ["number", "title", "phase", "due"], SortBy = "due", SortDesc = false, IsDefault = true });
        doc.Views.Add(new ViewDoc { Key = "open", Name = "Open", Category = "2. Records", Forms = [form.Key], Columns = ["number", "title", "phase", "due", "assignees"] });
        doc.Views.Add(new ViewDoc { Key = "closed", Name = "Completed", Category = "3. Closed", Forms = [form.Key], Status = "completed", Columns = ["number", "title", "modified"], SortBy = "modified" });
        return doc;
    }

    // ------------------------------------------------------------------ helpers

    private static void Rename(List<string> list, string from, string to)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], from, StringComparison.OrdinalIgnoreCase)) list[i] = to;
    }

    /// <summary>Rename a field inside a condition (<c>a=x &amp;&amp; !b</c>), leaving values alone.</summary>
    public static string? RenameInCondition(string? condition, string from, string to)
    {
        if (string.IsNullOrEmpty(condition)) return condition;
        return Regex.Replace(condition, @"(^|\|\||&&)(\s*!?\s*)" + Regex.Escape(from) + @"(?=\s*(!=|=|\|\||&&|$))", m => m.Groups[1].Value + m.Groups[2].Value + to);
    }
}
