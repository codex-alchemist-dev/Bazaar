using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Archive;

/// <summary>
/// The Archive cold-storage module (doc 25 "Archive cold storage"). Settings: <c>Bazaar:Archive:Root</c> (the bundle directory; defaults to <c>archive</c> under <c>Bazaar:Host:DataRoot</c>)
/// and <c>Bazaar:Archive:SegmentMaxRecords</c> (default 5000). Sources and restore targets come from the modules that own the data.
/// </summary>
public sealed class ArchiveComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "archive",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["archive:cold-storage", "archive:search"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Bazaar:Archive");
        var max = int.TryParse(section["SegmentMaxRecords"], out var m) && m is >= 1 and <= 1_000_000 ? m : new ArchiveOptions().SegmentMaxRecords;
        services.AddSingleton(new ArchiveOptions(max));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IArchiveSearchIndex, InMemoryArchiveSearchIndex>();
        // Default: an "archive" folder under the host data root, which backup (doc 33) and data-folder encryption (doc 63) cover.
        var dataRoot = configuration["Bazaar:Host:DataRoot"];
        var root = section["Root"] ?? (string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "archive"));
        if (!string.IsNullOrWhiteSpace(root))
        {
            services.TryAddSingleton<IArchiveStore>(_ => new DirectoryArchiveStore(root));
        }

        services.AddSingleton<ArchiveVerifier>();
        services.AddSingleton<ColdArchiver>();
        services.AddSingleton<ArchiveRestorer>();
    }
}
