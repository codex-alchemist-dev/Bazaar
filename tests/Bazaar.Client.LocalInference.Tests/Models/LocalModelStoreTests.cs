using Bazaar.Client.LocalInference.Models;

namespace Bazaar.Client.LocalInference.Tests.Models;

public class LocalModelStoreTests : IDisposable
{
    private static readonly ManifestPolicy Unsigned = new(RequireSignature: false);

    private readonly TempDirectory _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Source => _temp.Combine("source");

    private string StoreRoot => _temp.Combine("store");

    private LocalModelStore Store(ManifestPolicy? policy = null) =>
        new(StoreRoot, policy ?? Unsigned, FailClosedManifestVerifier.Instance);

    private void AssertNoPartialInstall(string model)
    {
        var modelDirectory = Path.Combine(StoreRoot, model);
        if (Directory.Exists(modelDirectory))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(modelDirectory));
        }
    }

    [Fact]
    public async Task Installed_model_is_listed_and_its_file_resolves_to_a_path()
    {
        ModelFixtures.WriteSource(Source, "phi-4-mini", "1.0.0", ("model.onnx", ModelFixtures.Text("weights")));
        var store = Store();

        var installed = await store.InstallAsync(Source, Ct);
        var listed = store.ListInstalled();
        var path = await store.ResolveFileAsync("phi-4-mini", "1.0.0", "model.onnx", Ct);

        Assert.Equal("phi-4-mini", installed.Manifest.Model);
        Assert.Single(listed);
        Assert.Equal("1.0.0", listed[0].Manifest.Version);
        Assert.Equal(Path.Combine(StoreRoot, "phi-4-mini", "1.0.0", "model.onnx"), path);
        Assert.Equal(ModelFixtures.Text("weights"), await File.ReadAllBytesAsync(path, Ct));
        Assert.True(File.Exists(Path.Combine(StoreRoot, "phi-4-mini", "1.0.0", "manifest.json")));
    }

    [Fact]
    public async Task Hash_mismatch_on_install_is_rejected_and_leaves_nothing_behind()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        File.WriteAllBytes(Path.Combine(Source, "a.bin"), ModelFixtures.Text("jello"));

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => Store().InstallAsync(Source, Ct));

        Assert.Equal(ModelIntegrityFailure.HashMismatch, error.Failure);
        Assert.False(Directory.Exists(Path.Combine(StoreRoot, "m", "1")));
        AssertNoPartialInstall("m");
        Assert.Empty(Store().ListInstalled());
    }

    [Fact]
    public async Task Byte_length_mismatch_on_install_is_rejected()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        File.WriteAllBytes(Path.Combine(Source, "a.bin"), ModelFixtures.Text("hello!"));

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => Store().InstallAsync(Source, Ct));

        Assert.Equal(ModelIntegrityFailure.LengthMismatch, error.Failure);
        AssertNoPartialInstall("m");
    }

    [Fact]
    public async Task Missing_file_on_install_is_rejected()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")), ("b.bin", ModelFixtures.Text("world")));
        File.Delete(Path.Combine(Source, "b.bin"));

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => Store().InstallAsync(Source, Ct));

        Assert.Equal(ModelIntegrityFailure.Missing, error.Failure);
        Assert.Equal("b.bin", error.FileName);
    }

    [Fact]
    public async Task Install_is_atomic_when_a_later_file_fails_mid_way()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")), ("b.bin", ModelFixtures.Text("world")));
        File.Delete(Path.Combine(Source, "b.bin"));

        await Assert.ThrowsAsync<ModelIntegrityException>(() => Store().InstallAsync(Source, Ct));

        Assert.False(Directory.Exists(Path.Combine(StoreRoot, "m", "1")));
        AssertNoPartialInstall("m");
    }

    [Fact]
    public async Task Install_is_atomic_when_cancelled_mid_way()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().InstallAsync(Source, cts.Token));

        Assert.False(Directory.Exists(Path.Combine(StoreRoot, "m", "1")));
        AssertNoPartialInstall("m");
    }

    [Fact]
    public async Task Installing_the_same_model_version_twice_is_refused()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        var store = Store();
        await store.InstallAsync(Source, Ct);

        await Assert.ThrowsAsync<ModelStoreException>(() => store.InstallAsync(Source, Ct));
    }

    [Fact]
    public async Task Unknown_model_is_not_selectable()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        var store = Store();
        await store.InstallAsync(Source, Ct);

        await Assert.ThrowsAsync<ModelStoreException>(() => store.ResolveFileAsync("other-model", "1", "a.bin", Ct));
        await Assert.ThrowsAsync<ModelStoreException>(() => store.ResolveFileAsync("m", "2", "a.bin", Ct));
        await Assert.ThrowsAsync<ModelStoreException>(() => store.ResolveFileAsync("../m", "1", "a.bin", Ct));
    }

    [Fact]
    public async Task File_not_listed_in_the_manifest_is_not_selectable()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        var store = Store();
        await store.InstallAsync(Source, Ct);
        File.WriteAllText(Path.Combine(StoreRoot, "m", "1", "extra.bin"), "not listed");

        await Assert.ThrowsAsync<ModelStoreException>(() => store.ResolveFileAsync("m", "1", "extra.bin", Ct));
    }

    [Fact]
    public async Task Manifest_placed_in_the_wrong_folder_is_not_listed_or_selectable()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        var store = Store();
        await store.InstallAsync(Source, Ct);
        var misplaced = Path.Combine(StoreRoot, "m", "9");
        Directory.CreateDirectory(misplaced);
        File.Copy(Path.Combine(StoreRoot, "m", "1", "manifest.json"), Path.Combine(misplaced, "manifest.json"));
        File.Copy(Path.Combine(StoreRoot, "m", "1", "a.bin"), Path.Combine(misplaced, "a.bin"));

        Assert.Single(store.ListInstalled());
        await Assert.ThrowsAsync<ModelStoreException>(() => store.ResolveFileAsync("m", "9", "a.bin", Ct));
    }

    [Fact]
    public async Task Corrupted_installed_file_is_rejected_on_resolve()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        var store = Store();
        await store.InstallAsync(Source, Ct);
        File.WriteAllBytes(Path.Combine(StoreRoot, "m", "1", "a.bin"), ModelFixtures.Text("jello"));

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => store.ResolveFileAsync("m", "1", "a.bin", Ct));

        Assert.Equal(ModelIntegrityFailure.HashMismatch, error.Failure);
    }

    [Fact]
    public async Task Signed_model_installs_only_when_the_verifier_accepts_its_signature()
    {
        var manifest = ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        ModelFixtures.WriteManifest(Source, ModelFixtures.WithSignature(manifest, "sig-bad"));
        var policy = new ManifestPolicy(RequireSignature: true);
        var rejecting = new LocalModelStore(StoreRoot, policy, new FakeManifestVerifier("sig-A"));

        await Assert.ThrowsAsync<ModelIntegrityException>(() => rejecting.InstallAsync(Source, Ct));
        Assert.False(Directory.Exists(Path.Combine(StoreRoot, "m", "1")));

        ModelFixtures.WriteManifest(Source, ModelFixtures.WithSignature(manifest, "sig-A"));
        var accepting = new LocalModelStore(StoreRoot, policy, new FakeManifestVerifier("sig-A"));

        await accepting.InstallAsync(Source, Ct);
        Assert.Single(accepting.ListInstalled());
    }

    [Fact]
    public async Task Unsigned_model_is_refused_by_default_policy_and_fail_closed_verifier()
    {
        ModelFixtures.WriteSource(Source, "m", "1", ("a.bin", ModelFixtures.Text("hello")));
        var store = new LocalModelStore(StoreRoot, new ManifestPolicy(RequireSignature: true), FailClosedManifestVerifier.Instance);

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => store.InstallAsync(Source, Ct));

        Assert.Equal(ModelIntegrityFailure.SignatureMissing, error.Failure);
    }

    [Fact]
    public void Listing_an_empty_or_missing_store_returns_nothing()
    {
        Assert.Empty(Store().ListInstalled());
    }
}
