namespace Bazaar.Server.Modules.Releases;

/// <summary>
/// Staged rollout rings for one build within one channel (doc 49 section 6). Canary is the first place a build runs
/// against real data. Rings are ordered: each one is a wider set than the one before.
/// </summary>
public enum ReleaseRing
{
    Canary,
    Early,
    Broad,
}

public static class ReleaseRings
{
    /// <summary>The ring a build moves to after this one, or null after Broad.</summary>
    public static ReleaseRing? Next(ReleaseRing ring) => ring switch
    {
        ReleaseRing.Canary => ReleaseRing.Early,
        ReleaseRing.Early => ReleaseRing.Broad,
        ReleaseRing.Broad => null,
        _ => throw new ArgumentOutOfRangeException(nameof(ring), ring, "Unknown release ring."),
    };
}
