using Bazaar.Client.LocalInference.Speech;

namespace Bazaar.Client.LocalInference.Tests.Speech;

public class VoiceCleanupPipelineTests
{
    private static readonly Func<string, string, bool> AcceptAll = (_, _) => true;

    [Fact]
    public async Task Raw_transcript_is_preserved_next_to_cleaned_text()
    {
        const string raw = "um so the build is uh green";
        var speech = new FakeSpeechBackend(raw);
        var transcript = await speech.TranscribeAsync(new float[16], CancellationToken.None);
        var backend = new FakeLocalBackend().Reply("The build is green.");
        var pipeline = new VoiceCleanupPipeline(backend, AcceptAll);

        var result = await pipeline.CleanAsync(transcript.Text, CancellationToken.None);

        Assert.Equal(raw, result.RawText);
        Assert.Equal("The build is green.", result.CleanedText);
        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public async Task Model_error_returns_raw_with_null_cleaned_text()
    {
        var backend = new FakeLocalBackend().Throw(new TimeoutException("slow"));
        var pipeline = new VoiceCleanupPipeline(backend, AcceptAll);

        var result = await pipeline.CleanAsync("raw words", CancellationToken.None);

        Assert.Equal("raw words", result.RawText);
        Assert.Null(result.CleanedText);
    }

    [Fact]
    public async Task Guard_failure_returns_raw_with_null_cleaned_text()
    {
        var backend = new FakeLocalBackend().Reply("Invented 42 figure.");
        var pipeline = new VoiceCleanupPipeline(backend, (_, _) => false);

        var result = await pipeline.CleanAsync("raw with 24", CancellationToken.None);

        Assert.Equal("raw with 24", result.RawText);
        Assert.Null(result.CleanedText);
    }

    [Fact]
    public async Task Guard_exception_returns_raw_with_null_cleaned_text()
    {
        var backend = new FakeLocalBackend().Reply("cleaned");
        var pipeline = new VoiceCleanupPipeline(backend, (_, _) => throw new InvalidOperationException("guard broke"));

        var result = await pipeline.CleanAsync("raw", CancellationToken.None);

        Assert.Equal("raw", result.RawText);
        Assert.Null(result.CleanedText);
    }

    [Fact]
    public async Task Guard_receives_raw_and_cleaned_text()
    {
        string? seenRaw = null;
        string? seenCleaned = null;
        var backend = new FakeLocalBackend().Reply("cleaned out");
        var pipeline = new VoiceCleanupPipeline(backend, (raw, cleaned) =>
        {
            seenRaw = raw;
            seenCleaned = cleaned;
            return true;
        });

        await pipeline.CleanAsync("raw in", CancellationToken.None);

        Assert.Equal("raw in", seenRaw);
        Assert.Equal("cleaned out", seenCleaned);
    }

    [Fact]
    public async Task Cancelled_token_throws_and_does_not_call_the_backend()
    {
        var backend = new FakeLocalBackend().Reply("never");
        var pipeline = new VoiceCleanupPipeline(backend, AcceptAll);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.CleanAsync("raw", cts.Token));
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public async Task Cancellation_during_cleanup_propagates_instead_of_returning_raw()
    {
        using var cts = new CancellationTokenSource();
        var backend = new FakeLocalBackend().Throw(new OperationCanceledException("cancelled mid-run"));
        var pipeline = new VoiceCleanupPipeline(backend, AcceptAll);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.CleanAsync("raw", cts.Token));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new VoiceCleanupPipeline(null!, AcceptAll));
        Assert.Throws<ArgumentNullException>(() => new VoiceCleanupPipeline(new FakeLocalBackend(), null!));
    }
}
