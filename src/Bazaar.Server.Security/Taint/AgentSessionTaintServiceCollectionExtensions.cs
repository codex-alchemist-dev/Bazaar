using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Security.Taint;

/// <summary>Registration for the agent session taint reads (doc 15, "Reads for modules").</summary>
public static class AgentSessionTaintServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="InMemoryAgentSessionDirectory"/> as both <see cref="IAgentSessionDirectory"/> and
    /// <see cref="IAgentSessionRegistrar"/>, and <see cref="AgentSessionTaintReader"/> as <see cref="IAgentSessionTaintReader"/>.
    /// All are singletons and use TryAdd, so calling this twice, or after a host registration, changes nothing.
    /// </summary>
    /// <remarks>
    /// The directory and the registrar must be the same instance, or the reader would list sessions nobody registers. When the
    /// host replaces one, it replaces both. The reader needs an <see cref="ISessionTaintReader"/>; agent-turns registers its
    /// session taint store as one. It is resolved only when the reader is.
    /// </remarks>
    public static IServiceCollection AddAgentSessionTaint(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryAgentSessionDirectory>();
        services.TryAddSingleton<IAgentSessionDirectory>(sp => sp.GetRequiredService<InMemoryAgentSessionDirectory>());
        services.TryAddSingleton<IAgentSessionRegistrar>(sp => sp.GetRequiredService<InMemoryAgentSessionDirectory>());
        services.TryAddSingleton<IAgentSessionTaintReader>(sp => new AgentSessionTaintReader(
            sp.GetRequiredService<IAgentSessionDirectory>(),
            sp.GetRequiredService<ISessionTaintReader>()));
        return services;
    }
}
