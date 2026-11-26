using Bazaar.Core;
using Bazaar.Erasure;
using Bazaar.Fun;
using Bazaar.Sanitization;
using Bazaar.Server.AccountApproval.Postgres;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.AgentSafety;
using Bazaar.Server.Blobs;
using Bazaar.Server.Constellation;
using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Constellation.Memory.Context;
using Bazaar.Server.Constellation.Memory.ReadLog;
using Bazaar.Server.DevSandbox;
using Bazaar.Server.DevSandbox.Linux;
using Bazaar.Server.DevSandbox.Wasm.Wasmtime;
using Bazaar.Server.DevSandbox.Windows;
using Bazaar.Server.Diagnostics;
using Bazaar.Server.Diagnostics.Adapters;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Hive.Approvals;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Host.Display;
using Bazaar.Server.Host.GeneticMemory;
using Bazaar.Server.Host.Http;
using Bazaar.Server.Host.Wire;
using Bazaar.Server.Hosting;
using Bazaar.Server.Http;
using Bazaar.Server.Identity.Postgres;
using Bazaar.Server.Jobs;
using Bazaar.Server.Kodo.Postgres;
using Bazaar.Server.Portability.Postgres;
using Bazaar.Server.Presence.Postgres;
using Bazaar.Server.Sanitization;
using Bazaar.Server.CompanyBuild.Postgres;
using Bazaar.Server.SignIn.Postgres;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Credentials;
using Bazaar.Server.Souls.Retirement;
using Bazaar.Server.VmDensity;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.VmDensity.HostProbes;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

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
        services.AddSingleton<Bazaar.Server.SignIn.Passkeys.IEnrolmentCaller, Bazaar.Server.Host.Auth.HostEnrolmentCaller>();
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
        services.AddBazaarGeneticMemoryHost();
        services.AddBazaarPostgresCredentialLeases();
        services.AddSecretStoreLeasedCredentialValues();
        services.AddLeasedCredentialMatcher();
        services.AddBazaarVms(configuration);
        // Developer sandbox (doc 48 section 2.1). The core is always added; each OS provider and engine adds itself only when the launch
        // plan or module source it needs is registered before this call, so with none every sandbox run is refused.
        services.AddBazaarDevSandbox();
        services.AddBazaarDevSandboxLinux();
        services.AddBazaarDevSandboxWindows();
        services.AddBazaarDevSandboxWasmtime();
        services.AddBazaarVmSecurity(configuration);
        services.AddBazaarAgentSafetyModule(configuration);
        services.TryAddSingleton<PersonKeyStore>();
        services.TryAddSingleton<ICrashKeyStore, PersonKeyStoreCrashKeys>();
        services.AddBazaarDiagnosticsModule(configuration);
        services.AddBazaarHiveGrantApprovals();
        services.AddBazaarOwnerApprovals();
        // The owner queue as the web admin approvals section (doc 51 section 5).
        services.AddBazaarAdminApprovalSource();
        services.AddBazaarRetirementLedger();
        services.AddBazaarGateway();
        services.AddBazaarSecretStore(configuration);
        services.AddLeaseMatcherSecretGrant();
        services.AddBazaarJobs();
        services.AddBazaarPortabilityPostgres();
        services.AddBazaarPresencePostgres();
        services.AddBazaarSignInPostgres();
        services.AddBazaarKodoPostgres();
        services.AddBazaarAccountApprovalPostgres();
        services.AddBazaarLegalHoldReader();

        // Coding harness (doc 17). Off unless Coding:Harness:Enabled is true. When on, the ports with no production source yet
        // (repo directory, work-share layout, worktree allocator, git identity, parent-turn directory, agent state, push session
        // records) must be bound, or the host fails at start naming the port.
        services.AddBazaarCodingHarness(configuration);
        services.AddBazaarScreenToolPolicy();

        // The fun systems depend on the sanitizer abstractions only (Bazaar.Server.Sanitization.Abstractions). The host supplies the
        // implementation, and only when the fun-systems component is enabled, as before the split.
        if (BazaarComponentRegistration.IsEnabled(configuration, FunComponent.ManifestDefinition.Id)
            || BazaarComponentRegistration.IsEnabled(configuration, Bazaar.Lucky.LuckyComponent.ManifestDefinition.Id))
        {
            services.TryAddSingleton<ITextSanitizer, TextSanitizer>();
        }

        // Lucky runs (docs/design/26-fun-systems.md, section 6): the host supplies the run store and the side-effect sink.
        if (BazaarComponentRegistration.IsEnabled(configuration, Bazaar.Lucky.LuckyComponent.ManifestDefinition.Id))
        {
            services.TryAddSingleton<Bazaar.Lucky.ILuckyRunStore>(sp =>
                new Bazaar.Server.Host.Lucky.PgLuckyRunStore(sp.GetRequiredService<NpgsqlDataSource>()));
            services.TryAddSingleton<Bazaar.Lucky.ILuckyOptInStore>(sp =>
                new Bazaar.Server.Host.Lucky.PgLuckyOptInStore(sp.GetRequiredService<NpgsqlDataSource>()));
            services.TryAddSingleton<Bazaar.Lucky.ILuckyCandidateSource>(sp =>
                new Bazaar.Server.Host.Lucky.PgLuckyCandidateSource(sp.GetRequiredService<NpgsqlDataSource>()));
            services.TryAddSingleton<Bazaar.Lucky.ILuckyRunSink>(sp =>
            {
                var ds = sp.GetRequiredService<NpgsqlDataSource>();
                return new Bazaar.Server.Host.Lucky.PgLuckyRunSink(
                    ds,
                    new Bazaar.Server.Gc.Create.GcCreateService(ds),
                    sp.GetRequiredService<Bazaar.Server.Gc.Write.IGcLogWriter>(),
                    sp.GetRequiredService<Bazaar.Server.Gc.Body.IGcBodyCodec>(),
                    sp.GetRequiredService<Bazaar.Server.Agents.Data.InboxRepository>(),
                    sp.GetRequiredService<Bazaar.RyuseiFuda.Kanban.TaskService>());
            });
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
        services.AddBazaarEmbeddingsHost(configuration);
        services.AddBazaarGcForkTarget();
        services.AddBazaarReplayWire();

        // The memory graph (Bazaar.Server.Constellation) is wired here, not in a component: the Constellation component cannot
        // reference its own module project, and the graph sink depends on the Memory module. Only when Constellation is enabled.
        if (BazaarComponentRegistration.IsEnabled(configuration, ConstellationComponent.ManifestDefinition.Id))
        {
            services.AddMemoryGraphProjection();
        }

        // After the components: the Valkey supervisor is one of them, and the deferred cache selection must start after it (doc 40 section 4c.4 item 8).
        services.AddBazaarCacheSelection();

            // The retrieval operation over the graph (constellation-memory-graph.md, "Retrieval operation"): memory.context.* settings,
            // vector seeds through the Vault query embedder when one is registered, the service and the memory.context wire command.
            Guid? constellationVault = Guid.TryParse(configuration[ConstellationComponent.VaultIdKey], out var constellationVaultId) ? constellationVaultId : null;
            services.AddMemoryContext(new MemoryContextHostOptions(constellationVault));

            // The durable owner-read log (constellation/0003): owner reads of another agent's memory are recorded and hash-chained,
            // and a read that cannot be logged is refused.
            services.AddMemoryReadLog();
        }

        // Puts each packaged module's approved permission set in force once the components are up (doc 48 section 1).
        services.AddBazaarModuleHostWiring(HostComponents.All.Select(c => c.Manifest));
        // Module selection, the rebuild queue and the catalogue snapshot the TUI reads (doc 37 section 4a, PROPOSED).
        services.AddBazaarModuleSelectionPostgres(HostComponents.All.Select(c => c.Manifest));
        services.AddBazaarBackupOffsite();
        // The community draft seams: policy source, GitHub adapter and its bridge, owner keys, action store (doc 36 WP7 and WP8).
        services.AddBazaarCommunityHost();
        // The Council's Vault decision writer (C24). It resolves the Vault writer on first use.
        services.AddBazaarCouncilDecisionVault();
        services.AddBazaarCouncilHostAdapters();
        services.AddBazaarCouncilSettings();
        FreeLlmApiComponent.RequireComponentWhenGatewayIsOn(configuration);
        AnthropicDirectComponent.RequireComponentWhenGatewayIsOn(configuration);
        services.AddBazaarAgentResponder(configuration);
        services.AddBazaarHostAthanor(configuration);
        // Pairing and refresh services (doc 32). Their routes are mapped by MapBazaarHttpSurface in Program.cs.
        services.AddBazaarRemoteAuth(configuration);
        services.AddBazaarBlobs(configuration);
        services.AddBazaarHttpSurface(configuration);
        services.AddBazaarCompanyReadOptions();
        services.AddBazaarArchive();
        return services;
    }
}
