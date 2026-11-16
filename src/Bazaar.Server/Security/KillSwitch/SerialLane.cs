namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Runs asynchronous work one item at a time, in the order it was queued. The kill switch and containment adapters use it so a
/// store write and the matching cache update never interleave with another change.
/// </summary>
/// <remarks>
/// Each item waits for the one before it to finish. A queued item that is cancelled before it starts throws without running. An
/// item that fails does not block the items behind it.
/// </remarks>
internal sealed class SerialLane
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask; // guarded by _gate

    /// <summary>Runs <paramref name="work"/> after every item queued before it has finished.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before the work started.</exception>
    public async Task RunAsync(Func<Task> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _tail;
            _tail = done.Task;
        }

        try
        {
            await previous.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await work().ConfigureAwait(false);
        }
        finally
        {
            done.SetResult();
        }
    }
}
