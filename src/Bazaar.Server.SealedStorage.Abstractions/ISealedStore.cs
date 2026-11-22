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
/// <summary>
/// Optional capability of a sealed store: destroys a scope that can no longer be opened because it failed verification (a tampered manifest or
/// header). Erasure uses it so corruption cannot keep ciphertext alive. The key header goes first.
/// </summary>
public interface ISealedScopeShredder
{
    /// <summary>Crypto-shreds the scope without verifying it. Does nothing but clear the anchor when the scope does not exist.</summary>
    ValueTask ForceShredScopeAsync(string scope, CancellationToken cancellationToken = default);
}

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

    /// <summary>Deletes a file with no generation check. Returns <c>false</c> when the path was not in the manifest.</summary>
    ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file. <paramref name="expectedGeneration"/> is a generation to require that exact current one, 0 to require that the path
    /// does not exist (then nothing is deleted), or <c>null</c> for any. A mismatch throws <see cref="SealGenerationConflictException"/> and deletes nothing.
    /// Returns <c>false</c> when the path was not in the manifest.
    /// </summary>
    ValueTask<bool> DeleteAsync(string path, long? expectedGeneration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every file of the scope as one consistent snapshot: the manifest and all files are read and authenticated under one lock, so a
    /// concurrent writer can never produce a mix of old and new files. Throws <see cref="SealTamperedException"/> on any mismatch and returns no
    /// partial data. Throws <see cref="InvalidOperationException"/> when the files together exceed the store's bulk limit.
    /// </summary>
    ValueTask<SealedSnapshot> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies many writes and deletes with one manifest commit and returns the new manifest generation (unchanged when nothing changed).
    /// Every expected generation and size is checked before anything is written; a conflict throws <see cref="SealGenerationConflictException"/>.
    /// Readers see either all of the batch or none of it: new files are written first under new names and the manifest rename is the single
    /// commit point. If anything fails before that rename, the batch's files are removed and the scope is exactly as it was.
    /// </summary>
    ValueTask<long> WriteBatchAsync(IReadOnlyList<SealedWrite> writes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the scope's cross-process lease: an OS lock file held until the lease is disposed, so a long read-modify-write cycle (for example
    /// materialise, work, reseal) is exclusive against every other lease holder, in this or another process or store instance. Single operations
    /// do not take the lease; they stay serialised by the per-operation lock and their generation checks. Throws <see cref="TimeoutException"/>
    /// when the lease is not free within <paramref name="timeout"/>.
    /// </summary>
    ValueTask<ISealedLease> AcquireLeaseAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Lists the manifest (not the folder), so a file added on disk is never listed.</summary>
    ValueTask<IReadOnlyList<SealedEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Authenticates the manifest and every file, and reports files on disk that the manifest does not know. Throws on tamper.</summary>
    ValueTask<SealVerifyReport> VerifyAsync(CancellationToken cancellationToken = default);

    /// <summary>Crypto-shred: destroys the scope's data key and every file. The data is unrecoverable (doc 14, doc 48 section 9).</summary>
    ValueTask ShredAsync(CancellationToken cancellationToken = default);
}

/// <summary>A decrypted file and the generation it was read at.</summary>
public sealed record SealedFile(string Path, long Generation, byte[] Content);

/// <summary>A consistent snapshot of a scope (<see cref="ISealedScope.ReadAllAsync"/>): every file at the manifest generation it was read at, ordered by path.</summary>
public sealed record SealedSnapshot(long ManifestGeneration, IReadOnlyList<SealedFile> Files);

/// <summary>One operation of <see cref="ISealedScope.WriteBatchAsync"/>. <see cref="ExpectedGeneration"/> works as in <see cref="ISealedScope.WriteAsync"/>.</summary>
public sealed record SealedWrite(string Path, ReadOnlyMemory<byte> Content, long? ExpectedGeneration, bool IsDelete)
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/>.</summary>
    public static SealedWrite Put(string path, ReadOnlyMemory<byte> content, long? expectedGeneration = null) => new(path, content, expectedGeneration, false);

    /// <summary>Deletes <paramref name="path"/>; a path that does not exist is left alone.</summary>
    public static SealedWrite Delete(string path, long? expectedGeneration = null) => new(path, ReadOnlyMemory<byte>.Empty, expectedGeneration, true);
}

/// <summary>A held scope lease (<see cref="ISealedScope.AcquireLeaseAsync"/>). Disposing releases it.</summary>
public interface ISealedLease : IDisposable
{
    /// <summary>The scope the lease is held on.</summary>
    string Scope { get; }
}

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
        Path = path;
        Expected = expected;
        Actual = actual;
    }

    public string Path { get; }

    public long Expected { get; }

    public long Actual { get; }
}
