namespace Bazaar.Constellation.Rendering.Core;

/// <summary>Zoom tiers: far shows clusters, mid shows shapes and labels, near shows all detail.</summary>
public enum LodTier
{
    Far,
    Mid,
    Near,
}

public static class LevelOfDetail
{
    /// <summary>
    /// Maps a zoom level to a tier. These defaults are a starting point
    /// to tune in the spike and are passed in so callers can override them.
    /// </summary>
    public static LodTier TierFor(float zoom, float farBelow = 0.25f, float nearAtOrAbove = 1.5f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(farBelow);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(nearAtOrAbove, farBelow);
        if (zoom < farBelow)
        {
            return LodTier.Far;
        }

        return zoom >= nearAtOrAbove ? LodTier.Near : LodTier.Mid;
    }
}
