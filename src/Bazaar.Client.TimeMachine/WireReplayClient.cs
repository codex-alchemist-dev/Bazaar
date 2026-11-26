using System.Text.Json;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;
using WireForkRequest = Bazaar.Contracts.TimeMachine.ReplayForkRequest;

namespace Bazaar.Client.TimeMachine;

/// <summary>
/// <see cref="IReplayClient"/> over the socket client's request call (doc 02, replay and fork requests): <c>replay.frame</c> and
/// <c>replay.fork</c>. Each call is one request and one reply; there is no stream. A server error becomes a
/// <see cref="ReplayClientException"/> with a message that is safe to show.
/// </summary>
/// <remarks>
/// Message text: the wire carries no event kind, so every row is decoded as a message by <see cref="ReplayMessageText"/>.
/// Author labels are the author id, since the replay frame carries no names. The fork reply does not carry the clamped seq, so
/// <see cref="ReplayForkOutcome.SourceSeq"/> is the seq asked for.
/// </remarks>
public sealed class WireReplayClient(IWireRequester requester) : IReplayClient, IReplayPointClient
{
    /// <inheritdoc />
    public async Task<ReplayPointsPage> GetPointsAsync(Guid gcId, long afterSeq, int limit, CancellationToken ct)
    {
        var result = await SendAsync<ReplayPointsRequest, ReplayPointsResult>(
            WireTypes.ReplayPoints, new ReplayPointsRequest(gcId, afterSeq, limit), ct).ConfigureAwait(false);
        return new ReplayPointsPage([.. result.Points.Select(p => new ReplayPointView(p.Seq, p.At, p.Kind, p.Label))], result.Next);
    }

    /// <inheritdoc />
    public async Task<ReplayFrameView> GetFrameAsync(Guid gcId, long seq, CancellationToken ct)
    {
        var result = await SendAsync<ReplayFrameRequest, ReplayFrameResult>(WireTypes.ReplayFrame, new ReplayFrameRequest(gcId, seq), ct)
            .ConfigureAwait(false);
        var frame = result.Frame;
        return new ReplayFrameView(
            result.Gc,
            result.Seq,
            result.Head,
            Clamped: result.Seq < seq,
            [.. frame.Messages.Select(ToView)],
            [
                Part("Kanban", frame.Kanban.Available, frame.Kanban.Value, frame.Kanban.Reason, v => $"{v.Count} task(s)"),
                Part("Vault", frame.Vault.Available, frame.Vault.Value, frame.Vault.Reason, v => $"commit {v.Commit}"),
                Part("Souls", frame.Souls.Available, frame.Souls.Value, frame.Souls.Reason, v => $"{v.Count} agent(s)"),
                Part("Reputation", frame.Reputation.Available, frame.Reputation.Value, frame.Reputation.Reason, v => $"{v.Count} agent(s)"),
            ]);
    }

    /// <inheritdoc />
    public async Task<ReplayForkOutcome> ForkAsync(ReplayForkRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await SendAsync<WireForkRequest, ReplayForkResult>(
            WireTypes.ReplayFork, new WireForkRequest(request.SourceGcId, request.SourceSeq, request.RequestId), ct).ConfigureAwait(false);
        return new ReplayForkOutcome(result.NewGc, request.SourceSeq, result.Created);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken ct)
    {
        try
        {
            return await requester.RequestAsync<TRequest, TResponse>(type, payload, ct).ConfigureAwait(false);
        }
        catch (WireRequestException e)
        {
            throw new ReplayClientException(e.Error.Code switch
            {
                WireErrorCodes.NotFound => "That group chat is not available to you.",
                WireErrorCodes.RateLimited => "Too many replay requests. Try again shortly.",
                WireErrorCodes.Unsupported => "This server does not support replay.",
                WireErrorCodes.BadRequest => "The replay request was not valid.",
                _ => "The server could not serve the replay request.",
            });
        }
    }

    private static ReplayMessageView ToView(ReplayMessageWire m) =>
        new(m.MessageId, m.AuthorId.ToString("N")[..8], m.VersionSeq, m.Version, m.Edited, m.Quarantined, ReplayMessageText.Decode(kind: null, m.PayloadJson));

    private static ReplayPartView Part<T>(string name, bool available, T? value, string? reason, Func<T, string> summary) =>
        available && value is { } present
            ? ReplayPartView.Available(name, summary(present))
            : ReplayPartView.Unavailable(name, reason ?? "unavailable");
}
