namespace Bazaar.Server.Agents.Turns.Start;

/// <summary>
/// Hooks the lifecycle of a task session (doc 06, "Wake model"; doc 17, coding harness). A component registers one to prepare
/// and release per-task state (for example a worktree) without the agent runtime referencing that component.
/// </summary>
public interface ITaskSessionHook
{
    /// <summary>Runs before the first turn of a task. It must be safe to call again for the same session.</summary>
    Task StartAsync(TaskSessionStart start, CancellationToken cancellationToken);

    /// <summary>Runs when the task's card leaves In progress, not when a turn ends.</summary>
    Task EndAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>What a task wake knows when the task session starts. <see cref="TaskId"/> is the card the wake was about.</summary>
public sealed record TaskSessionStart(Guid SessionId, Guid AgentId, string? RepoRef, string? TaskSlug, Guid? GcId, Guid? TaskId);
