using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Behaviors;

namespace Vellum.Modules.AdverseEvents;

public sealed record AckRow(Guid Id, string Number, string Type, bool Accepted, DateTime? ReceivedAt, string? Detail, string? Reference, string? Source, string? MessageId);

public sealed record AerStep(string Label, string State, DateTime? At, string? Note = null);

/// <summary>Everything the MedWatch status panel and acknowledgements tab show.</summary>
public sealed class MedWatchPanelModel
{
    public required Guid Id { get; init; }
    public required string Number { get; init; }
    public string? ReportNumber { get; init; }
    public Keyword? ReportType { get; init; }
    public Keyword? SubmissionStatus { get; init; }
    public DateOnly? Due { get; init; }
    public string? Rule { get; init; }
    public DateOnly Today { get; init; }
    public bool Open { get; init; }
    public bool Completed { get; init; }
    public bool Voided { get; init; }
    public string PhaseKey { get; init; } = "";
    public EmdrSubmission? Current { get; init; }
    public List<EmdrSubmission> History { get; init; } = [];
    public List<AckRow> Acks { get; init; } = [];
    public List<EmdrIssue> Issues { get; init; } = [];
    public bool ShowValidation { get; init; }
    public string? GatewayName { get; init; }
    public bool ManualGateway { get; init; }
    public (Guid Id, string Number)? Complaint { get; init; }
    public (Guid Id, string Number)? Tree { get; init; }
    public (Guid Id, string Number)? Original { get; init; }
    public int? FollowUp { get; init; }
    public int ApprovalsTotal { get; init; }
    public int ApprovalsDone { get; init; }
    public int ApprovalsChanges { get; init; }
    public bool FinalConfirmed { get; init; }
    public string? ProfileProblem { get; init; }
    public bool MappingOn { get; init; }
    public bool NumberPending { get; init; }
    public List<AerStep> Steps { get; init; } = [];
    public TimeZoneInfo Tz { get; init; } = TimeZoneInfo.Utc;
    public int ReceiptDelayMinutes { get; init; }

    public int? DaysLeft => Due is { } d ? d.DayNumber - Today.DayNumber : null;
    public bool Accepted => Current?.Status == EmdrStatus.Accepted;
    public string DeadlineState => Accepted || Completed ? "done" : Voided ? "closed" : DaysLeft is < 0 ? "missed" : DaysLeft is <= 5 ? "soon" : "ok";
    public int BlockingCount => Issues.Count(i => i.Blocking);

