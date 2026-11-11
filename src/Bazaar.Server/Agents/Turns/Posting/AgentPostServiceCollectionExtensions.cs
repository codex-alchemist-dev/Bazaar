using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>Registration for the agent post sink. The host must also register <c>IGcLogWriter</c> and <c>IGcBodyCodec</c> (<c>AddBazaarGcWire</c>).</summary>
public static class AgentPostServiceCollectionExtensions
{
    /// <summary>Registers <see cref="GcAgentPostSink"/> as the <see cref="IAgentPostSink"/>, unless the host already set one.</summary>
    public static IServiceCollection AddGcAgentPostSink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAgentPostSink, GcAgentPostSink>();
        return services;
    }
}
