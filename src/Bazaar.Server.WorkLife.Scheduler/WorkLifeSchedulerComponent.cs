using Cdax.Core;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife.Scheduler.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.WorkLife.Scheduler;

/// <summary>
/// The shift scheduler as a Bazaar Component (docs/design/09, "Schedule calculator" and "Availability machine"). It drives each agent's
/// availability from its schedule and reports every applied transition to presence through the presence sources module. The source,
/// the state store and the drain gate are in memory or immediate until the durable ones (doc 09 WP3, WP5) register theirs first.
/// </summary>
public sealed class WorkLifeSchedulerComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "work-life-scheduler",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["presence-sources"],
        Provides: ["feature:work-life-scheduler"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddBazaarWorkLifeScheduler(configuration);
}

/// <summary>Registers the shift scheduler.</summary>
public static class WorkLifeSchedulerServiceCollectionExtensions
{
    /// <summary>Adds the scheduler, its in-memory defaults and the hosted loop. Options bind from <c>Bazaar:WorkLifeScheduler</c>.</summary>
    public static IServiceCollection AddBazaarWorkLifeScheduler(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(ShiftSchedulerOptionsReader.Read(configuration.GetSection(ShiftSchedulerOptions.SectionName)));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InMemoryAgentScheduleSource>();
        services.TryAddSingleton<IAgentScheduleSource>(sp => sp.GetRequiredService<InMemoryAgentScheduleSource>());
        services.TryAddSingleton<IAvailabilityStateStore, InMemoryAvailabilityStateStore>();
        services.TryAddSingleton<IDrainGate, ImmediateDrainGate>();
        services.TryAddSingleton(sp => new ShiftScheduler(
            sp.GetRequiredService<IAgentScheduleSource>(),
            sp.GetRequiredService<IAvailabilityStateStore>(),
            sp.GetRequiredService<IDrainGate>(),
            sp.GetRequiredService<ShiftSchedulerOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IAvailabilityTransitionSink>(),
            sp.GetService<ILogger<ShiftScheduler>>()));
        services.AddHostedService<ShiftSchedulerHostedService>();
        return services;
    }
}
