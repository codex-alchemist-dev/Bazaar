using System.Numerics;

namespace Bazaar.Constellation.Vulkan.Tests;

public class TileDecimationPassTests
{
    // 2D orthographic camera as in the benchmark: clip z is constant, clip w is one.
    private static Matrix4x4 Orthographic(float zoom) => new(
        zoom, 0, 0, 0,
        0, zoom, 0, 0,
        0, 0, 0, 0,
        0, 0, 0.5f, 1);

    private static readonly Matrix4x4 Identity2D = Orthographic(1f);

    [Fact]
    public void Two_points_in_one_cell_keep_only_the_higher_index()
    {
        Vector3[] positions = [new(0.1f, 0.1f, 0), new(0.1001f, 0.1001f, 0)];
        int[] survivors = TileDecimationPass.Decimate(positions, [(0, 2)], Identity2D, 640, 360);
        Assert.Equal([1], survivors);
    }

    [Fact]
    public void Points_in_different_cells_all_survive()
    {
        Vector3[] positions = [new(-0.5f, 0, 0), new(0, 0, 0), new(0.5f, 0, 0)];
        int[] survivors = TileDecimationPass.Decimate(positions, [(0, 3)], Identity2D, 640, 360);
        Assert.Equal([0, 1, 2], survivors);
    }

    [Fact]
    public void Points_outside_the_view_are_dropped()
    {
        Vector3[] positions = [new(2f, 0, 0), new(0, -1.5f, 0), new(0, 0, 0)];
        int[] survivors = TileDecimationPass.Decimate(positions, [(0, 3)], Identity2D, 640, 360);
        Assert.Equal([2], survivors);
    }

    [Fact]
    public void Only_the_listed_tiles_are_considered()
    {
        Vector3[] positions = [new(-0.5f, 0, 0), new(0, 0, 0), new(0.5f, 0, 0), new(0.7f, 0, 0)];
        int[] survivors = TileDecimationPass.Decimate(positions, [(1, 1), (3, 1)], Identity2D, 640, 360);
        Assert.Equal([1, 3], survivors);
    }

    [Fact]
    public void A_point_behind_a_perspective_camera_is_dropped()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 50f);
        Vector3[] positions = [new(0, 0, 0), new(0, 0, 6)];
        int[] survivors = TileDecimationPass.Decimate(positions, [(0, 2)], view * projection, 640, 360);
        Assert.Equal([0], survivors);
    }

    [Fact]
    public void The_survivor_count_never_exceeds_the_cell_count()
    {
        var random = new Random(3);
        var positions = new Vector3[5000];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = new Vector3((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), 0);
        }

        int[] survivors = TileDecimationPass.Decimate(positions, [(0, positions.Length)], Identity2D, 16, 9);
        Assert.InRange(survivors.Length, 1, 16 * 9);
    }
}
