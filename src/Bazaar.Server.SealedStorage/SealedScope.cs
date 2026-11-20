using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.SealedStorage;

/// <summary>
/// One scope of the sealed store (doc 61). All operations on a scope are serialised (an in-process gate plus the per-scope OS lock file);
/// the manifest is re-read and re-authenticated on every call so a change made while the server runs is caught too. Every write, delete and
/// batch goes through one path with a single manifest commit. A long cycle across several operations takes the separate scope lease.
/// </summary>
internal sealed class SealedScope : ISealedScope
{
    private static readonly TimeSpan _osLockTimeout = TimeSpan.FromSeconds(30);

    private readonly SealedStore _store;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate;
    private readonly byte[] _fileKey;
    private readonly byte[] _nameKey;
    private readonly byte[] _manifestKey;
    private bool _disposed;
    private int _disposeState;

    internal SealedScope(SealedStore store, string scope, string directory, byte[] dek, SemaphoreSlim gate)
    {
        _store = store;
        Scope = scope;
        _directory = directory;
        _gate = gate;
        _fileKey = SealFormat.Derive(dek, "file");
        _nameKey = SealFormat.Derive(dek, "name");
        _manifestKey = SealFormat.Derive(dek, "manifest");
        CryptographicOperations.ZeroMemory(dek);
    }

    public string Scope { get; }

    private string ManifestPath => Path.Combine(_directory, "manifest.bzm");

    internal async ValueTask InitialiseAsync(CancellationToken cancellationToken)
    {
        // Under the scope lock no writer has a temp file in flight, so every temp file is a leftover of a crashed write: never trusted, removed.
        using (await AcquireOsLockAsync(cancellationToken).ConfigureAwait(false))
        {
            var leftovers = 0;
            foreach (var temp in Directory.EnumerateFiles(_directory, "*.tmp"))
            {
                if (!Path.GetFileName(temp).StartsWith("scope.key.", StringComparison.Ordinal) && TryDelete(temp))
                {
                    leftovers++;
                }
            }

            if (leftovers > 0)
            {
                await _store.RecordAsync("seal.cleanup", Scope, null, $"{leftovers} temp file(s) of an interrupted write were removed; they were never committed or read.", CancellationToken.None).ConfigureAwait(false);
            }

            if (!File.Exists(ManifestPath))
            {
                var anchor = await _store.Anchor.GetAsync(Scope, cancellationToken).ConfigureAwait(false);
                if (anchor is not null)
                {
                    throw await _store.FailAsync(Scope, null, "Manifest is missing but the anchor remembers it.", cancellationToken).ConfigureAwait(false);
                }

                if (Directory.EnumerateFileSystemEntries(_directory).Any(e => Path.GetFileName(e) is not ("scope.key" or "scope.lock" or "scope.lease") && !e.EndsWith(".tmp", StringComparison.Ordinal)))
                {
                    throw await _store.FailAsync(Scope, null, "Manifest is missing but the scope folder holds other data.", cancellationToken).ConfigureAwait(false);
                }

                await CommitManifestAsync(new Manifest(0, new Dictionary<string, ManifestEntry>()), cancellationToken).ConfigureAwait(false);
            }

            await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<SealedFile?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var logical = SealFormat.NormalisePath(path);
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        return manifest.Entries.TryGetValue(logical, out var entry)
            ? new SealedFile(logical, entry.Generation, await ReadEntryAsync(logical, entry, cancellationToken).ConfigureAwait(false))
            : null;
    }

    public async ValueTask<SealedSnapshot> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var ordered = manifest.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();

        // Check the total before reading anything. A file over the single-file limit is not counted: reading it below fails as tamper.
        long total = 0;
        foreach (var (logical, entry) in ordered)
        {
            var info = new FileInfo(FilePath(logical, entry.Generation));
            if (info.Exists && info.Length <= SealFormat.MaxStoredBytes)
            {
                total += info.Length;
            }
        }

        if (total > _store.MaxBulkBytes)
        {
            throw new InvalidOperationException($"Scope '{Scope}' holds {total} bytes, more than the {_store.MaxBulkBytes}-byte snapshot limit.");
        }

        var files = new List<SealedFile>(ordered.Count);
        try
        {
            foreach (var (logical, entry) in ordered)
            {
                files.Add(new SealedFile(logical, entry.Generation, await ReadEntryAsync(logical, entry, cancellationToken).ConfigureAwait(false)));
            }
        }
        catch
        {
            // No partial data: plaintext already decrypted is cleared before the failure propagates.
            foreach (var file in files)
            {
                CryptographicOperations.ZeroMemory(file.Content);
            }

            throw;
        }

        return new SealedSnapshot(manifest.Generation, files);
    }

