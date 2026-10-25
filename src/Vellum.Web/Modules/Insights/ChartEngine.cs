using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Views;
using Vellum.Infrastructure;
using Vellum.Modules.Insights.Charts;

namespace Vellum.Modules.Insights;

/// <summary>
/// Evaluates chart definitions (RPT §7.1–7.3). Every data point starts from <see cref="ViewService.Query"/>, i.e. from
/// the viewing user's security-trimmed record set (INV-081, RPT-102). Relative date ranges are resolved at evaluation
/// time (INV-125). Each record counts once per category/series bucket even when the view repeats it.
/// </summary>
[ScopedService]
public sealed class ChartEngine(VellumDbContext db, AccessService access, ViewService views, ConfigCache cfg, UserDirectory dir, CurrentUser me)
{
    public const int MaxRows = 50_000;
    private const string Other = "__other";
    private const string None = "_none";
    private ConfigModel Cfg => cfg.Current;

    public sealed class Evaluation
    {
        public ChartData? Chart { get; init; }
        public List<ChartIssue> Issues { get; init; } = [];
        public string RangeText { get; init; } = "";
        public int Records { get; init; }
        public bool Truncated { get; init; }
        public DateTime ComputedAt { get; init; } = DateTime.UtcNow;
        public bool Ok => Chart != null && Issues.Count == 0;
    }

    private sealed record Row(Guid Id, int FormId, int? PhaseId, PhaseType PhaseType, DateTime CreatedAt, DateTime ModifiedAt, DateOnly? DueDate, int? LocationId, int CreatedById);

    private sealed record Val(Guid RecordId, string FieldKey, int Index, string? Text, decimal? Number, DateTime? Date, int? RefId);

    private sealed record Bucket(string Key, string Label, double Order, DateOnly? Date = null, string? Tone = null);

    private sealed class Prepared
    {
        public required AppDefinition App { get; init; }
        public required ViewDefinition View { get; init; }
        public required List<ChartColumn> Columns { get; init; }
        public ChartColumn? Cat { get; init; }
        public ChartColumn? Cmp { get; init; }
        public ChartColumn? ValueCol { get; init; }
        public required List<Row> Rows { get; init; }
        public required ILookup<(Guid, string), Val> Values { get; init; }
        public required ILookup<Guid, int> Assignees { get; init; }
        public bool Truncated { get; init; }
        public (DateOnly? From, DateOnly? To) Range { get; init; }
        public required IQueryable<Record> Base { get; init; }
        public Dictionary<int, Location> Locations { get; init; } = [];
    }

    private sealed class Acc { public double Sum; public int N; public readonly HashSet<Guid> Ids = []; }

    private sealed class Computed
    {
        public List<Bucket> Cats { get; } = [];
        public List<Bucket> Sers { get; } = [];
        public Dictionary<(string C, string S), Acc> Cells { get; } = [];
        public Dictionary<Guid, (HashSet<string> C, HashSet<string> S)> RowKeys { get; } = [];
        public double GaugeSum; public int GaugeN;
        public bool KeywordColors;
    }

