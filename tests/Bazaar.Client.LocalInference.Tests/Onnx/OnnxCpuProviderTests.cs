using Bazaar.Client.LocalInference.Onnx;

namespace Bazaar.Client.LocalInference.Tests.Onnx;

public class OnnxCpuProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-onnx-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static ModelManifest Manifest(params string[] fileNames) =>
        new("identity", "0.0.1", "Apache-2.0", fileNames.Select(name => new ManifestFile(name, "", 139)).ToList(), "1.31.1", null);

    private string InstallFixture(ModelManifest manifest, string fileName)
    {
        var target = new LocalModelStore(_root).ResolveFile(manifest, new ManifestFile(fileName, "", 139));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "identity.onnx"), target);
        return target;
    }

    [Fact]
    public async Task Selects_a_CPU_backend_when_the_model_file_is_installed()
    {
        var manifest = Manifest("identity.onnx");
        InstallFixture(manifest, "identity.onnx");
        var provider = new OnnxCpuProvider(new LocalModelStore(_root));

        await using var backend = await provider.SelectAsync(manifest, CancellationToken.None);
        var result = await backend.RunAsync(new LocalTaskRequest(LocalTaskKind.UiAssist, "ok", 8, "key"), CancellationToken.None);

        Assert.Equal(BackendTier.Cpu, backend.Tier);
        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public async Task Missing_model_file_throws_LocalModelNotInstalledException()
    {
        var manifest = Manifest("identity.onnx");
        var provider = new OnnxCpuProvider(new LocalModelStore(_root));

        var error = await Assert.ThrowsAsync<LocalModelNotInstalledException>(() => provider.SelectAsync(manifest, CancellationToken.None));

        Assert.Contains("identity.onnx", error.Path);
    }

    [Fact]
    public async Task Manifest_without_an_onnx_file_throws_ArgumentException()
    {
        var manifest = Manifest("weights.bin");
        var provider = new OnnxCpuProvider(new LocalModelStore(_root));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SelectAsync(manifest, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_before_selection_throws_OperationCanceledException()
    {
        var manifest = Manifest("identity.onnx");
        InstallFixture(manifest, "identity.onnx");
        var provider = new OnnxCpuProvider(new LocalModelStore(_root));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SelectAsync(manifest, cts.Token));
    }
}
