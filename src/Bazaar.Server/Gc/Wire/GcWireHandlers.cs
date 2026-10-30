using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Gc.Wire;

/// <summary>Maps the wire's principal kind (<c>owner</c>, <c>human</c>, <c>agent</c>) onto the GC model.</summary>
internal static class GcWirePrincipals
{
    public static PrincipalKind KindOf(PrincipalRef principal) => principal.Kind switch
    {
        "owner" => PrincipalKind.Owner,
        "human" => PrincipalKind.Human,
        "agent" => PrincipalKind.Agent,
        _ => throw new InvalidOperationException($"Unknown principal kind '{principal.Kind}'."),
    };

    public static GcPrincipal PrincipalOf(PrincipalRef principal) =>
        new(principal.Id, KindOf(principal), string.Empty);
}

/// <summary>
/// <c>subscribe</c>: opens the requested streams on this connection's subscription session. The per-stream results go back
/// in the response; the replay starts after it is queued, so the client sees the answer before the first event.
/// </summary>
public sealed class SubscribeHandler : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.Subscribe;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var session = context.Connection.Subscriptions;
        if (session is null)
            return WireReply.Fail(WireErrorCodes.Unsupported, "This server has no subscription engine.");

        var request = context.Payload<Subscribe>();
        var outcome = await session.SubscribeAsync(request, cancellationToken).ConfigureAwait(false);
        return WireReply.OkThen(context.Connection.Codec, outcome.Result, outcome.StartAsync);
    }
}

/// <summary>
/// <c>gc.send</c>: appends a message to a GC. The envelope id is the idempotency key (the request layer answers a repeat with the
/// original). Denied and missing GCs get the same answer. The commit notifies listeners, so subscribers receive the message
/// through the dispatcher; this handler does not fan out.
/// </summary>
public sealed class GcSendHandler(IGcLogWriter writer, IGcBodyCodec bodies, GcReadAccess access) : IWireRequestHandler
{
    /// <summary>Longest accepted message text, in characters. Replaced by the <c>gc.message_max_bytes</c> setting when it is wired.</summary>
    public const int MaxTextLength = 64 * 1024;

    /// <inheritdoc />
    public string Type => WireTypes.GcSend;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<GcSend>();
        var principal = context.Connection.Principal;
        var clientMsgId = context.Request.Id;

        if (string.IsNullOrWhiteSpace(request.Text))
            return WireReply.Fail(WireErrorCodes.BadRequest, "Message text is empty.");
        if (request.Text.Length > MaxTextLength)
            return WireReply.Fail(WireErrorCodes.BadRequest, "Message text is too long.");
        if (string.IsNullOrEmpty(clientMsgId))
            return WireReply.Fail(WireErrorCodes.BadRequest, "A send needs a request id.");

        var viewer = GcWirePrincipals.PrincipalOf(principal);
        if (!await access.CanWriteAsync(request.GcId, viewer, cancellationToken).ConfigureAwait(false))
            return WireReply.Fail(WireErrorCodes.NotFound);

        var mentions = (request.Mentions ?? []).Distinct().ToList();
        if (mentions.Count > 0)
        {
            var active = await access.LoadActiveMemberIdsAsync(request.GcId, cancellationToken).ConfigureAwait(false);
            if (mentions.Any(m => !active.Contains(m)))
                return WireReply.Fail(WireErrorCodes.BadRequest, "A mention names someone who is not an active member.");
        }

        var kind = viewer.Kind;
        var append = new AppendRequest(
            request.GcId,
            GcEventKinds.Message,
            kind switch
            {
                PrincipalKind.Owner => AuthorKind.Owner,
                PrincipalKind.Agent => AuthorKind.Agent,
                _ => AuthorKind.Human,
            },
            principal.Id,
            kind switch
            {
                // The server sets provenance from the authenticated session; the client never does (doc 15).
                PrincipalKind.Owner => SourceTrust.Owner,
                PrincipalKind.Agent => SourceTrust.Agent,
                _ => SourceTrust.MemberHuman,
            },
            clientMsgId)
        {
            Message = bodies.Seal(request.Text),
            Mentions = mentions,
            ReplyToSeq = request.ReplyToN,
        };

        try
        {
            var result = await writer.AppendAsync(append, cancellationToken).ConfigureAwait(false);
            // A message's first version: n and ev are the same seq (doc 02, sequence numbers).
            return WireReply.Ok(context.Connection.Codec, new GcSendResult(result.Seq, result.Seq));
        }
        catch (GcNotFoundException)
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }
        catch (GcArchivedException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The GC is archived.");
        }
    }
}

/// <summary><c>gc.history</c>: older message entries, for paging back past a hole or a <c>resync</c>.</summary>
public sealed class GcHistoryHandler(PgGcStreamSource source, GcReadAccess access) : IWireRequestHandler
{
    /// <summary>Largest page a client may ask for.</summary>
    public const int MaxPage = 200;

    /// <inheritdoc />
    public string Type => WireTypes.GcHistory;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<GcHistory>();
        if (request.Limit < 1 || request.Limit > MaxPage)
            return WireReply.Fail(WireErrorCodes.BadRequest, $"Limit must be between 1 and {MaxPage}.");

        // Only a full grant reads history. A limited (former member) grant is denied, the same as a missing GC.
        var grant = await access.GetGrantAsync(request.GcId, GcWirePrincipals.PrincipalOf(context.Connection.Principal), cancellationToken)
            .ConfigureAwait(false);
        if (!grant.Allowed || grant.ThroughSeq is not null)
            return WireReply.Fail(WireErrorCodes.NotFound);

        var page = await source.ReadHistoryAsync(request.GcId, request.Before, request.Limit, cancellationToken).ConfigureAwait(false);
        return WireReply.Ok(context.Connection.Codec, page);
    }
}
