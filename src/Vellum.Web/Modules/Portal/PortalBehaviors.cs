using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure.Files;
using Vellum.Modules.Audits;
using Vellum.Modules.Quality;

namespace Vellum.Modules.Portal;

/// <summary>The "Supplier portal" card on a shared record (portal status visible internally).</summary>
public sealed class PortalStatusVm
{
    public ExternalShare? Share { get; init; }
    public PortalKind? Kind { get; init; }
    public string? SupplierName { get; init; }
    public Guid? SupplierId { get; init; }
    public string? SharedBy { get; init; }
    public string? SubmittedBy { get; init; }
    public int ContactCount { get; init; }
    /// <summary>Action key the card's main button opens, when available.</summary>
    public string? PrimaryAction { get; init; }
    public string? PrimaryLabel { get; init; }
    public string? Hint { get; init; }
    public List<(Record Record, ExternalShare? Share)> Related { get; init; } = [];
    public PortalState? State => Share is { } s && Kind is { } k ? InternalState(s, k) : null;

    /// <summary>The status as the internal side reads it.</summary>
    public static PortalState InternalState(ExternalShare s, PortalKind k) => s.Status switch
    {
        ExternalShareStatus.Withdrawn => new("Not shared", "neutral", "bi-eye-slash", false, true),
        _ when k.ReadOnly => new("Shared", "info", "bi-eye", false, true),
        ExternalShareStatus.Open => s.DueDate is { } d && d < BusinessCalendar.Today
            ? new("Waiting for the supplier (overdue)", "bad", "bi-hourglass-bottom", false, false)
            : new("Waiting for the supplier", "warn", "bi-hourglass-split", false, false),
        ExternalShareStatus.Returned => new("Returned to the supplier", "warn", "bi-arrow-return-left", false, false),
        ExternalShareStatus.Submitted => new("Supplier responded: review", "info", "bi-inbox", true, false),
        ExternalShareStatus.Accepted => new("Accepted", "ok", "bi-check-circle", false, true),
        _ => new(s.Status.ToString(), "neutral", "bi-circle", false, true),
    };
}

public sealed class PortalContactsVm
{
    public required Guid SupplierId { get; init; }
    public required string SupplierName { get; init; }
    public required List<PortalContactVm> Contacts { get; init; }
    public required List<(Record Record, ExternalShare Share, PortalKind Kind)> Requests { get; init; }
    public required bool CanManage { get; init; }
    public required HashSet<string> EnabledActions { get; init; }
}

/// <summary>Helpers shared by the portal behaviors.</summary>
internal static class PortalKit
{
    public const string Group = "Supplier portal";

    public static PortalService Portal(this RecordContext ctx) => ctx.Get<PortalService>();

    /// <summary>Supplier profiles that have at least one active portal contact, best name match first.</summary>
    public static async Task<List<(Guid Id, string Name)>> SuppliersWithPortalAsync(RecordContext ctx, string? preferName)
    {
        var orgs = await ctx.Db.Users.AsNoTracking().Where(u => u.IsExternal && u.ExternalOrgId != null && u.Status == UserStatus.Active)
            .Select(u => u.ExternalOrgId!.Value).Distinct().ToListAsync();
        var names = await ctx.Db.RecordValues.AsNoTracking().Where(v => orgs.Contains(v.RecordId) && v.RowId == null && v.FieldKey == "name")
            .Select(v => new { v.RecordId, v.Text }).ToListAsync();
        var list = names.Select(n => (n.RecordId, n.Text ?? "Supplier")).OrderBy(n => n.Item2).ToList();
        var best = BestMatch(list, preferName);
        if (best is { } b) { list.Remove(b); list.Insert(0, b); }
        return list;
    }

