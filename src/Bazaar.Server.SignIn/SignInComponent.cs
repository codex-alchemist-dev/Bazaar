using Bazaar.Core;
using Bazaar.Server.SignIn.Handoff;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.SignIn;

/// <summary>
/// The interactive sign-in page and handoff-code store as a Bazaar Component (docs/design/32 sections 4 and 5). It registers the
/// in-memory stores, the adapter that lets the admin panel redeem codes, and a deny-all passkey verifier until an adapter replaces it.
/// </summary>
public sealed class SignInComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sign-in",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:sign-in"],
        SoftDependsOn: ["audit"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(SignInOptions.Read(configuration));
        services.TryAddSingleton<IHandoffCodeStore, InMemoryHandoffCodeStore>();
        services.TryAddSingleton<IPasskeyChallengeStore, InMemoryPasskeyChallengeStore>();
        services.TryAddSingleton<IPasskeyAssertionVerifier, DenyAllPasskeyVerifier>();
        services.TryAddSingleton<IAdminHandoffRedeemer, HandoffRedeemer>();
    }
}
