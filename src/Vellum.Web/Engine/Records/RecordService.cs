using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure.Files;

namespace Vellum.Engine.Records;

public sealed class CreateRequest
{
    public required string FormKey { get; init; }
    public string? WorkflowKey { get; init; }
    public string? StartPhaseKey { get; init; }
    public Guid? ParentId { get; init; }
    /// <summary>Initial data (e.g. parsed from the New page).</summary>
    public RecordData? Data { get; init; }
    /// <summary>Programmatic values: field key → value (keyword keys, user IDs, decimals, DateOnly, Guid, lists…).</summary>
    public Dictionary<string, object?>? Values { get; init; }
    /// <summary>Subform rows to create: section key → rows of field values.</summary>
    public Dictionary<string, List<Dictionary<string, object?>>>? RowValues { get; init; }
    /// <summary>Assignees of the start phase. Defaults to the phase's assignment rule, then the creator.</summary>
    public IReadOnlyList<int>? Assignees { get; init; }
    public DateOnly? DueDate { get; init; }
    public int? LocationId { get; init; }
    /// <summary>Skip the caller's creation-rights check (behaviors creating linked records the user may not author directly, PLT-642).</summary>
    public bool SkipAccessCheck { get; init; }
    public List<(string FieldKey, string FileName, string ContentType, Stream Content)>? Files { get; init; }

    // Revision control (DOC-093)
    public string? Number { get; init; }
    public long? Sequence { get; init; }
    public Guid? FamilyId { get; init; }
    public string? Revision { get; init; }
    public int RevisionOrdinal { get; init; }
    public DateTime? CreatedAt { get; init; }
    public int? CreatedById { get; init; }
    public string? CreateComment { get; init; }
}

public sealed record CreateResult(Record? Record, IReadOnlyList<FieldError> Errors)
{
    public bool Ok => Record != null;
}

public sealed class SaveOptions
{
    public int? ExpectedVersion { get; init; }
    /// <summary>System writes (behaviors, jobs) bypass edit rights and the visibility matrix, but not validation.</summary>
    public bool System { get; init; }
    /// <summary>PLT-1901 administrative override (Edit All Fields): needs a reason, recorded as an override.</summary>
    public bool AdminOverride { get; init; }
    public string? Reason { get; init; }
    public List<(string FieldKey, string FileName, string ContentType, Stream Content)> Uploads { get; init; } = [];
    public List<long> RemoveAttachmentIds { get; init; } = [];
}

public sealed record SaveResult(bool Ok, IReadOnlyList<FieldError> Errors, bool Changed = false, string? Message = null)
{
    public static SaveResult Fail(string message) => new(false, [new FieldError(null, message)]);
    public static SaveResult Fail(IReadOnlyList<FieldError> errors) => new(false, errors);
}

