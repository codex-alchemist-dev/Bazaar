using Bazaar.Contracts.Wire;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Presence;

/// <summary>
/// Answers <c>presence.snapshot</c> with every agent's row and its revision (docs/design/23). Agent presence is visible to people
/// only in v1 (doc 23, Client views, Q2), so an agent principal is refused with <c>not_found</c>, which does not reveal the request type exists.
/// </summary>
public sealed class PresenceSnapshotHandler(IPresencePublisher publisher) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.PresenceSnapshot;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.Connection.Principal.Kind, "human", StringComparison.Ordinal))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        var rows = await publisher.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return WireReply.Ok(context.Connection.Codec, new PresenceSnapshot([.. rows.Select(PresencePublisher.ToWire)]));
    }
}
