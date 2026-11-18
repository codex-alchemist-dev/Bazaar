using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// Re-applies containment after a start. The containment store is durable, but the work it records (revoke the leases, pause the
/// VMs) is not atomic with it: a host that stopped between the store write and the pause, or whose backend lost a VM's state, can
/// restart with a contained agent whose VMs still run. For every contained agent this revokes the tokens and pauses the VMs again.
/// Both calls are idempotent by contract, so an agent that is already contained is left as it is.
/// </summary>
/// <remarks>
/// It runs after the startup gate, so the stored stop state is known. A failure does not stop the host: the stop itself is enforced
/// by the kill switch engine and this is the second line. Each failed agent is logged and retried on a fixed interval until it
/// succeeds, and the contained set is read again on every pass, so an agent that was released meanwhile drops out. An unreadable
/// store is treated the same way.
/// </remarks>
public sealed partial class ContainmentEnforcer : BackgroundService
{
    /// <summary>How long to wait before trying the agents that failed again.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(30);

    private readonly IContainmentStateStore _store;
    private readonly IAgentTokenRevoker _revoker;
    private readonly IVmPauser _pauser;
    private readonly ILogger<ContainmentEnforcer> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryInterval;

    public ContainmentEnforcer(
        IContainmentStateStore store,
        IAgentTokenRevoker revoker,
        IVmPauser pauser,
        ILogger<ContainmentEnforcer> logger,
        TimeProvider? time = null,
        TimeSpan? retryInterval = null)
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

        var failed = new List<string>();
        foreach (var agent in contained)
        {
            if (!await EnforceAsync(agent.AgentId, ct).ConfigureAwait(false))
            {
                failed.Add(agent.AgentId);
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
                if (failed is { Count: 0 })
                {
                    return;
                }

                await Task.Delay(_retryInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    /// <summary>Revokes and pauses one agent. Tries both even when the first fails, so one broken adapter does not hide the other.</summary>
    private async Task<bool> EnforceAsync(string agentId, CancellationToken ct)
    {
        var ok = true;
        try
        {
            await _revoker.RevokeAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ok = false;
            LogRevokeFailed(ex, agentId);
        }

        try
        {
            await _pauser.PauseAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ok = false;
            LogPauseFailed(ex, agentId);
        }

        return ok;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The containment store could not be read; contained agents are not re-checked yet.")]
    private partial void LogStoreUnreadable(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Contained agent {AgentId}: its tokens could not be revoked again.")]
    private partial void LogRevokeFailed(Exception exception, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Contained agent {AgentId}: its VMs could not be paused again.")]
    private partial void LogPauseFailed(Exception exception, string agentId);
}
