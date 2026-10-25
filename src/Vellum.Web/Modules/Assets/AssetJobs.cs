using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Jobs;
using Vellum.Modules.Kit;

namespace Vellum.Modules.Assets;

/// <summary>
/// AST-029 / SAF-030: moves assets to "Calibration due" and "Overdue" as their dates pass, reminds the owner and technician,
/// and escalates overdue assets to the maintenance group weekly. State-based, so a run after downtime catches up.
/// </summary>
public sealed class CalibrationDueJob(VellumDbContext db, RecordService records, NotificationService notify, UserDirectory dir, ConfigCache cfg) : IScheduledJob
{
    public const string DuePrefix = "Calibration due:";
    public const string OverduePrefix = "Calibration overdue:";

    public string Key => "calibration-due";
    public string Name => "Calibration due and overdue";
    public string Description => "Marks assets due or overdue for calibration and notifies their owners, technicians and the maintenance group.";
    public int DefaultIntervalMinutes => 60;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var n = await RunAsync(BusinessCalendar.Today, ct);
        return new JobResult(n, $"{n} asset(s) changed status or were notified.");
    }

    public async Task<int> RunAsync(DateOnly today, CancellationToken ct = default)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("asset", out var form)) return 0;
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id).Select(r => r.Id).ToListAsync(ct);
        var all = await ModuleKit.MainValuesAsync(db, ids, "status", "calibration_required", "next_cal_date", "cal_interval_days", "last_cal_date",
            "owner", "technician", "asset_id", "description");
        var touched = 0;
        foreach (var (id, v) in all)
        {
            var status = cfg.Current.Keyword(v.Ref("status"))?.Key;
            if (status != null && AssetStatus.Sticky.Contains(status)) continue;
            var calRequired = v.Bool("calibration_required") == true;
            var next = v.Date("next_cal_date");
            var computed = AssetStatus.Compute(status, calRequired, next, (int)(v.Number("cal_interval_days") ?? 0), today);
            var changed = computed != status;
            if (changed)
            {
                db.ChangeTracker.Clear();
                await records.UpdateAsync(id, new() { ["status"] = computed }, next is { } nd ? $"Calibration due {nd:dd MMM yyyy}" : "No calibration recorded");
            }

            var people = new[] { v.Ref("owner"), v.Ref("technician") }.OfType<int>().Distinct().ToList();
            var label = $"{v.Text("asset_id")} · {v.Text("description")}";
            var notified = false;
            if (computed == "due")
            {
                var since = (v.Date("last_cal_date") ?? DateOnly.MinValue).ToDateTime(TimeOnly.MinValue);
                if (!await db.Notifications.AnyAsync(x => x.RecordId == id && x.Title.StartsWith(DuePrefix) && x.CreatedAt >= since, ct))
                {
                    foreach (var uid in people)
                        notify.Notify(uid, NotificationKind.Reminder, $"{DuePrefix} {v.Text("asset_id")}",
                            $"{label} is due for calibration{(next is { } d ? $" on {d:dd MMM yyyy}" : "")}. Record it from the asset profile.", id);
                    notified = true;
                }
            }
            else if (computed == "overdue")
            {
                var weekAgo = DateTime.UtcNow.AddDays(-7);
                if (!await db.Notifications.AnyAsync(x => x.RecordId == id && x.Title.StartsWith(OverduePrefix) && x.CreatedAt >= weekAgo, ct))
                {
                    var title = $"{OverduePrefix} {v.Text("asset_id")}";
                    var body = $"{label} was due for calibration on {next:dd MMM yyyy}. Don't use it to measure until it's calibrated (SAF-040).";
                    var recipients = people.Concat(dir.ActiveMembers("maintenance").Select(u => u.Id)).Distinct().ToList();
                    foreach (var uid in recipients) notify.Notify(uid, NotificationKind.Escalation, title, body, id);
                    notified = true;
                }
            }
            if (changed || notified)
            {
                touched++;
                await db.SaveChangesAsync(ct);
            }
        }
        return touched;
    }
}

public sealed record CalDueItem(Guid Id, string Title, DateOnly? Next, Keyword? Status);
public sealed record CalDueVm(int Overdue, int Due, int Quarantined, List<CalDueItem> Items);

/// <summary>Home widget "Calibration due" for the maintenance group and application managers (AST-050).</summary>
public sealed class CalibrationDueWidget(VellumDbContext db, AccessService access, ConfigCache cfg, CurrentUser me, UserDirectory dir) : IHomeWidget
{
    public string Key => "calibration-due";
    public int Order => 40;
    public string Column => "side";

    public async Task<HomeWidgetModel?> BuildAsync()
    {
        if (!cfg.Current.AppsByKey.TryGetValue("assets", out var app) || !cfg.Current.FormsByKey.TryGetValue("asset", out var form)) return null;
        if (!dir.IsMember(me.Id, "maintenance") && access.RoleFor(app.Id) < AppRole.Manager) return null;
        var statuses = new[] { "overdue", "due", "quarantined" }.Select(k => (int?)(cfg.Current.Keyword("asset_status", k)?.Id ?? -1)).ToList();
        var recs = await access.Readable().AsNoTracking()
            .Where(r => r.FormId == form.Id && db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "status" && statuses.Contains(v.RefId)))
            .Select(r => new { r.Id, r.Title }).ToListAsync();
        var values = await ModuleKit.MainValuesAsync(db, recs.Select(r => r.Id).ToList(), "status", "next_cal_date");
        var items = recs.Select(r => new CalDueItem(r.Id, r.Title, values[r.Id].Date("next_cal_date"), cfg.Current.Keyword(values[r.Id].Ref("status")))).ToList();
        var vm = new CalDueVm(items.Count(i => i.Status?.Key == "overdue"), items.Count(i => i.Status?.Key == "due"), items.Count(i => i.Status?.Key == "quarantined"),
            items.OrderBy(i => i.Status?.Key switch { "overdue" => 0, "quarantined" => 1, _ => 2 }).ThenBy(i => i.Next).Take(6).ToList());
        return new HomeWidgetModel("~/Modules/Assets/Views/_CalibrationDueWidget.cshtml", vm);
    }
}
