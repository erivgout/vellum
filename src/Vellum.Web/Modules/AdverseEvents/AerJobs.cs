using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.AdverseEvents;

/// <summary>
/// AER-008 / AER-010 / SAF-020: collects acknowledgements from the gateway (the simulated one delivers ACK1-3 on a timer), and raises
/// safety-critical alerts plus an urgent action item when a submission is rejected or fails, when acknowledgements don't arrive within the
/// receipt delay, and when a report's deadline passes before the FDA has accepted it. Works from current state, so a run after
/// downtime catches up (SAF-030); every alert is raised once.
/// </summary>
public sealed class EmdrAcknowledgementJob(EmdrSubmissionService submissions) : IScheduledJob
{
    public string Key => "aer-emdr-acknowledgements";
    public string Name => "eMDR acknowledgements";
    public string Description => "Collects FDA eMDR acknowledgements (ACK1, ACK2, ACK3) and alerts regulatory affairs to rejections, failures, overdue acknowledgements and missed deadlines (AER-010).";
    public int DefaultIntervalMinutes => 1;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var result = await submissions.MonitorAsync(DateTime.UtcNow, ct);
        foreach (var line in result.Log) ctx.Log(line);
        return new JobResult(result.Acks + result.Alerts, $"Recorded {result.Acks} acknowledgement(s); raised {result.Alerts} safety-critical alert(s).");
    }
}

/// <summary>Sidebar links for regulatory affairs: the eMDR submissions board and, for managers, the setup page.</summary>
public sealed class AerNav(AccessService access, ConfigCache cfg) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        if (!cfg.Current.AppsByKey.TryGetValue(AerKeys.App, out var app) || !access.CanOpenApp(app.Id)) yield break;
        yield return new NavItem("Regulatory reporting", "eMDR submissions", "/quality/aer", "bi-send-check", 10, ActivePrefix: "/quality/aer/board");
        if (access.RoleFor(app.Id) >= AppRole.Manager)
            yield return new NavItem("Regulatory reporting", "Adverse event setup", "/quality/aer/setup", "bi-sliders", 20);
    }
}

public sealed record EmdrWidgetItem(Guid Id, string Number, string Title, string State, string Label, DateOnly? Due);

/// <summary>Home card for regulatory affairs: MedWatch reports that need someone now (rejected, overdue, awaiting upload, due soon).</summary>
public sealed class EmdrAttentionWidget(VellumDbContext db, CurrentUser me, UserDirectory dir, AccessService access, ConfigCache cfg) : IHomeWidget
{
    public string Key => "aer-emdr-attention";
    public int Order => 21;
    public string Column => "side";

    public async Task<HomeWidgetModel?> BuildAsync()
    {
        if (!me.IsAuthenticated || me.IsSystem || !dir.IsMember(me.Id, "regulatory")) return null;
        if (!cfg.Current.FormsByKey.TryGetValue(AerKeys.MedWatch, out var form)) return null;
        var open = await access.Readable().AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseType == PhaseType.Normal).ToListAsync();
        if (open.Count == 0) return null;
        var ids = open.Select(r => r.Id).ToList();
        var subs = await db.Set<EmdrSubmission>().AsNoTracking().Where(s => ids.Contains(s.MedWatchId) && !s.Superseded).ToListAsync();
        var due = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && v.FieldKey == "report_due" && v.Date != null)
            .ToDictionaryAsync(v => v.RecordId, v => DateOnly.FromDateTime(v.Date!.Value));
        var today = Engine.Records.BusinessCalendar.Today;
        var items = new List<(int Rank, EmdrWidgetItem Item)>();
        foreach (var r in open)
        {
            var s = subs.Where(x => x.MedWatchId == r.Id).OrderByDescending(x => x.Attempt).FirstOrDefault();
            var d = due.TryGetValue(r.Id, out var dd) ? dd : (DateOnly?)null;
            (int, string, string)? state = s switch
            {
                { Status: EmdrStatus.Rejected or EmdrStatus.Failed } => (0, "bad", "Rejected by FDA"),
                { Status: EmdrStatus.Sent, OverdueAlertedAt: not null } => (1, "bad", "ACK overdue"),
                { Status: EmdrStatus.AwaitingUpload } => (2, "warn", "Upload to ESG"),
                { Status: EmdrStatus.Accepted } => (4, "ok", "Accepted: complete"),
                null when d is { } x && x < today => (0, "bad", "Deadline missed"),
                null when d is { } x && x.DayNumber - today.DayNumber <= 5 => (3, "warn", $"Due {x:dd MMM}"),
                _ => null,
            };
            if (state is { } st) items.Add((st.Item1, new EmdrWidgetItem(r.Id, r.Number, r.Title, st.Item2, st.Item3, d)));
        }
        if (items.Count == 0) return null;
        return new HomeWidgetModel("~/Modules/AdverseEvents/Views/_AttentionWidget.cshtml",
            items.OrderBy(i => i.Rank).ThenBy(i => i.Item.Due).Select(i => i.Item).Take(6).ToList());
    }
}
