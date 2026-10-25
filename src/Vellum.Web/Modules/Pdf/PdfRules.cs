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

namespace Vellum.Modules.Pdf;

/// <summary>
/// Packaged default rendition rules for a form. Implement this in an application module (no registration needed): the
/// defaults are installed the first time the form's rules are read, and administrators can change them afterwards.
/// </summary>
public interface IPdfDefaults
{
    string FormKey { get; }
    void Define(PdfRulesBuilder rules);
}

/// <summary>Fluent definition of default rules (see <see cref="IPdfDefaults"/>).</summary>
public sealed class PdfRulesBuilder(PdfProfile profile)
{
    internal PdfProfile Profile { get; } = profile;

    public PdfRulesBuilder Style(string color, int opacity) { Profile.Color = color; Profile.Opacity = opacity; return this; }
    public PdfRulesBuilder AutoMark(bool on = true) { Profile.AutoMark = on; return this; }
    public PdfRulesBuilder Manifest(bool on = true, string? note = null) { Profile.Manifest = on; Profile.ManifestNote = note; return this; }

    public PdfRulesBuilder Phase(string phaseKey, string? watermark = null, WatermarkPosition position = WatermarkPosition.Diagonal,
        AppRole download = AppRole.Reader, AppRole print = AppRole.Reader)
    {
        Profile.Phases.RemoveAll(p => p.PhaseKey == phaseKey);
        Profile.Phases.Add(new PdfPhaseSetting { PhaseKey = phaseKey, Watermark = watermark, Position = position, DownloadRole = download, PrintRole = print });
        return this;
    }

    public PdfRulesBuilder Field(string fieldKey, string phaseKey, bool convert = false, bool require = false, bool pdfOnly = false)
    {
        Profile.Fields.RemoveAll(f => f.FieldKey == fieldKey && f.PhaseKey == phaseKey);
        Profile.Fields.Add(new PdfFieldSetting { FieldKey = fieldKey, PhaseKey = phaseKey, Convert = convert, Require = require, PdfOnly = pdfOnly });
        return this;
    }
}

/// <summary>Read-only view of one form's rendition rules (PLT-804..807, DOC-021..023, DOC-081).</summary>
public sealed class PdfFormRules
{
    public required PdfProfile Profile { get; init; }
    public string FormKey => Profile.FormKey;
    public bool Enabled => Profile.Enabled;

    public PdfPhaseSetting? Phase(string? phaseKey) => phaseKey == null ? null : Profile.Phases.FirstOrDefault(p => p.PhaseKey == phaseKey);

    public PdfFieldSetting? Field(string fieldKey, string? phaseKey) =>
        phaseKey == null ? null : Profile.Fields.FirstOrDefault(f => f.FieldKey == fieldKey && f.PhaseKey == phaseKey);

    public IEnumerable<string> ConvertFields(string? phaseKey) => Enabled ? Profile.Fields.Where(f => f.PhaseKey == phaseKey && f.Convert).Select(f => f.FieldKey).Distinct() : [];
    public IEnumerable<string> RequiredFields(string? phaseKey) => Enabled ? Profile.Fields.Where(f => f.PhaseKey == phaseKey && f.Require).Select(f => f.FieldKey).Distinct() : [];
    public bool PdfOnly(string fieldKey, string? phaseKey) => Enabled && Field(fieldKey, phaseKey)?.PdfOnly == true;
    public bool Converts(string fieldKey, string? phaseKey) => Enabled && Field(fieldKey, phaseKey)?.Convert == true;
    public bool ConvertsAnywhere(string fieldKey) => Enabled && Profile.Fields.Any(f => f.FieldKey == fieldKey && (f.Convert || f.Require));
    public IEnumerable<string> FieldKeys => Profile.Fields.Select(f => f.FieldKey).Distinct();

    /// <summary>Minimum role that may download / print renditions in a phase (DOC-081). Managers always may.</summary>
    public AppRole DownloadRole(string? phaseKey) => Phase(phaseKey)?.DownloadRole ?? AppRole.Reader;
    public AppRole PrintRole(string? phaseKey) => Phase(phaseKey)?.PrintRole ?? AppRole.Reader;
}

/// <summary>Posted form of the rule editor.</summary>
public sealed class PdfRulesInput
{
    public bool Enabled { get; set; } = true;
    public bool AutoMark { get; set; } = true;
    public string Color { get; set; } = "#b91c1c";
    public int Opacity { get; set; } = 18;
    public bool Manifest { get; set; } = true;
    public string? ManifestNote { get; set; }
    public List<PdfPhaseSetting> Phases { get; set; } = [];
    public List<PdfFieldSetting> Fields { get; set; } = [];
}

