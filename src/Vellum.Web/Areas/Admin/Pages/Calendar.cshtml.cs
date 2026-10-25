using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Modules.Admin;

namespace Vellum.Areas.Admin.Pages;

/// <summary>ADM-301: business calendar exceptions (holidays and extra working days) with a year view.</summary>
public class CalendarModel(VellumDbContext db, CalendarAdminService calendar) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public int? Year { get; set; }

    public int Y => Year ?? BusinessCalendar.Today.Year;
    public List<CalendarDay> Days { get; private set; } = [];
    public Dictionary<DateOnly, CalendarDay> ByDate { get; private set; } = [];
    public List<int> Years { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Y is < 2000 or > 2100) return NotFound();
        var start = new DateOnly(Y, 1, 1);
        var end = new DateOnly(Y, 12, 31);
        Days = await db.CalendarDays.AsNoTracking().Where(d => d.Date >= start && d.Date <= end).OrderBy(d => d.Date).ToListAsync();
        ByDate = Days.ToDictionary(d => d.Date);
        Years = await db.CalendarDays.Select(d => d.Date.Year).Distinct().OrderBy(y => y).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(DateOnly? date, string? name, bool isWorking)
    {
        var res = await calendar.AddAsync(date, name, isWorking);
        return Done(res, $"/admin/calendar?year={(date ?? BusinessCalendar.Today).Year}");
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id) => Done(await calendar.RemoveAsync(id), $"/admin/calendar?year={Y}");
}
