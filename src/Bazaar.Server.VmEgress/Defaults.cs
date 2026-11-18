using System.Collections.Concurrent;

namespace Bazaar.Server.VmEgress;

/// <summary>
/// In-memory <see cref="IEgressAllowlistStore"/>. Fine for tests and a first run; the approved allowlist and the blocked flag are
/// lost on restart, which fails open for the flag, so the host should replace it with a durable store before production use.
/// </summary>
public sealed class InMemoryEgressAllowlistStore : IEgressAllowlistStore
{
    private readonly ConcurrentDictionary<string, EgressRules> _rules = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Dictionary<string, (bool Blocked, long Epoch)> _blocks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<EgressRules?> GetAsync(string agentId, CancellationToken ct = default) =>
        Task.FromResult(_rules.TryGetValue(agentId, out var rules) ? rules : null);

    /// <inheritdoc />
    public Task SaveAsync(string agentId, EgressRules rules, CancellationToken ct = default)
    {
        _rules[agentId] = rules;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsBlockedAsync(string agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_blocks.TryGetValue(agentId, out var b) && b.Blocked);
        }
    }

    /// <inheritdoc />
    public Task<long> GetBlockEpochAsync(string agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_blocks.TryGetValue(agentId, out var b) ? b.Epoch : 0L);
        }
    }

    /// <inheritdoc />
    public Task<long> BlockAsync(string agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var epoch = (_blocks.TryGetValue(agentId, out var b) ? b.Epoch : 0L) + 1;
            _blocks[agentId] = (true, epoch);
            return Task.FromResult(epoch);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryReleaseBlockAsync(string agentId, long expectedEpoch, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_blocks.TryGetValue(agentId, out var b) && b.Blocked && b.Epoch == expectedEpoch)
            {
                _blocks[agentId] = (false, b.Epoch);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// <see cref="IEgressEnforcer"/> for a host with no switch or bridge executor. Throws, so egress never reports default-deny as in
/// place when it is not. Replaced by the host's Hyper-V or nftables executor.
/// </summary>
public sealed class UnavailableEgressEnforcer : IEgressEnforcer
{
    /// <inheritdoc />
    public Task ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default) =>
        throw new InvalidOperationException("No egress enforcer is configured, so default-deny at the switch cannot be put in place.");

    /// <inheritdoc />
    public Task BlockAllAsync(string agentId, CancellationToken ct = default) =>
        throw new InvalidOperationException("No egress enforcer is configured, so the switch rule cannot be dropped. The proxy is closed regardless.");
}

/// <summary><see cref="IEgressApprovalGate"/> with no approval path wired. Refuses every growth, so the allowlist cannot widen by accident.</summary>
public sealed class DenyAllEgressApprovalGate : IEgressApprovalGate
{
    /// <inheritdoc />
    public Task<EgressApprovalState> RequestGrowthAsync(string agentId, IReadOnlyList<string> additions, int tier, CancellationToken ct = default) =>
        Task.FromResult(EgressApprovalState.Denied);
}

/// <summary>Recording <see cref="IEgressEnforcer"/> for tests and for the cloud build. Holds the last rules per agent; touches no host.</summary>
public sealed class RecordingEgressEnforcer : IEgressEnforcer
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly Dictionary<string, EgressRules> _applied = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);

    /// <summary>Set to make the next calls throw, to test failure paths.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>The calls in order, as <c>apply:agent</c> and <c>block:agent</c>.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The rules last applied for the agent, or null.</summary>
    public EgressRules? AppliedFor(string agentId)
    {
        lock (_gate)
        {
            return _applied.TryGetValue(agentId, out var rules) ? rules : null;
        }
    }

    /// <summary>Whether the agent's last call was a block.</summary>
    public bool IsBlocked(string agentId)
    {
        lock (_gate)
        {
            return _blocked.Contains(agentId);
        }
    }

    /// <inheritdoc />
    public Task ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default)
    {
        if (FailWith is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _calls.Add("apply:" + agentId);
            _applied[agentId] = rules;
            _blocked.Remove(agentId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BlockAllAsync(string agentId, CancellationToken ct = default)
    {
        if (FailWith is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _calls.Add("block:" + agentId);
            _blocked.Add(agentId);
        }

        return Task.CompletedTask;
    }
}
