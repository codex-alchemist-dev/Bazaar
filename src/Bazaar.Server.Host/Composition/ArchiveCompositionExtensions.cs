using Bazaar.Archive;
using Bazaar.Archive.Gc;
using Bazaar.Server.Gc.Archive;
using Bazaar.Server.Gc.Read;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>Wires cold storage to the GC log (doc 25 "Archive cold storage"): the GC bridge, the project lister and the source factory.</summary>
internal static class ArchiveCompositionExtensions
{
    public static IServiceCollection AddBazaarArchive(this IServiceCollection services)
    {
        services.AddGcArchiveBridge();
        services.TryAddSingleton<IArchiveSourceFactory>(sp => new GcArchiveSourceFactory(
            sp.GetRequiredService<IGcLogReader>(), sp.GetRequiredService<IGcArchiveBridge>(), sp.GetRequiredService<IProjectGcLister>()));
        return services;
    }
}
