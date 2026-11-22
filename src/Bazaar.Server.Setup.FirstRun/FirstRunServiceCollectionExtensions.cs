using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Setup.FirstRun;

/// <summary>Registers the first-run seed step. The host adds the stores behind the ports and passes the step to <c>AddSetupApply</c>.</summary>
public static class FirstRunServiceCollectionExtensions
{
    /// <summary>Adds <see cref="FirstRunSeedStep"/> as a singleton, and as an <see cref="ISetupStep"/> the host can collect.</summary>
    public static IServiceCollection AddFirstRunSeed(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<FirstRunSeedStep>();
        services.AddSingleton<ISetupStep>(provider => provider.GetRequiredService<FirstRunSeedStep>());
        return services;
    }
}