    /// <summary>Validate (INV-124) and evaluate a definition for the current user at <paramref name="now"/>.</summary>
    /// <param name="drill">Builds the drill-through link for a (category key, series key) bucket.</param>
    public async Task<Evaluation> EvaluateAsync(ChartDefinition def, string id, string title, Func<string, string?, string>? drill = null,
        int width = 560, int height = 260, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var (p, issues) = await PrepareAsync(def, at);
        if (p == null) return new Evaluation { Issues = issues, ComputedAt = at };
        var c = Compute(def, p);
        var today = DateOnly.FromDateTime(at);
        var rangeText = RelativeDates.Describe(def, today) + LocationText(def);

        var kind = def.Kind;
        var valueLabel = ValueLabel(def, p);
        var fmt = def.Aggregate == "count" ? ValueFormat.Count : ValueFormat.Decimal;

        if (kind == ChartKind.Gauge)
        {
            double? gv = c.GaugeN == 0 && def.Aggregate == "avg" ? null : def.Aggregate switch
            {
                "count" => c.GaugeN, "sum" => c.GaugeSum, _ => Math.Round(c.GaugeSum / Math.Max(1, c.GaugeN), 2),
            };
            var min = def.GaugeMin ?? 0;
            var max = def.GaugeMax ?? 100;
            var bands = def.Ranges.Where(r => r.Min != null || r.Max != null)
                .Select(r => new GaugeBand(r.Min ?? min, r.Max ?? max, r.Tone, r.Label ?? DefaultBandLabel(r.Tone))).ToList();
            return new Evaluation
            {
                Chart = new ChartData
                {
                    Id = id, Title = title, Kind = ChartKind.Gauge, GaugeValue = gv, GaugeMin = min, GaugeMax = max, Bands = bands,
                    GaugeLabel = valueLabel, GaugeHref = drill?.Invoke("_all", null), Format = fmt,
                },
                RangeText = rangeText, Records = p.Rows.Count, Truncated = p.Truncated, ComputedAt = at,
            };
        }

        var categories = c.Cats.Select(b => new ChartCategory(b.Key, b.Label)).ToList();
        var series = new List<ChartSeries>();
        for (var j = 0; j < c.Sers.Count; j++)
        {
            var s = c.Sers[j];
            var vals = new double?[c.Cats.Count];
            var hrefs = new string?[c.Cats.Count];
            for (var i = 0; i < c.Cats.Count; i++)
            {
                c.Cells.TryGetValue((c.Cats[i].Key, s.Key), out var acc);
                vals[i] = def.Aggregate switch
                {
                    "count" => acc?.N ?? 0,
                    "sum" => acc?.Sum ?? 0,
                    _ => acc is { N: > 0 } ? Math.Round(acc.Sum / acc.N, 2) : null,
                };
                if (acc != null && drill != null) hrefs[i] = drill(c.Cats[i].Key, p.Cmp == null ? null : s.Key);
            }
            var tone = s.Key == Other ? "muted" : c.KeywordColors && s.Tone != null ? s.Tone : "s" + (j % 8 + 1);
            series.Add(new ChartSeries { Key = s.Key, Name = s.Label, Values = vals, Hrefs = hrefs, Tone = tone });
        }

        var dataIssues = ChartValidator.ValidateData(def, series.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value));
        if (dataIssues.Count > 0) return new Evaluation { Issues = dataIssues, RangeText = rangeText, ComputedAt = at };

        // Sort (single series only; Pareto sorts itself).
        if (def.Sort != "none" && series.Count == 1 && kind is not (ChartKind.Pareto or ChartKind.Donut))
        {
            var idx = Enumerable.Range(0, categories.Count).ToList();
            idx = def.Sort == "asc" ? idx.OrderBy(i => series[0].Values[i] ?? 0).ToList() : idx.OrderByDescending(i => series[0].Values[i] ?? 0).ToList();
            categories = idx.Select(i => categories[i]).ToList();
            series[0] = new ChartSeries
            {
                Key = series[0].Key, Name = series[0].Name, Tone = series[0].Tone,
                Values = idx.Select(i => series[0].Values[i]).ToArray(), Hrefs = idx.Select(i => series[0].Hrefs?[i]).ToArray(),
            };
        }

