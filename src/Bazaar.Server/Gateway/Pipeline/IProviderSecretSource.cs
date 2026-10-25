namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>
/// Turns a provider's secret reference into the key to send. A stand-in for the secret store of doc 14, which does
/// not exist yet. The returned value goes only into the adapter's endpoint; the gateway never logs or yields it.
/// </summary>
public interface IProviderSecretSource
{
    /// <summary>The secret for the reference, or null when it cannot be found.</summary>
    ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct);
}
