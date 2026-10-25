using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Engine;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Documents;

public sealed record DueForReviewWidgetModel(List<ReviewInfo> Items, int Overdue);

/// <summary>Home widget for document owners: their effective documents whose periodic review is overdue or due within 60 days (DOC-072).</summary>
public sealed class DocumentsDueForReviewWidget(DocumentControlService svc, VellumDbContext db, CurrentUser me) : IHomeWidget
{
    public string Key => "documents-due-review";
    public int Order => 30;
    public string Column => "side";

    public async Task<HomeWidgetModel?> BuildAsync()
    {
        if (!me.IsAuthenticated || me.IsSystem) return null;
        var formId = svc.DocumentForm.Id;
        var uid = me.Id;
        var docs = await db.Records.AsNoTracking()
            .Where(r => r.FormId == formId && r.IsEffective && db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "owner" && v.RefId == uid))
            .ToListAsync();
        var due = (await svc.ReviewScheduleAsync(docs)).Where(i => i.Window is "overdue" or "due30" or "due60").OrderBy(i => i.NextReview).ToList();
        return due.Count == 0 ? null : new HomeWidgetModel("~/Modules/Documents/Views/_DueForReviewWidget.cshtml", new DueForReviewWidgetModel(due, due.Count(i => i.Window == "overdue")));
    }
}

/// <summary>Sidebar link to the periodic review schedule (DOC-120 "documents due for review").</summary>
public sealed class DocumentNav(AccessService access, ConfigCache cfg) : INavContributor
{
    public IEnumerable<NavItem> Items()
    {
        if (cfg.Current.AppsByKey.TryGetValue(DocKeys.App, out var app) && access.CanOpenApp(app.Id))
            yield return new NavItem("Document control", "Review schedule", "/documents/reviews", "bi-calendar2-check", 10);
    }
}
