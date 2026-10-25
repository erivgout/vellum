using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.AdverseEvents;

/// <summary>AER-001 (2): the singleton application profile.</summary>
public sealed record AerProfileInfo(Guid Id, string Number, bool MappingEnabled, int ReceiptDelayMinutes, string SignSubmit, string SignComplete,
    string SignVoid, IReadOnlyList<int> DefaultApprovers, IReadOnlySet<string> MappedGroups, DateTime ModifiedAt)
{
    public const int DefaultReceiptDelayMinutes = 2880;
    public const string DefaultSignSubmit = "Submitted to FDA";
    public const string DefaultSignComplete = "Final acknowledgement confirmed";
    public const string DefaultSignVoid = "Voided";
    public static readonly string[] AllGroups = ["event", "patient", "device", "reporter", "evaluation"];
    public bool Maps(string group) => MappedGroups.Contains(group);
}

/// <summary>AER-001 (3): report-number settings for one location.</summary>
public sealed record AerMappingInfo(Guid Id, string Number, string Location, string ReporterType, string CodeMode, string? MfrCode,
    string MfrSequenceFormat, int MfrInitial, int? MfrInitialYear, string? ImporterCode, string ImporterSequenceFormat, int ImporterInitial,
    int? ImporterInitialYear, IReadOnlyList<(string ProductCode, string Code)> Rules, string? MfrName, string? MfrAddress,
    string? ContactName, string? ContactAddress, string? ContactPhone)
{
    /// <summary>Basic: the static code. Advanced: the first rule whose product code matches, else the static code.</summary>
    public string? CodeFor(string reporterType, string? productCode)
    {
        if (reporterType == AerKeys.Importer) return ImporterCode;
        if (CodeMode == "advanced" && productCode != null)
            foreach (var (pc, code) in Rules)
                if (string.Equals(pc, productCode, StringComparison.OrdinalIgnoreCase)) return code;
        return MfrCode;
    }

    public bool Sequential(string reporterType) => (reporterType == AerKeys.Importer ? ImporterSequenceFormat : MfrSequenceFormat) == "sequential";

    /// <summary>The first sequence number of <paramref name="year"/>: the configured initial number in its year, 1 in later years.</summary>
    public int StartFor(string reporterType, int year)
    {
        var (initial, initialYear) = reporterType == AerKeys.Importer ? (ImporterInitial, ImporterInitialYear) : (MfrInitial, MfrInitialYear);
        return initialYear == null || initialYear == year ? Math.Max(1, initial) : 1;
    }
}

public sealed record AerProductCode(Guid Id, string Code, string DeviceName, string? DeviceClass, string? Regulation, string? Panel, IReadOnlyList<string> BrandNames);

/// <summary>
/// Lets this module's own system writes set fields that people may not (report-number parts assigned by the mapping).
/// </summary>
[ScopedService]
public sealed class AerWriteGuard
{
    public bool SystemWrite { get; private set; }

    public IDisposable Allow()
    {
        var before = SystemWrite;
        SystemWrite = true;
        return new Reset(() => SystemWrite = before);
    }

    private sealed class Reset(Action a) : IDisposable { public void Dispose() => a(); }
}

/// <summary>Reads (no tracking) the setup records of Adverse Event Reporting and maintains the application profile.</summary>
[ScopedService]
public sealed class AerSetup(VellumDbContext db, ConfigCache cfg, RecordService records, AccessService access, AuditService audit, CurrentUser me)
{
    private ConfigModel Cfg => cfg.Current;
    private AerProfileInfo? _profile;
    private bool _profileLoaded;

    public bool Installed => Cfg.FormsByKey.ContainsKey(AerKeys.MedWatch);

    public bool CanManage => Cfg.AppsByKey.TryGetValue(AerKeys.App, out var app) && access.RoleFor(app.Id) >= AppRole.Manager;

