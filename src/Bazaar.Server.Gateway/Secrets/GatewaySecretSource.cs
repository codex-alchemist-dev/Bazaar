using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Gateway.Secrets;

/// <summary>
/// The gateway's <see cref="IProviderSecretSource"/>. Accepts only <c>secret://bazaar/providers/&lt;id&gt;/&lt;name&gt;</c>
/// references and resolves them through <see cref="ISecretResolver"/>. A reference the resolver cannot find yields null,
/// which the gateway treats as a missing secret. There is no fallback to the environment: a reference is never turned
/// into a variable name.
/// </summary>
public sealed class GatewaySecretSource(ISecretResolver resolver) : IProviderSecretSource
{
    private readonly ISecretResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>Throws <see cref="ArgumentException"/> for any other reference shape. The message does not repeat the reference.</summary>
    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        if (!IsProviderReference(secretRef))
        {
            throw new ArgumentException(
                $"A provider secret reference must start with '{CredentialKinds.SecretRefPrefix}' and name a secret. The reference was refused and is not shown.",
                nameof(secretRef));
        }

        return ResolveCoreAsync(secretRef, ct);
    }

    /// <summary>True for a reference with the provider prefix and a non-empty name after it.</summary>
    public static bool IsProviderReference(string secretRef) =>
        secretRef.StartsWith(CredentialKinds.SecretRefPrefix, StringComparison.Ordinal)
        && secretRef.Length > CredentialKinds.SecretRefPrefix.Length;

    private async ValueTask<string?> ResolveCoreAsync(string secretRef, CancellationToken ct)
    {
        // TODO(component): read from the Identity Service's ISecretStore (doc 14) once the component model (PR #125) lands.
        // Until then no resolver in the host understands this scheme, so the reference is simply missing.
        if (!_resolver.CanResolve(secretRef))
        {
            return null;
        }

        try
        {
            var value = await _resolver.ResolveAsync(secretRef, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (SecretResolutionException)
        {
            // The exception names the reference and never a value, and a missing secret is reported as null, not as a fault.
            return null;
        }
    }
}
