using Bazaar.Core;
using Bazaar.Server.SignIn.Handoff;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.SignIn.Passkeys;

/// <summary>
/// Registers the WebAuthn passkey check for the sign-in page (doc 32 section 4). It needs the database and <c>signin:rpId</c>; without
/// either it registers nothing and the deny-all default stays. It replaces only that default, so a verifier a host chose first
/// (the development one while dev auth is on) is kept.
/// </summary>
public sealed class PasskeysComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sign-in-passkeys",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["sign-in"],
        Provides: ["feature:sign-in-passkeys"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (PasskeyOptions.Read(configuration) is not { } options)
        {
            return;
        }

        services.TryAddSingleton(options);
        services.TryAddSingleton<IPasskeyCredentialStore>(sp => new PostgresPasskeyCredentialStore(sp.GetRequiredService<NpgsqlDataSource>()));

        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IPasskeyAssertionVerifier));
        if (existing is null || existing.ImplementationType == typeof(DenyAllPasskeyVerifier))
        {
            services.Replace(ServiceDescriptor.Singleton<IPasskeyAssertionVerifier>(sp =>
                new PasskeyAssertionVerifier(sp.GetRequiredService<IPasskeyCredentialStore>(), sp.GetRequiredService<PasskeyOptions>())));
        }
    }
}
