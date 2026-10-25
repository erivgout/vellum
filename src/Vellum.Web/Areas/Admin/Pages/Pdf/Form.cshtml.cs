using Microsoft.AspNetCore.Mvc;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Modules.Pdf;

namespace Vellum.Areas.Admin.Pages.Pdf;

/// <summary>Rendition rules of one form: the phase × attachment-field matrix (PLT-804..807, DOC-021..023, DOC-081).</summary>
public class FormModel(ConfigCache cfg, PdfRuleStore rules) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";

    public FormDefinition Form { get; private set; } = null!;
    public List<PhaseDefinition> Phases { get; private set; } = [];
    public List<FieldDefinition> Fields { get; private set; } = [];
    public PdfRulesInput Input { get; private set; } = new();
    public PdfProfile? Profile { get; private set; }

    private bool Load()
    {
        if (!cfg.Current.FormsByKey.TryGetValue(Key, out var form)) return false;
        Form = form;
        Phases = form.Workflows.OrderBy(w => w.Disabled).SelectMany(w => w.Phases.OrderBy(p => p.Order)).GroupBy(p => p.Key).Select(g => g.First()).ToList();
        Fields = form.Fields.Where(f => f.Type == FieldType.Attachment).OrderBy(f => f.Order).ToList();
        return Fields.Count > 0;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Load()) return NotFound();
        var r = await rules.ForFormAsync(Key);
        Profile = r?.Profile;
        Input = r == null ? new PdfRulesInput { Enabled = true } : new PdfRulesInput
        {
            Enabled = r.Profile.Enabled, AutoMark = r.Profile.AutoMark, Color = r.Profile.Color, Opacity = r.Profile.Opacity,
            Manifest = r.Profile.Manifest, ManifestNote = r.Profile.ManifestNote, Phases = r.Profile.Phases, Fields = r.Profile.Fields,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!Load()) return NotFound();
        var f = Request.Form;
        bool On(string name) => f[name].Any(v => v == "true" || v == "on");
        var input = new PdfRulesInput
        {
            Enabled = On("enabled"), AutoMark = On("autoMark"), Manifest = On("manifest"),
            Color = f["color"].ToString(), Opacity = int.TryParse(f["opacity"], out var o) ? o : 18, ManifestNote = f["manifestNote"],
        };
        foreach (var p in Phases)
        {
            input.Phases.Add(new PdfPhaseSetting
            {
                PhaseKey = p.Key, Watermark = f[$"wm.{p.Key}"],
                Position = Enum.TryParse<WatermarkPosition>(f[$"pos.{p.Key}"], out var pos) ? pos : WatermarkPosition.Diagonal,
                DownloadRole = Enum.TryParse<AppRole>(f[$"dl.{p.Key}"], out var dl) && dl >= AppRole.Reader ? dl : AppRole.Reader,
                PrintRole = Enum.TryParse<AppRole>(f[$"pr.{p.Key}"], out var pr) && pr >= AppRole.Reader ? pr : AppRole.Reader,
            });
            foreach (var fld in Fields)
                input.Fields.Add(new PdfFieldSetting
                {
                    FieldKey = fld.Key, PhaseKey = p.Key,
                    Convert = On($"cv.{fld.Key}.{p.Key}"), Require = On($"rq.{fld.Key}.{p.Key}"), PdfOnly = On($"po.{fld.Key}.{p.Key}"),
                });
        }
        var (ok, message) = await rules.SaveAsync(Key, input);
        if (ok) return Toast(message, $"/admin/pdf/form/{Key}");
        Errors.Add(message);
        Input = input;
        Profile = (await rules.ForFormAsync(Key))?.Profile;
        return Page();
    }

    public PdfPhaseSetting? PhaseOf(string key) => Input.Phases.FirstOrDefault(p => p.PhaseKey == key);
    public PdfFieldSetting? FieldOf(string field, string phase) => Input.Fields.FirstOrDefault(x => x.FieldKey == field && x.PhaseKey == phase);

    public static string TypeLabel(PhaseDefinition p) => p.Type switch
    {
        PhaseType.Normal => p.IsStart ? "Start" : "In progress",
        PhaseType.Completed => "Completed",
        PhaseType.Voided => "Voided",
        PhaseType.Archived => "Archived",
        PhaseType.Obsolete => "Obsolete",
        _ => p.Type.ToString(),
    };
}
