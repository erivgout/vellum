using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Modules.Pdf;

namespace Vellum.Areas.Admin.Pages.Pdf;

public sealed record PdfFormRow(AppDefinition App, FormDefinition Form, List<FieldDefinition> Fields, PdfFormRules? Rules,
    List<string> ConvertPhases, List<string> RequirePhases, List<string> PdfOnlyPhases, int Watermarks);

/// <summary>PLT §2.8 overview: forms with attachment fields and their rendition rules, converter health and queue state.</summary>
public class IndexModel(VellumDbContext db, ConfigCache cfg, PdfRuleStore rules, PdfConverter converter) : AdminPageModel
{
    public List<PdfFormRow> Rows { get; private set; } = [];
    public string? Soffice { get; private set; }
    public string MissingMessage { get; private set; } = "";
    public Dictionary<PdfJobStatus, int> Counts { get; private set; } = [];
    public int FailedRecent { get; private set; }
    public int? P95Ms { get; private set; }
    public int OfficeDone { get; private set; }
    public int Cached { get; private set; }

    public async Task OnGetAsync()
    {
        var all = (await rules.AllAsync()).ToDictionary(r => r.FormKey);
        foreach (var app in cfg.Current.Apps.Where(a => !a.IsSystem).OrderBy(a => a.Suite).ThenBy(a => a.Order).ThenBy(a => a.Name))
            foreach (var form in app.Forms.Where(f => !f.Disabled).OrderBy(f => f.Order))
            {
                var fields = form.Fields.Where(f => f.Type == FieldType.Attachment).OrderBy(f => f.Order).ToList();
                if (fields.Count == 0) continue;
                var r = all.GetValueOrDefault(form.Key);
                var phases = form.Workflows.SelectMany(w => w.Phases).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Name);
                List<string> Names(Func<PdfFieldSetting, bool> pick) => r == null ? [] :
                    r.Profile.Fields.Where(pick).Select(f => f.PhaseKey).Distinct().Select(k => phases.GetValueOrDefault(k, k)).ToList();
                Rows.Add(new PdfFormRow(app, form, fields, r, Names(f => f.Convert), Names(f => f.Require), Names(f => f.PdfOnly),
                    r?.Profile.Phases.Count(p => !string.IsNullOrWhiteSpace(p.Watermark)) ?? 0));
            }
        var office = converter.Office;
        Soffice = office.Locate();
        MissingMessage = office.MissingMessage;
        Counts = await db.Set<PdfJob>().GroupBy(j => j.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var since = DateTime.UtcNow.AddDays(-7);
        FailedRecent = await db.Set<PdfJob>().CountAsync(j => j.Status == PdfJobStatus.Failed && j.CompletedAt >= since);
        var durations = await db.Set<PdfConversion>().Where(c => c.Converter == "libreoffice" && c.ConvertedAt >= DateTime.UtcNow.AddDays(-30))
            .Select(c => c.DurationMs).OrderBy(d => d).ToListAsync();
        OfficeDone = durations.Count;
        if (durations.Count > 0) P95Ms = durations[Math.Clamp((int)Math.Ceiling(durations.Count * 0.95) - 1, 0, durations.Count - 1)];
        Cached = await db.Set<PdfConversion>().CountAsync();
    }
}
