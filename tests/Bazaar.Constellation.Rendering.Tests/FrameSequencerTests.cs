namespace Bazaar.Constellation.Rendering.Tests;

public class FrameSequencerTests
{
    private static RenderFrame Frame(long graph, long seq) => new(graph, seq, 2, new float[] { 0f, 0f });

    [Fact]
    public void Accepts_first_frame_and_newer_frames()
    {
        var sequencer = new FrameSequencer();
        Assert.True(sequencer.TryAccept(Frame(1, 1)));
        Assert.True(sequencer.TryAccept(Frame(1, 2)));
        Assert.True(sequencer.TryAccept(Frame(2, 0)));
    }

    [Fact]
    public void Drops_equal_and_older_frames()
    {
        var sequencer = new FrameSequencer();
        Assert.True(sequencer.TryAccept(Frame(3, 5)));
        Assert.False(sequencer.TryAccept(Frame(3, 5)));
        Assert.False(sequencer.TryAccept(Frame(3, 4)));
        Assert.False(sequencer.TryAccept(Frame(2, 99)));
    }

    [Fact]
    public void Reset_accepts_older_frames_after_reconnect()
    {
        var sequencer = new FrameSequencer();
        Assert.True(sequencer.TryAccept(Frame(3, 5)));
        sequencer.Reset();
        Assert.True(sequencer.TryAccept(Frame(1, 1)));
    }
}