    public static (Guid, string)? BestMatch(List<(Guid Id, string Name)> list, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        static string First(string s) => s.Split([' ', ',', '.', '-'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        var exact = list.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact.Id != Guid.Empty) return exact;
        var word = First(name);
        var byWord = list.FirstOrDefault(x => word.Length > 2 && First(x.Name) == word);
        return byWord.Id != Guid.Empty ? byWord : null;
    }

    public static async Task<PortalStatusVm> StatusAsync(RecordContext ctx, PortalKind kind, string? primaryAction = null, string? primaryLabel = null, string? hint = null)
    {
        var portal = ctx.Portal();
        var share = await portal.ShareOfAsync(ctx.Record.Id);
        return new PortalStatusVm
        {
            Share = share, Kind = kind,
            SupplierId = share?.OrgId,
            SupplierName = share != null ? await portal.SupplierNameAsync(share.OrgId) : null,
            SharedBy = share != null ? ctx.Directory.Name(share.SharedById) : null,
            SubmittedBy = share?.SubmittedById is { } sb ? ctx.Directory.Name(sb) : null,
            ContactCount = share != null ? (await portal.ActiveContactsAsync(share.OrgId)).Count : 0,
            PrimaryAction = primaryAction, PrimaryLabel = primaryLabel, Hint = hint,
        };
    }

    public static RecordPanel Panel(PortalStatusVm vm) => new()
    {
        Key = "portal-status", Title = "Supplier portal", Icon = "bi-globe2", Placement = "side", Order = 5,
        Partial = "~/Modules/Portal/Views/_PortalStatus.cshtml", Model = vm,
    };

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

// ====================================================================== the three request forms

/// <summary>
/// Document requests, questionnaires and audit finding responses: entering With Supplier shares the record with the supplier's
/// portal (and emails the contacts); Review → With Supplier returns it with the route comment as feedback; completion accepts
/// it; voiding or withdrawing to Draft takes it off the portal. An accepted certificate is filed on the supplier profile.
/// </summary>
public sealed class PortalRequestBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => [PortalKinds.DocForm, PortalKinds.QuestionnaireForm, PortalKinds.AuditResponseForm];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        if (ctx.Proposed.Main.Link("supplier") is not { } sid) return;
        var portal = ctx.Services.GetRequiredService<PortalService>();
        var db = ctx.Services.GetRequiredService<VellumDbContext>();
        var supplier = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == sid);
        if (supplier == null || ctx.Config.FormsById[supplier.FormId].Key != "supplier")
        {
            ctx.Error("supplier", "Choose a supplier profile.");
            return;
        }
        ctx.Set("supplier_name", await portal.SupplierNameAsync(sid));
        // INV-088: once shared, a request stays with its supplier. Sharing with another company means a new request.
        if (ctx.Current != null && ctx.Current.Links("supplier").FirstOrDefault() is var before && before != Guid.Empty && before != sid &&
            await db.Set<ExternalShare>().AnyAsync(s => s.RecordId == ctx.Current.Record.Id && s.Status != ExternalShareStatus.Withdrawn))
            ctx.Error("supplier", "This request is already shared with another supplier. Cancel it and create a new one instead.");
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.To.Key != "with_supplier" || ctx.Kind != RouteKind.Forward) return;
        if (ctx.Snapshot.Links("supplier").FirstOrDefault() is not { } sid || sid == Guid.Empty) { ctx.Error("Choose the supplier first."); return; }
        if (await ctx.Portal().PortalUserCountAsync(sid) == 0)
            ctx.Error($"{ctx.Snapshot.Text("supplier_name") ?? "The supplier"} has no supplier portal contacts yet. Invite one from the supplier profile (Supplier portal › Invite portal contact), then send this again.");
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var portal = ctx.Portal();
        var kind = PortalKinds.ForForm(ctx.Snapshot.Form.Key)!;
        var to = ctx.To;
        if (to.Key == "with_supplier" && ctx.From.Key == "draft")
        {
            var res = await portal.ShareAsync(ctx.Snapshot, ctx.Snapshot.Links("supplier").First(), kind, ctx.Snapshot.Date("due_date"), null);
            if (!res.Success) throw new WorkflowException(string.Join(" ", res.Errors ?? []));
        }
        else if (to.Key == "with_supplier")
            await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Returned, feedback: ctx.Comment);
        else if (to.Key == "review")
        {
            // The supplier submitted (status already Submitted) or the owner recorded a response received another way.
            var share = await portal.ShareOfAsync(ctx.Record.Id);
            if (share is { Status: ExternalShareStatus.Open or ExternalShareStatus.Returned })
                await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Submitted, reason: "Response recorded internally");
        }
        else if (to.Type == PhaseType.Completed)
        {
            await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Accepted);
            if (kind.Key == PortalKinds.Document) await FileCertificateAsync(ctx);
        }
        else if (to.Type == PhaseType.Voided || to.Key == "draft")
            await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Withdrawn, reason: ctx.Comment);
    }

    /// <summary>SUP-003: an accepted quality system certificate is added to the supplier's certificates, with a copy of the file.</summary>
    private static async Task FileCertificateAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (s.KeywordKey("doc_type") != "iso_cert" || s.Bool("add_certificate") == false) return;
        if (s.KeywordKey("standard") is not { } standard || s.Date("expires_on") is not { } expires) return;
        if (s.Links("supplier").FirstOrDefault() is not { } sid || sid == Guid.Empty) return;
        var supplier = await ctx.Records.LoadAsync(sid);
        if (supplier == null) return;
        var number = s.Text("doc_number");
        if (supplier.Rows("certifications").Any(r => supplier.KeywordKey(r, "standard") == standard && r.Values.Date("expires_on") == expires &&
                                                     (number == null || r.Values.Text("cert_number") == number))) return; // already filed

        var proposed = new RecordData();
        var rows = proposed.RowsOf("certifications");
        rows.AddRange(supplier.Rows("certifications").Select(r => r.Clone()));
        rows.Add(Change.ModuleKit.NewRow(supplier.Form, supplier.Config, rows.Count, new()
        {
            ["standard"] = standard, ["cert_number"] = number, ["registrar"] = s.Text("issuer"), ["expires_on"] = expires,
        }));
        var uploads = new List<(string, string, string, Stream)>();
        var store = ctx.Get<FileStore>();
        foreach (var att in s.AttachmentsOf("doc_file").Where(a => !a.Removed))
            uploads.Add(("cert_files", att.FileName, att.ContentType, new MemoryStream(await store.ReadVerifiedAsync(att.Sha256))));
        var res = await ctx.Records.SaveAsync(supplier, proposed, new SaveOptions { System = true, Uploads = uploads, Reason = $"Certificate accepted on {s.Record.Number}" });
        if (!res.Ok) throw new WorkflowException("The certificate couldn't be added to the supplier profile: " + string.Join(" ", res.Errors.Select(e => e.Message)));
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.PhaseKey != "with_supplier" || !await ctx.CanActAsync()) return [];
        var share = await ctx.Portal().ShareOfAsync(ctx.Record.Id);
        if (share is not { Status: ExternalShareStatus.Open or ExternalShareStatus.Returned }) return [];
        return
        [
            new RecordAction
            {
                Key = "portal-remind", Label = "Remind supplier", Icon = "bi-bell", Group = PortalKit.Group,
                Description = "Email the supplier's portal contacts a reminder about this request.",
            },
        ];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "portal-remind" || ctx.Snapshot.PhaseKey != "with_supplier" || !await ctx.CanActAsync())
            return ActionOutcome.Fail("This action isn't available.");
        return await ctx.Portal().RemindAsync(ctx.Record.Id);
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var kind = PortalKinds.ForForm(ctx.Snapshot.Form.Key)!;
        var hint = ctx.Snapshot.PhaseKey == "draft" ? "Use Send to supplier portal when the request is ready. The supplier's portal contacts are emailed." : null;
        return [PortalKit.Panel(await PortalKit.StatusAsync(ctx, kind, hint: hint))];
    }
}