    public async ValueTask<long> WriteAsync(string path, ReadOnlyMemory<byte> content, long? expectedGeneration = null, CancellationToken cancellationToken = default)
    {
        var logical = SealFormat.NormalisePath(path);
        if (content.Length > SealFormat.MaxFileBytes)
        {
            throw new ArgumentException("Sealed file is larger than the 256 MiB limit.", nameof(content));
        }

        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ApplyAsync([new Operation(logical, content, expectedGeneration, false)], cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => DeleteAsync(path, null, cancellationToken);

    public async ValueTask<bool> DeleteAsync(string path, long? expectedGeneration, CancellationToken cancellationToken = default)
    {
        var logical = SealFormat.NormalisePath(path);
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var before = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var existed = before.Entries.ContainsKey(logical);
        await ApplyAsync([new Operation(logical, ReadOnlyMemory<byte>.Empty, expectedGeneration, true)], cancellationToken, before).ConfigureAwait(false);
        return existed;
    }

    public async ValueTask<long> WriteBatchAsync(IReadOnlyList<SealedWrite> writes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);

        // Validate everything before taking the lock or writing a byte.
        var operations = new List<Operation>(writes.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var write in writes)
        {
            ArgumentNullException.ThrowIfNull(write, nameof(writes));
            var logical = SealFormat.NormalisePath(write.Path);
            if (!paths.Add(logical))
            {
                throw new ArgumentException($"Sealed batch names '{logical}' more than once.", nameof(writes));
            }

            if (!write.IsDelete)
            {
                if (write.Content.Length > SealFormat.MaxFileBytes)
                {
                    throw new ArgumentException($"Sealed file '{logical}' is larger than the 256 MiB limit.", nameof(writes));
                }

                total += write.Content.Length;
            }

            operations.Add(new Operation(logical, write.IsDelete ? ReadOnlyMemory<byte>.Empty : write.Content, write.ExpectedGeneration, write.IsDelete));
        }

        if (total > _store.MaxBulkBytes)
        {
            throw new ArgumentException($"Sealed batch holds {total} bytes, more than the {_store.MaxBulkBytes}-byte batch limit.", nameof(writes));
        }

        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(operations, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ISealedLease> AcquireLeaseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var path = Path.Combine(_directory, "scope.lease");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                // The lease file stays empty: it carries no data, only the OS lock.
                return new Lease(Scope, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
            }
            catch (DirectoryNotFoundException)
            {
                throw await _store.FailAsync(Scope, null, "Scope folder is missing.", cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < timeout)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"The lease on sealed scope '{Scope}' was not free within {timeout}.", ex);
            }
        }
    }

