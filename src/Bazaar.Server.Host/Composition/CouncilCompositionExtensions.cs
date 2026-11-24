using Bazaar.Server.Council.Data;
using Bazaar.Server.Council.Escalation;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Host.CouncilAdapters;
using Bazaar.Server.Host.CouncilVault;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Settings;
using Bazaar.Server.Vault.Writes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The Council module's Vault adapter in the host (C24, doc 10 Recording). It sits here, not in the Council module, because
/// <see cref="VaultWriter"/> is still inside <c>Bazaar.Server.csproj</c> and no module may reference that project (doc 40).
/// </summary>
public static class CouncilCompositionExtensions
{
    /// <summary>
    /// Registers <see cref="ICouncilDecisionWriter"/> as the Vault decision writer. The <see cref="VaultWriter"/> is resolved when a
    /// decision is first recorded, not when the port is requested, so the Council service resolves on a host with no Vault
    /// repository configured (the Vault is a soft dependency of the Council component); recording a decision there fails with the
    /// container's missing-service message.
    /// </summary>
    public static IServiceCollection AddBazaarCouncilDecisionVault(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICouncilDecisionWriter>(sp =>
            new CouncilDecisionVaultAdapter((draft, ct) => sp.GetRequiredService<VaultWriter>().CreateDecisionAsync(draft, ct)));
        return services;
    }

    /// <summary>
    /// Registers the host adapters for the Council ports that need Server or GC types (doc 40): <see cref="ICouncilGcEvents"/> over
    /// <see cref="IGcLogWriter"/>, <see cref="IGcMembership"/> over <c>gc_member_current</c>, and <see cref="ICouncilReasoningCipher"/>
    /// over <see cref="PersonKeyStore"/>, and <see cref="ICouncilOwnerNotifier"/> over the owner approvals queue (an owner card per escalated vote and re-notify round; doc 10 Escalation). Each resolves its dependencies when first requested, so a host without a KEK provider fails
    /// at the first ballot, not at start. <c>TryAdd</c>, so a host or test can replace any of them.
    /// </summary>
    public static IServiceCollection AddBazaarCouncilHostAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICouncilGcEvents>(sp => new CouncilGcEventsAdapter(sp.GetRequiredService<IGcLogWriter>()));
        services.TryAddSingleton<IGcMembership>(sp => new PgGcMembershipAdapter(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ICouncilReasoningCipher>(sp => new PersonKeyCouncilReasoningCipher(
            new PersonKeyStore(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IKekProvider>())));
        services.TryAddSingleton<ICouncilOwnerNotifier>(sp => new OwnerQueueCouncilOwnerNotifier(
            sp.GetRequiredService<IOwnerApprovalEnqueuer>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<CouncilEscalationOptions>() ?? new CouncilEscalationOptions(),
            sp.GetService<ICouncilEscalationRenotifySource>()));
        return services;
    }

    /// <summary>
    /// Wires the <c>council.*</c> settings into the Council (doc 10, Settings; doc 40). When an <see cref="ISettingStore"/> is registered,
    /// <see cref="CouncilOptions"/> is replaced by the value <see cref="SettingsCouncilOptionsProvider"/> reads when the Council service
    /// is first resolved (a change applies at the next restart), and <see cref="ICouncilDissentShareSource"/> reads
    /// <c>council.followup_dissent_share</c> at each tally. Without a settings store the component's defaults stand. Call it after the
    /// components are added, because the Council component registers the default <see cref="CouncilOptions"/> first.
    /// </summary>
    public static IServiceCollection AddBazaarCouncilSettings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(d => d.ServiceType == typeof(ISettingStore)))
        {
            return services;
        }

        services.TryAddSingleton(sp => new SettingsCouncilOptionsProvider(sp.GetRequiredService<ISettingStore>()));
        services.Replace(ServiceDescriptor.Singleton(sp =>
            sp.GetRequiredService<SettingsCouncilOptionsProvider>().LoadAsync().GetAwaiter().GetResult()));
        // The service builds on first use, so the settings read waits until after the data volume guard (doc 63 section 5).
        if (services.Any(d => d.ServiceType == typeof(ICouncilService) && d.ImplementationType == typeof(CouncilService)))
        {
            services.Replace(ServiceDescriptor.Singleton<ICouncilService>(sp =>
                new LazyCouncilService(() => ActivatorUtilities.CreateInstance<CouncilService>(sp))));
        }

        services.TryAddSingleton<ICouncilDissentShareSource>(sp => new SettingsCouncilDissentShareSource(sp.GetRequiredService<ISettingStore>()));
        return services;
    }
}
