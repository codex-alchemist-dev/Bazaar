using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AgentSafety.Drill.Tests;

/// <summary>Containment fake that is also the drill probe. It keeps the effects per agent and can inject faults.</summary>
internal sealed class FakeContainment(TimeProvider time) : IAgentContainment, IDrillProbe
{
    private readonly Dictionary<string, DrillObservation> _state = new(StringComparer.Ordinal);

    /// <summary>Makes every release a denial, as a containment that does not accept the signature would.</summary>
    public bool DenyRelease { get; set; }

    /// <summary>Makes the challenge step return no challenge.</summary>
    public bool IssueNoChallenge { get; set; }

    /// <summary>Leaves egress open on containment, so the containment step fails.</summary>
    public bool SkipEgressBlock { get; set; }

    /// <summary>Makes every observation of a contained agent throw. The message carries text that must never reach a result.</summary>
    public bool ThrowWhileContained { get; set; }

    /// <summary>Called with each observation after it is taken, so a test can cancel at a chosen point.</summary>
    public Action<DrillObservation>? OnObserved { get; set; }

    /// <summary>Agents this fake holds in a contained state.</summary>
    public IReadOnlyCollection<string> ContainedAgents => _state.Where(kv => kv.Value.IsContained || kv.Value.VmsPaused).Select(kv => kv.Key).ToArray();

    public Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
    {
        _state[agentId] = new DrillObservation(TokensRevoked: true, VmsPaused: true, EgressBlocked: !SkipEgressBlock);
        return Task.CompletedTask;
    }

    public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ReleaseChallenge? challenge = IssueNoChallenge ? null : new ReleaseChallenge(RandomNumberGenerator.GetBytes(16), time.GetUtcNow().AddMinutes(2));
        return Task.FromResult(challenge);
    }

    public Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (DenyRelease)
        {
            return Task.FromResult(new ContainmentReleaseResult(ContainmentReleaseOutcome.Denied));
        }

        _state[agentId] = new DrillObservation(TokensRevoked: _state.TryGetValue(agentId, out var s) && s.TokensRevoked, VmsPaused: false, EgressBlocked: false);
        return Task.FromResult(new ContainmentReleaseResult(ContainmentReleaseOutcome.Released));
    }

    public Task<DrillObservation> ObserveAsync(string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var observed = _state.TryGetValue(agentId, out var s) ? s : new DrillObservation(false, false, false);
        if (ThrowWhileContained && !observed.IsOpen)
        {
            throw new InvalidOperationException("probe detail that must not reach a result");
        }

        OnObserved?.Invoke(observed);
        return Task.FromResult(observed);
    }
}

/// <summary>Drill target with one synthetic agent and one drill device.</summary>
internal sealed class FakeDrillTarget : IDrillTarget
{
    public const string AgentId = "drill-agent";
    public const string DeviceId = "drill-device";

    public string DrillAgentId => AgentId;

    public string DrillDeviceId => DeviceId;

    /// <summary>Makes the drill device unable to sign.</summary>
    public bool CanSign { get; set; } = true;

    public bool IsDrillAgent(string agentId) => agentId == AgentId;

    public Task<ContainmentReleaseRequest?> SignReleaseAsync(string agentId, string deviceId, ReleaseChallenge challenge, CancellationToken ct = default)
    {
        ContainmentReleaseRequest? request = CanSign
            ? new ContainmentReleaseRequest(agentId, deviceId, challenge.Nonce, challenge.ExpiresAt, new byte[64])
            : null;
        return Task.FromResult(request);
    }
}

/// <summary>Wires an engine, the fakes and the runner over one fake clock.</summary>
internal sealed class DrillHarness
{
    public DrillHarness(FakeTimeProvider? time = null)
    {
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        Containment = new FakeContainment(Time);
        Audit = new InMemoryKillSwitchAuditSink();
        Engine = new KillSwitchEngine(Audit, Containment, new InMemoryKillSwitchStateStore(), Time);
        Target = new FakeDrillTarget();
        Runner = new DrillRunner(Engine, Containment, Containment, Target, Time);
    }

    public FakeTimeProvider Time { get; }

    public FakeContainment Containment { get; }

    public InMemoryKillSwitchAuditSink Audit { get; }

    public KillSwitchEngine Engine { get; }

    public FakeDrillTarget Target { get; }

    public DrillRunner Runner { get; }
}

/// <summary>Logger that keeps the formatted messages, so a test can read what the scheduler logged.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Enqueue(formatter(state, exception));
}
