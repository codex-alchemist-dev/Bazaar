using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class Camera2DTests
{
    [Fact]
    public void Screen_and_world_round_trip()
    {
        var camera = new Camera2D();
        camera.CenterOn(12f, -4f);
        camera.ZoomAt(100f, 50f, 800, 600, 2f);
        (float wx, float wy) = camera.ScreenToWorld(300f, 220f, 800, 600);
        (float sx, float sy) = camera.WorldToScreen(wx, wy, 800, 600);
        Assert.Equal(300f, sx, 3);
        Assert.Equal(220f, sy, 3);
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_pointer_fixed()
    {
        var camera = new Camera2D();
        (float before, float beforeY) = camera.ScreenToWorld(600f, 150f, 800, 600);
        camera.ZoomAt(600f, 150f, 800, 600, 4f);
        (float after, float afterY) = camera.ScreenToWorld(600f, 150f, 800, 600);
        Assert.Equal(before, after, 3);
        Assert.Equal(beforeY, afterY, 3);
    }

    [Fact]
    public void Zoom_is_clamped()
    {
        var camera = new Camera2D();
        for (int i = 0; i < 100; i++)
        {
            camera.ZoomAt(0f, 0f, 800, 600, 2f);
        }

        var matrix = new float[16];
        camera.WriteProjection(matrix, 800, 600);
        Assert.Equal(2f * Camera2D.MaxZoom / 800f, matrix[0], 3);
    }

    [Fact]
    public void Pan_moves_content_with_the_pointer()
    {
        var camera = new Camera2D();
        camera.PanPixels(10f, 0f);
        (float x, _) = camera.WorldToScreen(-10f, 0f, 800, 600);
        Assert.Equal(400f, x, 3);
    }

    [Fact]
    public void Projection_maps_the_view_centre_to_clip_origin()
    {
        var camera = new Camera2D();
        camera.CenterOn(7f, 9f);
        var m = new float[16];
        camera.WriteProjection(m, 800, 600);
        float clipX = (m[0] * 7f) + m[12];
        float clipY = (m[5] * 9f) + m[13];
        Assert.Equal(0f, clipX, 4);
        Assert.Equal(0f, clipY, 4);
        Assert.Equal(1f, m[15]);
    }
}
