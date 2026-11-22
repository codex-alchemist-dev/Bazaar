using Bazaar.Server.Diagnostics;
using Bazaar.Server.Gc.Body;

namespace Bazaar.Server.Host.Composition;

/// <summary>Binds the crash sealer's key port to the identity <see cref="PersonKeyStore"/>, so the diagnostics module stays off the server library.</summary>
public sealed class PersonKeyStoreCrashKeys(PersonKeyStore keys) : ICrashKeyStore
{
    /// <inheritdoc />
    public async ValueTask<CrashDataKey> GetOrCreateActiveAsync(Guid ownerId, CancellationToken ct)
    {
        var key = await keys.GetOrCreateActiveAsync(ownerId, ct).ConfigureAwait(false);
        return new CrashDataKey(key.KeyId, key.DataKey);
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> UnwrapAsync(Guid keyId, CancellationToken ct) => keys.UnwrapAsync(keyId, ct);
}
