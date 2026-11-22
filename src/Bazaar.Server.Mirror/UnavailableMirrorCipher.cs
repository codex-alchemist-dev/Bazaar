namespace Bazaar.Server.Mirror;

/// <summary>Registered when there is a database but no KEK: every call fails, so nothing is ever stored unsealed.</summary>
public sealed class UnavailableMirrorCipher : IMirrorCipher
{
    private static InvalidOperationException Failure() => new("Mirror has no KEK provider, so it cannot seal or open values (fail closed).");

    public ValueTask<SealedMirrorValue> SealAsync(string plaintext, byte[] associatedData, CancellationToken cancellationToken) => throw Failure();

    public ValueTask<string> OpenAsync(SealedMirrorValue value, byte[] associatedData, CancellationToken cancellationToken) => throw Failure();
}
