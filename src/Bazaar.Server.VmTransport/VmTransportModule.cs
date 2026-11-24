using Bazaar.Contracts.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmTransport;

/// <summary>Identity and host requirements of the guest transport module (doc 16 WP7).</summary>
public static class VmTransportModule
{
    /// <summary>The module name used in settings and the module list.</summary>
    public const string Name = "vm_transport";

    /// <summary>
    /// The module loads on Windows and Linux hosts. The listener is chosen by OS at registration: Hyper-V sockets on Windows, vsock on Linux.
    /// Whether the host can really run it (Hyper-V enabled, vsock loaded) is a runtime fact the listener reports through its own IsSupported.
    /// </summary>
    public static HostRequirements Requirements { get; } = new(
        HostRequirements.CurrentSchemaVersion,
        ["windows", "linux"],
        ["x64", "arm64"],
        []);
}

/// <summary>Opens guest channels: waits for a VM's connection on a listener, then checks its Hello.</summary>
public sealed class GuestChannelFactory
{
    private readonly GuestChannelOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the factory.</summary>
    public GuestChannelFactory(GuestChannelOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
    }

    /// <summary>
    /// Waits for the guest at <paramref name="peerVmId"/> (the VM's backend ref) to connect, then opens the channel once its Hello matches
    /// <paramref name="expected"/>. A guest whose Hello does not match is closed and <see cref="GuestHandshakeException"/> is thrown.
    /// </summary>
    public async Task<GuestChannel> OpenAsync(IGuestConnectionListener listener, string peerVmId, GuestHandshake expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var connection = await listener.AcceptAsync(peerVmId, ct);
        return await GuestChannel.OpenAsync(connection, expected, _options, _time, ct);
    }
}

/// <summary>Registration of the guest transport module.</summary>
public static class VmTransportRegistration
{
    /// <summary>
    /// Registers the channel factory with default limits and the listener for this OS: Hyper-V sockets on Windows, vsock on Linux.
    /// Neither binds a socket until the first wait. Every registration is TryAdd, so a host or a test can substitute its own before this call.
    /// On any other OS no listener is registered. Also registers the channel registry, directory and registrar, and the fail-closed
    /// <see cref="IAgentVmLookup"/> default.
    /// </summary>
    public static IServiceCollection AddBazaarVmTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new GuestChannelOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<GuestChannelFactory>();

        // The channel directory (doc 16 "Channel directory", PROPOSED). The VM store port defaults to "no VMs", so the directory serves nothing
        // until the host supplies IAgentVmLookup.
        services.TryAddSingleton<IAgentVmLookup, NoAgentVmLookup>();
        services.TryAddSingleton<GuestChannelRegistry>();
        services.TryAddSingleton<IGuestChannelRegistry>(sp => sp.GetRequiredService<GuestChannelRegistry>());
        services.TryAddSingleton<IGuestChannelDirectory>(sp => new GuestChannelDirectory(
            sp.GetRequiredService<GuestChannelRegistry>(), sp.GetRequiredService<IAgentVmLookup>()));
        services.TryAddSingleton<GuestChannelRegistrar>();
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<IGuestConnectionListener>(_ => new HvSocketListener());
        }
        else if (OperatingSystem.IsLinux())
        {
            services.TryAddSingleton<IGuestConnectionListener>(_ => new VsockListener());
        }

        return services;
    }
}
