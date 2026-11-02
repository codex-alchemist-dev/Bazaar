using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Components;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gc;
using Bazaar.Server.Hosting;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The whole server service graph, in the order the host runs it. <c>Program.cs</c> calls this once; a test calls it to prove the
/// graph resolves, so a missing registration fails in CI instead of at the first start.
/// </summary>
public static class HostApplicationExtensions
{
    /// <summary>Registers every host module. Does not add the Windows service or systemd hooks, nor the status listener.</summary>
    public static IServiceCollection AddBazaarHostApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBazaarDevAuth(configuration);
        services.AddBazaarHostOptions(configuration);
        services.AddBazaarHostRuntime(configuration);
        services.AddBazaarSecrets();
        services.AddBazaarProviders(configuration);
        services.AddBazaarHostWire(configuration);
        services.AddBazaarSync(configuration);
        // Kept where AddBazaarGcWire ran, after AddBazaarSync, so the GC hosted services still start before the agent responder.
        services.AddBazaarComponents(configuration, [new GcComponent()]);
        services.AddBazaarAgentSafety();
        services.AddBazaarSettings();
        services.AddBazaarGateway();
        services.AddBazaarAgentResponder(configuration);

        // Bazaar Components (docs/design/BAZAAR-COMPONENTS.md). Each entry is one component; the kernel orders and validates them.
        services.AddBazaarComponents(configuration, HostComponents.All);
        FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(configuration);
        return services;
    }
}
