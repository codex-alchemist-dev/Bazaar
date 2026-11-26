using Bazaar.Constellation.Rendering.Core;
using Xunit;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class ReversedZCameraRelativeTests
{
    private static float DepthAt(PerspectiveCamera3D cam, float x, float y, float z)
    {
        Span<float> m = stackalloc float[16];
        cam.WriteViewProjection(m, 16f / 9f);
        float cz = (m[2] * x) + (m[6] * y) + (m[10] * z) + m[14];
        float cw = (m[3] * x) + (m[7] * y) + (m[11] * z) + m[15];
        return cz / cw;
    }

    [Fact]
    public void ReversedZIsTheDefault() => Assert.True(new PerspectiveCamera3D(0, 0, 0, 0, 0, -1).ReversedZ);

    [Fact]
    public void ReversedFiniteMapsNearToOneAndFarToZero()
    {
        var cam = new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Near: 0.5f, Far: 1000f, ReversedZ: true);
        Assert.Equal(1f, DepthAt(cam, 0, 0, -0.5f), 1e-4f);
        Assert.Equal(0f, DepthAt(cam, 0, 0, -1000f), 1e-4f);
    }

    [Fact]
    public void ForwardFiniteMapsNearToZeroAndFarToOne()
    {
        var cam = new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Near: 0.5f, Far: 1000f, ReversedZ: false);
        Assert.Equal(0f, DepthAt(cam, 0, 0, -0.5f), 1e-4f);
        Assert.Equal(1f, DepthAt(cam, 0, 0, -1000f), 1e-4f);
    }

    [Fact]
    public void ReversedInfiniteFarMapsNearToOneAndApproachesZero()
    {
        var cam = new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Near: 0.5f, Far: float.PositiveInfinity);
        Assert.Equal(1f, DepthAt(cam, 0, 0, -0.5f), 1e-4f);
        Assert.Equal(0.5f / 1.0e6f, DepthAt(cam, 0, 0, -1.0e6f), 1e-9f);
    }

    [Fact]
    public void ForwardInfiniteFarMapsNearToZeroAndApproachesOne()
    {
        var cam = new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Near: 0.5f, Far: float.PositiveInfinity, ReversedZ: false);
        Assert.Equal(0f, DepthAt(cam, 0, 0, -0.5f), 1e-4f);
        Assert.Equal(1f, DepthAt(cam, 0, 0, -1.0e6f), 1e-5f);
    }

    [Theory]
    [InlineData(1000f)]
    [InlineData(float.PositiveInfinity)]
    public void FrustumVisibilityMatchesBetweenModes(float far)
    {
        var fwd = new PerspectiveView(new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Far: far, ReversedZ: false), 1920, 1080);
        var rev = new PerspectiveView(new PerspectiveCamera3D(0, 0, 0, 0, 0, -1, Far: far, ReversedZ: true), 1920, 1080);
        var boxes = new TileBounds[]
        {
            new(-1, -1, -20, 1, 1, -10),       // in front
            new(-1, -1, 10, 1, 1, 20),          // behind
            new(-1, -1, -0.05f, 1, 1, 0.02f),   // straddles the eye, inside the near plane
            new(-1, -1, -990, 1, 1, -980),      // near the far plane
            new(-1, -1, -5000, 1, 1, -4000),    // beyond a finite far
            new(500, -1, -10, 600, 1, -5),      // off to the side
        };
        foreach (TileBounds b in boxes)
        {
            Assert.Equal(fwd.Intersects(b), rev.Intersects(b));
        }

        Assert.True(rev.Intersects(boxes[0]));
        Assert.False(rev.Intersects(boxes[1]));
        Assert.False(rev.Intersects(boxes[5]));
        Assert.Equal(!float.IsPositiveInfinity(far) ? false : true, rev.Intersects(boxes[4]));
    }

    [Fact]
    public void OneUnitApartPointsAtBillionDistanceStayExactWhenRebased()
    {
        const double Far = 1.0e9;
        var eye = new CameraRelativeOrigin(Far, Far, Far);
        (float ax, _, _) = eye.Rebase(Far + 2.0, Far, Far);
        (float bx, _, _) = eye.Rebase(Far + 3.0, Far, Far);
        Assert.Equal(2f, ax);
        Assert.Equal(3f, bx);
        Assert.Equal(1f, bx - ax);

        // Naive: cast world positions to float first, then subtract. Float spacing at 1e9 is 64, so 1 unit is lost.
        float naiveA = (float)(Far + 2.0) - (float)Far;
        float naiveB = (float)(Far + 3.0) - (float)Far;
        Assert.NotEqual(1f, naiveB - naiveA);
    }

    [Fact]
    public void RelativeCameraHasEyeAtOriginAndSeesTheTarget()
    {
        var eye = new CameraRelativeOrigin(1.0e9, 2.0e9, -3.0e9);
        PerspectiveCamera3D cam = eye.MakeRelativeCamera(1.0e9, 2.0e9, -3.0e9 - 10.0);
        Assert.Equal(0f, cam.EyeX);
        Assert.Equal(0f, cam.EyeY);
        Assert.Equal(0f, cam.EyeZ);
        Assert.Equal(-10f, cam.TargetZ);

        Span<float> m = stackalloc float[16];
        eye.WriteRelativeViewProjection(m, 16f / 9f, 1.0e9, 2.0e9, -3.0e9 - 10.0, near: 0.1f, far: 1000f);
        (float rx, float ry, float rz) = eye.Rebase(1.0e9, 2.0e9, -3.0e9 - 10.0);
        float cx = (m[0] * rx) + (m[4] * ry) + (m[8] * rz) + m[12];
        float cy = (m[1] * rx) + (m[5] * ry) + (m[9] * rz) + m[13];
        Assert.Equal(0f, cx, 1e-4f);
        Assert.Equal(0f, cy, 1e-4f);
    }
}
