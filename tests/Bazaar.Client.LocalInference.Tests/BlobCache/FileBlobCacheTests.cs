using Bazaar.Client.LocalInference.BlobCache;
using Bazaar.Client.LocalInference.Tests.Models;

namespace Bazaar.Client.LocalInference.Tests.BlobCache;

public sealed class FileBlobCacheTests : IDisposable
{
    private const string BlobFile = "blob.bin";
    private const string SidecarFile = "key.json";

    private readonly TempDirectory _temp = new();
    private readonly string _cacheRoot;

    public FileBlobCacheTests()
    {
        _cacheRoot = _temp.Combine("cache");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Hit_on_exact_key_returns_the_stored_blob()
    {
        var cache = new FileBlobCache(_cacheRoot);
        var key = Key();
        var source = WriteSource("compiled.blob", "compiled-bytes");

        var stored = cache.Put(key, source);

        Assert.Equal(stored, cache.TryGet(key));
        Assert.Equal("compiled-bytes", File.ReadAllText(stored));
    }

    [Fact]
    public void Miss_when_nothing_is_stored()
    {
        var cache = new FileBlobCache(_cacheRoot);

        Assert.Null(cache.TryGet(Key()));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("executionProvider")]
    [InlineData("driverVersion")]
    [InlineData("runtimeVersion")]
    public void Miss_when_any_one_key_field_differs(string changedField)
    {
        var cache = new FileBlobCache(_cacheRoot);
        cache.Put(Key(), WriteSource("compiled.blob", "compiled-bytes"));

        Assert.Null(cache.TryGet(Mutate(Key(), changedField)));
        Assert.NotNull(cache.TryGet(Key()));
    }

    [Fact]
    public void Driver_version_change_from_a_value_to_null_is_a_miss()
    {
        var cache = new FileBlobCache(_cacheRoot);
        cache.Put(Key(), WriteSource("compiled.blob", "compiled-bytes"));

        Assert.Null(cache.TryGet(new BlobCacheKey("model-a", "OpenVINO", null, "2026.2.0")));
    }

    [Fact]
    public void Driver_version_change_from_null_to_a_value_is_a_miss()
    {
        var cache = new FileBlobCache(_cacheRoot);
        cache.Put(new BlobCacheKey("model-a", "CPU", null, "2026.2.0"), WriteSource("compiled.blob", "compiled-bytes"));

        Assert.Null(cache.TryGet(new BlobCacheKey("model-a", "CPU", "32.0.100.5540", "2026.2.0")));
    }

    [Fact]
    public void Sidecar_with_a_different_key_is_a_miss_even_though_the_blob_exists()
    {
        var cache = new FileBlobCache(_cacheRoot);
        var key = Key();
        var stored = cache.Put(key, WriteSource("compiled.blob", "compiled-bytes"));
        var sidecar = Directory.EnumerateFiles(_cacheRoot, SidecarFile, SearchOption.AllDirectories).Single();
        File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("2026.3.0", "2026.2.0", StringComparison.Ordinal));

        Assert.True(File.Exists(stored));
        Assert.Null(cache.TryGet(key));
    }

    [Fact]
    public void Blob_without_its_sidecar_is_a_miss()
    {
        var cache = new FileBlobCache(_cacheRoot);
        var key = Key();
        cache.Put(key, WriteSource("compiled.blob", "compiled-bytes"));
        File.Delete(Directory.EnumerateFiles(_cacheRoot, SidecarFile, SearchOption.AllDirectories).Single());

        Assert.Null(cache.TryGet(key));
    }

    [Fact]
    public void Malformed_sidecar_is_a_miss()
    {
        var cache = new FileBlobCache(_cacheRoot);
        var key = Key();
        cache.Put(key, WriteSource("compiled.blob", "compiled-bytes"));
        File.WriteAllText(Directory.EnumerateFiles(_cacheRoot, SidecarFile, SearchOption.AllDirectories).Single(), "not json");

        Assert.Null(cache.TryGet(key));
    }

    [Fact]
    public void Put_copies_the_source_and_leaves_it_in_place()
    {
        var cache = new FileBlobCache(_cacheRoot);
        var source = WriteSource("compiled.blob", "compiled-bytes");

        var stored = cache.Put(Key(), source);

        Assert.NotEqual(source, stored);
        Assert.True(File.Exists(source));
        Assert.Equal("compiled-bytes", File.ReadAllText(stored));
    }

    [Fact]
    public void Put_replaces_an_existing_entry_for_the_same_key()
    {
        var cache = new FileBlobCache(_cacheRoot);
        cache.Put(Key(), WriteSource("first.blob", "first"));

        var stored = cache.Put(Key(), WriteSource("second.blob", "second"));

        Assert.Equal("second", File.ReadAllText(cache.TryGet(Key())!));
        Assert.Equal(stored, cache.TryGet(Key()));
    }

