namespace Bazaar.Constellation.Rendering.Core;

/// <summary>Camera-side tests the tile selector needs: is a box visible, and how many pixels wide is it.</summary>
public abstract class ViewVolume
{
    public abstract bool Intersects(in TileBounds b);

    /// <summary>Approximate on-screen size in pixels of the box's largest extent. Large when the camera is inside it.</summary>
    public abstract float ProjectedPixels(in TileBounds b);
}

/// <summary>The 2D pan and zoom view.</summary>
public sealed class OrthoView : ViewVolume
{
    private readonly float _minX;
    private readonly float _minY;
    private readonly float _maxX;
    private readonly float _maxY;
    private readonly float _zoom;

    public OrthoView(Camera2D camera, int widthPixels, int heightPixels)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _zoom = camera.Zoom;
        float halfW = widthPixels * 0.5f / _zoom;
        float halfH = heightPixels * 0.5f / _zoom;
        _minX = camera.CenterX - halfW;
        _maxX = camera.CenterX + halfW;
        _minY = camera.CenterY - halfH;
        _maxY = camera.CenterY + halfH;
    }

    public override bool Intersects(in TileBounds b) =>
        b.MaxX >= _minX && b.MinX <= _maxX && b.MaxY >= _minY && b.MinY <= _maxY;

    public override float ProjectedPixels(in TileBounds b) => MathF.Max(b.MaxX - b.MinX, b.MaxY - b.MinY) * _zoom;
}

/// <summary>A look-at perspective camera for 3D mode. Right-handed, depth 0 to 1 (Vulkan).</summary>
public sealed record PerspectiveCamera3D(
    float EyeX, float EyeY, float EyeZ,
    float TargetX, float TargetY, float TargetZ,
    float VerticalFovRadians = 1.0471976f, float Near = 0.1f, float Far = 1.0e9f)
/// <summary>
/// A look-at perspective camera for 3D mode. Right-handed, depth 0 to 1 (Vulkan). With <c>ReversedZ</c> (the default)
/// near maps to depth 1 and far to 0, which spreads float depth precision evenly over huge ranges; a positive-infinity
/// <c>Far</c> gives an infinite far plane in either mode.
/// </summary>
public sealed record PerspectiveCamera3D(
    float EyeX, float EyeY, float EyeZ,
    float TargetX, float TargetY, float TargetZ,
    float VerticalFovRadians = 1.0471976f, float Near = 0.1f, float Far = 1.0e9f, bool ReversedZ = true)
{
    /// <summary>Writes the column-major view-projection matrix (clip = M * world).</summary>
    public void WriteViewProjection(Span<float> m, float aspect)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(m.Length, 16);
        (float zx, float zy, float zz) = Normalize(EyeX - TargetX, EyeY - TargetY, EyeZ - TargetZ);
        // up = (0, 1, 0): x = cross(up, z) = (zz, 0, -zx)
        (float xx, float xy, float xz) = Normalize(zz, 0f, -zx);
        float yx = (zy * xz) - (zz * xy);
        float yy = (zz * xx) - (zx * xz);
        float yz = (zx * xy) - (zy * xx);

        float f = 1f / MathF.Tan(VerticalFovRadians * 0.5f);
        float a = Far / (Near - Far);
        float b = Near * Far / (Near - Far);
        // Depth is (a * z + b) / -z for view-space z. Forward: near 0, far 1. Reversed: near 1, far 0.
        bool infinite = float.IsPositiveInfinity(Far);
        float a;
        float b;
        if (ReversedZ)
        {
            a = infinite ? 0f : Near / (Far - Near);
            b = infinite ? Near : Near * Far / (Far - Near);
        }
        else
        {
            a = infinite ? -1f : Far / (Near - Far);
            b = infinite ? -Near : Near * Far / (Near - Far);
        }

        float tx = -((xx * EyeX) + (xy * EyeY) + (xz * EyeZ));
        float ty = -((yx * EyeX) + (yy * EyeY) + (yz * EyeZ));
        float tz = -((zx * EyeX) + (zy * EyeY) + (zz * EyeZ));

        // View rows: x, y, z. Clip rows: (f/aspect)*x, f*y, a*z + b*w, -z.
        Span<float> r0 = stackalloc float[4] { f / aspect * xx, f / aspect * xy, f / aspect * xz, f / aspect * tx };
        Span<float> r1 = stackalloc float[4] { f * yx, f * yy, f * yz, f * ty };
        Span<float> r2 = stackalloc float[4] { a * zx, a * zy, a * zz, (a * tz) + b };
        Span<float> r3 = stackalloc float[4] { -zx, -zy, -zz, -tz };
        for (int col = 0; col < 4; col++)
        {
            m[(col * 4) + 0] = r0[col];
            m[(col * 4) + 1] = r1[col];
            m[(col * 4) + 2] = r2[col];
            m[(col * 4) + 3] = r3[col];
        }
    }

    private static (float X, float Y, float Z) Normalize(float x, float y, float z)
    {
        float len = MathF.Sqrt((x * x) + (y * y) + (z * z));
        return len <= 0f ? (0f, 0f, 1f) : (x / len, y / len, z / len);
    }
}

