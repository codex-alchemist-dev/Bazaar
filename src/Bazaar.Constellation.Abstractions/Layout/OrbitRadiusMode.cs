namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// How a branch's orbit radius follows its last-activity age (doc 19 <c>constellation.orbit_radius</c>). Plain-value mirror of
/// the server setting enum; the numeric values match it exactly, so a mapping by value is lossless.
/// </summary>
public enum OrbitRadiusMode
{
    /// <summary>Radius scales from the youngest to the oldest activity between the min and max orbit radii.</summary>
    Age = 0,

    /// <summary>Every body sits at the minimum orbit radius.</summary>
    Equal = 1,
}
