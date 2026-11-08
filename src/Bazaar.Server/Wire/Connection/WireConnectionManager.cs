using System.Net.WebSockets;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Connection;

/// <summary>Runs accepted WebSockets as wire connections.</summary>
public sealed class WireConnectionManager
{
    private readonly IOptions<WireOptions> _options;
    private readonly IWireCodec[] _codecs;
    private readonly IConnectionAuthenticator _authenticator;
    private readonly WireConnectionRegistry _registry;
    private readonly IdempotentRequestExecutor _idempotency;
    private readonly IReadOnlyDictionary<string, IWireRequestHandler> _handlers;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;
    private readonly SubscriptionEngine? _subscriptions;

    /// <summary>
    /// Creates the manager. Handlers are keyed by type; two handlers for one type are a startup error. With a
    /// <paramref name="subscriptions"/> engine, each authenticated connection gets its own subscription session.
    /// </summary>
    public WireConnectionManager(
        IOptions<WireOptions> options,
        IEnumerable<IWireCodec> codecs,
        IConnectionAuthenticator authenticator,
        WireConnectionRegistry registry,
        IdempotentRequestExecutor idempotency,
        IEnumerable<IWireRequestHandler> handlers,
        TimeProvider time,
        ILoggerFactory logs,
        SubscriptionEngine? subscriptions = null)
    {
        _subscriptions = subscriptions;
        _options = options;
        _codecs = codecs.ToArray();
        if (_codecs.Length == 0) throw new InvalidOperationException("No wire codec is registered.");
        _authenticator = authenticator;
        _registry = registry;
        _idempotency = idempotency;
        _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);
        _time = time;
        _logs = logs;
    }

    /// <summary>The live connections.</summary>
    public WireConnectionRegistry Registry => _registry;

    /// <summary>Serves one socket until it closes.</summary>
    /// <param name="socket">An accepted WebSocket.</param>
    /// <param name="headerCredential">Credential from the upgrade <c>Authorization</c> header, or null.</param>
    /// <param name="cancellationToken">Request abort token.</param>
    public Task RunAsync(WebSocket socket, string? headerCredential, CancellationToken cancellationToken) =>
        RunCoreAsync(socket, headerCredential, cancellationToken);

    private async Task RunCoreAsync(WebSocket socket, string? headerCredential, CancellationToken cancellationToken)
    {
        using var connection = new WireConnection(
            socket, headerCredential, _options.Value, _codecs, _authenticator, _registry, _idempotency, _handlers,
            _time, _logs.CreateLogger<WireConnection>(), _subscriptions);
        await connection.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
