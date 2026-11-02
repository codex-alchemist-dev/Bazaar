using Bazaar.Client.LocalInference.Speech;

namespace Bazaar.Client.LocalInference.Tests.Speech;

/// <summary>Scripted speech backend for tests. Counts calls; never touches a model.</summary>
public sealed class FakeSpeechBackend : ISpeechBackend
{
    private readonly string _text;

    public FakeSpeechBackend(string text = "hello world") => _text = text;

    public BackendTier Tier => BackendTier.Fake;

    public int CallCount { get; private set; }

    public async Task<SpeechResult> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CallCount++;
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        return new SpeechResult(_text, Tier, TimeSpan.FromMilliseconds(1));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
