using Bazaar.Core;
using Bazaar.Server.Jobs;
using Bazaar.Server.Knowledge.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Health;

/// <summary>Bazaar.Server.Health as a Bazaar Component (doc 25 Health and Analytics): the project health service and its settings.</summary>
public sealed class HealthServerComponent : IBazaarComponent
{
    /// <summary>Configuration section for the health settings until the Settings service feeds them.</summary>
    public const string SectionName = "Bazaar:Health";

    /// <summary>Migration namespace and history key for this component's SQL. Equals <see cref="ComponentManifest.MigrationNamespace"/>.</summary>
    public const string MigrationProvider = "health";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "health",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:project-health"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarHealth(configuration);
}

/// <summary>Composition for Bazaar.Server.Health.</summary>
public static class HealthServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProjectHealthService"/>, the metric store and the recurring collection job. The store is Postgres when the
    /// host registered an <see cref="NpgsqlDataSource"/> (the <c>health</c> migration creates <c>metric_samples</c>), otherwise in memory.
    /// Settings read <c>Bazaar:Health</c> (<c>CoverageDropPoints</c>, <c>BuildFailureStreak</c>, <c>WindowDays</c>, <c>MoraleSignals</c>,
    /// <c>CollectIntervalMinutes</c>, default 60; <c>RawRetentionDays</c>, default 90, replaced by the host's setting; <c>RetentionIntervalMinutes</c>, default 1440). Bound by hand, no reflection. Every registration is TryAdd, so a host or test can
    /// substitute first. The collection job needs the host's job poller and does nothing until a module registers an
    /// <see cref="IHealthProjectSource"/>.
    /// </summary>
    public static IServiceCollection AddBazaarHealth(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var s = configuration?.GetSection(HealthServerComponent.SectionName);
        var d = ProjectHealthSettings.Defaults;
        var signals = s?.GetSection("MoraleSignals").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToArray();
        services.TryAddSingleton(new ProjectHealthSettings
        {
            CoverageDropPoints = Int(s, "CoverageDropPoints", d.CoverageDropPoints),
            BuildFailureStreak = Int(s, "BuildFailureStreak", d.BuildFailureStreak),
            WindowDays = Int(s, "WindowDays", d.WindowDays),
            MoraleSignals = signals is { Length: > 0 } ? signals : d.MoraleSignals,
        });
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMetricStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgMetricStore(dataSource) : new InMemoryMetricStore());
        services.TryAddSingleton<IProjectHealthService, ProjectHealthService>();
        services.TryAddSingleton<IHealthReportStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgHealthReportStore(dataSource) : new InMemoryHealthReportStore());
        services.TryAddSingleton<HealthReportFactSource>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthFactSource, HealthReportFactSource>(sp => sp.GetRequiredService<HealthReportFactSource>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProjectSource, HealthReportFactSource>(sp => sp.GetRequiredService<HealthReportFactSource>()));

        services.TryAddSingleton<HealthCollectJobHandler>();
        services.AddSingleton<IJobHandler>(sp => sp.GetRequiredService<HealthCollectJobHandler>());
        var minutes = Math.Clamp(Int(s, "CollectIntervalMinutes", 60), 1, 24 * 60);
        services.AddSingleton(new RecurringJob(HealthCollectJobHandler.JobKind, TimeSpan.FromMinutes(minutes)));

        services.TryAddSingleton<IRawRetentionSource>(new FixedRawRetentionSource(
            Math.Clamp(Int(s, "RawRetentionDays", RawRetention.DefaultDays), RawRetention.MinDays, RawRetention.MaxDays)));
        services.TryAddSingleton<HealthRetentionJobHandler>();
        services.AddSingleton<IJobHandler>(sp => sp.GetRequiredService<HealthRetentionJobHandler>());
        var sweepMinutes = Math.Clamp(Int(s, "RetentionIntervalMinutes", 24 * 60), 5, 7 * 24 * 60);
        services.AddSingleton(new RecurringJob(HealthRetentionJobHandler.JobKind, TimeSpan.FromMinutes(sweepMinutes)));
        return services;
    }

    private static int Int(IConfigurationSection? s, string key, int fallback) =>
        int.TryParse(s?[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
