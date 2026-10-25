using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Training;

public sealed record MyTrainingWidgetModel(List<AssignmentRow> Pending, int CompletedThisYear, int Expiring);

/// <summary>TRN-044: the homepage "My training" widget with due dates and a link to acknowledge.</summary>
public sealed class MyTrainingWidget(TrainingService training, CurrentUser me) : IHomeWidget
{
    public string Key => "my-training";
    public int Order => 10;
    public string Column => "side";

    public async Task<HomeWidgetModel?> BuildAsync()
    {
        if (!me.IsAuthenticated || me.IsSystem) return null;
        var (pending, done, expiring) = await training.MineAsync(me.Id);
        if (pending.Count == 0 && done == 0) return null;
        return new HomeWidgetModel("~/Modules/Training/Views/_MyTrainingWidget.cshtml", new MyTrainingWidgetModel(pending, done, expiring));
    }
}
