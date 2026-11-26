using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.GitHub.Wire;

/// <summary><c>pr.summary</c> (doc 21, Client PR wire): the PR at its current head. Owner only; an unknown PR is <c>not_found</c>.</summary>
public sealed class PrSummaryWireHandler(IGitHubPrReadModel reads, TimeProvider? time = null) : IWireRequestHandler
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public string Type => WireTypes.PrSummary;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PrWireSupport.IsOwner(context))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        if (!PrWireSupport.TryDecode<PrSummaryRequest>(context, out var request, out var failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(request.PrId))
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "A summary request needs a PR id.");
        }

        try
        {
            var pr = await reads.ResolveAsync(request.PrId, cancellationToken).ConfigureAwait(false);
            var summary = pr is null ? null : await reads.GetSummaryAsync(request.PrId, pr, cancellationToken).ConfigureAwait(false);
            return summary is null
                ? WireReply.Fail(WireErrorCodes.NotFound)
                : WireReply.Ok(context.Connection.Codec, summary);
        }
        catch (GitHubApiException error)
        {
            return PrWireSupport.MapGitHubFailure(error, _time);
        }
    }
}
