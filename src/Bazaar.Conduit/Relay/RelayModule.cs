using Microsoft.Extensions.Logging;

namespace Bazaar.Conduit.Relay;

/// <summary>
/// Operator relay (doc 39 section 2, priority 50). The server opens an outbound WebSocket to a relay the operator runs,
/// registers its room, and publishes one offer that clients reach through the relay. The relay forwards opaque bytes;
/// TLS runs end to end to the server key, so the module never decrypts anything and holds no keys.
/// </summary>
public sealed class RelayModule : IConduitModule
{
    /// <summary>Settings key for the relay's absolute <c>wss://</c> URL. It must carry no credentials.</summary>
    public const string UrlKey = "conduit.relay.url";

    /// <summary>Settings key for the opaque room id on the relay. It is an identifier, never a secret.</summary>
    public const string RoomKey = "conduit.relay.room";

    private static readonly TimeSpan _initialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _closeTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<IRelaySocket> _socketFactory;
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private IRelaySocket? _socket;
    private Task? _supervisor;
    private ILogger? _logger;
    private ModuleStatus _status;

    /// <summary>Creates the module with the real WebSocket transport.</summary>
    public RelayModule()
        : this(static () => new ClientWebSocketRelaySocket())
    {
    }

    /// <summary>Creates the module with a socket factory, for tests or alternative transports.</summary>
    public RelayModule(Func<IRelaySocket> socketFactory)
    {
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _status = new ModuleStatus(Id, ConduitModuleState.Disabled, null, []);
    }

    /// <inheritdoc />
    public string Id => "relay";

    /// <inheritdoc />
    public string DisplayName => "Operator relay";

    /// <inheritdoc />
    public ConduitScope Scope => ConduitScope.Relay;

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public bool DefaultEnabled => false;

    /// <inheritdoc />
    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ModuleStatus>? StatusChanged;

    /// <inheritdoc />
    /// <remarks>
    /// The first connection is made here, so a launch error becomes <see cref="ConduitModuleState.Failed"/> and is never
    /// thrown. Later drops are handled by the supervisor with backoff.
    /// </remarks>
    public async ValueTask StartAsync(ConduitContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryResolve(context, out var plan, out var failure))
        {
            RelayLog.SettingsRejected(context.Logger, failure);
            if (!IsRunning)
            {
                SetStatus(new ModuleStatus(Id, ConduitModuleState.Failed, failure, []));
            }

            return;
        }

        var lifetime = new CancellationTokenSource();
        lock (_gate)
        {
            if (_lifetime is not null)
            {
                return;
            }

            _lifetime = lifetime;
            _logger = context.Logger;
        }

        SetStatus(new ModuleStatus(Id, ConduitModuleState.Starting, null, []));

        IRelaySocket socket;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            socket = await OpenAsync(plan, startup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bool owned;
            lock (_gate)
            {
                owned = ReferenceEquals(_lifetime, lifetime);
                if (owned)
                {
                    _lifetime = null;
                    _logger = null;
                }
            }

            // StopAsync already ran and reported Disabled; nothing more to report.
            if (owned)
            {
                RelayLog.LaunchFailed(context.Logger, ex.GetType().Name);
                SetStatus(new ModuleStatus(Id, ConduitModuleState.Failed, ex.GetType().Name, []));
            }

            return;
        }

        if (!PublishRunning(lifetime, plan, socket))
        {
            socket.Dispose();
            return;
        }

