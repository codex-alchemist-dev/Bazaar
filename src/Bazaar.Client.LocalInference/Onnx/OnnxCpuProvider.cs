namespace Bazaar.Client.LocalInference.Onnx;

/// <summary>
/// Selection chain step for the ONNX Runtime CPU provider (doc 31). It runs on every platform, including Linux CI.
/// </summary>
public sealed class OnnxCpuProvider : IExecutionProviderSelector
{
    private readonly LocalModelStore _store;

    /// <summary>Creates a provider that finds model files in <paramref name="store"/>.</summary>
    public OnnxCpuProvider(LocalModelStore store) => _store = store;

    /// <summary>
    /// Returns a CPU backend for the manifest's <c>.onnx</c> file. Throws <see cref="LocalModelNotInstalledException"/>
    /// when that file is not in the store, and <see cref="ArgumentException"/> when the manifest lists no <c>.onnx</c> file.
    /// </summary>
    /// <remarks>
    /// TODO(component): the OpenVINO / Windows ML NPU step (spike S21) plugs in here, ahead of this CPU step.
    /// </remarks>
    public Task<ILocalModelBackend> SelectAsync(ModelManifest manifest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var file = manifest.Files.FirstOrDefault(candidate => candidate.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The manifest lists no .onnx file.", nameof(manifest));
        var path = _store.ResolveFile(manifest, file);
        if (!File.Exists(path))
        {
            throw new LocalModelNotInstalledException(path);
        }

        ILocalModelBackend backend = new OnnxCpuBackend(path);
        return Task.FromResult(backend);
    }
}