// ====================================================================== SCAR (CAPA-011)

/// <summary>
/// CAPA-011: "Send to supplier portal" invites the supplier to answer the SCAR in the portal; the portal response fills in the
/// supplier response section directly. The SQE can remind, return the response with feedback, or withdraw it; routing the SCAR
/// on to verification accepts it, and "Not effective: back to supplier" reopens it for the supplier.
/// </summary>
public sealed class PortalScarBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["scar"];
    public override int Order => 10;

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        if (s.PhaseKey != "assign_to_supplier" || !await ctx.CanActAsync()) return [];
        var share = await ctx.Portal().ShareOfAsync(ctx.Record.Id);
        var list = new List<RecordAction>();
        if (share is null or { Status: ExternalShareStatus.Withdrawn or ExternalShareStatus.Accepted })
        {
            var suppliers = await PortalKit.SuppliersWithPortalAsync(ctx, s.Text("supplier_name"));
            list.Add(new RecordAction
            {
                Key = "portal-send", Label = "Send to supplier portal", Icon = "bi-globe2", Group = PortalKit.Group,
                Description = "Share this SCAR with the supplier's portal contacts. They see the problem, part and containment request, " +
                              "answer the supplier response section in the portal, and you're notified when they submit (CAPA-011). " +
                              "Internal notes and verification are never shown to them.",
                DisabledReason = suppliers.Count == 0 ? "No supplier has portal contacts yet. Invite one from the supplier profile first." : null,
                Inputs =
                [
                    new ActionField("supplier", "Supplier", "select", true, "Only suppliers with portal contacts are listed.",
                        suppliers.Select(x => (x.Id.ToString(), x.Name)).ToList(), suppliers.FirstOrDefault().Id.ToString()),
                    new ActionField("due", "Response due", "date", true, Default: PortalKit.Iso(s.Date("response_due") ?? BusinessCalendar.Today.AddDays(20))),
                    new ActionField("message", "Message to the supplier", "textarea", Default: s.Text("request_message") ??
                        $"Please investigate {s.Record.Number} ({s.Text("subject")}) and give us your containment, root cause and corrective actions."),
                ],
            });
            return list;
        }
        if (share.Status is ExternalShareStatus.Open or ExternalShareStatus.Returned)
            list.Add(new RecordAction { Key = "portal-remind", Label = "Remind supplier", Icon = "bi-bell", Group = PortalKit.Group,
                Description = "Email the supplier's portal contacts a reminder." });
        if (share.Status == ExternalShareStatus.Submitted)
            list.Add(new RecordAction
            {
                Key = "portal-return", Label = "Return response to supplier", Icon = "bi-arrow-return-left", Group = PortalKit.Group, Variant = "warning",
                Description = "Ask the supplier to change their response. Your comment is shown to them in the portal. To accept it, route the SCAR on to verification.",
                RequiresComment = true, CommentLabel = "Feedback to the supplier",
            });
        list.Add(new RecordAction
        {
            Key = "portal-withdraw", Label = "Withdraw from portal", Icon = "bi-eye-slash", Group = PortalKit.Group, Variant = "secondary",
            Description = "Stop sharing this SCAR. The supplier can no longer see it in the portal.", RequiresComment = true, CommentLabel = "Reason",
        });
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (!actionKey.StartsWith("portal-")) return ActionOutcome.Fail("This action isn't available.");
        if (ctx.Snapshot.PhaseKey != "assign_to_supplier" || !await ctx.CanActAsync())
            return ActionOutcome.Fail("Only the SCAR owner or an application manager can do this while the SCAR is with the supplier.");
        var portal = ctx.Portal();
        switch (actionKey)
        {
            case "portal-send":
            {
                if (!Guid.TryParse(input.Get("supplier"), out var org)) return ActionOutcome.Fail("Choose the supplier.");
                if (PortalKit.ParseDate(input.Get("due")) is not { } due || due < BusinessCalendar.Today) return ActionOutcome.Fail("Choose a response due date that isn't in the past.");
                return await Change.ModuleKit.InTransactionAsync(ctx, async () =>
                {
                    var upd = await ctx.Records.UpdateAsync(ctx.Record.Id, new() { ["sent_on"] = BusinessCalendar.Today, ["response_due"] = due, ["request_message"] = input.Get("message") });
                    if (!upd.Ok) return ActionOutcome.Fail(upd.Errors.Select(e => e.Message).ToArray());
                    var snap = await ctx.Records.LoadRequiredAsync(ctx.Record.Id);
                    return await portal.ShareAsync(snap, org, PortalKinds.ScarKind, due, input.Get("message"));
                });
            }
            case "portal-remind":
                return await portal.RemindAsync(ctx.Record.Id);
            case "portal-return":
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("Tell the supplier what to change.");
                await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Returned, feedback: input.Comment);
                return ActionOutcome.Ok("Returned to the supplier with your feedback.");
            case "portal-withdraw":
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("Give a reason.");
                await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Withdrawn, reason: input.Comment);
                return ActionOutcome.Ok("The SCAR is no longer shared in the supplier portal.");
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var portal = ctx.Portal();
        if (await portal.ShareOfAsync(ctx.Record.Id) is not { } share || share.Status == ExternalShareStatus.Withdrawn) return;
        if (ctx.From.Key == "assign_to_supplier" && ctx.Kind == RouteKind.Forward) await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Accepted);
        else if (ctx.To.Key == "assign_to_supplier" && ctx.Kind == RouteKind.Back) await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Returned, feedback: ctx.Comment);
        else if (ctx.To.Type == PhaseType.Voided || (ctx.From.Key == "assign_to_supplier" && ctx.Kind == RouteKind.Back))
            await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Withdrawn, reason: ctx.Comment);
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var share = await ctx.Portal().ShareOfAsync(ctx.Record.Id);
        if (share == null && ctx.Snapshot.PhaseKey != "assign_to_supplier") return [];
        var canSend = ctx.Snapshot.PhaseKey == "assign_to_supplier" && share is null or { Status: ExternalShareStatus.Withdrawn or ExternalShareStatus.Accepted } &&
                      (await ActionsAsync(ctx)).Any(a => a.Key == "portal-send" && a.DisabledReason == null);
        return [PortalKit.Panel(await PortalKit.StatusAsync(ctx, PortalKinds.ScarKind, canSend ? "portal-send" : null, "Send to supplier portal",
            share == null ? "Not shared yet. The supplier can answer the supplier response section themselves in the portal." : null))];
    }
}

