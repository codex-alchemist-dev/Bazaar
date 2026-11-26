namespace Bazaar.Server.Agents.Turns.Start;

/// <summary>
/// Calls <see cref="ITaskSessionHook.EndAsync"/> on every registered hook, in registration order. The Ryusei card transition
/// calls it when a task's card leaves In progress; <see cref="TurnStarter"/> never does.
/// </summary>
public sealed class TaskSessionEnder(IEnumerable<ITaskSessionHook> hooks)
{
    private readonly ITaskSessionHook[] _hooks = (hooks ?? throw new ArgumentNullException(nameof(hooks))).ToArray();

    /// <summary>Ends the task session for every hook. A hook exception propagates.</summary>
    public async Task EndAsync(Guid sessionId, CancellationToken ct)
    {
        foreach (var hook in _hooks)
            await hook.EndAsync(sessionId, ct).ConfigureAwait(false);
    }
}
