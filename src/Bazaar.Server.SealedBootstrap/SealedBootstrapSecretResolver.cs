using Bazaar.Server.SealedStorage;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.SealedBootstrap;

/// <summary>
/// Resolves <c>sealed:&lt;name&gt;</c> bootstrap references (doc 03) from the sealed scope <c>host:bootstrap</c> (doc 61 work package 5).
/// Reads fail closed: a missing value, a tampered or rolled-back scope or a KEK that does not open it throws
/// <see cref="SecretResolutionException"/>, which names the reference and never the value. There is no plaintext fallback.
/// </summary>
public sealed class SealedBootstrapSecretResolver(ISealedBootstrapStore store) : ISecretResolver
{
    /// <summary>The reference scheme.</summary>
    public const string Prefix = "sealed:";

    private readonly ISealedBootstrapStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public bool CanResolve(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public async ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!CanResolve(reference))
            throw new SecretResolutionException(reference, "unsupported scheme; this resolver handles sealed: only.");

        var name = reference[Prefix.Length..];
        try
        {
            SealedBootstrapStore.ValidateName(name);
            return await _store.GetAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new SecretResolutionException(reference, "the value name is not valid.", ex);
        }
        catch (SealedBootstrapValueNotFoundException ex)
        {
            throw new SecretResolutionException(reference, "no sealed bootstrap value has this name.", ex);
        }
        catch (SealTamperedException ex)
        {
            throw new SecretResolutionException(reference, "the sealed bootstrap scope failed verification (tamper, rollback or wrong KEK); restore from backup.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SecretResolutionException)
        {
            // Whatever else fails (a KEK that will not open, an IO or keyring error, an import that failed) reaches callers as one type that names the reference only.
            throw new SecretResolutionException(reference, "the sealed bootstrap value could not be read; see the inner exception.", ex);
        }
    }
}
