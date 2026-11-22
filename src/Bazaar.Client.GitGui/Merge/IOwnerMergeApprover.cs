using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.Merge;

/// <summary>What the owner is asked to approve: one squash merge of one head.</summary>
/// <param name="CommitTitle">The squash commit title the server will use. It is signed over, so the server cannot change it.</param>
public sealed record MergeApprovalRequest(string Provider, string Repo, long Number, string HeadSha, string CommitTitle);

/// <summary>How an approval attempt ended.</summary>
public enum MergeApprovalOutcome
{
    /// <summary>The owner approved and the request carries the signed approval.</summary>
    Approved,

    /// <summary>The owner dismissed the prompt, or the biometric check did not pass. Nothing was signed.</summary>
    Cancelled,

    /// <summary>This device cannot approve (no enrolled key, no biometric). Nothing was signed.</summary>
    Unavailable,

    /// <summary>Signing failed. Nothing usable was produced.</summary>
    Failed,
}

/// <summary>The result of one approval attempt. <see cref="Merge"/> is set only for <see cref="MergeApprovalOutcome.Approved"/>.</summary>
public sealed record MergeApprovalResult(MergeApprovalOutcome Outcome, GitMergeRequestDto? Merge);

/// <summary>
/// The seam between the merge button and the device signer (doc 32 section 5a, doc 52 Client). The head composes it; a test uses a
/// fake. An implementation never returns an approval without a fresh presence check, and never logs the signature.
/// </summary>
public interface IOwnerMergeApprover
{
    /// <summary>Asks the owner to approve this merge and returns the ready-to-send request body.</summary>
    Task<MergeApprovalResult> ApproveAsync(MergeApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Who signs and what the squash commit says about it. Supplied by the head; GitGui does not guess either value.</summary>
public interface IOwnerMergeIdentity
{
    /// <summary>The deciding human's id, as the server's owner key registry knows it.</summary>
    Guid HumanId { get; }

    /// <summary>The sign-off line of the squash commit (doc 52 DCO line). Part of what the owner signs.</summary>
    string SignOff { get; }
}