    public async Task<AerProfileInfo?> ProfileAsync()
    {
        if (_profileLoaded) return _profile;
        _profileLoaded = true;
        if (!Cfg.FormsByKey.TryGetValue(AerKeys.Profile, out var form)) return null;
        var row = (await AerData.LoadAsync(db, form, take: 1)).FirstOrDefault();
        if (row == null) return null;
        var m = row.Data.Main;
        var groups = m.Refs("mapped_groups").Select(id => Cfg.Keyword(id)?.Key).OfType<string>().ToHashSet();
        _profile = new AerProfileInfo(row.Record.Id, row.Record.Number, m.Bool("mapping_enabled") == true,
            (int)(m.Number("receipt_delay_minutes") ?? AerProfileInfo.DefaultReceiptDelayMinutes),
            m.Text("sign_submit") ?? AerProfileInfo.DefaultSignSubmit, m.Text("sign_complete") ?? AerProfileInfo.DefaultSignComplete,
            m.Text("sign_void") ?? AerProfileInfo.DefaultSignVoid, m.Refs("default_approvers"), groups, row.Record.ModifiedAt);
        return _profile;
    }

    public async Task<List<AerMappingInfo>> MappingsAsync()
    {
        if (!Cfg.FormsByKey.TryGetValue(AerKeys.Mapping, out var form)) return [];
        return (await AerData.LoadAsync(db, form)).Select(ToMapping).OrderBy(m => m.Location).ToList();
    }

    public async Task<AerMappingInfo?> MappingForAsync(string? location) =>
        location == null ? null : (await MappingsAsync()).FirstOrDefault(m => m.Location == location);

    private AerMappingInfo ToMapping(AerRow r)
    {
        var m = r.Data.Main;
        string? Kw(string k) => Cfg.Keyword(m.Ref(k))?.Key;
        int? Int(string k) => m.Number(k) is { } n ? (int)n : null;
        var rules = r.Data.RowsOf("map_code_rules")
            .Select(x => (x.Values.Text("rule_product_code")?.Trim().ToUpperInvariant() ?? "", x.Values.Text("rule_code")?.Trim() ?? ""))
            .Where(x => x.Item1.Length > 0 && x.Item2.Length > 0).ToList();
        return new AerMappingInfo(r.Record.Id, r.Record.Number, Kw("map_location") ?? "", Kw("map_reporter_type") ?? AerKeys.Manufacturer,
            Kw("map_mfr_code_mode") ?? "basic", m.Text("map_mfr_code")?.Trim(), Kw("map_mfr_seq_format") ?? "sequential", Int("map_mfr_seq_initial") ?? 1,
            Int("map_mfr_seq_initial_year"), m.Text("map_importer_code")?.Trim(), Kw("map_imp_seq_format") ?? "sequential", Int("map_imp_seq_initial") ?? 1,
            Int("map_imp_seq_initial_year"), rules, m.Text("map_mfr_name"), m.Text("map_mfr_address"), m.Text("map_contact_name"),
            m.Text("map_contact_address"), m.Text("map_contact_phone"));
    }

