using Bazaar.Server.Gc.Read;
using Bazaar.Server.Http.Blobs;
using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Search.PgFts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bazaar.Server.Http;

/// <summary>Registration of the authenticated HTTP surface (doc 02 WP10).</summary>
public static class HttpSurfaceServiceCollectionExtensions
{
    /// <summary>
    /// Adds options, the HTTP rate bucket, the blob store and the blob and search access helpers. The host supplies the
    /// <see cref="Wire.Connection.IConnectionAuthenticator"/>; <see cref="IGcReadAccess"/> and <see cref="NpgsqlDataSource"/> are
    /// optional and their absence fails closed (uploader-only blob reads, search unavailable).
    /// </summary>
    public static IServiceCollection AddBazaarHttpSurface(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HttpSurfaceOptions>()
            .Bind(configuration.GetSection(HttpSurfaceOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HttpSurfaceOptions>, HttpSurfaceOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<HttpSurfaceOptions>>().Value);
        services.TryAddSingleton<HttpRateLimiter>();
        services.TryAddSingleton<IBlobStore, FileBlobStore>();
        services.TryAddSingleton<IBlobGcLinks>(sp => sp.GetService<NpgsqlDataSource>() is { } data ? new PgBlobGcLinks(data) : new NoBlobGcLinks());
        services.TryAddSingleton(sp => new BlobReadAccess(sp.GetRequiredService<IBlobStore>(), sp.GetRequiredService<IBlobGcLinks>(), sp.GetService<IGcReadAccess>()));
        services.TryAddSingleton<ISearchProvider>(sp => new PgFtsSearchProvider(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
