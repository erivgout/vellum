using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;
using Vellum.Modules.Kit;

namespace Vellum.Modules.Ehs;

/// <summary>JSA risk scoring: risk = severity × likelihood (1–25), banded Low/Medium/High/Extreme (EHS-301, EHS-304).</summary>
public static class JsaRisk
{
    public static int? Level(string? keywordKey) =>
        keywordKey is { Length: 4 } k && int.TryParse(k[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 5 ? n : null;

    public static int? Score(string? severityKey, string? likelihoodKey) =>
        Level(severityKey) is { } s && Level(likelihoodKey) is { } l ? s * l : null;

    public static string Band(int score) => score switch
    {
        <= 4 => "low",
        <= 9 => "medium",
        <= 14 => "high",
        _ => "extreme",
    };

    /// <summary>EHS-304: residual risk in this band blocks approval.</summary>
    public const string UnacceptableBand = "extreme";

    /// <summary>INV-078: residual ≤ initial, unless a justification is stored.</summary>
    public static bool ResidualAcceptable(int initial, int residual, string? justification) =>
        residual <= initial || !string.IsNullOrWhiteSpace(justification);
}

public sealed class JsaBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["jsa"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        foreach (var row in ctx.Proposed.RowsOf("steps"))
        {
            var initial = JsaRisk.Score(ctx.KeywordKey(row, "sev_i"), ctx.KeywordKey(row, "lik_i"));
            var residual = JsaRisk.Score(ctx.KeywordKey(row, "sev_r"), ctx.KeywordKey(row, "lik_r"));
            ctx.SetRow(row, "risk_i", initial);
            ctx.SetRow(row, "risk_r", residual);
            ctx.SetRow(row, "band_r", residual is { } r ? JsaRisk.Band(r) : null);
            // INV-078 (Domain): a step whose residual risk exceeds its initial risk needs a stored justification.
            if (initial is { } i && residual is { } res && !JsaRisk.ResidualAcceptable(i, res, row.Values.Text("justification")))
                ctx.Error("justification", $"Step {row.Order + 1}: the residual risk ({res}) is higher than the initial risk ({i}). Add a justification or strengthen the controls (INV-078).", row.IsNew ? null : row.Id);
        }
        if (ctx.Proposed.Main.Ref("owner") is { } owner && owner == ctx.Proposed.Main.Ref("reviewer"))
            ctx.Error("reviewer", "The reviewer must be someone other than the JSA owner (SAF-044).");
        return Task.CompletedTask;
    }

    public override Task ValidateRouteAsync(RoutingContext ctx)
    {
        if (ctx.Kind is not (RouteKind.Forward or RouteKind.SpecialForward)) return Task.CompletedTask;
        var steps = ctx.Snapshot.Rows("steps");
        if (steps.Count == 0) ctx.Error("Add at least one job step.");
        foreach (var row in steps)
        {
            var initial = JsaRisk.Score(ctx.Snapshot.KeywordKey(row, "sev_i"), ctx.Snapshot.KeywordKey(row, "lik_i"));
            var residual = JsaRisk.Score(ctx.Snapshot.KeywordKey(row, "sev_r"), ctx.Snapshot.KeywordKey(row, "lik_r"));
            var name = row.Values.Text("step") ?? $"Step {row.Order + 1}";
            if (initial == null || residual == null) { ctx.Error($"\"{name}\": rate both the initial and the residual risk."); continue; }
            if (!JsaRisk.ResidualAcceptable(initial.Value, residual.Value, row.Values.Text("justification")))
                ctx.Error($"\"{name}\": residual risk {residual} is higher than initial risk {initial} without a justification (INV-078).");
            if (ctx.To.Key == "approved" && JsaRisk.Band(residual.Value) == JsaRisk.UnacceptableBand)
                ctx.Error($"\"{name}\": the residual risk ({residual}) is still Extreme. Add controls before this JSA can be approved (EHS-304).");
        }
        return Task.CompletedTask;
    }
}

/// <summary>Hours worked: one record per site and month, normalized to the first of the month (EHS-113).</summary>
public sealed class HoursWorkedBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["hours_worked"];

    public override async Task OnSavingAsync(SavingContext ctx)
    {
        if (ctx.Proposed.Main.Date("period") is not { } p) return;
        var first = new DateOnly(p.Year, p.Month, 1);
        ctx.Set("period", first);
        ctx.Set("period_label", first.ToString("MMM yyyy", CultureInfo.InvariantCulture));
        if (ctx.Proposed.Main.Ref("site") is not { } siteId) return;

        var db = ctx.Services.GetRequiredService<Data.VellumDbContext>();
        var formId = ctx.Form.Id;
        var self = ctx.Current?.Record.Id ?? Guid.Empty;
        var date = first.ToDateTime(TimeOnly.MinValue);
        var dup = await db.Records.AsNoTracking()
            .Where(r => r.FormId == formId && r.Id != self && r.PhaseType != PhaseType.Voided)
            .Where(r => db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "site" && v.RefId == siteId))
            .Where(r => db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "period" && v.Date == date))
            .Select(r => r.Number).FirstOrDefaultAsync();
        if (dup != null) ctx.Error("period", $"Hours for this site and month are already recorded in {dup}. Correct that record instead.");
    }
}

/// <summary>Work-permit validity window (EHS-406): expiring soon and expired, computed from the end time.</summary>
public static class PermitValidity
{
    /// <summary>Warn when 20 % of the window is left, but never less than 2 hours or more than 24 hours ahead.</summary>
    public static TimeSpan WarningLead(DateTime fromUtc, DateTime toUtc)
    {
        var window = toUtc - fromUtc;
        var lead = TimeSpan.FromTicks(window.Ticks / 5);
        if (lead < TimeSpan.FromHours(2)) lead = TimeSpan.FromHours(2);
        if (lead > TimeSpan.FromHours(24)) lead = TimeSpan.FromHours(24);
        return lead;
    }

