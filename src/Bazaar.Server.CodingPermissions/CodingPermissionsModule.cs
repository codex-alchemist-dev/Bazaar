using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CodingPermissions;

/// <summary>Registration for the coding permission gate.</summary>
public static class CodingPermissionsModule
{
    /// <summary>
    /// Registers <see cref="ToolPermissionGate"/>. The ports (generic policy over ToolPolicyEngine, trace, freeze)
    /// come from the host; none is defaulted, so a missing one fails resolution instead of opening the gate.
    /// </summary>
    public static IServiceCollection AddBazaarCodingPermissions(this IServiceCollection services, CodingPermissionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var resolved = options ?? new CodingPermissionOptions();
        resolved.Validate();
        services.TryAddSingleton(resolved);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IToolPermissionGate>(sp => new ToolPermissionGate(
            sp.GetRequiredService<IGenericToolPolicy>(),
            sp.GetRequiredService<IToolCallTrace>(),
            sp.GetRequiredService<IAgentFreeze>(),
            sp.GetRequiredService<CodingPermissionOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IToolCallRecorder>(sp => new ToolCallOutcomeRecorder(
            sp.GetRequiredService<IToolPermissionGate>() as IIdentifiedToolPermissionGate
                ?? throw new InvalidOperationException("The registered permission gate does not report row ids, so outcomes cannot be recorded."),
            sp.GetRequiredService<IToolCallTrace>()));
        return services;
    }
}
