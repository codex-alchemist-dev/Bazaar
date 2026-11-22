using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Health;

/// <summary>Bazaar.Server.Health as a Bazaar Component (doc 25 Health and Analytics): the project health service and its settings.</summary>
public sealed class HealthServerComponent : IBazaarComponent
{
    /// <summary>Configuration section for the health settings until the Settings service feeds them.</summary>
    public const string SectionName = "Bazaar:Health";

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
    /// Registers <see cref="IProjectHealthService"/> over the host's <c>IMetricStore</c> and <see cref="TimeProvider"/>. Settings read
    /// <c>Bazaar:Health</c> (<c>CoverageDropPoints</c>, <c>BuildFailureStreak</c>, <c>WindowDays</c>, <c>MoraleSignals</c>). Bound by hand, no
    /// reflection. Every registration is TryAdd, so a host or test can substitute first.
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
        services.TryAddSingleton<IProjectHealthService, ProjectHealthService>();
        return services;
    }

    private static int Int(IConfigurationSection? s, string key, int fallback) =>
        int.TryParse(s?[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
