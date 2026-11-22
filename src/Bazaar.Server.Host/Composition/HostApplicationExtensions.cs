using Bazaar.Server.Security;
using Bazaar.Core;
using Bazaar.Erasure;
using Bazaar.Fun;
using Bazaar.Sanitization;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.AgentSafety;
using Bazaar.Server.Blobs;
using Bazaar.Server.Hive.Approvals;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Host.Http;
using Bazaar.Server.Hosting;
using Bazaar.Server.Http;
using Bazaar.Server.AccountApproval.Postgres;
using Bazaar.Server.Identity.Postgres;
using Bazaar.Server.Jobs;
using Bazaar.Server.Portability.Postgres;
using Bazaar.Server.Presence.Postgres;
using Bazaar.Server.Sanitization;
using Bazaar.Server.Security.Credentials;
using Bazaar.Server.Souls.Retirement;
using Bazaar.Server.VmDensity;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.VmDensity.HostProbes;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddBazaarProviders();
        services.AddBazaarHostWire(configuration);
        services.AddBazaarHostWireDiscovery();
        services.AddBazaarHostInterrupts();
        services.AddBazaarSync(configuration);
        services.AddBazaarAgentSafety(configuration);
        services.AddBazaarPostgresCredentialLeases();
        services.AddSecretStoreLeasedCredentialValues();
        services.AddLeasedCredentialMatcher();
        services.AddBazaarVms(configuration);
        services.AddBazaarVmSecurity(configuration);
        services.AddBazaarAgentSafetyModule(configuration);
        services.AddBazaarHiveGrantApprovals();
        services.AddBazaarOwnerApprovals();
        services.AddBazaarRetirementLedger();
        services.AddBazaarGateway();
        services.AddBazaarSecretStore(configuration);
        services.AddLeaseMatcherSecretGrant();
        services.AddBazaarJobs();
        services.AddBazaarPortabilityPostgres();
        services.AddBazaarPresencePostgres();
        services.AddBazaarAccountApprovalPostgres();
        services.AddBazaarLegalHoldReader();

        // Coding harness (doc 17). Off unless Coding:Harness:Enabled is true. When on, the ports with no production source yet
        // (repo directory, work-share layout, worktree allocator, git identity, parent-turn directory, agent state, push session
        // records) must be bound, or the host fails at start naming the port.
        services.AddBazaarCodingHarness(configuration);

        // The fun systems depend on the sanitizer abstractions only (Bazaar.Server.Sanitization.Abstractions). The host supplies the
        // implementation, and only when the fun-systems component is enabled, as before the split.
        if (BazaarComponentRegistration.IsEnabled(configuration, FunComponent.ManifestDefinition.Id))
        {
            services.TryAddSingleton<ITextSanitizer, TextSanitizer>();
        }

        // The fun systems depend on the sanitizer abstractions only (Bazaar.Server.Sanitization.Abstractions). The host supplies the
        // implementation, and only when the fun-systems component is enabled, as before the split.
        if (BazaarComponentRegistration.IsEnabled(configuration, FunComponent.ManifestDefinition.Id))
        {
            services.TryAddSingleton<ITextSanitizer, TextSanitizer>();
        }

        // The density governor's host memory port (doc 59 section 8). The governor still admits every start until the host also
        // supplies the VM directory and VM control ports, which have no implementation yet.
        if (BazaarComponentRegistration.IsEnabled(configuration, VmDensityComponent.ManifestDefinition.Id)
            && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
        {
            services.TryAddSingleton<IHostMemoryProbe>(_ => HostMemoryProbes.CreateForCurrentOs());
        }

        // Bazaar Components (docs/design/BAZAAR-COMPONENTS.md). Each entry is one component; the kernel orders and validates them.
        // Registered before the agent responder, so the GC component's hosted services (GC log, wire) still start before it.
        // The setup component shows its one-time bootstrap code through this; the setup modules themselves use no console.
        services.TryAddSingleton<Bazaar.Server.Setup.Host.IBootstrapCodeAnnouncer, ConsoleBootstrapCodeAnnouncer>();
        // The DM access flow reads gc.dm_access_approvers live from the settings store. Registered first, so the gc-metadata component keeps it.
        services.TryAddSingleton<Bazaar.Server.GcMetadata.IDmAccessOptionsSource, Components.GcMetadata.SettingsDmAccessOptionsSource>();
        services.AddBazaarComponents(configuration, HostComponents.All);
        // Puts each packaged module's approved permission set in force once the components are up (doc 48 section 1).
        services.AddBazaarModuleHostWiring(HostComponents.All.Select(c => c.Manifest));
        services.AddBazaarBackupOffsite();
        // The Council's Vault decision writer (C24). It resolves the Vault writer on first use.
        services.AddBazaarCouncilDecisionVault();
        services.AddBazaarCouncilHostAdapters();
        FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(configuration);
        AnthropicDirectComponent.RequireComponentWhenGatewayIsOn(configuration);
        services.AddBazaarAgentResponder(configuration);
        // Pairing and refresh services (doc 32). Their routes are mapped by MapBazaarHttpSurface in Program.cs.
        services.AddBazaarRemoteAuth(configuration);
        services.AddBazaarBlobs(configuration);
        services.AddBazaarHttpSurface(configuration);
        services.AddBazaarCompanyReadOptions();
        services.AddBazaarArchive();
        return services;
    }
}
