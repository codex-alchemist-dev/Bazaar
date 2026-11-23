namespace Bazaar.Server.Community;

/// <summary>
/// One outbound post, exactly as approved (doc 36, "Data and interfaces"). <see cref="BodySha256"/> is the hash the approval
/// is bound to; an adapter refuses a post whose body no longer hashes to it.
/// </summary>
/// <param name="DraftId">Draft id. Also the idempotency key: a retry searches the platform for it before posting again.</param>
/// <param name="Platform">Platform key, matching the policy record.</param>
/// <param name="Kind">Item kind: <c>issue</c>, <c>pr</c>, <c>thread</c> or <c>comment</c>.</param>
/// <param name="Target">Target reference, for example <c>owner/repo</c> or a thread URL.</param>
/// <param name="Title">Title for an issue or PR; null for a comment.</param>
/// <param name="Body">Exact outbound text with the disclosure footer.</param>
/// <param name="BodySha256">Lowercase hex SHA-256 of <paramref name="Body"/>.</param>
/// <param name="HeadBranch">Head branch for a PR; null otherwise.</param>
/// <param name="BaseBranch">Base branch for a PR; null otherwise.</param>
public sealed record OutboundPost(
    Guid DraftId,
    string Platform,
    string Kind,
    string Target,
    string? Title,
    string Body,
    string BodySha256,
    string? HeadBranch = null,
    string? BaseBranch = null);

/// <summary>What the platform returned for a post.</summary>
/// <param name="ExternalId">Platform id of the created item.</param>
/// <param name="Url">Public URL of the created item.</param>
/// <param name="Reused">True when an earlier attempt for the same draft was found and no new item was created.</param>
public sealed record PostReceipt(string ExternalId, Uri Url, bool Reused);

/// <summary>An approval id the dispatcher has already verified against the exact post. The adapter never re-derives trust from it.</summary>
public readonly record struct ApprovalId(Guid Value);

/// <summary>A community platform adapter (doc 36). Reachable only through the dispatcher, which verifies the approval first.</summary>
public interface ICommunityAdapter
{
    /// <summary>Platform key this adapter serves.</summary>
    string Platform { get; }

    /// <summary>Posts one approved item. Idempotent per <see cref="OutboundPost.DraftId"/>.</summary>
    Task<PostReceipt> PostAsync(OutboundPost post, ApprovalId approval, CancellationToken cancellationToken);
}

/// <summary>Thrown by an adapter when a post cannot be made. The dispatcher records it on the action row.</summary>
public sealed class CommunityPostException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True when a later attempt may succeed (rate limit, 5xx). False for a refusal (hash mismatch, 403, 404).</summary>
    public bool Retryable { get; } = retryable;
}
