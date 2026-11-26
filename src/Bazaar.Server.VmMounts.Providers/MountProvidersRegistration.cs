using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Canaries;
using Bazaar.Server.VmMounts.Providers.Guest;
using Bazaar.Server.VmMounts.Providers.Local;
using Bazaar.Server.VmMounts.Providers.Smb;
using Bazaar.Server.VmTransport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.VmMounts.Providers;

/// <summary>
/// Registration of the host-side ports of the scoped-mount module (doc 16 WP8, PROPOSED): <see cref="IMountProvider"/> (SMB on a Windows
/// host, the directory provider elsewhere), <see cref="IMountGuestChannel"/> (over the transport's guest channel) and
/// <see cref="ICanarySink"/> (audit plus containment). Every registration is <c>TryAdd</c>, so a host override registered earlier wins.
/// </summary>
public static class MountProvidersRegistration
{
    /// <summary>
    /// Registers the three ports and what they need. Fail-closed defaults stand in for the parts the host must still supply:
    /// <see cref="IVmShareIdentity"/>, <see cref="IVmShareEndpoint"/> and <see cref="IGuestChannelProvider"/> refuse or answer "none", so a
    /// mount that needs one fails visibly instead of running without it. Call before <c>AddBazaarVmMounts</c>.
    /// </summary>
    public static IServiceCollection AddBazaarVmMountProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IHostCommandRunner, ProcessHostCommandRunner>();
        services.TryAddSingleton<IVmShareIdentity, UnavailableVmShareIdentity>();
        services.TryAddSingleton<IVmShareEndpoint, UnavailableVmShareEndpoint>();
        // Over the transport's channel directory when the host composed one (order-independent: decided at first resolve), else "no channel".
        services.TryAddSingleton<IGuestChannelProvider>(sp => sp.GetService<IGuestChannelDirectory>() is { } directory
            ? new DirectoryGuestChannelProvider(directory)
            : new UnavailableGuestChannelProvider());
        services.TryAddSingleton(new CanarySinkOptions());

        // The concrete providers are registered by factory (their constructors take internal types) and both are lazy in the sense that
        // neither touches the file system or the host until its first use.
        services.TryAddSingleton(sp => new SmbMountProvider(
            sp.GetRequiredService<VmMountOptions>(),
            sp.GetRequiredService<IHostCommandRunner>(),
            sp.GetRequiredService<IVmShareIdentity>(),
            sp.GetRequiredService<IVmShareEndpoint>(),
            sp.GetRequiredService<ILogger<SmbMountProvider>>()));
        services.TryAddSingleton<DirectoryMountProvider>();

        // The OS choice is made here and nowhere else.
        services.TryAddSingleton<IMountProvider>(sp => MountProviderFactory.Create(sp, OperatingSystem.IsWindows()));

        services.TryAddSingleton<IMountGuestChannel>(sp => new GuestChannelMountAdapter(
            sp.GetRequiredService<IGuestChannelProvider>(),
            sp.GetRequiredService<IScopedMountStore>(),
            sp.GetRequiredService<IGuestMountLayoutProvider>(),
            sp.GetRequiredService<VmMountOptions>()));

        services.TryAddSingleton<ContainingCanarySink>(sp => new ContainingCanarySink(
            sp.GetRequiredService<IAuditLog>(),
            sp,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<CanarySinkOptions>(),
            sp.GetRequiredService<ILogger<ContainingCanarySink>>()));
        services.TryAddSingleton<ICanarySink>(sp => sp.GetRequiredService<ContainingCanarySink>());

        services.AddHostedService(sp => new CanaryTripRetryService(sp.GetRequiredService<ContainingCanarySink>(), sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => new SmbShareReconcileService(
            sp, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SmbShareReconcileService>>()));
        return services;
    }
}

/// <summary>Chooses the mount provider for the host OS. The only place in the module that looks at the OS.</summary>
internal static class MountProviderFactory
{
    public static IMountProvider Create(IServiceProvider services, bool isWindows) =>
        isWindows ? services.GetRequiredService<SmbMountProvider>() : services.GetRequiredService<DirectoryMountProvider>();
}
