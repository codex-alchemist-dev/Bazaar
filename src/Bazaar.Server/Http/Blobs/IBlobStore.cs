namespace Bazaar.Server.Http.Blobs;

/// <summary>An unfinished upload.</summary>
/// <param name="Id">32 hex characters, server generated.</param>
/// <param name="Owner">Principal that opened it; only that principal may continue it.</param>
/// <param name="Length">Declared total size.</param>
/// <param name="Offset">Bytes received so far (the next chunk must start here).</param>
/// <param name="DeclaredSha256">Hash the client promised, lowercase hex, or null.</param>
public sealed record UploadSession(string Id, Guid Owner, long Length, long Offset, string? DeclaredSha256);

/// <summary>How an append ended.</summary>
public enum AppendStatus
{
    /// <summary>Chunk stored; more expected.</summary>
    Accepted,

    /// <summary>Last chunk stored, hash verified, blob committed.</summary>
    Completed,

    /// <summary>The offset does not match what the server holds.</summary>
    OffsetMismatch,

    /// <summary>No such upload for this principal.</summary>
    NotFound,

    /// <summary>The chunk would pass the declared length, or the body was shorter than declared.</summary>
    BadChunk,

    /// <summary>The assembled bytes do not match the declared hash; the upload is discarded.</summary>
    HashMismatch,
}

/// <summary>Result of <see cref="IBlobStore.AppendAsync"/>.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="Offset">Bytes now held (or held before a rejected chunk).</param>
/// <param name="Hash">Content hash when <see cref="AppendStatus.Completed"/>.</param>
public readonly record struct AppendResult(AppendStatus Status, long Offset, string? Hash);

/// <summary>An open committed blob.</summary>
/// <param name="Content">Readable stream; the caller disposes it.</param>
/// <param name="Length">Size in bytes.</param>
public sealed record BlobHandle(Stream Content, long Length);

/// <summary>
/// Content-addressed blob store. A blob id is the lowercase hex SHA-256 of its bytes. Callers pass only ids that passed
/// <see cref="BlobIds"/>; implementations must not build paths from anything else.
/// </summary>
public interface IBlobStore
{
    /// <summary>Opens an upload, or returns null when the principal holds too many unfinished ones.</summary>
    ValueTask<UploadSession?> CreateUploadAsync(Guid owner, long length, string? declaredSha256, CancellationToken ct);

    /// <summary>The upload when it exists and belongs to <paramref name="owner"/>.</summary>
    ValueTask<UploadSession?> GetUploadAsync(Guid owner, string uploadId, CancellationToken ct);

    /// <summary>Appends exactly <paramref name="count"/> bytes at <paramref name="offset"/>, committing after the last chunk.</summary>
    ValueTask<AppendResult> AppendAsync(Guid owner, string uploadId, long offset, Stream body, long count, CancellationToken ct);

    /// <summary>Opens a committed blob, or null.</summary>
    ValueTask<BlobHandle?> OpenAsync(string hash, CancellationToken ct);

    /// <summary>Whether <paramref name="principal"/> uploaded (or re-uploaded) the blob.</summary>
    ValueTask<bool> IsUploaderAsync(string hash, Guid principal, CancellationToken ct);
}

/// <summary>Validation of identifiers that reach the filesystem.</summary>
public static class BlobIds
{
    /// <summary>True for exactly 64 hex characters; <paramref name="normalized"/> is lowercase.</summary>
    public static bool TryNormalizeHash(string? value, out string normalized) => TryHex(value, 64, out normalized);

    /// <summary>True for exactly 32 hex characters; <paramref name="normalized"/> is lowercase.</summary>
    public static bool TryNormalizeUploadId(string? value, out string normalized) => TryHex(value, 32, out normalized);

    private static bool TryHex(string? value, int length, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != length) return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        normalized = value.ToLowerInvariant();
        return true;
    }
}
