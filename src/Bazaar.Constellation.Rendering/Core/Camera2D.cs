namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Pan and zoom over the 2D view. Screen space has its origin at the top left, y down, in pixels.
/// </summary>
public sealed class Camera2D
{
    public const float MinZoom = 0.05f;
    public const float MaxZoom = 64f;

    private float _zoom = 1f;

    public float CenterX { get; private set; }

    public float CenterY { get; private set; }

    public float Zoom
    {
        get => _zoom;
        private set => _zoom = Math.Clamp(value, MinZoom, MaxZoom);
    }

    public (float X, float Y) ScreenToWorld(float screenX, float screenY, int widthPixels, int heightPixels) =>
        (((screenX - (widthPixels * 0.5f)) / Zoom) + CenterX,
         ((screenY - (heightPixels * 0.5f)) / Zoom) + CenterY);

    public (float X, float Y) WorldToScreen(float worldX, float worldY, int widthPixels, int heightPixels) =>
        (((worldX - CenterX) * Zoom) + (widthPixels * 0.5f),
         ((worldY - CenterY) * Zoom) + (heightPixels * 0.5f));

    /// <summary>Moves the view by a pixel drag. Content follows the pointer.</summary>
    public void PanPixels(float deltaXPixels, float deltaYPixels)
    {
        CenterX -= deltaXPixels / Zoom;
        CenterY -= deltaYPixels / Zoom;
    }

    /// <summary>Zooms by <paramref name="factor"/> and keeps the world point under the pointer fixed.</summary>
    public void ZoomAt(float screenX, float screenY, int widthPixels, int heightPixels, float factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        (float beforeX, float beforeY) = ScreenToWorld(screenX, screenY, widthPixels, heightPixels);
        Zoom *= factor;
        (float afterX, float afterY) = ScreenToWorld(screenX, screenY, widthPixels, heightPixels);
        CenterX += beforeX - afterX;
        CenterY += beforeY - afterY;
    }

    /// <summary>Centres the view on a world point.</summary>
    public void CenterOn(float worldX, float worldY)
    {
        CenterX = worldX;
        CenterY = worldY;
    }

    /// <summary>
    /// Writes a column-major 4x4 orthographic matrix that maps world space to clip space.
    /// Clip y follows screen y (down), matching the Vulkan viewport convention.
    /// </summary>
    public void WriteProjection(Span<float> columnMajor4x4, int widthPixels, int heightPixels)
    {
        if (columnMajor4x4.Length < 16)
        {
            throw new ArgumentException("A 4x4 matrix needs 16 floats.", nameof(columnMajor4x4));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);
        columnMajor4x4.Slice(0, 16).Clear();
        columnMajor4x4[0] = 2f * Zoom / widthPixels;
        columnMajor4x4[5] = 2f * Zoom / heightPixels;
        columnMajor4x4[10] = 1f;
        columnMajor4x4[12] = -2f * Zoom * CenterX / widthPixels;
        columnMajor4x4[13] = -2f * Zoom * CenterY / heightPixels;
        columnMajor4x4[15] = 1f;
    }
}
