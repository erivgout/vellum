using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.AdverseEvents;

namespace Vellum.Areas.Quality.Pages.Aer;

public sealed record BoardRow(Guid Id, string Number, string Title, string? ReportNumber, Keyword? ReportType, string? Phase, PhaseType PhaseType,
    DateOnly? Due, int? FollowUp, EmdrSubmission? Submission, string? Owner, string Attention);

/// <summary>AER-008/010: every MedWatch with its deadline, submission state and acknowledgements. Security-trimmed (INV-081).</summary>
[Authorize]
public class IndexModel(AccessService access, ConfigCache cfg, VellumDbContext db, UserDirectory dir, EmdrProfileService profiles, EmdrGateways gateways,
    AerSetup setup) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Show { get; set; } = "open";

    public List<BoardRow> Rows { get; private set; } = [];
    public int OpenCount { get; private set; }
    public int AwaitingAcks { get; private set; }
    public int NeedsAction { get; private set; }
    public int DueSoon { get; private set; }
    public int CompletedThisYear { get; private set; }
    public EmdrProfile? Profile { get; private set; }
    public IEmdrGateway? Gateway { get; private set; }
    public string? GatewayProblem { get; private set; }
    public bool ForeignProfile { get; private set; }
    public bool CanManage { get; private set; }
    public bool HasAppProfile { get; private set; }
    public EnvironmentType Environment { get; private set; }
    public DateOnly Today { get; } = BusinessCalendar.Today;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!cfg.Current.AppsByKey.TryGetValue(AerKeys.App, out var app) || !access.CanOpenApp(app.Id)) return NotFound();
        var form = cfg.Current.Form(AerKeys.MedWatch);
        CanManage = access.RoleFor(app.Id) >= AppRole.Manager;
        HasAppProfile = await setup.ProfileAsync() != null;
        var settings = await profiles.SettingsAsync();
        Environment = settings.EnvironmentType;
        Profile = await profiles.CurrentAsync();
        ForeignProfile = Profile == null && (await profiles.ForeignAsync()).Count > 0;
        Gateway = gateways.Get(Profile?.Gateway);
        GatewayProblem = Profile == null ? null : Gateway?.UnavailableReason(Profile, settings.EnvironmentType);

        var recs = await access.Readable().AsNoTracking().Where(r => r.FormId == form.Id).OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync();
        var ids = recs.Select(r => r.Id).ToList();
        var subs = await db.Set<EmdrSubmission>().AsNoTracking().Where(s => ids.Contains(s.MedWatchId) && !s.Superseded).ToListAsync();
        string[] keys = ["report_number", "report_type", "report_due", "followup_number", "owner"];
        var vals = await db.RecordValues.AsNoTracking().Where(v => ids.Contains(v.RecordId) && v.RowId == null && keys.Contains(v.FieldKey)).ToListAsync();
        RecordValue? V(Guid id, string k) => vals.FirstOrDefault(v => v.RecordId == id && v.FieldKey == k);

        var all = recs.Select(r =>
        {
            var sub = subs.Where(s => s.MedWatchId == r.Id).MaxBy(s => s.Attempt);
            var due = V(r.Id, "report_due")?.Date is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
            var attention = r.PhaseType != PhaseType.Normal ? "" : sub switch
            {
                { Status: EmdrStatus.Rejected or EmdrStatus.Failed } => "rejected",
                { Status: EmdrStatus.Sent, OverdueAlertedAt: not null } => "overdue",
                { Status: EmdrStatus.AwaitingUpload } => "upload",
                { Status: EmdrStatus.Accepted } => "complete",
                null when due is { } x && x < Today => "missed",
                null when due is { } x && x.DayNumber - Today.DayNumber <= 5 => "soon",
                _ => "",
            };
            return new BoardRow(r.Id, r.Number, r.Title, V(r.Id, "report_number")?.Text, cfg.Current.Keyword(V(r.Id, "report_type")?.RefId),
                cfg.Current.Phase(r.PhaseId)?.Name, r.PhaseType, due, V(r.Id, "followup_number")?.Number is { } f ? (int)f : null, sub,
                dir.Name(V(r.Id, "owner")?.RefId), attention);
        }).ToList();

        var open = all.Where(r => r.PhaseType == PhaseType.Normal).ToList();
        OpenCount = open.Count;
        AwaitingAcks = open.Count(r => r.Submission?.Status == EmdrStatus.Sent);
        NeedsAction = open.Count(r => r.Attention is "rejected" or "overdue" or "upload" or "missed");
        DueSoon = open.Count(r => r.Submission?.Status != EmdrStatus.Accepted && r.Due is { } d && d.DayNumber - Today.DayNumber <= 5);
        CompletedThisYear = all.Count(r => r.PhaseType == PhaseType.Completed && r.Submission?.Ack3At?.Year == Today.Year);
        if (Show is not ("open" or "attention" or "all")) Show = "open";
        Rows = (Show switch
        {
            "attention" => open.Where(r => r.Attention is "rejected" or "overdue" or "upload" or "missed" or "soon" or "complete"),
            "all" => all,
            _ => open,
        }).OrderBy(r => r.PhaseType != PhaseType.Normal).ThenBy(r => r.Attention switch { "rejected" or "missed" => 0, "overdue" => 1, "upload" => 2, "soon" => 3, "complete" => 4, _ => 5 })
            .ThenBy(r => r.Due ?? DateOnly.MaxValue).ToList();
        return Page();
    }
}