        var supervisor = SuperviseAsync(context, plan, socket, lifetime);
        lock (_gate)
        {
            if (ReferenceEquals(_lifetime, lifetime))
            {
                _supervisor = supervisor;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Withdraws the offer, cancels the connection and closes the socket within the token.</remarks>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var disabled = new ModuleStatus(Id, ConduitModuleState.Disabled, null, []);
        CancellationTokenSource? lifetime;
        Task? supervisor;
        IRelaySocket? socket;
        ILogger? logger;
        lock (_gate)
        {
            lifetime = _lifetime;
            supervisor = _supervisor;
            socket = _socket;
            logger = _logger;
            _lifetime = null;
            _supervisor = null;
            _socket = null;
            _logger = null;
            _status = disabled;
        }

        lifetime?.Cancel();
        StatusChanged?.Invoke(this, disabled);

        if (socket is not null)
        {
            try
            {
                await socket.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (logger is not null)
                {
                    RelayLog.StopFailed(logger, ex.GetType().Name);
                }
            }
        }

        if (supervisor is not null)
        {
            try
            {
                await supervisor.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller's deadline passed; the socket was cancelled and closed above.
            }
        }
    }

    private bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _lifetime is not null;
            }
        }
    }

    private async Task SuperviseAsync(ConduitContext context, RelayPlan plan, IRelaySocket initial, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;
        IRelaySocket? current = initial;
        try
        {
            while (true)
            {
                await DrainAsync(current, context, token).ConfigureAwait(false);
                await ReleaseAsync(current, token).ConfigureAwait(false);
                current = null;

                lock (_gate)
                {
                    if (ReferenceEquals(_lifetime, lifetime))
                    {
                        _socket = null;
                    }
                }

                // The first retry waits 2 seconds. The timer is created before the status changes, so a test clock
                // advanced on Degraded sees it. Each later timer is created before the attempt it precedes.
                var failures = 0;
                var wait = Task.Delay(BackoffFor(failures), context.Time, token);
                RelayLog.Dropped(context.Logger);
                SetStatusIfCurrent(lifetime, new ModuleStatus(Id, ConduitModuleState.Degraded, "relay-disconnected", []));

                while (true)
                {
                    await wait.ConfigureAwait(false);
                    wait = Task.Delay(BackoffFor(failures + 1), context.Time, token);
                    current = await TryOpenAsync(context, plan, token).ConfigureAwait(false);
                    if (current is not null)
                    {
                        break;
                    }

                    failures++;
                }

                if (!PublishRunning(lifetime, plan, current))
                {
                    return;
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Stopped. StopAsync cancelled the lifetime and closes the socket it holds.
        }
        finally
        {
            current?.Dispose();
        }
    }

    private bool PublishRunning(CancellationTokenSource lifetime, RelayPlan plan, IRelaySocket socket)
    {
        var status = new ModuleStatus(Id, ConduitModuleState.Running, null, [plan.Offer]);
        lock (_gate)
        {
            if (!ReferenceEquals(_lifetime, lifetime))
            {
                return false;
            }

            _socket = socket;
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
        return true;
    }

    private void SetStatus(ModuleStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private void SetStatusIfCurrent(CancellationTokenSource lifetime, ModuleStatus status)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_lifetime, lifetime))
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private async Task<IRelaySocket?> TryOpenAsync(ConduitContext context, RelayPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            return await OpenAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            RelayLog.ReconnectFailed(context.Logger, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Connects and registers the room. The returned socket is connected; on failure it is disposed.</summary>
    private async Task<IRelaySocket> OpenAsync(RelayPlan plan, CancellationToken cancellationToken)
    {
        var socket = _socketFactory();
        try
        {
            await socket.ConnectAsync(plan.RelayUri, cancellationToken).ConfigureAwait(false);
            await socket.SendAsync(RelayFrameCodec.Encode(RelayFrame.Register(plan.Room)), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task DrainAsync(IRelaySocket socket, ConduitContext context, CancellationToken cancellationToken)
    {
        try
        {
            // Relayed bytes belong to clients and are never decoded here; the loop only waits for the close.
            while (await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            RelayLog.SocketFailed(context.Logger, ex.GetType().Name);
        }
    }

    private static async Task ReleaseAsync(IRelaySocket socket, CancellationToken cancellationToken)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(_closeTimeout);
            await socket.CloseAsync(bound.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            // The relay already dropped the connection. Disposing the socket below is enough.
        }
        finally
        {
            socket.Dispose();
        }
    }

    private static TimeSpan BackoffFor(int failures)
    {
        var exponent = Math.Min(failures, 10);
        var seconds = Math.Min(_maxBackoff.TotalSeconds, _initialBackoff.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    private bool TryResolve(ConduitContext context, out RelayPlan plan, out string reason)
    {
        plan = null!;
        var url = context.Settings.Read(UrlKey, string.Empty);
        var room = context.Settings.Read(RoomKey, string.Empty);
        if (url.Length == 0 || room.Length == 0)
        {
            reason = "not-configured";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var relay)
            || relay.Scheme != Uri.UriSchemeWss
            || relay.Host.Length == 0
            || relay.UserInfo.Length > 0
            || relay.Query.Length > 0
            || relay.Fragment.Length > 0)
        {
            reason = "invalid-relay-url";
            return false;
        }

        // The offer names the room in the path, never in a query, and carries no credentials.
        var basePath = relay.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        var endpoint = new Uri(basePath + "/rooms/" + Uri.EscapeDataString(room), UriKind.Absolute);
        var offer = new ConduitOffer(
            Id,
            Scope,
            endpoint,
            context.ServerKeyHash,
            ExpiresAt: null,
            RequiresRelay: true,
            Verified: false,
            TerminatesTls: false);

        if (ConduitOfferRules.Validate(offer) is not null)
        {
            reason = "invalid-offer";
            return false;
        }

        plan = new RelayPlan(relay, room, offer);
        reason = string.Empty;
        return true;
    }

    /// <summary>Resolved settings for one start. The room is never logged.</summary>
    private sealed record RelayPlan(Uri RelayUri, string Room, ConduitOffer Offer);
}
