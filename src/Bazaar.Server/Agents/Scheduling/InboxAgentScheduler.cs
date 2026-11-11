using System.Collections.Concurrent;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Start;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// The first <see cref="IAgentScheduler"/>: a plain pass over the Postgres inbox (doc 06, "Base choice"). Each pass reads the
/// agents with due work, checks the loop guard for each one, and starts one turn per agent through <see cref="TurnStarter"/>.
/// Agents run concurrently up to <see cref="AgentSchedulerOptions.MaxConcurrentTurns"/>. The pass claims nothing itself: the
/// runner claims the items it is given.
/// </summary>
/// <remarks>
/// A session per turn is the interim rule. Every turn gets a fresh version 7 session id, so taint from one turn never carries
/// into the next turn of the same agent. This stays until a session model exists (doc 06, "Session key"; doc 15).
/// An agent the guard trips is skipped, and its items stay pending. The guard counts every wake it is asked about, so a
/// tripped agent is not asked about again until <see cref="AgentSchedulerOptions.GuardTripBackoff"/> has passed; asking on
/// every pass would keep its wake count over the limit for good. The backoff is kept in memory and is lost on restart.
/// A turn whose lease is held elsewhere
/// (<see cref="TurnEndReason.LeaseLost"/>) is counted as a lease skip, not an error. One agent's failure is logged and counted;
/// it does not stop the other agents. Cancellation of the pass token stops the pass.
/// A pass returns only after every turn it started has ended, so an agent that becomes due during a pass waits for the next one.
/// </remarks>
public sealed partial class InboxAgentScheduler(
    IDueAgentReader dueAgents,
    TurnStarter turns,
    ILoopGuard guard,
    AgentSchedulerOptions options,
    TimeProvider time,
    ILogger<InboxAgentScheduler> logger) : IAgentScheduler
{
    private readonly IDueAgentReader _dueAgents = dueAgents;
    private readonly TurnStarter _turns = turns;
    private readonly ILoopGuard _guard = guard;
    private readonly AgentSchedulerOptions _options = options;
    private readonly TimeProvider _time = time;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _guardHoldUntil = new();
    private readonly ILogger<InboxAgentScheduler> _logger = logger;

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<AgentSchedulerPass> RunOnceAsync(CancellationToken ct)
    {
        _options.Validate();

        var due = await _dueAgents.PeekDueAgentsAsync(_options.MaxAgentsPerPass, ct).ConfigureAwait(false);
        if (due.Count == 0)
            return AgentSchedulerPass.Empty;

        var tally = new Tally();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrentTurns, CancellationToken = ct };
        await Parallel.ForEachAsync(due, parallel, (agent, token) => WakeAsync(agent.AgentId, tally, token)).ConfigureAwait(false);

        return tally.ToPass(due.Count);
    }

    /// <summary>Checks the guard for one agent and starts its turn. Never throws except for cancellation of the pass.</summary>
    private async ValueTask WakeAsync(Guid agentId, Tally tally, CancellationToken ct)
    {
        try
        {
            var now = _time.GetUtcNow();
            if (_guardHoldUntil.TryGetValue(agentId, out var holdUntil))
            {
                if (now < holdUntil)
                {
                    Interlocked.Increment(ref tally.SkippedByGuard);
                    return;
                }

                _guardHoldUntil.TryRemove(agentId, out _);
            }

            var verdict = _guard.Check(new AgentWake(agentId.ToString("D"), WakeKind.Standard));
            if (verdict.Tripped)
            {
                if (_options.GuardTripBackoff > TimeSpan.Zero)
                    _guardHoldUntil[agentId] = now + _options.GuardTripBackoff;
                LogGuardSkipped(_logger, agentId, verdict.Kind);
                Interlocked.Increment(ref tally.SkippedByGuard);
                return;
            }

            var request = new TurnStartRequest(
                agentId,
                Guid.CreateVersion7(),
                TurnRole.Actor,
                $"{_options.HolderPrefix}:{agentId:D}",
                _options.Route,
                _options.ToolAllowlist,
                _options.MaxItemsPerTurn);
            var result = await _turns.StartAsync(request, ct).ConfigureAwait(false);

            if (!result.Started)
            {
                Interlocked.Increment(ref tally.SkippedNothingDue);
            }
            else if (result.Outcome is { Reason: TurnEndReason.LeaseLost })
            {
                LogLeaseNotAcquired(_logger, agentId);
                Interlocked.Increment(ref tally.SkippedLeaseNotAcquired);
            }
            else
            {
                Interlocked.Increment(ref tally.TurnsStarted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWakeFailed(_logger, ex, agentId);
            Interlocked.Increment(ref tally.Errors);
        }
    }

    /// <summary>Counts for one pass. Each agent's task updates these with interlocked increments.</summary>
    private sealed class Tally
    {
        public int TurnsStarted;
        public int SkippedByGuard;
        public int SkippedLeaseNotAcquired;
        public int SkippedNothingDue;
        public int Errors;

        public AgentSchedulerPass ToPass(int agentsDue) => new(
            agentsDue,
            Volatile.Read(ref TurnsStarted),
            Volatile.Read(ref SkippedByGuard),
            Volatile.Read(ref SkippedLeaseNotAcquired),
            Volatile.Read(ref SkippedNothingDue),
            Volatile.Read(ref Errors));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} was not woken: loop guard {GuardKind} tripped. Its inbox items stay pending.")]
    private static partial void LogGuardSkipped(ILogger logger, Guid agentId, GuardKind? guardKind);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {AgentId} was not woken: its lease is held by another turn.")]
    private static partial void LogLeaseNotAcquired(ILogger logger, Guid agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Starting a turn for agent {AgentId} failed. The pass continues with the other agents.")]
    private static partial void LogWakeFailed(ILogger logger, Exception exception, Guid agentId);
}
