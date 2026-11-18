using Bazaar.Server.Identity.Keys;
using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Host.Auth;

/// <summary>Adapts the host key-encryption key (<see cref="IKekProvider"/>) to the identity signing-key wrapper.</summary>
public sealed class SigningKeyKekWrapper(IKekProvider kek) : ISigningKeyWrapper
{
    private readonly IKekProvider _kek = kek ?? throw new ArgumentNullException(nameof(kek));

    /// <inheritdoc />
    public string KekId => _kek.KekId;

    /// <inheritdoc />
    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) =>
        _kek.WrapAsync(key, associatedData, cancellationToken);

    /// <inheritdoc />
    public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) =>
        _kek.UnwrapAsync(wrappedKey, associatedData, cancellationToken);
}
