namespace Bazaar.GitHub.Adapter;

/// <summary>
/// The public face of the owner-signed merge gate (doc 21, Merge request path; C25). It is the only way outside this module to
/// reach a squash merge: the merge call itself is internal. A refusal throws <see cref="MergeNotApprovedException"/> and sends nothing.
/// </summary>
public interface IOwnerSignedMergeService
{
    /// <summary>Squash-merges one PR on the owner's signed approval, pinned to the approved head sha.</summary>
    /// <exception cref="MergeNotApprovedException">The approval does not cover this merge, the signature was refused, or the head moved.</exception>
    Task MergeAsync(OwnerMergeApproval approval, MergeRequest merge, CancellationToken cancellationToken);
}
