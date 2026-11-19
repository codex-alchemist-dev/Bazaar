using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// The KEK module (doc 14, <c>identity.kek_provider</c>). It registers <see cref="IKekProvider"/> through the kernel, so the host
/// and every consumer depend only on the abstractions. The provider kind is read from <c>Bazaar:Identity:KekProvider</c>
/// (environment <c>BAZAAR__IDENTITY__KEKPROVIDER</c>); it is host configuration because the KEK must open before the database.
/// The key file is under <c>Bazaar:Host:BootRoot</c> when that is set (doc 63: open only, never created at start), otherwise
/// under <c>Bazaar:Host:DataRoot</c>.
/// </summary>
public sealed class KekComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "identity-kek",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: [],
        Provides: ["identity:kek:provider"]);

    /// <summary>The configuration key holding <c>identity.kek_provider</c>.</summary>
    public const string KekProviderKey = "Bazaar:Identity:KekProvider";

    /// <summary>The configuration key holding the data root.</summary>
    public const string DataRootKey = "Bazaar:Host:DataRoot";

    /// <summary>The configuration key holding the boot root (doc 63 section 2): the plain folder that holds the KEK file.</summary>
    public const string BootRootKey = "Bazaar:Host:BootRoot";

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IKekProvider>(_ =>
        {
            // With a boot root the install follows doc 63: the key lives outside the encrypted data root and the host only
            // opens it. Without one the install predates doc 63 and keeps the old location until it is migrated.
            var bootRoot = configuration[BootRootKey];
            if (!string.IsNullOrWhiteSpace(bootRoot))
                return KekProviderFactory.Create(configuration[KekProviderKey], bootRoot, KekOpenMode.OpenExisting);

            var dataRoot = configuration[DataRootKey]
                ?? throw new InvalidOperationException($"{BootRootKey} or {DataRootKey} is required to open the KEK.");
            return KekProviderFactory.Create(configuration[KekProviderKey], dataRoot);
        });
    }
}
