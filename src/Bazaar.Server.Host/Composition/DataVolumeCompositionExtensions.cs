using Bazaar.Server.DataVolume;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Setup.Storage;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Registers the data volume guard (doc 63 section 5) for an install that has a boot root. The rule is the one
/// <c>KekComponent</c> already follows: with <c>Bazaar:Host:BootRoot</c> the install follows doc 63, the guard runs first in
/// the start sequence and before PostgreSQL, and a refusal ends the process with exit code 78. Without a boot root the
/// install predates doc 63 and starts as before until it is migrated (<c>storage migrate</c>).
/// </summary>
public static class DataVolumeCompositionExtensions
{
    /// <summary>True when host configuration names a boot root, so the install follows doc 63.</summary>
    public static bool HasBootRoot(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[DataVolumeOptions.BootRootKey]);

    public static IServiceCollection AddBazaarDataVolume(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!HasBootRoot(configuration))
        {
            return services;
        }

        // The component adds the guard and the dev-plain provider; the OS providers come from the one per-OS place.
        new DataVolumeComponent().AddServices(services, configuration);
        foreach (var provider in VolumePlatform.Create().Providers.Where(p => p is not DevPlainDataVolumeProvider))
        {
            services.AddSingleton(provider);
        }

        // The apply step for the plan's storage section. It only verifies with the guard registered above.
        services.AddStorageVolumeStep();
        return services;
    }
}
