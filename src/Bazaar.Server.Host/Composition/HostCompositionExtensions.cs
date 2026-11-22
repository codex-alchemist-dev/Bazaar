using Bazaar.Components.ManagedPostgres;
using Bazaar.Components.ManagedPostgres.Secrets;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Recovery;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Host.Recovery;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Hosting;
using Bazaar.Server.LoopGuard;
using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Linux;
using Bazaar.Server.Providers.Windows;
using Bazaar.Server.Providers.Windows.Postgres;
using Bazaar.Server.PsychWard;
using Bazaar.Server.Secrets;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Souls;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Composition;

/// <summary>One <c>AddXxx</c> per module. <c>Program.cs</c> only calls these; HOST1 is the only place modules are wired.</summary>
public static class HostCompositionExtensions
{
    /// <summary>Startup sequence, instance lock, readiness checks and the status listener options (doc 03 steps 3 to 7, WP7).</summary>
    public static IServiceCollection AddBazaarHostRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<BazaarStatusOptions>().Bind(configuration.GetSection(BazaarStatusOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BazaarStatusOptions>, BazaarStatusOptionsValidator>();
        services.AddOptions<BazaarReadinessOptions>().Bind(configuration.GetSection(BazaarReadinessOptions.SectionName));
        services.TryAddSingleton<HostState>();
        services.TryAddSingleton<HostExitCode>();
        // Managed PostgreSQL (packaging.md P5). External mode is the default and leaves the configured secret reference in charge.
        // Windows keeps the managed superuser password under DPAPI. The component's file store refuses to run there.
        if (OperatingSystem.IsWindows())
        {
            var passwordPath = (configuration.GetSection(ManagedPostgresOptions.SectionName).Get<ManagedPostgresOptions>() ?? new ManagedPostgresOptions())
                .ResolvedPasswordStorePath;
            services.TryAddSingleton<IManagedPasswordStore>(new DpapiManagedPasswordStore(passwordPath));
        }

        services.AddBazaarManagedPostgres(configuration);
        services.TryAddSingleton<DatabaseConnection>();
        // Module repositories take the shared data source; resolving it blocks once on the bootstrap secret read.
        services.TryAddSingleton(sp => sp.GetRequiredService<DatabaseConnection>().GetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());
        services.TryAddSingleton<IInstanceLock, PostgresInstanceLock>();
        services.TryAddSingleton<ISchemaGate, MigrationSchemaGate>();
        services.AddSingleton<IReadinessCheck, DatabaseReadinessCheck>();
        services.AddSingleton<IReadinessCheck, InstanceLockReadinessCheck>();
        services.AddSingleton<IReadinessCheck, ProviderReadinessCheck>();
        services.AddHostedService<StartupSequence>();
        return services;
    }

    /// <summary>
    /// Doc 03 WP5: startup lease recovery sweep (step 6) and shutdown checkpoint. Call after <see cref="AddBazaarHostRuntime"/> so the hosted
    /// service starts after <c>StartupSequence</c>. TODO(component): move into the component model once PR #125 merges.
    /// </summary>
    public static IServiceCollection AddBazaarRecoveryHook(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RecoveryHookOptions>().Bind(configuration.GetSection(RecoveryHookOptions.SectionName));
        services.TryAddSingleton<AgentLeaseRecovery>();
        services.TryAddSingleton<ILeaseRecoveryRunner, LeaseRecoveryRunner>();
        services.TryAddSingleton<RecoveryHookState>();
        services.AddSingleton<IReadinessCheck, RecoverySweepReadinessCheck>();
        services.AddHostedService<RecoveryHostedService>();
        return services;
    }

    /// <summary>Bootstrap secret references: <c>env:</c> and <c>file:</c> everywhere, plus the OS resolver (<c>dpapi:</c> or <c>systemd-creds:</c>).</summary>
    public static IServiceCollection AddBazaarSecrets(this IServiceCollection services)
    {
        services.AddSingleton<ISecretResolver>(_ =>
        {
            var resolvers = new List<ISecretResolver> { new BootstrapSecretResolver() };
            if (OperatingSystem.IsWindows())
            {
                resolvers.Add(new DpapiSecretResolver());
            }
            else if (OperatingSystem.IsLinux())
            {
                resolvers.Add(new SystemdCredsSecretResolver());
            }

            return new CompositeSecretResolver(resolvers);
        });
        return services;
    }

    /// <summary>
    /// Registers the OS service host providers and the capability report (doc 03, "Provider modules"). Providers are plain modules: nothing
    /// here chooses among them, and every provider registered in DI, from this method or from a module, is listed in the report with its own
    /// status. A consuming module decides what to do with several.
    /// </summary>
    public static IServiceCollection AddBazaarProviders(this IServiceCollection services)
    {
        foreach (var serviceHost in WindowsProviders.ServiceHostCandidates().Concat(LinuxProviders.ServiceHostCandidates()))
        {
            services.AddSingleton<IServiceHostProvider>(serviceHost);
        }

        if (OperatingSystem.IsWindows())
        {
            // Built when first resolved, so a host with no data root configured fails only when something asks for the VM provider.
            services.AddSingleton<IVmProvider>(sp =>
                WindowsProviders.VmCandidates(sp.GetRequiredService<IOptions<BazaarHostOptions>>().Value.DataRoot).Single());
        }

        services.TryAddSingleton(sp => new ProviderCapabilityReport(
            sp.GetServices<IProvider>()
                .Concat(sp.GetServices<IServiceHostProvider>())
                .Concat(sp.GetServices<IVmProvider>())
                .Concat(sp.GetServices<ICacheProvider>())
                .Concat(sp.GetServices<ISearchProvider>())
                .Distinct()));
        return services;
    }

    /// <summary>
    /// Wire connection manager and codec, with a fail-closed authenticator until the Identity Service supplies the real one.
    /// The socket endpoint (<c>UseBazaarWire</c>) is not mapped yet: it waits for authentication.
    /// </summary>
    public static IServiceCollection AddBazaarHostWire(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBazaarWire(configuration);
        services.TryAddSingleton<IConnectionAuthenticator, DenyAllAuthenticator>();
        return services;
    }

    /// <summary>Taint policy, loop guard and usage accounting. In-memory stores stand in until the database repositories (GW3, GW8) are wired.</summary>
    public static IServiceCollection AddBazaarAgentSafety(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TaintPolicy.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<LoopGuardOptions>()
            .BindConfiguration(LoopGuardOptions.SectionName)
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }, "Loop guard limits are invalid.");
        services.TryAddSingleton<ILoopGuard>(sp => new LoopGuardFacade(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<LoopGuardOptions>>().Value));
        services.TryAddSingleton<IUsageStore, InMemoryUsageStore>();
        services.TryAddSingleton<UsageRecorder>();
        services.AddBazaarSoulBindings();
        services.AddBazaarPsychWard();
        services.AddBazaarLoopTripHandling();
        return services;
    }
}
