using Cdax.Core;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDisplay.GuestChannel;

/// <summary>
/// Joins the display module's guest source to the guest channel directory as a Bazaar Component (docs/design/59 section 5 rule 4; PROPOSED).
/// It registers <see cref="GuestChannelDisplayExec"/> as an <see cref="IDisplayGuestExec"/>. The channel directory is resolved when the
/// adapter is first built and is optional: without one every call fails. Loading starts no process and opens no socket.
/// </summary>
public sealed class VmDisplayGuestChannelComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-display-guest-channel",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["vm-display"],
        Provides: ["vm-display:guest-channel"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(GuestChannelDisplayOptions.Bind(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDisplayGuestExec, GuestChannelDisplayExec>());
    }
}
