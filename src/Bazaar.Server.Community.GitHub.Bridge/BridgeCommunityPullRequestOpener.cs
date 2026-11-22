using System.Globalization;
using Bazaar.GitHub.Adapter;

namespace Bazaar.Server.Community.GitHub.Bridge;

/// <summary>
/// Binds <see cref="ICommunityPullRequestOpener"/> to <see cref="IGitHubAdapter.OpenDraftPrAsync"/>. The adapter searches by head branch before
/// creating but its result does not say which path it took, so <c>Reused</c> is always false here (the draft marker in the body keeps a retry from duplicating).
/// </summary>
public sealed class BridgeCommunityPullRequestOpener(IGitHubAdapter adapter, ICommunityInstallationResolver installations) : ICommunityPullRequestOpener
{
    public async Task<PostReceipt> OpenDraftAsync(CommunityPullRequestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var installation = await installations.ResolveAsync(request.Owner, request.Repo, cancellationToken).ConfigureAwait(false)
                ?? throw BridgeFailure.Refusal("The GitHub App is not installed on that repository.");
            var pr = await adapter.OpenDraftPrAsync(
                new OpenPrRequest(installation.InstallationId, request.Owner, request.Repo, request.HeadBranch, request.BaseBranch, request.Title, request.Body),
                cancellationToken).ConfigureAwait(false);
            var url = new Uri(
                $"https://github.com/{Uri.EscapeDataString(pr.Pr.Owner)}/{Uri.EscapeDataString(pr.Pr.Repo)}/pull/{pr.Pr.Number.ToString(CultureInfo.InvariantCulture)}");
            return new PostReceipt(pr.NodeId, url, Reused: false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CommunityPostException))
        {
            throw BridgeFailure.From(ex, "Opening the draft pull request");
        }
    }
}
