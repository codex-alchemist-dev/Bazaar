using Bazaar.Client.LocalInference.Speech;

namespace Bazaar.Client.LocalInference.Tests.Speech;

public class SpeechBackendTests
{
    private static readonly ReadOnlyMemory<float> _oneSecond = new float[16_000];

    [Fact]
    public async Task Transcription_returns_raw_text_with_tier_and_partial_defaulting_to_false()
    {
        var backend = new FakeSpeechBackend("turn the lights off");

        var result = await backend.TranscribeAsync(_oneSecond, CancellationToken.None);

        Assert.Equal("turn the lights off", result.Text);
        Assert.Equal(BackendTier.Fake, result.Tier);
        Assert.False(result.Partial);
        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public async Task Cancelled_token_stops_transcription_without_a_call()
    {
        var backend = new FakeSpeechBackend();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.TranscribeAsync(_oneSecond, cts.Token));
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public void Default_model_ids_match_the_settings_table()
    {
        Assert.Equal("whisper", SpeechModelDefaults.WindowsStt);
        Assert.Equal("whisper-small", SpeechModelDefaults.AndroidStt);
    }
}
