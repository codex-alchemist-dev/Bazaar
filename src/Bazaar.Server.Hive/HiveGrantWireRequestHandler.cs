using Bazaar.Contracts.Hive;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Hive;

/// <summary>
/// Host adapter for <c>hive.grant.set</c> and <c>hive.grant.revoke</c> (doc 25, "Hive grant wire request"). One instance serves one
/// wire type: it decodes the payload, builds the <see cref="HiveCaller"/> from the connection (the owner is the principal of kind
/// <c>owner</c>), runs <see cref="IHiveGrantWireHandler"/> and encodes its <see cref="HiveGrantChangeReply"/>. Every outcome of a
/// request, refusals included, is a reply; only a payload that does not fit its schema is a wire error.
/// </summary>
public sealed class HiveGrantWireRequestHandler : IWireRequestHandler
{
    private readonly IHiveGrantWireHandler _handler;

    /// <summary>Creates the adapter for <paramref name="type"/>, which must be one of the two Hive grant wire types.</summary>
    public HiveGrantWireRequestHandler(string type, IHiveGrantWireHandler handler)
    {
        if (type is not (WireTypes.HiveGrantSet or WireTypes.HiveGrantRevoke))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Not a Hive grant wire type.");
        }

        Type = type;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        HiveGrantChangeRequest request;
        try
        {
            request = context.Payload<HiveGrantChangeRequest>();
        }
        catch (WireFormatException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The grant request does not fit its schema.");
        }

        var principal = context.Connection.Principal;
        var caller = new HiveCaller(principal.Id.ToString("D"), string.Equals(principal.Kind, "owner", StringComparison.Ordinal));
        var reply = await _handler.HandleAsync(Type, request, caller, cancellationToken).ConfigureAwait(false);
        return WireReply.Ok(context.Connection.Codec, reply);
    }
}
