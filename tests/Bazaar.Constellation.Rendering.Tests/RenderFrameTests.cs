using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class RenderFrameTests
{
    [Fact]
    public void Counts_nodes_from_packed_positions_in_2d_and_3d()
    {
        Assert.Equal(2, new RenderFrame(1, 1, 2, new float[] { 1, 2, 3, 4 }).NodeCount);
        Assert.Equal(2, new RenderFrame(1, 1, 3, new float[] { 1, 2, 3, 4, 5, 6 }).NodeCount);
    }

    [Fact]
    public void Rejects_partial_nodes_and_non_finite_values()
    {
        Assert.Throws<ArgumentException>(() => new RenderFrame(1, 1, 2, new float[] { 1, 2, 3 }));
        Assert.Throws<ArgumentException>(() => new RenderFrame(1, 1, 2, new float[] { 1, float.NaN }));
        Assert.Throws<ArgumentException>(() => new RenderFrame(1, 1, 2, new float[] { float.PositiveInfinity, 0 }));
    }

    [Fact]
    public void Keeps_epoch_and_sequence_as_given()
    {
        var frame = new RenderFrame(7, 3, 2, new float[] { 1, 2 });

        Assert.Equal(7, frame.Epoch);
        Assert.Equal(3, frame.Sequence);
    }

    [Fact]
    public void Rejects_dimensions_other_than_2_or_3()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderFrame(1, 1, 4, new float[] { 1, 2, 3, 4 }));
    }
}