    public async Task<List<AerProductCode>> ProductCodesAsync()
    {
        if (!Cfg.FormsByKey.TryGetValue(AerKeys.ProductCode, out var form)) return [];
        return (await AerData.LoadAsync(db, form)).Select(r =>
        {
            var m = r.Data.Main;
            var brands = (m.Text("pc_brand_names") ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new AerProductCode(r.Record.Id, (m.Text("pc_code") ?? "").ToUpperInvariant(), m.Text("pc_device_name") ?? "", Cfg.Keyword(m.Ref("pc_class"))?.Label,
                m.Text("pc_regulation"), m.Text("pc_panel"), brands);
        }).OrderBy(p => p.Code).ToList();
    }

    /// <summary>The product code whose brand names (or device name) appear in the complaint's product text.</summary>
    public static AerProductCode? MatchDevice(IEnumerable<AerProductCode> codes, string? product)
    {
        if (string.IsNullOrWhiteSpace(product)) return null;
        var list = codes.ToList();
        return list.Where(c => c.BrandNames.Any(b => product.Contains(b, StringComparison.OrdinalIgnoreCase)))
                   .OrderByDescending(c => c.BrandNames.Max(b => product.Contains(b, StringComparison.OrdinalIgnoreCase) ? b.Length : 0)).FirstOrDefault()
               ?? list.FirstOrDefault(c => product.Contains(c.DeviceName, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record ProfileInput(bool MappingEnabled, int ReceiptDelayMinutes, string SignSubmit, string SignComplete, string SignVoid,
        IReadOnlyList<int> DefaultApprovers, IReadOnlyList<string> MappedGroups);

    /// <summary>
    /// Create or update the singleton profile (AER-001/002) and apply its e-signature meanings to the MedWatch routes. One transaction;
    /// the record change goes to the record audit trail and the route change to the configuration audit trail.
    /// </summary>
    public async Task<(bool Ok, IReadOnlyList<string> Errors)> SaveProfileAsync(ProfileInput input)
    {
        if (!CanManage) return (false, ["Only Adverse Event Reporting managers can change the application profile."]);
        var errors = new List<string>();
        if (input.ReceiptDelayMinutes is < 5 or > 20160) errors.Add("The submission receipt delay must be between 5 minutes and 14 days (20,160 minutes).");
        foreach (var (label, v) in new[] { ("Submit to FDA", input.SignSubmit), ("Complete", input.SignComplete), ("Void", input.SignVoid) })
            if (string.IsNullOrWhiteSpace(v) || v.Trim().Length > 80) errors.Add($"Enter an e-signature meaning for {label} (at most 80 characters).");
        if (errors.Count > 0) return (false, errors);

        var values = new Dictionary<string, object?>
        {
            ["mapping_enabled"] = input.MappingEnabled, ["receipt_delay_minutes"] = (decimal)input.ReceiptDelayMinutes,
            ["sign_submit"] = input.SignSubmit.Trim(), ["sign_complete"] = input.SignComplete.Trim(), ["sign_void"] = input.SignVoid.Trim(),
            ["default_approvers"] = input.DefaultApprovers.Distinct().ToList(),
            ["mapped_groups"] = input.MappedGroups.Where(g => AerProfileInfo.AllGroups.Contains(g)).Distinct().ToList(),
        };
        var existing = await ProfileAsync();
        try
        {
            return await Tx.RunAsync(db, async () =>
            {
                if (existing == null)
                {
                    var res = await records.CreateAsync(new CreateRequest { FormKey = AerKeys.Profile, Values = values, SkipAccessCheck = true, CreateComment = "Application profile set up (AER-001)" });
                    if (!res.Ok) return (false, (IReadOnlyList<string>)res.Errors.Select(e => e.Message).ToList());
                }
                else
                {
                    var res = await records.UpdateAsync(existing.Id, values, "Application profile changed on the setup page");
                    if (!res.Ok) return (false, (IReadOnlyList<string>)res.Errors.Select(e => e.Message).ToList());
                }
                await ApplySignatureMeaningsAsync(input.SignSubmit.Trim(), input.SignComplete.Trim(), input.SignVoid.Trim());
                await db.SaveChangesAsync();
                return (true, (IReadOnlyList<string>)[]);
            }, r => r.Item1);
        }
        finally
        {
            _profileLoaded = false;
            _profile = null;
        }
    }

    /// <summary>AER-001: the profile's meanings for Submit to FDA, Complete and Void become the routes' signature meanings.</summary>
    private async Task ApplySignatureMeaningsAsync(string submit, string complete, string voidMeaning)
    {
        var form = Cfg.Form(AerKeys.MedWatch);
        var wfIds = form.Workflows.Select(w => w.Id).ToList();
        var routes = await db.Routes.Include(r => r.FromPhase).Include(r => r.ToPhase).Where(r => wfIds.Contains(r.WorkflowId)).ToListAsync();
        var changed = new List<string>();
        foreach (var r in routes)
        {
            string? want = r.Kind switch
            {
                RouteKind.Void => voidMeaning,
                RouteKind.Forward when r.FromPhase.Key == "approval" && r.ToPhase.Key == "submitted" => submit,
                RouteKind.Forward when r.ToPhase.Key == "completed" => complete,
                _ => r.SignatureMeaning,
            };
            if (want == r.SignatureMeaning) continue;
            audit.Config("Route", $"{form.Key}:{r.FromPhase.Key}->{r.ToPhase.Key}", "Signature meaning changed (application profile)",
                JsonSerializer.Serialize(new { r.SignatureMeaning }), JsonSerializer.Serialize(new { SignatureMeaning = want }));
            changed.Add($"{r.FromPhase.Name} → {r.ToPhase.Name}: \"{want}\"");
            r.SignatureMeaning = want;
        }
        if (changed.Count == 0) return;
        var next = (await db.ConfigVersions.MaxAsync(v => (int?)v.Number) ?? 0) + 1;
        db.ConfigVersions.Add(new ConfigVersion
        {
            Number = next, PublishedAt = DateTime.UtcNow, PublishedById = me.IsAuthenticated ? me.Id : null,
            Note = "Adverse Event Reporting application profile: e-signature meanings (" + string.Join("; ", changed) + ")",
        });
        await db.SaveChangesAsync();
        cfg.Invalidate();
    }
}

/// <summary>AER-005 / INV-023: report numbers, and the yearly sequence they're drawn from.</summary>
[ScopedService]
public sealed class ReportNumberService(VellumDbContext db)
{
    public static string Format(string code, int year, int sequence, bool userFacility = false) =>
        $"{code}-{year.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString(userFacility ? "D4" : "D5", CultureInfo.InvariantCulture)}";

    public static string Scope(string reporterType, string code, int year) => $"aer:{reporterType}:{code}:{year.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The next number for a reporter type, code and year. Drawn inside the submit transaction (HOLDLOCK), so a rolled-back submission
    /// gives its number back and issued numbers stay an unbroken, increasing sequence. Several locations sharing a code share one sequence,
    /// which keeps numbers unique per code and year. Numbers already used by hand are skipped.
    /// </summary>
    public async Task<int> NextAsync(string reporterType, string code, int year, int start)
    {
        if (db.Database.CurrentTransaction == null) throw new InvalidOperationException("Report numbers are only drawn inside the submit transaction (INV-023).");
        var scope = Scope(reporterType, code, year);
        for (var guard = 0; guard < 1000; guard++)
        {
            var value = (await db.Database.SqlQueryRaw<long>("""
                MERGE [NumberSequences] WITH (HOLDLOCK) AS t
                USING (SELECT {0} AS [Scope]) AS s ON t.[Scope] = s.[Scope]
                WHEN MATCHED THEN UPDATE SET t.[Next] = t.[Next] + 1
                WHEN NOT MATCHED THEN INSERT ([Scope], [Next]) VALUES (s.[Scope], {1} + 1)
                OUTPUT inserted.[Next] - 1 AS [Value];
                """, scope, (long)start).ToListAsync()).Single();
            var seq = (int)value;
            if (seq > 99999) throw new InvalidOperationException($"The {year} sequence for code {code} is exhausted.");
            var taken = await db.Set<MedWatchReport>().AnyAsync(x => x.IsInitial && x.ReporterType == reporterType && x.ReportCode == code &&
                                                                     x.ReportYear == year && x.ReportSequence == seq);
            if (!taken) return seq;
        }
        throw new InvalidOperationException("Couldn't find a free report number.");
    }
}

public sealed record AerRow(Record Record, RecordData Data);

/// <summary>No-tracking reads of records with their values (setup tables are small).</summary>
public static class AerData
{
    public static async Task<List<AerRow>> LoadAsync(VellumDbContext db, FormDefinition form, int? take = null, IReadOnlyCollection<Guid>? ids = null)
    {
        var q = db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseType != PhaseType.Voided && !r.IsArchived);
        if (ids != null) q = q.Where(r => ids.Contains(r.Id));
        q = q.OrderBy(r => r.CreatedAt);
        var recs = take is { } t ? await q.Take(t).ToListAsync() : await q.ToListAsync();
        if (recs.Count == 0) return [];
        var recIds = recs.Select(r => r.Id).ToList();
        var values = await db.RecordValues.AsNoTracking().Where(v => recIds.Contains(v.RecordId)).ToListAsync();
        var rows = await db.SubformRows.AsNoTracking().Where(r => recIds.Contains(r.RecordId)).OrderBy(r => r.Order).ToListAsync();
        return recs.Select(r =>
        {
            var data = new RecordData();
            var mine = values.Where(v => v.RecordId == r.Id).ToList();
            foreach (var g in mine.Where(v => v.RowId == null).GroupBy(v => v.FieldKey))
                data.Main.Set(g.Key, g.OrderBy(v => v.Index).Select(v => new Val(v.Text, v.Number, v.Date, v.RefId, v.RefGuid)));
            foreach (var row in rows.Where(x => x.RecordId == r.Id))
            {
                var bag = new RowBag { Id = row.Id, Order = row.Order };
                foreach (var g in mine.Where(v => v.RowId == row.Id).GroupBy(v => v.FieldKey))
                    bag.Values.Set(g.Key, g.OrderBy(v => v.Index).Select(v => new Val(v.Text, v.Number, v.Date, v.RefId, v.RefGuid)));
                data.RowsOf(row.SectionKey).Add(bag);
            }
            return new AerRow(r, data);
        }).ToList();
    }
}
