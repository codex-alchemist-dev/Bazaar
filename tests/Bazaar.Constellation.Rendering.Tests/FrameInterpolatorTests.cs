namespace Bazaar.Constellation.Rendering.Tests;

public class FrameInterpolatorTests
{
    private static RenderFrame Frame(params float[] xy) => new(1, 1, 2, xy);

    [Fact]
    public void Returns_nothing_before_the_first_frame()
    {
        var interpolator = new FrameInterpolator();
        Assert.False(interpolator.Sample(0, new float[2]));
    }

    [Fact]
    public void First_frame_is_drawn_at_its_own_positions()
    {
        var interpolator = new FrameInterpolator(0.5);
        interpolator.Push(Frame(10f, 20f), 0);
        var output = new float[2];
        Assert.True(interpolator.Sample(0, output));
        Assert.Equal(new float[] { 10f, 20f }, output);
    }

    [Fact]
    public void Blends_to_the_target_and_settles_after_the_duration()
    {
        var interpolator = new FrameInterpolator(1.0);
        interpolator.Push(Frame(0f, 0f), 0);
        interpolator.Push(Frame(10f, 0f), 0);
        var mid = new float[2];
        interpolator.Sample(0.5, mid);
        Assert.Equal(5f, mid[0], 4);

        var end = new float[2];
        interpolator.Sample(2.0, end);
        Assert.Equal(10f, end[0], 4);
    }

    [Fact]
    public void Continues_from_the_drawn_state_when_a_new_frame_arrives_mid_blend()
    {
        var interpolator = new FrameInterpolator(1.0);
        interpolator.Push(Frame(0f, 0f), 0);
        interpolator.Push(Frame(10f, 0f), 0);
        var drawnBefore = new float[2];
        interpolator.Sample(0.5, drawnBefore);

        interpolator.Push(Frame(20f, 0f), 0.5);
        var justAfter = new float[2];
        interpolator.Sample(0.5, justAfter);
        Assert.Equal(drawnBefore[0], justAfter[0], 4);
    }

    [Fact]
    public void New_nodes_appear_at_their_target_without_jumping_existing_ones()
    {
        var interpolator = new FrameInterpolator(1.0);
        interpolator.Push(Frame(0f, 0f), 0);
        interpolator.Push(new RenderFrame(1, 2, 2, new float[] { 10f, 0f, 50f, 60f }), 0);
        var output = new float[4];
        interpolator.Sample(0, output);
        Assert.Equal(0f, output[0], 4);
        Assert.Equal(50f, output[2], 4);
        Assert.Equal(60f, output[3], 4);
    }

    [Fact]
    public void Rejects_a_destination_smaller_than_the_frame()
    {
        var interpolator = new FrameInterpolator();
        interpolator.Push(Frame(1f, 2f), 0);
        Assert.Throws<ArgumentException>(() => interpolator.Sample(0, new float[1]));
    }
}
