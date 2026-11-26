using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Setup.Storage;

/// <summary>Registers the <c>storage.volume</c> step. The host registers the data volume guard the step verifies with.</summary>
public static class StorageSetupServiceCollectionExtensions
{
    /// <summary>Adds <see cref="StorageVolumeStep"/> as a singleton, and as an <see cref="ISetupStep"/> the host can collect.</summary>
    public static IServiceCollection AddStorageVolumeStep(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<StorageVolumeStep>();
        services.AddSingleton<ISetupStep>(provider => provider.GetRequiredService<StorageVolumeStep>());
        return services;
    }
}
