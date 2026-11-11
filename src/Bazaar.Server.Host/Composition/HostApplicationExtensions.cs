using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Components;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Hosting;
using Bazaar.Server.Http;
using Bazaar.Server.Jobs;
using Bazaar.Server.Souls.Retirement;
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
        services.AddBazaarRecoveryHook(configuration);
        services.AddBazaarSecrets();
        services.AddBazaarProviders(configuration);
        services.AddBazaarHostWire(configuration);
        services.AddBazaarSync(configuration);
        services.AddBazaarAgentSafety();
        services.AddBazaarRetirementLedger();
        services.AddBazaarGateway();
        services.AddBazaarJobs();

        // Bazaar Components (docs/design/BAZAAR-COMPONENTS.md). Each entry is one component; the kernel orders and validates them.
        // Registered before the agent responder, so the GC component's hosted services (GC log, wire) still start before it.
        services.AddBazaarComponents(configuration, HostComponents.All);
        FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(configuration);
        services.AddBazaarAgentResponder(configuration);
        services.AddBazaarHttpSurface(configuration);
        return services;
    }
}
