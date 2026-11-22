namespace Bazaar.Server.Valkey;

/// <summary>Where the managed child is in its supervision lifecycle.</summary>
public enum ValkeyChildState
{
    /// <summary>Not supervised: Valkey is external, or the cache is off.</summary>
    Disabled,

    /// <summary>Launched, waiting for the first successful PING.</summary>
    Starting,

    /// <summary>Answering PING and supervised.</summary>
    Running,

    /// <summary>Failed or exited; waiting before the next attempt.</summary>
    Backoff,

    /// <summary>The circuit breaker is open. No further attempt runs until the host restarts.</summary>
    Tripped,

    /// <summary>The host is shutting down, or the supervisor has stopped.</summary>
    Stopped,
}

/// <summary>Read-only view of the managed child, used by the liveness probe.</summary>
public interface IValkeyChildStatus
{
    /// <summary>The current lifecycle state.</summary>
    ValkeyChildState State { get; }

    /// <summary>How many times a child has been started after the first attempt.</summary>
    int Restarts { get; }
}
