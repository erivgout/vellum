using System.Reflection;
using Vellum.Engine.Behaviors;
using Vellum.Infrastructure.Jobs;
using Vellum.Packaging;

namespace Vellum.Infrastructure;

/// <summary>
/// Mark a class to be registered as a scoped service automatically (no Program.cs edits needed). Optionally list
/// interfaces it should also be resolvable as (same instance per scope), e.g. <c>[ScopedService(typeof(IPdfService))]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ScopedServiceAttribute(params Type[] serviceTypes) : Attribute
{
    public IReadOnlyList<Type> ServiceTypes { get; } = serviceTypes;
}

/// <summary>Mark a <see cref="IHostedService"/> (e.g. a <see cref="BackgroundService"/>) to be started with the application.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BackgroundWorkerAttribute : Attribute;

/// <summary>A homepage widget contributed by a module (PLT-1402).</summary>
public interface IHomeWidget
{
    string Key { get; }
    /// <summary>Lower renders first.</summary>
    int Order { get; }
    /// <summary>"main" (wide column) or "side" (right rail).</summary>
    string Column { get; }
    /// <summary>Return null to hide the widget for this user.</summary>
    Task<HomeWidgetModel?> BuildAsync();
}

/// <param name="Partial">App-relative partial path, e.g. <c>~/Modules/Training/Views/_MyTraining.cshtml</c>.</param>
public sealed record HomeWidgetModel(string Partial, object? Model);

/// <summary>Adds links to the sidebar (e.g. Administration Center, Designer, Insights).</summary>
public interface INavContributor
{
    IEnumerable<NavItem> Items();
}

/// <param name="Section">Sidebar group: "Workspace", "Insights", "Administration".</param>
/// <param name="ActivePrefix">Path prefix that marks the item active (defaults to Href).</param>
public sealed record NavItem(string Section, string Label, string Href, string Icon, int Order = 100, string? Badge = null, string? ActivePrefix = null);

public static class ExtensibilityRegistration
{
    /// <summary>Discover behaviors, packaged apps, jobs, widgets, nav contributors and [ScopedService] classes.</summary>
    public static IServiceCollection AddVellumModules(this IServiceCollection services, Assembly assembly)
    {
        foreach (var t in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            if (typeof(RecordBehavior).IsAssignableFrom(t)) services.AddScoped(typeof(RecordBehavior), t);
            if (typeof(IPackagedApp).IsAssignableFrom(t)) services.AddSingleton(typeof(IPackagedApp), t);
            if (typeof(IScheduledJob).IsAssignableFrom(t)) services.AddScoped(typeof(IScheduledJob), t);
            if (typeof(IHomeWidget).IsAssignableFrom(t)) services.AddScoped(typeof(IHomeWidget), t);
            if (typeof(INavContributor).IsAssignableFrom(t)) services.AddScoped(typeof(INavContributor), t);
            if (t.GetCustomAttribute<ScopedServiceAttribute>() is { } scoped)
            {
                services.AddScoped(t);
                foreach (var st in scoped.ServiceTypes) services.AddScoped(st, sp => sp.GetRequiredService(t));
            }
            if (typeof(IHostedService).IsAssignableFrom(t) && t.GetCustomAttribute<BackgroundWorkerAttribute>() != null)
                services.AddSingleton(typeof(IHostedService), t);
        }
        return services;
    }
}