    public static async Task<MedWatchPanelModel> BuildAsync(RecordContext ctx)
    {
        var s = ctx.Snapshot;
        var db = ctx.Db;
        var subs = ctx.Get<EmdrSubmissionService>();
        var history = await subs.HistoryAsync(s.Record.Id);
        var current = history.FirstOrDefault(h => !h.Superseded);
        var gw = ctx.Get<EmdrGateways>().Get(current?.Gateway);
        var ackForm = ctx.Config.FormsByKey.GetValueOrDefault(AerKeys.Ack);
        var acks = new List<AckRow>();
        if (ackForm != null)
            foreach (var row in await AerData.LoadAsync(db, ackForm, ids: await db.Records.AsNoTracking()
                         .Where(r => r.ParentId == s.Record.Id && r.FormId == ackForm.Id).Select(r => r.Id).ToListAsync()))
            {
                var m = row.Data.Main;
                acks.Add(new AckRow(row.Record.Id, row.Record.Number, ctx.Config.Keyword(m.Ref("ack_type"))?.Key ?? "", ctx.Config.Keyword(m.Ref("ack_status"))?.Key == "accepted",
                    m.DateTime("ack_received_at"), m.Text("ack_detail"), m.Text("ack_fda_reference"), m.Text("ack_source"), m.Text("ack_message_id")));
            }
        acks = acks.OrderByDescending(a => a.ReceivedAt).ToList();

        var approvals = s.PhaseKey is "approval" ? await MedWatchBehavior.CurrentApprovalsAsync(ctx) : [];
        var live = approvals.Where(a => a.PhaseType != PhaseType.Voided).ToList();
        var setup = ctx.Get<AerSetup>();
        var profile = await setup.ProfileAsync();
        var emdr = await ctx.Get<EmdrProfileService>().CurrentAsync();
        var showValidation = s.PhaseKey is "draft" or "approval";
        var issues = showValidation ? await MedWatchBehavior.ValidateAsync(ctx, s.PhaseKey == "draft" ? "approval" : "submit") : [];

        async Task<(Guid, string)?> Link(string field)
        {
            var id = s.Links(field).Cast<Guid?>().FirstOrDefault();
            if (id == null) return null;
            var num = await ctx.Access.Readable().AsNoTracking().Where(r => r.Id == id).Select(r => r.Number).FirstOrDefaultAsync();
            return num == null ? null : (id.Value, num);
        }

        var steps = new List<AerStep>();
        if (current != null)
        {
            var failed = current.Status is EmdrStatus.Rejected or EmdrStatus.Failed;
            steps.Add(new("File generated", "done", current.CreatedAt));
            steps.Add(new(current.Gateway == "manual" ? "Uploaded to ESG" : "Sent to FDA", current.SentAt != null ? "done" : current.Status == EmdrStatus.Failed ? "bad" : "current", current.SentAt));
            string State(DateTime? at, DateTime? prev, bool isLast) =>
                at != null ? (failed && isLast ? "bad" : "done") : prev != null && !failed ? (current.OverdueAlertedAt != null ? "late" : "current") : "todo";
            var rejectedAt = failed ? current.Ack3At ?? current.Ack2At ?? current.Ack1At : null;
            steps.Add(new("ACK1 · ESG", current.Ack1At != null && rejectedAt == current.Ack1At && failed ? "bad" : State(current.Ack1At, current.SentAt, false), current.Ack1At));
            steps.Add(new("ACK2 · CDRH", current.Ack2At != null && rejectedAt == current.Ack2At && failed ? "bad" : State(current.Ack2At, current.Ack1At, false), current.Ack2At));
            steps.Add(new("ACK3 · loaded", current.Ack3At != null && failed ? "bad" : State(current.Ack3At, current.Ack2At, true), current.Ack3At));
            steps.Add(new("Final acknowledgement", s.Record.PhaseType == PhaseType.Completed ? "done" : current.Status == EmdrStatus.Accepted ? "current" : "todo", null));
        }

        return new MedWatchPanelModel
        {
            Id = s.Record.Id, Number = s.Record.Number, ReportNumber = s.Text("report_number"), ReportType = s.Keyword("report_type"),
            SubmissionStatus = s.Keyword("submission_status"), Due = s.Date("report_due"), Rule = s.Text("report_rule"), Today = Engine.Records.BusinessCalendar.Today,
            Open = s.Record.PhaseType == PhaseType.Normal, Completed = s.Record.PhaseType == PhaseType.Completed, Voided = s.Record.PhaseType == PhaseType.Voided,
            PhaseKey = s.PhaseKey, Current = current, History = history, Acks = acks, Issues = issues, ShowValidation = showValidation,
            GatewayName = gw?.Name, ManualGateway = gw?.ManualAcknowledgements == true,
            Complaint = await Link("complaint"), Tree = await Link("mdr_tree"), Original = await Link("original_medwatch"),
            FollowUp = s.Number("followup_number") is { } f ? (int)f : null,
            ApprovalsTotal = live.Count, ApprovalsDone = live.Count(a => a.Phase?.Key == "approved"), ApprovalsChanges = live.Count(a => a.Phase?.Key == "changes_requested"),
            FinalConfirmed = s.Bool("confirm_final_ack") == true,
            ProfileProblem = s.Record.PhaseType != PhaseType.Normal ? null : profile == null ? "The Adverse Event Reporting application profile isn't set up (AER-002)."
                : emdr == null ? "There's no eMDR gateway profile for this environment yet: an administrator sets it up before reports can be submitted (INT-902)." : null,
            MappingOn = profile?.MappingEnabled == true,
            NumberPending = s.Text("report_number") == null,
            Steps = steps, Tz = ctx.Records.TimeZone, ReceiptDelayMinutes = profile?.ReceiptDelayMinutes ?? AerProfileInfo.DefaultReceiptDelayMinutes,
        };
    }
}
