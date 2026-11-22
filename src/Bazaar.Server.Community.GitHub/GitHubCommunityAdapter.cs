using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Community.GitHub;

/// <summary>
/// GitHub adapter for community posts (docs/design/36, work package 7). Reachable only through the dispatcher, which has already
/// verified the approval; this class still refuses anything that no longer matches what was approved. Posts issues, draft pull
/// requests and comments under the GitHub App identity. It never adds a <c>Signed-off-by</c> line: any trailers (such as
/// <c>Assisted-by</c>) arrive in the caller's body.
/// </summary>
public sealed class GitHubCommunityAdapter(
    ICommunityGitHubApi api, ICommunityPullRequestOpener prOpener, ICommunityFooterCheck footerCheck) : ICommunityAdapter
{
    public const string PlatformKey = "github";
    public const string KindIssue = "issue";
    public const string KindPullRequest = "pr";
    public const string KindComment = "comment";

    public string Platform => PlatformKey;

    /// <summary>The hidden idempotency marker appended to every body.</summary>
    public static string MarkerFor(Guid draftId) => $"<!-- bazaar-draft:{draftId:D} -->";

    public async Task<PostReceipt> PostAsync(OutboundPost post, ApprovalId approval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        Validate(post, approval);

        var marker = MarkerFor(post.DraftId);
        var (owner, repo, issueNumber) = ParseTarget(post);

        var body = post.Body + "\n\n" + marker;

        // Draft PRs are Bazaar.GitHub's job (it searches by head branch first, so it is idempotent itself).
        if (post.Kind == KindPullRequest)
        {
            return await prOpener.OpenDraftAsync(
                new CommunityPullRequestRequest(post.DraftId, owner, repo, post.Title!, body, post.HeadBranch!, post.BaseBranch!),
                cancellationToken).ConfigureAwait(false);
        }

        var existing = await api.FindByMarkerAsync(owner, repo, issueNumber, marker, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new PostReceipt(existing.ExternalId, existing.Url, Reused: true);
        }

        var created = post.Kind == KindIssue
            ? await api.CreateIssueAsync(owner, repo, post.Title!, body, cancellationToken).ConfigureAwait(false)
            : await api.CreateCommentAsync(owner, repo, issueNumber!.Value, body, cancellationToken).ConfigureAwait(false);
        return new PostReceipt(created.ExternalId, created.Url, Reused: false);
    }

    private void Validate(OutboundPost post, ApprovalId approval)
    {
        if (approval.Value == Guid.Empty)
        {
            throw Refuse("A post needs an approval id.");
        }

        if (!string.Equals(post.Platform, PlatformKey, StringComparison.Ordinal))
        {
            throw Refuse($"This adapter serves '{PlatformKey}', not '{post.Platform}'.");
        }

        if (post.Kind is not (KindIssue or KindPullRequest or KindComment))
        {
            throw Refuse($"Unsupported kind '{post.Kind}'.");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(post.Body)));
        if (!string.Equals(hash, post.BodySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse("The body no longer matches the approved hash.");
        }

        if (!footerCheck.HasDisclosureFooter(post.Body))
        {
            throw Refuse("The body lacks the AI disclosure footer.");
        }

        if (HasSignedOffByLine(post.Body))
        {
            throw Refuse("Agents never add Signed-off-by; a human signs off at merge.");
        }

        if (post.Kind is KindIssue or KindPullRequest && string.IsNullOrWhiteSpace(post.Title))
        {
            throw Refuse($"A {post.Kind} needs a title.");
        }

        if (post.Kind == KindPullRequest
            && (string.IsNullOrWhiteSpace(post.HeadBranch) || string.IsNullOrWhiteSpace(post.BaseBranch)))
        {
            throw Refuse("A pull request needs head and base branches.");
        }
    }

    private static bool HasSignedOffByLine(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            if (line.TrimStart().StartsWith("Signed-off-by:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Target is <c>owner/repo</c> for an issue or PR and <c>owner/repo#number</c> for a comment.</summary>
    private static (string Owner, string Repo, int? Number) ParseTarget(OutboundPost post)
    {
        var target = post.Target.Trim();
        int? number = null;
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            if (!int.TryParse(target.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            {
                throw Refuse("The target issue number is not valid.");
            }

            number = n;
            target = target[..hash];
        }

        var parts = target.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
        {
            throw Refuse("The target must be owner/repo.");
        }

        if (post.Kind == KindComment && number is null)
        {
            throw Refuse("A comment target must be owner/repo#number.");
        }

        if (post.Kind != KindComment && number is not null)
        {
            throw Refuse($"A {post.Kind} target must be owner/repo.");
        }

        return (parts[0], parts[1], number);
    }

    private static CommunityPostException Refuse(string message) => new(message, retryable: false);
}
