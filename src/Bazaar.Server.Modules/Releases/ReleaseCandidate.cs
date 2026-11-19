namespace Bazaar.Server.Modules.Releases;

/// <summary>
/// Status of a release candidate. Candidate is proposed but not rolling. Rolling is in some ring. Halted is terminal
/// (a failed health gate or an owner halt). Complete is past Broad with a passing gate.
/// </summary>
public enum ReleaseCandidateStatus
{
    Candidate,
    Rolling,
    Halted,
    Complete,
}

/// <summary>
/// One build of one version on one channel. Immutable: each change returns a new record, made by
/// <see cref="ReleaseCandidateLifecycle"/>, which checks the transition rules.
/// </summary>
public sealed record ReleaseCandidate(
    string Version,
    ReleaseChannel Channel,
    ReleaseRing Ring,
    ReleaseCandidateStatus Status,
    DateTimeOffset StatusChangedAt,
    string? HaltReason = null);
