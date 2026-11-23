using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.SealedStorage;

/// <summary>One scope of the sealed store (doc 61). All operations on a scope are serialised; the manifest is re-read and re-authenticated on every call so a change made while the server runs is caught too.</summary>
internal sealed class SealedScope : ISealedScope
{
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
        if (!File.Exists(ManifestPath))
        {
            var anchor = await _store.Anchor.GetAsync(Scope, cancellationToken).ConfigureAwait(false);
            if (anchor is not null)
            {
                throw await _store.FailAsync(Scope, null, "Manifest is missing but the anchor remembers it.", cancellationToken).ConfigureAwait(false);
            }

            if (Directory.EnumerateFileSystemEntries(_directory).Any(e => Path.GetFileName(e) is not ("scope.key" or "scope.lock") && !e.EndsWith(".tmp", StringComparison.Ordinal)))
            {
                throw await _store.FailAsync(Scope, null, "Manifest is missing but the scope folder holds other data.", cancellationToken).ConfigureAwait(false);
            }

            await CommitManifestAsync(new Manifest(0, new Dictionary<string, ManifestEntry>()), cancellationToken).ConfigureAwait(false);
        }

        await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<long> WriteAsync(string path, ReadOnlyMemory<byte> content, long? expectedGeneration = null, CancellationToken cancellationToken = default)
    {
        var logical = SealFormat.NormalisePath(path);
        if (content.Length > SealFormat.MaxFileBytes)
        {
            throw new ArgumentException("Sealed file is larger than the 256 MiB limit.", nameof(content));
        }

        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        var hasCurrent = manifest.Entries.TryGetValue(logical, out var current);
        var currentGeneration = hasCurrent ? current!.Generation : 0;
        if (expectedGeneration is { } expected && expected != currentGeneration)
        {
            throw new SealGenerationConflictException(logical, expected, currentGeneration);
        }

        // File generations come from the manifest counter, so a path that is deleted and recreated never reuses a generation.
        var generation = manifest.Generation + 1;
        var blob = SealFormat.SealFile(_fileKey, Scope, logical, generation, content.Span);
        var target = FilePath(logical, generation);
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        await SealFormat.WriteDurableAsync(temp, blob, cancellationToken).ConfigureAwait(false);
        // A file with this name that the manifest does not reference is a leftover of a crash before the commit: it was never trusted.
        TryDelete(target);
        File.Move(temp, target, overwrite: false);

        var entries = new Dictionary<string, ManifestEntry>(manifest.Entries, StringComparer.Ordinal)
        {
            [logical] = new ManifestEntry(generation, SealFormat.Sha256(blob)),
        };
        await CommitManifestAsync(new Manifest(generation, entries), cancellationToken).ConfigureAwait(false);
        if (hasCurrent)
        {
            TryDelete(FilePath(logical, currentGeneration));
        }

        return generation;
    }

    public async ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var logical = SealFormat.NormalisePath(path);
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
        if (!manifest.Entries.TryGetValue(logical, out var current))
        {
            return false;
        }

        var entries = new Dictionary<string, ManifestEntry>(manifest.Entries, StringComparer.Ordinal);
        entries.Remove(logical);
        await CommitManifestAsync(new Manifest(manifest.Generation + 1, entries), cancellationToken).ConfigureAwait(false);
        TryDelete(FilePath(logical, current.Generation));
        return true;
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
        var expectedNames = new HashSet<string>(StringComparer.Ordinal) { "scope.key", "manifest.bzm", "scope.lock" };
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
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                throw await _store.FailAsync(Scope, null, "Scope folder is missing.", cancellationToken).ConfigureAwait(false);
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stale files are unreferenced by the manifest, so they are never read; verify reports them.
        }
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
