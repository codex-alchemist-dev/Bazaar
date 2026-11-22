using Bazaar.Client.Mobile;
using Bazaar.Client.Mobile.Voice;

namespace Bazaar.Client.Mobile.Tests;

public class DictationControllerTests
{
    private sealed class FakeRecorder : IDictationRecorder
    {
        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public void Start() => Starts++;

        public ReadOnlyMemory<float> Finish()
        {
            Stops++;
            return new float[] { 0.1f, 0.2f, 0.3f };
        }
    }

    private sealed class FakeTranscriber(string text) : IDictationTranscriber
    {
        public int Calls { get; private set; }

        public Task<string> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(text);
        }
    }

    private sealed class FakeCleaner(string? cleaned) : IDictationCleaner
    {
        public Task<string?> CleanAsync(string rawText, CancellationToken ct) => Task.FromResult(cleaned);
    }

    private static (DictationController Controller, FakeRecorder Recorder, FakeTranscriber Transcriber, List<DictationResult> Results) Build(string raw = " um send the report ", string? cleaned = "Send the report.")
    {
        var recorder = new FakeRecorder();
        var transcriber = new FakeTranscriber(raw);
        var controller = new DictationController(recorder, transcriber, new FakeCleaner(cleaned));
        var results = new List<DictationResult>();
        controller.TranscriptReady += (_, r) => results.Add(r);
        return (controller, recorder, transcriber, results);
    }

    private static async Task SettleAsync(CancellationToken ct) => await Task.Delay(30, ct);

    [Fact]
    public async Task A_release_in_the_centre_transcribes_cleans_and_hands_the_text_to_the_composer()
    {
        var (c, recorder, _, results) = Build();

        c.OnDictationChanged(null, true);
        Assert.True(c.IsRecording);
        c.OnDictationChanged(null, false);
        c.OnRadialReleased(null, new RadialRelease(RadialOutcome.PlaceTranscript, null));
        await SettleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, recorder.Starts);
        Assert.Equal(1, recorder.Stops);
        var result = Assert.Single(results);
        Assert.Equal("um send the report", result.RawText);
        Assert.Equal("Send the report.", result.Text);
    }

    [Fact]
    public async Task A_rejected_cleanup_falls_back_to_the_raw_transcript()
    {
        var (c, _, _, results) = Build(cleaned: null);

        c.OnDictationChanged(null, true);
        c.OnDictationChanged(null, false);
        c.OnRadialReleased(null, new RadialRelease(RadialOutcome.PlaceTranscript, null));
        await SettleAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Null(result.CleanedText);
        Assert.Equal("um send the report", result.Text);
    }

    [Theory]
    [InlineData(RadialOutcome.OpenSector)]
    [InlineData(RadialOutcome.Cancelled)]
    public async Task Entering_or_releasing_in_a_sector_or_cancelling_discards_the_audio(RadialOutcome outcome)
    {
        var (c, _, transcriber, results) = Build();

        c.OnDictationChanged(null, true);
        c.OnDictationChanged(null, false);
        c.OnRadialReleased(null, new RadialRelease(outcome, outcome == RadialOutcome.OpenSector ? "Kanban" : null));
        await SettleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, transcriber.Calls);
        Assert.Empty(results);
    }

    [Fact]
    public async Task An_empty_transcript_places_nothing()
    {
        var (c, _, _, results) = Build(raw: "   ");

        c.OnDictationChanged(null, true);
        c.OnDictationChanged(null, false);
        c.OnRadialReleased(null, new RadialRelease(RadialOutcome.PlaceTranscript, null));
        await SettleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Disposing_mid_recording_stops_the_recorder_and_places_nothing()
    {
        var (c, recorder, _, results) = Build();

        c.OnDictationChanged(null, true);
        c.Dispose();
        c.OnRadialReleased(null, new RadialRelease(RadialOutcome.PlaceTranscript, null));
        await SettleAsync(TestContext.Current.CancellationToken);

        Assert.False(c.IsRecording);
        Assert.Equal(1, recorder.Stops);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Audio_from_an_interrupted_gesture_is_dropped_when_the_next_one_starts()
    {
        var (c, _, transcriber, results) = Build();

        c.OnDictationChanged(null, true);
        c.OnDictationChanged(null, false);
        // The gesture was cancelled: no release arrived. A new one starts, then a sector release discards it.
        c.OnDictationChanged(null, true);
        c.OnDictationChanged(null, false);
        c.OnRadialReleased(null, new RadialRelease(RadialOutcome.OpenSector, "Reply"));
        await SettleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, transcriber.Calls);
        Assert.Empty(results);
    }
}
