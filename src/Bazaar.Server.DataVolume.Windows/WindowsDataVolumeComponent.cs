using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// The Windows data volume provider module (doc 63 section 3, <c>bitlocker-vhdx</c>). On Windows it registers the unprivileged
/// provider the start guard uses and the privileged operator the volume unit and setup use; on any other OS it registers nothing.
/// The Windows service host, the status pipe's hosting and the <c>volume</c> subcommands belong to the host.
/// </summary>
public sealed class WindowsDataVolumeComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "data-volume-windows",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["data-volume"],
        Provides: ["storage:data-volume:provider:bitlocker-vhdx"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        if (!OperatingSystem.IsWindows())
            return;

        services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        services.AddSingleton<IBitLockerVolumes, PowerShellBitLockerVolumes>();
        services.AddSingleton<IVirtualDisks, DiskpartVirtualDisks>();
        services.AddSingleton<IMountPointReader, NativeMountPointReader>();
        services.AddSingleton<IVolumeStatusChannel>(_ => new NamedPipeVolumeStatusChannel());
        services.AddSingleton<IDataVolumeProvider>(provider => new BitLockerVhdxDataVolumeProvider(
            provider.GetRequiredService<IMountPointReader>(),
            provider.GetRequiredService<IVolumeStatusChannel>()));
        services.AddSingleton<IDataVolumeOperator>(provider => new BitLockerVhdxDataVolumeOperator(
            provider.GetRequiredService<IVirtualDisks>(),
            provider.GetRequiredService<IBitLockerVolumes>(),
            provider.GetRequiredService<IMountPointReader>(),
            provider.GetRequiredService<ICommandRunner>()));
    }
}
