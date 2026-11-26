using Bazaar.Server.Agents.Turns.Start;
using Bazaar.Server.CodingWorkspace;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Opens the task worktree when a task session starts and closes it when the task leaves In progress (doc 17, "Default repo and
/// per-task worktree flow"). The key it gets is recorded for the session so the tool bridge can find the worktree. Starting the
/// same session twice is a no-op.
/// </summary>
/// <param name="workspace">The per-task workspace.</param>
/// <param name="keys">Where the session's worktree key is recorded.</param>
public sealed class CodingTaskSessionHook(ICodingTaskWorkspace workspace, CodingSessionWorktreeKeys keys) : ITaskSessionHook, IDisposable
{
    private readonly ICodingTaskWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    private readonly CodingSessionWorktreeKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public async Task StartAsync(TaskSessionStart start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        var slug = !string.IsNullOrWhiteSpace(start.TaskSlug) ? start.TaskSlug : start.TaskId?.ToString("N");
        if (slug is null)
        {
            return; // A wake with no task has nothing to write on.
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys.KeyFor(start.SessionId) is not null)
            {
                return;
            }

            var worktree = await _workspace.OpenAsync(
                start.AgentId.ToString("D"), slug, start.RepoRef, start.GcId?.ToString("D"), cancellationToken).ConfigureAwait(false);
            _keys.Set(start.SessionId, worktree.WorktreeKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EndAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var key = _keys.KeyFor(sessionId);
        if (key is null)
        {
            return;
        }

        await _workspace.CloseAsync(key, cancellationToken).ConfigureAwait(false);
        _keys.Remove(sessionId);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
