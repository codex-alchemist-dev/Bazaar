using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// The KEK module (doc 14, <c>identity.kek_provider</c>). It registers <see cref="IKekProvider"/> through the kernel, so the host
/// and every consumer depend only on the abstractions. The provider kind is read from <c>Bazaar:Identity:KekProvider</c>
/// (environment <c>BAZAAR__IDENTITY__KEKPROVIDER</c>); it is host configuration because the KEK must open before the database.
/// The data root comes from <c>Bazaar:Host:DataRoot</c>.
/// </summary>
public sealed class KekComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "identity-kek",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: [],
        Provides: ["identity:kek:provider", "identity:dek:service", "secrets:kek"]);

    /// <summary>The configuration key holding <c>identity.kek_provider</c>.</summary>
    public const string KekProviderKey = "Bazaar:Identity:KekProvider";

    /// <summary>The configuration key holding the data root.</summary>
    public const string DataRootKey = "Bazaar:Host:DataRoot";

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IKekProvider>(_ =>
        {
            var dataRoot = configuration[DataRootKey]
                ?? throw new InvalidOperationException($"{DataRootKey} is required to open the KEK.");
            return KekProviderFactory.Create(configuration[KekProviderKey], dataRoot);
        });
        // The secret store (doc 14 ISecretStore) takes the same host KEK through its key-id-aware contract: one custody path.
        services.AddSingleton<Bazaar.Secrets.IKekProvider>(sp => new SecretsKekAdapter(sp.GetRequiredService<IKekProvider>()));
        services.AddSingleton<ISoulDekStore>(sp => new PostgresSoulDekStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(sp => new SoulDekService(
            sp.GetRequiredService<IKekProvider>(),
            sp.GetRequiredService<ISoulDekStore>(),
            sp.GetService<IKekAuditSink>()));
    }
}
