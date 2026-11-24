using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Contracts.Engines;

namespace Bazaar.Server.EngineDispatch;

/// <summary>
/// Server half of the relay-request channel [PROPOSED] (doc 02 stream registry, doc 20). Authorises the call with
/// <see cref="EngineToolDispatcher"/> first, publishes an <see cref="EngineRelayRequestEvent"/> to the owner's desktop device, and
/// awaits the answer. An answer counts only from the device the event went to, once. Fails closed: a denied call is never
/// published, no target fails at once (never queued), and every pending entry is removed on answer, timeout, cancel or close.
/// The wire adapters (event publishing, <c>engine.relay.result</c> handler, connection close hook) live in the wire kernel.
/// </summary>
public sealed class EngineRelayBroker
{
    private static readonly JsonElement _emptyObject = JsonElement.Parse("{}");

    private readonly EngineToolDispatcher _dispatcher;
    private readonly IRelayTargetDirectory _targets;
    private readonly IRelayPublisher _publisher;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    public EngineRelayBroker(
        EngineToolDispatcher dispatcher,
        IRelayTargetDirectory targets,
        IRelayPublisher publisher,
        EngineRelayOptions? options = null,
        TimeProvider? time = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _timeout = (options ?? new EngineRelayOptions()).EffectiveTimeout;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Relays in flight, for diagnostics and leak tests.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Authorises, publishes and awaits one call. Throws only <see cref="OperationCanceledException"/> when <paramref name="ct"/> is
    /// cancelled; every other failure is an <see cref="EngineRelayOutcome"/>.
    /// </summary>
    public async Task<EngineRelayOutcome> RelayAsync(EngineDispatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var decision = await _dispatcher.AuthorizeAsync(request, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return EngineRelayOutcome.Denied(decision.Reason);
        }

        string? device;
        try
        {
            device = await _targets.FindTargetDeviceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EngineRelayOutcome.Unavailable("the relay target could not be determined");
        }

        if (string.IsNullOrEmpty(device))
        {
            return EngineRelayOutcome.Unavailable("no desktop device is connected");
        }

        var relayId = Guid.NewGuid();
        var pending = new Pending(device, request.GrantId ?? "", request.Tool);
        _pending[relayId] = pending;
        try
        {
            using var timeout = new CancellationTokenSource(_timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var args = request.Args.ValueKind == JsonValueKind.Undefined ? _emptyObject : request.Args;
            var call = new EngineRelayCall(pending.GrantId, request.StudioId, request.Tool, args);
            var evt = new EngineRelayRequestEvent(relayId, _time.GetUtcNow() + _timeout, call);
            try
            {
                await _publisher.PublishAsync(device, evt, linked.Token).ConfigureAwait(false);
                return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return EngineRelayOutcome.TimedOut();
            }
            catch (Exception)
            {
                return EngineRelayOutcome.Unavailable("the relay request could not be delivered");
            }
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<Guid, Pending>(relayId, pending));
        }
    }

    /// <summary>
    /// Correlates an answer. Accepted only from the device the request went to and only once; anything else is ignored and the
    /// wire handler answers <c>err</c>.
    /// </summary>
    public EngineRelaySubmitStatus SubmitResult(string deviceId, Guid relayId, EngineRelayCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!_pending.TryGetValue(relayId, out var pending))
        {
            return EngineRelaySubmitStatus.Unknown;
        }

        if (!string.Equals(pending.DeviceId, deviceId, StringComparison.Ordinal))
        {
            return EngineRelaySubmitStatus.ForeignDevice;
        }

        if (!string.Equals(pending.GrantId, result.GrantId, StringComparison.Ordinal))
        {
            return EngineRelaySubmitStatus.GrantMismatch;
        }

        // Removing is the one-shot gate: only the caller that removes the entry may complete it.
        if (!_pending.TryRemove(new KeyValuePair<Guid, Pending>(relayId, pending)))
        {
            return EngineRelaySubmitStatus.Unknown;
        }

        pending.Completion.TrySetResult(EngineRelayOutcome.Completed(result));
        return EngineRelaySubmitStatus.Accepted;
    }

    /// <summary>
    /// True while a relay is pending on <paramref name="deviceId"/> for exactly this grant and tool. The dispatcher authorised that call
    /// before it was published, so this is the server's answer to the client's <c>engine.grant.verify</c> (doc 02, doc 20): the grant
    /// covers the call only for as long as the call it was issued for is live, on the device it went to. Fails closed.
    /// </summary>
    public bool IsLiveCall(string deviceId, string grantId, string tool)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(grantId) || string.IsNullOrEmpty(tool))
        {
            return false;
        }

        foreach (var pending in _pending.Values)
        {
            if (string.Equals(pending.DeviceId, deviceId, StringComparison.Ordinal)
                && string.Equals(pending.GrantId, grantId, StringComparison.Ordinal)
                && string.Equals(pending.Tool, tool, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Fails every relay pending on a closed connection with <see cref="EngineRelayStatus.Unavailable"/>. Returns how many.</summary>
    public int DeviceClosed(string deviceId)
    {
        var failed = 0;
        foreach (var (relayId, pending) in _pending)
        {
            if (string.Equals(pending.DeviceId, deviceId, StringComparison.Ordinal)
                && _pending.TryRemove(new KeyValuePair<Guid, Pending>(relayId, pending))
                && pending.Completion.TrySetResult(EngineRelayOutcome.Unavailable("the device connection closed")))
            {
                failed++;
            }
        }

        return failed;
    }

    private sealed class Pending(string deviceId, string grantId, string tool)
    {
        public string DeviceId { get; } = deviceId;

        public string GrantId { get; } = grantId;

        public string Tool { get; } = tool;

        public TaskCompletionSource<EngineRelayOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
