using Bazaar.Core;
using Bazaar.Server.SignIn.WebAuthn.Credentials;
using Bazaar.Server.SignIn.WebAuthn.Registration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.SignIn.WebAuthn;

/// <summary>
/// Real passkey verification and registration for the sign-in page (docs/design/32 section 4, docs/design/51 section 5). It
/// replaces the sign-in module's deny-all verifier, owns the <c>sign_in_webauthn</c> migration namespace, and needs the audit log:
/// a passkey is never registered unrecorded. Operator settings are in <see cref="WebAuthnOptions"/>.
/// </summary>
public sealed class SignInWebAuthnComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sign-in-webauthn",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["sign-in", "audit"],
        Provides: ["feature:sign-in-webauthn"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarSignInWebAuthn(configuration);
}

public static class SignInWebAuthnServiceCollectionExtensions
{
    /// <summary>
    /// Registers the verifier (replacing the sign-in module's deny-all one), the Postgres credential store, the registration
    /// services and the bootstrap-code announcer. The caller must register an <c>NpgsqlDataSource</c> and an <c>IAuditLog</c>.
    /// </summary>
    public static IServiceCollection AddBazaarSignInWebAuthn(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(WebAuthnOptions.Read(configuration));
        services.TryAddSingleton<IPasskeyCredentialStore, PostgresPasskeyCredentialStore>();
        services.TryAddSingleton<AttestationVerifier>();
        services.TryAddSingleton<RegistrationChallengeStore>();
        services.TryAddSingleton<BootstrapCodeService>();
        services.TryAddSingleton<IRegistrationPrincipalResolver, NoSignedInPrincipalResolver>();
        services.TryAddSingleton<IBootstrapCodeSink, ConsoleBootstrapCodeSink>();

        // Replace, not TryAdd: the sign-in module registers its deny-all verifier first, and this component depends on it.
        services.Replace(ServiceDescriptor.Singleton<IPasskeyAssertionVerifier, WebAuthnAssertionVerifier>());
        services.AddHostedService<BootstrapCodeAnnouncer>();
        return services;
    }
}
