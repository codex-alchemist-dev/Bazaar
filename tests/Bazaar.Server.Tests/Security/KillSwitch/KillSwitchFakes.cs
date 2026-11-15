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

    public Task ContainAsync(string agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add("contain:" + agentId);
        }

        return FailContain ? Task.FromException(new InvalidOperationException("containment failed")) : Task.CompletedTask;
    }

    public Task ReleaseAsync(string agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add("release:" + agentId);
        }

        return FailRelease ? Task.FromException(new InvalidOperationException("release failed")) : Task.CompletedTask;
    }
}

/// <summary>An audit sink that always fails, to test that a stop or resume without a record does not take effect.</summary>
internal sealed class FailingKillSwitchAuditSink : IKillSwitchAuditSink
{
    public Task RecordAsync(KillSwitchAuditEntry entry, CancellationToken ct = default) =>
        Task.FromException(new InvalidOperationException("audit sink unavailable"));
}
