using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.Tests.Security.KillSwitch;

/// <summary>Records every containment call so tests can assert the host hook ran.</summary>
internal sealed class RecordingContainment : IAgentContainment
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];

    /// <summary>Calls in order, as "contain:agent" or "release:agent".</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>When set, <see cref="ContainAsync"/> throws, to test a failing host hook.</summary>
    public bool FailContain { get; set; }

    /// <summary>When set, <see cref="ReleaseAsync"/> throws, to test a failing host hook.</summary>
    public bool FailRelease { get; set; }

    public Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add("contain:" + agentId);
        }

        return FailContain ? Task.FromException(new InvalidOperationException("containment failed")) : Task.CompletedTask;
    }

    public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
        Task.FromResult<ReleaseChallenge?>(new ReleaseChallenge(new byte[16], DateTimeOffset.MaxValue));

    public Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add("release:" + agentId);
        }

        return FailRelease
            ? Task.FromException<ContainmentReleaseResult>(new InvalidOperationException("release failed"))
            : Task.FromResult(new ContainmentReleaseResult(ContainmentReleaseOutcome.Released));
    }
}

/// <summary>An audit sink that always fails, to test that a stop or resume without a record does not take effect.</summary>
internal sealed class FailingKillSwitchAuditSink : IKillSwitchAuditSink
{
    public Task RecordAsync(KillSwitchAuditEntry entry, CancellationToken ct = default) =>
        Task.FromException(new InvalidOperationException("audit sink unavailable"));
}

/// <summary>Release requests for tests that use a fake containment and never verify the signature.</summary>
internal static class ReleaseRequests
{
    /// <summary>A request that carries no real signature.</summary>
    public static ContainmentReleaseRequest Unsigned(string agentId, string deviceId) =>
        new(agentId, deviceId, new byte[16], DateTimeOffset.UnixEpoch, new byte[64]);
}
