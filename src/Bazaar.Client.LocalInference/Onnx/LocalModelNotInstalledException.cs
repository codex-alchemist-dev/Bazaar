namespace Bazaar.Client.LocalInference.Onnx;

/// <summary>Thrown when a manifest-listed model file is not present in the model store.</summary>
public sealed class LocalModelNotInstalledException : Exception
{
    /// <summary>Creates the exception for the expected file <paramref name="path"/>.</summary>
    public LocalModelNotInstalledException(string path)
        : base($"The local model file is not installed: {path}")
    {
        Path = path;
    }

    /// <summary>Full path where the model file was expected.</summary>
    public string Path { get; }
}
