using Bazaar.Server.Engines.Abstractions;
using Bazaar.Server.Godot.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Godot;

public static class GodotServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GodotAdapterOptions"/> and the <see cref="IGodotAdapter"/> as singletons. A process-backed
    /// <see cref="IProcessRunner"/> is added only when the host has not registered one. Pass options to override the
    /// binary path, variant, pin or timeout; otherwise the defaults apply.
    /// </summary>
    public static IServiceCollection AddBazaarGodot(this IServiceCollection services, GodotAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        GodotAdapterOptions adapterOptions = options ?? new GodotAdapterOptions();
        services.AddSingleton(adapterOptions);
        services.TryAddSingleton<IProcessRunner>(_ => new SystemGodotProcessRunner(adapterOptions.MaxOutputCharacters));
        services.AddSingleton<IGodotAdapter, GodotAdapter>();
        return services;
    }
}
