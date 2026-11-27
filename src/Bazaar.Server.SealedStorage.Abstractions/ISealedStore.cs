namespace Bazaar.Server.SealedStorage;

/// <summary>
/// Entry point of sealed storage (doc 61). A scope is a named group of files with one data key. Every file in a scope is
/// authenticated ciphertext on disk, and any change made outside Bazaar is detected on read and fails closed.
/// </summary>
public interface ISealedStore
{
    /// <summary>Opens a scope, creating it (and its data key) on first use. Throws <see cref="SealTamperedException"/> when the scope's key header or manifest has been altered, rolled back or cannot be authenticated.</summary>
    ValueTask<ISealedScope> OpenScopeAsync(string scope, CancellationToken cancellationToken = default);
}

/// <summary>One sealed scope. Paths are logical (forward slashes, no <c>..</c>); they are never visible on disk. Disposing clears the scope's derived keys from memory.</summary>
public interface ISealedScope : IDisposable
{
    /// <summary>The scope name, for example <c>soul:7f3a</c>.</summary>
    string Scope { get; }

    /// <summary>Reads a file. Returns <c>null</c> only when the manifest has no such path. Throws <see cref="SealTamperedException"/> on any mismatch, and never returns partial data.</summary>
    ValueTask<SealedFile?> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a file and returns its new generation. <paramref name="expectedGeneration"/> is 0 to require that the path does not exist,
    /// a generation to require that exact current one (stale writes throw <see cref="SealGenerationConflictException"/>), or <c>null</c> for any.
    /// </summary>
    ValueTask<long> WriteAsync(string path, ReadOnlyMemory<byte> content, long? expectedGeneration = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file. Returns <c>false</c> when the path was not in the manifest.</summary>
    ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Lists the manifest (not the folder), so a file added on disk is never listed.</summary>
    ValueTask<IReadOnlyList<SealedEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Authenticates the manifest and every file, and reports files on disk that the manifest does not know. Throws on tamper.</summary>
    ValueTask<SealVerifyReport> VerifyAsync(CancellationToken cancellationToken = default);

    /// <summary>Crypto-shred: destroys the scope's data key and every file. The data is unrecoverable (doc 14, doc 48 section 9).</summary>
    ValueTask ShredAsync(CancellationToken cancellationToken = default);
}

/// <summary>A decrypted file and the generation it was read at.</summary>
public sealed record SealedFile(string Path, long Generation, byte[] Content);

/// <summary>A manifest entry.</summary>
public sealed record SealedEntry(string Path, long Generation);

/// <summary>Result of <see cref="ISealedScope.VerifyAsync"/>. Unreferenced files are never read; they are reported so the operator sees them.</summary>
public sealed record SealVerifyReport(int FilesVerified, IReadOnlyList<string> UnreferencedFiles);

/// <summary>Records where the newest manifest of each scope was, so an old copy of the whole scope is detected (doc 61 anti-rollback).</summary>
public interface ISealAnchor
{
    /// <summary>The last published manifest generation and hash for a scope, or <c>null</c> when none.</summary>
    ValueTask<SealAnchorPoint?> GetAsync(string scope, CancellationToken cancellationToken);

    /// <summary>Publishes a newer point. Implementations never move a scope's anchor backwards.</summary>
    ValueTask PublishAsync(string scope, SealAnchorPoint point, CancellationToken cancellationToken);

    /// <summary>Forgets a scope after a shred.</summary>
    ValueTask ClearAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>A manifest generation and the SHA-256 of the manifest bytes as stored.</summary>
public sealed record SealAnchorPoint(long Generation, byte[] ManifestHash);

/// <summary>Receives security events (doc 48 section 5). A failure to record must not hide the tamper: the store still throws.</summary>
public interface ISealAuditSink
{
    /// <summary>Records one event, for example <c>seal.tamper</c>.</summary>
    ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken);
}

/// <summary>An audit event. <see cref="Detail"/> names the failure and never contains key material or content.</summary>
public sealed record SealAuditEvent(DateTimeOffset At, string Kind, string Scope, string? Path, string Detail);

/// <summary>Thrown when sealed data fails authentication or is inconsistent. The caller gets no data.</summary>
public sealed class SealTamperedException : Exception
{
    public SealTamperedException(string scope, string? path, string message)
        : base(message)
    {
        Scope = scope;
        Path = path;
    }

    public string Scope { get; }

    public string? Path { get; }
}

/// <summary>Thrown when a write's expected generation is not the current one.</summary>
public sealed class SealGenerationConflictException : Exception
{
    public SealGenerationConflictException(string path, long expected, long actual)
        : base($"Sealed write to '{path}' expected generation {expected} but it is {actual}.")
    {
    }
}
