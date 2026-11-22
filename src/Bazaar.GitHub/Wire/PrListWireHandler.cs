using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;
using Bazaar.GitHub.Adapter;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.GitHub.Wire;

/// <summary><c>pr.list</c> (doc 21, Client PR wire): one page of PRs from the read model. Owner only.</summary>
public sealed class PrListWireHandler(IGitHubPrReadModel reads, TimeProvider? time = null) : IWireRequestHandler
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public string Type => WireTypes.PrList;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PrWireSupport.IsOwner(context))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        if (!PrWireSupport.TryDecode<PrListRequest>(context, out var request, out var failure))
        {
            return failure!;
        }

        try
        {
            var reply = await reads.ListAsync(request, cancellationToken).ConfigureAwait(false);
            return WireReply.Ok(context.Connection.Codec, reply);
        }
        catch (GitHubApiException error)
        {
            return PrWireSupport.MapGitHubFailure(error, _time);
        }
    }
}
