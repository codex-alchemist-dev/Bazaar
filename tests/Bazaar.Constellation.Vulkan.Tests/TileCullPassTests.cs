using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bazaar.Constellation.Vulkan.Tests;

public class TileCullPassTests
{
    // 2D orthographic camera used by the benchmark: clip.z is a constant 0.5 and clip.w is 1.
    private static Matrix4x4 Orthographic(float zoom) => new(
        zoom, 0, 0, 0,
        0, zoom, 0, 0,
        0, 0, 0, 0,
        0, 0, 0.5f, 1);

    [Fact]
    public void A_tile_entry_is_thirty_two_bytes_to_match_the_shader_layout()
    {
        Assert.Equal(TileCullPass.EntryBytes, Unsafe.SizeOf<TileCullEntry>());
    }

    [Fact]
    public void A_box_inside_the_view_is_visible()
    {
        Assert.True(TileCullPass.IsVisible(new Vector3(-0.2f, -0.2f, 0), new Vector3(0.2f, 0.2f, 0), Orthographic(1f)));
    }

    [Fact]
    public void A_box_that_straddles_the_edge_is_visible()
    {
        Assert.True(TileCullPass.IsVisible(new Vector3(0.9f, -0.1f, 0), new Vector3(1.5f, 0.1f, 0), Orthographic(1f)));
    }

    [Fact]
    public void A_box_wholly_beyond_one_side_is_culled()
    {
        Assert.False(TileCullPass.IsVisible(new Vector3(1.5f, -0.1f, 0), new Vector3(2f, 0.1f, 0), Orthographic(1f)));
        Assert.False(TileCullPass.IsVisible(new Vector3(-0.1f, -2f, 0), new Vector3(0.1f, -1.5f, 0), Orthographic(1f)));
    }

    [Fact]
    public void Zooming_in_culls_what_the_wide_view_keeps()
    {
        var min = new Vector3(0.5f, 0.5f, 0);
        var max = new Vector3(0.6f, 0.6f, 0);
        Assert.True(TileCullPass.IsVisible(min, max, Orthographic(1f)));
        Assert.False(TileCullPass.IsVisible(min, max, Orthographic(4f)));
    }

    [Fact]
    public void A_box_behind_a_perspective_camera_is_culled()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 50f);
        Matrix4x4 viewProjection = view * projection;

        Assert.True(TileCullPass.IsVisible(new Vector3(-0.1f), new Vector3(0.1f), viewProjection));
        Assert.False(TileCullPass.IsVisible(new Vector3(-0.1f, -0.1f, 5f), new Vector3(0.1f, 0.1f, 6f), viewProjection));
    }
}
