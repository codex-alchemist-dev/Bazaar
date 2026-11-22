namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>Mutable lane health, read through <see cref="IClaudeCodeLaneState"/>. Thread safe.</summary>
public sealed class ClaudeCodeLaneState(TimeProvider time) : IClaudeCodeLaneState
{
    private readonly object _gate = new();
    private string? _down;
    private DateTimeOffset? _limitedUntil;
    private DateTimeOffset? _lastSuccess;
    private string? _lastError;
    private long _ignored;

    public ClaudeCodeStatus Current
    {
        get
        {
            lock (_gate)
            {
                var until = _limitedUntil is { } u && u > time.GetUtcNow() ? u : (DateTimeOffset?)null;
                return new ClaudeCodeStatus(_down is null, _down, until, _lastSuccess, _lastError, _ignored);
            }
        }
    }

    internal void MarkDown(string reason, string errorClass)
    {
        lock (_gate)
        {
            _down = reason;
            _lastError = errorClass;
        }
    }

    internal void MarkUp()
    {
        lock (_gate)
        {
            _down = null;
        }
    }

    internal void MarkRateLimited(TimeSpan retryAfter)
    {
        lock (_gate)
        {
            _limitedUntil = time.GetUtcNow() + retryAfter;
            _lastError = "rate_limited";
        }
    }

    internal void MarkSuccess(long ignored)
    {
        lock (_gate)
        {
            _lastSuccess = time.GetUtcNow();
            _ignored += ignored;
        }
    }

    internal void MarkError(string errorClass, long ignored)
    {
        lock (_gate)
        {
            _lastError = errorClass;
            _ignored += ignored;
        }
    }
}
