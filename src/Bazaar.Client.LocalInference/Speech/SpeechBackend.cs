namespace Bazaar.Client.LocalInference.Speech;

/// <summary>Result of one transcription. <see cref="Text"/> is the raw transcript; cleanup never overwrites it (doc 31, Voice pipeline).</summary>
public sealed record SpeechResult(string Text, BackendTier Tier, TimeSpan Elapsed, bool Partial = false);

/// <summary>Speech-to-text session for one model on one execution provider. Input is 16 kHz mono float32 PCM.</summary>
public interface ISpeechBackend : IAsyncDisposable
{
    BackendTier Tier { get; }

    Task<SpeechResult> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct);
}
