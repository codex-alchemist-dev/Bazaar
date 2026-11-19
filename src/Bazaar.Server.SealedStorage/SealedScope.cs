using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.SealedStorage;

/// <summary>One scope of the sealed store (doc 60). All operations on a scope are serialised; the manifest is re-read and re-authenticated on every call so a change made while the server runs is caught too.</summary>
internal sealed class SealedScope : ISealedScope
{
    private readonly SealedStore _store;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate;
    private readonly byte[] _fileKey;
    private readonly byte[] _nameKey;
    private readonly byte[] _manifestKey;
    private bool _disposed;

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
        var temp = target + ".tmp";
        await File.WriteAllBytesAsync(temp, blob, cancellationToken).ConfigureAwait(false);
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
        var expectedNames = new HashSet<string>(StringComparer.Ordinal) { "scope.key", "manifest.bzm" };
        foreach (var (logical, entry) in manifest.Entries)
        {
            await ReadEntryAsync(logical, entry, cancellationToken).ConfigureAwait(false);
            expectedNames.Add(Path.GetFileName(FilePath(logical, entry.Generation)));
        }

        var unreferenced = Directory.EnumerateFiles(_directory)
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
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        // Destroy the key header first: from this point the remaining ciphertext cannot be read by anyone.
        TryDelete(Path.Combine(_directory, "scope.key"));
        Directory.Delete(_directory, recursive: true);
        await _store.Anchor.ClearAsync(Scope, cancellationToken).ConfigureAwait(false);
        await _store.RecordAsync("seal.shred", Scope, null, "Scope shredded.", cancellationToken).ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
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
        return new Releaser(_gate);
    }

    private string FilePath(string logical, long generation)
    {
        var name = Convert.ToHexString(HMACSHA256.HashData(_nameKey, Encoding.UTF8.GetBytes(logical))).ToLowerInvariant();
        return Path.Combine(_directory, $"{name}.{generation}.bzs");
    }

    private async ValueTask<byte[]> ReadEntryAsync(string logical, ManifestEntry entry, CancellationToken cancellationToken)
    {
        var path = FilePath(logical, entry.Generation);
        byte[] blob;
        try
        {
            blob = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw await _store.FailAsync(Scope, logical, "Sealed file is missing from disk.", cancellationToken).ConfigureAwait(false);
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
        byte[] blob;
        try
        {
            blob = await File.ReadAllBytesAsync(ManifestPath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw await _store.FailAsync(Scope, null, "Manifest is missing.", cancellationToken).ConfigureAwait(false);
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
        var anchor = await _store.Anchor.GetAsync(Scope, cancellationToken).ConfigureAwait(false);
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
        var temp = ManifestPath + ".tmp";
        await File.WriteAllBytesAsync(temp, blob, cancellationToken).ConfigureAwait(false);
        File.Move(temp, ManifestPath, overwrite: true);
        await _store.Anchor.PublishAsync(Scope, new SealAnchorPoint(manifest.Generation, SealFormat.Sha256(blob)), cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Stale files are unreferenced by the manifest, so they are never read; verify reports them.
        }
    }

    private readonly struct Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private sealed record ManifestEntry(long Generation, byte[] Hash);

    private sealed record Manifest(long Generation, IReadOnlyDictionary<string, ManifestEntry> Entries)
    {
        internal byte[] Serialise()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
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
                using var reader = new BinaryReader(new MemoryStream(plaintext), Encoding.UTF8);
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
