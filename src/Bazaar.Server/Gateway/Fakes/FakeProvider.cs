using System.Runtime.CompilerServices;
using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Fakes;

/// <summary>
/// A scripted model provider for tests. Each <see cref="StreamAsync"/> call plays the next enqueued script,
/// so a test states exactly what the upstream does and in what order. Every request is recorded in
/// <see cref="Calls"/>. A call with no script left throws, so an unexpected model call fails the test.
/// </summary>
public sealed class FakeProvider
{
    private readonly TimeProvider _time;
    private readonly Queue<IReadOnlyList<ScriptStep>> _scripts = new();
    private readonly List<ModelRequest> _calls = [];
    private readonly Lock _gate = new();

    public FakeProvider(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Every request streamed so far, in order, including a call that found no script.</summary>
    public IReadOnlyList<ModelRequest> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>Scripts that have not been played yet.</summary>
    public int RemainingScripts
    {
        get
        {
            lock (_gate)
            {
                return _scripts.Count;
            }
        }
    }

    /// <summary>Queues the script for one model call. Throws if a terminal step is not last, or a malformed JSON is valid.</summary>
    public void EnqueueCall(params ScriptStep[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Length == 0)
        {
            throw new ArgumentException("A scripted call needs at least one step.", nameof(steps));
        }

        for (var i = 0; i < steps.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(steps[i]);
            if (steps[i] is ScriptStep.MalformedToolJson malformed)
            {
                RejectValidJson(malformed.RawArguments);
            }

            if (IsTerminal(steps[i]) && i < steps.Length - 1)
            {
                throw new ArgumentException($"Step {i} ends the call, so it must be the last step.", nameof(steps));
            }
        }

        lock (_gate)
        {
            _scripts.Enqueue(steps.ToArray());
        }
    }

    /// <summary>Streams the next scripted call. The request is recorded now, and the script is taken now.</summary>
    public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<ScriptStep> script;
        lock (_gate)
        {
            _calls.Add(request);
            if (_scripts.Count == 0)
            {
                throw new InvalidOperationException($"No script is queued for the model call from agent '{request.AgentId}'.");
            }

            script = _scripts.Dequeue();
        }

        return Play(script, cancellationToken);
    }

    private async IAsyncEnumerable<ModelEvent> Play(
        IReadOnlyList<ScriptStep> steps,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (step)
            {
                case ScriptStep.Emit emit:
                    yield return emit.Event;
                    break;

                case ScriptStep.RateLimited rateLimited:
                    yield return new ModelEvent.Failure("rate_limited", "The upstream returned 429.", rateLimited.RetryAfterSeconds);
                    yield break;

                case ScriptStep.SpendCapped:
                    yield return new ModelEvent.Failure("spend_capped", "The upstream spend cap is reached.");
                    yield break;

                case ScriptStep.TimedOut timedOut:
                    await Task.Delay(timedOut.After, _time, cancellationToken);
                    yield return new ModelEvent.Failure("timeout", "The upstream did not respond in time.");
                    yield break;

                case ScriptStep.MalformedToolJson malformed:
                    yield return new ModelEvent.ToolCallStart(malformed.CallId, malformed.Name);
                    yield return new ModelEvent.ToolCallArgsDelta(malformed.CallId, malformed.RawArguments);
                    yield return new ModelEvent.ToolCallEnd(malformed.CallId);
                    yield return new ModelEvent.Failure("malformed_tool_json", $"Tool call '{malformed.CallId}' has arguments that are not valid JSON.");
                    yield break;

                case ScriptStep.ConnectionDropped:
                    yield return new ModelEvent.Failure("stream_dropped", "The stream was cut off before it finished.");
                    yield break;

                default:
                    throw new InvalidOperationException($"Unknown script step '{step.GetType().Name}'.");
            }
        }
    }

    private static bool IsTerminal(ScriptStep step) => step switch
    {
        ScriptStep.Emit { Event: ModelEvent.Failure or ModelEvent.Done } => true,
        ScriptStep.Emit => false,
        _ => true,
    };

    private static void RejectValidJson(string raw)
    {
        try
        {
            using var _ = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return;
        }

        throw new ArgumentException("A malformed tool call must carry arguments that are not valid JSON.", nameof(raw));
    }
}
