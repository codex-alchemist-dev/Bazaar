using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Bazaar.Server.Identity.Kek;
using Npgsql;

namespace Bazaar.Server.Mirror;

/// <summary>
/// The Mirror Agent module (doc 26 section 5, work package 6, server only). Settings: <c>Bazaar:Mirror:SignalKinds</c>
/// (comma list), <c>:EvidenceThreshold</c>, <c>:AllowRewriteOffers</c>, <c>:ModelSource</c> (<c>Local</c> or <c>OperatorApprovedHosted</c>).
/// </summary>
public sealed class MirrorComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "mirror",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["mirror:profile", "mirror:annotate"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        // With a database the cipher is the owner's person key under the KEK (doc 04, doc 26 C01). Without the KEK there is no
        // cipher and the store refuses to run (fail closed). With no database at all, a process-local store is used.
        services.TryAddSingleton<IMirrorCipher>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } ds && sp.GetService<IKekProvider>() is { } kek
                ? new PersonKeyMirrorCipher(ds, kek)
                : new UnavailableMirrorCipher());
        services.TryAddSingleton<IMirrorStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } ds
                ? new PostgresMirrorStore(ds, sp.GetRequiredService<IMirrorCipher>())
                : new InMemoryMirrorStore());
        services.AddSingleton(_ => ReadOptions(configuration));
        services.AddSingleton<IMirrorService, MirrorService>();
        services.AddSingleton<IMirrorFeed, MirrorFeed>();
    }

    public static MirrorOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Bazaar:Mirror");
        var defaults = new MirrorOptions();

        var kinds = defaults.SignalKinds;
        if (section["SignalKinds"] is { Length: > 0 } list)
        {
            var parsed = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => Enum.TryParse<MirrorSignalKind>(k, true, out var v) ? (MirrorSignalKind?)v : null)
                .Where(k => k is not null).Select(k => k!.Value).ToHashSet();
            if (parsed.Count > 0) { kinds = parsed; }
        }

        // A bad value falls back to the safe default rather than stopping start-up.
        var threshold = int.TryParse(section["EvidenceThreshold"], out var t) && t >= 1 ? t : defaults.EvidenceThreshold;
        var rewrite = bool.TryParse(section["AllowRewriteOffers"], out var r) ? r : defaults.AllowRewriteOffers;
        var source = Enum.TryParse<MirrorModelSource>(section["ModelSource"], true, out var s) ? s : defaults.ModelSource;
        return new MirrorOptions { SignalKinds = kinds, EvidenceThreshold = threshold, AllowRewriteOffers = rewrite, ModelSource = source };
    }
}
