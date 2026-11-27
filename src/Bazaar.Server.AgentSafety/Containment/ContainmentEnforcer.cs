using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// Re-applies containment after a start and keeps sweeping. The containment store is durable, but the work it records (revoke the leases, pause the
/// VMs) is not atomic with it: a host that stopped between the store write and the pause, or whose backend lost a VM's state, can
/// restart with a contained agent whose VMs still run. For every contained agent this revokes the tokens and pauses the VMs again.
/// Both calls are idempotent by contract, so an agent that is already contained is left as it is.
/// </summary>
/// <remarks>
/// It runs after the startup gate, so the stored stop state is known. A failure does not stop the host: the stop itself is enforced
/// by the kill switch engine and this is the second line. Each failed agent is logged and retried on a fixed interval until it
/// succeeds, and the contained set is read again on every pass, so an agent that was released meanwhile drops out. An unreadable
/// store is treated the same way.
/// <para>
/// After the first clean pass it keeps sweeping on <see cref="DefaultSweepInterval"/>, so a VM started later for a contained agent, or
/// a contain whose runtime step failed, is paused again. A sweep must not undo a release, which resumes the VMs before it removes the
/// agent from the store: it takes the agent's <see cref="ContainmentReleaseGuard"/> without waiting, skips an agent whose release is
/// in flight, and re-reads the store under the guard so an agent released since the pass began is left alone.
/// </para>
/// </remarks>
public sealed partial class ContainmentEnforcer : BackgroundService
{
    /// <summary>How long to wait before trying the agents that failed again.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait between sweeps once every contained agent is contained.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(60);

    /// <summary>The longest one revoke or one pause may take for one agent. A step that runs longer counts as failed and is retried.</summary>
    public static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromMinutes(2);

    private readonly IContainmentStateStore _store;
    private readonly IAgentTokenRevoker _revoker;
    private readonly IVmPauser _pauser;
    private readonly ILogger<ContainmentEnforcer> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryInterval;
    private readonly TimeSpan _sweepInterval;
    private readonly ContainmentReleaseGuard _guard;
    private readonly TimeSpan _stepTimeout;

    public ContainmentEnforcer(
        IContainmentStateStore store,
        IAgentTokenRevoker revoker,
        IVmPauser pauser,
        ILogger<ContainmentEnforcer> logger,
        TimeProvider? time = null,
        TimeSpan? retryInterval = null,
        TimeSpan? sweepInterval = null,
        ContainmentReleaseGuard? releaseGuard = null,
        TimeSpan? stepTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _revoker = revoker ?? throw new ArgumentNullException(nameof(revoker));
        _pauser = pauser ?? throw new ArgumentNullException(nameof(pauser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _retryInterval = retryInterval ?? DefaultRetryInterval;
        if (_retryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryInterval), "The retry interval must be positive.");
        }

        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
        if (_sweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sweepInterval), "The sweep interval must be positive.");
        }

        _stepTimeout = stepTimeout ?? DefaultStepTimeout;
        if (_stepTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stepTimeout), "The step timeout must be positive.");
        }

        _guard = releaseGuard ?? new ContainmentReleaseGuard();
    }

    /// <summary>
    /// Enforces containment for every contained agent once. Returns the ids of the agents it could not fully contain, or
    /// <c>null</c> when the contained set itself could not be read. An empty list means every contained agent is contained.
    /// </summary>
    public async Task<IReadOnlyList<string>?> EnforceOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<ContainedAgent> contained;
        try
        {
            contained = await _store.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStoreUnreadable(ex);
            return null;
        }

        // Two phases. Every token cut is attempted before any VM pause starts, so a slow or hung VM save for one agent never delays the
        // revoke of another. The guard leases are held across both phases, so a release cannot slip in between the re-read and the pause.
        var leases = new List<IDisposable>();
        var targets = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var agent in contained)
            {
                // A release in flight (or an earlier pass still working on the agent) holds the guard: skip, the next pass looks again.
                var lease = _guard.TryAcquire(agent.AgentId);
                if (lease is null)
                {
                    continue;
                }

                leases.Add(lease);

                // Re-read under the guard: a release that finished after the set was loaded has removed the agent, and must stay released.
                if (await StillContainedAsync(agent.AgentId, ct).ConfigureAwait(false))
                {
                    targets.Add(agent.AgentId);
                }
            }

            var bad = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in targets)
            {
                if (!await RevokeStepAsync(id, ct).ConfigureAwait(false))
                {
                    bad.Add(id);
                }
            }

            foreach (var id in targets)
            {
                if (!await PauseStepAsync(id, ct).ConfigureAwait(false))
                {
                    bad.Add(id);
                }
            }

            failed.AddRange(targets.Where(bad.Contains));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        return failed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var failed = await EnforceOnceAsync(stoppingToken).ConfigureAwait(false);

                // Failed agents are retried quickly; once all are contained the sweep continues at the slower interval.
                var delay = failed is { Count: 0 } ? _sweepInterval : _retryInterval;
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    private async Task<bool> StillContainedAsync(string agentId, CancellationToken ct)
    {
        try
        {
            var now = await _store.LoadAsync(ct).ConfigureAwait(false);
            return now.Any(a => string.Equals(a.AgentId, agentId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable: stay on the safe side and enforce.
            LogStoreUnreadable(ex);
            return true;
        }
    }

    private async Task<bool> RevokeStepAsync(string agentId, CancellationToken ct)
    {
        try
        {
            await RunBoundedAsync(token => _revoker.RevokeAsync(agentId, token), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogRevokeFailed(ex, agentId);
            return false;
        }
    }

    private async Task<bool> PauseStepAsync(string agentId, CancellationToken ct)
    {
        try
        {
            await RunBoundedAsync(token => _pauser.PauseAsync(agentId, token), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogPauseFailed(ex, agentId);
            return false;
        }
    }

    /// <summary>
    /// Runs one adapter call under the step timeout. The call gets a token that fires at the timeout, and the wait stops at the timeout
    /// even if the adapter ignores the token, so a hung adapter cannot hold up the other agents. A timeout, and an
    /// <see cref="OperationCanceledException"/> the adapter throws on its own, are failures like any other, never an abort of the pass.
    /// </summary>
    private async Task RunBoundedAsync(Func<CancellationToken, Task> call, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_stepTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await call(linked.Token).WaitAsync(_stepTimeout, _time, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The containment store could not be read; contained agents are not re-checked yet.")]
    private partial void LogStoreUnreadable(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Contained agent {AgentId}: its tokens could not be revoked again.")]
    private partial void LogRevokeFailed(Exception exception, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Contained agent {AgentId}: its VMs could not be paused again.")]
    private partial void LogPauseFailed(Exception exception, string agentId);
}
