using Bazaar.Constellation.Rendering.Core;
using Xunit;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class HiZOcclusionTests
{
    private static float[] Wall(int w, int h, int x0, int y0, int x1, int y1, float depth)
    {
        float[] d = new float[w * h];
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                d[(y * w) + x] = depth;
            }
        }

        return d;
    }

    [Theory]
    [InlineData(64, 64, 7)]
    [InlineData(100, 37, 8)]
    [InlineData(1, 1, 1)]
    [InlineData(5, 1, 4)]
    [InlineData(1920, 1080, 12)]
    public void MipChainSizesHalveRoundingUp(int w, int h, int levels)
    {
        var p = new HiZPyramid(new float[w * h], w, h);
        Assert.Equal(levels, p.LevelCount);
        for (int l = 1; l < p.LevelCount; l++)
        {
            Assert.Equal((p.LevelWidth(l - 1) + 1) / 2, p.LevelWidth(l));
            Assert.Equal((p.LevelHeight(l - 1) + 1) / 2, p.LevelHeight(l));
        }

        Assert.Equal(1, p.LevelWidth(levels - 1));
        Assert.Equal(1, p.LevelHeight(levels - 1));
    }

    [Fact]
    public void MipKeepsMinimumIncludingOddEdges()
    {
        float[] d = [0.9f, 0.5f, 0.7f, 0.8f, 0.6f, 0.4f, 0.3f, 0.2f, 0.1f]; // 3x3
        var p = new HiZPyramid(d, 3, 3);
        Assert.Equal(0.5f, p.Get(1, 0, 0));
        Assert.Equal(0.4f, p.Get(1, 1, 0)); // column 2, rows 0..1
        Assert.Equal(0.2f, p.Get(1, 0, 1)); // row 2, columns 0..1
        Assert.Equal(0.1f, p.Get(1, 1, 1));
        Assert.Equal(0.1f, p.Get(2, 0, 0));
    }

    [Fact]
    public void BoxBehindNearWallIsOccluded()
    {
        float[] d = Wall(64, 64, 8, 8, 55, 55, 0.9f);
        var p = new HiZPyramid(d, 64, 64);
        Assert.True(OcclusionCuller.IsOccluded(p, 16f, 16f, 40f, 40f, 0.5f));
    }

    [Fact]
    public void BoxInFrontOfWallIsNotOccluded()
    {
        float[] d = Wall(64, 64, 8, 8, 55, 55, 0.5f);
        var p = new HiZPyramid(d, 64, 64);
        Assert.False(OcclusionCuller.IsOccluded(p, 16f, 16f, 40f, 40f, 0.9f));
    }

    [Fact]
    public void BoxStraddlingWallEdgeIsNotOccluded()
    {
        float[] d = Wall(64, 64, 8, 8, 31, 55, 0.9f); // wall covers x 8..31 only
        var p = new HiZPyramid(d, 64, 64);
        Assert.False(OcclusionCuller.IsOccluded(p, 24f, 16f, 40f, 40f, 0.5f));
    }

    [Fact]
    public void OffImageBoxIsNotOccludedAndNonFiniteIsSafe()
    {
        var p = new HiZPyramid(Wall(8, 8, 0, 0, 7, 7, 1f), 8, 8);
        Assert.False(OcclusionCuller.IsOccluded(p, 100f, 100f, 120f, 120f, 0f));
        Assert.False(OcclusionCuller.IsOccluded(p, float.NaN, 0f, 1f, 1f, 0f));
    }

    [Fact]
    public void ProjectedBoxBehindWallIsOccludedViaCamera()
    {
        const int W = 128;
        const int H = 128;
        var cam = new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Near: 0.1f, Far: 1000f);
        float[] m = new float[16];
        cam.WriteViewProjection(m, 1f);

        // A near slab at z=-10 covering the screen, rasterised as a flat depth image.
        var wallBounds = new TileBounds(-50f, -50f, -10.5f, 50f, 50f, -10f);
        Assert.True(OcclusionCuller.TryProject(wallBounds, m, W, H, false, out ScreenBox wall));
        float[] d = Wall(W, H, 0, 0, W - 1, H - 1, wall.NearestDepth);
        var p = new HiZPyramid(d, W, H);

        var behind = new TileBounds(-1f, -1f, -60f, 1f, 1f, -50f);
        Assert.True(OcclusionCuller.TryProject(behind, m, W, H, false, out ScreenBox bBehind));
        Assert.True(OcclusionCuller.IsOccluded(p, bBehind));

        var front = new TileBounds(-0.5f, -0.5f, -5f, 0.5f, 0.5f, -4f);
        Assert.True(OcclusionCuller.TryProject(front, m, W, H, false, out ScreenBox bFront));
        Assert.False(OcclusionCuller.IsOccluded(p, bFront));
    }

    [Fact]
    public void BoxCrossingEyePlaneCannotBeProjected()
    {
        var cam = new PerspectiveCamera3D(0, 0, 0, 0, 0, -1);
        float[] m = new float[16];
        cam.WriteViewProjection(m, 1f);
        Assert.False(OcclusionCuller.TryProject(new TileBounds(-1, -1, -1, 1, 1, 1), m, 64, 64, false, out _));
    }

    [Fact]
    public void NeverCullsABoxThatPerPixelTestSaysIsVisible()
    {
        var rng = new Random(12345);
        for (int iter = 0; iter < 400; iter++)
        {
            int w = rng.Next(1, 90);
            int h = rng.Next(1, 90);
            float[] d = new float[w * h];
            bool blocky = iter % 2 == 0;
            for (int i = 0; i < d.Length; i++)
            {
                d[i] = blocky ? 0.2f + (0.16f * ((((i % w) / 8) + ((i / w) / 8)) % 5)) : (float)rng.NextDouble();
            }

            var p = new HiZPyramid(d, w, h);
            for (int q = 0; q < 60; q++)
            {
                float minX = (float)((rng.NextDouble() * (w + 20)) - 10);
                float minY = (float)((rng.NextDouble() * (h + 20)) - 10);
                float maxX = minX + (float)(rng.NextDouble() * 40);
                float maxY = minY + (float)(rng.NextDouble() * 40);
                float near = (float)rng.NextDouble();
                if (!OcclusionCuller.IsOccluded(p, minX, minY, maxX, maxY, near))
                {
                    continue;
                }

                // Occluded claim: every covered pixel must be strictly nearer than the box.
                Assert.True(OcclusionCuller.TryPixelRange(p, minX, minY, maxX, maxY, out int x0, out int y0, out int x1, out int y1));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Assert.True(d[(y * w) + x] > near, $"culled a visible box at pixel {x},{y}");
                    }
                }
            }
        }
    }
}
