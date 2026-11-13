using System.Net;

namespace Bazaar.Conduit.PeerToPeer;

/// <summary>A pair of endpoints that the two peers can try: our reflexive address and the peer's candidate.</summary>
/// <param name="Local">Our server-reflexive endpoint.</param>
/// <param name="Remote">The endpoint the peer announced.</param>
public sealed record CandidatePair(IPEndPoint Local, IPEndPoint Remote);

/// <summary>Outcome of a candidate exchange. <see cref="Pair"/> is null exactly when <see cref="Reason"/> is set.</summary>
/// <param name="Pair">The endpoints, on success.</param>
/// <param name="Reason">A machine-readable failure reason, on failure.</param>
public sealed record SignalingResult(CandidatePair? Pair, string? Reason);

/// <summary>
/// Exchanges candidates with a peer over a rendezvous channel. It sends our candidate, then waits for the peer's, with
/// a deadline read from the injected clock. It does not open a data path; the QUIC stage consumes the result.
/// </summary>
public sealed class SignalingSession
{
    /// <summary>Reason when the peer left, or the channel closed, before announcing a candidate.</summary>
    public const string NoPeerCandidateReason = "no-peer-candidate";

    /// <summary>Reason when the peer did not announce a candidate before the deadline.</summary>
    public const string TimeoutReason = "timeout";

    private readonly IRendezvousChannel _channel;
    private readonly string _sessionId;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a session for one rendezvous.</summary>
    /// <param name="channel">The injected channel to the peer.</param>
    /// <param name="sessionId">The rendezvous id both peers use.</param>
    /// <param name="time">Clock for the deadline. Tests pass a fake clock.</param>
    /// <param name="timeout">How long to wait for the peer's candidate.</param>
    public SignalingSession(IRendezvousChannel channel, string sessionId, TimeProvider time, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        _channel = channel;
        _sessionId = sessionId;
        _time = time;
        _timeout = timeout;
    }

    /// <summary>Sends <paramref name="local"/> as our candidate and waits for the peer's candidate.</summary>
    /// <param name="local">Our server-reflexive endpoint, from <c>StunGatherer</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange. Cancellation throws; it is not a failure reason.</param>
    public async Task<SignalingResult> ExchangeCandidatesAsync(IPEndPoint local, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(local);

        await _channel.SendAsync(new SignalMessage(SignalKinds.Candidate, _sessionId, local.ToString()), cancellationToken)
            .ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceivePeerCandidateAsync(local, cts.Token);
        var deadline = Task.Delay(_timeout, _time, cts.Token);

        var first = await Task.WhenAny(receive, deadline).ConfigureAwait(false);
        if (first == receive)
        {
            cts.Cancel();
            return await receive.ConfigureAwait(false);
        }

        cts.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await receive.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the receive loop was stopped by the deadline.
        }

        return new SignalingResult(null, TimeoutReason);
    }

    private async Task<SignalingResult> ReceivePeerCandidateAsync(IPEndPoint local, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return new SignalingResult(null, NoPeerCandidateReason);
            }

            if (!string.Equals(message.SessionId, _sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (message.Kind == SignalKinds.End)
            {
                return new SignalingResult(null, NoPeerCandidateReason);
            }

            if (message.Kind == SignalKinds.Candidate && IPEndPoint.TryParse(message.Payload, out var remote))
            {
                return new SignalingResult(new CandidatePair(local, remote), null);
            }
        }
    }
}