    /// <summary>
    /// Applies operations under the held lock with one manifest commit. Every check runs first; then new files are written under new
    /// names, the manifest is renamed into place (the single commit point) and only then are old generations removed. A failure before the
    /// rename removes this call's files, so the manifest and every file it references are exactly as before.
    /// </summary>
    private async ValueTask<long> ApplyAsync(IReadOnlyList<Operation> operations, CancellationToken cancellationToken, Manifest? loaded = null)
    {
        var manifest = loaded ?? await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        foreach (var op in operations)
        {
            var currentGeneration = manifest.Entries.TryGetValue(op.Path, out var current) ? current.Generation : 0;
            if (op.ExpectedGeneration is { } expected && expected != currentGeneration)
            {
                throw new SealGenerationConflictException(op.Path, expected, currentGeneration);
            }
        }

        if (!operations.Any(op => !op.IsDelete || manifest.Entries.ContainsKey(op.Path)))
        {
            return manifest.Generation;
        }

        // File generations come from the manifest counter, so a path that is deleted and recreated never reuses a generation.
        var generation = manifest.Generation + 1;
        var entries = new Dictionary<string, ManifestEntry>(manifest.Entries, StringComparer.Ordinal);
        var superseded = new List<string>();
        var written = new List<string>();
        string? pendingTemp = null;
        var committed = false;
        try
        {
            foreach (var op in operations)
            {
                if (manifest.Entries.TryGetValue(op.Path, out var current))
                {
                    superseded.Add(FilePath(op.Path, current.Generation));
                }

                if (op.IsDelete)
                {
                    entries.Remove(op.Path);
                    continue;
                }

                var blob = SealFormat.SealFile(_fileKey, Scope, op.Path, generation, op.Content.Span);
                var target = FilePath(op.Path, generation);
                pendingTemp = $"{target}.{Guid.NewGuid():N}.tmp";
                await SealFormat.WriteDurableAsync(pendingTemp, blob, cancellationToken).ConfigureAwait(false);
                // A file with this name that the manifest does not reference is a leftover of a crash before a commit: it was never trusted.
                TryDelete(target);
                File.Move(pendingTemp, target, overwrite: false);
                pendingTemp = null;
                written.Add(target);
                entries[op.Path] = new ManifestEntry(generation, SealFormat.Sha256(blob));
            }

            var manifestBlob = SealFormat.SealManifest(_manifestKey, Scope, generation, new Manifest(generation, entries).Serialise());
            pendingTemp = $"{ManifestPath}.{Guid.NewGuid():N}.tmp";
            await SealFormat.WriteDurableAsync(pendingTemp, manifestBlob, cancellationToken).ConfigureAwait(false);
            File.Move(pendingTemp, ManifestPath, overwrite: true);
            committed = true;
            pendingTemp = null;

            // Committed: the rest is not cancellable, so the anchor never lags a manifest because a caller gave up.
            await _store.Anchor.PublishAsync(Scope, new SealAnchorPoint(generation, SealFormat.Sha256(manifestBlob)), CancellationToken.None).ConfigureAwait(false);
        }
        catch when (!committed)
        {
            if (pendingTemp is not null)
            {
                TryDelete(pendingTemp);
            }

            foreach (var file in written)
            {
                TryDelete(file);
            }

            throw;
        }

        foreach (var old in superseded)
        {
            TryDelete(old);
        }

        return generation;
    }

