using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>ADM-1003: every record deletion with application, form, number, revision, snapshot, reason and user.</summary>
public class DeletionsModel(VellumDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public Paged<DeletionHistory> Result { get; private set; } = null!;
    public bool Filtered => Q != null || From != null || To != null;

    public async Task OnGetAsync()
    {
        var q = db.DeletionHistory.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            q = q.Where(d => d.Number.Contains(s) || d.AppName.Contains(s) || d.FormName.Contains(s) || d.UserName.Contains(s) || d.Reason.Contains(s));
        }
        if (From is { } f) { var t = f.ToDateTime(TimeOnly.MinValue); q = q.Where(d => d.At >= t); }
        if (To is { } to) { var t = to.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(d => d.At < t); }
        Result = await q.OrderByDescending(d => d.Id).PageAsync(P, 25);
    }

    public static string Pretty(string json)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(json), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
        catch (System.Text.Json.JsonException) { return json; }
    }
}
