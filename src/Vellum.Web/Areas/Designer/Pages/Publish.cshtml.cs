using Microsoft.AspNetCore.Mvc;
using Vellum.Modules.Designer;

namespace Vellum.Areas.Designer.Pages;

/// <summary>Validate → impact analysis → publish (DSN-003, DSN-1201, SAF-070/071).</summary>
public class PublishModel(DesignerStore store, DesignerPublisher publisher) : DesignerPageModel(store)
{
    public PublishPlan Plan { get; private set; } = null!;
    [BindProperty] public string? Note { get; set; }
    [BindProperty] public bool AcceptWarnings { get; set; }
    public List<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        Plan = await publisher.PlanAsync(T);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await OpenAsync() is { } deny) return deny;
        var result = await publisher.PublishAsync(T.AppKey, Note, AcceptWarnings);
        if (result.Ok)
        {
            Toast($"Published {T.Doc.Name} as configuration version {result.Version} ({result.Changes} change{(result.Changes == 1 ? "" : "s")}).");
            return Redirect($"/designer/versions?app={T.AppKey}&v={result.Version}");
        }
        Errors = [.. result.Errors];
        if (await OpenAsync() is { } again) return again;
        Plan = await publisher.PlanAsync(T);
        return Page();
    }
}
