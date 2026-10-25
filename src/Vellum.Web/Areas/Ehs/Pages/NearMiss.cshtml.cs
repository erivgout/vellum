using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Ehs;
using Vellum.Modules.Kit;

namespace Vellum.Areas.Ehs.Pages;

/// <summary>
/// One-screen near-miss report for anyone (EHS-101/102). It creates an incident of type "near miss" that goes straight to EHS.
/// "Report anonymously" creates it as the anonymous intake identity, so the reporter's name isn't stored on the record or its
/// audit trail. A named EHS team member then has to accept the report before the investigation can move on.
/// </summary>
public class NearMissModel(ConfigCache cfg, AccessService access, RecordService records, CurrentUser me, UserDirectory dir, VellumDbContext db) : PageModel
{
    [BindProperty] public string? Subject { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Site { get; set; }
    [BindProperty] public string? Area { get; set; }
    [BindProperty] public string? OccurredAt { get; set; }
    [BindProperty] public string? Potential { get; set; }
    [BindProperty] public string? Immediate { get; set; }
    [BindProperty] public string? Suggestion { get; set; }
    [BindProperty] public bool Anonymous { get; set; }

    [BindProperty(SupportsGet = true)] public Guid? Done { get; set; }

    public Dictionary<string, string> Errors { get; } = [];
    public Record? Created { get; private set; }
    public bool WasAnonymous { get; private set; }
    public IEnumerable<Keyword> PotentialOptions => cfg.Current.Options("potential_severity");

    private FormDefinition? Form => cfg.Current.FormsByKey.GetValueOrDefault("incident");

    public async Task<IActionResult> OnGetAsync()
    {
        if (Form is not { } form || !access.CanCreate(form)) return NotFound();
        if (Done is { } id)
        {
            Created = await db.Records.FindAsync(id);
            WasAnonymous = Created != null && Created.CreatedById == dir.AnonymousReporterId;
            if (Created == null || (Created.CreatedById != me.Id && !WasAnonymous)) Created = null;
        }
        OccurredAt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, records.TimeZone).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        var mySite = me.Info?.LocationId is { } loc ? await db.Locations.FindAsync(loc) : null;
        Site = Sites.All.FirstOrDefault(s => string.Equals(s.Key, mySite?.Code, StringComparison.OrdinalIgnoreCase)).Key;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Form is not { } form || !access.CanCreate(form)) return NotFound();
        if (string.IsNullOrWhiteSpace(Subject)) Errors["Subject"] = "Say in a few words what nearly happened.";
        if (string.IsNullOrWhiteSpace(Description)) Errors["Description"] = "Describe what happened.";
        if (Sites.All.All(s => s.Key != Site)) Errors["Site"] = "Choose the site.";
        if (Potential != null && PotentialOptions.All(k => k.Key != Potential)) Errors["Potential"] = "Choose a value from the list.";
        DateTime occurredUtc = default;
        if (!DateTime.TryParseExact(OccurredAt, ["yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            Errors["OccurredAt"] = "Enter when it happened.";
        else
        {
            occurredUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), records.TimeZone);
            if (occurredUtc > DateTime.UtcNow.AddMinutes(10)) Errors["OccurredAt"] = "That's in the future.";
        }
        if (Errors.Count > 0) return Page();

        var ehs = dir.ActiveMembers("ehs").Select(u => u.Id).ToList();
        var reporterId = me.Id;
        var values = new Dictionary<string, object?>
        {
            ["subject"] = Subject!.Trim(), ["incident_type"] = "near_miss", ["occurred_at"] = occurredUtc, ["site"] = Site,
            ["area"] = Area?.Trim(), ["outcome"] = "none", ["description"] = Description!.Trim(), ["potential_severity"] = Potential,
            ["immediate_actions"] = Immediate?.Trim(), ["suggestion"] = Suggestion?.Trim(),
            ["reported_by"] = Anonymous ? null : reporterId, ["anonymous"] = Anonymous,
        };
        CreateResult res;
        try
        {
            // Anonymous reports are created by the anonymous intake identity on behalf of an unnamed employee.
            if (Anonymous) me.UseAnonymousReporter("Near-miss quick report");
            res = await records.CreateAsync(new CreateRequest
            {
                FormKey = "incident", StartPhaseKey = "investigation", Values = values,
                Assignees = ehs.Count > 0 ? ehs : null, SkipAccessCheck = true,
                CreatedById = Anonymous ? dir.AnonymousReporterId : null,
                CreateComment = Anonymous ? "Anonymous near-miss report (quick report form)" : "Near-miss quick report",
            });
        }
        finally
        {
            if (Anonymous) me.Impersonate(reporterId);
        }
        if (!res.Ok)
        {
            foreach (var e in res.Errors) Errors[e.FieldKey ?? "form"] = e.Message;
            return Page();
        }
        return Redirect($"/ehs/near-miss?done={res.Record!.Id}");
    }

    public string? Err(string key) => Errors.GetValueOrDefault(key);
}
