namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// Decides which agents run a turn now (doc 06, "Base choice"). The wake loop calls <see cref="RunOnceAsync"/> once per poll.
/// The first implementation is <see cref="InboxAgentScheduler"/> (a plain loop over the Postgres inbox); another scheduler
/// (for example an Orleans-based one) replaces it by registering another implementation of this interface.
/// </summary>
public interface IAgentScheduler
{
    /// <summary>Runs one pass: starts a turn for each agent that has due work and is allowed to wake.</summary>
    Task<AgentSchedulerPass> RunOnceAsync(CancellationToken ct);
}

/// <summary>
/// What one pass did. <see cref="AgentsDue"/> counts the agents the pass read as having due work. Every other count
/// partitions that set: a turn started, or an agent skipped for one reason (the loop guard, a lease held elsewhere, nothing
/// due by the time the turn was started, or an error). Errors never stop the pass.
/// </summary>
public sealed record AgentSchedulerPass(
    int AgentsDue,
    int TurnsStarted,
    int SkippedByGuard,
    int SkippedLeaseNotAcquired,
    int SkippedNothingDue,
    int Errors)
{
    /// <summary>A pass that found no due agents.</summary>
    public static AgentSchedulerPass Empty { get; } = new(0, 0, 0, 0, 0, 0);
}
