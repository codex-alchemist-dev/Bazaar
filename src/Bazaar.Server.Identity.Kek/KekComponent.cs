using Bazaar.Core;
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
    public const string DataRootKey = "Bazaar:Host:DataRoot";

    /// <summary>The configuration key holding the keyring mode for the <c>file</c> provider (doc 61 Q2): <c>auto</c> (default) or <c>required</c>.</summary>
    public const string KekKeyringKey = "Bazaar:Identity:KekKeyring";

    /// <summary>The configuration key holding the absolute path of <c>secret-tool</c> (Linux); blank uses the standard system locations, never <c>PATH</c>.</summary>
    public const string SecretToolPathKey = "Bazaar:Identity:SecretToolPath";

    /// <summary>The configuration key holding the longest the host waits for the KEK to open, in seconds (1 to 600; default 90).</summary>
    public const string OpenTimeoutSecondsKey = "Bazaar:Identity:KekOpenTimeoutSeconds";

    private const int _defaultOpenTimeoutSeconds = 90;

    private const string _fileKind = "file";

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IKekProvider>(sp =>
        {
            var dataRoot = configuration[DataRootKey]
                ?? throw new InvalidOperationException($"{DataRootKey} is required to open the KEK.");
            var kind = KekProviderFactory.Normalise(configuration[KekProviderKey]);
            // Only the file kind has custody events and a keyring; the other kinds need neither registered.
            if (kind != _fileKind)
                return KekProviderFactory.Create(kind, dataRoot);

            var mode = KekProviderFactory.ParseKeyringMode(configuration[KekKeyringKey]);
            var secretToolPath = configuration[SecretToolPathKey];
            // A host or test may register its own keyring; otherwise the OS default. There is no default audit sink: a KEK open that
            // may need to record a fallback or migration must have one (doc 48 section 5).
            var keyring = sp.GetService<IKekKeyring>() ?? KekProviderFactory.DefaultKeyring(secretToolPath);
            var audit = sp.GetRequiredService<IKekCustodyAuditSink>();
            var time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var verifier = sp.GetService<IKekWrapVerifier>();
            // The KEK must open before the host runs, so this blocks, but never for ever: the open is bounded and cancelled at the open timeout.
            var openTimeout = ParseOpenTimeout(configuration[OpenTimeoutSecondsKey]);
            using var cts = new CancellationTokenSource(openTimeout);
            try
            {
                return KekProviderFactory.CreateAsync(kind, dataRoot, keyring, mode, audit, time, verifier, cancellationToken: cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
            {
                throw new KekKeyringException($"Opening the KEK did not finish within {openTimeout.TotalSeconds:0} s; the start is refused.", ex);
            }
        });
        // The secret store (doc 14 ISecretStore) takes the same host KEK through its key-id-aware contract: one custody path.
        services.AddSingleton<Bazaar.Secrets.IKekProvider>(sp => new SecretsKekAdapter(sp.GetRequiredService<IKekProvider>()));
        services.AddSingleton<ISoulDekStore>(sp => new PostgresSoulDekStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(sp => new SoulDekService(
            sp.GetRequiredService<IKekProvider>(),
            sp.GetRequiredService<ISoulDekStore>(),
            sp.GetService<IKekAuditSink>()));
    }

    private static TimeSpan ParseOpenTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TimeSpan.FromSeconds(_defaultOpenTimeoutSeconds);
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 600)
            throw new ArgumentException($"{OpenTimeoutSecondsKey} '{value}' must be a whole number of seconds from 1 to 600.", nameof(value));
        return TimeSpan.FromSeconds(seconds);
    }
}
