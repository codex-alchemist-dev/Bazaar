using System.Globalization;
using System.Security.Cryptography;

namespace Bazaar.Server.Blobs;

/// <summary>
/// Filesystem <see cref="IBlobStore"/> under one operator-set root: <c>blobs/ab/&lt;hash&gt;</c> for committed bytes (with an
/// <c>.owners</c> sidecar) and <c>uploads/&lt;id&gt;.part</c> plus <c>.meta</c> for unfinished ones. Every path is built from a
/// validated hex id, never from request text.
/// </summary>
public sealed class FileBlobStore : IBlobStore
{
    private const int _stripes = 64;

    // The locks: one stripe per upload id, then one per blob hash (committing), then one that serialises upload creation.

    private readonly string _blobs;
    private readonly string _uploads;
    private readonly BlobStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim[] _locks = [.. Enumerable.Range(0, (2 * _stripes) + 1).Select(_ => new SemaphoreSlim(1, 1))];

    /// <summary>Creates the store and its directories.</summary>
    public FileBlobStore(BlobStoreOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        var root = options.ResolveBlobRoot();
        _blobs = Path.Combine(root, "blobs");
        _uploads = Path.Combine(root, "uploads");
        EnsureDirectories();
    }

    /// <summary>Creates the store's directories. Idempotent, and called again before every new upload so a volume that was unmounted, wiped or restored while the host ran heals without a restart.</summary>
    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_blobs);
        Directory.CreateDirectory(_uploads);
    }

    private string Part(string id) => Path.Combine(_uploads, id + ".part");

    private string Meta(string id) => Path.Combine(_uploads, id + ".meta");

    private string BlobPath(string hash) => Path.Combine(_blobs, hash[..2], hash);

    private SemaphoreSlim CommitLockFor(string hash) => _locks[_stripes + ((int)((uint)hash.GetHashCode(StringComparison.Ordinal) % _stripes))];

    private SemaphoreSlim LockFor(string id) => _locks[(uint)id.GetHashCode(StringComparison.Ordinal) % _stripes];

    /// <inheritdoc />
    public async ValueTask<UploadSession?> CreateUploadAsync(Guid owner, long length, string? declaredSha256, CancellationToken ct)
    {
        // Counting the principal's uploads and making the new one is one step: without the gate a burst of creations all counted
        // below the quota and each went on to make its own session.
        var createGate = _locks[2 * _stripes];
        await createGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CreateUploadLockedAsync(owner, length, declaredSha256, ct).ConfigureAwait(false);
        }
        finally
        {
            createGate.Release();
        }
    }

    private async ValueTask<UploadSession?> CreateUploadLockedAsync(Guid owner, long length, string? declaredSha256, CancellationToken ct)
    {
        EnsureDirectories();
        var cutoff = (_time.GetUtcNow() - TimeSpan.FromHours(_options.UploadTtlHours)).UtcDateTime;
        var held = 0;
        foreach (var meta in Directory.EnumerateFiles(_uploads, "*.meta"))
        {
            var id = Path.GetFileNameWithoutExtension(meta);
            if (!BlobIds.TryNormalizeUploadId(id, out _)) continue;
            if (File.GetLastWriteTimeUtc(meta) < cutoff && File.GetLastWriteTimeUtc(Part(id)) < cutoff)
            {
                Discard(id);
                continue;
            }

            var session = await ReadMetaAsync(id, ct).ConfigureAwait(false);
            if (session is not null && session.Owner == owner) held++;
        }

        if (held >= _options.MaxActiveUploadsPerPrincipal) return null;

        var newId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var text = string.Join('\n', owner.ToString("N"), length.ToString(CultureInfo.InvariantCulture), declaredSha256 ?? string.Empty);
        try
        {
            await File.WriteAllTextAsync(Meta(newId), text, ct).ConfigureAwait(false);
            await using (new FileStream(Part(newId), FileMode.CreateNew, FileAccess.Write)) { }
        }
        catch (IOException)
        {
            // A full or read-only volume: leave no half-made session behind (an orphan .meta with no .part), then let the caller report it.
            TryDiscard(newId);
            throw;
        }

        return new UploadSession(newId, owner, length, 0, declaredSha256);
    }

    /// <inheritdoc />
    public async ValueTask<UploadSession?> GetUploadAsync(Guid owner, string uploadId, CancellationToken ct)
    {
        if (!BlobIds.TryNormalizeUploadId(uploadId, out var id)) return null;
        var session = await ReadMetaAsync(id, ct).ConfigureAwait(false);
        return session is not null && session.Owner == owner ? session : null;
    }

    /// <inheritdoc />
    public async ValueTask<AppendResult> AppendAsync(Guid owner, string uploadId, long offset, Stream body, long count, CancellationToken ct)
    {
        if (!BlobIds.TryNormalizeUploadId(uploadId, out var id)) return new(AppendStatus.NotFound, 0, null);
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = await ReadMetaAsync(id, ct).ConfigureAwait(false);
            if (session is null || session.Owner != owner) return new(AppendStatus.NotFound, 0, null);

            // Every byte is already on disk and only the commit is missing (the request that sent the last chunk was aborted between
            // the write and the rename). Finish it, whatever this request carries, instead of refusing every retry for good.
            if (session.Offset == session.Length && session.Length > 0)
            {
                try
                {
                    return await CommitAsync(session, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new(AppendStatus.StorageUnavailable, session.Offset, null);
                }
            }

            if (offset != session.Offset) return new(AppendStatus.OffsetMismatch, session.Offset, null);
            if (count < 0 || offset + count > session.Length) return new(AppendStatus.BadChunk, session.Offset, null);

            try
            {
                await using (var part = new FileStream(Part(id), FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    part.Seek(offset, SeekOrigin.Begin);
                    var written = await CopyExactAsync(body, part, count, ct).ConfigureAwait(false);
                    if (written != count)
                    {
                        part.SetLength(offset);
                        return new(AppendStatus.BadChunk, offset, null);
                    }
                }

                var total = offset + count;
                if (total < session.Length) return new(AppendStatus.Accepted, total, null);
                return await CommitAsync(session, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The volume is full, read-only or gone. Drop the half-written chunk (and a failed commit's final chunk) so the
                // upload stays at the last good offset and the client can resend the chunk once space returns.
                TryTruncate(id, offset);
                return new(AppendStatus.StorageUnavailable, offset, null);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<BlobHandle?> OpenAsync(string hash, CancellationToken ct)
    {
        if (!BlobIds.TryNormalizeHash(hash, out var h)) return ValueTask.FromResult<BlobHandle?>(null);
        try
        {
            var stream = new FileStream(BlobPath(h), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ValueTask.FromResult<BlobHandle?>(new BlobHandle(stream, stream.Length));
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult<BlobHandle?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return ValueTask.FromResult<BlobHandle?>(null);
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsUploaderAsync(string hash, Guid principal, CancellationToken ct)
    {
        if (!BlobIds.TryNormalizeHash(hash, out var h)) return false;
        var owners = BlobPath(h) + ".owners";
        if (!File.Exists(owners)) return false;
        var text = principal.ToString("N");

        // Read under the commit gate, so a commit appending to the sidecar cannot make this read fail on a sharing violation.
        var commitGate = CommitLockFor(h);
        await commitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var line in await File.ReadAllLinesAsync(owners, ct).ConfigureAwait(false))
            {
                if (line == text) return true;
            }
        }
        finally
        {
            commitGate.Release();
        }

        return false;
    }

    private async ValueTask<AppendResult> CommitAsync(UploadSession session, CancellationToken ct)
    {
        string actual;
        await using (var read = new FileStream(Part(session.Id), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct).ConfigureAwait(false));
        }

        if (session.DeclaredSha256 is { } declared && !string.Equals(declared, actual, StringComparison.Ordinal))
        {
            Discard(session.Id);
            return new(AppendStatus.HashMismatch, 0, null);
        }

        var target = BlobPath(actual);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Uploads of the same bytes commit one at a time: side by side they raced on the owner sidecar and on the rename, and the
        // loser was told the volume had failed.
        var commitGate = CommitLockFor(actual);
        await commitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PlaceAsync(session, actual, target, ct).ConfigureAwait(false);
        }
        finally
        {
            commitGate.Release();
        }
    }

    private async ValueTask<AppendResult> PlaceAsync(UploadSession session, string actual, string target, CancellationToken ct)
    {
        // The owner record goes first: if the volume fills here the blob is not yet in place and the last chunk can be resent. The
        // other order left a committed blob nobody could read. An owner line with no blob is harmless (the blob is simply absent).
        await File.AppendAllTextAsync(target + ".owners", session.Owner.ToString("N") + "\n", ct).ConfigureAwait(false);
        if (File.Exists(target)) File.Delete(Part(session.Id));
        else File.Move(Part(session.Id), target);
        File.Delete(Meta(session.Id));
        return new(AppendStatus.Completed, session.Length, actual);
    }

    private async ValueTask<UploadSession?> ReadMetaAsync(string id, CancellationToken ct)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(Meta(id), ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (lines.Length < 2 || !Guid.TryParseExact(lines[0], "N", out var owner)
            || !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length)) return null;
        var declared = lines.Length > 2 && lines[2].Length > 0 ? lines[2] : null;
        var part = new FileInfo(Part(id));
        return part.Exists ? new UploadSession(id, owner, length, part.Length, declared) : null;
    }

    private void TryTruncate(string id, long length)
    {
        try
        {
            using var part = new FileStream(Part(id), FileMode.Open, FileAccess.Write, FileShare.None);
            part.SetLength(length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next HEAD reports whatever length the file really has.
        }
    }

    private void TryDiscard(string id)
    {
        try
        {
            Discard(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the TTL sweep removes leftovers.
        }
    }

    private void Discard(string id)
    {
        File.Delete(Part(id));
        File.Delete(Meta(id));
    }

    private static async Task<long> CopyExactAsync(Stream source, Stream target, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (total < count)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - total)), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client's body broke or ended before its declared length: a short chunk, not a failure of the volume.
                break;
            }

            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
        }

        return total;
    }
}
