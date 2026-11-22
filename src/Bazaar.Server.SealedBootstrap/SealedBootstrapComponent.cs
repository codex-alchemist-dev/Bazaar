using Bazaar.Core;
using Bazaar.Server.SealedStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.SealedBootstrap;

/// <summary>
/// The sealed bootstrap module (doc 61 work package 5). It registers <see cref="ISealedBootstrapStore"/> over the sealed scope
/// <c>host:bootstrap</c> and <see cref="SealedBootstrapSecretResolver"/> for <c>sealed:&lt;name&gt;</c> references. Plaintext files named
/// under <c>Bazaar:SealedBootstrap:ImportFrom</c> are imported once (verify, then wipe). The KEK itself is not here: its key file is
/// sealed by the OS keyring in the KEK module (doc 61 Q2).
/// </summary>
public sealed class SealedBootstrapComponent : IBazaarComponent
{
    /// <summary>The configuration section of import sources, one key per value name.</summary>
    public const string ImportFromSection = "Bazaar:SealedBootstrap:ImportFrom";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sealed-bootstrap",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["sealed-storage"],
        Provides: ["security:sealed-bootstrap"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in configuration.GetSection(ImportFromSection).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child.Value))
                throw new InvalidOperationException($"{ImportFromSection}:{child.Key} is empty.");
            SealedBootstrapStore.ValidateName(child.Key);
            if (!Path.IsPathFullyQualified(child.Value))
                throw new InvalidOperationException($"{ImportFromSection}:{child.Key} must be an absolute path.");
            imports[child.Key] = child.Value;
        }

        // Lets the KEK wrap check (SealedKekWrapVerifier) find this module's scope header.
        services.AddSingleton<ISealedScopeNameSource>(new BootstrapScopeNames());
        var options = new SealedBootstrapOptions { ImportFrom = imports };
        services.AddSingleton<ISealedBootstrapStore>(sp => new SealedBootstrapStore(
            sp.GetRequiredService<ISealedStore>(),
            sp.GetRequiredService<ISealAuditSink>(),
            options,
            sp.GetService<TimeProvider>()));
        services.AddSingleton(sp => new SealedBootstrapSecretResolver(sp.GetRequiredService<ISealedBootstrapStore>()));
    }
}

internal sealed class BootstrapScopeNames : ISealedScopeNameSource
{
    public ValueTask<IReadOnlyCollection<string>> ListScopeNamesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<string>>([SealedBootstrapStore.Scope]);
}
