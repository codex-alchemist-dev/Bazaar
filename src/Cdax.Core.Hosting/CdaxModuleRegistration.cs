using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cdax.Core;

public static class CdaxModuleRegistration
{
    /// <summary>
    /// The kernel's registration step. Validates and orders every component, then registers the enabled ones in dependency order.
    /// A disabled component is validated but not registered. An enabled component that depends on a disabled one fails the start.
    /// Enablement is <c>Cdax:Modules:&lt;id&gt;:Enabled</c>; with no setting it is the manifest's <c>EnabledByDefault</c> (true unless the manifest says otherwise).
    /// </summary>
    /// <exception cref="ComponentGraphException">The graph cannot start.</exception>
    public static IServiceCollection AddCdaxModules(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<ICdaxModule> components)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(components);

        var all = components.ToList();
        var ordered = ComponentGraph.Order(all.Select(c => c.Manifest));
        var enabled = new HashSet<string>(
            all.Where(c => IsEnabled(configuration, c.Manifest)).Select(c => c.Manifest.Id),
            StringComparer.Ordinal);

        foreach (var manifest in ordered.Where(m => enabled.Contains(m.Id)))
        {
            var missing = manifest.DependsOn.Where(d => !enabled.Contains(d)).ToList();
            if (missing.Count > 0)
            {
                throw new ComponentGraphException(
                    $"Enabled component '{manifest.Id}' needs disabled component(s): {string.Join(", ", missing)}.");
            }
        }

        var byId = all.ToDictionary(c => c.Manifest.Id, StringComparer.Ordinal);
        foreach (var manifest in ordered.Where(m => enabled.Contains(m.Id)))
        {
            byId[manifest.Id].AddServices(services, configuration);
        }

        return services;
    }

    /// <summary>
    /// Renamed component ids, mapped to the id they had. An operator who set the old key keeps their setting: the old key is read
    /// when the new one is absent. Add an entry whenever a component id changes after a release.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LegacyIds { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["ryusei"] = "ryusei-schedule" };

    /// <summary>Whether a component is enabled by configuration. Missing settings mean enabled; use the manifest overload for optional components.</summary>
    public static bool IsEnabled(IConfiguration configuration, string componentId) => IsEnabled(configuration, componentId, enabledByDefault: true);

    /// <summary>Whether a component is enabled: an explicit setting wins, otherwise the manifest's <see cref="ComponentManifest.EnabledByDefault"/>.</summary>
    public static bool IsEnabled(IConfiguration configuration, ComponentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return IsEnabled(configuration, manifest.Id, manifest.EnabledByDefault);
    }

    private static bool IsEnabled(IConfiguration configuration, string componentId, bool enabledByDefault)
    {
        var value = Read(configuration, componentId);
        if (value is null && LegacyIds.TryGetValue(componentId, out var legacy))
        {
            value = Read(configuration, legacy);
        }

        return value is null || !bool.TryParse(value, out var enabled) ? enabledByDefault : enabled;
    }

    /// <summary>The key prefix Bazaar used before the rename to Cdax. An operator's old settings keep working: it is read when the new key is absent.</summary>
    private const string _previousKeyPrefix = "Bazaar:Components:";

    private static string? Read(IConfiguration configuration, string componentId) =>
        configuration["Cdax:Modules:" + componentId + ":Enabled"]
        ?? configuration[_previousKeyPrefix + componentId + ":Enabled"];
}
