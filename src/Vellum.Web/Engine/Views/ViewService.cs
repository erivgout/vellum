using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Engine.Views;

public sealed class ViewFilter
{
    public string Field { get; set; } = "";
    /// <summary>eq | ne | set | empty</summary>
    public string Op { get; set; } = "eq";
    public string? Value { get; set; }
}

public sealed class ViewQuery
{
    public required ViewDefinition View { get; init; }
    public string? Search { get; init; }
    public string? Sort { get; init; }
    public bool? Desc { get; init; }
    public int Page { get; init; } = 1;
    public int? PageSize { get; init; }
    /// <summary>Quick filters from the UI: field key → keyword key / yes / no / "$me".</summary>
    public Dictionary<string, string> Filters { get; init; } = [];
}

public sealed record ViewColumn(string Key, string Label, bool Sortable, FieldDefinition? Field);

public sealed class ViewCell
{
    public string Text { get; init; } = "";
    /// <summary>keyword | user | users | date | due | phase | number | text | bool | revision</summary>
    public string Kind { get; init; } = "text";
    public string? Color { get; init; }
    public IReadOnlyList<int> UserIds { get; init; } = [];
    public DateOnly? Date { get; init; }
}

public sealed class ViewRow
{
    public required Record Record { get; init; }
    public required FormDefinition Form { get; init; }
    public PhaseDefinition? Phase { get; init; }
    public Dictionary<string, ViewCell> Cells { get; } = [];
}

public sealed class ViewOutput
{
    public required List<ViewColumn> Columns { get; init; }
    public required List<ViewRow> Rows { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Pages => PageSize == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

/// <summary>Runs views (PLT §2.11). Every query starts from the security-trimmed record set (INV-081).</summary>
public sealed class ViewService(VellumDbContext db, AccessService access, ConfigCache cfgCache, CurrentUser me, UserDirectory dir)
{
    private ConfigModel Cfg => cfgCache.Current;

    public static readonly Dictionary<string, string> SystemColumns = new()
    {
        ["number"] = "Number", ["title"] = "Title", ["phase"] = "Phase", ["due"] = "Due", ["assignees"] = "Assigned to",
        ["created"] = "Created", ["modified"] = "Modified", ["creator"] = "Created by", ["location"] = "Location",
        ["revision"] = "Rev", ["effective"] = "Effective", ["form"] = "Type", ["status"] = "Status",
    };

    public List<FormDefinition> FormsOf(ViewDefinition v)
    {
        var app = Cfg.AppsById[v.AppId];
        return v.Forms.Count > 0
            ? v.Forms.Select(k => Cfg.FormsByKey.GetValueOrDefault(k)).OfType<FormDefinition>().ToList()
            : app.Forms.Where(f => !f.IsActivity).ToList();
    }

    public IQueryable<Record> Query(ViewDefinition v, string? search = null, IReadOnlyDictionary<string, string>? quick = null)
    {
        var forms = FormsOf(v);
        var formIds = forms.Select(f => f.Id).ToList();
        var q = access.Readable().Where(r => formIds.Contains(r.FormId));

        q = v.Status switch
        {
            "open" => q.Where(r => r.PhaseType == PhaseType.Normal && !r.IsArchived),
            "completed" => q.Where(r => r.PhaseType == PhaseType.Completed && !r.IsArchived),
            "voided" => q.Where(r => r.PhaseType == PhaseType.Voided),
            "closed" => q.Where(r => r.PhaseType != PhaseType.Normal && !r.IsArchived),
            "effective" => q.Where(r => r.IsEffective),
            "archived" => q.Where(r => r.IsArchived),
            _ => q.Where(r => !r.IsArchived),
        };

        if (v.Phases.Count > 0)
        {
            var phaseIds = forms.SelectMany(f => f.Workflows).SelectMany(w => w.Phases).Where(p => v.Phases.Contains(p.Key)).Select(p => p.Id).ToList();
            q = q.Where(r => r.PhaseId != null && phaseIds.Contains(r.PhaseId.Value));
        }
        var uid = me.Id;
        if (v.AssignedToMe)
        {
            var ids = access.DelegatorIds.Append(uid).ToList();
            q = q.Where(r => db.Assignments.Any(a => a.RecordId == r.Id && a.Status == AssignmentStatus.Active && ids.Contains(a.UserId)));
        }
        if (v.CreatedByMe) q = q.Where(r => r.CreatedById == uid);
        if (v.OverdueOnly)
        {
            var today = BusinessCalendar.Today;
            q = q.Where(r => r.PhaseType == PhaseType.Normal && r.DueDate < today);
        }

        var filters = string.IsNullOrEmpty(v.FilterJson) ? [] : JsonSerializer.Deserialize<List<ViewFilter>>(v.FilterJson) ?? [];
        if (quick != null) filters.AddRange(quick.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => new ViewFilter { Field = kv.Key, Op = "eq", Value = kv.Value }));
        foreach (var f in filters) q = ApplyFilter(q, forms, f);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(r => r.Number.Contains(s) || r.Title.Contains(s));
        }
        return q;
    }

