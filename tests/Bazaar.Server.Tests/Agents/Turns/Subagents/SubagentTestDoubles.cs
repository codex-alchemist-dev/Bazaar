using Bazaar.Server.Agents.Turns;

namespace Bazaar.Server.Tests.Agents.Turns.Subagents;

/// <summary>
/// A turn runner that records every request and answers with a scripted outcome. The default script finishes at once with a
/// final message; a test can pass its own script to hold a child open or fail it.
/// </summary>
internal sealed class ScriptedTurnRunner(Func<TurnRunRequest, CancellationToken, Task<TurnOutcome>>? script = null) : ITurnRunner
{
    private readonly Lock _gate = new();
    private readonly List<TurnRunRequest> _requests = [];
    private readonly Func<TurnRunRequest, CancellationToken, Task<TurnOutcome>> _script =
        script ?? ((_, _) => Task.FromResult(new TurnOutcome(Guid.NewGuid(), TurnEndReason.FinalMessage, "done", 1)));

    /// <summary>Every request the runner received, in order.</summary>
    public IReadOnlyList<TurnRunRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public Task<TurnOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        return _script(request, cancellationToken);
    }
}
