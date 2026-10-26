namespace Vellum.Tests.E2e;

/// <summary>Action items (the shared follow-up activity of CAPAs, audits, risks, incidents and FMEAs) worked by their assignee.</summary>
public static class ActionItemSteps
{
    /// <summary>The assignee finds the action item in their work queue, records what was done and completes it with a signature.</summary>
    public static async Task<Page> CompleteActionItemAsync(this Browser assignee, string numberOrTitle, string actionTaken)
    {
        var work = await assignee.GoAsync("/work");
        var link = work.All("a[href^='/records/']").FirstOrDefault(a => a.TextContent.Contains(numberOrTitle))
            ?? throw new Xunit.Sdk.XunitException($"{numberOrTitle} isn't in the work queue.\n{work.Excerpt()}");
        var item = await assignee.GoAsync(link.GetAttribute("href")!);
        Assert.Equal("Open", item.Phase());
        item = await item.SaveAsync(f => f.Set("f.action_taken", actionTaken));
        item = await item.RouteOkAsync("Complete");
        Assert.Equal("Completed", item.Phase());
        return item;
    }
}
