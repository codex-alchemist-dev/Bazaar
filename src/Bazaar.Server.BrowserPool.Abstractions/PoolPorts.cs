namespace Bazaar.Server.BrowserPool.Abstractions;

/// <summary>
/// One command to run inside the pool VM. It is an argument vector, never a shell string: the host runs it with no shell, so no
/// element is ever re-parsed. Elements may contain spaces (Chromium's <c>--host-resolver-rules</c> does).
/// </summary>
/// <param name="Argv">Program and arguments.</param>
/// <param name="AllowedExitCodes">Exit codes that count as success. Null means only 0.</param>
public sealed record PoolCommand(IReadOnlyList<string> Argv, IReadOnlyList<int>? AllowedExitCodes = null);

/// <summary>
/// Everything the host needs to run one session: commands to run before, the launch command, commands to run at close (which must
/// always run), and the egress rule for the slot's OS user.
/// </summary>
/// <param name="SessionId">The session id.</param>
/// <param name="AgentId">The owning agent.</param>
/// <param name="Slot">The slot number.</param>
/// <param name="Uid">The slot's uid (equal to its gid).</param>
/// <param name="Prepare">Run in order before the launch (wipe and create the slot directory).</param>
/// <param name="Launch">The long-running browser command, to be started as the pool launcher (root), which drops to the slot uid itself.</param>
/// <param name="Cleanup">Run in order at close, always, including after a failed open.</param>
/// <param name="Firewall">The egress rule that must be in force before <see cref="Launch"/> starts.</param>
public sealed record PoolLaunchSpec(
    string SessionId,
    string AgentId,
    int Slot,
    int Uid,
    IReadOnlyList<PoolCommand> Prepare,
    IReadOnlyList<string> Launch,
    IReadOnlyList<PoolCommand> Cleanup,
    PoolFirewallRule Firewall);

/// <summary>A started browser in the pool VM.</summary>
/// <param name="SessionId">The session id.</param>
/// <param name="GuestProcessId">The host's handle for the guest process.</param>
public sealed record PoolProcess(string SessionId, string GuestProcessId);

/// <summary>
/// The host's door into the pool VM: runs argv lists as the pool launcher (root) over the guest channel. The module has no
/// hypervisor or guest code of its own. Implementations must throw when a command exits with a code outside its allowed list.
/// </summary>
public interface IPoolVmHost
{
    /// <summary>Runs a command to completion; throws if it fails.</summary>
    Task RunAsync(PoolCommand command, CancellationToken ct);

    /// <summary>
    /// Makes this the complete set of per-uid egress rules in the pool VM, replacing any earlier set, and keeping the default drop
    /// for every slot uid. Called before a browser starts and again after one ends. Must be atomic: it may never leave a window with no drop rule.
    /// </summary>
    Task ApplyFirewallAsync(IReadOnlyList<PoolFirewallRule> rules, CancellationToken ct);

    /// <summary>Starts the long-running launch command and returns when it is running.</summary>
    Task<PoolProcess> StartAsync(PoolLaunchSpec spec, CancellationToken ct);

    /// <summary>Stops a started browser. Cleanup commands still run afterwards.</summary>
    Task StopAsync(PoolProcess process, CancellationToken ct);
}

/// <summary>The host's egress proxy for an agent, as an address the pool VM can reach.</summary>
/// <param name="Host">An IPv4 literal; names are refused.</param>
/// <param name="Port">TCP port, 1 to 65535.</param>
public sealed record PoolEgressEndpoint(string Host, int Port);

/// <summary>Finds the agent's host egress proxy (doc 16). The module never decides what an agent may reach; the proxy does.</summary>
public interface IPoolEgressPolicy
{
    /// <summary>The agent's proxy endpoint, or null when the agent has none. Null means no browser.</summary>
    Task<PoolEgressEndpoint?> ResolveAsync(string agentId, CancellationToken ct);
}

/// <summary>The kill switch and taint gate (doc 15). The default registration blocks everyone until the host registers the real one.</summary>
public interface IPoolGate
{
    /// <summary>True when the agent may not have pool sessions now.</summary>
    bool IsBlocked(string agentId);
}

/// <summary>
/// The asynchronous form of <see cref="IPoolGate"/>, for gates whose answer comes from an asynchronous read (the kill switch reader,
/// doc 15 "Reads for modules"). When one is registered the pool asks it, awaited, at each point where it would call
/// <see cref="IPoolGate.IsBlocked"/>, and the synchronous gate is not consulted. An implementation is also registered as the
/// <see cref="IPoolGate"/> so that code which only knows the synchronous port stays safe.
/// </summary>
public interface IAsyncPoolGate
{
    /// <summary>
    /// True when the agent may not have pool sessions now. An implementation answers true when it cannot tell. The pool also treats
    /// an exception other than cancellation as true. Cancellation propagates.
    /// </summary>
    ValueTask<bool> IsBlockedAsync(string agentId, CancellationToken ct);
}

/// <summary>Audit rows for pool events. Implementations must not throw and must never record page content.</summary>
public interface IPoolAuditSink
{
    /// <summary>A session was opened.</summary>
    void Opened(PoolSession session);

    /// <summary>A session was closed.</summary>
    void Closed(PoolSession session, PoolCloseReason reason);

    /// <summary>An open was refused or failed.</summary>
    void Refused(string agentId, PoolFailure failure, string detail);

    /// <summary>A slot was taken out of use because its cleanup did not complete.</summary>
    void SlotQuarantined(int slot, string sessionId, string agentId, string detail);
}
