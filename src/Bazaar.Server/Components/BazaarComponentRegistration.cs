using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Components;

public static class BazaarComponentRegistration
{
    /// <summary>
    /// The kernel's registration step. Validates and orders every component, then registers the enabled ones in dependency order.
    /// A disabled component is validated but not registered. An enabled component that depends on a disabled one fails the start.
    /// Enablement is <c>Bazaar:Components:&lt;id&gt;:Enabled</c>, default true.
    /// </summary>
    /// <exception cref="ComponentGraphException">The graph cannot start.</exception>
    public static IServiceCollection AddBazaarComponents(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<IBazaarComponent> components)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(components);

        var all = components.ToList();
        var ordered = ComponentGraph.Order(all.Select(c => c.Manifest));
        var enabled = new HashSet<string>(
            all.Where(c => IsEnabled(configuration, c.Manifest.Id)).Select(c => c.Manifest.Id),
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

    /// <summary>Whether a component is enabled by configuration. Missing settings mean enabled.</summary>
    public static bool IsEnabled(IConfiguration configuration, string componentId)
    {
        var value = configuration["Bazaar:Components:" + componentId + ":Enabled"];
        return value is null || !bool.TryParse(value, out var enabled) || enabled;
    }
}
