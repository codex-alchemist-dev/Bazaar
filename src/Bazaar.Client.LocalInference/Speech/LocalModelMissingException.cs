namespace Bazaar.Client.LocalInference.Speech;

/// <summary>Thrown when a selected model has no file at the expected path in the local model store.</summary>
public sealed class LocalModelMissingException : Exception
{
    public LocalModelMissingException(string modelId, string modelPath)
        : base($"Local model '{modelId}' is not installed at '{modelPath}'.")
    {
        ModelId = modelId;
        ModelPath = modelPath;
    }

    public string ModelId { get; }

    public string ModelPath { get; }
}
