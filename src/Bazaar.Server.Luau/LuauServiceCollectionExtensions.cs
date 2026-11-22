using Bazaar.Server.Engines.Abstractions;
using Bazaar.Server.Luau.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Luau;

public static class LuauServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LuauToolOptions"/>, the process-backed <see cref="IProcessRunner"/> and the <see cref="ILuauChecker"/>
    /// as singletons. Pass options to override the binary paths or timeout; otherwise the defaults apply.
    /// </summary>
    public static IServiceCollection AddBazaarLuau(this IServiceCollection services, LuauToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        LuauToolOptions toolOptions = options ?? new LuauToolOptions();
        services.AddSingleton(toolOptions);
        services.AddSingleton<IProcessRunner>(_ => new SystemLuauToolRunner(toolOptions.MaxOutputCharacters));
        services.AddSingleton<ILuauChecker, LuauChecker>();
        return services;
    }
}