// ====================================================================== scorecards (SUP-036)

public sealed class PortalScorecardBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["scorecard"];
    public override int Order => 10;

    private static bool CanShare(RecordContext ctx) => !ctx.Me.IsSystem && ctx.Access.RoleFor(ctx.Record.AppId, "scorecard") >= AppRole.Author;

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.PhaseKey != "published" || !CanShare(ctx)) return [];
        var share = await ctx.Portal().ShareOfAsync(ctx.Record.Id);
        var sid = ctx.Snapshot.Links("supplier").FirstOrDefault();
        if (share is null or { Status: ExternalShareStatus.Withdrawn })
        {
            var contacts = sid == Guid.Empty ? 0 : await ctx.Portal().PortalUserCountAsync(sid);
            return
            [
                new RecordAction
                {
                    Key = "portal-share", Label = "Send to supplier portal", Icon = "bi-globe2", Group = PortalKit.Group,
                    Description = "Share this published scorecard with the supplier. Their portal contacts see it read-only and are emailed (SUP-036).",
                    DisabledReason = contacts == 0 ? "The supplier has no portal contacts yet. Invite one from the supplier profile." : null,
                    Inputs = [new ActionField("message", "Message to the supplier", "textarea", Default: "Your latest supplier scorecard is available in the supplier portal.")],
                },
            ];
        }
        return
        [
            new RecordAction { Key = "portal-withdraw", Label = "Withdraw from portal", Icon = "bi-eye-slash", Group = PortalKit.Group, Variant = "secondary",
                Description = "Stop sharing this scorecard with the supplier.", RequiresComment = true, CommentLabel = "Reason" },
        ];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (ctx.Snapshot.PhaseKey != "published" || !CanShare(ctx)) return ActionOutcome.Fail("Only published scorecards can be shared, by Supplier Quality authors.");
        var portal = ctx.Portal();
        switch (actionKey)
        {
            case "portal-share":
                if (ctx.Snapshot.Links("supplier").FirstOrDefault() is not { } sid || sid == Guid.Empty) return ActionOutcome.Fail("The scorecard has no supplier.");
                return await portal.ShareAsync(ctx.Snapshot, sid, PortalKinds.ScorecardKind, null, input.Get("message"));
            case "portal-withdraw":
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("Give a reason.");
                await portal.SetStatusAsync(ctx.Record.Id, ExternalShareStatus.Withdrawn, reason: input.Comment);
                return ActionOutcome.Ok("The scorecard is no longer shared in the supplier portal.");
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.PhaseKey != "published") return [];
        var actions = (await ActionsAsync(ctx)).ToList();
        var canShare = actions.Any(a => a.Key == "portal-share" && a.DisabledReason == null);
        return [PortalKit.Panel(await PortalKit.StatusAsync(ctx, PortalKinds.ScorecardKind, canShare ? "portal-share" : null, "Send to supplier portal",
            actions.FirstOrDefault(a => a.Key == "portal-share")?.DisabledReason))];
    }
}

// ====================================================================== supplier audits (AUD-031)

