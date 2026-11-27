using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DataVolume.Linux;

/// <summary>
/// The Linux data volume module (doc 63 section 3). It adds the <c>luks2</c> status reader and the privileged operator to the
/// data volume module, and only on Linux; on any other host it registers nothing.
/// </summary>
public sealed class LinuxDataVolumeComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "data-volume-linux",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["data-volume"],
        Provides: ["storage:data-volume:luks2"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        if (!OperatingSystem.IsLinux())
            return;

        services.AddSingleton<IDataVolumeProvider>(_ => new Luks2DataVolumeProvider());
        services.AddSingleton<IDataVolumeOperator>(_ => new Luks2DataVolumeOperator());
    }
}
