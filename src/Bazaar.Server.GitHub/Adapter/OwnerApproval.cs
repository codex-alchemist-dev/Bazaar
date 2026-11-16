namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// The owner's signed approval of one squash merge (doc 21, Merge request path). It names one PR and one head sha. The
/// signature is made with the device-held owner key (doc 32, PROPOSED).
/// </summary>
public sealed record OwnerMergeApproval(PrRef Pr, string HeadSha, string KeyId, string Signature, DateTimeOffset SignedAt);

/// <summary>Verifies the owner's signature over an approval. The key registry is doc 32 and is not built yet.</summary>
public interface IOwnerApprovalVerifier
{
    Task<bool> VerifyAsync(OwnerMergeApproval approval, CancellationToken cancellationToken);
}

/// <summary>
/// The default verifier. It refuses every approval until the doc 32 key registry exists, so nothing merges by default.
/// </summary>
public sealed class FailClosedOwnerApprovalVerifier : IOwnerApprovalVerifier
{
    public Task<bool> VerifyAsync(OwnerMergeApproval approval, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>A squash merge was refused because its owner approval did not hold. No merge request was sent.</summary>
public sealed class MergeNotApprovedException : Exception
{
    public MergeNotApprovedException(string reason)
        : base($"The squash merge was refused: {reason}.")
    {
    }
}
