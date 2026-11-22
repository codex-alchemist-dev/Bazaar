using Bazaar.Archive.Gc;
using Bazaar.Server.Gc.Read;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Gc.Archive;

/// <summary>Registers the GC side of cold storage. The host supplies <c>NpgsqlDataSource</c>, <c>IGcLogWriter</c> and <c>IGcBodyCodec</c>.</summary>
public static class GcArchiveServiceCollectionExtensions
{
    /// <summary>Adds the archive bridge, the message text source (the same object) and the project lister.</summary>
    public static IServiceCollection AddGcArchiveBridge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PgGcArchiveBridge>();
        services.TryAddSingleton<IGcArchiveBridge>(sp => sp.GetRequiredService<PgGcArchiveBridge>());
        services.TryAddSingleton<IGcMessageTextSource>(sp => sp.GetRequiredService<PgGcArchiveBridge>());
        services.TryAddSingleton<IProjectGcLister, PgProjectGcLister>();
        return services;
    }
}
