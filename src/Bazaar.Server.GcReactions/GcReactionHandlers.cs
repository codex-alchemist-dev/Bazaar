using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.GcReactions.Abstractions;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.GcReactions;

/// <summary>Maps the authenticated wire principal onto what the GC log records, the same way posting does.</summary>
internal static class ReactionPrincipals
{
    public static GcPrincipal GcPrincipalOf(PrincipalRef principal) =>
        new(principal.Id, principal.GcKind(), string.Empty);

    public static AuthorKind AuthorKindOf(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Owner => AuthorKind.Owner,
        PrincipalKind.Agent => AuthorKind.Agent,
        _ => AuthorKind.Human,
    };

    public static SourceTrust TrustOf(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Owner => SourceTrust.Owner,
        PrincipalKind.Agent => SourceTrust.Agent,
        _ => SourceTrust.MemberHuman,
    };
}

/// <summary>
/// <c>gc.react</c> (docs/design/02 "Reactions on the wire", docs/design/01 "Reactions"). Only a member who may write in the GC can
/// react; the check, the emoji rules, the limits and the target check run before anything is written, and a request that would
/// change nothing writes nothing. A change is one <c>reaction</c> event appended through the GC log writer, then pushed live.
/// </summary>
public sealed class GcReactHandler(
    IGcLogWriter writer,
    IGcWriteAccess access,
    IGcReactionStore store,
    GcLiveBroadcaster broadcaster,
    SlidingRateLimiter limiter,
    GcReactionsOptions options) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.GcReact;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var limits = options;
        var request = context.Payload<GcReact>();
        var principal = context.Connection.Principal;
        var clientMsgId = context.Request.Id;
        if (string.IsNullOrEmpty(clientMsgId))
            return WireReply.Fail(WireErrorCodes.BadRequest, "A reaction needs a request id.");
        if (clientMsgId.Length > AppendRequest.MaxClientMsgIdLength)
            return WireReply.Fail(WireErrorCodes.BadRequest, "The request id is too long.");

        // Access first, so a non-member learns nothing about emoji rules, limits or targets: denied and missing look the same.
        var who = ReactionPrincipals.GcPrincipalOf(principal);
        if (!await access.CanWriteAsync(request.GcId, who, cancellationToken).ConfigureAwait(false))
            return WireReply.Fail(WireErrorCodes.NotFound);
        if (!limits.ReactionsEnabled)
            return WireReply.Fail(WireErrorCodes.BadRequest, "Reactions are turned off.");
        if (EmojiRules.Refusal(request.Emoji, limits.EmojiMaxBytes) is { } refusal)
            return WireReply.Fail(WireErrorCodes.BadRequest, refusal);
        if (request.TargetN < 1)
            return WireReply.Fail(WireErrorCodes.NotFound);

        if (!limiter.TryAcquire(request.GcId, principal.Id, limits.RatePerMinute, out var wait))
            return WireReply.Fail(WireErrorCodes.RateLimited, "Too many reactions.", (int)Math.Ceiling(wait.TotalMilliseconds));

        if (!await store.MessageExistsAsync(request.GcId, request.TargetN, cancellationToken).ConfigureAwait(false))
            return WireReply.Fail(WireErrorCodes.NotFound);

        var rows = await store.ReadAsync(request.GcId, request.TargetN, null, cancellationToken).ConfigureAwait(false);
        var inEffect = ReactionFold.InEffectSeq(rows, principal.Id, request.TargetN, request.Emoji);
        if (request.Add)
        {
            if (inEffect is { } already)
                return WireReply.Ok(context.Connection.Codec, new GcReactResult(already, false));
            if (OverLimit(rows, principal.Id, request.Emoji, limits) is { } over)
                return WireReply.Fail(WireErrorCodes.BadRequest, over);
        }
        else if (inEffect is null)
        {
            return WireReply.Ok(context.Connection.Codec, new GcReactResult(0, false));
        }

        var append = new AppendRequest(
            request.GcId,
            GcEventKinds.Reaction,
            ReactionPrincipals.AuthorKindOf(who.Kind),
            principal.Id,
            ReactionPrincipals.TrustOf(who.Kind),
            clientMsgId)
        {
            TargetSeq = request.TargetN,
            PayloadJson = ReactionPayload.Write(request.Emoji, request.Add),
        };

        try
        {
            var result = await writer.AppendAsync(append, cancellationToken).ConfigureAwait(false);
            if (!result.Duplicate)
            {
                broadcaster.Publish(
                    request.GcId,
                    WireTypes.GcReaction,
                    WireStreams.Gc(request.GcId),
                    result.Seq,
                    new WireGcReaction(request.GcId, request.TargetN, request.Emoji, principal.Id, request.Add, result.CreatedAt));
            }

            return WireReply.Ok(context.Connection.Codec, new GcReactResult(result.Seq, true));
        }
        catch (Bazaar.Server.Gc.GcNotFoundException)
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }
        catch (Bazaar.Server.Gc.GcArchivedException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The GC is archived.");
        }
    }

    private static string? OverLimit(IReadOnlyList<ReactionEventRow> rows, Guid person, string emoji, GcReactionsOptions limits)
    {
        var groups = ReactionFold.Fold(rows);
        if (groups.Count(g => g.By.Contains(person)) >= limits.MaxPerPersonPerMessage)
            return "That is as many reactions as one person may leave on a message.";
        if (groups.All(g => g.Emoji != emoji) && groups.Count >= limits.MaxDistinctPerMessage)
            return "That message already has as many different reactions as it may have.";
        return null;
    }
}

