using Bazaar.Server.Gc.Read;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Blobs;

/// <summary>Registration of the blob module (doc 02 blob surface, doc 01 viewer rules).</summary>
public static class BlobsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the filesystem blob store and the read-access check. <see cref="IGcReadAccess"/> and <see cref="NpgsqlDataSource"/> are
    /// optional and their absence fails closed (uploader-only reads). Every registration is TryAdd, so a host or test may replace one.
    /// </summary>
    public static IServiceCollection AddBazaarBlobs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(BlobStoreOptions.SectionName);
        var options = new BlobStoreOptions { BlobRoot = section["BlobRoot"] };
        if (int.TryParse(section["MaxActiveUploadsPerPrincipal"], out var active)) options.MaxActiveUploadsPerPrincipal = active;
        if (int.TryParse(section["UploadTtlHours"], out var ttl)) options.UploadTtlHours = ttl;
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IBlobStore, FileBlobStore>();
        services.TryAddSingleton<IBlobGcLinks>(sp => sp.GetService<NpgsqlDataSource>() is { } data ? new PgBlobGcLinks(data) : new NoBlobGcLinks());
        services.TryAddSingleton<IBlobReadAccess>(sp => new BlobReadAccess(sp.GetRequiredService<IBlobStore>(), sp.GetRequiredService<IBlobGcLinks>(), sp.GetService<IGcReadAccess>()));
        return services;
    }
}
