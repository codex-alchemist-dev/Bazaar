namespace Bazaar.Server.Modules.Releases;

/// <summary>
/// Release channels (doc 49 section 6). An install is on one channel at a time; switching is an audited decision.
/// Names follow the 2026-11-13 owner decision. <c>docs/VERSIONING.md</c> still lists LTSC, Stable, Beta, Alpha and
/// Latest-Commit, and the mapping is OPEN (doc 49 section 12).
/// </summary>
public enum ReleaseChannel
{
    Stable,
    Beta,
    Nightly,
    Lts,
}
