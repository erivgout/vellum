using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Auth;
using Vellum.Infrastructure.Files;

namespace Vellum.Modules.Portal;

public sealed record PortalState(string Label, string Tone, string Icon, bool ActionNeeded, bool Closed);

public sealed record PortalItem(ExternalShare Share, PortalKind Kind, string Number, string Title, DateOnly? Due, PortalState State, DateTime UpdatedAt);

public sealed class PortalFieldVm
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required FieldType Type { get; init; }
    /// <summary>Formatted value for display.</summary>
    public string Display { get; init; } = "";
    /// <summary>Raw value for an input control (yyyy-MM-dd for dates).</summary>
    public string Input { get; init; } = "";
    public string? Help { get; init; }
    public string? Placeholder { get; init; }
    public bool Required { get; init; }
    public int? MaxLength { get; init; }
    public string? KeywordKey { get; init; }
    public string? KeywordColor { get; init; }
}

public sealed class PortalRowVm
{
    public required Guid Id { get; init; }
    public required int Order { get; init; }
    public required Dictionary<string, PortalFieldVm> Fields { get; init; }
    public string? Get(string key) => Fields.TryGetValue(key, out var f) && f.Display.Length > 0 ? f.Display : null;
    public string? KeywordKey(string key) => Fields.TryGetValue(key, out var f) ? f.KeywordKey : null;
}

public sealed record PortalFileVm(long Id, string Field, string FieldLabel, string Name, long Size, DateTime At, bool FromSupplier, bool Removable);

