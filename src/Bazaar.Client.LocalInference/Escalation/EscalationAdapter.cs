using System.Collections.Concurrent;

namespace Bazaar.Client.LocalInference.Escalation;

/// <summary>
/// Sends a local task's request to the server through the gateway (doc 31, Design: escalation adapter), within the mode
/// set by the operator. In <c>Explicit</c> mode, <see cref="RequestAsync"/> only returns a pending decision, and nothing
/// is sent until <see cref="ConfirmAsync"/> is called with that decision's token. <c>Off</c> refuses every request.
/// Input is never logged.
/// </summary>
public sealed class EscalationAdapter
{
    private readonly IEscalationTransport _transport;
    private readonly EscalationMode _mode;
    private readonly ConcurrentDictionary<string, EscalationRequest> _pending = new(StringComparer.Ordinal);

    public EscalationAdapter(IEscalationTransport transport, EscalationOptions options)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "Unknown escalation mode.");
        }

        _transport = transport;
        _mode = options.Mode;
    }

    /// <summary>
    /// Off: refused. Explicit: returns a pending decision and keeps the request until confirmed. Automatic: sends now.
    /// </summary>
    public async Task<EscalationOutcome> RequestAsync(EscalationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (_mode)
        {
            case EscalationMode.Off:
                return new EscalationRefused(EscalationRefusalReason.Disabled);
            case EscalationMode.Explicit:
                var token = EscalationConfirmationToken.New();
                _pending[token.Value] = request;
                return new EscalationPending(new EscalationDecision(request, token, ContentLeavesDevice: true));
            default:
                return await SendAsync(request, ct);
        }
    }

    /// <summary>Sends the request behind a pending decision. Each token works once; the request sent is the one shown to the user.</summary>
    public async Task<EscalationOutcome> ConfirmAsync(EscalationConfirmationToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (_mode == EscalationMode.Off || !_pending.TryRemove(token.Value, out var request))
        {
            return new EscalationRefused(_mode == EscalationMode.Off
                ? EscalationRefusalReason.Disabled
                : EscalationRefusalReason.UnknownConfirmation);
        }

        return await SendAsync(request, ct);
    }

    private async Task<EscalationOutcome> SendAsync(EscalationRequest request, CancellationToken ct)
    {
        EscalationReply reply;
        try
        {
            reply = await _transport.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type name is kept: an exception message can echo the input.
            return new EscalationFailed(EscalationFailureKind.TransportThrew, ex.GetType().Name, ContentLeavesDevice: true);
        }

        if (reply.Error is { } error)
        {
            return new EscalationFailed(EscalationFailureKind.TransportReturnedError, error, ContentLeavesDevice: true);
        }

        return new EscalationSent(reply, ContentLeavesDevice: true);
    }
}