    [Fact]
    public void Put_with_a_missing_source_throws_and_keeps_the_existing_entry_intact()
    {
        var cache = new FileBlobCache(_cacheRoot);
        cache.Put(Key(), WriteSource("first.blob", "first"));

        Assert.Throws<FileNotFoundException>(() => cache.Put(Key(), _temp.Combine("missing.blob")));

        Assert.Equal("first", File.ReadAllText(cache.TryGet(Key())!));
    }

    [Fact]
    public void Put_that_fails_while_staging_leaves_no_partial_entry_visible()
    {
        Directory.CreateDirectory(_cacheRoot);
        File.WriteAllText(Path.Combine(_cacheRoot, ".staging"), "a file where the staging folder should be");
        var cache = new FileBlobCache(_cacheRoot);
        var key = Key();

        Assert.ThrowsAny<IOException>(() => cache.Put(key, WriteSource("compiled.blob", "compiled-bytes")));

        Assert.Null(cache.TryGet(key));
        Assert.Empty(Directory.EnumerateFiles(_cacheRoot, BlobFile, SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_cacheRoot, SidecarFile, SearchOption.AllDirectories));
    }

    [Fact]
    public void Prune_keeps_only_the_current_key_for_the_model()
    {
        var cache = new FileBlobCache(_cacheRoot);
        var current = new BlobCacheKey("model-a", "OpenVINO", "32.0.100.5540", "2026.3.0");
        var oldDriver = new BlobCacheKey("model-a", "OpenVINO", "32.0.100.5000", "2026.3.0");
        var oldRuntime = new BlobCacheKey("model-a", "OpenVINO", "32.0.100.5540", "2026.2.0");
        var otherModel = new BlobCacheKey("model-b", "OpenVINO", "32.0.100.5540", "2026.3.0");
        cache.Put(current, WriteSource("current.blob", "current"));
        cache.Put(oldDriver, WriteSource("driver.blob", "driver"));
        cache.Put(oldRuntime, WriteSource("runtime.blob", "runtime"));
        cache.Put(otherModel, WriteSource("other.blob", "other"));

        var removed = cache.Prune(current);

        Assert.Equal(2, removed);
        Assert.NotNull(cache.TryGet(current));
        Assert.Null(cache.TryGet(oldDriver));
        Assert.Null(cache.TryGet(oldRuntime));
        Assert.NotNull(cache.TryGet(otherModel));
    }

    [Fact]
    public void Prune_on_an_empty_cache_removes_nothing()
    {
        var cache = new FileBlobCache(_cacheRoot);

        Assert.Equal(0, cache.Prune(Key()));
    }

    [Fact]
    public void Cpu_key_with_null_driver_round_trips_through_a_new_cache_instance()
    {
        var key = new BlobCacheKey("model-a", "CPU", null, "2026.3.0");
        new FileBlobCache(_cacheRoot).Put(key, WriteSource("cpu.blob", "cpu-bytes"));

        var reopened = new FileBlobCache(_cacheRoot);
        var found = reopened.TryGet(key);

        Assert.NotNull(found);
        Assert.Equal("cpu-bytes", File.ReadAllText(found));
    }

    [Fact]
    public void Remove_deletes_the_entry_once()
    {
        var cache = new FileBlobCache(_cacheRoot);
        var key = Key();
        cache.Put(key, WriteSource("compiled.blob", "compiled-bytes"));

        Assert.True(cache.Remove(key));
        Assert.Null(cache.TryGet(key));
        Assert.False(cache.Remove(key));
    }

    private static BlobCacheKey Key() => new("model-a", "OpenVINO", "32.0.100.5540", "2026.3.0");

    private static BlobCacheKey Mutate(BlobCacheKey key, string field) => field switch
    {
        "model" => new BlobCacheKey("model-b", key.ExecutionProvider, key.DriverVersion, key.RuntimeVersion),
        "executionProvider" => new BlobCacheKey(key.ModelSha256, "QNN", key.DriverVersion, key.RuntimeVersion),
        "driverVersion" => new BlobCacheKey(key.ModelSha256, key.ExecutionProvider, "32.0.100.5541", key.RuntimeVersion),
        "runtimeVersion" => new BlobCacheKey(key.ModelSha256, key.ExecutionProvider, key.DriverVersion, "2026.3.1"),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private string WriteSource(string name, string content)
    {
        var path = _temp.Combine(name);
        File.WriteAllText(path, content);
        return path;
    }
}
