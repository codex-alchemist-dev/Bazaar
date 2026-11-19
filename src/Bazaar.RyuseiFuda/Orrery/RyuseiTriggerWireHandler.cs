using Bazaar.Contracts.Ryusei;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>Which owner change a <see cref="RyuseiTriggerWireHandler"/> makes.</summary>
public enum RyuseiTriggerWireAction
{
    /// <summary>Pause an active trigger.</summary>
    Pause,

    /// <summary>Resume a paused trigger.</summary>
    Resume,
}

/// <summary>
/// <c>ryusei.trigger.pause</c> and <c>ryusei.trigger.resume</c> (doc 38, Layer 2): the owner changes one trigger from a client.
/// Only the owner principal may do so. Any other principal gets <c>not_found</c>, as the board does, so a trigger id does not leak.
/// The authority rules and the state change stay in <see cref="RyuseiTriggerService"/>; this handler only maps its result.
/// </summary>
public sealed class RyuseiTriggerWireHandler : IWireRequestHandler
{
    private readonly RyuseiTriggerService _triggers;
    private readonly TimeProvider _time;
    private readonly RyuseiTriggerWireAction _action;

    /// <param name="triggers">The trigger service that applies the change and its authority rules.</param>
    /// <param name="time">The clock stamped on the change.</param>
    /// <param name="action">Whether this instance serves pause or resume.</param>
    public RyuseiTriggerWireHandler(RyuseiTriggerService triggers, TimeProvider time, RyuseiTriggerWireAction action)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(time);
        _triggers = triggers;
        _time = time;
        _action = action;
        Type = action == RyuseiTriggerWireAction.Pause ? WireTypes.RyuseiPause : WireTypes.RyuseiResume;
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        RyuseiTriggerChangeRequest request;
        try
        {
            request = context.Payload<RyuseiTriggerChangeRequest>();
        }
        catch (WireFormatException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The trigger request does not fit its schema.");
        }

        if (request.TriggerId == Guid.Empty)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "A trigger request needs a trigger id.");
        }

        var principal = context.Connection.Principal;
        if (principal.Kind != "owner")
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        var actor = RyuseiActor.ForOwner(principal.Id);
        var now = _time.GetUtcNow();
        var result = _action == RyuseiTriggerWireAction.Pause
            ? await _triggers.PauseAsync(actor, request.TriggerId, now, cancellationToken).ConfigureAwait(false)
            : await _triggers.ResumeAsync(actor, request.TriggerId, now, cancellationToken).ConfigureAwait(false);

        switch (result.Status)
        {
            case RyuseiTriggerStatus.Ok:
                return WireReply.Ok(context.Connection.Codec, new RyuseiTriggerChangeReply(request.TriggerId, result.Version!.Value));
            case RyuseiTriggerStatus.NotFound:
                return WireReply.Fail(WireErrorCodes.NotFound);
            case RyuseiTriggerStatus.InvalidState:
                return WireReply.Fail(WireErrorCodes.BadRequest, result.Message);
            case RyuseiTriggerStatus.Refused:
                return WireReply.Fail(WireErrorCodes.BadRequest, result.Message);
            case RyuseiTriggerStatus.Conflict:
                return WireReply.Fail(WireErrorCodes.BadRequest, "The trigger changed first. Reload it and try again.");
            default:
                return WireReply.Fail(WireErrorCodes.Internal);
        }
    }
}
