using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.BrowserPool.Tests;

/// <summary>A scripted pool VM host that records everything the pool asks of it.</summary>
internal sealed class FakeHost : IPoolVmHost
{
    private readonly Lock _lock = new();
    private int _runCount;

    public List<string> Log { get; } = [];

    public List<PoolCommand> Ran { get; } = [];

    public List<IReadOnlyList<PoolFirewallRule>> Firewalls { get; } = [];

    public List<PoolLaunchSpec> Started { get; } = [];

    public List<PoolProcess> Stopped { get; } = [];

    /// <summary>Called for every <see cref="RunAsync"/> with the 1-based call number; may block or throw.</summary>
    public Func<PoolCommand, int, CancellationToken, Task>? OnRun { get; set; }

    public Func<PoolLaunchSpec, CancellationToken, Task>? OnStart { get; set; }

    public Predicate<PoolCommand>? FailRun { get; set; }

    /// <summary>When true, <see cref="ApplyFirewallAsync"/> throws.</summary>
    public bool FailFirewall { get; set; }

    public Task RunAsync(PoolCommand command, CancellationToken ct)
    {
        int n;
        lock (_lock)
        {
            n = ++_runCount;
            Ran.Add(command);
            Log.Add("run:" + command.Argv[0]);
        }

        if (FailRun?.Invoke(command) == true)
        {
            throw new InvalidOperationException("scripted failure");
        }

        return OnRun?.Invoke(command, n, ct) ?? Task.CompletedTask;
    }

    public Task ApplyFirewallAsync(IReadOnlyList<PoolFirewallRule> rules, CancellationToken ct)
    {
        lock (_lock)
        {
            Firewalls.Add(rules.ToList());
            Log.Add("fw");
        }

        if (FailFirewall)
        {
            throw new InvalidOperationException("scripted firewall failure");
        }

        return Task.CompletedTask;
    }

    public async Task<PoolProcess> StartAsync(PoolLaunchSpec spec, CancellationToken ct)
    {
        lock (_lock)
        {
            Started.Add(spec);
            Log.Add("start");
        }

        if (OnStart is not null)
        {
            await OnStart(spec, ct);
        }

        return new PoolProcess(spec.SessionId, "guest-" + spec.Slot);
    }

    public Task StopAsync(PoolProcess process, CancellationToken ct)
    {
        lock (_lock)
        {
            Stopped.Add(process);
            Log.Add("stop");
        }

        return Task.CompletedTask;
    }

    public int Count(string program)
    {
        lock (_lock)
        {
            return Ran.Count(c => c.Argv[0] == program);
        }
    }
}

internal sealed class FakeEgress : IPoolEgressPolicy
{
    public Func<string, Task<PoolEgressEndpoint?>> Resolver { get; set; } =
        _ => Task.FromResult<PoolEgressEndpoint?>(new PoolEgressEndpoint("10.0.0.1", 3128));

    public Task<PoolEgressEndpoint?> ResolveAsync(string agentId, CancellationToken ct) => Resolver(agentId);
}

internal sealed class FakeGate : IPoolGate
{
    public HashSet<string> Blocked { get; } = [];

    public bool IsBlocked(string agentId) => Blocked.Contains(agentId);
}

internal sealed class RecordingAudit : IPoolAuditSink
{
    public List<PoolSession> OpenedRows { get; } = [];

    public List<(PoolSession Session, PoolCloseReason Reason)> ClosedRows { get; } = [];

    public List<(string Agent, PoolFailure Failure, string Detail)> RefusedRows { get; } = [];

    public List<(int Slot, string Session, string Agent, string Detail)> QuarantineRows { get; } = [];

    public void Opened(PoolSession session)
    {
        lock (OpenedRows)
        {
            OpenedRows.Add(session);
        }
    }

    public void Closed(PoolSession session, PoolCloseReason reason)
    {
        lock (ClosedRows)
        {
            ClosedRows.Add((session, reason));
        }
    }

    public void Refused(string agentId, PoolFailure failure, string detail)
    {
        lock (RefusedRows)
        {
            RefusedRows.Add((agentId, failure, detail));
        }
    }

    public void SlotQuarantined(int slot, string sessionId, string agentId, string detail)
    {
        lock (QuarantineRows)
        {
            QuarantineRows.Add((slot, sessionId, agentId, detail));
        }
    }
}

/// <summary>A pool wired to fakes.</summary>
internal sealed class Rig : IDisposable
{
    public Rig(Action<BrowserPoolOptions>? configure = null)
    {
        // The legacy tests were written for the shared namespace; per-slot behaviour is covered by the Netns tests. DiskMaxMb = 0 keeps their prepare and cleanup lists as they were before the disk cap; the cap has its own tests.
        Options = new BrowserPoolOptions { NetworkIsolation = NetworkIsolation.SharedWithUidRule, DiskMaxMb = 0 };
        configure?.Invoke(Options);
        Pool = new BrowserPoolService(Microsoft.Extensions.Options.Options.Create(Options), Host, Egress, Gate, Audit, Time);
    }

    public BrowserPoolOptions Options { get; }

    public FakeHost Host { get; } = new();

    public FakeEgress Egress { get; } = new();

    public FakeGate Gate { get; } = new();

    public RecordingAudit Audit { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));

    public BrowserPoolService Pool { get; }

    public Task<PoolSession> OpenAsync(string agent, PoolDisplayMode display = PoolDisplayMode.Headless, string? url = null) =>
        Pool.OpenAsync(new PoolSessionRequest(agent, display, url), CancellationToken.None);

    public void Dispose() => Pool.Dispose();
}
