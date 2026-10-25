using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure.Jobs;

namespace Vellum.Modules.Supplier;

/// <summary>
/// SUP-003: re-evaluates every active supplier's certificates once a day. Supplier quality (group <c>supplier-quality</c>)
/// and the supplier's engineer are notified once per state change: when a certificate enters the 60-day window and
/// again when it expires. The flag on the supplier is updated through the record service, so it's audited.
/// </summary>
public sealed class SupplierCertificateJob(VellumDbContext db, RecordService records, ConfigCache cfg, NotificationService notify, UserDirectory dir)
    : IScheduledJob
{
    public string Key => "supplier-certificates";
    public string Name => "Supplier certificate expiry";
    public string Description => "Flags expiring and expired supplier certificates and notifies supplier quality 60 days ahead (SUP-003).";
    public int DefaultIntervalMinutes => 1440;

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        if (!cfg.Current.FormsByKey.TryGetValue("supplier", out var form)) return new JobResult(0, "Supplier Quality isn't installed.");
        var ids = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id && !r.IsArchived).Select(r => r.Id).ToListAsync(ct);
        int updated = 0, notified = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            var snap = await records.LoadRequiredAsync(id);
            if (snap.KeywordKey("approval_status") is "inactive" or "disqualified") continue;
            var before = snap.KeywordKey("cert_status");
            var save = await records.SaveAsync(snap, new RecordData(), new SaveOptions { System = true, Reason = "Daily certificate expiry check (SUP-003)" });
            if (!save.Ok) { ctx.Log($"{snap.Record.Number}: {string.Join("; ", save.Errors.Select(e => e.Message))}"); continue; }
            if (save.Changed) updated++;

            db.ChangeTracker.Clear();
            snap = await records.LoadRequiredAsync(id);
            var state = snap.KeywordKey("cert_status");
            var lastNotified = snap.KeywordKey("cert_notified_state");
            if (state is "expiring" or "expired" && state != lastNotified)
            {
                var (title, body) = Message(snap, state);
                var people = dir.ActiveMembers("supplier-quality").Select(u => u.Id).ToHashSet();
                if (snap.UserId("owner") is { } owner) people.Add(owner);
                foreach (var uid in people)
                    notify.Notify(uid, state == "expired" ? NotificationKind.Escalation : NotificationKind.Reminder, title, body, id);
                await records.UpdateAsync(id, new() { ["cert_notified_state"] = state, ["cert_notified_on"] = BusinessCalendar.Today },
                    $"Certificate reminder sent ({state})");
                notified++;
                ctx.Log($"{snap.Record.Number} {snap.Record.Title}: {title}");
            }
            else if (state is "valid" or "none" && lastNotified != null)
                await records.UpdateAsync(id, new() { ["cert_notified_state"] = null, ["cert_notified_on"] = null }, "Certificates renewed");
            if (before != state) ctx.Log($"{snap.Record.Number}: certificate status {before ?? "none"} → {state}");
        }
        await db.SaveChangesAsync(ct);
        return new JobResult(updated + notified, $"Checked {ids.Count} supplier(s): {updated} updated, {notified} notification(s) sent.");
    }

    private (string Title, string Body) Message(RecordSnapshot s, string state)
    {
        var today = BusinessCalendar.Today;
        var tz = TimeZoneInfo.Utc;
        var std = s.Form.Fields.First(f => f.Key == "standard");
        var rows = s.Rows("certifications")
            .Where(r => s.KeywordKey(r, "cert_state") == state)
            .Select(r => (Std: FieldCodec.Display(std, r.Values["standard"], s.Config, dir, tz), Exp: r.Values.Date("expires_on")))
            .OrderBy(x => x.Exp).ToList();
        var name = s.Text("name") ?? s.Record.Title;
        if (state == "expired")
            return ($"{name}: certificate expired", string.Join(" ", rows.Select(r => $"{r.Std} expired on {r.Exp:dd MMM yyyy}.")) +
                " The supplier is flagged on the approved supplier list until a renewed certificate is recorded.");
        return ($"{name}: certificate expires in {(rows.FirstOrDefault().Exp is { } e ? e.DayNumber - today.DayNumber : 0)} days",
            string.Join(" ", rows.Select(r => $"{r.Std} expires on {r.Exp:dd MMM yyyy}.")) + " Request the renewed certificate from the supplier.");
    }
}
