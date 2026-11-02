namespace Bazaar.Client.LocalInference.Speech;

/// <summary>
/// CPU speech backend for a Whisper model file (doc 31, Settings: coworker.model.stt.windows).
/// Only the selection path exists here: the model file must be present, and decoding is NOT implemented.
/// Decoder runtime is a follow-up, and the Whisper licence is unresolved (counsel, doc 31 Model candidates).
/// Do not ship or bundle a Whisper model until both are settled.
/// </summary>
public sealed class WhisperCpuBackend : ISpeechBackend
{
    /// <param name="modelId">Manifest model id, for example <c>whisper</c>.</param>
    /// <param name="modelPath">Path to the model file, as resolved from the local model store.</param>
    /// <exception cref="LocalModelMissingException">The model file does not exist.</exception>
    public WhisperCpuBackend(string modelId, string modelPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new LocalModelMissingException(modelId, modelPath);
        }

        ModelId = modelId;
        ModelPath = modelPath;
    }

    public BackendTier Tier => BackendTier.Cpu;

    public string ModelId { get; }

    public string ModelPath { get; }

    /// <summary>Not implemented: the Whisper CPU decoder is a follow-up.</summary>
    public Task<SpeechResult> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct) =>
        throw new NotSupportedException("Whisper CPU decoding is not implemented yet (follow-up to doc 31 WP10).");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