/// <summary>
/// Loads, creates and saves records. All writes are validated, attributed and audited in the same transaction
/// (INV-050), use optimistic concurrency (INV-092) and run the form's behaviors.
/// </summary>
public sealed class RecordService(
    VellumDbContext db,
    ConfigCache cfgCache,
    CurrentUser me,
    AccessService access,
    AuditService audit,
    NumberingService numbering,
    AssignmentService assignments,
    NotificationService notify,
    BehaviorRegistry behaviors,
    UserDirectory dir,
    FileStore files,
    IServiceProvider services)
{
    private ConfigModel Cfg => cfgCache.Current;

    public TimeZoneInfo TimeZone => Tz(me.Info?.TimeZone);

    public static TimeZoneInfo Tz(string? id)
    {
        try { return id is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    // ------------------------------------------------------------------ load

    public async Task<RecordSnapshot?> LoadAsync(Guid id)
    {
        var record = await db.Records.FirstOrDefaultAsync(r => r.Id == id);
        if (record == null) return null;
        var values = await db.RecordValues.Where(v => v.RecordId == id).ToListAsync();
        var rows = await db.SubformRows.Where(r => r.RecordId == id).OrderBy(r => r.Order).ToListAsync();
        var atts = await db.Attachments.Include(a => a.UploadedBy).Where(a => a.RecordId == id).OrderBy(a => a.UploadedAt).ToListAsync();
        var form = Cfg.FormsById[record.FormId];

        var data = new RecordData();
        foreach (var g in values.Where(v => v.RowId == null).GroupBy(v => v.FieldKey))
            data.Main.Set(g.Key, g.OrderBy(v => v.Index).Select(ToVal));
        foreach (var row in rows)
        {
            var bag = new RowBag { Id = row.Id, Order = row.Order, CreatedRecordId = row.CreatedRecordId };
            foreach (var g in values.Where(v => v.RowId == row.Id).GroupBy(v => v.FieldKey))
                bag.Values.Set(g.Key, g.OrderBy(v => v.Index).Select(ToVal));
            data.RowsOf(row.SectionKey).Add(bag);
        }
        return new RecordSnapshot { Record = record, Form = form, Config = Cfg, Data = data, Attachments = atts, ValueRows = values, SubformRows = rows };
    }

    public async Task<RecordSnapshot> LoadRequiredAsync(Guid id) =>
        await LoadAsync(id) ?? throw new InvalidOperationException($"Record {id} not found.");

    private static Val ToVal(RecordValue v) => new(v.Text, v.Number, v.Date, v.RefId, v.RefGuid);

    // ------------------------------------------------------------------ input parsing

    /// <summary>
    /// Parse posted form fields into proposed data. Only fields in sections editable in the current phase are read
    /// (the server, not the browser, decides what is editable: INV-082).
    /// Names: <c>f.{field}</c>; subform rows <c>r.{section}.{i}.{field}</c> with <c>r.{section}.{i}.__id</c>.
    /// </summary>
    public RecordData ParseInput(FormDefinition form, PhaseDefinition? phase, Record? record, IFormCollection input, List<FieldError> errors)
    {
        var tz = TimeZone;
        var data = new RecordData();
        var probe = record ?? new Record { PhaseType = phase?.Type ?? PhaseType.Normal, WorkflowId = phase?.WorkflowId };
        foreach (var section in form.Sections)
        {
            if (FormRules.StateOf(section, phase, probe) != SectionState.Editable) continue;
            var fields = form.Fields.Where(f => f.SectionKey == section.Key && FieldCodec.StoresValues(f.Type) && !f.ReadOnly).ToList();
            if (!section.IsSubform)
            {
                foreach (var f in fields)
                {
                    var (vals, err) = FieldCodec.Parse(f, input[$"f.{f.Key}"], Cfg, tz);
                    if (err != null) errors.Add(new FieldError(f.Key, err));
                    data.Main.Set(f.Key, vals);
                }
                continue;
            }

            var prefix = $"r.{section.Key}.";
            var indices = input.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(".__id", StringComparison.Ordinal))
                .Select(k => k[prefix.Length..^5]).Where(i => int.TryParse(i, out _)).Select(int.Parse).Order().ToList();
            var rows = data.RowsOf(section.Key);
            var order = 0;
            foreach (var i in indices)
            {
                var idRaw = input[$"{prefix}{i}.__id"].ToString();
                var row = Guid.TryParse(idRaw, out var rid) ? new RowBag { Id = rid } : new RowBag { IsNew = true };
                row.Order = order++;
                foreach (var f in fields)
                {
                    var (vals, err) = FieldCodec.Parse(f, input[$"{prefix}{i}.{f.Key}"], Cfg, tz);
                    if (err != null) errors.Add(new FieldError(f.Key, err, row.Id));
                    row.Values.Set(f.Key, vals);
                }
                if (row.Values.Keys.Any(row.Values.Has) || !row.IsNew) rows.Add(row);
            }
        }
        return data;
    }

    // ------------------------------------------------------------------ create

    public async Task<CreateResult> CreateAsync(CreateRequest req)
    {
        var form = Cfg.Form(req.FormKey);
        if (!me.IsSystem && !req.SkipAccessCheck && !access.CanCreate(form))
            return new CreateResult(null, [new FieldError(null, $"You don't have permission to create {form.Name} records.")]);

        WorkflowDefinition? wf = null;
        PhaseDefinition? start = null;
        if (!form.IsNonWorkflow)
        {
            wf = req.WorkflowKey != null ? form.Workflows.First(w => w.Key == req.WorkflowKey) : form.Workflows.FirstOrDefault(w => !w.Disabled);
            if (wf == null) return new CreateResult(null, [new FieldError(null, $"{form.Name} has no enabled workflow.")]);
            start = req.StartPhaseKey != null ? wf.Phases.First(p => p.Key == req.StartPhaseKey) : wf.Phases.FirstOrDefault(p => p.IsStart) ?? wf.Phases.First();
        }

        var data = req.Data?.Clone() ?? new RecordData();
        ApplyDefaults(form, data);
        if (req.Values != null)
            foreach (var (k, v) in req.Values)
                data.Main.Set(k, FieldCodec.FromObject(FieldOf(form, k), v, Cfg));
        if (req.RowValues != null)
            foreach (var (section, rows) in req.RowValues)
            {
                var list = data.RowsOf(section);
                foreach (var rv in rows)
                {
                    var row = new RowBag { IsNew = true, Order = list.Count };
                    foreach (var (k, v) in rv) row.Values.Set(k, FieldCodec.FromObject(FieldOf(form, k), v, Cfg));
                    list.Add(row);
                }
            }

        var saving = new SavingContext(null, data, form, Cfg, services, isNew: true);
        foreach (var b in behaviors.For(form)) await b.OnSavingAsync(saving);
        var errors = new List<FieldError>(saving.Errors);
        errors.AddRange(await ValidateAsync(form, data, null));
        if (errors.Count > 0) return new CreateResult(null, errors);

        return await Tx.RunAsync(db, async () =>
        {
            var now = DateTime.UtcNow;
            var seq = req.Sequence ?? (req.Number == null ? await numbering.NextAsync(form.NumberScope ?? form.Key) : 0);
            var id = Guid.CreateVersion7();
            var creatorId = req.CreatedById ?? me.Id;
            var record = new Record
            {
                Id = id,
                AppId = form.AppId,
                FormId = form.Id,
                WorkflowId = wf?.Id,
                PhaseId = start?.Id,
                PhaseType = start?.Type ?? PhaseType.Normal,
                Number = req.Number ?? NumberingService.Format(form, seq, now),
                Sequence = seq,
                LocationId = req.LocationId ?? dir.Get(creatorId)?.LocationId,
                CreatedAt = req.CreatedAt ?? now,
                CreatedById = creatorId,
                ModifiedAt = now,
                ModifiedById = me.Id,
                ParentId = req.ParentId,
                FamilyId = form.RevisionControlled ? req.FamilyId ?? id : null,
                Revision = form.RevisionControlled ? req.Revision ?? NumberingService.NextRevision(form.RevisionFormat, null) : null,
                RevisionOrdinal = req.RevisionOrdinal,
                ConfigVersion = Cfg.Version,
                Version = 1,
            };
            record.Title = BuildTitle(form, data.Main, record);
            db.Records.Add(record);
            WriteValues(record.Id, data);

            audit.Record(record.Id, "Created", $"{form.Name} {record.Number} created" + (start != null ? $" in {start.Name}" : ""),
                reason: req.CreateComment, recordVersion: 1);
            foreach (var f in form.Fields.Where(f => FieldCodec.StoresValues(f.Type) && data.Main.Has(f.Key)))
                audit.Record(record.Id, "Field set", fieldKey: f.Key, fieldLabel: f.Label,
                    newValue: FieldCodec.Display(f, data.Main[f.Key], Cfg, dir, TimeZoneInfo.Utc, await LinkLabelsAsync(data.Main[f.Key])), recordVersion: 1);

            if (req.Files != null)
                foreach (var file in req.Files)
                    await AddAttachmentAsync(record, file.FieldKey, null, file.FileName, file.ContentType, file.Content);

            if (wf != null && start != null)
            {
                var assignees = req.Assignees?.ToList() ?? [];
                if (assignees.Count == 0 && start.Type == PhaseType.Normal)
                {
                    var tmp = new RecordSnapshot { Record = record, Form = form, Config = Cfg, Data = data };
                    var (_, defaults, _) = await assignments.CandidatesAsync(tmp, start);
                    assignees = defaults.Count > 0 ? defaults.ToList() : [creatorId];
                }
                var due = req.DueDate ?? await assignments.DefaultDueAsync(start, data.Main);
                assignments.EnterPhase(record, start, assignees, due, "create", null);
                if (start.Type == PhaseType.Normal)
                    notify.Assigned(record, form, start, assignees.Where(a => a != me.Id), due);
            }

            if (req.ParentId is { } parentId)
            {
                db.Links.Add(new RecordLink { FromId = parentId, ToId = record.Id, Kind = LinkKind.Activity, At = now, ById = me.Id });
                audit.Record(parentId, "Activity created", $"{form.Name} {record.Number} created from this record");
                db.Comments.Add(new Comment { RecordId = parentId, IsSystem = true, Kind = "system", At = now, UserId = me.IsSystem ? null : me.Id,
                    Text = $"Created {form.Name} {record.Number}: {record.Title}" });
            }

            await db.SaveChangesAsync();

            var snap = await LoadRequiredAsync(record.Id);
            var ctx = new RecordContext(snap, services);
            foreach (var b in behaviors.For(form)) await b.OnCreatedAsync(ctx);
            await db.SaveChangesAsync();
            return new CreateResult(record, []);
        });
    }

    private static FieldDefinition FieldOf(FormDefinition form, string key) =>
        form.Fields.FirstOrDefault(f => f.Key == key) ?? throw new InvalidOperationException($"Form '{form.Key}' has no field '{key}'.");

    private void ApplyDefaults(FormDefinition form, RecordData data)
    {
        // PLT-402: default rules run on create.
        foreach (var f in form.Fields.Where(f => f.DefaultValue != null && FieldCodec.StoresValues(f.Type)))
        {
            if (data.Main.Has(f.Key)) continue;
            var section = form.Sections.FirstOrDefault(s => s.Key == f.SectionKey);
            if (section?.IsSubform == true) continue;
            var dv = f.DefaultValue!;
            object? value = dv switch
            {
                "$me" => me.IsSystem ? null : me.Id,
                "$today" => BusinessCalendar.Today,
                "$now" => DateTime.UtcNow,
                _ when dv.StartsWith("$today+") => BusinessCalendar.Today.AddDays(int.Parse(dv[7..], CultureInfo.InvariantCulture)),
                _ => dv,
            };
            if (value != null) data.Main.Set(f.Key, FieldCodec.FromObject(f, value, Cfg));
        }
    }

    // ------------------------------------------------------------------ save

    /// <summary>Save proposed changes. Only editable fields are taken (unless System/AdminOverride).</summary>
    public async Task<SaveResult> SaveAsync(RecordSnapshot snap, RecordData proposed, SaveOptions? opt = null)
    {
        opt ??= new SaveOptions();
        var r = snap.Record;
        var form = snap.Form;
        int? onBehalfOf = null;

        if (opt.ExpectedVersion is { } ev && ev != r.Version)
            return SaveResult.Fail("Someone else changed this record after you opened it. Reload to see their changes; your input is kept below.");

        if (opt.AdminOverride)
        {
            if (access.RoleFor(r.AppId) < AppRole.Manager) return SaveResult.Fail("Only application managers can edit all fields.");
            if (string.IsNullOrWhiteSpace(opt.Reason)) return SaveResult.Fail("Give a reason for the administrative override.");
            if (r.IsArchived) return SaveResult.Fail("Archived revisions can never be changed (INV-031).");
        }
        else if (!opt.System)
        {
            var can = await access.CanEditAsync(r);
            if (!can.Allowed) return SaveResult.Fail(can.Reason ?? "You can't edit this record.");
            onBehalfOf = can.OnBehalfOfId;
            var lockRow = await db.Locks.AsNoTracking().FirstOrDefaultAsync(l => l.RecordId == r.Id);
            if (lockRow != null && lockRow.UserId != me.Id && lockRow.LastActivityAt > DateTime.UtcNow.AddMinutes(-LockService.TimeoutMinutes))
                return SaveResult.Fail($"{dir.Name(lockRow.UserId)} is editing this record. Your changes weren't saved.");
        }

        // Merge: start from stored data, take proposed values for fields the caller may change.
        var merged = snap.Data.Clone();
        var phase = snap.Phase;
        foreach (var section in form.Sections)
        {
            var editable = opt.System || opt.AdminOverride || FormRules.StateOf(section, phase, r) == SectionState.Editable;
            if (!editable) continue;
            var fields = form.Fields.Where(f => f.SectionKey == section.Key && FieldCodec.StoresValues(f.Type)).ToList();
            if (section.IsSubform)
            {
                if (!proposed.Rows.TryGetValue(section.Key, out var rows)) continue;
                var existing = snap.Data.RowsOf(section.Key).ToDictionary(x => x.Id);
                var next = new List<RowBag>();
                foreach (var row in rows)
                {
                    var target = existing.TryGetValue(row.Id, out var old) ? old.Clone() : new RowBag { Id = row.Id, IsNew = true };
                    target.Order = row.Order;
                    target.CreatedRecordId = row.CreatedRecordId ?? target.CreatedRecordId;
                    foreach (var f in fields.Where(f => opt.System || opt.AdminOverride || !f.ReadOnly))
                        if (row.Values.ContainsKey(f.Key)) target.Values.Set(f.Key, row.Values[f.Key]);
                    next.Add(target);
                }
                merged.Rows[section.Key] = next;
                continue;
            }
            foreach (var f in fields)
            {
                if (!opt.System && !opt.AdminOverride && f.ReadOnly) continue;
                if (opt.AdminOverride && f.ProtectFromAdminUpdate) continue; // PLT-408
                if (proposed.Main.ContainsKey(f.Key)) merged.Main.Set(f.Key, proposed.Main[f.Key]);
            }
        }

        var saving = new SavingContext(snap, merged, form, Cfg, services, isNew: false);
        foreach (var b in behaviors.For(form)) await b.OnSavingAsync(saving);
        var errors = new List<FieldError>(saving.Errors);
        errors.AddRange(await ValidateAsync(form, merged, r.Id));
        if (errors.Count > 0) return SaveResult.Fail(errors);

        return await Tx.RunAsync(db, async () =>
        {
            var changed = await PersistDiffAsync(snap, merged, opt, onBehalfOf);
            foreach (var up in opt.Uploads)
            {
                await AddAttachmentAsync(r, up.FieldKey, null, up.FileName, up.ContentType, up.Content, onBehalfOf);
                changed = true;
            }
            foreach (var attId in opt.RemoveAttachmentIds)
            {
                var att = snap.Attachments.FirstOrDefault(a => a.Id == attId && !a.Removed);
                if (att == null) continue;
                var sec = form.Fields.FirstOrDefault(f => f.Key == att.FieldKey) is { } af ? form.Sections.FirstOrDefault(s => s.Key == af.SectionKey) : null;
                if (!opt.System && !opt.AdminOverride && (sec == null || FormRules.StateOf(sec, phase, r) != SectionState.Editable)) continue;
                att.Removed = true;
                att.RemovedAt = DateTime.UtcNow;
                att.RemovedById = me.Id;
                audit.Record(r.Id, "Attachment removed", $"{att.FileName} (SHA-256 {att.Sha256[..12]}…)", fieldKey: att.FieldKey,
                    fieldLabel: form.Fields.FirstOrDefault(f => f.Key == att.FieldKey)?.Label, onBehalfOfId: onBehalfOf);
                changed = true;
            }
            if (!changed) return new SaveResult(true, [], false, "No changes to save.");

            var title = BuildTitle(form, merged.Main, r);
            if (title != r.Title) r.Title = title;
            r.ModifiedAt = DateTime.UtcNow;
            r.ModifiedById = me.Id;
            r.Version++;
            if (opt.AdminOverride)
                audit.Record(r.Id, "Administrative override", "Edit All Fields", reason: opt.Reason, recordVersion: r.Version);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return SaveResult.Fail("Someone else saved this record at the same time. Reload to see their changes.");
            }
            return new SaveResult(true, [], true, "Saved.");
        }, res => res.Ok);
    }

    /// <summary>Programmatic update by a behavior or job (validated and audited; bypasses edit rights).</summary>
    public async Task<SaveResult> UpdateAsync(Guid id, Dictionary<string, object?> values, string? reason = null)
    {
        var snap = await LoadRequiredAsync(id);
        var proposed = new RecordData();
        foreach (var (k, v) in values) proposed.Main.Set(k, FieldCodec.FromObject(FieldOf(snap.Form, k), v, Cfg));
        return await SaveAsync(snap, proposed, new SaveOptions { System = true, Reason = reason });
    }

    private async Task<bool> PersistDiffAsync(RecordSnapshot snap, RecordData merged, SaveOptions opt, int? onBehalfOf)
    {
        var r = snap.Record;
        var form = snap.Form;
        var changed = false;
        var version = r.Version + 1;
        var tz = TimeZoneInfo.Utc;

        foreach (var f in form.Fields.Where(f => FieldCodec.StoresValues(f.Type)))
        {
            var section = form.Sections.FirstOrDefault(s => s.Key == f.SectionKey);
            if (section?.IsSubform == true) continue;
            var before = snap.Data.Main[f.Key];
            var after = merged.Main[f.Key];
            if (snap.Data.Main.Canonical(f.Key) == merged.Main.Canonical(f.Key)) continue;
            changed = true;
            db.RecordValues.RemoveRange(snap.ValueRows.Where(v => v.RowId == null && v.FieldKey == f.Key));
            AddValues(r.Id, null, f.Key, after);
            var labels = await LinkLabelsAsync(before.Concat(after));
            audit.Record(r.Id, opt.AdminOverride ? "Field changed (override)" : "Field changed",
                fieldKey: f.Key, fieldLabel: f.Label,
                oldValue: FieldCodec.Display(f, before, Cfg, dir, tz, labels),
                newValue: FieldCodec.Display(f, after, Cfg, dir, tz, labels),
                phaseName: snap.Phase?.Name, recordVersion: version, reason: opt.Reason, onBehalfOfId: onBehalfOf);
        }

        foreach (var section in form.Sections.Where(s => s.IsSubform))
        {
            var fields = form.Fields.Where(f => f.SectionKey == section.Key && FieldCodec.StoresValues(f.Type)).ToList();
            var oldRows = snap.Data.RowsOf(section.Key).ToDictionary(x => x.Id);
            var newRows = merged.Rows.TryGetValue(section.Key, out var nr) ? nr : snap.Data.RowsOf(section.Key);
            var newIds = newRows.Select(x => x.Id).ToHashSet();
            var noun = section.RowNoun ?? "row";

            foreach (var removed in oldRows.Values.Where(o => !newIds.Contains(o.Id)))
            {
                changed = true;
                db.RecordValues.RemoveRange(snap.ValueRows.Where(v => v.RowId == removed.Id));
                db.SubformRows.RemoveRange(snap.SubformRows.Where(x => x.Id == removed.Id));
                audit.Record(r.Id, "Row removed", $"{section.Name}: {RowSummary(fields, removed.Values)}", fieldKey: section.Key, fieldLabel: section.Name,
                    phaseName: snap.Phase?.Name, recordVersion: version, onBehalfOfId: onBehalfOf);
            }
            foreach (var row in newRows)
            {
                if (!oldRows.TryGetValue(row.Id, out var old))
                {
                    changed = true;
                    db.SubformRows.Add(new SubformRow { Id = row.Id, RecordId = r.Id, SectionKey = section.Key, Order = row.Order, CreatedAt = DateTime.UtcNow, CreatedRecordId = row.CreatedRecordId });
                    foreach (var f in fields) AddValues(r.Id, row.Id, f.Key, row.Values[f.Key]);
                    audit.Record(r.Id, "Row added", $"{section.Name}: {RowSummary(fields, row.Values)}", fieldKey: section.Key, fieldLabel: section.Name,
                        phaseName: snap.Phase?.Name, recordVersion: version, onBehalfOfId: onBehalfOf);
                    continue;
                }
                var stored = snap.SubformRows.First(x => x.Id == row.Id);
                if (stored.Order != row.Order) { stored.Order = row.Order; changed = true; }
                if (stored.CreatedRecordId != row.CreatedRecordId) { stored.CreatedRecordId = row.CreatedRecordId; changed = true; }
                foreach (var f in fields)
                {
                    if (old.Values.Canonical(f.Key) == row.Values.Canonical(f.Key)) continue;
                    changed = true;
                    db.RecordValues.RemoveRange(snap.ValueRows.Where(v => v.RowId == row.Id && v.FieldKey == f.Key));
                    AddValues(r.Id, row.Id, f.Key, row.Values[f.Key]);
                    var labels = await LinkLabelsAsync(old.Values[f.Key].Concat(row.Values[f.Key]));
                    audit.Record(r.Id, "Field changed", fieldKey: $"{section.Key}.{f.Key}", fieldLabel: $"{section.Name} › {f.Label} (row {row.Order + 1})",
                        oldValue: FieldCodec.Display(f, old.Values[f.Key], Cfg, dir, tz, labels),
                        newValue: FieldCodec.Display(f, row.Values[f.Key], Cfg, dir, tz, labels),
                        phaseName: snap.Phase?.Name, recordVersion: version, onBehalfOfId: onBehalfOf);
                }
            }
        }
        if (changed) snap.Data = merged;
        return changed;
    }

    private string RowSummary(List<FieldDefinition> fields, ValueBag values) =>
        string.Join(" · ", fields.Where(f => values.Has(f.Key)).Take(3).Select(f => FieldCodec.Display(f, values[f.Key], Cfg, dir, TimeZoneInfo.Utc)));

    private void WriteValues(Guid recordId, RecordData data)
    {
        foreach (var key in data.Main.Keys) AddValues(recordId, null, key, data.Main[key]);
        foreach (var (section, rows) in data.Rows)
            foreach (var row in rows)
            {
                db.SubformRows.Add(new SubformRow { Id = row.Id, RecordId = recordId, SectionKey = section, Order = row.Order, CreatedAt = DateTime.UtcNow, CreatedRecordId = row.CreatedRecordId });
                foreach (var key in row.Values.Keys) AddValues(recordId, row.Id, key, row.Values[key]);
            }
    }

    private void AddValues(Guid recordId, Guid? rowId, string key, IReadOnlyList<Val> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            db.RecordValues.Add(new RecordValue { RecordId = recordId, RowId = rowId, FieldKey = key, Index = i, Text = v.Text, Number = v.Number, Date = v.Date, RefId = v.RefId, RefGuid = v.RefGuid });
        }
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Save-time validation that holds on every write path: types, lengths, ranges, prevent-null (PLT-503), unique (PLT-410).</summary>
    public async Task<List<FieldError>> ValidateAsync(FormDefinition form, RecordData data, Guid? recordId)
    {
        var errors = new List<FieldError>();
        foreach (var f in form.Fields.Where(f => FieldCodec.StoresValues(f.Type)))
        {
            var section = form.Sections.FirstOrDefault(s => s.Key == f.SectionKey);
            var bags = section?.IsSubform == true
                ? data.RowsOf(section.Key).Select(r => (r.Values, (Guid?)r.Id)).ToList()
                : [(data.Main, (Guid?)null)];
            foreach (var (bag, rowId) in bags)
            {
                var vals = bag[f.Key];
                if (f.PreventNull && vals.Count == 0)
                    errors.Add(new FieldError(f.Key, $"{f.Label} can't be empty.", rowId));
                foreach (var v in vals)
                {
                    if (f.MaxLength is { } max && v.Text?.Length > max)
                        errors.Add(new FieldError(f.Key, $"{f.Label} can be at most {max} characters.", rowId));
                    if (f.Min is { } min && v.Number < min)
                        errors.Add(new FieldError(f.Key, $"{f.Label} must be at least {min.ToString("G29", CultureInfo.InvariantCulture)}.", rowId));
                    if (f.Max is { } mx && v.Number > mx)
                        errors.Add(new FieldError(f.Key, $"{f.Label} must be at most {mx.ToString("G29", CultureInfo.InvariantCulture)}.", rowId));
                    if (f.Decimals is { } dp && v.Number is { } n && decimal.Round(n, dp) != n)
                        errors.Add(new FieldError(f.Key, $"{f.Label} allows {dp} decimal places.", rowId));
                    if (f.Type is FieldType.Keyword or FieldType.KeywordMulti && Cfg.Keyword(v.RefId) is null)
                        errors.Add(new FieldError(f.Key, $"{f.Label} has an unknown value.", rowId));
                    if (f.Type is FieldType.User or FieldType.UserMulti && dir.Get(v.RefId) is null)
                        errors.Add(new FieldError(f.Key, $"{f.Label} refers to an unknown person.", rowId));
                }
                if (f.Unique && rowId == null && vals.Count > 0)
                {
                    var canon = vals[0];
                    // Revisions of the same document share their values, so they don't count as duplicates.
                    var family = recordId == null ? null : await db.Records.Where(x => x.Id == recordId).Select(x => x.FamilyId).FirstOrDefaultAsync();
                    var dup = await db.RecordValues.AnyAsync(v => v.FieldKey == f.Key && v.RowId == null && v.RecordId != recordId &&
                        v.Text == canon.Text && v.Number == canon.Number && v.RefId == canon.RefId && v.Date == canon.Date &&
                        db.Records.Any(r => r.Id == v.RecordId && r.FormId == form.Id && (family == null || r.FamilyId != family)));
                    if (dup) errors.Add(new FieldError(f.Key, $"Another {form.Name} already uses this {f.Label}.", null));
                }
            }
        }
        return errors;
    }

    /// <summary>PLT-501/502/504: required fields when sending forward (only fields currently shown and editable count, PLT-506).</summary>
    public List<FieldError> RequiredErrors(RecordSnapshot snap)
    {
        var errors = new List<FieldError>();
        var phase = snap.Phase;
        foreach (var section in snap.Form.Sections)
        {
            if (FormRules.StateOf(section, phase, snap.Record) != SectionState.Editable) continue;
            var fields = snap.Form.Fields.Where(f => f.SectionKey == section.Key && f.Type != FieldType.Prompt &&
                (f.Required || (phase != null && f.RequiredInPhases.Contains(phase.Key)))).ToList();
            if (section.IsSubform)
            {
                var rows = snap.Data.RowsOf(section.Key);
                if (section.Required && rows.Count == 0)
                    errors.Add(new FieldError(section.Key, $"Add at least one {section.RowNoun ?? "row"} to {section.Name}."));
                foreach (var row in rows)
                    foreach (var f in fields.Where(f => !row.Values.Has(f.Key)))
                        errors.Add(new FieldError(f.Key, $"{section.Name}: {f.Label} is required (row {row.Order + 1}).", row.Id));
                continue;
            }
            foreach (var f in fields)
            {
                if (!FormRules.IsFieldVisible(f, snap.Data.Main, snap.Form, snap.Config)) continue;
                var has = f.Type == FieldType.Attachment ? snap.AttachmentsOf(f.Key).Any() : snap.Data.Main.Has(f.Key);
                if (!has) errors.Add(new FieldError(f.Key, $"{f.Label} is required."));
            }
        }
        return errors;
    }

    // ------------------------------------------------------------------ helpers

    public string BuildTitle(FormDefinition form, ValueBag main, Record r)
    {
        var title = FormRules.ApplyTemplate(form.TitleTemplate, key => key switch
        {
            "number" => r.Number,
            "form" => form.Name,
            "revision" => r.Revision ?? "",
            _ => form.Fields.FirstOrDefault(f => f.Key == key) is { } f ? FieldCodec.Display(f, main[key], Cfg, dir, TimeZoneInfo.Utc) : "",
        });
        return string.IsNullOrWhiteSpace(title) ? $"{form.Name} {r.Number}" : title.Length > 1000 ? title[..1000] : title;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> LinkLabelsAsync(IEnumerable<Val> values)
    {
        var ids = values.Where(v => v.RefGuid != null).Select(v => v.RefGuid!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await db.Records.AsNoTracking().Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Number + (r.Revision != null ? $" rev {r.Revision}" : "") + " " + r.Title);
    }

    public async Task<Attachment> AddAttachmentAsync(Record r, string fieldKey, Guid? rowId, string fileName, string contentType, Stream content, int? onBehalfOf = null)
    {
        var (sha, size) = await files.SaveAsync(content);
        var att = new Attachment
        {
            RecordId = r.Id, RowId = rowId, FieldKey = fieldKey, FileName = Path.GetFileName(fileName), ContentType = contentType,
            Sha256 = sha, Size = size, UploadedById = me.Id, UploadedAt = DateTime.UtcNow,
        };
        db.Attachments.Add(att);
        var form = Cfg.FormsById[r.FormId];
        audit.Record(r.Id, "Attachment added", $"{att.FileName} ({size:N0} bytes, SHA-256 {sha[..12]}…)", fieldKey: fieldKey,
            fieldLabel: form.Fields.FirstOrDefault(f => f.Key == fieldKey)?.Label, onBehalfOfId: onBehalfOf);
        return att;
    }

    public async Task AddCommentAsync(Guid recordId, string text, string kind = "user", bool system = false)
    {
        if (!system && !await access.CanReadAsync(recordId)) throw new UnauthorizedAccessException();
        db.Comments.Add(new Comment { RecordId = recordId, UserId = me.IsSystem ? null : me.Id, Text = text.Trim(), Kind = kind, IsSystem = system, At = DateTime.UtcNow });
        audit.Record(recordId, system ? "System comment" : "Comment added", text.Length > 500 ? text[..500] + "…" : text);
        await db.SaveChangesAsync();
    }

    /// <summary>Link two records (both directions are queryable). Audited on both records.</summary>
    public async Task LinkAsync(Guid fromId, Guid toId, LinkKind kind, string? fieldKey = null)
    {
        if (await db.Links.AnyAsync(l => l.FromId == fromId && l.ToId == toId && l.Kind == kind && l.FieldKey == fieldKey)) return;
        db.Links.Add(new RecordLink { FromId = fromId, ToId = toId, Kind = kind, FieldKey = fieldKey, At = DateTime.UtcNow, ById = me.Id });
        var nums = await db.Records.AsNoTracking().Where(r => r.Id == fromId || r.Id == toId).ToDictionaryAsync(r => r.Id, r => r.Number);
        audit.Record(fromId, "Link added", $"Linked to {nums.GetValueOrDefault(toId)} ({kind})");
        audit.Record(toId, "Link added", $"Linked from {nums.GetValueOrDefault(fromId)} ({kind})");
    }

    /// <summary>Child activities of a record (optionally of one form).</summary>
    public Task<List<Record>> ChildrenAsync(Guid parentId, string? formKey = null)
    {
        var q = db.Records.AsNoTracking().Include(r => r.Phase).Where(r => r.ParentId == parentId);
        if (formKey != null)
        {
            var fid = Cfg.Form(formKey).Id;
            q = q.Where(r => r.FormId == fid);
        }
        return q.OrderBy(r => r.CreatedAt).ToListAsync();
    }

    /// <summary>INV-114: content hash of the record version (data, attachments, identity). Phase and workflow state are excluded.</summary>
    public string ComputeHash(RecordSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.Append(snap.Record.Id).Append('|').Append(snap.Record.Number).Append('|').Append(snap.Record.Revision).Append('|').Append(snap.Record.Title).Append('\n');
        foreach (var key in snap.Data.Main.Keys.Order(StringComparer.Ordinal))
            if (snap.Data.Main.Has(key)) sb.Append(key).Append('=').Append(snap.Data.Main.Canonical(key)).Append('\n');
        foreach (var (section, rows) in snap.Data.Rows.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var row in rows.OrderBy(r => r.Order))
                foreach (var key in row.Values.Keys.Order(StringComparer.Ordinal))
                    if (row.Values.Has(key)) sb.Append(section).Append('[').Append(row.Order).Append("].").Append(key).Append('=').Append(row.Values.Canonical(key)).Append('\n');
        foreach (var a in snap.Attachments.Where(a => !a.Removed).OrderBy(a => a.FieldKey).ThenBy(a => a.Sha256))
            sb.Append("file:").Append(a.FieldKey).Append('=').Append(a.Sha256).Append('\n');
        return AuditHashing.Sha256Hex(sb.ToString());
    }

    public string SnapshotJson(RecordSnapshot snap) => JsonSerializer.Serialize(new
    {
        snap.Record.Number,
        snap.Record.Title,
        snap.Record.Revision,
        Form = snap.Form.Key,
        Phase = snap.Phase?.Name,
        Fields = snap.Form.Fields.Where(f => snap.Data.Main.Has(f.Key))
            .ToDictionary(f => f.Key, f => FieldCodec.Display(f, snap.Data.Main[f.Key], Cfg, dir, TimeZoneInfo.Utc)),
    });
}
