namespace Bazaar.Server.Host.Startup;

/// <summary>Process-wide start facts for the status listener: when the host started and whether the startup sequence finished.</summary>
public sealed class HostState(TimeProvider time)
{
    private volatile bool _started;

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    /// <summary>True once the startup sequence (lock, schema check, provider probe) has completed.</summary>
    public bool Started => _started;

    public TimeSpan Uptime => time.GetUtcNow() - StartedAt;

    public void MarkStarted() => _started = true;
}