/// <summary>
/// Stores and reads rendition rules (module tables PdfProfiles / PdfPhaseSettings / PdfFieldSettings). Packaged defaults
/// (<see cref="IPdfDefaults"/>) are installed on first read. Changes are recorded in the configuration audit (DSN-1202).
/// </summary>
[ScopedService]
public sealed class PdfRuleStore(VellumDbContext db, ConfigCache cfg, CurrentUser me, AuditService audit)
{
    private static readonly IReadOnlyDictionary<string, IPdfDefaults> Defaults = typeof(PdfRuleStore).Assembly.GetTypes()
        .Where(t => typeof(IPdfDefaults).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
        .Select(t => (IPdfDefaults)Activator.CreateInstance(t)!)
        .ToDictionary(d => d.FormKey);

    private static volatile bool _defaultsInstalled;
    private Dictionary<string, PdfFormRules?>? _cache;

    public static IEnumerable<string> DefaultForms => Defaults.Keys;

    /// <summary>Install packaged defaults for forms that have no profile yet (idempotent).</summary>
    public async Task EnsureDefaultsAsync()
    {
        if (_defaultsInstalled) return;
        var existing = await db.Set<PdfProfile>().Select(p => p.FormKey).ToListAsync();
        var added = false;
        foreach (var (formKey, def) in Defaults)
        {
            if (existing.Contains(formKey) || !cfg.Current.FormsByKey.ContainsKey(formKey)) continue;
            var profile = new PdfProfile { FormKey = formKey, Origin = "default", UpdatedAt = DateTime.UtcNow };
            def.Define(new PdfRulesBuilder(profile));
            db.Set<PdfProfile>().Add(profile);
            audit.Config("PDF renditions", formKey, "Defaults installed", null, Describe(profile));
            added = true;
        }
        if (added)
        {
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); } // another scope installed them first
        }
        // Only remember success once every packaged form exists (apps install before their rules are first read).
        _defaultsInstalled = Defaults.Keys.All(cfg.Current.FormsByKey.ContainsKey);
        _cache = null;
    }

    /// <summary>Rules of a form, or null when none are configured. Cached for the scope.</summary>
    public async Task<PdfFormRules?> ForFormAsync(string formKey)
    {
        _cache ??= await LoadAllAsync();
        return _cache.GetValueOrDefault(formKey);
    }

    public async Task<List<PdfFormRules>> AllAsync()
    {
        _cache ??= await LoadAllAsync();
        return _cache.Values.OfType<PdfFormRules>().ToList();
    }

    private async Task<Dictionary<string, PdfFormRules?>> LoadAllAsync()
    {
        await EnsureDefaultsAsync();
        var profiles = await db.Set<PdfProfile>().AsNoTracking().Include(p => p.Phases).Include(p => p.Fields).ToListAsync();
        return profiles.ToDictionary(p => p.FormKey, p => (PdfFormRules?)new PdfFormRules { Profile = p });
    }

    public void Invalidate() => _cache = null;

    /// <summary>Replace a form's rules (administrators only). Unknown phases and non-attachment fields are dropped.</summary>
    public async Task<(bool Ok, string Message)> SaveAsync(string formKey, PdfRulesInput input)
    {
        if (!me.IsAdmin) return (false, "Only administrators can change PDF rendition rules.");
        if (!cfg.Current.FormsByKey.TryGetValue(formKey, out var form)) return (false, "That form doesn't exist.");
        var phases = form.Workflows.SelectMany(w => w.Phases).Select(p => p.Key).ToHashSet();
        var fields = form.Fields.Where(f => f.Type == FieldType.Attachment).Select(f => f.Key).ToHashSet();
        var color = NormalizeColor(input.Color);
        if (color == null) return (false, "Enter the watermark colour as a hex value, for example #b91c1c.");

        var profile = await db.Set<PdfProfile>().Include(p => p.Phases).Include(p => p.Fields).FirstOrDefaultAsync(p => p.FormKey == formKey);
        var before = profile == null ? null : Describe(profile);
        if (profile == null)
        {
            profile = new PdfProfile { FormKey = formKey };
            db.Set<PdfProfile>().Add(profile);
        }
        profile.Enabled = input.Enabled;
        profile.AutoMark = input.AutoMark;
        profile.Color = color;
        profile.Opacity = Math.Clamp(input.Opacity, 5, 100);
        profile.Manifest = input.Manifest;
        profile.ManifestNote = string.IsNullOrWhiteSpace(input.ManifestNote) ? null : input.ManifestNote.Trim()[..Math.Min(2000, input.ManifestNote.Trim().Length)];
        profile.UpdatedAt = DateTime.UtcNow;
        profile.UpdatedById = me.Id;
        profile.Origin = "custom";

        db.Set<PdfPhaseSetting>().RemoveRange(profile.Phases);
        db.Set<PdfFieldSetting>().RemoveRange(profile.Fields);
        profile.Phases.Clear();
        profile.Fields.Clear();
        foreach (var p in input.Phases.Where(p => phases.Contains(p.PhaseKey)).GroupBy(p => p.PhaseKey).Select(g => g.Last()))
        {
            var wm = string.IsNullOrWhiteSpace(p.Watermark) ? null : p.Watermark.Trim();
            if (wm?.Length > 300) return (false, "Watermarks can be at most 300 characters.");
            if (wm == null && p.DownloadRole == AppRole.Reader && p.PrintRole == AppRole.Reader) continue;
            profile.Phases.Add(new PdfPhaseSetting { PhaseKey = p.PhaseKey, Watermark = wm, Position = p.Position, DownloadRole = p.DownloadRole, PrintRole = p.PrintRole });
        }
        foreach (var f in input.Fields.Where(f => fields.Contains(f.FieldKey) && phases.Contains(f.PhaseKey) && (f.Convert || f.Require || f.PdfOnly))
                     .GroupBy(f => (f.FieldKey, f.PhaseKey)).Select(g => g.Last()))
            profile.Fields.Add(new PdfFieldSetting { FieldKey = f.FieldKey, PhaseKey = f.PhaseKey, Convert = f.Convert, Require = f.Require, PdfOnly = f.PdfOnly });

        // A phase that requires renditions needs a way to produce them before or on entry.
        var order = form.Workflows.SelectMany(w => w.Phases).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Min(p => p.Order));
        foreach (var req in profile.Fields.Where(f => f.Require))
            if (!profile.Fields.Any(c => c.FieldKey == req.FieldKey && c.Convert && order.GetValueOrDefault(c.PhaseKey) <= order.GetValueOrDefault(req.PhaseKey)))
                return (false, $"{Label(form, req.FieldKey)} is required as PDF in {PhaseName(form, req.PhaseKey)}, but it isn't converted in that phase or any earlier one. Turn on Convert in an earlier phase (INV-054).");

        var after = Describe(profile);
        if (before == after && profile.Id != 0) return (true, "No changes to save.");
        audit.Config("PDF renditions", formKey, before == null ? "Created" : "Changed", before, after);
        await db.SaveChangesAsync();
        _cache = null;
        return (true, $"PDF rules for {form.Name} saved.");
    }

    private static string Label(FormDefinition form, string key) => form.Fields.FirstOrDefault(f => f.Key == key)?.Label ?? key;
    private static string PhaseName(FormDefinition form, string key) => form.Workflows.SelectMany(w => w.Phases).FirstOrDefault(p => p.Key == key)?.Name ?? key;

    public static string? NormalizeColor(string? c)
    {
        var s = (c ?? "").Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        return s.Length == 7 && int.TryParse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? s.ToLowerInvariant() : null;
    }

    private static string Describe(PdfProfile p) => JsonSerializer.Serialize(new
    {
        p.Enabled, p.AutoMark, p.Color, p.Opacity, p.Manifest, p.ManifestNote,
        Phases = p.Phases.OrderBy(x => x.PhaseKey).Select(x => new { x.PhaseKey, x.Watermark, Position = x.Position.ToString(), Download = x.DownloadRole.ToString(), Print = x.PrintRole.ToString() }),
        Fields = p.Fields.OrderBy(x => x.FieldKey).ThenBy(x => x.PhaseKey).Select(x => new { x.FieldKey, x.PhaseKey, x.Convert, x.Require, x.PdfOnly }),
    });

    /// <summary>Resolve watermark tokens: {number}, {revision}, {title}, {phase}, {effective_date}, {today}, {form}, {app}, {org} or any field key.</summary>
    public static string ResolveTokens(string template, RecordSnapshot snap, UserDirectory dir, string organization, TimeZoneInfo tz)
    {
        var r = snap.Record;
        static string D(DateOnly? d) => d?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "";
        return FormRules.ApplyTemplate(template, key => key.Trim().ToLowerInvariant() switch
        {
            "number" => r.Number,
            "revision" or "rev" => r.Revision ?? "",
            "title" => r.Title,
            "phase" => snap.Phase?.Name ?? "",
            "effective_date" or "effective" => D(r.EffectiveDate),
            "today" or "date" => D(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz))),
            "form" => snap.Form.Name,
            "app" => snap.App.Name,
            "org" or "organization" => organization,
            "status" => r.IsArchived ? (r.ArchiveReason == "obsolete" ? "Obsolete" : "Superseded") : r.IsEffective ? "Effective" : snap.Phase?.Name ?? "",
            var k when snap.Form.Fields.FirstOrDefault(f => f.Key == k) is { } f => FieldCodec.Display(f, snap.Data.Main[f.Key], snap.Config, dir, tz),
            _ => "{" + key + "}",
        }).Trim();
    }

    public static readonly (string Token, string Meaning)[] Tokens =
    [
        ("{number}", "Record number, e.g. SOP-00012"), ("{revision}", "Revision, e.g. B"), ("{title}", "Title"), ("{phase}", "Current phase"),
        ("{effective_date}", "Effective date (released revisions)"), ("{today}", "Date the rendition is made"), ("{status}", "Effective, Superseded, Obsolete or the phase"),
        ("{org}", "Organization name"), ("{field_key}", "Any field of the form, e.g. {department}"),
    ];
}
