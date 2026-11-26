using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Sanitization;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Lucky;

/// <summary>Maps wire requests for Lucky onto the services. The rules stay in the services; these handlers only map.</summary>
public static class LuckyWireErrors
{
    /// <summary>The reply for a request that does not fit its schema.</summary>
    public static WireReply BadSchema() => WireReply.Fail(WireErrorCodes.BadRequest, "The Lucky request does not fit its schema.");
}

/// <summary><c>lucky.idea</c>: a participating agent records an idea. Any other principal gets <c>not_found</c>.</summary>
public sealed class LuckyIdeaWireHandler(LuckyIdeaService ideas, TimeProvider time) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.LuckyIdea;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        LuckyIdeaRequest request;
        try
        {
            request = context.Payload<LuckyIdeaRequest>();
        }
        catch (WireFormatException)
        {
            return LuckyWireErrors.BadSchema();
        }

        var principal = context.Connection.Principal;
        if (principal.Kind != "agent")
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        var outcome = await ideas.RecordIdeaAsync(
            principal.Id, request.RunId, request.Text, time.GetUtcNow(), new SensitiveTerms([], [], [], [], []), cancellationToken)
            .ConfigureAwait(false);

        return outcome.Status switch
        {
            LuckyActStatus.Done => WireReply.Ok(context.Connection.Codec, new LuckyIdeaReply(request.RunId, outcome.IdeaIndex, outcome.Quarantined)),
            LuckyActStatus.Refused => WireReply.Fail(WireErrorCodes.BadRequest, outcome.Reason),
            _ => WireReply.Fail(WireErrorCodes.NotFound),
        };
    }
}

/// <summary><c>lucky.mark</c>: the owner marks an idea for a Backlog card. Any other principal gets <c>not_found</c>.</summary>
public sealed class LuckyMarkWireHandler(LuckyIdeaService ideas) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.LuckyMark;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        LuckyMarkRequest request;
        try
        {
            request = context.Payload<LuckyMarkRequest>();
        }
        catch (WireFormatException)
        {
            return LuckyWireErrors.BadSchema();
        }

        var isOwner = context.Connection.Principal.Kind == "owner";
        var outcome = await ideas.MarkIdeaAsync(isOwner, request.RunId, request.IdeaIndex, cancellationToken).ConfigureAwait(false);

        return outcome.Status switch
        {
            LuckyActStatus.Done => WireReply.Ok(context.Connection.Codec, new LuckyMarkReply(request.RunId, outcome.IdeaIndex, outcome.CardId!.Value)),
            LuckyActStatus.Refused => WireReply.Fail(WireErrorCodes.BadRequest, outcome.Reason),
            _ => WireReply.Fail(WireErrorCodes.NotFound),
        };
    }
}

/// <summary><c>lucky.opt_in</c>: the owner sets an agent's Lucky opt-in. Any other principal gets <c>not_found</c>.</summary>
public sealed class LuckyOptInWireHandler(ILuckyOptInStore optIns) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.LuckyOptIn;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        LuckyOptInRequest request;
        try
        {
            request = context.Payload<LuckyOptInRequest>();
        }
        catch (WireFormatException)
        {
            return LuckyWireErrors.BadSchema();
        }

        var principal = context.Connection.Principal;
        if (principal.Kind != "owner")
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        if (request.AgentId == Guid.Empty)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "An opt-in needs an agent id.");
        }

        await optIns.SetAsync(request.AgentId, request.OptedIn, principal.Id, cancellationToken).ConfigureAwait(false);
        return WireReply.Ok(context.Connection.Codec, new LuckyOptInReply(request.AgentId, request.OptedIn));
    }
}