/// <summary>What the portal pages render. Built only from the kind's whitelist (<see cref="PortalKind"/>), never from the raw record.</summary>
public sealed class PortalView
{
    public required ExternalShare Share { get; init; }
    public required PortalKind Kind { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public required PortalState State { get; init; }
    public required bool CanRespond { get; init; }
    public required int Version { get; init; }
    public required List<(PortalGroup Group, List<PortalFieldVm> Fields)> Groups { get; init; }
    public required Dictionary<string, PortalFieldVm> Editable { get; init; }
    public required List<PortalRowVm> Rows { get; init; }
    public required List<PortalFileVm> Files { get; init; }
    public string? ContactName { get; init; }
    public string? ContactEmail { get; init; }
    public DateOnly? Due { get; init; }
    public string? RespondedBy { get; init; }
    public DateOnly? RespondedOn { get; init; }
    public Dictionary<string, string> RowLabels { get; init; } = [];

    public PortalFieldVm? Field(string key) =>
        Editable.TryGetValue(key, out var e) ? e : Groups.SelectMany(g => g.Fields).FirstOrDefault(f => f.Key == key);
    public string? Value(string key) => Field(key) is { Display.Length: > 0 } f ? f.Display : null;
}

public sealed record PortalResult(bool Ok, List<string> Errors, string? Message = null)
{
    public static PortalResult Fail(params string[] errors) => new(false, [.. errors]);
}

/// <summary>
/// The supplier portal (SUP-004). External side: lists and opens the requests shared with the signed-in user's company and
/// saves their responses. Every read starts from <see cref="AccessService.Readable"/> (which, for external users, is exactly the
/// records shared with their organization, INV-088) and every write goes through <see cref="RecordService"/> and
/// <see cref="WorkflowEngine"/> as the external user, audited with the source "Supplier portal". Only the fields listed in the
/// request's <see cref="PortalKind"/> are ever shown or written. Internal side: shares records with a supplier, changes the
/// portal status as the record moves on, and emails the supplier's portal contacts.
/// </summary>
[ScopedService]
public sealed class PortalService(
    VellumDbContext db, CurrentUser me, AccessService access, RecordService records, WorkflowEngine workflow, ConfigCache cfg,
    UserDirectory dir, AuditService audit, NotificationService notify, FileStore files, IHttpContextAccessor http, IConfiguration config)
{
    public const long MaxUploadBytes = 25L * 1024 * 1024;

    private ConfigModel Cfg => cfg.Current;
    private DbSet<ExternalShare> Shares => db.Set<ExternalShare>();
    private TimeZoneInfo Tz => records.TimeZone;

    // ================================================================== shared helpers

    public static string Url(int shareId) => $"/portal/requests/{shareId}";

    public string AbsoluteUrl(string path)
    {
        var req = http.HttpContext?.Request;
        var root = req != null ? $"{req.Scheme}://{req.Host}" : (config["Vellum:PublicUrl"] ?? "http://localhost:5048").TrimEnd('/');
        return root + path;
    }

    /// <summary>The supplier's name (just the name: the supplier profile itself is internal).</summary>
    public async Task<string> SupplierNameAsync(Guid orgId) =>
        await db.RecordValues.AsNoTracking().Where(v => v.RecordId == orgId && v.RowId == null && v.FieldKey == "name").Select(v => v.Text).FirstOrDefaultAsync()
        ?? "your company";

    public async Task<string> OrganizationNameAsync() => await db.Settings.AsNoTracking().Select(s => s.OrganizationName).FirstAsync();

    public static PortalState StateOf(ExternalShare s, PortalKind kind, Record r, bool canRespond)
    {
        if (kind.ReadOnly) return new("Shared with you", "info", "bi-eye", false, true);
        var overdue = s.DueDate is { } due && due < BusinessCalendar.Today;
        return s.Status switch
        {
            ExternalShareStatus.Returned when canRespond => new("Changes requested", "bad", "bi-arrow-return-left", true, false),
            ExternalShareStatus.Open when canRespond => overdue ? new("Overdue", "bad", "bi-exclamation-circle", true, false) : new("Action needed", "warn", "bi-pencil-square", true, false),
            ExternalShareStatus.Submitted => new("Under review", "info", "bi-hourglass-split", false, false),
            ExternalShareStatus.Accepted => new("Accepted", "ok", "bi-check-circle", false, true),
            _ when r.PhaseType == PhaseType.Voided => new("Cancelled", "neutral", "bi-x-circle", false, true),
            _ => new("Closed", "neutral", "bi-lock", false, true),
        };
    }

    private static bool CanRespond(ExternalShare s, PortalKind kind, RecordSnapshot snap) =>
        !kind.ReadOnly && s.Status is ExternalShareStatus.Open or ExternalShareStatus.Returned &&
        snap.Record.PhaseType == PhaseType.Normal && kind.ResponsePhases.Contains(snap.PhaseKey);

    // ================================================================== external side (signed-in supplier contact)

    private Guid? Org => me.IsExternal ? me.ExternalOrgId : null;

    /// <summary>Shares of the user's own organization whose records the user can read (INV-088: both conditions).</summary>
    private IQueryable<ExternalShare> MyShares()
    {
        if (Org is not { } org) return Shares.Where(_ => false);
        var readable = access.Readable();
        return Shares.AsNoTracking().Where(s => s.OrgId == org && s.Status != ExternalShareStatus.Withdrawn && readable.Any(r => r.Id == s.RecordId));
    }

    public async Task<List<PortalItem>> ListAsync()
    {
        var shares = await MyShares().ToListAsync();
        if (shares.Count == 0) return [];
        var ids = shares.Select(s => s.RecordId).ToList();
        var recs = await access.Readable().AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var list = new List<PortalItem>();
        foreach (var s in shares)
        {
            if (!recs.TryGetValue(s.RecordId, out var r) || PortalKinds.Get(s.Kind) is not { } kind) continue;
            var phase = Cfg.Phase(r.PhaseId);
            if (kind.ReadOnly && !kind.VisiblePhases.Contains(phase?.Key)) continue;
            var canRespond = !kind.ReadOnly && s.Status is ExternalShareStatus.Open or ExternalShareStatus.Returned &&
                             r.PhaseType == PhaseType.Normal && kind.ResponsePhases.Contains(phase?.Key);
            list.Add(new PortalItem(s, kind, r.Number, r.Title, s.DueDate, StateOf(s, kind, r, canRespond), s.UpdatedAt));
        }
        return list;
    }

    private async Task<(ExternalShare Share, PortalKind Kind, RecordSnapshot Snap)?> OpenAsync(int shareId, bool track = false)
    {
        var share = await MyShares().FirstOrDefaultAsync(s => s.Id == shareId);
        if (share == null || PortalKinds.Get(share.Kind) is not { } kind) return null;
        if (!await access.CanReadAsync(share.RecordId)) return null;
        var snap = await records.LoadAsync(share.RecordId);
        if (snap == null || snap.Form.Key != kind.FormKey) return null;
        if (kind.ReadOnly && !kind.VisiblePhases.Contains(snap.PhaseKey)) return null; // SUP-036: released scorecards only
        if (track) share = await Shares.FirstAsync(s => s.Id == shareId);
        return (share, kind, snap);
    }

    public async Task<PortalView?> ViewAsync(int shareId)
    {
        if (await OpenAsync(shareId) is not { } opened) return null;
        var (share, kind, snap) = opened;
        var canRespond = CanRespond(share, kind, snap);

        var linkIds = kind.VisibleFields.Select(k => snap.Field(k)).Where(f => f?.Type == FieldType.Link)
            .SelectMany(f => snap.Links(f!.Key)).Distinct().ToList();
        // Linked records (e.g. the material of an SDS request) show by title only: never their internal number or content.
        var labels = linkIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.Records.AsNoTracking().Where(r => linkIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Title);

        PortalFieldVm Vm(string key, ValueBag bag, bool required = false)
        {
            var f = snap.Field(key) ?? throw new InvalidOperationException($"Portal kind {kind.Key}: form {kind.FormKey} has no field {key}.");
            var vals = bag[key];
            var kw = f.Type == FieldType.Keyword ? Cfg.Keyword(bag.Ref(key)) : null;
            return new PortalFieldVm
            {
                Key = key, Label = f.Label, Type = f.Type, Help = f.Help, Placeholder = f.Placeholder, MaxLength = f.MaxLength, Required = required,
                Display = FieldCodec.Display(f, vals, Cfg, dir, Tz, labels),
                Input = vals.Count > 0 ? FieldCodec.InputValue(f, vals[0], Tz) : "",
                KeywordKey = kw?.Key, KeywordColor = kw?.Color,
            };
        }

        var groups = kind.Groups.Select(g => (g, g.Fields.Select(k => Vm(k, snap.Data.Main))
            .Where(v => v.Display.Length > 0 && FormRules.IsFieldVisible(snap.Field(v.Key)!, snap.Data.Main, snap.Form, Cfg)).ToList()))
            .Where(x => x.Item2.Count > 0).ToList();
        var editable = kind.Editable.ToDictionary(k => k, k => Vm(k, snap.Data.Main, RequiredOnSubmit(kind, snap, k)));
        var rows = new List<PortalRowVm>();
        var rowLabels = new Dictionary<string, string>();
        if (kind.Rows is { } spec)
        {
            foreach (var k in spec.Visible.Concat(spec.Editable)) rowLabels[k] = snap.Field(k)?.Label ?? k;
            foreach (var row in snap.Rows(spec.Section).OrderBy(r => r.Order))
                rows.Add(new PortalRowVm
                {
                    Id = row.Id, Order = row.Order,
                    Fields = spec.Visible.Concat(spec.Editable).ToDictionary(k => k, k => Vm(k, row.Values, spec.Editable.Contains(k) && RowRequired(kind, snap, row, k))),
                });
        }

        var orgUsers = (await db.Users.AsNoTracking().Where(u => u.IsExternal && u.ExternalOrgId == share.OrgId).Select(u => u.Id).ToListAsync()).ToHashSet();
        var visibleFiles = kind.AllFiles.ToHashSet();
        var filesVm = snap.Attachments.Where(a => !a.Removed && visibleFiles.Contains(a.FieldKey) && a.RowId == null)
            .Select(a => new PortalFileVm(a.Id, a.FieldKey, snap.Field(a.FieldKey)?.Label ?? a.FieldKey, a.FileName, a.Size, a.UploadedAt,
                orgUsers.Contains(a.UploadedById), canRespond && orgUsers.Contains(a.UploadedById) && kind.UploadFiles.Contains(a.FieldKey)))
            .OrderBy(a => a.At).ToList();

        var owner = dir.Get(snap.UserId(kind.OwnerField)) ?? dir.Get(share.SharedById);
        return new PortalView
        {
            Share = share, Kind = kind, Number = snap.Record.Number, Title = snap.Record.Title,
            State = StateOf(share, kind, snap.Record, canRespond), CanRespond = canRespond, Version = snap.Record.Version,
            Groups = groups, Editable = editable, Rows = rows, Files = filesVm, RowLabels = rowLabels,
            ContactName = owner?.DisplayName, ContactEmail = owner?.Email,
            Due = share.DueDate ?? (kind.DueField != null ? snap.Date(kind.DueField) : null),
            RespondedBy = kind.RespondedByField != null ? snap.Text(kind.RespondedByField) : null,
            RespondedOn = kind.RespondedOnField != null ? snap.Date(kind.RespondedOnField) : null,
        };
    }

    /// <summary>Editable main fields that must be filled in before submitting (some depend on the request's own data).</summary>
    private static bool RequiredOnSubmit(PortalKind kind, RecordSnapshot snap, string key) => kind.Key switch
    {
        PortalKinds.Scar when key == "sr_containment" => snap.Bool("containment_required") == true,
        PortalKinds.Document when key == "expires_on" => snap.KeywordKey("doc_type") is "iso_cert" or "insurance",
        _ => kind.Required.Contains(key),
    };

    private static bool RowRequired(PortalKind kind, RecordSnapshot snap, RowBag row, string key) => kind.Key switch
    {
        PortalKinds.Questionnaire => key == "answer" && row.Values.Bool("q_required") == true,
        PortalKinds.AuditResponse => key is "sr_root_cause" or "sr_corrective_action" or "sr_target_date" &&
                                     PortalKinds.NonconformanceClasses.Contains(snap.KeywordKey(row, "classification")),
        _ => false,
    };

    /// <summary>Save (draft) or submit the supplier's response. Only the kind's editable fields and upload fields are taken.</summary>
    public async Task<PortalResult> RespondAsync(int shareId, int version, IFormCollection form, bool submit)
    {
        if (!me.IsExternal) return PortalResult.Fail("Only supplier portal users respond here.");
        me.Source = ExternalUserBoundary.PortalSource;
        if (await OpenAsync(shareId, track: true) is not { } opened) return PortalResult.Fail("This request isn't available.");
        var (share, kind, snap) = opened;
        if (!CanRespond(share, kind, snap))
            return PortalResult.Fail(share.Status == ExternalShareStatus.Submitted
                ? "Your response has been submitted and is being reviewed. You can't change it now."
                : "This request is closed.");

        var errors = new List<string>();
        var proposed = new RecordData();
        foreach (var key in kind.Editable)
        {
            if (!form.ContainsKey($"f.{key}")) continue;
            var f = snap.Field(key)!;
            var (vals, err) = FieldCodec.Parse(f, form[$"f.{key}"], Cfg, Tz);
            if (err != null) { errors.Add($"{f.Label}: {err}"); continue; }
            proposed.Main.Set(key, vals);
        }
        if (kind.Rows is { } spec)
        {
            var rows = proposed.RowsOf(spec.Section);
            foreach (var existing in snap.Rows(spec.Section))
            {
                // Every existing row is passed back (rows left out would be removed); only the editable keys carry new values.
                var row = new RowBag { Id = existing.Id, Order = existing.Order, CreatedRecordId = existing.CreatedRecordId };
                foreach (var key in spec.Editable)
                {
                    var name = $"r.{existing.Id:N}.{key}";
                    if (!form.ContainsKey(name)) continue;
                    var f = snap.Field(key)!;
                    var (vals, err) = FieldCodec.Parse(f, form[name], Cfg, Tz);
                    if (err != null) { errors.Add($"{spec.Noun} {existing.Order + 1}, {f.Label}: {err}"); continue; }
                    row.Values.Set(key, vals);
                }
                rows.Add(row);
            }
        }

        var orgUsers = (await db.Users.AsNoTracking().Where(u => u.IsExternal && u.ExternalOrgId == share.OrgId).Select(u => u.Id).ToListAsync()).ToHashSet();
        var removeIds = form["remove"].Select(v => long.TryParse(v, out var id) ? id : 0)
            .Where(id => snap.Attachments.Any(a => a.Id == id && !a.Removed && kind.UploadFiles.Contains(a.FieldKey) && orgUsers.Contains(a.UploadedById)))
            .ToList();
        var uploads = new List<(string, string, string, Stream)>();
        foreach (var file in form.Files.Where(f => f.Length > 0))
        {
            var fieldKey = file.Name.StartsWith("file.", StringComparison.Ordinal) ? file.Name[5..] : "";
            if (!kind.UploadFiles.Contains(fieldKey)) continue;
            if (file.Length > MaxUploadBytes) { errors.Add($"{file.FileName} is larger than 25 MB."); continue; }
            uploads.Add((fieldKey, file.FileName, string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType, file.OpenReadStream()));
        }
        if (errors.Count > 0) return new PortalResult(false, errors);

        if (submit)
        {
            if (kind.RespondedByField != null)
                proposed.Main.Set(kind.RespondedByField, Val.Of(me.Info?.JobTitle is { Length: > 0 } t ? $"{me.Name}, {t}" : me.Name));
            if (kind.RespondedOnField != null) proposed.Main.Set(kind.RespondedOnField, Val.Of(BusinessCalendar.Today));
            errors.AddRange(SubmitErrors(kind, snap, proposed, uploads.Select(u => u.Item1).ToList(), removeIds));
            if (errors.Count > 0) return new PortalResult(false, errors);
        }

        var company = await SupplierNameAsync(share.OrgId);
        try
        {
            return await Tx.RunAsync(db, async () =>
            {
                var save = await records.SaveAsync(snap, proposed, new SaveOptions
                {
                    System = true, ExpectedVersion = version, Uploads = uploads, RemoveAttachmentIds = removeIds,
                    Reason = "Supplier portal response",
                });
                if (!save.Ok) return new PortalResult(false, save.Errors.Select(e => e.Message).ToList());

                share.UpdatedAt = DateTime.UtcNow;
                if (!submit)
                {
                    await db.SaveChangesAsync();
                    return new PortalResult(true, [], save.Changed ? "Draft saved. Submit when you're ready." : "No changes to save.");
                }

                share.Status = ExternalShareStatus.Submitted;
                share.SubmittedAt = DateTime.UtcNow;
                share.SubmittedById = me.Id;
                audit.Record(snap.Record.Id, "Supplier response submitted", $"{me.Name} ({company}) submitted the response through the supplier portal",
                    phaseName: snap.Phase?.Name);
                db.Comments.Add(new Comment
                {
                    RecordId = snap.Record.Id, UserId = me.Id, IsSystem = true, Kind = "system", At = DateTime.UtcNow,
                    Text = $"{company} submitted their response through the supplier portal ({me.Name}).",
                });
                await db.SaveChangesAsync();

                if (kind.SubmitRoutesTo is { } target)
                {
                    var routed = await workflow.SystemRouteAsync(snap.Record.Id, target, $"{company} responded through the supplier portal");
                    if (!routed.Ok) return new PortalResult(false, routed.Errors.Select(e => e.Message).ToList());
                }
                else
                {
                    var ownerId = snap.UserId(kind.OwnerField) ?? share.SharedById;
                    notify.Notify(ownerId, NotificationKind.Assignment, $"{company} responded to {snap.Record.Number}",
                        $"{me.Name} submitted the supplier response to \"{snap.Record.Title}\" through the supplier portal. Review it and route the record on.",
                        snap.Record.Id);
                }
                await db.SaveChangesAsync();
                return new PortalResult(true, [], "Thank you. Your response was submitted and the team has been notified.");
            }, r => r.Ok);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return PortalResult.Fail("Someone changed this request while you were working on it. Reload the page; your browser may still have your text.");
        }
    }

    private List<string> SubmitErrors(PortalKind kind, RecordSnapshot snap, RecordData proposed, List<string> uploadedFields, List<long> removeIds)
    {
        var errors = new List<string>();
        bool Has(ValueBag before, ValueBag after, string key) => after.ContainsKey(key) ? after.Has(key) : before.Has(key);
        foreach (var key in kind.Editable.Where(k => RequiredOnSubmit(kind, snap, k)))
            if (!Has(snap.Data.Main, proposed.Main, key)) errors.Add($"{snap.Field(key)!.Label} is required.");
        if (kind.RequiredFile is { } rf && !uploadedFields.Contains(rf) && !snap.AttachmentsOf(rf).Any(a => !a.Removed && !removeIds.Contains(a.Id)))
            errors.Add($"Upload the {snap.Field(rf)!.Label.ToLowerInvariant()} before submitting.");
        if (kind.Rows is { } spec)
        {
            var posted = proposed.RowsOf(spec.Section).ToDictionary(r => r.Id);
            foreach (var row in snap.Rows(spec.Section).OrderBy(r => r.Order))
                foreach (var key in spec.Editable.Where(k => RowRequired(kind, snap, row, k)))
                    if (!Has(row.Values, posted.TryGetValue(row.Id, out var p) ? p.Values : new ValueBag(), key))
                        errors.Add($"{char.ToUpperInvariant(spec.Noun[0])}{spec.Noun[1..]} {row.Order + 1}: {snap.Field(key)!.Label} is required.");
        }
        return errors;
    }

    /// <summary>A file of a shared request, only from the fields the supplier may see. Verified and audited.</summary>
    public async Task<(Attachment Att, byte[] Bytes)?> FileAsync(int shareId, long attachmentId)
    {
        if (await OpenAsync(shareId) is not { } opened) return null;
        var (_, kind, snap) = opened;
        var att = snap.Attachments.FirstOrDefault(a => a.Id == attachmentId && !a.Removed && a.RowId == null && kind.AllFiles.Contains(a.FieldKey));
        if (att == null) return null;
        byte[] bytes;
        try { bytes = await files.ReadVerifiedAsync(att.Sha256); }
        catch (FileIntegrityException) { return null; }
        me.Source = ExternalUserBoundary.PortalSource;
        audit.Record(snap.Record.Id, "Attachment downloaded", $"{att.FileName} (SHA-256 {att.Sha256[..12]}…) through the supplier portal", fieldKey: att.FieldKey);
        await db.SaveChangesAsync();
        return (att, bytes);
    }

    // ================================================================== internal side

    /// <summary>Active external users of a supplier who have set their password (they can sign in).</summary>
    public Task<List<User>> ActiveContactsAsync(Guid orgId) =>
        db.Users.AsNoTracking().Where(u => u.IsExternal && u.ExternalOrgId == orgId && u.Status == UserStatus.Active && u.PasswordHash != null)
            .OrderBy(u => u.DisplayName).ToListAsync();

    /// <summary>Active external users of a supplier, including invited ones who haven't set a password yet.</summary>
    public Task<int> PortalUserCountAsync(Guid orgId) =>
        db.Users.CountAsync(u => u.IsExternal && u.ExternalOrgId == orgId && u.Status == UserStatus.Active);

    public Task<ExternalShare?> ShareOfAsync(Guid recordId) =>
        Shares.AsNoTracking().Where(s => s.RecordId == recordId).OrderBy(s => s.Status == ExternalShareStatus.Withdrawn).ThenByDescending(s => s.UpdatedAt).FirstOrDefaultAsync();

    /// <summary>
    /// Share a record with a supplier's portal (the "Send to supplier portal" actions). Re-sharing reopens the request. A record
    /// is shared with one supplier at a time: sharing it with another withdraws the earlier share. The caller has already
    /// checked the internal user's right to act on the record.
    /// </summary>
    public async Task<ActionOutcome> ShareAsync(RecordSnapshot snap, Guid orgId, PortalKind kind, DateOnly? due, string? message)
    {
        if (me.IsExternal) return ActionOutcome.Fail("Supplier portal users can't share records.");
        if (snap.Form.Key != kind.FormKey) return ActionOutcome.Fail("This record can't be shared as that kind of request.");
        var supplier = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == orgId);
        if (supplier == null || Cfg.FormsById[supplier.FormId].Key != "supplier") return ActionOutcome.Fail("Choose a supplier profile.");
        if (snap.Field("supplier") is { Type: FieldType.Link } && snap.Links("supplier").FirstOrDefault() is var linked && linked != Guid.Empty && linked != orgId)
            return ActionOutcome.Fail("A record can only be shared with the supplier it belongs to (INV-088)."); // never with another company
        if (await PortalUserCountAsync(orgId) == 0)
            return ActionOutcome.Fail($"{await SupplierNameAsync(orgId)} has no supplier portal contacts yet. Invite one from the supplier profile (Supplier portal › Invite portal contact).");

        var name = await SupplierNameAsync(orgId);
        var now = DateTime.UtcNow;
        var others = await Shares.Where(s => s.RecordId == snap.Record.Id && s.OrgId != orgId && s.Status != ExternalShareStatus.Withdrawn).ToListAsync();
        foreach (var o in others)
        {
            o.Status = ExternalShareStatus.Withdrawn;
            o.UpdatedAt = now;
            audit.Record(snap.Record.Id, "Withdrawn from supplier portal", $"No longer shared with {await SupplierNameAsync(o.OrgId)}: shared with {name} instead");
        }
        var share = await Shares.FirstOrDefaultAsync(s => s.RecordId == snap.Record.Id && s.OrgId == orgId);
        var reopened = share != null;
        if (share == null)
        {
            share = new ExternalShare { RecordId = snap.Record.Id, OrgId = orgId, Kind = kind.Key, SharedAt = now, SharedById = me.Id };
            Shares.Add(share);
        }
        share.Kind = kind.Key;
        share.Status = kind.ReadOnly ? ExternalShareStatus.Accepted : ExternalShareStatus.Open;
        share.Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        share.DueDate = due;
        share.UpdatedAt = now;
        share.ClosedAt = kind.ReadOnly ? now : null;
        await db.SaveChangesAsync();

        var contacts = await ActiveContactsAsync(orgId);
        audit.Record(snap.Record.Id, reopened ? "Re-sent to supplier portal" : "Sent to supplier portal",
            $"{kind.Noun} shared with {name} through the supplier portal" + (due is { } d ? $", due {d:dd MMM yyyy}" : "") +
            $"; {Ui(contacts.Count, "portal contact")} notified", phaseName: snap.Phase?.Name);
        var org = await OrganizationNameAsync();
        var subject = kind.ReadOnly ? $"{org} shared a {kind.Noun.ToLowerInvariant()} with you: {snap.Record.Title}"
            : $"{org} needs your response: {snap.Record.Number} {snap.Record.Title}";
        var body = (share.Message is { } m ? m + "\n\n" : "") +
                   (kind.ReadOnly ? $"{me.Name} shared {snap.Record.Number} with {name} in the supplier portal."
                       : $"{me.Name} sent {name} a {kind.Noun.ToLowerInvariant()} ({snap.Record.Number})" + (due is { } dd ? $", due {dd:dd MMM yyyy}." : "."));
        MailContacts(contacts, subject, body, share.Id, snap.Record.Id);
        await db.SaveChangesAsync();
        return ActionOutcome.Ok(kind.ReadOnly ? $"Shared with {name} in the supplier portal." : $"Sent to {name} through the supplier portal.");
    }

