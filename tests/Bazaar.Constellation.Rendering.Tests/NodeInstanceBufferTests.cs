using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class NodeInstanceBufferTests
{
    [Fact]
    public void Fill_packs_seven_floats_per_node_in_layout_order()
    {
        var styles = new NodeStyleTable(2, NodeStyle.Create(1f, 1f, 1f, 1f, 2f));
        styles.Set(0, NodeStyle.Create(0.5f, 0.25f, 0.125f, 0.75f, 5f));
        var buffer = new NodeInstanceBuffer();

        buffer.Fill(new float[] { 1.5f, -2f, 3f, 4f }, 2, styles);

        Assert.Equal(2, buffer.InstanceCount);
        Assert.Equal(
            new float[]
            {
                1.5f, -2f, 5f, 0.5f, 0.25f, 0.125f, 0.75f,
                3f, 4f, 2f, 1f, 1f, 1f, 1f,
            },
            buffer.Floats.ToArray());
    }

    [Fact]
    public void Fill_ignores_z_for_three_dimensional_input()
    {
        var styles = new NodeStyleTable(2, NodeStyle.Create(0f, 0f, 0f, 1f, 1f));

        var buffer = new NodeInstanceBuffer();
        buffer.Fill(new float[] { 1f, 2f, 99f, 3f, 4f, -7f }, 3, styles);

        Assert.Equal(2 * NodeInstanceBuffer.FloatsPerInstance, buffer.Floats.Length);
        Assert.Equal(1f, buffer.Floats[0]);
        Assert.Equal(2f, buffer.Floats[1]);
        Assert.Equal(3f, buffer.Floats[7]);
        Assert.Equal(4f, buffer.Floats[8]);
        Assert.DoesNotContain(99f, buffer.Floats.ToArray());
        Assert.DoesNotContain(-7f, buffer.Floats.ToArray());
    }

    [Fact]
    public void Fill_reuses_storage_and_reports_the_latest_count()
    {
        var buffer = new NodeInstanceBuffer();
        buffer.Fill(new float[] { 0f, 0f, 1f, 1f, 2f, 2f }, 2, new NodeStyleTable(3, NodeStyle.Create(0f, 0f, 0f, 1f, 1f)));
        buffer.Fill(new float[] { 9f, 8f }, 2, new NodeStyleTable(1, NodeStyle.Create(0f, 0f, 0f, 1f, 1f)));

        Assert.Equal(1, buffer.InstanceCount);
        Assert.Equal(NodeInstanceBuffer.FloatsPerInstance, buffer.Floats.Length);
        Assert.Equal(9f, buffer.Floats[0]);
        Assert.Equal(8f, buffer.Floats[1]);
    }

    [Fact]
    public void Clear_sets_the_count_to_zero_and_empties_the_view()
    {
        var buffer = new NodeInstanceBuffer();
        buffer.Fill(new float[] { 1f, 2f }, 2, new NodeStyleTable(1, NodeStyle.Create(0f, 0f, 0f, 1f, 1f)));

        buffer.Clear();

        Assert.Equal(0, buffer.InstanceCount);
        Assert.True(buffer.Floats.IsEmpty);
        Assert.True(buffer.Bytes.IsEmpty);
    }

    [Fact]
    public void A_new_buffer_is_empty()
    {
        var buffer = new NodeInstanceBuffer();
        Assert.Equal(0, buffer.InstanceCount);
        Assert.True(buffer.Floats.IsEmpty);
    }

    [Fact]
    public void Bytes_is_the_raw_view_of_the_floats()
    {
        var buffer = new NodeInstanceBuffer();
        buffer.Fill(new float[] { 1f, 2f, 3f, 4f }, 2, new NodeStyleTable(2, NodeStyle.Create(0f, 0f, 0f, 1f, 1f)));

        Assert.Equal(buffer.Floats.Length * sizeof(float), buffer.Bytes.Length);
        Assert.Equal(BitConverter.GetBytes(1f), buffer.Bytes[..sizeof(float)].ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Fill_rejects_dimensions_other_than_two_or_three(int dimensions)
    {
        var buffer = new NodeInstanceBuffer();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            buffer.Fill(new float[4], dimensions, new NodeStyleTable(2, NodeStyle.Create(0f, 0f, 0f, 1f, 1f))));
        Assert.Equal("dimensions", error.ParamName);
    }

    [Fact]
    public void Fill_rejects_a_position_length_that_does_not_match_the_node_count()
    {
        var buffer = new NodeInstanceBuffer();
        var styles = new NodeStyleTable(2, NodeStyle.Create(0f, 0f, 0f, 1f, 1f));

        Assert.Throws<ArgumentException>(() => buffer.Fill(new float[5], 2, styles));
        Assert.Throws<ArgumentException>(() => buffer.Fill(new float[4], 3, styles));
    }

    [Fact]
    public void A_rejected_fill_leaves_the_previous_contents_untouched()
    {
        var buffer = new NodeInstanceBuffer();
        var styles = new NodeStyleTable(1, NodeStyle.Create(0f, 0f, 0f, 1f, 1f));
        buffer.Fill(new float[] { 4f, 5f }, 2, styles);

        Assert.Throws<ArgumentException>(() => buffer.Fill(new float[3], 2, styles));

        Assert.Equal(1, buffer.InstanceCount);
        Assert.Equal(4f, buffer.Floats[0]);
    }
}
