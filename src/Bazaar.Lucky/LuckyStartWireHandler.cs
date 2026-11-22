using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Lucky;

/// <summary>
/// <c>lucky.start</c> (docs/design/26-fun-systems.md, section 6): the owner starts a run from a client. Any other principal gets
/// <c>not_found</c>. The candidates come from the server; the rules and the side effects stay in <see cref="LuckyStartService"/>.
/// </summary>
public sealed class LuckyStartWireHandler(LuckyStartService service, TimeProvider time) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.LuckyStart;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        LuckyStartRequest request;
        try
        {
            request = context.Payload<LuckyStartRequest>();
        }
        catch (WireFormatException)
        {
            return LuckyWireErrors.BadSchema();
        }

        if (request.ProjectId == Guid.Empty)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "A Lucky start needs a project id.");
        }

        var principal = context.Connection.Principal;
        var outcome = await service.StartAsync(
            principal.Kind == "owner", principal.Id, request.ProjectId, request.Prompt, time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        return outcome.Status switch
        {
            LuckyStartStatus.Started => WireReply.Ok(
                context.Connection.Codec,
                new LuckyStartReply(outcome.RunId!, outcome.GcId!.Value, outcome.Participants, outcome.WakesInserted)),
            LuckyStartStatus.Refused => WireReply.Fail(WireErrorCodes.BadRequest, outcome.Refusal),
            _ => WireReply.Fail(WireErrorCodes.NotFound),
        };
    }
}
