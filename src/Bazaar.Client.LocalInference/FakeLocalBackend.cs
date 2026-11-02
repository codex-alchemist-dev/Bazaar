namespace Bazaar.Client.LocalInference;

/// <summary>
/// Scripted backend for tests (doc 31, Testing). Each call to <see cref="RunAsync"/> consumes the next scripted step:
/// a text reply, or an exception to throw (timeout, out of memory, blob mismatch).
/// </summary>
public sealed class FakeLocalBackend : ILocalModelBackend
{
    private readonly Queue<Func<LocalTaskRequest, LocalTaskResult>> _script = new();

    public FakeLocalBackend(BackendTier tier = BackendTier.Fake) => Tier = tier;

    public BackendTier Tier { get; }

    public int CallCount { get; private set; }

    public FakeLocalBackend Reply(string text, bool degraded = false, TimeSpan? elapsed = null)
    {
        _script.Enqueue(request => new LocalTaskResult(text, Tier, degraded, elapsed ?? TimeSpan.FromMilliseconds(1)));
        return this;
    }

    public FakeLocalBackend Throw(Exception error)
    {
        _script.Enqueue(_ => throw error);
        return this;
    }

    public async Task<LocalTaskResult> RunAsync(LocalTaskRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CallCount++;
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("FakeLocalBackend has no scripted step left.");
        }

        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        return _script.Dequeue()(request);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
