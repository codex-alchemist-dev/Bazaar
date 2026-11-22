using Bazaar.Server.Secrets;

namespace Bazaar.Server.Valkey;

/// <summary>
/// The connection of an operator-run Valkey: the plain <see cref="ValkeyCacheOptions.Endpoint"/>, or the secret reference resolved at
/// connect time. The configuration rule (exactly one of the two) is checked by the cache provider, not here.
/// </summary>
public sealed class ValkeyConfiguredEndpoint(ValkeyCacheOptions options, ISecretResolver? secretResolver) : IValkeyConnectionSource
{
    /// <inheritdoc />
    public ValueTask<string> GetConnectionStringAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return ValueTask.FromResult(options.Endpoint.Trim());
        }

        if (secretResolver is null || string.IsNullOrWhiteSpace(options.SecretReference))
        {
            throw new InvalidOperationException("The Valkey endpoint is not configured.");
        }

        return secretResolver.ResolveAsync(options.SecretReference, ct);
    }
}
