using System.Text.Json;

namespace Bazaar.Client.LocalInference.BlobCache;

/// <summary>
/// File-based <see cref="IBlobCache"/> laid out as <c>&lt;root&gt;/&lt;hash of model&gt;/&lt;hash of key&gt;/</c>, holding
/// <c>blob.bin</c> and a <c>key.json</c> sidecar with the full key. Entries are written in a staging folder under
/// <c>&lt;root&gt;/.staging</c> and moved into place with one rename, so blob and sidecar appear together or not at all.
/// Folder names are SHA-256 hashes, so no key or model text reaches the file system as a path.
/// </summary>
public sealed class FileBlobCache : IBlobCache
{
    internal const string BlobFileName = "blob.bin";
    internal const string SidecarFileName = "key.json";

    private const string StagingDirectoryName = ".staging";

    /// <summary>Creates a cache rooted at <paramref name="root"/>. The folder is created on first write.</summary>
    public FileBlobCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Absolute root directory of the cache.</summary>
    public string Root { get; }

    /// <inheritdoc />
    public string? TryGet(BlobCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var directory = EntryDirectory(key);
        var blob = Path.Combine(directory, BlobFileName);
        var sidecar = Path.Combine(directory, SidecarFileName);
        if (!File.Exists(blob) || !File.Exists(sidecar))
        {
            return null;
        }

        var recorded = ReadSidecar(sidecar);
        return recorded is not null && Matches(recorded, key) ? blob : null;
    }

    /// <inheritdoc />
    public string Put(BlobCacheKey key, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Blob source file not found.", sourcePath);
        }

        var directory = EntryDirectory(key);
        var staging = Path.Combine(Root, StagingDirectoryName, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            File.Copy(sourcePath, Path.Combine(staging, BlobFileName));
            var sidecar = new BlobCacheSidecar(key.ModelSha256, key.ExecutionProvider, key.DriverVersion, key.RuntimeVersion);
            File.WriteAllText(Path.Combine(staging, SidecarFileName), JsonSerializer.Serialize(sidecar, BlobCacheJsonContext.Default.BlobCacheSidecar));

            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.Move(staging, directory);
        }
        catch
        {
            RemoveQuietly(staging);
            throw;
        }

        return Path.Combine(directory, BlobFileName);
    }

    /// <inheritdoc />
    public bool Remove(BlobCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var directory = EntryDirectory(key);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        Directory.Delete(directory, recursive: true);
        return true;
    }

    /// <inheritdoc />
    public int Prune(BlobCacheKey current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var modelDirectory = ModelDirectory(current.ModelSha256);
        if (!Directory.Exists(modelDirectory))
        {
            return 0;
        }

        var keep = current.DirectoryName;
        var removed = 0;
        foreach (var entry in Directory.EnumerateDirectories(modelDirectory))
        {
            if (Path.GetFileName(entry) == keep)
            {
                continue;
            }

            Directory.Delete(entry, recursive: true);
            removed++;
        }

        return removed;
    }

    private string ModelDirectory(string modelSha256) => Path.Combine(Root, BlobCacheKey.HashHex(modelSha256));

    private string EntryDirectory(BlobCacheKey key) => Path.Combine(ModelDirectory(key.ModelSha256), key.DirectoryName);

    private static BlobCacheSidecar? ReadSidecar(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), BlobCacheJsonContext.Default.BlobCacheSidecar);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private static bool Matches(BlobCacheSidecar recorded, BlobCacheKey key) =>
        string.Equals(recorded.ModelSha256, key.ModelSha256, StringComparison.Ordinal)
        && string.Equals(recorded.ExecutionProvider, key.ExecutionProvider, StringComparison.Ordinal)
        && string.Equals(recorded.DriverVersion, key.DriverVersion, StringComparison.Ordinal)
        && string.Equals(recorded.RuntimeVersion, key.RuntimeVersion, StringComparison.Ordinal);

    private static void RemoveQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
