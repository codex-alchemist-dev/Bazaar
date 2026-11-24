using Bazaar.Server.Interrupts;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Host side of the Interrupts system producer (doc 24 section 3): the bridge from the settings store to the module, which cannot
/// reference Bazaar.Server. The module itself is <see cref="InterruptsComponent"/>, listed in <see cref="HostComponents"/>.
/// </summary>
public static class InterruptsHostComposition
{
    /// <summary>Registers the host pieces the system producer needs. Safe when the component is disabled: nothing reads it.</summary>
    public static IServiceCollection AddBazaarHostInterrupts(this IServiceCollection services)
    {
        services.TryAddSingleton<IInterruptSystemTargetsReader, SettingStoreInterruptTargetsReader>();
        return services;
    }
}

/// <summary>Reads <c>interrupt.system_targets</c> from <see cref="ISettingStore"/>. A stored value that is invalid throws, and the producer logs and skips.</summary>
internal sealed class SettingStoreInterruptTargetsReader(ISettingStore store) : IInterruptSystemTargetsReader
{
    public Task<InterruptSystemTargets> GetAsync(CancellationToken cancellationToken = default) =>
        store.GetAsync(InterruptSettings.SystemTargets, cancellationToken: cancellationToken);
}
