using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>
/// A fake <see cref="IModelGateway"/> for turn tests. Each call plays the next script in order and is counted, so a test
/// states exactly what the model does and can assert how many calls were made. A call with no script left throws, so an
/// unexpected call is visible. A script can also throw, or hold until the call is cancelled.
/// </summary>
internal sealed class ScriptedModelGateway : IModelGateway
{
    private readonly Lock _gate = new();
    private readonly Queue<Script> _scripts = new();
    private readonly List<ModelRequest> _requests = [];
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every request received, in order, including a call that found no script.</summary>
    public IReadOnlyList<ModelRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>How many times the gateway was called.</summary>
    public int Calls => Requests.Count;

    /// <summary>Completes when a holding script has started waiting, so a test can cancel the run at a known point.</summary>
    public Task Held => _held.Task;

    /// <summary>Queues a script that streams these events.</summary>
    public ScriptedModelGateway Reply(params ModelEvent[] events) => Enqueue(new Script(events, null, Hold: false));

    /// <summary>Queues a script that throws when the call starts.</summary>
    public ScriptedModelGateway Throw(Exception exception) => Enqueue(new Script([], exception, Hold: false));

    /// <summary>Queues a script that never answers and waits for cancellation.</summary>
    public ScriptedModelGateway Hold() => Enqueue(new Script([], null, Hold: true));

    public static ModelEvent TextEvent(string text) => new ModelEvent.TextDelta(text);

    public static ModelEvent UsageEvent(long input, long output) =>
        new ModelEvent.UsageReported(new GatewayTokenUsage(input, 0, 0, 0, output, 0));

    public static ModelEvent DoneEvent(StopReason reason) => new ModelEvent.Done(reason, "fake-model");

    /// <summary>A complete answer: text, usage and a final stop.</summary>
    public static ModelEvent[] FinalAnswer(string text) => [TextEvent(text), UsageEvent(10, 5), DoneEvent(StopReason.EndTurn)];

    public async IAsyncEnumerable<ModelEvent> CallAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Script script;
        lock (_gate)
        {
            _requests.Add(request);
            if (_scripts.Count == 0)
                throw new InvalidOperationException("The scripted gateway has no reply left for this call.");
            script = _scripts.Dequeue();
        }

        if (script.Exception is not null)
            throw script.Exception;

        if (script.Hold)
        {
            _held.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        foreach (var modelEvent in script.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return modelEvent;
        }
    }

    private ScriptedModelGateway Enqueue(Script script)
    {
        lock (_gate)
            _scripts.Enqueue(script);
        return this;
    }

    private sealed record Script(ModelEvent[] Events, Exception? Exception, bool Hold);
}