/// <summary>The 3D view: frustum planes from the view-projection matrix, pixel size from the field of view.</summary>
public sealed class PerspectiveView : ViewVolume
{
    private readonly float[] _planes = new float[24]; // 6 planes of (a, b, c, d); inside is positive.
    private readonly PerspectiveCamera3D _camera;
    private readonly float _pixelsPerRadianScale;

    public PerspectiveView(PerspectiveCamera3D camera, int widthPixels, int heightPixels)
    {
        ArgumentNullException.ThrowIfNull(camera);
        _camera = camera;
        _pixelsPerRadianScale = heightPixels / (2f * MathF.Tan(camera.VerticalFovRadians * 0.5f));
        Span<float> m = stackalloc float[16];
        camera.WriteViewProjection(m, (float)widthPixels / heightPixels);

        // Gribb and Hartmann: row i, column j is m[(j * 4) + i]. Depth is 0 to 1, so near is row 2 alone.
        // Gribb and Hartmann: row i, column j is m[(j * 4) + i]. Depth is 0 to 1, so the depth-0 plane is row 2 alone
        // and the depth-1 plane is row 3 minus row 2. Forward Z: depth 0 is near. Reversed Z: depth 0 is far.
        bool reversed = camera.ReversedZ;
        for (int p = 0; p < 6; p++)
        {
            for (int j = 0; j < 4; j++)
            {
                float r3 = m[(j * 4) + 3];
                float v = p switch
                {
                    0 => r3 + m[(j * 4) + 0],
                    1 => r3 - m[(j * 4) + 0],
                    2 => r3 + m[(j * 4) + 1],
                    3 => r3 - m[(j * 4) + 1],
                    4 => m[(j * 4) + 2],
                    _ => r3 - m[(j * 4) + 2],
                    4 => reversed ? r3 - m[(j * 4) + 2] : m[(j * 4) + 2],
                    _ => reversed ? m[(j * 4) + 2] : r3 - m[(j * 4) + 2],
                };
                _planes[(p * 4) + j] = v;
            }
        }
    }

    public override bool Intersects(in TileBounds b)
    {
        for (int p = 0; p < 6; p++)
        {
            float a = _planes[p * 4];
            float bb = _planes[(p * 4) + 1];
            float c = _planes[(p * 4) + 2];
            float d = _planes[(p * 4) + 3];
            float x = a >= 0f ? b.MaxX : b.MinX;
            float y = bb >= 0f ? b.MaxY : b.MinY;
            float z = c >= 0f ? b.MaxZ : b.MinZ;
            if ((a * x) + (bb * y) + (c * z) + d < 0f)
            {
                return false;
            }
        }

        return true;
    }

    public override float ProjectedPixels(in TileBounds b)
    {
        float dx = b.CenterX - _camera.EyeX;
        float dy = b.CenterY - _camera.EyeY;
        float dz = b.CenterZ - _camera.EyeZ;
        float dist = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        float radius = b.Radius;
        return dist <= radius ? float.MaxValue : 2f * radius / dist * _pixelsPerRadianScale;
    }
}