    public static string Status(DateTime fromUtc, DateTime toUtc, DateTime nowUtc) =>
        nowUtc >= toUtc ? "expired" : nowUtc < fromUtc ? "pending" : toUtc - nowUtc <= WarningLead(fromUtc, toUtc) ? "expiring" : "valid";

    public static TimeSpan MaxWindow(string? permitType) =>
        permitType is "hot_work" or "confined_space" ? TimeSpan.FromHours(12) : TimeSpan.FromDays(7);

    /// <summary>Oxygen 19.5–23.5 %, flammables &lt; 10 % LEL, H₂S &lt; 10 ppm, CO &lt; 35 ppm.</summary>
    public static bool? GasOk(decimal? o2, decimal? lel, decimal? h2s, decimal? co)
    {
        if (o2 == null && lel == null && h2s == null && co == null) return null;
        if (o2 == null || lel == null) return false;
        return o2 >= 19.5m && o2 <= 23.5m && lel < 10m && (h2s ?? 0) < 10m && (co ?? 0) < 35m;
    }
}

public sealed class PermitBehavior : RecordBehavior
{
    public override IReadOnlyCollection<string> Forms => ["permit"];

    public override Task OnSavingAsync(SavingContext ctx)
    {
        var m = ctx.Proposed.Main;
        var from = m.DateTime("valid_from") is { } f ? ModuleKit.Utc(f) : (DateTime?)null;
        var to = m.DateTime("valid_to") is { } t ? ModuleKit.Utc(t) : (DateTime?)null;
        var type = ctx.KeywordKey("permit_type");
        if (from is { } a && to is { } b)
        {
            if (b <= a) ctx.Error("valid_to", "The permit must end after it starts.");
            else if (b - a > PermitValidity.MaxWindow(type))
                ctx.Error("valid_to", type is "hot_work" or "confined_space"
                    ? "Hot work and confined space permits are valid for one shift at most (12 hours). Issue a new permit for the next shift."
                    : "A work permit can be valid for 7 days at most.");
            var phase = ctx.Current?.PhaseKey;
            var status = phase switch
            {
                "closed" => "closed",
                "expired" => "expired",
                "active" => PermitValidity.Status(a, b, DateTime.UtcNow),
                _ => DateTime.UtcNow >= b ? "expired" : "pending",
            };
            if (ctx.Current?.Phase?.Type != PhaseType.Voided) ctx.Set("validity_status", status);
        }
        if (m.Ref("issuer") is { } issuer && issuer == m.Ref("receiver"))
            ctx.Error("issuer", "The issuer can't also be the permit receiver (SAF-044).");

        var gas = PermitValidity.GasOk(m.Number("o2_pct"), m.Number("lel_pct"), m.Number("h2s_ppm"), m.Number("co_ppm"));
        ctx.Set("gas_result", gas is { } ok ? ok ? "pass" : "fail" : null);
        return Task.CompletedTask;
    }

    public override async Task ValidateRouteAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        if (ctx.Kind is not (RouteKind.Forward or RouteKind.SpecialForward)) return;
        var to = s.DateTime("valid_to") is { } t ? ModuleKit.Utc(t) : (DateTime?)null;
        if (ctx.To.Key is "authorization" or "active" && to <= DateTime.UtcNow)
            ctx.Error("The validity window has already ended. Change the dates or raise a new permit.");

        if (ctx.To.Key == "active")
        {
            var rows = s.Rows("conditions");
            if (rows.Count == 0) ctx.Error("List at least one precaution before authorizing the permit.");
            var open = rows.Where(r => r.Values.Bool("confirmed") != true).Select(r => r.Values.Text("condition")).ToList();
            if (open.Count > 0) ctx.Error($"Confirm every precaution is in place before authorizing: {string.Join("; ", open)}.");
            if (s.KeywordKey("permit_type") == "confined_space")
            {
                if (s.KeywordKey("gas_result") != "pass") ctx.Error("Confined space entry needs a gas test within limits before authorization.");
                if (s.DateTime("tested_at") is { } tested && DateTime.UtcNow - ModuleKit.Utc(tested) > TimeSpan.FromHours(2))
                    ctx.Error("The gas test is more than 2 hours old. Re-test the atmosphere before authorizing entry.");
            }
            if (s.Links("jsa").FirstOrDefault() is { } jsaId)
            {
                var jsa = await ctx.Db.Records.AsNoTracking().Include(r => r.Phase).FirstOrDefaultAsync(r => r.Id == jsaId);
                if (jsa?.Phase?.Key != "approved") ctx.Error($"The linked JSA {jsa?.Number} isn't approved yet.");
            }
        }
        if (ctx.To.Key == "closed" && (s.Bool("work_completed") != true || s.Bool("area_left_safe") != true))
            ctx.Error("Confirm the work is complete and the area is left safe before closing out.");
    }

    public override async Task OnRoutedAsync(RoutingContext ctx)
    {
        var s = ctx.Snapshot;
        var from = s.DateTime("valid_from");
        var to = s.DateTime("valid_to");
        string? status = ctx.To.Key switch
        {
            "active" when from != null && to != null => PermitValidity.Status(ModuleKit.Utc(from.Value), ModuleKit.Utc(to.Value), DateTime.UtcNow),
            "closed" => "closed",
            "expired" => "expired",
            "request" or "authorization" => "pending",
            _ => null,
        };
        if (status != null && status != s.KeywordKey("validity_status"))
            await ctx.Records.UpdateAsync(s.Record.Id, new() { ["validity_status"] = status });
    }
}
