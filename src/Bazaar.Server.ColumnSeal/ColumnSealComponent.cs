using Bazaar.Core;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// The column sealing module (doc 63 section 9): the sealer, the blind indexer and the PostgreSQL DEK store. It needs the KEK
/// module and an <see cref="NpgsqlDataSource"/> from the host. Its table comes from the <c>column_seal</c> migration namespace.
/// </summary>
public sealed class ColumnSealComponent : IBazaarComponent
{
    /// <summary>The configuration key for the number of unwrapped DEKs kept in memory.</summary>
    public const string CacheSizeKey = "Bazaar:ColumnSeal:DekCacheSize";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "column-seal",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["identity-kek"],
        Provides: ["security:column-seal:sealer", "security:column-seal:blind-index"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var options = ReadOptions(configuration);
        services.AddSingleton<IColumnDekStore>(provider => new PostgresColumnDekStore(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IKekProvider>(),
            options));
        services.AddSingleton<IColumnSealer>(provider => new ColumnSealer(provider.GetRequiredService<IColumnDekStore>()));
        services.AddSingleton<IBlindIndexer>(provider => new BlindIndexer(provider.GetRequiredService<IColumnDekStore>()));
    }

    /// <summary>Reads the options. A cache size that is not a positive integer is an error, never a silent default.</summary>
    public static ColumnDekStoreOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[CacheSizeKey];
        if (string.IsNullOrWhiteSpace(raw))
            return new ColumnDekStoreOptions();
        if (!int.TryParse(raw, out var size) || size < 1)
            throw new InvalidOperationException($"{CacheSizeKey} must be a positive integer.");
        return new ColumnDekStoreOptions { CacheSize = size };
    }
}
