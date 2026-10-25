using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Records;

namespace Vellum.Modules.Supplier;

public sealed record ScorePoint(Guid Id, string Number, string Period, DateOnly Start, decimal? Score, Keyword? Grade, bool Published,
    decimal? Ppm, decimal? Otd, decimal? Responsiveness, decimal? Audit);

public sealed class SupplierPerformanceVm
{
    public required string SupplierName { get; init; }
    public List<ScorePoint> Points { get; init; } = [];
    public decimal? CurrentScore { get; init; }
    public Keyword? CurrentGrade { get; init; }
    public string? CurrentPeriod { get; init; }
    public Keyword? Status { get; init; }
    public Keyword? CertStatus { get; init; }
    public DateOnly? NextExpiry { get; init; }
    public DateOnly? RequalifyOn { get; init; }
    public bool CanCreateScorecard { get; init; }
    public Guid SupplierId { get; init; }
}

public sealed record LotRow(Guid Id, string Number, string? Supplier, string? Material, string? Lot, DateOnly? Received, decimal? Quantity,
    Keyword? Status, Keyword? Result, PhaseDefinition? Phase, PhaseType PhaseType, DateOnly? Due);

public sealed class ReceivingListVm
{
    public List<LotRow> Lots { get; init; } = [];
    public string Context { get; init; } = "supplier";
}

public sealed record StatusChange(DateTime At, string By, string? From, string? To, string? Reason, int? Version);

public sealed class StatusHistoryVm
{
    public List<StatusChange> Entries { get; init; } = [];
    public List<Signature> Signatures { get; init; } = [];
    public TimeZoneInfo Tz { get; init; } = TimeZoneInfo.Utc;
}

public sealed record MaterialSupplierRow(Guid Id, string Number, string Name, Keyword? Status, Keyword? Grade, string? SupplierPart,
    int Lots, SkipLotDecision? NextLot);

public sealed class MaterialSuppliersVm
{
    public List<MaterialSupplierRow> Suppliers { get; init; } = [];
    public SkipLotProfile Profile { get; init; } = SkipLotProfile.Default;
}

/// <summary>Builds the supplier-quality record panels. Every list starts from the security-trimmed record set (INV-081).</summary>
public static class SupplierPanels
{
    public static async Task<SupplierPerformanceVm> PerformanceAsync(RecordContext ctx)
    {
        var reader = ctx.Get<RecordReader>();
        var id = ctx.Record.Id;
        var ids = await reader.LinkingTo(ctx.Access.Readable(), "scorecard", "supplier", id).ToListAsync();
        var cards = await reader.LoadManyAsync(ids, rows: false);
        var points = cards.Where(c => c.Record.PhaseType != PhaseType.Voided && c.Date("period_start") != null)
            .OrderBy(c => c.Date("period_start"))
            .Select(c => new ScorePoint(c.Id, c.Record.Number, c.Text("period") ?? "", c.Date("period_start")!.Value, c.Number("overall_score"),
                c.Keyword("grade"), c.Record.PhaseType == PhaseType.Completed, c.Number("ppm"), c.Number("otd_pct"), c.Number("responsiveness"), c.Number("audit_score")))
            .ToList();
        var s = ctx.Snapshot;
        return new SupplierPerformanceVm
        {
            SupplierId = id,
            SupplierName = s.Text("name") ?? s.Record.Title,
            Points = points,
            CurrentScore = s.Number("current_score"),
            CurrentGrade = s.Keyword("current_grade"),
            CurrentPeriod = s.Text("current_period"),
            Status = s.Keyword("approval_status"),
            CertStatus = s.Keyword("cert_status"),
            NextExpiry = s.Date("next_cert_expiry"),
            RequalifyOn = s.Date("requalify_on"),
            CanCreateScorecard = ctx.Config.FormsByKey.TryGetValue("scorecard", out var sf) && ctx.Access.CanCreate(sf),
        };
    }

    public static async Task<ReceivingListVm> ReceivingAsync(RecordContext ctx, string linkField)
    {
        var reader = ctx.Get<RecordReader>();
        var ids = await reader.LinkingTo(ctx.Access.Readable(), "receiving", linkField, ctx.Record.Id).ToListAsync();
        var lots = await reader.LoadManyAsync(ids, rows: false);
        return new ReceivingListVm
        {
            Context = linkField,
            Lots = lots.OrderByDescending(l => l.Date("received_on")).ThenByDescending(l => l.Record.Sequence).Take(50)
                .Select(l => new LotRow(l.Id, l.Record.Number, l.Text("supplier_name"), l.Text("material_name"), l.Text("lot_number"),
                    l.Date("received_on"), l.Number("quantity"), l.Keyword("inspection_status"), l.Keyword("inspection_result"), l.Phase,
                    l.Record.PhaseType, l.Record.DueDate))
                .ToList(),
        };
    }

    public static async Task<StatusHistoryVm> StatusHistoryAsync(RecordContext ctx)
    {
        var id = ctx.Record.Id;
        var audit = await ctx.Db.Audit.AsNoTracking().Where(a => a.RecordId == id && a.FieldKey == "approval_status")
            .OrderByDescending(a => a.Seq).ToListAsync();
        var entries = audit.Select(a => new StatusChange(a.At, a.UserName, string.IsNullOrEmpty(a.OldValue) ? null : a.OldValue, a.NewValue,
            a.Action == "Field set" ? "Supplier profile created" : a.Reason, a.RecordVersion)).ToList();
        var sigs = await ctx.Db.Signatures.AsNoTracking().Where(x => x.RecordId == id).OrderByDescending(x => x.SignedAt).ToListAsync();
        return new StatusHistoryVm { Entries = entries, Signatures = sigs, Tz = ctx.Records.TimeZone };
    }

    public static async Task<MaterialSuppliersVm> MaterialSuppliersAsync(RecordContext ctx)
    {
        var reader = ctx.Get<RecordReader>();
        var id = ctx.Record.Id;
        var supplierIds = await reader.LinkingTo(ctx.Access.Readable(), "supplier", "material", id, inRows: true).ToListAsync();
        var suppliers = await reader.LoadManyAsync(supplierIds);
        var material = await reader.LoadAsync(id, rows: false);
        var rows = new List<MaterialSupplierRow>();
        foreach (var sup in suppliers.OrderBy(x => x.Text("name")))
        {
            var lots = await ReceivingRules.HistoryAsync(ctx.Services, reader, sup.Id, id, null, null);
            var decision = material == null ? null : ReceivingRules.Decide(material, sup, lots, coaReceived: true);
            var row = sup.Rows("supplied_materials").FirstOrDefault(r => r.Values.Link("material") == id);
            rows.Add(new MaterialSupplierRow(sup.Id, sup.Record.Number, sup.Text("name") ?? sup.Record.Title, sup.Keyword("approval_status"),
                sup.Keyword("current_grade"), row?.Values.Text("supplier_part"), lots.Count, decision));
        }
        return new MaterialSuppliersVm { Suppliers = rows, Profile = material == null ? SkipLotProfile.Default : ReceivingRules.ProfileOf(material) };
    }
}