/// <summary><c>gc.reactions</c>: the folded reaction state of a GC, for a client that opens a GC or reconnects.</summary>
public sealed class GcReactionsHandler(IGcReadAccess access, IGcReactionStore store) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.GcReactions;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<GcReactionsRequest>();
        var grant = await access.GetGrantAsync(
            request.GcId, ReactionPrincipals.GcPrincipalOf(context.Connection.Principal), cancellationToken).ConfigureAwait(false);
        if (!grant.Allowed)
            return WireReply.Fail(WireErrorCodes.NotFound);

        var rows = await store.ReadAsync(request.GcId, null, request.AfterN, cancellationToken).ConfigureAwait(false);

        // A member who left keeps what they saw, not later events (docs/design/01): reactions after their last seq stay hidden.
        if (grant.ThroughSeq is { } through)
            rows = [.. rows.Where(r => r.Seq <= through)];
        return WireReply.Ok(context.Connection.Codec, new GcReactionsReply(ReactionFold.Fold(rows)));
    }
}

/// <summary>
/// <c>gc.typing</c> (docs/design/02 "Typing signal"). Same write check as posting; the signal is held only in memory, rate-limited,
/// and pushed as a <c>presence.update</c> to the other subscribers of the GC. Nothing is written to the log.
/// </summary>
public sealed class GcTypingHandler(
    IGcWriteAccess access,
    TypingTracker tracker,
    GcLiveBroadcaster broadcaster,
    GcReactionsOptions options) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.GcTyping;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var limits = options;
        var request = context.Payload<GcTyping>();
        var principal = context.Connection.Principal;
        if (!await access.CanWriteAsync(request.GcId, ReactionPrincipals.GcPrincipalOf(principal), cancellationToken).ConfigureAwait(false))
            return WireReply.Fail(WireErrorCodes.NotFound);
        if (!limits.TypingEnabled)
            return WireReply.Ok(context.Connection.Codec, new GcTypingResult(false));

        var ttl = TimeSpan.FromSeconds(limits.TypingTtlS);
        if (!tracker.TryAccept(request.GcId, principal.Id, request.Typing, TimeSpan.FromSeconds(limits.TypingMinIntervalS), ttl))
            return WireReply.Ok(context.Connection.Codec, new GcTypingResult(false));

        broadcaster.Publish(
            request.GcId,
            WireTypes.PresenceUpdate,
            WireStreams.Presence,
            null,
            new PresenceTyping(PresenceKinds.Typing, request.GcId, principal.Id, request.Typing, (int)ttl.TotalMilliseconds),
            exceptPrincipal: principal.Id);
        return WireReply.Ok(context.Connection.Codec, new GcTypingResult(true));
    }
}