public sealed class PortalAuditBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["audit"];
    public override int Order => 10;

    private static bool Eligible(RecordContext ctx) =>
        ctx.Snapshot.KeywordKey("audit_kind") == "supplier" && ctx.Snapshot.PhaseKey is "scheduled" or "report_approval" or "completed";

    private static bool CanSend(RecordContext ctx) =>
        !ctx.Me.IsSystem && ctx.Access.RoleFor(ctx.Record.AppId) >= AppRole.Author &&
        ctx.Config.FormsByKey.TryGetValue(PortalKinds.AuditResponseForm, out var f) && ctx.Access.CanCreate(f);

    private static List<RowBag> Findings(RecordSnapshot s) =>
        s.Rows("checklist").Where(r => AuditRules.FindingClasses.Contains(s.KeywordKey(r, "classification"))).OrderBy(r => r.Order).ToList();

    private static async Task<List<Record>> ResponsesAsync(RecordContext ctx)
    {
        if (!ctx.Config.FormsByKey.TryGetValue(PortalKinds.AuditResponseForm, out var f)) return [];
        var id = ctx.Record.Id;
        return await ctx.Db.Records.AsNoTracking().Where(r => r.FormId == f.Id &&
            ctx.Db.Links.Any(l => l.FromId == id && l.ToId == r.Id && l.Kind == LinkKind.Source)).OrderBy(r => r.CreatedAt).ToListAsync();
    }

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (!Eligible(ctx) || !CanSend(ctx)) return [];
        var s = ctx.Snapshot;
        var findings = Findings(s);
        var open = (await ResponsesAsync(ctx)).Any(r => r.PhaseType == PhaseType.Normal);
        var suppliers = await PortalKit.SuppliersWithPortalAsync(ctx, s.Text("auditee"));
        return
        [
            new RecordAction
            {
                Key = "portal-findings", Label = "Send findings to supplier portal", Icon = "bi-globe2", Group = PortalKit.Group,
                Description = "Create an audit finding response with this audit's findings and share it with the supplier's portal contacts. " +
                              "They answer each finding (root cause, correction, corrective action) in the portal (AUD-031).",
                DisabledReason = findings.Count == 0 ? "This audit has no findings to send." : open ? "The findings are already with the supplier." :
                    suppliers.Count == 0 ? "No supplier has portal contacts yet. Invite one from the supplier profile first." : null,
                Inputs =
                [
                    new ActionField("supplier", "Supplier", "select", true, "Only suppliers with portal contacts are listed.",
                        suppliers.Select(x => (x.Id.ToString(), x.Name)).ToList(), suppliers.FirstOrDefault().Id.ToString()),
                    new ActionField("due", "Responses due", "date", true, Default: PortalKit.Iso(BusinessCalendar.Today.AddDays(30))),
                    new ActionField("instructions", "Instructions to the supplier", "textarea",
                        Default: "Please answer each finding. Minor and major nonconformances need a root cause, a correction, a corrective action and a target date."),
                ],
            },
        ];
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (actionKey != "portal-findings" || !Eligible(ctx) || !CanSend(ctx)) return ActionOutcome.Fail("This action isn't available.");
        if (!Guid.TryParse(input.Get("supplier"), out var org)) return ActionOutcome.Fail("Choose the supplier.");
        if (PortalKit.ParseDate(input.Get("due")) is not { } due || due < BusinessCalendar.Today) return ActionOutcome.Fail("Choose a due date that isn't in the past.");
        var s = ctx.Snapshot;
        var findings = Findings(s);
        if (findings.Count == 0) return ActionOutcome.Fail("This audit has no findings to send.");
        if ((await ResponsesAsync(ctx)).Any(r => r.PhaseType == PhaseType.Normal)) return ActionOutcome.Fail("The findings are already with the supplier.");

        return await Change.ModuleKit.InTransactionAsync(ctx, async () =>
        {
            var subject = $"Findings of {s.Record.Number}: {s.Text("subject")}";
            var res = await ctx.Records.CreateAsync(new CreateRequest
            {
                FormKey = PortalKinds.AuditResponseForm,
                Values = new()
                {
                    ["supplier"] = org, ["subject"] = subject.Length > 200 ? subject[..197] + "…" : subject, ["audit"] = s.Record.Id,
                    ["audit_title"] = s.Text("subject"), ["audit_date"] = s.DateTime("start_at") is { } st ? DateOnly.FromDateTime(st) : null,
                    ["auditor_name"] = ctx.Directory.Get(s.UserId("lead_auditor"))?.DisplayName, ["owner"] = ctx.Me.Id, ["due_date"] = due,
                    ["instructions"] = input.Get("instructions"),
                },
                RowValues = new()
                {
                    ["findings"] = findings.Select((r, i) => new Dictionary<string, object?>
                    {
                        ["finding_no"] = $"{i + 1}", ["clause"] = r.Values.Text("clause"), ["classification"] = s.KeywordKey(r, "classification"),
                        ["finding"] = r.Values.Text("question"), ["evidence"] = r.Values.Text("comments"),
                    }).ToList(),
                },
                CreateComment = $"Findings of supplier audit {s.Record.Number} sent to the supplier (AUD-031)",
            });
            if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            await ctx.Records.LinkAsync(s.Record.Id, res.Record!.Id, LinkKind.Source);
            await ctx.Db.SaveChangesAsync();
            var created = await ctx.Records.LoadRequiredAsync(res.Record.Id);
            var route = created.Workflow!.Routes.First(r => r.FromPhaseId == created.Record.PhaseId && r.ToPhase.Key == "with_supplier");
            var routed = await ctx.Workflow.RouteAsync(new RouteRequest { RecordId = created.Record.Id, RouteId = route.Id });
            if (!routed.Ok) return ActionOutcome.Fail(routed.Errors.Select(e => e.Message).ToArray());
            return ActionOutcome.Ok($"{res.Record.Number} sent to the supplier with {Vellum.Infrastructure.Ui.Ui.Plural(findings.Count, "finding")}.", res.Record.Id);
        });
    }

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        if (ctx.Snapshot.KeywordKey("audit_kind") != "supplier") return [];
        var responses = await ResponsesAsync(ctx);
        var portal = ctx.Portal();
        var related = new List<(Record, ExternalShare?)>();
        foreach (var r in responses) related.Add((r, await portal.ShareOfAsync(r.Id)));
        var canSend = (await ActionsAsync(ctx)).Any(a => a.Key == "portal-findings" && a.DisabledReason == null);
        if (related.Count == 0 && !canSend) return [];
        return [PortalKit.Panel(new PortalStatusVm
        {
            Kind = PortalKinds.AuditResponseKind, Related = related,
            PrimaryAction = canSend ? "portal-findings" : null, PrimaryLabel = "Send findings to supplier portal",
            Hint = related.Count == 0 ? "The supplier can answer this audit's findings in the supplier portal (AUD-031)." : null,
        })];
    }
}

