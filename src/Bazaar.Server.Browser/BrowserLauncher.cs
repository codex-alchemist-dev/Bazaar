using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.Browser;

/// <summary>
/// Starts and ends agent browser sessions (doc 16 WP13, doc 14 "Browser (C22)"). Every launch fails closed with a
/// <see cref="BrowserLaunchException"/>. No message produced here contains a cookie value.
/// </summary>
public sealed class BrowserLauncher(
    IBrowserProfileDirectory profiles,
    IBrowserCookieSource cookieSource,
    IBrowserEgressPolicy egressPolicy,
    IBrowserTaintGate taintGate,
    IBrowserGuestHost host,
    BrowserOptions options,
    TimeProvider time) : IBrowserLauncher
{
    private sealed record Live(BrowserSession Session, BrowserProcessHandle Handle);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Live> _live = [];

    /// <inheritdoc />
    public async Task<BrowserSession> LaunchAsync(BrowserLaunchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await profiles.FindAsync(request.ProfileId, ct).ConfigureAwait(false)
            ?? throw new BrowserLaunchException(BrowserLaunchFailure.ProfileNotFound, "Browser profile not found.");
        if (!string.Equals(profile.AgentId, request.AgentId, StringComparison.Ordinal))
            throw new BrowserLaunchException(BrowserLaunchFailure.ProfileOwnedByAnotherAgent, "Browser profile belongs to another agent.");
        // Hard taint blocks every browser, anonymous included (doc 16).
        if (taintGate.IsHardTainted(request.AgentId))
            throw new BrowserLaunchException(BrowserLaunchFailure.HardTainted, "Agent is hard-tainted; no browser is available.");
        var egress = await egressPolicy.ResolveAsync(request.AgentId, ct).ConfigureAwait(false)
            ?? throw new BrowserLaunchException(BrowserLaunchFailure.NoEgress, "No egress route for this agent.");

        var sessionId = Guid.NewGuid().ToString("N");
        var windows = request.GuestOs == BrowserGuestOs.Windows;
        var root = windows ? options.WindowsProfileRoot : options.LinuxProfileRoot;
        var separator = windows ? '\\' : '/';
        var profileDirectory = root.TrimEnd('/', '\\') + separator + profile.ProfileId.ToString("D");
        var isAccount = profile.Class == BrowserProfileClass.Account;

        // Account launches never get a start URL here: navigation happens after cookies are set, by the caller.
        var startUrl = isAccount ? null : request.StartUrl;
        var args = ChromiumCommandLine.Build(profile.Class, request.Display, request.GuestOs, profileDirectory, egress, startUrl);
        var spec = new BrowserLaunchSpec(sessionId, request.AgentId, request.GuestOs,
            windows ? options.WindowsExecutable : options.LinuxExecutable, args);

        BrowserProcessHandle handle;
        try
        {
            handle = await host.StartAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BrowserLaunchException(BrowserLaunchFailure.GuestFailed, "The guest failed to start the browser.", ex);
        }

        if (isAccount)
        {
            try
            {
                var cookies = await cookieSource.GetCookiesAsync(sessionId, profile, ct).ConfigureAwait(false);
                await host.SetCookiesAsync(handle, cookies, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RollbackAsync(sessionId, handle).ConfigureAwait(false);
                if (ex is OperationCanceledException)
                    throw;
                throw new BrowserLaunchException(BrowserLaunchFailure.CookiesRefused, "Cookies could not be loaded into the browser.", ex);
            }
        }

        var session = new BrowserSession(sessionId, request.AgentId, profile.ProfileId, profile.Class, request.Display, time.GetUtcNow());
        lock (_gate)
            _live[sessionId] = new Live(session, handle);
        return session;
    }

    /// <inheritdoc />
    public async Task EndAsync(string sessionId, CancellationToken ct)
    {
        Live? live;
        lock (_gate)
        {
            if (!_live.Remove(sessionId, out live))
                return;
        }

        await StopAsync(live, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DetachAccountSessionsAsync(string agentId, CancellationToken ct)
    {
        List<Live> victims;
        lock (_gate)
        {
            victims = [.. _live.Values.Where(l => l.Session.AgentId == agentId && l.Session.Class == BrowserProfileClass.Account)];
            foreach (var v in victims)
                _live.Remove(v.Session.SessionId);
        }

        List<Exception>? errors = null;
        foreach (var v in victims)
        {
            try
            {
                await StopAsync(v, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
            throw new AggregateException("Some account browser sessions did not stop cleanly.", errors);
        return victims.Count;
    }

    /// <inheritdoc />
    public IReadOnlyList<BrowserSession> ActiveSessions(string agentId)
    {
        lock (_gate)
            return [.. _live.Values.Where(l => l.Session.AgentId == agentId).Select(l => l.Session)];
    }

    /// <inheritdoc />
    public IReadOnlyList<BrowserSession> AllActiveSessions()
    {
        lock (_gate)
            return [.. _live.Values.Select(l => l.Session)];
    }

    private async Task StopAsync(Live live, CancellationToken ct)
    {
        try
        {
            await host.StopAsync(live.Handle, ct).ConfigureAwait(false);
        }
        finally
        {
            // Cookies are dropped even when the stop fails.
            cookieSource.EndSession(live.Session.SessionId);
        }
    }

    private async Task RollbackAsync(string sessionId, BrowserProcessHandle handle)
    {
        try
        {
            await host.StopAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the launch error is the one the caller needs.
        }
        finally
        {
            cookieSource.EndSession(sessionId);
        }
    }
}
