using Bazaar.Core;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.SealedStorage;

/// <summary>
/// The sealed storage module (doc 60). It registers <see cref="ISealedStore"/> over the active <see cref="IKekProvider"/>.
/// Folders come from <c>Bazaar:Host:DataRoot</c>: scopes in <c>sealed</c>, the local anchor in <c>sealed-anchor</c>. A host that
/// registers its own <see cref="ISealAnchor"/> (for example the audit-log anchor) wins. <see cref="ISealAuditSink"/> must be registered by the
/// host: there is no silent default, because a store that cannot audit tamper must not start.
/// </summary>
public sealed class SealedStorageComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sealed-storage",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["identity-kek"],
        Provides: ["security:sealed-storage"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var dataRoot = configuration[KekComponent.DataRootKey]
            ?? throw new InvalidOperationException($"{KekComponent.DataRootKey} is required to place sealed storage.");
        services.TryAddSingleton<ISealAnchor>(_ => new FileSealAnchor(Path.Combine(dataRoot, "sealed-anchor")));
        services.AddSingleton<ISealedStore>(sp => new SealedStore(
            new SealedStoreOptions { RootPath = Path.Combine(dataRoot, "sealed") },
            sp.GetRequiredService<IKekProvider>(),
            sp.GetRequiredService<ISealAnchor>(),
            sp.GetRequiredService<ISealAuditSink>()));
    }
}
