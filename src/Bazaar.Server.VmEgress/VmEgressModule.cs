using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmEgress.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmEgress;

/// <summary>
/// The egress module's composition (doc 16 WP9). The host calls this once. Defaults fail closed: with no enforcer registered the
/// policy throws instead of claiming default-deny, and with no approval gate every allowlist growth is refused. The host
/// registers <see cref="IAuditLog"/>, and replaces the store, enforcer and gate with real ones.
/// </summary>
public static class VmEgressModule
{
    /// <summary>Registers the module. Register real <see cref="IEgressEnforcer"/>, <see cref="IEgressApprovalGate"/> and <see cref="IEgressAllowlistStore"/> before this call.</summary>
    public static IServiceCollection AddBazaarVmEgress(this IServiceCollection services, Action<VmEgressOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new VmEgressOptionsBuilder();
        configure?.Invoke(builder);
        var options = builder.Build();
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEgressAllowlistStore, InMemoryEgressAllowlistStore>();
        services.TryAddSingleton<IEgressEnforcer, UnavailableEgressEnforcer>();
        services.TryAddSingleton<IEgressApprovalGate, DenyAllEgressApprovalGate>();
        services.TryAddSingleton(sp => new EgressAuditor(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<VmEgressOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new EgressProxyHost(sp.GetRequiredService<VmEgressOptions>(), sp.GetRequiredService<EgressAuditor>(), sp.GetServices<IProxyRequestFilter>()));
        services.TryAddSingleton<IEgressProxyHost>(sp => sp.GetRequiredService<EgressProxyHost>());
        services.TryAddSingleton<IEgressPolicy>(sp => new EgressPolicyService(
            sp.GetRequiredService<IEgressAllowlistStore>(),
            sp.GetRequiredService<IEgressEnforcer>(),
            sp.GetRequiredService<IEgressProxyHost>(),
            sp.GetRequiredService<IEgressApprovalGate>(),
            sp.GetRequiredService<EgressAuditor>(),
            sp.GetRequiredService<VmEgressOptions>()));
        return services;
    }
}

/// <summary>Mutable builder for <see cref="VmEgressOptions"/> used by <see cref="VmEgressModule.AddBazaarVmEgress"/>.</summary>
public sealed class VmEgressOptionsBuilder
{
    /// <summary><c>security.egress.default_allowlist</c>.</summary>
    public List<string> DefaultAllowlist { get; } = [];

    /// <summary><c>vm.egress.allowlist_change_approval</c> tier. Values below 2 act as 2.</summary>
    public int AllowlistChangeApprovalTier { get; set; } = VmEgressOptions.ApprovalTierFloor;

    /// <summary>Other limits start from the shipped defaults; set them with a <c>with</c> expression on the built options through <see cref="Tune"/>.</summary>
    public Func<VmEgressOptions, VmEgressOptions>? Tune { get; set; }

    internal VmEgressOptions Build()
    {
        var options = new VmEgressOptions
        {
            DefaultAllowlist = [.. DefaultAllowlist],
            AllowlistChangeApprovalTier = AllowlistChangeApprovalTier,
        };
        return Tune?.Invoke(options) ?? options;
    }
}