    private IQueryable<Record> ApplyFilter(IQueryable<Record> q, List<FormDefinition> forms, ViewFilter f)
    {
        var field = forms.SelectMany(x => x.Fields).FirstOrDefault(x => x.Key == f.Field);
        if (field == null) return q;
        var key = field.Key;
        if (f.Op == "set") return q.Where(r => db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == key));
        if (f.Op == "empty") return q.Where(r => !db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == key));

        IQueryable<RecordValue> matches = db.RecordValues.Where(v => v.RowId == null && v.FieldKey == key);
        switch (field.Type)
        {
            case FieldType.Keyword or FieldType.KeywordMulti:
                var kw = field.KeywordList != null && f.Value != null ? Cfg.Keyword(field.KeywordList, f.Value) : null;
                var kid = kw?.Id ?? (int.TryParse(f.Value, out var n) ? n : -1);
                matches = matches.Where(v => v.RefId == kid);
                break;
            case FieldType.YesNo:
                var b = f.Value is "yes" or "true" or "1" ? 1 : 0;
                matches = matches.Where(v => v.RefId == b);
                break;
            case FieldType.User or FieldType.UserMulti:
                var uid = f.Value == "$me" ? me.Id : int.TryParse(f.Value, out var u) ? u : dir.Get(f.Value ?? "")?.Id ?? -1;
                matches = matches.Where(v => v.RefId == uid);
                break;
            default:
                var text = f.Value ?? "";
                matches = matches.Where(v => v.Text == text);
                break;
        }
        return f.Op == "ne"
            ? q.Where(r => !matches.Any(v => v.RecordId == r.Id))
            : q.Where(r => matches.Any(v => v.RecordId == r.Id));
    }

    public List<ViewColumn> Columns(ViewDefinition v)
    {
        var forms = FormsOf(v);
        var cols = v.Columns.Count > 0 ? v.Columns : ["number", "title", "phase", "due", "assignees"];
        return cols.Select(c =>
        {
            if (SystemColumns.TryGetValue(c, out var label)) return new ViewColumn(c, label, c is not ("assignees" or "status"), null);
            var f = forms.SelectMany(x => x.Fields).FirstOrDefault(x => x.Key == c);
            return new ViewColumn(c, f?.Label ?? c, f != null && f.Type is not (FieldType.Link or FieldType.UserMulti or FieldType.KeywordMulti), f);
        }).ToList();
    }

    public async Task<ViewOutput> RunAsync(ViewQuery query)
    {
        var v = query.View;
        var q = Query(v, query.Search, query.Filters);
        var total = await q.CountAsync();
        var sort = query.Sort ?? v.SortBy;
        var desc = query.Desc ?? v.SortDesc;
        q = Sort(q, v, sort, desc);
        var size = Math.Clamp(query.PageSize ?? v.PageSize, 5, 500);
        var page = Math.Max(1, query.Page);
        var records = await q.Skip((page - 1) * size).Take(size).AsNoTracking().ToListAsync();
        return new ViewOutput { Columns = Columns(v), Rows = await BuildRowsAsync(records, Columns(v)), Total = total, Page = page, PageSize = size };
    }

    /// <summary>All matching rows (export). Still security-trimmed.</summary>
    public async Task<ViewOutput> ExportAsync(ViewDefinition v, string? search, Dictionary<string, string> filters)
    {
        var q = Sort(Query(v, search, filters), v, v.SortBy, v.SortDesc);
        var records = await q.Take(10000).AsNoTracking().ToListAsync();
        var cols = Columns(v);
        return new ViewOutput { Columns = cols, Rows = await BuildRowsAsync(records, cols), Total = records.Count, Page = 1, PageSize = records.Count };
    }

    private IQueryable<Record> Sort(IQueryable<Record> q, ViewDefinition v, string sort, bool desc)
    {
        IOrderedQueryable<Record> o = sort switch
        {
            "title" => desc ? q.OrderByDescending(r => r.Title) : q.OrderBy(r => r.Title),
            "phase" => desc ? q.OrderByDescending(r => r.PhaseId) : q.OrderBy(r => r.PhaseId),
            "due" => desc ? q.OrderByDescending(r => r.DueDate) : q.OrderBy(r => r.DueDate == null).ThenBy(r => r.DueDate),
            "created" => desc ? q.OrderByDescending(r => r.CreatedAt) : q.OrderBy(r => r.CreatedAt),
            "modified" => desc ? q.OrderByDescending(r => r.ModifiedAt) : q.OrderBy(r => r.ModifiedAt),
            "revision" => desc ? q.OrderByDescending(r => r.RevisionOrdinal) : q.OrderBy(r => r.RevisionOrdinal),
            "effective" => desc ? q.OrderByDescending(r => r.EffectiveDate) : q.OrderBy(r => r.EffectiveDate),
            "number" or "" => desc ? q.OrderByDescending(r => r.Sequence).ThenByDescending(r => r.Number) : q.OrderBy(r => r.Sequence).ThenBy(r => r.Number),
            _ => SortByField(q, sort, desc),
        };
        return o.ThenByDescending(r => r.RevisionOrdinal);
    }

    private IOrderedQueryable<Record> SortByField(IQueryable<Record> q, string key, bool desc)
    {
        var vals = db.RecordValues.Where(v => v.FieldKey == key && v.RowId == null && v.Index == 0);
        return desc
            ? q.OrderByDescending(r => vals.Where(v => v.RecordId == r.Id).Select(v => v.Number).FirstOrDefault())
               .ThenByDescending(r => vals.Where(v => v.RecordId == r.Id).Select(v => v.Date).FirstOrDefault())
               .ThenByDescending(r => vals.Where(v => v.RecordId == r.Id).Select(v => v.Text).FirstOrDefault())
               .ThenByDescending(r => vals.Where(v => v.RecordId == r.Id).Select(v => v.RefId).FirstOrDefault())
            : q.OrderBy(r => vals.Where(v => v.RecordId == r.Id).Select(v => v.Number).FirstOrDefault())
               .ThenBy(r => vals.Where(v => v.RecordId == r.Id).Select(v => v.Date).FirstOrDefault())
               .ThenBy(r => vals.Where(v => v.RecordId == r.Id).Select(v => v.Text).FirstOrDefault())
               .ThenBy(r => vals.Where(v => v.RecordId == r.Id).Select(v => v.RefId).FirstOrDefault());
    }

    public async Task<List<ViewRow>> BuildRowsAsync(List<Record> records, List<ViewColumn> cols)
    {
        var ids = records.Select(r => r.Id).ToList();
        var fieldKeys = cols.Where(c => c.Field != null).Select(c => c.Key).ToList();
        var values = fieldKeys.Count == 0 ? [] : await db.RecordValues.AsNoTracking()
            .Where(v => ids.Contains(v.RecordId) && v.RowId == null && fieldKeys.Contains(v.FieldKey)).ToListAsync();
        var assigned = cols.Any(c => c.Key == "assignees")
            ? await db.Assignments.AsNoTracking().Where(a => ids.Contains(a.RecordId) && a.Status == AssignmentStatus.Active).ToListAsync()
            : [];
        var locations = cols.Any(c => c.Key == "location") ? await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name) : [];
        var today = BusinessCalendar.Today;
        var tz = RecordService.Tz(me.Info?.TimeZone);

        var rows = new List<ViewRow>();
        foreach (var r in records)
        {
            var form = Cfg.FormsById[r.FormId];
            var phase = Cfg.Phase(r.PhaseId);
            var row = new ViewRow { Record = r, Form = form, Phase = phase };
            foreach (var c in cols)
            {
                row.Cells[c.Key] = c.Key switch
                {
                    "number" => new ViewCell { Text = r.Number, Kind = "number" },
                    "title" => new ViewCell { Text = r.Title },
                    "phase" => new ViewCell { Text = phase?.Name ?? (r.WorkflowId == null ? "Profile" : ""), Kind = "phase", Color = phase?.Type.ToString() },
                    "due" => new ViewCell { Kind = "due", Date = r.PhaseType == PhaseType.Normal ? r.DueDate : null, Text = r.DueDate?.ToString("dd MMM yyyy") ?? "" },
                    "assignees" => new ViewCell { Kind = "users", UserIds = assigned.Where(a => a.RecordId == r.Id).Select(a => a.UserId).Distinct().ToList() },
                    "created" => new ViewCell { Kind = "date", Date = DateOnly.FromDateTime(r.CreatedAt), Text = r.CreatedAt.ToString("dd MMM yyyy") },
                    "modified" => new ViewCell { Kind = "date", Date = DateOnly.FromDateTime(r.ModifiedAt), Text = r.ModifiedAt.ToString("dd MMM yyyy") },
                    "creator" => new ViewCell { Kind = "users", UserIds = [r.CreatedById] },
                    "location" => new ViewCell { Text = r.LocationId is { } lid && locations.TryGetValue(lid, out var ln) ? ln : "" },
                    "revision" => new ViewCell { Text = r.Revision ?? "", Kind = "revision" },
                    "effective" => new ViewCell { Kind = "date", Date = r.EffectiveDate, Text = r.EffectiveDate?.ToString("dd MMM yyyy") ?? "" },
                    "form" => new ViewCell { Text = form.Name },
                    "status" => new ViewCell { Text = r.IsEffective ? "Effective" : r.IsArchived ? (r.ArchiveReason ?? "Archived") : phase?.Type.ToString() ?? "", Kind = "phase", Color = phase?.Type.ToString() },
                    _ => FieldCell(c.Field, values.Where(v => v.RecordId == r.Id && v.FieldKey == c.Key).OrderBy(v => v.Index)
                        .Select(v => new Val(v.Text, v.Number, v.Date, v.RefId, v.RefGuid)).ToList(), r.AppId, tz),
                };
            }
            rows.Add(row);
        }
        return rows;
    }

    private ViewCell FieldCell(FieldDefinition? f, List<Val> vals, int appId, TimeZoneInfo tz)
    {
        if (f == null || vals.Count == 0) return new ViewCell();
        if (f.Sensitive && !access.CanSeeSensitive(appId)) return new ViewCell { Text = "Restricted", Kind = "restricted" };
        return f.Type switch
        {
            FieldType.Keyword => new ViewCell { Kind = "keyword", Text = Cfg.Keyword(vals[0].RefId)?.Label ?? "", Color = Cfg.Keyword(vals[0].RefId)?.Color },
            FieldType.User or FieldType.UserMulti => new ViewCell { Kind = "users", UserIds = vals.Where(v => v.RefId != null).Select(v => v.RefId!.Value).ToList() },
            FieldType.YesNo => new ViewCell { Kind = "bool", Text = vals[0].RefId == 1 ? "Yes" : "No" },
            FieldType.Date => new ViewCell { Kind = "date", Date = vals[0].Date is { } d ? DateOnly.FromDateTime(d) : null, Text = FieldCodec.Display(f, vals, Cfg, dir, tz) },
            _ => new ViewCell { Text = FieldCodec.Display(f, vals, Cfg, dir, tz), Kind = f.Type is FieldType.Number or FieldType.Currency ? "numeric" : "text" },
        };
    }
}
