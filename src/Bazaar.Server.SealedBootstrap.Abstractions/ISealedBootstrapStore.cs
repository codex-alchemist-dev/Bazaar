namespace Bazaar.Server.SealedBootstrap;

/// <summary>
/// Host bootstrap values (doc 03: the database credential and provider bootstrap values the server needs before the database is up)
/// kept in the sealed scope <c>host:bootstrap</c> (doc 61 work package 5). Every read is authenticated and fails closed: a tampered,
/// rolled-back or plaintext copy throws and returns nothing. The KEK reference is never stored here, because this scope is wrapped by
/// the KEK (doc 61 "Trust chain").
/// </summary>
public interface ISealedBootstrapStore
{
    /// <summary>Returns the value. Throws <see cref="SealedBootstrapValueNotFoundException"/> when the name is not stored, and the sealed store's tamper exception on any mismatch.</summary>
    ValueTask<string> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Stores or replaces a value. The audit entry names the value, never its content.</summary>
    ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes a value. Returns false when it was not stored.</summary>
    ValueTask<bool> RemoveAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists the stored names (from the sealed manifest).</summary>
    ValueTask<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a plaintext file (the form a <c>file:</c> reference points at) once: seals it, reads it back and compares, then
    /// zero-overwrites and deletes the file. A failed verification keeps the plaintext. When the name already holds the same value (an
    /// earlier import whose wipe failed) the file is only wiped; a different stored value refuses and keeps the file.
    /// </summary>
    ValueTask<SealedBootstrapImportResult> ImportPlaintextFileAsync(string name, string path, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="ISealedBootstrapStore.ImportPlaintextFileAsync"/>.</summary>
public enum SealedBootstrapImportResult
{
    /// <summary>Sealed, verified and the plaintext wiped.</summary>
    Imported,

    /// <summary>The same value was already sealed; the plaintext was wiped.</summary>
    AlreadyImported,
}

/// <summary>Thrown when a bootstrap value is not stored. The message names the value, never content.</summary>
public sealed class SealedBootstrapValueNotFoundException(string name)
    : Exception($"No sealed bootstrap value named '{name}'.")
{
    public string Name { get; } = name;
}
