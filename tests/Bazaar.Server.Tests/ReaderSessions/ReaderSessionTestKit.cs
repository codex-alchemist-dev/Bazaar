using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Security.Readers;

namespace Bazaar.Server.Tests.ReaderSessions;

/// <summary>Scripted gateway: records every request and replays the configured events, or runs a hook.</summary>
internal sealed class ScriptedGateway : IModelGateway
{
    private readonly Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelEvent>> _script;

    public ScriptedGateway(Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelEvent>> script) => _script = script;

    public ConcurrentQueue<ModelRequest> Requests { get; } = new();

    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Reader sessions never embed.");

    public static ScriptedGateway Replying(string text) => Events(
        new ModelEvent.TextDelta(text), new ModelEvent.Done(StopReason.EndTurn, "fake-model"));

    public static ScriptedGateway Events(params ModelEvent[] events) => new((_, _) => Stream(events));

    public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return _script(request, cancellationToken);
    }

    private static async IAsyncEnumerable<ModelEvent> Stream(params ModelEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }
}

/// <summary>Wraps the in-memory store and records the sessions it was asked to purge, and what was in scratch at purge time.</summary>
internal sealed class RecordingScratchStore : IReaderScratchStore
{
    private readonly InMemoryReaderScratchStore _inner = new();

    public ConcurrentQueue<Guid> Opened { get; } = new();

    public ConcurrentQueue<Guid> Purged { get; } = new();

    public async Task PutAsync(Guid sessionId, string key, string value, CancellationToken ct)
    {
        Opened.Enqueue(sessionId);
        await _inner.PutAsync(sessionId, key, value, ct);
    }

    public Task<string?> GetAsync(Guid sessionId, string key, CancellationToken ct) => _inner.GetAsync(sessionId, key, ct);

    public async Task PurgeAsync(Guid sessionId, CancellationToken ct)
    {
        await _inner.PurgeAsync(sessionId, ct);
        Purged.Enqueue(sessionId);
    }
}
