namespace Bazaar.Server.Browser.Abstractions;

/// <summary>Kinds of browser profile (doc 14, "Browser (C22)"). Mirrors the Identity profile kinds without referencing them.</summary>
public enum BrowserProfileClass
{
    /// <summary>No credentials. Reads arbitrary pages. Never receives a cookie.</summary>
    Anonymous,

    /// <summary>Allowlisted sites only. Cookies come from Passport.</summary>
    Account,
}

/// <summary>Whether Chromium shows a window in the guest (doc 16: headless by default, one VM may be headed).</summary>
public enum BrowserDisplayMode
{
    Headless,
    Headed,
}

/// <summary>Which of the agent's two VMs runs the browser (doc 16: the pair; no third VM).</summary>
public enum BrowserGuestOs
{
    Linux,
    Windows,
}

/// <summary>A browser profile as the launcher sees it. Built by the host from the Identity profile store.</summary>
public sealed record BrowserProfileRef(
    Guid ProfileId,
    string AgentId,
    BrowserProfileClass Class,
    IReadOnlyList<string> ServiceAllowlist);

/// <summary>
/// One cookie to hand to a browser session. The value never reaches logs: <see cref="ToString"/> prints only the
/// name, domain and path.
/// </summary>
public sealed record BrowserCookieEntry(string Name, string Value, string Domain, string Path, DateTimeOffset? Expires)
{
    /// <inheritdoc />
    public override string ToString() => $"{Name}@{Domain}{Path}";
}

/// <summary>The host egress proxy a guest browser must use (doc 16, "Network and egress"). DNS also goes through it.</summary>
public sealed record BrowserEgressEndpoint(string Host, int Port);

/// <summary>A request to start one browser session for one agent.</summary>
public sealed record BrowserLaunchRequest(
    string AgentId,
    Guid ProfileId,
    BrowserGuestOs GuestOs,
    BrowserDisplayMode Display,
    string? StartUrl);

/// <summary>Everything the guest host needs to start Chromium. No secrets: cookies travel separately over the guest channel.</summary>
public sealed record BrowserLaunchSpec(
    string SessionId,
    string AgentId,
    BrowserGuestOs GuestOs,
    string ExecutablePath,
    IReadOnlyList<string> Arguments);

/// <summary>A running browser process in a guest.</summary>
public sealed record BrowserProcessHandle(string SessionId, string GuestProcessId);

/// <summary>A launched session as the caller sees it.</summary>
public sealed record BrowserSession(
    string SessionId,
    string AgentId,
    Guid ProfileId,
    BrowserProfileClass Class,
    BrowserDisplayMode Display,
    DateTimeOffset StartedAt);

/// <summary>Why a launch was refused. The launcher fails closed on every one of them.</summary>
public enum BrowserLaunchFailure
{
    ProfileNotFound,
    ProfileOwnedByAnotherAgent,
    HardTainted,
    NoEgress,
    CookiesRefused,
    GuestFailed,
}

/// <summary>Thrown when a launch is refused. The message never contains cookie values.</summary>
public sealed class BrowserLaunchException(BrowserLaunchFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public BrowserLaunchFailure Failure { get; } = failure;
}

/// <summary>Launches and ends agent browser sessions (doc 16 WP13, doc 14 Browser).</summary>
public interface IBrowserLauncher
{
    /// <exception cref="BrowserLaunchException">The launch was refused or the guest failed.</exception>
    Task<BrowserSession> LaunchAsync(BrowserLaunchRequest request, CancellationToken ct);

    /// <summary>Stops the session and clears its cookies. Safe for an unknown or already ended session.</summary>
    Task EndAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Hard taint (doc 16 behaviour): ends every account session of the agent and drops its cookies. Anonymous
    /// sessions are untouched. Returns how many sessions ended.
    /// </summary>
    Task<int> DetachAccountSessionsAsync(string agentId, CancellationToken ct);

    /// <summary>The live sessions of one agent.</summary>
    IReadOnlyList<BrowserSession> ActiveSessions(string agentId);

    /// <summary>Every live session, for lifetime sweeps and the admin view.</summary>
    IReadOnlyList<BrowserSession> AllActiveSessions();
}

/// <summary>Port: looks up profiles. The host adapts the Identity profile store to it.</summary>
public interface IBrowserProfileDirectory
{
    Task<BrowserProfileRef?> FindAsync(Guid profileId, CancellationToken ct);
}

/// <summary>
/// Port: decrypted cookies for one account session, allowlist-filtered and unexpired. The host adapts the Identity
/// materializer and injector. The launcher never calls it for an anonymous profile.
/// </summary>
public interface IBrowserCookieSource
{
    Task<IReadOnlyList<BrowserCookieEntry>> GetCookiesAsync(string sessionId, BrowserProfileRef profile, CancellationToken ct);

    /// <summary>Drops the session's decrypted cookies.</summary>
    void EndSession(string sessionId);
}

/// <summary>Port: the per-agent egress proxy (doc 16 WP9). Null means no route, so the launch is refused.</summary>
public interface IBrowserEgressPolicy
{
    Task<BrowserEgressEndpoint?> ResolveAsync(string agentId, CancellationToken ct);
}

/// <summary>Port: current taint state (doc 15). A hard-tainted agent gets no browser.</summary>
public interface IBrowserTaintGate
{
    bool IsHardTainted(string agentId);
}

/// <summary>Port: runs Chromium inside the agent's VM over the guest channel (doc 16 WP6-8).</summary>
public interface IBrowserGuestHost
{
    Task<BrowserProcessHandle> StartAsync(BrowserLaunchSpec spec, CancellationToken ct);

    /// <summary>Sets cookies in the running browser (Chrome DevTools Protocol over the guest channel).</summary>
    Task SetCookiesAsync(BrowserProcessHandle handle, IReadOnlyList<BrowserCookieEntry> cookies, CancellationToken ct);

    /// <summary>Clears every cookie, then stops the process tree.</summary>
    Task StopAsync(BrowserProcessHandle handle, CancellationToken ct);
}

/// <summary>
/// Narrow port: a byte pipe to a Chromium process in a guest (doc 16 WP6-7). Chromium is started with
/// <c>--remote-debugging-pipe</c>, so the guest exposes its fd3 and fd4 as this pipe. The real guest channel adapts to it.
/// </summary>
public interface IBrowserGuestPipe
{
    /// <summary>Starts the process and returns the guest process id.</summary>
    Task<string> StartProcessAsync(BrowserGuestOs os, string executable, IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>Writes one CDP frame (already NUL-terminated by the caller) to the process.</summary>
    Task WritePipeAsync(string guestProcessId, ReadOnlyMemory<byte> frame, CancellationToken ct);

    /// <summary>Reads one CDP JSON message, without its NUL terminator.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<byte[]> ReadPipeFrameAsync(string guestProcessId, CancellationToken ct);

    /// <summary>Kills the process and its children.</summary>
    Task KillProcessTreeAsync(string guestProcessId, CancellationToken ct);
}