    public async ValueTask<IReadOnlyList<SealedEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        return [.. manifest.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new SealedEntry(e.Key, e.Value.Generation))];
    }

    public async ValueTask<SealVerifyReport> VerifyAsync(CancellationToken cancellationToken = default)
    {
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var expectedNames = new HashSet<string>(StringComparer.Ordinal) { "scope.key", "manifest.bzm", "scope.lock", "scope.lease" };
        foreach (var (logical, entry) in manifest.Entries)
        {
            await ReadEntryAsync(logical, entry, cancellationToken).ConfigureAwait(false);
            expectedNames.Add(Path.GetFileName(FilePath(logical, entry.Generation)));
        }

        var unreferenced = Directory.EnumerateFileSystemEntries(_directory)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !expectedNames.Contains(name))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unreferenced.Count > 0)
        {
            await _store.RecordAsync("seal.unreferenced", Scope, null, $"{unreferenced.Count} file(s) in the scope folder are not in the manifest and are never read.", cancellationToken).ConfigureAwait(false);
        }

        return new SealVerifyReport(manifest.Entries.Count, unreferenced);
    }

    public async ValueTask ShredAsync(CancellationToken cancellationToken = default)
    {
        using var entered = await EnterAsync(cancellationToken).ConfigureAwait(false);
        // Destroy the key header first: from this point the remaining ciphertext cannot be read by anyone.
        TryDelete(Path.Combine(_directory, "scope.key"));
        entered.ReleaseOsLock();
        Directory.Delete(_directory, recursive: true);
        await _store.Anchor.ClearAsync(Scope, cancellationToken).ConfigureAwait(false);
        await _store.RecordAsync("seal.shred", Scope, null, "Scope shredded.", cancellationToken).ConfigureAwait(false);
        ZeroKeys();
    }

    public void Dispose()
    {
        // Wait for an in-flight operation: keys are zeroed only while nobody is using them.
        _gate.Wait();
        try
        {
            ZeroKeys();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void ZeroKeys()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 1)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_fileKey);
        CryptographicOperations.ZeroMemory(_nameKey);
        CryptographicOperations.ZeroMemory(_manifestKey);
    }

    private async ValueTask<Releaser> EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var osLock = await AcquireOsLockAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(_gate, osLock);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>A per-scope OS lock so two processes (or two store instances) never interleave a manifest read-modify-write.</summary>
    private async ValueTask<FileStream> AcquireOsLockAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "scope.lock");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (DirectoryNotFoundException)
            {
                // Before the IOException filter: a missing folder is tamper at once, not something to wait for.
                throw await _store.FailAsync(Scope, null, "Scope folder is missing.", cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < _osLockTimeout)
            {
                // Short polls: an operation holds this lock only for one manifest read-modify-write.
                await Task.Delay(Random.Shared.Next(5, 20), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private string FilePath(string logical, long generation)
    {
        var name = Convert.ToHexString(HMACSHA256.HashData(_nameKey, SealFormat.StrictUtf8.GetBytes(logical))).ToLowerInvariant();
        return Path.Combine(_directory, $"{name}.{generation}.bzs");
    }

    private async ValueTask<byte[]> ReadEntryAsync(string logical, ManifestEntry entry, CancellationToken cancellationToken)
    {
        var path = FilePath(logical, entry.Generation);
        byte[]? blob;
        try
        {
            blob = await SealFormat.ReadBoundedAsync(path, SealFormat.MaxStoredBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw await _store.FailAsync(Scope, logical, "Sealed file cannot be read.", cancellationToken).ConfigureAwait(false);
        }

        if (blob is null)
        {
            throw await _store.FailAsync(Scope, logical, "Sealed file is missing from disk or larger than the limit.", cancellationToken).ConfigureAwait(false);
        }

        if (!CryptographicOperations.FixedTimeEquals(SealFormat.Sha256(blob), entry.Hash))
        {
            throw await _store.FailAsync(Scope, logical, "Sealed file does not match the manifest.", cancellationToken).ConfigureAwait(false);
        }

        if (!SealFormat.TryReadHeader(blob, SealFormat.FileMagic, out var generation) || generation != entry.Generation)
        {
            throw await _store.FailAsync(Scope, logical, "Sealed file header is malformed or has the wrong generation.", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return SealFormat.Open(_fileKey, blob, SealFormat.FileAad(Scope, logical, generation));
        }
        catch (CryptographicException)
        {
            throw await _store.FailAsync(Scope, logical, "Sealed file does not authenticate.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<Manifest> LoadManifestAsync(CancellationToken cancellationToken)
    {
        byte[]? blob;
        try
        {
            blob = await SealFormat.ReadBoundedAsync(ManifestPath, SealFormat.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw await _store.FailAsync(Scope, null, "Manifest cannot be read.", cancellationToken).ConfigureAwait(false);
        }

        if (blob is null)
        {
            throw await _store.FailAsync(Scope, null, "Manifest is missing or larger than the limit.", cancellationToken).ConfigureAwait(false);
        }

        if (!SealFormat.TryReadHeader(blob, SealFormat.ManifestMagic, out var generation))
        {
            throw await _store.FailAsync(Scope, null, "Manifest header is malformed.", cancellationToken).ConfigureAwait(false);
        }

        Manifest manifest;
        try
        {
            manifest = Manifest.Parse(generation, SealFormat.Open(_manifestKey, blob, SealFormat.ManifestAad(Scope, generation)));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            throw await _store.FailAsync(Scope, null, "Manifest does not authenticate.", cancellationToken).ConfigureAwait(false);
        }

        var hash = SealFormat.Sha256(blob);
        SealAnchorPoint? anchor;
        try
        {
            anchor = await _store.Anchor.GetAsync(Scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw await _store.FailAsync(Scope, null, "Anchor cannot be read or is malformed.", cancellationToken).ConfigureAwait(false);
        }

        if (anchor is not null)
        {
            if (anchor.Generation > generation)
            {
                throw await _store.FailAsync(Scope, null, $"Manifest is older than the anchor (generation {generation}, anchor {anchor.Generation}): rollback.", cancellationToken).ConfigureAwait(false);
            }

            if (anchor.Generation == generation && !CryptographicOperations.FixedTimeEquals(anchor.ManifestHash, hash))
            {
                throw await _store.FailAsync(Scope, null, "Manifest differs from the anchored manifest of the same generation.", cancellationToken).ConfigureAwait(false);
            }
        }

        if (anchor is null && generation > 0)
        {
            throw await _store.FailAsync(Scope, null, "Manifest has writes but no anchor remembers the scope: the anchor was removed or the scope was restored.", cancellationToken).ConfigureAwait(false);
        }

        if (anchor is null || anchor.Generation < generation)
        {
            // A crash after the manifest commit and before the anchor publish lands here; the authenticated manifest is newer, so catch the anchor up.
            await _store.Anchor.PublishAsync(Scope, new SealAnchorPoint(generation, hash), cancellationToken).ConfigureAwait(false);
        }

        return manifest;
    }

    private async ValueTask CommitManifestAsync(Manifest manifest, CancellationToken cancellationToken)
    {
        var blob = SealFormat.SealManifest(_manifestKey, Scope, manifest.Generation, manifest.Serialise());
        var temp = $"{ManifestPath}.{Guid.NewGuid():N}.tmp";
        await SealFormat.WriteDurableAsync(temp, blob, cancellationToken).ConfigureAwait(false);
        File.Move(temp, ManifestPath, overwrite: true);
        await _store.Anchor.PublishAsync(Scope, new SealAnchorPoint(manifest.Generation, SealFormat.Sha256(blob)), cancellationToken).ConfigureAwait(false);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stale files are unreferenced by the manifest, so they are never read; verify reports them.
            return false;
        }
    }

    private sealed record Operation(string Path, ReadOnlyMemory<byte> Content, long? ExpectedGeneration, bool IsDelete);

    private sealed class Lease(string scope, FileStream lockFile) : ISealedLease
    {
        public string Scope { get; } = scope;

        public void Dispose() => lockFile.Dispose();
    }

    private sealed class Releaser(SemaphoreSlim gate, FileStream osLock) : IDisposable
    {
        private int _released;

        public void ReleaseOsLock() => osLock.Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                osLock.Dispose();
                gate.Release();
            }
        }
    }

    private sealed record ManifestEntry(long Generation, byte[] Hash);

    private sealed record Manifest(long Generation, IReadOnlyDictionary<string, ManifestEntry> Entries)
    {
        internal byte[] Serialise()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, SealFormat.StrictUtf8, leaveOpen: true);
            writer.Write(Entries.Count);
            foreach (var (path, entry) in Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                writer.Write(path);
                writer.Write(entry.Generation);
                writer.Write(entry.Hash);
            }

            writer.Flush();
            return stream.ToArray();
        }

        internal static Manifest Parse(long generation, byte[] plaintext)
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(plaintext), SealFormat.StrictUtf8);
                var count = reader.ReadInt32();
                if (count < 0 || count > 10_000_000)
                {
                    throw new InvalidDataException("Manifest entry count is out of range.");
                }

                var entries = new Dictionary<string, ManifestEntry>(count, StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var path = reader.ReadString();
                    var entryGeneration = reader.ReadInt64();
                    var hash = reader.ReadBytes(SHA256.HashSizeInBytes);
                    if (hash.Length != SHA256.HashSizeInBytes || !entries.TryAdd(path, new ManifestEntry(entryGeneration, hash)))
                    {
                        throw new InvalidDataException("Manifest entry is malformed or duplicated.");
                    }
                }

                return new Manifest(generation, entries);
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("Manifest is truncated.", ex);
            }
        }
    }
}