        var limits = await LimitsAsync(def, p, today);
        var chart = new ChartData
        {
            Id = id, Title = title, Kind = kind, Categories = categories, Series = series, Format = fmt,
            CategoryHeader = p.Cat?.Label ?? "Category", YTitle = def.YTitle ?? (kind is ChartKind.Bar or ChartKind.StackedBar ? null : valueLabel),
            XTitle = def.XTitle, Limits = limits, HideTotal = def.HideTotal, AverageRow = def.Aggregate == "avg", NoShare = def.Aggregate == "avg",
            Ordered = p.Cat?.IsDate == true, MarkOutOfControl = limits.Count > 0 && kind == ChartKind.Line,
            Width = width, Height = height,
            Note = p.Assignees.Count > 0 && (p.Cat?.Key == "assignee" || p.Cmp?.Key == "assignee") ? "A record with several assignees counts once for each of them." : null,
        };
        return new Evaluation { Chart = chart, RangeText = rangeText, Records = p.Rows.Count, Truncated = p.Truncated, ComputedAt = at };
    }

    /// <summary>The records behind one bucket of the chart (drill-through), for the current user.</summary>
    public async Task<(List<Guid> Ids, string Label, List<ChartIssue> Issues)> DrillAsync(ChartDefinition def, string? catKey, string? serKey, DateTime? now = null)
    {
        var (p, issues) = await PrepareAsync(def, now ?? DateTime.UtcNow);
        if (p == null) return ([], "", issues);
        var c = Compute(def, p);
        var ids = p.Rows.Where(r => c.RowKeys.TryGetValue(r.Id, out var k) &&
                (catKey is null or "_all" || k.C.Contains(catKey)) && (serKey == null || k.S.Contains(serKey)))
            .OrderByDescending(r => r.CreatedAt).Select(r => r.Id).ToList();
        var label = string.Join(" · ", new[]
        {
            c.Cats.FirstOrDefault(b => b.Key == catKey)?.Label, serKey == null ? null : c.Sers.FirstOrDefault(b => b.Key == serKey)?.Label,
        }.Where(x => x != null));
        return (ids, label, []);
    }

    // ------------------------------------------------------------------ preparation

    private async Task<(Prepared? P, List<ChartIssue> Issues)> PrepareAsync(ChartDefinition def, DateTime at)
    {
        if (!Cfg.AppsByKey.TryGetValue(def.App ?? "", out var app) || app.Disabled || !access.CanOpenApp(app.Id))
            return (null, [new ChartIssue("App", "You don't have access to this chart's application, or it no longer exists.")]);
        var view = app.Views.FirstOrDefault(v => v.Key == def.View);
        if (view == null) return (null, [new ChartIssue("View", $"The view '{def.View}' no longer exists in {app.Name}.")]);
        var columns = ChartColumns.For(views, view);
        var issues = ChartValidator.Validate(def, columns);
        if (issues.Count > 0) return (null, issues);

        ChartColumn? Col(string? k) => k == null ? null : columns.FirstOrDefault(c => c.Key == k);
        var cat = def.Type == "gauge" ? null : Col(def.Category);
        var cmp = Col(def.Compare);
        var valueCol = def.Aggregate == "count" ? null : Col(def.ValueField);
        foreach (var col in new[] { cat, cmp, valueCol })
            if (col?.Field is { Sensitive: true } && !access.CanSeeSensitive(app.Id))
                return (null, [new ChartIssue("Category", $"{col.Label} is a restricted field, so you can't chart it.")]);

        var q = views.Query(view);
        var today = DateOnly.FromDateTime(at);
        var range = RelativeDates.Resolve(def, today);
        q = ApplyRange(q, def.DateField ?? "created", range.From, range.To);

        var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id);
        if (def.LocationScope != "all")
        {
            var locId = def.LocationMine ? me.Info?.LocationId : def.LocationId;
            var set = LocationSet(locations, locId, def.LocationScope);
            q = q.Where(r => r.LocationId != null && set.Contains(r.LocationId.Value));
        }

        var rows = await q.AsNoTracking()
            .Select(r => new Row(r.Id, r.FormId, r.PhaseId, r.PhaseType, r.CreatedAt, r.ModifiedAt, r.DueDate, r.LocationId, r.CreatedById))
            .Take(MaxRows + 1).ToListAsync();
        var truncated = rows.Count > MaxRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var keys = new[] { cat, cmp, valueCol }.Where(c => c?.Field != null).Select(c => c!.Key).Distinct().ToList();
        var ids = q.Select(r => r.Id);
        var values = keys.Count == 0 ? [] : await db.RecordValues.AsNoTracking()
            .Where(v => v.RowId == null && keys.Contains(v.FieldKey) && ids.Contains(v.RecordId))
            .Select(v => new Val(v.RecordId, v.FieldKey, v.Index, v.Text, v.Number, v.Date, v.RefId)).ToListAsync();
        var needAssignees = cat?.Key == "assignee" || cmp?.Key == "assignee";
        var assignees = needAssignees
            ? await db.Assignments.AsNoTracking().Where(a => a.Status == AssignmentStatus.Active && ids.Contains(a.RecordId)).Select(a => new { a.RecordId, a.UserId }).Distinct().ToListAsync()
            : [];

        return (new Prepared
        {
            App = app, View = view, Columns = columns, Cat = cat, Cmp = cmp, ValueCol = valueCol, Rows = rows,
            Values = values.ToLookup(v => (v.RecordId, v.FieldKey)), Assignees = assignees.ToLookup(a => a.RecordId, a => a.UserId),
            Truncated = truncated, Range = range, Base = views.Query(view), Locations = locations,
        }, []);
    }

    private IQueryable<Record> ApplyRange(IQueryable<Record> q, string field, DateOnly? from, DateOnly? to)
    {
        if (from == null && to == null) return q;
        var f = from is { } a ? InsightsQueries.Utc(a) : DateTime.MinValue;
        var t = to is { } b ? InsightsQueries.Utc(b.AddDays(1)) : DateTime.MaxValue;
        switch (field)
        {
            case "created": return q.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
            case "modified": return q.Where(r => r.ModifiedAt >= f && r.ModifiedAt < t);
            case "due":
                var df = from ?? DateOnly.MinValue;
                var dt = to ?? DateOnly.MaxValue;
                return q.Where(r => r.DueDate >= df && r.DueDate <= dt);
            default:
                return q.Where(r => db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == field && v.Date >= f && v.Date < t));
        }
    }

    private static HashSet<int> LocationSet(Dictionary<int, Location> all, int? id, string scope)
    {
        var set = new HashSet<int>();
        if (id is not { } root || !all.ContainsKey(root)) return set;
        set.Add(root);
        if (scope is "lower" or "both")
        {
            var frontier = new Queue<int>([root]);
            while (frontier.Count > 0)
            {
                var cur = frontier.Dequeue();
                foreach (var child in all.Values.Where(l => l.ParentId == cur)) if (set.Add(child.Id)) frontier.Enqueue(child.Id);
            }
        }
        if (scope is "higher" or "both")
            for (var p = all[root].ParentId; p is { } pid && all.ContainsKey(pid); p = all[pid].ParentId) set.Add(pid);
        return set;
    }

    // ------------------------------------------------------------------ aggregation

    private Computed Compute(ChartDefinition def, Prepared p)
    {
        var c = new Computed();
        var catInfo = new Dictionary<string, Bucket>();
        var serInfo = new Dictionary<string, Bucket>();
        var rawKeys = new Dictionary<Guid, (List<string> C, List<string> S)>();
        var single = new Bucket("_v", ValueLabel(def, p), 0);
        var all = new Bucket("_all", "All records", 0);

        foreach (var r in p.Rows)
        {
            var cats = p.Cat == null ? [all] : Keys(p, r, p.Cat, def.CategoryDates ?? "month").ToList();
            var sers = p.Cmp == null ? [single] : Keys(p, r, p.Cmp, def.CompareDates ?? "month").ToList();
            double? value = def.Aggregate == "count" ? 1 : p.Values[(r.Id, def.ValueField!)].OrderBy(v => v.Index).Select(v => (double?)v.Number).FirstOrDefault();
            if (def.Aggregate == "count" || value != null) { c.GaugeN++; c.GaugeSum += value ?? 0; }
            foreach (var cb in cats) catInfo.TryAdd(cb.Key, cb);
            foreach (var sb in sers) serInfo.TryAdd(sb.Key, sb);
            rawKeys[r.Id] = (cats.Select(x => x.Key).ToList(), sers.Select(x => x.Key).ToList());
            foreach (var cb in cats)
                foreach (var sb in sers)
                {
                    if (!c.Cells.TryGetValue((cb.Key, sb.Key), out var acc)) c.Cells[(cb.Key, sb.Key)] = acc = new Acc();
                    if (!acc.Ids.Add(r.Id)) continue; // each record counts once per bucket
                    if (def.Aggregate == "count") acc.N++;
                    else if (value is { } v) { acc.Sum += v; acc.N++; }
                }
        }

        double Total(string key, bool isCat) => c.Cells.Where(kv => (isCat ? kv.Key.C : kv.Key.S) == key).Sum(kv => def.Aggregate == "count" ? kv.Value.N : kv.Value.Sum);

        // Categories: dates in time order with gaps filled; nominal columns ordered, then folded into "Other" past the limit.
        var catKeyMap = new Dictionary<string, string>();
        if (p.Cat?.IsDate == true)
        {
            var g = def.CategoryDates ?? "month";
            var dates = catInfo.Values.Where(b => b.Date != null).Select(b => b.Date!.Value).ToList();
            var start = p.Range.From is { } rf && p.Cat.Key == (def.DateField ?? "created") ? DateBuckets.Start(rf, g) : dates.DefaultIfEmpty().Min();
            var end = p.Range.To is { } rt && p.Cat.Key == (def.DateField ?? "created") ? DateBuckets.Start(rt, g) : dates.DefaultIfEmpty().Max();
            if (dates.Count > 0 || p.Range.From != null)
            {
                if (dates.Count > 0) { start = start > dates.Min() ? dates.Min() : start; end = end < dates.Max() ? dates.Max() : end; }
                var n = 0;
                int? prevYear = null;
                for (var d = start; d <= end && n < 400; d = DateBuckets.Next(d, g), n++)
                {
                    var withYear = prevYear == null || prevYear != d.Year;
                    prevYear = d.Year;
                    c.Cats.Add(new Bucket(DateBuckets.Key(d), DateBuckets.Label(d, g, withYear), n, d));
                }
            }
            if (catInfo.ContainsKey(None)) c.Cats.Add(catInfo[None]);
            foreach (var b in c.Cats) catKeyMap[b.Key] = b.Key;
        }
        else
        {
            var ordered = OrderBuckets(catInfo.Values, p.Cat, k => Total(k, true));
            var limit = def.Top ?? (def.Type == "heatmap" ? 15 : def.Type == "donut" ? 7 : 20);
            var kept = ordered;
            if (ordered.Count > limit)
            {
                var top = ordered.OrderByDescending(b => Total(b.Key, true)).Take(Math.Max(1, limit - 1)).ToHashSet();
                kept = ordered.Where(top.Contains).ToList();
            }
            c.Cats.AddRange(kept);
            foreach (var b in kept) catKeyMap[b.Key] = b.Key;
            if (kept.Count < ordered.Count)
            {
                c.Cats.Add(new Bucket(Other, "Other", double.MaxValue));
                foreach (var b in ordered.Where(b => !catKeyMap.ContainsKey(b.Key))) catKeyMap[b.Key] = Other;
            }
        }

        // Series: at most 8 (fixed palette; the tail folds into "Other").
        var serKeyMap = new Dictionary<string, string>();
        var sOrdered = p.Cmp?.IsDate == true ? serInfo.Values.OrderBy(b => b.Date).ToList() : OrderBuckets(serInfo.Values, p.Cmp, k => Total(k, false));
        if (p.Cmp?.IsDate == true)
        {
            var g = def.CompareDates ?? "month";
            sOrdered = sOrdered.Select((b, i) => b.Date is { } d ? b with { Label = DateBuckets.Label(d, g, true) } : b).ToList();
        }
        var sKept = sOrdered;
        if (sOrdered.Count > 8)
        {
            var stop = sOrdered.OrderByDescending(b => Total(b.Key, false)).Take(7).ToHashSet();
            sKept = sOrdered.Where(stop.Contains).ToList();
        }
        c.Sers.AddRange(sKept);
        foreach (var b in sKept) serKeyMap[b.Key] = b.Key;
        if (sKept.Count < sOrdered.Count)
        {
            c.Sers.Add(new Bucket(Other, "Other", double.MaxValue));
            foreach (var b in sOrdered.Where(b => !serKeyMap.ContainsKey(b.Key))) serKeyMap[b.Key] = Other;
        }
        c.KeywordColors = p.Cmp?.Kind is "keyword" or "keywords" && c.Sers.Where(s => s.Key != Other).All(s => s.Tone != null)
            && c.Sers.Where(s => s.Key != Other).Select(s => s.Tone).Distinct().Count() == c.Sers.Count(s => s.Key != Other);

        // Fold cells into the kept buckets.
        if (catKeyMap.Values.Contains(Other) || serKeyMap.Values.Contains(Other))
        {
            var folded = new Dictionary<(string, string), Acc>();
            foreach (var ((ck, sk), acc) in c.Cells)
            {
                var key = (catKeyMap.GetValueOrDefault(ck, ck), serKeyMap.GetValueOrDefault(sk, sk));
                if (!folded.TryGetValue(key, out var f)) folded[key] = f = new Acc();
                foreach (var id in acc.Ids) f.Ids.Add(id);
                f.Sum += acc.Sum;
            }
            // Recount N from distinct records (a record in two folded buckets counts once in "Other").
            foreach (var (key, f) in folded)
                f.N = def.Aggregate == "count" ? f.Ids.Count : c.Cells.Where(kv => (catKeyMap.GetValueOrDefault(kv.Key.C, kv.Key.C), serKeyMap.GetValueOrDefault(kv.Key.S, kv.Key.S)) == key).Sum(kv => kv.Value.N);
            c.Cells.Clear();
            foreach (var kv in folded) c.Cells[kv.Key] = kv.Value;
        }
        foreach (var (id, (ck, sk)) in rawKeys)
            c.RowKeys[id] = (ck.Select(k => catKeyMap.GetValueOrDefault(k, k)).ToHashSet(), sk.Select(k => serKeyMap.GetValueOrDefault(k, k)).ToHashSet());
        return c;
    }

    private static List<Bucket> OrderBuckets(IEnumerable<Bucket> buckets, ChartColumn? col, Func<string, double> total)
    {
        var list = buckets.ToList();
        // Configured order (phases, forms, status, keywords, yes/no); people, places and free text by size.
        return col?.Kind is "user" or "users" or "text" or "location"
            ? list.OrderBy(b => b.Key == None ? 1 : 0).ThenByDescending(b => total(b.Key)).ThenBy(b => b.Label, StringComparer.CurrentCultureIgnoreCase).ToList()
            : list.OrderBy(b => b.Order).ThenBy(b => b.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private IEnumerable<Bucket> Keys(Prepared p, Row r, ChartColumn col, string group)
    {
        switch (col.Key)
        {
            case "phase":
                var ph = Cfg.Phase(r.PhaseId);
                yield return ph == null ? new Bucket(None, "(No phase)", 9999)
                    : new Bucket(ph.Key, ph.Name, (ph.Type == PhaseType.Normal ? 0 : 1000) + ph.Order);
                yield break;
            case "form":
                var form = Cfg.FormsById[r.FormId];
                yield return new Bucket(form.Key, form.Name, form.Order);
                yield break;
            case "status":
                yield return r.PhaseType switch
                {
                    PhaseType.Normal => new Bucket("open", "Open", 0),
                    PhaseType.Completed => new Bucket("completed", "Completed", 1),
                    PhaseType.Voided => new Bucket("voided", "Voided", 2),
                    var t => new Bucket(t.ToString().ToLowerInvariant(), t.ToString(), 3),
                };
                yield break;
            case "location":
                yield return r.LocationId is { } lid && p.Locations.TryGetValue(lid, out var loc)
                    ? new Bucket(lid.ToString(), loc.Name, 0) : new Bucket(None, "(No location)", 9999);
                yield break;
            case "creator":
                yield return new Bucket(r.CreatedById.ToString(), dir.Name(r.CreatedById), 0);
                yield break;
            case "assignee":
                var us = p.Assignees[r.Id].Distinct().ToList();
                if (us.Count == 0) yield return new Bucket(None, "(Unassigned)", 9999);
                foreach (var u in us) yield return new Bucket(u.ToString(), dir.Name(u), 0);
                yield break;
            case "created": yield return DateBucket(DateOnly.FromDateTime(r.CreatedAt), group); yield break;
            case "modified": yield return DateBucket(DateOnly.FromDateTime(r.ModifiedAt), group); yield break;
            case "due":
                yield return r.DueDate is { } due ? DateBucket(due, group) : new Bucket(None, "(No due date)", 9999);
                yield break;
        }

        var vals = p.Values[(r.Id, col.Key)].OrderBy(v => v.Index).ToList();
        if (vals.Count == 0)
        {
            yield return new Bucket(None, "(Blank)", 9999);
            yield break;
        }
        foreach (var v in col.Kind is "keywords" or "users" ? vals : vals.Take(1))
        {
            switch (col.Kind)
            {
                case "keyword" or "keywords":
                    var k = Cfg.Keyword(v.RefId);
                    yield return k == null ? new Bucket(None, "(Blank)", 9999) : new Bucket(k.Key, k.Label, k.Order, null, ToneOf(k.Color));
                    break;
                case "bool":
                    yield return v.RefId == 1 ? new Bucket("yes", "Yes", 0) : new Bucket("no", "No", 1);
                    break;
                case "user" or "users":
                    yield return v.RefId is { } uid ? new Bucket(uid.ToString(), dir.Name(uid), 0) : new Bucket(None, "(Blank)", 9999);
                    break;
                case "date":
                    yield return v.Date is { } d ? DateBucket(DateOnly.FromDateTime(d), group) : new Bucket(None, "(Blank)", 9999);
                    break;
                default:
                    var t = (v.Text ?? "").Trim();
                    yield return t.Length == 0 ? new Bucket(None, "(Blank)", 9999) : new Bucket(t, t, 0);
                    break;
            }
        }
    }

    private static Bucket DateBucket(DateOnly d, string g)
    {
        var s = DateBuckets.Start(d, g);
        return new Bucket(DateBuckets.Key(s), DateBuckets.Label(s, g, true), s.DayNumber, s);
    }

    /// <summary>RPT-401: keyword colours from configuration (e.g. CAPA priority) map onto chart tones.</summary>
    private static string? ToneOf(string? color) => color switch
    {
        "danger" => "bad", "warning" => "warn", "success" => "ok", "info" => "info", "primary" => "s1", "secondary" => "muted",
        "violet" => "s7", _ => null,
    };

    private static string DefaultBandLabel(string tone) => tone switch { "ok" => "On target", "warn" => "Watch", "bad" => "Action needed", _ => "In range" };

    private static string ValueLabel(ChartDefinition def, Prepared p) => def.Aggregate switch
    {
        "sum" => $"Sum of {p.ValueCol?.Label ?? def.ValueField}",
        "avg" => $"Average {p.ValueCol?.Label ?? def.ValueField}",
        _ => "Records",
    };

    private string LocationText(ChartDefinition def)
    {
        if (def.LocationScope == "all") return "";
        var name = def.LocationMine ? "my location" : db.Locations.Where(l => l.Id == def.LocationId).Select(l => l.Name).FirstOrDefault() ?? "location";
        return " · " + def.LocationScope switch
        {
            "lower" => $"{name} and below", "higher" => $"{name} and above", "both" => $"{name}, above and below", _ => $"{name} only",
        };
    }

    // ------------------------------------------------------------------ control limits (RPT-301)

    private async Task<List<LimitLine>> LimitsAsync(ChartDefinition def, Prepared p, DateOnly today)
    {
        var lines = new List<LimitLine>();
        if (def.Limits == "manual")
        {
            if (def.Ucl is { } u) lines.Add(new LimitLine(u, def.UclTitle ?? "UCL", "ucl"));
            if (def.Center is { } c) lines.Add(new LimitLine(c, def.CenterTitle ?? "Average", "cl"));
            if (def.Lcl is { } l) lines.Add(new LimitLine(l, def.LclTitle ?? "LCL", "lcl"));
        }
        else if (def.Limits == "calculated")
        {
            var (from, to) = RelativeDates.LimitsPeriod(def, today);
            var field = p.Cat?.IsDate == true ? p.Cat.Key : def.DateField ?? "created";
            var g = p.Cat?.IsDate == true ? def.CategoryDates ?? "month" : "month";
            var q = ApplyRange(p.Base, field, from, to);
            List<DateOnly> dates = field switch
            {
                "created" => (await q.Select(r => r.CreatedAt).ToListAsync()).Select(DateOnly.FromDateTime).ToList(),
                "modified" => (await q.Select(r => r.ModifiedAt).ToListAsync()).Select(DateOnly.FromDateTime).ToList(),
                "due" => (await q.Where(r => r.DueDate != null).Select(r => r.DueDate!.Value).ToListAsync()),
                _ => (await db.RecordValues.Where(v => v.RowId == null && v.FieldKey == field && v.Date != null && q.Select(r => r.Id).Contains(v.RecordId))
                        .Select(v => v.Date!.Value).ToListAsync()).Select(DateOnly.FromDateTime).ToList(),
            };
            var counts = new List<double>();
            for (var d = DateBuckets.Start(from, g); d <= to; d = DateBuckets.Next(d, g))
            {
                var next = DateBuckets.Next(d, g);
                counts.Add(dates.Count(x => x >= d && x < next));
            }
            var (cl, ucl, lcl) = ControlLimits.CChart(counts);
            lines.Add(new LimitLine(ucl, def.UclTitle ?? "UCL", "ucl"));
            lines.Add(new LimitLine(cl, def.CenterTitle ?? "C̄", "cl"));
            lines.Add(new LimitLine(lcl, def.LclTitle ?? "LCL", "lcl"));
        }
        return lines;
    }
}
