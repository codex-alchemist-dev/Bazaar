using System.Threading.Channels;
using Bazaar.Client.LocalInference;

namespace Bazaar.Client.LocalInference.Tests.Scheduling;

/// <summary>
/// Backend whose invocations stay open until the test completes or fails them. Each one is announced on a channel, so tests
/// control ordering with no timing. Honours cancellation the way a real session does: the invocation throws.
/// </summary>
public sealed class GatedBackend : ILocalModelBackend
{
    private readonly Channel<Invocation> _started = Channel.CreateUnbounded<Invocation>();
    private readonly object _sync = new();
    private int _calls;
    private int _active;

    public BackendTier Tier => BackendTier.Fake;

    public int CallCount => Volatile.Read(ref _calls);

    public int MaxActive { get; private set; }

    public async Task<LocalTaskResult> RunAsync(LocalTaskRequest request, CancellationToken ct)
    {
        var invocation = new Invocation(request, ct);
        Interlocked.Increment(ref _calls);
        lock (_sync)
        {
            _active++;
            MaxActive = Math.Max(MaxActive, _active);
        }

        _started.Writer.TryWrite(invocation);
        using var registration = ct.Register(() => invocation.Outcome.TrySetCanceled(ct));
        try
        {
            return await invocation.Outcome.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _active--;
            }
        }
    }

    public Task<Invocation> NextStartedAsync() => _started.Reader.ReadAsync().AsTask();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public sealed class Invocation
    {
        internal Invocation(LocalTaskRequest request, CancellationToken token)
        {
            Request = request;
            Token = token;
        }

        public LocalTaskRequest Request { get; }

        public CancellationToken Token { get; }

        internal TaskCompletionSource<LocalTaskResult> Outcome { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(string text) =>
            Outcome.TrySetResult(new LocalTaskResult(text, BackendTier.Fake, Degraded: false, TimeSpan.Zero));

        public void Fail(Exception error) => Outcome.TrySetException(error);
    }
}
