namespace Bazaar.Constellation.Rendering.Core;

/// <summary>A tile's projected box: a pixel rectangle (x right, y as the depth image is stored) and its nearest reversed-Z depth.</summary>
public readonly record struct ScreenBox(float MinX, float MinY, float MaxX, float MaxY, float NearestDepth);

/// <summary>
/// CPU reference occlusion test against a <see cref="HiZPyramid"/>. Conservative: a tile is reported occluded only when its
/// nearest depth is strictly behind the farthest stored depth of every texel it could touch, so a visible tile is never
/// culled. Missed occlusion (a false negative) is allowed and expected, since coarse texels bound their block loosely.
/// </summary>
public static class OcclusionCuller
{
    /// <summary>
    /// The box covers pixels floor(min) through floor(max), clamped to the image. It is occluded when
    /// <paramref name="nearestDepth"/> (reversed-Z, 1 near) is less than the pyramid minimum over those pixels.
    /// A box entirely off the image is not occluded here (the frustum test owns that).
    /// </summary>
    public static bool IsOccluded(HiZPyramid pyramid, float minX, float minY, float maxX, float maxY, float nearestDepth)
    {
        ArgumentNullException.ThrowIfNull(pyramid);
        if (!TryPixelRange(pyramid, minX, minY, maxX, maxY, out int x0, out int y0, out int x1, out int y1))
        {
            return false;
        }

        // Smallest level where the range spans at most 2 texels per axis.
        int level = 0;
        while (level < pyramid.LevelCount - 1 && ((x1 >> level) - (x0 >> level) > 1 || (y1 >> level) - (y0 >> level) > 1))
        {
            level++;
        }

        int w = pyramid.LevelWidth(level);
        int h = pyramid.LevelHeight(level);
        float farthest = float.MaxValue;
        // Texel i covers pixels [i << level, (i + 1) << level); the last texel of an axis also covers the widened remainder,
        // so a pixel past the last texel's start maps to the last texel.
        for (int y = Math.Min(y0 >> level, h - 1); y <= Math.Min(y1 >> level, h - 1); y++)
        {
            for (int x = Math.Min(x0 >> level, w - 1); x <= Math.Min(x1 >> level, w - 1); x++)
            {
                farthest = Math.Min(farthest, pyramid.Get(level, x, y));
            }
        }

        return nearestDepth < farthest;
    }

    public static bool IsOccluded(HiZPyramid pyramid, in ScreenBox box) =>
        IsOccluded(pyramid, box.MinX, box.MinY, box.MaxX, box.MaxY, box.NearestDepth);

    /// <summary>The integer pixel range a rect covers, clamped to the image; false when it misses the image entirely or is not finite.</summary>
    public static bool TryPixelRange(HiZPyramid pyramid, float minX, float minY, float maxX, float maxY, out int x0, out int y0, out int x1, out int y1)
    {
        ArgumentNullException.ThrowIfNull(pyramid);
        x0 = y0 = x1 = y1 = 0;
        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY))
        {
            return false;
        }

        if (maxX < 0f || maxY < 0f || minX >= pyramid.Width || minY >= pyramid.Height)
        {
            return false;
        }

        x0 = Math.Clamp((int)MathF.Floor(minX), 0, pyramid.Width - 1);
        y0 = Math.Clamp((int)MathF.Floor(minY), 0, pyramid.Height - 1);
        x1 = Math.Clamp((int)MathF.Floor(maxX), 0, pyramid.Width - 1);
        y1 = Math.Clamp((int)MathF.Floor(maxY), 0, pyramid.Height - 1);
        return true;
    }

    /// <summary>
    /// Projects a box through a column-major view-projection (clip = M * v, as <see cref="PerspectiveCamera3D.WriteViewProjection"/> writes it)
    /// to a pixel rect and nearest reversed-Z depth. Pixel x = (ndcX * 0.5 + 0.5) * width, y likewise (no flip; flip the rect if the depth
    /// image is stored top-down). Returns false when any corner is at or behind the eye plane (w &lt;= 0): the box cannot be bounded, so the
    /// caller must treat it as visible. Depth outside 0 to 1 (near-plane straddle) is clamped, which only makes the box nearer.
    /// </summary>
    /// <param name="matrixIsReversedZ">False for the 0 (near) to 1 (far) matrix the camera writes; true if the matrix already maps near to 1.</param>
    public static bool TryProject(in TileBounds b, ReadOnlySpan<float> viewProjection, int width, int height, bool matrixIsReversedZ, out ScreenBox box)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(viewProjection.Length, 16);
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        float nearest = 0f;
        for (int i = 0; i < 8; i++)
        {
            float vx = (i & 1) == 0 ? b.MinX : b.MaxX;
            float vy = (i & 2) == 0 ? b.MinY : b.MaxY;
            float vz = (i & 4) == 0 ? b.MinZ : b.MaxZ;
            float cx = (viewProjection[0] * vx) + (viewProjection[4] * vy) + (viewProjection[8] * vz) + viewProjection[12];
            float cy = (viewProjection[1] * vx) + (viewProjection[5] * vy) + (viewProjection[9] * vz) + viewProjection[13];
            float cz = (viewProjection[2] * vx) + (viewProjection[6] * vy) + (viewProjection[10] * vz) + viewProjection[14];
            float cw = (viewProjection[3] * vx) + (viewProjection[7] * vy) + (viewProjection[11] * vz) + viewProjection[15];
            if (cw <= 0f)
            {
                box = default;
                return false;
            }

            float px = (((cx / cw) * 0.5f) + 0.5f) * width;
            float py = (((cy / cw) * 0.5f) + 0.5f) * height;
            float z = Math.Clamp(cz / cw, 0f, 1f);
            float reversed = matrixIsReversedZ ? z : 1f - z;
            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
            nearest = Math.Max(nearest, reversed);
        }

        box = new ScreenBox(minX, minY, maxX, maxY, nearest);
        return true;
    }

    /// <summary>
    /// Wraps a pyramid and a view-projection matrix as the tile occlusion test <see cref="ViewSelector"/> takes.
    /// A box that cannot be projected (it touches the eye plane) counts as visible.
    /// </summary>
    public static Func<TileBounds, bool> AsTest(HiZPyramid pyramid, float[] viewProjection, bool matrixIsReversedZ)
    {
        ArgumentNullException.ThrowIfNull(pyramid);
        ArgumentNullException.ThrowIfNull(viewProjection);
        return bounds => TryProject(bounds, viewProjection, pyramid.Width, pyramid.Height, matrixIsReversedZ, out ScreenBox box)
            && IsOccluded(pyramid, box);
    }
}
