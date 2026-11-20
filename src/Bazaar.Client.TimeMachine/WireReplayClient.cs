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
/// Message text: the server sends each message's kind-specific payload as JSON and this module has no per-kind decoder yet, so
/// <see cref="ReplayMessageView.Text"/> is the payload's <c>text</c> string when it has one, otherwise the payload as-is.
/// Author labels are the author id, since the replay frame carries no names. The fork reply does not carry the clamped seq, so
/// <see cref="ReplayForkOutcome.SourceSeq"/> is the seq asked for.
/// </remarks>
public sealed class WireReplayClient(IWireRequester requester) : IReplayClient
{
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
                Part("Kanban", frame.Kanban, v => $"{v.Count} task(s)"),
                Part("Vault", frame.Vault, v => $"commit {v.Commit}"),
                Part("Souls", frame.Souls, v => $"{v.Count} agent(s)"),
                Part("Reputation", frame.Reputation, v => $"{v.Count} agent(s)"),
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
        new(m.MessageId, m.AuthorId.ToString("N")[..8], m.VersionSeq, m.Version, m.Edited, m.Quarantined, TextOf(m.PayloadJson));

    private static string? TextOf(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON: show it as it came.
        }

        return payloadJson;
    }

    private static ReplayPartView Part<T>(string name, ReplayPartWire<T> part, Func<T, string> summary) =>
        part.Available && part.Value is { } value
            ? ReplayPartView.Available(name, summary(value))
            : ReplayPartView.Unavailable(name, part.Reason ?? "unavailable");
}
