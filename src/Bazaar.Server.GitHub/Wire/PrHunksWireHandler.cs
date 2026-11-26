using System.Globalization;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.GitHub.Wire;

/// <summary>
/// <c>pr.hunks</c> (doc 21, Client PR wire): one file's hunks, keyed by head sha. A stale head sha is answered <c>stale</c> with the
/// current sha and no hunks; old hunks are never returned. Owner only.
/// </summary>
public sealed class PrHunksWireHandler(IGitHubAdapter adapter, IGitHubPrReadModel reads, TimeProvider? time = null) : IWireRequestHandler
{
    /// <summary>Hunks per reply page.</summary>
    public const int PageSize = 50;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public string Type => WireTypes.PrHunks;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PrWireSupport.IsOwner(context))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        if (!PrWireSupport.TryDecode<PrHunksRequest>(context, out var request, out var failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(request.PrId) || string.IsNullOrWhiteSpace(request.HeadSha) || string.IsNullOrEmpty(request.Path))
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "A hunks request needs a PR id, a head sha and a path.");
        }

        var offset = 0;
        if (request.Cursor is not null
            && !int.TryParse(request.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The cursor is not valid.");
        }

        try
        {
            var pr = await reads.ResolveAsync(request.PrId, cancellationToken).ConfigureAwait(false);
            if (pr is null)
            {
                return WireReply.Fail(WireErrorCodes.NotFound);
            }

            // Read the head first so a stale sha never reaches the files call. The adapter re-checks it too (GitHubStaleHeadException).
            var current = await adapter.GetHeadShaAsync(pr, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, request.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                return Stale(context, current, request.Path);
            }

            IReadOnlyList<PrFile> files;
            try
            {
                files = await adapter.GetPrFilesAsync(pr, request.HeadSha, cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubStaleHeadException moved)
            {
                return Stale(context, moved.ActualHeadSha, request.Path);
            }

            var file = files.FirstOrDefault(f => string.Equals(f.Path, request.Path, StringComparison.Ordinal));
            if (file is null)
            {
                return WireReply.Fail(WireErrorCodes.NotFound);
            }

            var all = PrPatchParser.Parse(file.Patch);
            var page = all.Skip(offset).Take(PageSize).ToArray();
            var next = offset + page.Length < all.Count
                ? (offset + page.Length).ToString(CultureInfo.InvariantCulture)
                : null;
            return WireReply.Ok(
                context.Connection.Codec,
                new PrHunksReply(PrHunksStatus.Ok, request.HeadSha, request.Path, page, next));
        }
        catch (GitHubApiException error)
        {
            return PrWireSupport.MapGitHubFailure(error, _time);
        }
    }

    private static WireReply Stale(WireRequestContext context, string currentSha, string path) =>
        WireReply.Ok(context.Connection.Codec, new PrHunksReply(PrHunksStatus.Stale, currentSha, path, [], null));
}
