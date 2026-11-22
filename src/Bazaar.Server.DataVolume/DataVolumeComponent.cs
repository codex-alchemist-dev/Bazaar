using Bazaar.Core;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DataVolume;

/// <summary>
/// The data volume module (doc 63). It registers the start guard and the <c>dev-plain</c> provider; the OS provider
/// modules add <c>bitlocker-vhdx</c> and <c>luks2</c>. Options come from host configuration because the volume must be
/// verified before the database is reachable.
/// </summary>
public sealed class DataVolumeComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "data-volume",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["identity-kek"],
        Provides: ["storage:data-volume:guard"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => ReadOptions(configuration));
        services.AddSingleton<IDataVolumeProvider, DevPlainDataVolumeProvider>();
        services.AddSingleton<IDataVolumeGuard>(provider => new DataVolumeGuard(
            provider.GetRequiredService<DataVolumeOptions>(),
            provider.GetRequiredService<IKekProvider>(),
            provider.GetServices<IDataVolumeProvider>()));
    }

    /// <summary>Reads the options. An unknown instance class is an error, never a fallback to development.</summary>
    public static DataVolumeOptions ReadOptions(IConfiguration configuration)
    {
        var instanceClass = InstanceClass.Production;
        var rawClass = configuration[DataVolumeOptions.InstanceClassKey];
        if (!string.IsNullOrWhiteSpace(rawClass) && !Enum.TryParse(rawClass, ignoreCase: true, out instanceClass))
            throw new InvalidOperationException($"{DataVolumeOptions.InstanceClassKey} must be Production or Development.");

        return new DataVolumeOptions
        {
            BootRoot = configuration[DataVolumeOptions.BootRootKey] ?? string.Empty,
            DataRoot = configuration[DataVolumeOptions.DataRootKey] ?? string.Empty,
            InstallId = configuration[DataVolumeOptions.InstallIdKey] ?? string.Empty,
            VolumeId = configuration[DataVolumeOptions.VolumeIdKey] ?? string.Empty,
            InstanceClass = instanceClass,
            IsDevelopmentEnvironment = string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase),
        };
    }
}
