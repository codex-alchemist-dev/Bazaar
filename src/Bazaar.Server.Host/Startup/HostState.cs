namespace Bazaar.Server.Host.Startup;

/// <summary>Process-wide start facts for the status listener: when the host started and whether the startup sequence finished.</summary>
public sealed class HostState(TimeProvider time)
{
    private volatile bool _started;

    private readonly long _startedTimestamp = time.GetTimestamp();

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    /// <summary>True once the startup sequence (lock, schema check, provider probe) has completed.</summary>
    public bool Started => _started;

    /// <summary>Time since start on the monotonic clock, so a wall-clock step (NTP, a VM resume, a bad RTC) never makes it negative or huge.</summary>
    public TimeSpan Uptime => time.GetElapsedTime(_startedTimestamp);

    public void MarkStarted() => _started = true;
}
