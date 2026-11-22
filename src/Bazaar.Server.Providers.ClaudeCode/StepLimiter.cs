namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>
/// The lane's own throttle (docs/design/91 section 9, rule T7): a concurrency cap that queues, a minimum spacing between starts,
/// and an hourly step cap that refuses. Only the hourly cap is a quota fact; waiting for a free slot is not.
/// </summary>
internal sealed class StepLimiter(ClaudeCodeOptions options, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.MaxConcurrent));
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _starts = new();
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;

    /// <summary>Waits for a slot. Returns false with <paramref name="retryAfter"/> set when the hourly cap is full.</summary>
    public async Task<bool> TryEnterAsync(Action<TimeSpan> onRetryAfter, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        TimeSpan wait;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            while (_starts.Count > 0 && now - _starts.Peek() >= TimeSpan.FromHours(1))
            {
                _starts.Dequeue();
            }

            if (options.MaxStepsPerHour > 0 && _starts.Count >= options.MaxStepsPerHour)
            {
                onRetryAfter(_starts.Peek() + TimeSpan.FromHours(1) - now);
                _slots.Release();
                return false;
            }

            var earliest = _lastStart + options.MinSpacing;
            wait = earliest > now ? earliest - now : TimeSpan.Zero;
            _lastStart = now + wait;
            _starts.Enqueue(_lastStart);
        }

        if (wait > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(wait, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _slots.Release();
                throw;
            }
        }

        return true;
    }

    public void Exit() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}