    /// <summary>Move the portal status as the record moves on internally (returned, accepted, withdrawn, reopened).</summary>
    public async Task SetStatusAsync(Guid recordId, ExternalShareStatus status, string? feedback = null, string? reason = null)
    {
        var share = await Shares.FirstOrDefaultAsync(s => s.RecordId == recordId && s.Status != ExternalShareStatus.Withdrawn);
        if (share == null || share.Status == status && feedback == null) return;
        var kind = PortalKinds.Get(share.Kind);
        var name = await SupplierNameAsync(share.OrgId);
        var now = DateTime.UtcNow;
        share.Status = status;
        share.UpdatedAt = now;
        if (feedback != null) share.Feedback = feedback.Trim();
        share.ClosedAt = status is ExternalShareStatus.Accepted or ExternalShareStatus.Withdrawn ? now : null;
        var rec = await db.Records.AsNoTracking().FirstAsync(r => r.Id == recordId);
        var (action, detail) = status switch
        {
            ExternalShareStatus.Returned => ("Returned to supplier", $"{name} was asked for changes in the supplier portal"),
            ExternalShareStatus.Accepted => ("Supplier response accepted", $"Closed in the supplier portal for {name}"),
            ExternalShareStatus.Withdrawn => ("Withdrawn from supplier portal", $"No longer shared with {name}"),
            ExternalShareStatus.Open => ("Reopened in supplier portal", $"{name} can respond again"),
            _ => ("Supplier portal status changed", status.ToString()),
        };
        audit.Record(recordId, action, detail, reason: reason ?? feedback);
        await db.SaveChangesAsync();
        if (status is ExternalShareStatus.Returned or ExternalShareStatus.Accepted or ExternalShareStatus.Open)
        {
            var org = await OrganizationNameAsync();
            var contacts = await ActiveContactsAsync(share.OrgId);
            var (subject, body) = status switch
            {
                ExternalShareStatus.Returned => ($"Changes requested: {rec.Number} {rec.Title}",
                    $"{org} reviewed your response to {rec.Number} and asked for changes" + (feedback is { Length: > 0 } fb ? $":\n\n{fb}" : ".")),
                ExternalShareStatus.Accepted => ($"Accepted: {rec.Number} {rec.Title}", $"{org} accepted your response to {rec.Number}. Thank you."),
                _ => ($"Please respond again: {rec.Number} {rec.Title}", $"{org} reopened {rec.Number} for your response."),
            };
            if (kind?.ReadOnly != true) MailContacts(contacts, subject, body, share.Id, recordId);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Email the supplier's portal contacts again about an open request.</summary>
    public async Task<ActionOutcome> RemindAsync(Guid recordId)
    {
        var share = await Shares.FirstOrDefaultAsync(s => s.RecordId == recordId && s.Status != ExternalShareStatus.Withdrawn);
        if (share == null || share.Status is not (ExternalShareStatus.Open or ExternalShareStatus.Returned))
            return ActionOutcome.Fail("The supplier isn't being asked for anything on this record right now.");
        var rec = await db.Records.AsNoTracking().FirstAsync(r => r.Id == recordId);
        var contacts = await ActiveContactsAsync(share.OrgId);
        if (contacts.Count == 0) return ActionOutcome.Fail("None of the supplier's portal contacts can sign in yet.");
        var org = await OrganizationNameAsync();
        MailContacts(contacts, $"Reminder: {rec.Number} {rec.Title}",
            $"This is a reminder from {me.Name} at {org}: we're still waiting for your response to {rec.Number}" + (share.DueDate is { } d ? $", due {d:dd MMM yyyy}." : "."),
            share.Id, recordId);
        share.UpdatedAt = DateTime.UtcNow;
        audit.Record(recordId, "Supplier reminded", $"Reminder sent to {Ui(contacts.Count, "portal contact")} of {await SupplierNameAsync(share.OrgId)}");
        await db.SaveChangesAsync();
        return ActionOutcome.Ok($"Reminder sent to {Ui(contacts.Count, "portal contact")}.");
    }

    private void MailContacts(IEnumerable<User> contacts, string subject, string body, int shareId, Guid recordId)
    {
        var link = AbsoluteUrl(Url(shareId));
        foreach (var u in contacts) // kept in the record's email log, so the internal side sees what the supplier was sent
            notify.Notify(u.Id, NotificationKind.Info, subject, $"{body}\n\nOpen it in the supplier portal: {link}", recordId, url: Url(shareId));
    }

    private static string Ui(int n, string noun) => Vellum.Infrastructure.Ui.Ui.Plural(n, noun);

    public static string Date(DateOnly? d) => d?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "–";
}
