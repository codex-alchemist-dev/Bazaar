namespace Bazaar.Server.Modules.Releases;

/// <summary>
/// Status transitions for a release candidate. Pure: the only clock is the injected <see cref="TimeProvider"/>.
/// Rules: Candidate may start (to Rolling) or be halted. Rolling may pass a gate (advance a ring, or complete after
/// Broad), hold, or be halted. Halted and Complete are terminal. A version halted on any channel cannot be proposed again.
/// </summary>
public sealed class ReleaseCandidateLifecycle
{
    private readonly TimeProvider _clock;

    public ReleaseCandidateLifecycle(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Proposes a candidate on the Canary ring. Refused when <paramref name="existing"/> already halted this version.</summary>
    public ReleaseCandidate Propose(string version, ReleaseChannel channel, IEnumerable<ReleaseCandidate> existing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(existing);

        if (existing.Any(c => c.Status == ReleaseCandidateStatus.Halted && string.Equals(c.Version, version, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Version {version} was halted and cannot be proposed again.");
        }

        return new ReleaseCandidate(version, channel, ReleaseRing.Canary, ReleaseCandidateStatus.Candidate, _clock.GetUtcNow());
    }

    /// <summary>Starts rolling a proposed candidate in its current ring.</summary>
    public ReleaseCandidate Start(ReleaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        RequireStatus(candidate, ReleaseCandidateStatus.Candidate, "start");

        return candidate with { Status = ReleaseCandidateStatus.Rolling, StatusChangedAt = _clock.GetUtcNow() };
    }

    /// <summary>
    /// Applies a gate decision to a rolling candidate. Continue advances to the next ring, or completes after Broad.
    /// Hold changes nothing. Halt halts the candidate (see <see cref="Halt"/>).
    /// </summary>
    public ReleaseCandidate ApplyGate(ReleaseCandidate candidate, GateDecision decision)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        switch (decision)
        {
            case GateDecision.Continue:
                {
                    RequireStatus(candidate, ReleaseCandidateStatus.Rolling, "advance");
                    var now = _clock.GetUtcNow();
                    return ReleaseRings.Next(candidate.Ring) is { } next
                        ? candidate with { Ring = next, StatusChangedAt = now }
                        : candidate with { Status = ReleaseCandidateStatus.Complete, StatusChangedAt = now };
                }

            case GateDecision.Hold:
                RequireStatus(candidate, ReleaseCandidateStatus.Rolling, "hold");
                return candidate;

            case GateDecision.Halt:
                return Halt(candidate, $"Health gate halted the {candidate.Ring} ring.");

            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown gate decision.");
        }
    }

    /// <summary>Halts a candidate that is proposed or rolling. The reason is recorded. Halt is terminal.</summary>
    public ReleaseCandidate Halt(ReleaseCandidate candidate, string reason)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (candidate.Status is not (ReleaseCandidateStatus.Candidate or ReleaseCandidateStatus.Rolling))
        {
            throw new InvalidOperationException(Describe(candidate, "halt"));
        }

        return candidate with
        {
            Status = ReleaseCandidateStatus.Halted,
            StatusChangedAt = _clock.GetUtcNow(),
            HaltReason = reason,
        };
    }

    private static void RequireStatus(ReleaseCandidate candidate, ReleaseCandidateStatus expected, string action)
    {
        if (candidate.Status != expected)
        {
            throw new InvalidOperationException(Describe(candidate, action));
        }
    }

    private static string Describe(ReleaseCandidate candidate, string action) =>
        candidate.Status == ReleaseCandidateStatus.Halted
            ? $"Version {candidate.Version} is halted; halt is terminal and cannot {action}."
            : $"Cannot {action} a candidate in status {candidate.Status}.";
}
