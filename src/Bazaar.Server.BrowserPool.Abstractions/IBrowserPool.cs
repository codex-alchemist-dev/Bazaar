namespace Bazaar.Server.BrowserPool.Abstractions;

/// <summary>
/// Runs many agents' credential-free ("anonymous") browsers side by side in one shared pool VM, each in its own OS user,
/// namespace sandbox and empty profile directory, with egress only through that agent's proxy (doc 59 sections 5 to 7).
/// Logged-in account profiles never run here, and this interface has no way to ask for one.
/// </summary>
public interface IBrowserPool
{
    /// <summary>Starts a browser session for the request, or throws <see cref="BrowserPoolException"/> with the reason it was refused.</summary>
    Task<PoolSession> OpenAsync(PoolSessionRequest request, CancellationToken ct);

    /// <summary>Closes one session and destroys its profile directory. Safe to call again and for unknown ids.</summary>
    Task CloseAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// The kill-switch call: closes every session of the agent, including ones still being opened, and returns how many were
    /// affected. It never waits for a slow open to finish.
    /// </summary>
    Task<int> CloseAllForAgentAsync(string agentId, CancellationToken ct);

    /// <summary>The agent's open sessions.</summary>
    IReadOnlyList<PoolSession> Sessions(string agentId);

    /// <summary>Marks activity on a session so the idle timeout restarts. Unknown ids are ignored.</summary>
    void Touch(string sessionId);

    /// <summary>The pool's current state.</summary>
    PoolSnapshot Snapshot();

    /// <summary>Closes expired and idle-timed-out sessions, abandons opens that hung, and retries cleanup of quarantined slots. Returns the number of sessions closed.</summary>
    Task<int> SweepAsync(CancellationToken ct);
}

/// <summary>Why a session was ended, for the audit trail.</summary>
public enum PoolCloseReason
{
    /// <summary>The caller closed it.</summary>
    Requested,

    /// <summary>The kill switch: all of the agent's sessions were closed.</summary>
    KillSwitch,

    /// <summary>It reached its hard lifetime.</summary>
    Expired,

    /// <summary>No activity within the idle timeout.</summary>
    IdleTimeout,

    /// <summary>It never finished opening.</summary>
    OpenFailed,
}

/// <summary>Why an open was refused or failed.</summary>
public enum PoolFailure
{
    /// <summary>The pool is switched off.</summary>
    Disabled,

    /// <summary>The kill switch or the gate refuses this agent.</summary>
    AgentBlocked,

    /// <summary>The agent has no egress proxy, so it gets no browser (a pool browser without a proxy would have no network control).</summary>
    NoEgress,

    /// <summary>Every slot is taken or quarantined.</summary>
    PoolFull,

    /// <summary>The agent is at its own session limit.</summary>
    AgentLimit,

    /// <summary>The request failed validation.</summary>
    InvalidRequest,

    /// <summary>The browser could not be started, or was ended while starting.</summary>
    LaunchFailed,
}

/// <summary>Thrown by <see cref="IBrowserPool.OpenAsync"/> when the open is refused or fails. The message never contains page content.</summary>
public sealed class BrowserPoolException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BrowserPoolException(PoolFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    /// <summary>Creates the exception with the underlying error.</summary>
    public BrowserPoolException(PoolFailure failure, string message, Exception innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>The reason.</summary>
    public PoolFailure Failure { get; }
}