// ====================================================================== supplier profile: portal contacts and requests

/// <summary>
/// The supplier profile's "Supplier portal" tab: who has portal access (invite, resend, disable, enable) and the requests
/// shared with the supplier. Also "Request a document" and "Send a questionnaire". Supplier Quality authors and
/// administrators manage portal access.
/// </summary>
public sealed class PortalSupplierBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["supplier"];
    public override int Order => 10;

    private static bool CanManage(RecordContext ctx) => !ctx.Me.IsSystem && !ctx.Me.IsExternal && ctx.Access.RoleFor(ctx.Record.AppId, "supplier") >= AppRole.Author;

    private static readonly (string Text, string? Guidance, string Type, bool Required)[] StandardQuestions =
    [
        ("Does your company hold a current ISO 9001 or ISO 13485 certificate?", "Upload the certificate with your answers.", "yes_no", true),
        ("Have your ownership, sites or key personnel changed in the last 12 months?", "If yes, describe the change in your comments.", "yes_no", true),
        ("How do you control changes to processes, materials and sub-tier suppliers that affect our parts?", "Include how and when you notify us.", "text", true),
        ("How do you select, evaluate and monitor your own suppliers?", null, "text", true),
        ("Is all inspection, measuring and test equipment calibrated to traceable standards?", null, "yes_no", true),
        ("How do you handle nonconforming product, including telling us about it?", null, "text", true),
        ("Do you have a documented business continuity plan covering our parts?", null, "yes_no", false),
        ("Anything else you'd like us to know?", null, "text", false),
    ];

    public override async Task<IEnumerable<RecordAction>> ActionsAsync(RecordContext ctx)
    {
        if (!CanManage(ctx)) return [];
        var s = ctx.Snapshot;
        var contacts = await ctx.Get<PortalAccounts>().ContactsAsync(s.Record.Id, s.Rows("contacts"));
        var invitable = contacts.Where(c => c.UserId == null && c.ContactRowId != null && !string.IsNullOrWhiteSpace(c.Email)).ToList();
        var resendable = contacts.Where(c => c.UserId != null && c.Access is not PortalAccess.Disabled).ToList();
        var disableable = contacts.Where(c => c.UserId != null && c.Access is PortalAccess.Active or PortalAccess.Invited or PortalAccess.InviteExpired or PortalAccess.Locked).ToList();
        var enableable = contacts.Where(c => c.UserId != null && c.Access is PortalAccess.Disabled or PortalAccess.Locked).ToList();
        static string Label(PortalContactVm c) => $"{c.Name} ({c.Email})";
        var list = new List<RecordAction>
        {
            new()
            {
                Key = "portal-invite", Label = "Invite portal contact", Icon = "bi-person-plus", Group = PortalKit.Group,
                Description = "Create a supplier portal account and email a one-time link to set a password. Pick one of the supplier's contacts, or enter someone new (they're added to the contacts).",
                Inputs =
                [
                    new ActionField("contact", "Contact", "select", false, invitable.Count == 0 ? "Every contact with an email already has access." : null,
                        invitable.Select(c => (c.ContactRowId!.Value.ToString(), Label(c))).ToList(), invitable.FirstOrDefault()?.ContactRowId?.ToString()),
                    new ActionField("name", "Or a new contact: name"),
                    new ActionField("email", "Email"),
                    new ActionField("role", "Role", Help: "For example Quality Manager."),
                ],
            },
            new()
            {
                Key = "portal-resend", Label = "Resend invitation", Icon = "bi-envelope-arrow-up", Group = PortalKit.Group,
                Description = "Email a new one-time link: an invitation if they haven't set a password yet, otherwise a password reset link. Earlier links stop working.",
                DisabledReason = resendable.Count == 0 ? "Nobody has portal access yet." : null,
                Inputs = [new ActionField("user", "Portal contact", "select", true, Options: resendable.Select(c => (c.UserId!.Value.ToString(), Label(c))).ToList(),
                    Default: resendable.FirstOrDefault(c => c.Access is PortalAccess.Invited or PortalAccess.InviteExpired)?.UserId?.ToString())],
            },
            new()
            {
                Key = "portal-disable", Label = "Disable portal access", Icon = "bi-person-slash", Group = PortalKit.Group, Variant = "danger",
                Description = "The contact is signed out at once and can't sign in again until access is enabled.",
                DisabledReason = disableable.Count == 0 ? "Nobody has portal access to disable." : null, RequiresComment = true, CommentLabel = "Reason",
                Inputs = [new ActionField("user", "Portal contact", "select", true, Options: disableable.Select(c => (c.UserId!.Value.ToString(), Label(c))).ToList())],
            },
        };
        if (enableable.Count > 0)
            list.Add(new RecordAction
            {
                Key = "portal-enable", Label = "Enable portal access", Icon = "bi-person-check", Group = PortalKit.Group,
                Description = "Let a disabled or locked contact sign in to the supplier portal again.",
                Inputs = [new ActionField("user", "Portal contact", "select", true, Options: enableable.Select(c => (c.UserId!.Value.ToString(), Label(c))).ToList())],
            });

        var noPortal = contacts.All(c => c.UserId == null || c.Access is PortalAccess.Disabled) ? "Invite a portal contact first." : null;
        if (ctx.Config.FormsByKey.TryGetValue(PortalKinds.DocForm, out var df) && ctx.Access.CanCreate(df))
            list.Add(new RecordAction
            {
                Key = "portal-request-doc", Label = "Request a document", Icon = "bi-file-earmark-arrow-up", Group = PortalKit.Group,
                Description = "Ask the supplier for a certificate, SDS or declaration through the supplier portal. An accepted certificate is added to this profile.",
                DisabledReason = noPortal,
                Inputs =
                [
                    new ActionField("doc_type", "Document type", "select", true, Options: ctx.Config.Options("sdr_doc_type").Select(k => (k.Key, k.Label)).ToList(), Default: "iso_cert"),
                    new ActionField("subject", "Document requested", Required: true, Default: "Current quality system certificate"),
                    new ActionField("standard", "Standard (certificates)", "select", Options: ctx.Config.Options("sup_cert_std").Select(k => (k.Key, k.Label)).ToList()),
                    new ActionField("due", "Needed by", "date", true, Default: PortalKit.Iso(BusinessCalendar.Today.AddDays(14))),
                    new ActionField("instructions", "Instructions to the supplier", "textarea"),
                ],
            });
        if (ctx.Config.FormsByKey.TryGetValue(PortalKinds.QuestionnaireForm, out var qf) && ctx.Access.CanCreate(qf))
            list.Add(new RecordAction
            {
                Key = "portal-questionnaire", Label = "Send a questionnaire", Icon = "bi-ui-checks", Group = PortalKit.Group,
                Description = "Create a supplier questionnaire, optionally starting from the standard self-assessment questions. It opens in Draft so you can edit the questions before sending.",
                Inputs =
                [
                    new ActionField("subject", "Title", Required: true, Default: $"{BusinessCalendar.Today.Year} supplier self-assessment"),
                    new ActionField("type", "Type", "select", true, Options: ctx.Config.Options("sq_type").Select(k => (k.Key, k.Label)).ToList(), Default: "self_assessment"),
                    new ActionField("due", "Answers due", "date", true, Default: PortalKit.Iso(BusinessCalendar.Today.AddDays(21))),
                    new ActionField("standard_questions", "Start from the standard questions?", "yesno", Default: "yes"),
                ],
            });
        return list;
    }

    public override async Task<ActionOutcome> ExecuteAsync(RecordContext ctx, string actionKey, ActionInput input)
    {
        if (!actionKey.StartsWith("portal-")) return ActionOutcome.Fail("This action isn't available.");
        if (!CanManage(ctx)) return ActionOutcome.Fail("You need author access to Supplier Quality to manage the supplier portal.");
        var accounts = ctx.Get<PortalAccounts>();
        var s = ctx.Snapshot;
        int? UserIn() => int.TryParse(input.Get("user"), out var id) ? id : null;
        switch (actionKey)
        {
            case "portal-invite":
            {
                string? name, email, role;
                if (Guid.TryParse(input.Get("contact"), out var rowId) && input.Get("email") == null)
                {
                    var row = s.Rows("contacts").FirstOrDefault(r => r.Id == rowId);
                    if (row == null) return ActionOutcome.Fail("That contact no longer exists.");
                    (name, email, role) = (row.Values.Text("contact_name"), row.Values.Text("email"), row.Values.Text("contact_role"));
                    if (string.IsNullOrWhiteSpace(email)) return ActionOutcome.Fail("Add an email address to the contact first.");
                    return await accounts.InviteAsync(s.Record.Id, name ?? email!, email!, role); // commits on its own (see InviteAsync)
                }
                (name, email, role) = (input.Get("name"), input.Get("email"), input.Get("role"));
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)) return ActionOutcome.Fail("Pick a contact, or enter the new contact's name and email.");
                var invited = await accounts.InviteAsync(s.Record.Id, name!, email!, role);
                if (!invited.Success) return invited;
                // Keep the supplier's contact list complete: the new portal contact is added to it.
                var proposed = new RecordData();
                var rows = proposed.RowsOf("contacts");
                rows.AddRange(s.Rows("contacts").Select(r => r.Clone()));
                rows.Add(Change.ModuleKit.NewRow(s.Form, s.Config, rows.Count, new() { ["contact_name"] = name!.Trim(), ["contact_role"] = role, ["email"] = email!.Trim() }));
                ctx.Db.ChangeTracker.Clear();
                var saved = await ctx.Records.SaveAsync(await ctx.Records.LoadRequiredAsync(s.Record.Id), proposed, new SaveOptions { System = true, Reason = "Supplier portal contact invited" });
                return saved.Ok ? invited : ActionOutcome.Ok(invited.Message + " (Add them to the contacts list yourself: " + string.Join(" ", saved.Errors.Select(e => e.Message)) + ")");
            }
            case "portal-resend":
                return UserIn() is { } ru ? await accounts.ResendAsync(s.Record.Id, ru) : ActionOutcome.Fail("Choose a portal contact.");
            case "portal-disable":
                if (string.IsNullOrWhiteSpace(input.Comment)) return ActionOutcome.Fail("Give a reason.");
                return UserIn() is { } du ? await accounts.DisableAsync(s.Record.Id, du, input.Comment!) : ActionOutcome.Fail("Choose a portal contact.");
            case "portal-enable":
                return UserIn() is { } eu ? await accounts.EnableAsync(s.Record.Id, eu) : ActionOutcome.Fail("Choose a portal contact.");
            case "portal-request-doc":
            {
                if (PortalKit.ParseDate(input.Get("due")) is not { } due || due < BusinessCalendar.Today) return ActionOutcome.Fail("Choose a date that isn't in the past.");
                var docType = input.Get("doc_type");
                if (docType == null || ctx.Config.Keyword("sdr_doc_type", docType) == null) return ActionOutcome.Fail("Choose the document type.");
                if (string.IsNullOrWhiteSpace(input.Get("subject"))) return ActionOutcome.Fail("Say which document you need.");
                return await Change.ModuleKit.InTransactionAsync(ctx, async () =>
                {
                    var res = await ctx.Records.CreateAsync(new CreateRequest
                    {
                        FormKey = PortalKinds.DocForm,
                        Values = new()
                        {
                            ["supplier"] = s.Record.Id, ["doc_type"] = docType, ["subject"] = input.Get("subject"), ["owner"] = ctx.Me.Id, ["due_date"] = due,
                            ["standard"] = docType == "iso_cert" && input.Get("standard") is { } std && ctx.Config.Keyword("sup_cert_std", std) != null ? std : null,
                            ["instructions"] = input.Get("instructions"),
                        },
                        CreateComment = $"Requested from supplier {s.Record.Number}",
                    });
                    if (!res.Ok) return ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
                    var created = await ctx.Records.LoadRequiredAsync(res.Record!.Id);
                    var route = created.Workflow!.Routes.First(r => r.FromPhaseId == created.Record.PhaseId && r.ToPhase.Key == "with_supplier");
                    var routed = await ctx.Workflow.RouteAsync(new RouteRequest { RecordId = created.Record.Id, RouteId = route.Id });
                    if (!routed.Ok) return ActionOutcome.Fail(routed.Errors.Select(e => e.Message).ToArray());
                    return ActionOutcome.Ok($"{res.Record.Number} sent to the supplier through the portal.", res.Record.Id);
                });
            }
            case "portal-questionnaire":
            {
                if (PortalKit.ParseDate(input.Get("due")) is not { } due || due < BusinessCalendar.Today) return ActionOutcome.Fail("Choose a date that isn't in the past.");
                var type = input.Get("type");
                if (type == null || ctx.Config.Keyword("sq_type", type) == null) return ActionOutcome.Fail("Choose the questionnaire type.");
                var standard = input.Get("standard_questions") != "no";
                var res = await ctx.Records.CreateAsync(new CreateRequest
                {
                    FormKey = PortalKinds.QuestionnaireForm,
                    Values = new()
                    {
                        ["supplier"] = s.Record.Id, ["subject"] = input.Get("subject") ?? "Supplier questionnaire", ["questionnaire_type"] = type,
                        ["owner"] = ctx.Me.Id, ["due_date"] = due,
                    },
                    RowValues = standard ? new() { ["questions"] = StandardQuestionRows() } : null,
                    CreateComment = $"Created from supplier {s.Record.Number}",
                });
                return res.Ok
                    ? ActionOutcome.Ok($"{res.Record!.Number} created in Draft. Check the questions, then use Send to supplier portal.", res.Record.Id)
                    : ActionOutcome.Fail(res.Errors.Select(e => e.Message).ToArray());
            }
        }
        return ActionOutcome.Fail("This action isn't available.");
    }

    public static List<Dictionary<string, object?>> StandardQuestionRows() => StandardQuestions.Select(q => new Dictionary<string, object?>
    {
        ["q_text"] = q.Text, ["q_guidance"] = q.Guidance, ["q_type"] = q.Type, ["q_required"] = q.Required,
    }).ToList();

    public override async Task<IEnumerable<RecordPanel>> PanelsAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var portal = ctx.Portal();
        var contacts = await ctx.Get<PortalAccounts>().ContactsAsync(s.Record.Id, s.Rows("contacts"));
        var shares = await ctx.Db.Set<ExternalShare>().AsNoTracking().Where(x => x.OrgId == s.Record.Id).OrderByDescending(x => x.UpdatedAt).Take(50).ToListAsync();
        var ids = shares.Select(x => x.RecordId).ToList();
        // Only requests the internal user can read themselves are listed (INV-081).
        var recs = await ctx.Access.Readable().AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var requests = shares.Where(x => recs.ContainsKey(x.RecordId) && PortalKinds.Get(x.Kind) != null)
            .Select(x => (recs[x.RecordId], x, PortalKinds.Get(x.Kind)!)).ToList();
        var enabled = CanManage(ctx) ? (await ActionsAsync(ctx)).Where(a => a.DisabledReason == null).Select(a => a.Key).ToHashSet() : [];
        var vm = new PortalContactsVm
        {
            SupplierId = s.Record.Id, SupplierName = s.Text("name") ?? s.Record.Title, Contacts = contacts, Requests = requests,
            CanManage = CanManage(ctx), EnabledActions = enabled,
        };
        _ = portal;
        return
        [
            new RecordPanel
            {
                Key = "portal-contacts", Title = "Supplier portal", Icon = "bi-globe2", Placement = "tab", Order = 4,
                Count = contacts.Count(c => c.Access is PortalAccess.Active or PortalAccess.Invited),
                Partial = "~/Modules/Portal/Views/_PortalContacts.cshtml", Model = vm,
            },
        ];
    }
}
