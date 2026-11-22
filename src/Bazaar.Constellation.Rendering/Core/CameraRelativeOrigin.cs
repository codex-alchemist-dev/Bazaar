namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Double-precision world eye for the 3D view. Tile origins stay in double precision on the CPU and are re-based to
/// small float offsets from the eye, so the GPU never sees large coordinates and close-up nodes do not jitter.
/// </summary>
public readonly record struct CameraRelativeOrigin(double X, double Y, double Z)
{
    /// <summary>Offset of a world point from the eye, subtracted in double precision and then narrowed to float.</summary>
    public (float X, float Y, float Z) Rebase(double worldX, double worldY, double worldZ) =>
        ((float)(worldX - X), (float)(worldY - Y), (float)(worldZ - Z));

    /// <summary>Rebases a tile origin; the tile's own vertex offsets are added on the GPU side in float.</summary>
    public (float X, float Y, float Z) Rebase((double X, double Y, double Z) origin) => Rebase(origin.X, origin.Y, origin.Z);

    /// <summary>
    /// A camera with the eye at the origin looking at the world target, for use with camera-relative tile offsets.
    /// Its view-projection takes positions produced by <see cref="Rebase(double, double, double)"/>.
    /// </summary>
    public PerspectiveCamera3D MakeRelativeCamera(
        double targetX, double targetY, double targetZ,
        float verticalFovRadians = 1.0471976f, float near = 0.1f, float far = 1.0e9f, bool reversedZ = true)
    {
        (float tx, float ty, float tz) = Rebase(targetX, targetY, targetZ);
        return new PerspectiveCamera3D(0f, 0f, 0f, tx, ty, tz, verticalFovRadians, near, far, reversedZ);
    }

    /// <summary>Writes the column-major view-projection for a camera looking at the target with the eye at the origin.</summary>
    public void WriteRelativeViewProjection(
        Span<float> m, float aspect, double targetX, double targetY, double targetZ,
        float verticalFovRadians = 1.0471976f, float near = 0.1f, float far = 1.0e9f, bool reversedZ = true) =>
        MakeRelativeCamera(targetX, targetY, targetZ, verticalFovRadians, near, far, reversedZ).WriteViewProjection(m, aspect);
}
