using Bazaar.Core;
using Bazaar.Server.Identity.Kek.Tpm;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

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
        Provides: ["identity:kek:provider", "identity:dek:service", "secrets:kek"]);

    /// <summary>The configuration key holding <c>identity.kek_provider</c>.</summary>
    public const string KekProviderKey = "Bazaar:Identity:KekProvider";

    /// <summary>The configuration key holding the data root.</summary>
    /// <summary>The configuration key holding the PCR list that binds a <c>tpm</c> KEK (default <c>7</c>).</summary>
    public const string TpmPcrsKey = "Bazaar:Identity:TpmPcrs";

    public const string DataRootKey = "Bazaar:Host:DataRoot";

    /// <summary>The configuration key holding the boot root (doc 63 section 2): the plain folder that holds the KEK file.</summary>
    public const string BootRootKey = "Bazaar:Host:BootRoot";

    public ComponentManifest Manifest => ManifestDefinition;

    /// <summary>
    /// Opens the existing KEK under <c>Bazaar:Host:BootRoot</c> and never creates one (doc 63 section 5). For the volume unit,
    /// which has no legacy data-root fallback.
    /// </summary>
    /// <exception cref="KekMissingException">The key file is missing or empty.</exception>
    public static IKekProvider OpenExisting(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var bootRoot = configuration[BootRootKey];
        if (string.IsNullOrWhiteSpace(bootRoot))
            throw new InvalidOperationException($"{BootRootKey} is required to open the KEK.");

        return KekProviderFactory.Create(configuration[KekProviderKey], bootRoot, KekOpenMode.OpenExisting);
    }

    /// <summary>
    /// Opens the KEK under <c>Bazaar:Host:BootRoot</c> and creates it when absent. Only setup (<c>storage init</c>) and the
    /// restore wizard call this; the host and the volume unit use <see cref="OpenExisting"/>.
    /// </summary>
    public static IKekProvider OpenOrCreate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var bootRoot = configuration[BootRootKey];
        if (string.IsNullOrWhiteSpace(bootRoot))
            throw new InvalidOperationException($"{BootRootKey} is required to create the KEK.");

        return KekProviderFactory.Create(configuration[KekProviderKey], bootRoot, KekOpenMode.CreateIfMissing);
    }

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IKekProvider>(sp =>
        {
            // With a boot root the install follows doc 63: the key lives outside the encrypted data root and the host only
            // opens it. Without one the install predates doc 63 and keeps the old location until it is migrated.
            var kind = KekProviderFactory.NormaliseKind(configuration[KekProviderKey]);
            var plugged = sp.GetServices<IKekProviderKind>().FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.Ordinal));
            var bootRoot = configuration[BootRootKey];
            if (!string.IsNullOrWhiteSpace(bootRoot))
                return plugged is not null
                    ? plugged.Create(key => configuration[key], bootRoot)
                    : KekProviderFactory.Create(kind, bootRoot, KekOpenMode.OpenExisting);

            var dataRoot = configuration[DataRootKey]
                ?? throw new InvalidOperationException($"{DataRootKey} is required to open the KEK.");
            return KekProviderFactory.Create(
                configuration[KekProviderKey],
                dataRoot,
                new TpmKekOptions(Pcrs: configuration[TpmPcrsKey] ?? "7"));
                ?? throw new InvalidOperationException($"{BootRootKey} or {DataRootKey} is required to open the KEK.");
            return plugged is not null
                ? plugged.Create(key => configuration[key], dataRoot)
                : KekProviderFactory.Create(kind, dataRoot);
        });
        // The secret store (doc 14 ISecretStore) takes the same host KEK through its key-id-aware contract: one custody path.
        services.AddSingleton<Bazaar.Secrets.IKekProvider>(sp => new SecretsKekAdapter(sp.GetRequiredService<IKekProvider>()));
        services.AddHostedService<KekStartupOpener>();
        services.AddSingleton<ISoulDekStore>(sp => new PostgresSoulDekStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(sp => new SoulDekService(
            sp.GetRequiredService<IKekProvider>(),
            sp.GetRequiredService<ISoulDekStore>(),
            sp.GetService<IKekAuditSink>()));

        // With a host audit log present, one custody entry is written at start (doc 14). Without one the recorder does nothing.
        services.AddHostedService(sp => new KekCustodyRecorder(
            sp.GetService<IAuditLog>() is { } log ? new AuditLogKekSink(log) : null,
            configuration[KekProviderKey],
            sp.GetService<ILogger<KekCustodyRecorder>>() ?? NullLogger<KekCustodyRecorder>.Instance));
    }
}
